using System.Collections.Concurrent;
using FUPlayer.Core.Localization;

namespace FUPlayer.Core.Dsp.Acceleration;

/// <summary>One OpenCL device the machine offers.</summary>
public sealed record GpuDevice
{
    /// <summary>Stable identifier stored in the settings file.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Vendor { get; init; }

    public required string Platform { get; init; }

    /// <summary>Native driver API. CUDA is preferred for NVIDIA and Vulkan for other vendors.</summary>
    public GpuBackend Backend { get; init; }

    /// <summary>The conventional API name for device selectors and diagnostics.</summary>
    public string BackendName => Backend switch { GpuBackend.Cuda => "CUDA", GpuBackend.Vulkan => "Vulkan", _ => "OpenCL" };

    internal int MaxGroupSize { get; init; } = 256;
    internal long LocalMemoryBytes { get; init; } = 16 * 1024;
    internal bool IsDiscrete { get; init; }

    /// <summary>Whether the device can do 64-bit maths, which is what the processor path uses.</summary>
    public required bool SupportsDouble { get; init; }

    public int ComputeUnits { get; init; }

    public long GlobalMemoryBytes { get; init; }

    /// <summary>Largest single allocation; a very long filter needs one buffer of its whole spectrum.</summary>
    public long MaxAllocationBytes { get; init; }

    public bool IsGpu { get; init; }

    internal nint Handle { get; init; }

    public string Summary =>
        (ComputeUnits > 0 ? Loc.F("{0} compute units", ComputeUnits) + "  ·  " : string.Empty) + $"{GlobalMemoryBytes / (1024 * 1024)} MB  ·  " +
        (SupportsDouble ? Loc.T("64-bit maths") : Loc.T("32-bit maths only")) + $"  ·  {Platform}";
}

/// <summary>
/// Finds native compute devices and keeps one kernel program per device and precision. CUDA is preferred
/// for NVIDIA and Vulkan for other GPUs; OpenCL remains an explicit alternative.
/// </summary>
public static class GpuRuntime
{
    private static readonly Lazy<(IReadOnlyList<GpuDevice> Devices, string? Reason)> Found =
        new(Discover, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly ConcurrentDictionary<(string Device, bool Double), Lazy<GpuProgram>> Programs = new();

    public static IReadOnlyList<GpuDevice> Devices => Found.Value.Devices;

    /// <summary>Why there is nothing to accelerate with, or null when there is.</summary>
    public static string? Unavailable => Found.Value.Reason;

    public static bool IsAvailable => Devices.Count > 0;

    /// <summary>The stored device, or the most capable one when the id is unknown or empty.</summary>
    public static GpuDevice? Resolve(string? id)
    {
        IReadOnlyList<GpuDevice> devices = Devices;
        if (devices.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(id))
        {
            GpuDevice? exact = devices.FirstOrDefault(d => d.Id == id);
            if (exact is not null)
            {
                return exact;
            }
        }

        // Old settings stored a display name. Migrate that name to the preferred driver API.
        IEnumerable<GpuDevice> candidates = devices;
        if (Enum.TryParse(id, ignoreCase: true, out GpuBackend backend))
        {
            candidates = devices.Where(d => d.Backend == backend).ToArray();
            if (!candidates.Any()) return null;
        }
        else if (!string.IsNullOrEmpty(id) && devices.Any(d => d.Name == id))
            candidates = devices.Where(d => d.Name == id);
        return candidates.OrderByDescending(d =>
                d.Backend == GpuBackend.Cuda || (d.Backend == GpuBackend.Vulkan && !d.Vendor.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)))
            .ThenByDescending(d => d.IsGpu && d.SupportsDouble)
            .ThenByDescending(d => d.IsDiscrete)
            .ThenByDescending(d => d.IsGpu)
            .ThenByDescending(d => d.ComputeUnits)
            .First();
    }

    /// <summary>Builds (or returns) the kernel program for a device at the requested precision.</summary>
    internal static GpuProgram GetProgram(GpuDevice device, bool useDouble) =>
        Programs.GetOrAdd((device.Id, useDouble), key => new Lazy<GpuProgram>(
            () => GpuProgram.Build(device, key.Double), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    private static (IReadOnlyList<GpuDevice>, string?) Discover()
    {
        var devices = new List<GpuDevice>();
        var reasons = new List<string>();
        try { devices.AddRange(CudaComputeApi.Discover()); }
        catch (Exception ex) when (ex is OpenClException or DllNotFoundException or EntryPointNotFoundException)
        { reasons.Add(ex.Message); }
        try { devices.AddRange(VulkanComputeApi.Discover()); }
        catch (Exception ex) when (ex is OpenClException or DllNotFoundException or EntryPointNotFoundException or NotSupportedException)
        { reasons.Add(ex.Message); }
        var openCl = DiscoverOpenCl();
        devices.AddRange(openCl.Item1);
        if (openCl.Item2 is not null) reasons.Add(openCl.Item2);
        return (devices, devices.Count > 0 ? null : string.Join(" ", reasons));
    }

    private static (IReadOnlyList<GpuDevice>, string?) DiscoverOpenCl()
    {
        if (!OpenClApi.IsPresent)
        {
            return ([], OpenClApi.LoadError ?? Loc.T("This machine has no usable OpenCL loader."));
        }

        var devices = new List<GpuDevice>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            foreach (nint platform in OpenClApi.GetPlatforms())
            {
                string platformName = OpenClApi.GetPlatformText(platform, OpenClApi.PlatformName);
                foreach (nint handle in OpenClApi.GetDevices(platform, OpenClApi.DeviceTypeGpu | OpenClApi.DeviceTypeAccelerator))
                {
                    if (OpenClApi.GetDeviceValue<uint>(handle, OpenClApi.DeviceAvailable) == 0
                        || OpenClApi.GetDeviceValue<uint>(handle, OpenClApi.DeviceCompilerAvailable) == 0)
                    {
                        continue;
                    }

                    string name = OpenClApi.GetDeviceText(handle, OpenClApi.DeviceName);
                    if (name.Length == 0)
                    {
                        continue;
                    }

                    seen.TryGetValue(name, out int repeat);
                    seen[name] = repeat + 1;
                    string extensions = OpenClApi.GetDeviceText(handle, OpenClApi.DeviceExtensions);
                    devices.Add(new GpuDevice
                    {
                        Id = "opencl:" + (repeat == 0 ? name : $"{name} #{repeat + 1}"),
                        Name = name,
                        Vendor = OpenClApi.GetDeviceText(handle, OpenClApi.DeviceVendor),
                        Platform = "OpenCL · " + platformName,
                        SupportsDouble = extensions.Contains("cl_khr_fp64", StringComparison.Ordinal),
                        ComputeUnits = (int)OpenClApi.GetDeviceValue<uint>(handle, OpenClApi.DeviceComputeUnits),
                        GlobalMemoryBytes = (long)OpenClApi.GetDeviceValue<ulong>(handle, OpenClApi.DeviceGlobalMemory),
                        MaxAllocationBytes = (long)OpenClApi.GetDeviceValue<ulong>(handle, OpenClApi.DeviceMaxAllocation),
                        IsGpu = (OpenClApi.GetDeviceValue<ulong>(handle, OpenClApi.DeviceType) & OpenClApi.DeviceTypeGpu) != 0,
                        Handle = handle,
                        MaxGroupSize = (int)OpenClApi.GetDeviceValue<nuint>(handle, OpenClApi.DeviceMaxWorkGroupSize),
                        LocalMemoryBytes = (long)OpenClApi.GetDeviceValue<ulong>(handle, OpenClApi.DeviceLocalMemory),
                    });
                }
            }
        }
        catch (OpenClException ex)
        {
            return (devices, ex.Message);
        }

        return (devices, devices.Count > 0 ? null : Loc.T("OpenCL is installed but reports no usable graphics device."));
    }
}

/// <summary>A built kernel program and the context it belongs to. Kept for the life of the process.</summary>
internal sealed class GpuProgram
{
    private GpuProgram(GpuDevice device, bool useDouble, nint context, nint program)
    {
        Device = device;
        UsesDouble = useDouble;
        Context = context;
        Program = program;
    }

    public GpuDevice Device { get; }

    public bool UsesDouble { get; }

    public nint Context { get; }

    public nint Program { get; }

    public int ElementSize => UsesDouble ? sizeof(double) : sizeof(float);

    public ComputeApi Api { get; private init; } = OpenClComputeApi.Instance;

    public static GpuProgram Build(GpuDevice device, bool useDouble)
    {
        if (device.Backend == GpuBackend.Cuda)
        {
            var api = new CudaComputeApi(device, useDouble);
            return new GpuProgram(device, useDouble, 0, api.Module) { Api = api };
        }

        if (device.Backend == GpuBackend.Vulkan)
        {
            var api = new VulkanComputeApi(device, useDouble);
            return new GpuProgram(device, useDouble, 0, 0) { Api = api };
        }

        nint context = OpenClApi.CreateContext(device.Handle);
        try
        {
            // -cl-std keeps the source portable; no fast-math, because the point of the exercise is accuracy.
            string options = useDouble ? "-cl-std=CL1.2 -D REAL_IS_DOUBLE" : "-cl-std=CL1.2";
            nint program = OpenClApi.BuildProgram(context, device.Handle, GpuKernels.Source, options);
            return new GpuProgram(device, useDouble, context, program);
        }
        catch
        {
            OpenClApi.ReleaseContext(context);
            throw;
        }
    }
}
