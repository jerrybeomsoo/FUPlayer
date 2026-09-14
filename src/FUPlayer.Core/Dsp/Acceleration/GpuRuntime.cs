using System.Collections.Concurrent;

namespace FUPlayer.Core.Dsp.Acceleration;

/// <summary>One OpenCL device the machine offers.</summary>
public sealed record GpuDevice
{
    /// <summary>Stable identifier stored in the settings file.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Vendor { get; init; }

    public required string Platform { get; init; }

    /// <summary>Whether the device can do 64-bit maths, which is what the processor path uses.</summary>
    public required bool SupportsDouble { get; init; }

    public int ComputeUnits { get; init; }

    public long GlobalMemoryBytes { get; init; }

    /// <summary>Largest single allocation; a very long filter needs one buffer of its whole spectrum.</summary>
    public long MaxAllocationBytes { get; init; }

    public bool IsGpu { get; init; }

    internal nint Handle { get; init; }

    public string Summary =>
        $"{ComputeUnits} compute units  ·  {GlobalMemoryBytes / (1024 * 1024)} MB  ·  " +
        (SupportsDouble ? "64-bit maths" : "32-bit maths only") + $"  ·  {Platform}";
}

/// <summary>
/// Finds OpenCL devices and keeps one compiled program per device, so switching tracks does not recompile the
/// kernels. Everything here is best effort: a machine with no OpenCL simply reports no devices.
/// </summary>
public static class GpuRuntime
{
    private static readonly Lazy<(IReadOnlyList<GpuDevice> Devices, string? Reason)> Found =
        new(Discover, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly ConcurrentDictionary<(nint Device, bool Double), GpuProgram> Programs = new();

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

        // Prefer a real GPU that can do 64-bit maths, then any GPU, then whatever is left.
        return devices.OrderByDescending(d => d.IsGpu && d.SupportsDouble)
            .ThenByDescending(d => d.IsGpu)
            .ThenByDescending(d => d.ComputeUnits)
            .First();
    }

    /// <summary>Builds (or returns) the kernel program for a device at the requested precision.</summary>
    internal static GpuProgram GetProgram(GpuDevice device, bool useDouble) =>
        Programs.GetOrAdd((device.Handle, useDouble), key => GpuProgram.Build(device, key.Double));

    private static (IReadOnlyList<GpuDevice>, string?) Discover()
    {
        if (!OpenClApi.IsPresent)
        {
            return ([], OpenClApi.LoadError ?? "This machine has no usable OpenCL loader.");
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
                        Id = repeat == 0 ? name : $"{name} #{repeat + 1}",
                        Name = name,
                        Vendor = OpenClApi.GetDeviceText(handle, OpenClApi.DeviceVendor),
                        Platform = platformName,
                        SupportsDouble = extensions.Contains("cl_khr_fp64", StringComparison.Ordinal),
                        ComputeUnits = (int)OpenClApi.GetDeviceValue<uint>(handle, OpenClApi.DeviceComputeUnits),
                        GlobalMemoryBytes = (long)OpenClApi.GetDeviceValue<ulong>(handle, OpenClApi.DeviceGlobalMemory),
                        MaxAllocationBytes = (long)OpenClApi.GetDeviceValue<ulong>(handle, OpenClApi.DeviceMaxAllocation),
                        IsGpu = (OpenClApi.GetDeviceValue<ulong>(handle, OpenClApi.DeviceType) & OpenClApi.DeviceTypeGpu) != 0,
                        Handle = handle,
                    });
                }
            }
        }
        catch (OpenClException ex)
        {
            return (devices, ex.Message);
        }

        return (devices, devices.Count > 0 ? null : "OpenCL is installed but reports no usable graphics device.");
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

    public static GpuProgram Build(GpuDevice device, bool useDouble)
    {
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
