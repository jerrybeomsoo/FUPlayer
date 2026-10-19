using System.Runtime.InteropServices;
using FUPlayer.Core.Localization;
using Microsoft.ML.OnnxRuntime;

namespace FUPlayer.Core.Dsp.Restoration;

/// <summary>A graphics adapter a network can run on: its name, and its place in DirectX's list, which is the number DirectML takes.</summary>
public sealed record GraphicsAdapter(int Index, string Name, long MemoryBytes)
{
    /// <summary>Off Windows, the device ONNX Runtime's WebGPU provider runs on; null for a DirectML adapter.</summary>
    internal OrtEpDevice? WebGpu { get; init; }

    /// <summary>A card of its own rather than a processor's graphics, which is where a network is worth moving.</summary>
    public bool IsDiscrete { get; init; }

    public override string ToString() => Name;
}

/// <summary>
/// Where the neural networks run: on the processor, or on a graphics adapter through ONNX Runtime's DirectML provider
/// on Windows and its WebGPU provider on Linux.
///
/// DirectML is part of Windows (System32, from Windows 10 version 1903). On Windows the player is built against ONNX
/// Runtime's DirectML build, which carries the processor provider as well, and leaves out Microsoft's DirectML
/// redistributable, which is not open source; the copy Windows has is the one loaded. On Linux the WebGPU provider is a
/// plugin library of its own (MIT, with Google's Dawn, BSD), which drives the card through Vulkan; its Windows build,
/// which carries Microsoft's shader compiler, is never used. Measured on an RX 6400 through RADV, the upscaler's call of
/// 116 frames took 6.7 ms against 11.4 ms on three cores of an i5-8500, its output within 7e-8 of the processor's.
///
/// Whether an adapter pays depends on the network and the card. On a laptop's Quadro M2200 the upscaler, ten million
/// parameters over 116 frames a call, ran four times faster than on the processor; the restorer, small and called every
/// few frames, ran at half the speed, its time going on the trips to the card and back. So each has its own setting.
/// </summary>
public static class InferenceDevices
{
    private static readonly Lazy<IReadOnlyList<GraphicsAdapter>> Listed = new(List);

    /// <summary>The hardware adapters DirectX lists, in its order. Empty off Windows, and where DirectX cannot be asked.</summary>
    public static IReadOnlyList<GraphicsAdapter> Adapters => Listed.Value;

    /// <summary>
    /// The adapter of that name, or null for the processor (an empty name or "cpu") and for an adapter that is not there.
    /// "auto" is the first adapter with a card of its own, if there is one.
    /// </summary>
    public static GraphicsAdapter? Find(string? name) => name switch
    {
        null or "" or "cpu" => null,
        Automatic => Adapters.FirstOrDefault(a => a.IsDiscrete),
        _ => Adapters.FirstOrDefault(a => a.Name == name),
    };

    /// <summary>The device name that picks the first discrete adapter, or the processor where there is none.</summary>
    public const string Automatic = "auto";

    /// <summary>
    /// Where the WebGPU provider's library is, when the host has put it somewhere of its own; otherwise it is looked for
    /// beside the program, in the folders .NET loads native libraries from.
    /// </summary>
    public static string? WebGpuLibrary { get; set; }

    /// <summary>What ONNX Runtime calls its WebGPU provider.</summary>
    internal const string WebGpuProvider = "WebGpuExecutionProvider";

    /// <summary>
    /// Opens a network on <paramref name="adapter"/>, or on the processor when that is null or cannot take it, in which
    /// case <paramref name="failure"/> says why. <paramref name="threads"/> are the processor's, for whatever runs there
    /// (0 lets ONNX Runtime choose); on an adapter they only serve the operators DirectML leaves to the processor.
    /// </summary>
    internal static InferenceSession Open(string path, GraphicsAdapter? adapter, int threads, out GraphicsAdapter? used, out string? failure, bool allowSpinning = true)
    {
        failure = null;
        if (adapter is not null)
        {
            try
            {
                InferenceSession session = OpenOn(path, adapter, threads, frames: 0, allowSpinning);
                used = adapter;
                return session;
            }
            catch (Exception ex) when (ex is OnnxRuntimeException or EntryPointNotFoundException or DllNotFoundException or SEHException)
            {
                failure = ex.Message;
            }
        }

        using SessionOptions processor = Options(threads, allowSpinning);
        used = null;
        return new InferenceSession(path, processor);
    }

    /// <summary>
    /// A session on an adapter, with the frame axis fixed at <paramref name="frames"/> (and a batch of one) when that is
    /// above zero. DirectML compiles a graph whose shapes are all known as one piece.
    /// </summary>
    internal static InferenceSession OpenOn(string path, GraphicsAdapter adapter, int threads, int frames, bool allowSpinning = true)
    {
        using SessionOptions options = Options(threads, allowSpinning);

        // DirectML takes neither memory patterns nor operators run side by side.
        options.EnableMemoryPattern = false;
        if (frames > 0)
        {
            options.AddFreeDimensionOverrideByName(FrameAxis, frames);
            options.AddFreeDimensionOverrideByName(BatchAxis, 1);
        }

        if (adapter.WebGpu is { } device)
        {
            options.AppendExecutionProvider(OrtEnv.Instance(), [device], new Dictionary<string, string>());
        }
        else
        {
            options.AppendExecutionProvider_DML(adapter.Index);
        }

        return new InferenceSession(path, options);
    }

    /// <summary>What the exports call the axis of frames, the one dimension whose size changes from call to call.</summary>
    internal const string FrameAxis = "frames";

    /// <summary>What the upscaler's export calls its batch axis; the player hands it one channel a call.</summary>
    internal const string BatchAxis = "batch";

    private static SessionOptions Options(int threads, bool allowSpinning)
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        };

        if (threads > 0)
        {
            options.IntraOpNumThreads = threads;
        }

        if (!allowSpinning)
        {
            options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
            options.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
        }
        return options;
    }

    /// <summary>
    /// Where a network runs, for its line in Now playing: the adapter it is on, or the processor and, when an adapter was
    /// asked for, why not there.
    /// </summary>
    public static string Describe(string? requested, GraphicsAdapter? used, string? failure)
    {
        if (used is not null)
        {
            return Loc.F("On {0}.", used.Name);
        }

        if (string.IsNullOrEmpty(requested) || requested is "cpu" || (requested == Automatic && failure is null))
        {
            return Loc.T("On the processor.");
        }

        return failure is null
            ? Loc.F("On the processor: {0} is not present.", requested)
            : Loc.F("On the processor: {0} could not open it ({1}).", requested, failure.Trim());
    }

    /// <summary>
    /// DirectX's adapters through IDXGIFactory1::EnumAdapters1, the call DirectML numbers its devices by. Software
    /// renderers are left out, as DirectML refuses them. Two adapters of the same name are told apart by a number.
    /// </summary>
    private static IReadOnlyList<GraphicsAdapter> List()
    {
        if (OperatingSystem.IsLinux())
        {
            return ListWebGpu();
        }

        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        try
        {
            var found = new List<GraphicsAdapter>();
            foreach ((int index, string name, long memory) in Enumerate())
            {
                int same = found.Count(a => a.Name == name || a.Name.StartsWith(name + " (", StringComparison.Ordinal));

                // A processor's graphics has a few hundred megabytes of its own at most; a card has gigabytes.
                found.Add(new GraphicsAdapter(index, same == 0 ? name : $"{name} ({same + 1})", memory) { IsDiscrete = memory >= 1L << 30 });
            }

            return found;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException or COMException)
        {
            return [];
        }
    }

    /// <summary>
    /// The graphics adapters the WebGPU provider can run on, named as Vulkan names them. None when its library is not
    /// there or will not load with this ONNX Runtime, or when the system has no Vulkan loader.
    /// </summary>
    private static IReadOnlyList<GraphicsAdapter> ListWebGpu()
    {
        string? library = WebGpuLibrary ?? FindWebGpuLibrary();
        if (library is null)
        {
            return [];
        }

        try
        {
            OrtEnv environment = OrtEnv.Instance();
            environment.RegisterExecutionProviderLibrary("fuplayer_webgpu", library);
            IReadOnlyList<Dsp.Acceleration.GpuDevice> vulkan;
            try
            {
                vulkan = Dsp.Acceleration.VulkanComputeApi.Discover();
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or Dsp.Acceleration.OpenClException)
            {
                vulkan = [];
            }

            var found = new List<GraphicsAdapter>();
            foreach (OrtEpDevice device in environment.GetEpDevices())
            {
                if (device.EpName != WebGpuProvider || device.HardwareDevice.Type != OrtHardwareDeviceType.GPU)
                {
                    continue;
                }

                uint vendor = device.HardwareDevice.VendorId;
                uint id = device.HardwareDevice.DeviceId;
                Dsp.Acceleration.GpuDevice? named = vulkan.FirstOrDefault(v => v.Id.StartsWith($"vulkan:{vendor:x4}:{id:x4}:", StringComparison.Ordinal));
                string name = named?.Name ?? $"GPU {vendor:x4}:{id:x4}";
                int same = found.Count(a => a.Name == name || a.Name.StartsWith(name + " (", StringComparison.Ordinal));
                found.Add(new GraphicsAdapter(found.Count, same == 0 ? name : $"{name} ({same + 1})", named?.GlobalMemoryBytes ?? 0)
                {
                    WebGpu = device,
                    IsDiscrete = named?.IsDiscrete ?? false,
                });
            }

            return found;
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return [];
        }
    }

    private static string? FindWebGpuLibrary()
    {
        const string name = "libonnxruntime_providers_webgpu.so";
        var folders = new List<string>();
        if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string searched)
        {
            folders.AddRange(searched.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        }

        folders.Add(AppContext.BaseDirectory);
        folders.Add(Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native"));
        folders.Add(Path.Combine(AppContext.BaseDirectory, "runtimes",
            RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64", "native"));
        return folders.Select(folder => Path.Combine(folder, name)).FirstOrDefault(File.Exists);
    }

    private const uint SoftwareAdapter = 2;
    private const int NotFound = unchecked((int)0x887A0002);

    private static unsafe List<(int Index, string Name, long Memory)> Enumerate()
    {
        var adapters = new List<(int, string, long)>();
        Guid factoryId = new("770aae78-f26f-4dba-a829-253c83d1b387");
        if (CreateDXGIFactory1(&factoryId, out IntPtr factory) < 0 || factory == IntPtr.Zero)
        {
            return adapters;
        }

        try
        {
            // IDXGIFactory1: IUnknown's three, IDXGIObject's four, IDXGIFactory's five, then EnumAdapters1.
            void** factoryTable = *(void***)factory;
            var enumAdapters1 = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)factoryTable[12];
            for (uint index = 0; index < 64; index++)
            {
                IntPtr adapter;
                int result = enumAdapters1(factory, index, &adapter);
                if (result == NotFound || result < 0)
                {
                    break;
                }

                try
                {
                    // IDXGIAdapter1: IUnknown's three, IDXGIObject's four, IDXGIAdapter's three, then GetDesc1.
                    void** adapterTable = *(void***)adapter;
                    var getDesc1 = (delegate* unmanaged[Stdcall]<IntPtr, AdapterDescription*, int>)adapterTable[10];
                    AdapterDescription description;
                    if (getDesc1(adapter, &description) >= 0 && (description.Flags & SoftwareAdapter) == 0)
                    {
                        string name = new string(description.Description).Trim();
                        adapters.Add(((int)index, name.Length > 0 ? name : $"Adapter {index}", (long)description.DedicatedVideoMemory));
                    }
                }
                finally
                {
                    Release(adapter);
                }
            }
        }
        finally
        {
            Release(factory);
        }

        return adapters;
    }

    private static unsafe void Release(IntPtr unknown)
    {
        void** table = *(void***)unknown;
        ((delegate* unmanaged[Stdcall]<IntPtr, uint>)table[2])(unknown);
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern unsafe int CreateDXGIFactory1(Guid* riid, out IntPtr factory);

    /// <summary>DXGI_ADAPTER_DESC1.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct AdapterDescription
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }
}
