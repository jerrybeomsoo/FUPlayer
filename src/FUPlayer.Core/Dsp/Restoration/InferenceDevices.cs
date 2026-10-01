using System.Runtime.InteropServices;
using FUPlayer.Core.Localization;
using Microsoft.ML.OnnxRuntime;

namespace FUPlayer.Core.Dsp.Restoration;

/// <summary>A graphics adapter a network can run on: its name, and its place in DirectX's list, which is the number DirectML takes.</summary>
public sealed record GraphicsAdapter(int Index, string Name, long MemoryBytes)
{
    public override string ToString() => Name;
}

/// <summary>
/// Where the neural networks run: on the processor, or on a graphics adapter through ONNX Runtime's DirectML provider.
///
/// DirectML is part of Windows (System32, from Windows 10 version 1903). On Windows the player is built against ONNX
/// Runtime's DirectML build, which carries the processor provider as well, and leaves out Microsoft's DirectML
/// redistributable, which is not open source; the copy Windows has is the one loaded. Elsewhere there is no adapter to
/// list and the networks stay on the processor.
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

    /// <summary>The adapter of that name, or null for the processor (an empty name) and for an adapter that is not there.</summary>
    public static GraphicsAdapter? Find(string? name) =>
        string.IsNullOrEmpty(name) ? null : Adapters.FirstOrDefault(a => a.Name == name);

    /// <summary>
    /// Opens a network on <paramref name="adapter"/>, or on the processor when that is null or cannot take it, in which
    /// case <paramref name="failure"/> says why. <paramref name="threads"/> are the processor's, for whatever runs there
    /// (0 lets ONNX Runtime choose); on an adapter they only serve the operators DirectML leaves to the processor.
    /// </summary>
    internal static InferenceSession Open(string path, GraphicsAdapter? adapter, int threads, out GraphicsAdapter? used, out string? failure)
    {
        failure = null;
        if (adapter is not null)
        {
            try
            {
                InferenceSession session = OpenOn(path, adapter, threads, frames: 0);
                used = adapter;
                return session;
            }
            catch (Exception ex) when (ex is OnnxRuntimeException or EntryPointNotFoundException or DllNotFoundException or SEHException)
            {
                failure = ex.Message;
            }
        }

        using SessionOptions processor = Options(threads);
        used = null;
        return new InferenceSession(path, processor);
    }

    /// <summary>
    /// A session on an adapter, with the frame axis fixed at <paramref name="frames"/> (and a batch of one) when that is
    /// above zero. DirectML compiles a graph whose shapes are all known as one piece.
    /// </summary>
    internal static InferenceSession OpenOn(string path, GraphicsAdapter adapter, int threads, int frames)
    {
        using SessionOptions options = Options(threads);

        // DirectML takes neither memory patterns nor operators run side by side.
        options.EnableMemoryPattern = false;
        if (frames > 0)
        {
            options.AddFreeDimensionOverrideByName(FrameAxis, frames);
            options.AddFreeDimensionOverrideByName(BatchAxis, 1);
        }

        options.AppendExecutionProvider_DML(adapter.Index);
        return new InferenceSession(path, options);
    }

    /// <summary>What the exports call the axis of frames, the one dimension whose size changes from call to call.</summary>
    internal const string FrameAxis = "frames";

    /// <summary>What the upscaler's export calls its batch axis; the player hands it one channel a call.</summary>
    internal const string BatchAxis = "batch";

    private static SessionOptions Options(int threads)
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

        if (string.IsNullOrEmpty(requested))
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
                found.Add(new GraphicsAdapter(index, same == 0 ? name : $"{name} ({same + 1})", memory));
            }

            return found;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException or COMException)
        {
            return [];
        }
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
