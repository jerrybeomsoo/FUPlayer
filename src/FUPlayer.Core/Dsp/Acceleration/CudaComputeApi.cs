using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace FUPlayer.Core.Dsp.Acceleration;

/// <summary>CUDA driver execution. The installed NVIDIA driver loads the project's own embedded kernels.</summary>
internal sealed unsafe class CudaComputeApi : ComputeApi
{
    private static readonly nint Library = LoadDriver();
    private readonly nint _context;
    public nint Module { get; }

    private sealed class Kernel(nint function)
    {
        public readonly nint Function = function;
        public readonly ulong[] Arguments = new ulong[16];
        public uint SharedBytes;
    }

    private static nint LoadDriver()
    {
        string name = OperatingSystem.IsWindows() ? "nvcuda.dll" : "libcuda.so.1";
        return NativeLibrary.TryLoad(name, typeof(CudaComputeApi).Assembly,
            OperatingSystem.IsWindows() ? DllImportSearchPath.System32 : null, out nint library) ? library : 0;
    }

    private static nint Export(string name) => NativeLibrary.GetExport(Library, name);

    private static void Check(int result, string operation)
    {
        if (result == 0) return;
        byte* reason = null;
        _ = ((delegate* unmanaged[Cdecl]<int, byte**, int>)Export("cuGetErrorString"))(result, &reason);
        throw new OpenClException($"CUDA {operation}: {Marshal.PtrToStringUTF8((nint)reason) ?? result.ToString()}");
    }

    internal static IReadOnlyList<GpuDevice> Discover()
    {
        if (Library == 0) return [];
        int init = ((delegate* unmanaged[Cdecl]<uint, int>)Export("cuInit"))(0);
        if (init != 0) return [];
        int count = 0;
        Check(((delegate* unmanaged[Cdecl]<int*, int>)Export("cuDeviceGetCount"))(&count), "device count");
        var devices = new List<GpuDevice>();
        byte* name = stackalloc byte[256];
        byte* uuid = stackalloc byte[16];
        for (int ordinal = 0; ordinal < count; ordinal++)
        {
            int device = 0;
            Check(((delegate* unmanaged[Cdecl]<int*, int, int>)Export("cuDeviceGet"))(&device, ordinal), "device");
            Check(((delegate* unmanaged[Cdecl]<byte*, int, int, int>)Export("cuDeviceGetName"))(name, 256, device), "device name");
            nuint memory = 0;
            Check(((delegate* unmanaged[Cdecl]<nuint*, int, int>)Export("cuDeviceTotalMem_v2"))(&memory, device), "memory");
            string label = Marshal.PtrToStringUTF8((nint)name)!;
            Check(((delegate* unmanaged[Cdecl]<byte*, int, int>)Export("cuDeviceGetUuid_v2"))(uuid, device), "device UUID");
            string identity = Convert.ToHexString(new ReadOnlySpan<byte>(uuid, 16));
            devices.Add(new GpuDevice
            {
                Id = $"cuda:{identity}:{label}", Name = label, Vendor = "NVIDIA", Platform = "CUDA",
                Backend = GpuBackend.Cuda, SupportsDouble = Attribute(device, 75) >= 2,
                ComputeUnits = Attribute(device, 16), GlobalMemoryBytes = (long)memory,
                MaxAllocationBytes = (long)memory * 3 / 4, IsGpu = true, IsDiscrete = true, Handle = device,
                MaxGroupSize = Attribute(device, 1), LocalMemoryBytes = Attribute(device, 8),
            });
        }
        return devices;
    }

    private static int Attribute(int device, int attribute)
    {
        int value = 0;
        Check(((delegate* unmanaged[Cdecl]<int*, int, int, int>)Export("cuDeviceGetAttribute"))(&value, attribute, device), "device attribute");
        return value;
    }

    internal CudaComputeApi(GpuDevice device, bool useDouble)
    {
        nint context = 0;
        Check(((delegate* unmanaged[Cdecl]<nint*, int, int>)Export("cuDevicePrimaryCtxRetain"))(&context, (int)device.Handle), "retain context");
        _context = context;
        try
        {
            Current();
            string resource = $"FUPlayer.Core.Gpu.cuda{(useDouble ? 64 : 32)}.fatbin";
            using Stream source = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
                ?? throw new OpenClException($"CUDA kernels are missing: {resource}");
            var image = new byte[source.Length];
            source.ReadExactly(image);
            nint module = 0;
            fixed (byte* p = image)
                Check(((delegate* unmanaged[Cdecl]<nint*, void*, int>)Export("cuModuleLoadData"))(&module, p), "load kernels");
            Module = module;
        }
        catch
        {
            _ = ((delegate* unmanaged[Cdecl]<int, int>)Export("cuDevicePrimaryCtxRelease_v2"))((int)device.Handle);
            throw;
        }
    }

    private void Current() => Check(((delegate* unmanaged[Cdecl]<nint, int>)Export("cuCtxSetCurrent"))(_context), "set context");
    private static Kernel Get(nint handle) => (Kernel)GCHandle.FromIntPtr(handle).Target!;
    public override nint CreateQueue(nint context, nint device)
    {
        Current();
        nint stream = 0;
        Check(((delegate* unmanaged[Cdecl]<nint*, uint, int>)Export("cuStreamCreate"))(&stream, 1), "create stream");
        return stream;
    }
    public override nint CreateKernel(nint program, string name)
    {
        Current();
        nint function = 0;
        byte[] bytes = Encoding.UTF8.GetBytes(name + '\0');
        fixed (byte* p = bytes)
            Check(((delegate* unmanaged[Cdecl]<nint*, nint, byte*, int>)Export("cuModuleGetFunction"))(&function, Module, p), "kernel");
        return GCHandle.ToIntPtr(GCHandle.Alloc(new Kernel(function)));
    }
    public override nint CreateBuffer(nint context, ulong flags, nuint bytes)
    {
        Current();
        ulong address = 0;
        Check(((delegate* unmanaged[Cdecl]<ulong*, nuint, int>)Export("cuMemAlloc_v2"))(&address, bytes), "allocate buffer");
        return (nint)address;
    }
    public override void Write(nint queue, nint buffer, nuint offsetBytes, ReadOnlySpan<byte> data)
    {
        Finish(queue);
        fixed (byte* p = data)
            Check(((delegate* unmanaged[Cdecl]<ulong, void*, nuint, int>)Export("cuMemcpyHtoD_v2"))((ulong)buffer + offsetBytes, p, (nuint)data.Length), "upload");
    }
    public override void Read(nint queue, nint buffer, nuint offsetBytes, Span<byte> data)
    {
        Finish(queue);
        fixed (byte* p = data)
            Check(((delegate* unmanaged[Cdecl]<void*, ulong, nuint, int>)Export("cuMemcpyDtoH_v2"))(p, (ulong)buffer + offsetBytes, (nuint)data.Length), "download");
    }
    public override void Copy(nint queue, nint source, nuint sourceOffset, nint destination, nuint destinationOffset, nuint bytes)
    {
        Current();
        Check(((delegate* unmanaged[Cdecl]<ulong, ulong, nuint, nint, int>)Export("cuMemcpyDtoDAsync_v2"))(
            (ulong)destination + destinationOffset, (ulong)source + sourceOffset, bytes, queue), "copy");
    }
    public override void SetArg(nint kernel, uint index, nint buffer) => Get(kernel).Arguments[index] = (ulong)buffer;
    public override void SetArg(nint kernel, uint index, int value) => Get(kernel).Arguments[index] = (uint)value;
    public override void SetArg(nint kernel, uint index, float value) => Get(kernel).Arguments[index] = BitConverter.SingleToUInt32Bits(value);
    public override void SetArg(nint kernel, uint index, double value) => Get(kernel).Arguments[index] = BitConverter.DoubleToUInt64Bits(value);
    public override void SetLocalArg(nint kernel, uint index, nuint bytes) => Get(kernel).SharedBytes = checked((uint)bytes);
    public override void Run(nint queue, nint kernel, nuint globalX, nuint globalY, nuint groupX)
    {
        Current();
        Kernel k = Get(kernel);
        void** pointers = stackalloc void*[16];
        fixed (ulong* values = k.Arguments)
        {
            for (int i = 0; i < 16; i++) pointers[i] = values + i;
            Check(((delegate* unmanaged[Cdecl]<nint, uint, uint, uint, uint, uint, uint, uint, nint, void**, void**, int>)Export("cuLaunchKernel"))(
                k.Function, checked((uint)((globalX + groupX - 1) / groupX)), checked((uint)globalY), 1,
                checked((uint)groupX), 1, 1, k.SharedBytes, queue, pointers, null), "dispatch");
        }
    }
    public override void Finish(nint queue)
    {
        Current();
        Check(((delegate* unmanaged[Cdecl]<nint, int>)Export("cuStreamSynchronize"))(queue), "wait stream");
    }
    public override void Flush(nint queue) { }
    public override void ReleaseMemory(nint buffer)
    {
        if (buffer == 0) return;
        Current();
        Check(((delegate* unmanaged[Cdecl]<ulong, int>)Export("cuMemFree_v2"))((ulong)buffer), "free buffer");
    }
    public override void ReleaseKernel(nint kernel)
    {
        if (kernel != 0) GCHandle.FromIntPtr(kernel).Free();
    }
    public override void ReleaseQueue(nint queue)
    {
        if (queue == 0) return;
        Finish(queue);
        Check(((delegate* unmanaged[Cdecl]<nint, int>)Export("cuStreamDestroy_v2"))(queue), "destroy stream");
    }
}
