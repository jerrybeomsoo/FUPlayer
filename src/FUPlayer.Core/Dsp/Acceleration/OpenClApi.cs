using System.Runtime.InteropServices;
using System.Text;

namespace FUPlayer.Core.Dsp.Acceleration;

/// <summary>An OpenCL call that did not return CL_SUCCESS.</summary>
public sealed class OpenClException(string message) : Exception(message);

/// <summary>
/// The handful of OpenCL entry points the GPU convolver needs, resolved by hand from whatever ICD loader the
/// machine has. Nothing is linked at build time and no import resolver is installed, so on a machine without
/// OpenCL every member simply reports the API as absent: this stays an optional extra, never a dependency.
/// </summary>
internal static unsafe class OpenClApi
{
    public const uint PlatformName = 0x0902;
    public const uint DeviceType = 0x1000;
    public const uint DeviceComputeUnits = 0x1002;
    public const uint DeviceMaxWorkGroupSize = 0x1004;
    public const uint DeviceLocalMemory = 0x1023;
    public const uint DeviceMaxAllocation = 0x1010;
    public const uint DeviceGlobalMemory = 0x101F;
    public const uint DeviceAvailable = 0x1027;
    public const uint DeviceCompilerAvailable = 0x1028;
    public const uint DeviceName = 0x102B;
    public const uint DeviceVendor = 0x102C;
    public const uint DeviceVersion = 0x102F;
    public const uint DeviceExtensions = 0x1030;
    public const uint ProgramBuildLog = 0x1183;

    public const ulong DeviceTypeCpu = 1UL << 1;
    public const ulong DeviceTypeGpu = 1UL << 2;
    public const ulong DeviceTypeAccelerator = 1UL << 3;
    public const ulong DeviceTypeAll = 0xFFFFFFFFUL;

    public const ulong MemReadWrite = 1UL << 0;
    public const ulong MemWriteOnly = 1UL << 1;
    public const ulong MemReadOnly = 1UL << 2;
    public const ulong MemCopyHostPointer = 1UL << 5;

    private static readonly nint Handle = LoadLoader();

    private static readonly delegate* unmanaged[Cdecl]<uint, nint*, uint*, int> ClGetPlatformIDs =
        (delegate* unmanaged[Cdecl]<uint, nint*, uint*, int>)Export("clGetPlatformIDs");

    private static readonly delegate* unmanaged[Cdecl]<nint, uint, nuint, void*, nuint*, int> ClGetPlatformInfo =
        (delegate* unmanaged[Cdecl]<nint, uint, nuint, void*, nuint*, int>)Export("clGetPlatformInfo");

    private static readonly delegate* unmanaged[Cdecl]<nint, ulong, uint, nint*, uint*, int> ClGetDeviceIDs =
        (delegate* unmanaged[Cdecl]<nint, ulong, uint, nint*, uint*, int>)Export("clGetDeviceIDs");

    private static readonly delegate* unmanaged[Cdecl]<nint, uint, nuint, void*, nuint*, int> ClGetDeviceInfo =
        (delegate* unmanaged[Cdecl]<nint, uint, nuint, void*, nuint*, int>)Export("clGetDeviceInfo");

    private static readonly delegate* unmanaged[Cdecl]<nint*, uint, nint*, nint, nint, int*, nint> ClCreateContext =
        (delegate* unmanaged[Cdecl]<nint*, uint, nint*, nint, nint, int*, nint>)Export("clCreateContext");

    private static readonly delegate* unmanaged[Cdecl]<nint, nint, ulong, int*, nint> ClCreateCommandQueue =
        (delegate* unmanaged[Cdecl]<nint, nint, ulong, int*, nint>)Export("clCreateCommandQueue");

    private static readonly delegate* unmanaged[Cdecl]<nint, nint, nint*, int*, nint> ClCreateCommandQueueWithProperties =
        (delegate* unmanaged[Cdecl]<nint, nint, nint*, int*, nint>)Export("clCreateCommandQueueWithProperties");

    private static readonly delegate* unmanaged[Cdecl]<nint, uint, byte**, nuint*, int*, nint> ClCreateProgramWithSource =
        (delegate* unmanaged[Cdecl]<nint, uint, byte**, nuint*, int*, nint>)Export("clCreateProgramWithSource");

    private static readonly delegate* unmanaged[Cdecl]<nint, uint, nint*, byte*, nint, nint, int> ClBuildProgram =
        (delegate* unmanaged[Cdecl]<nint, uint, nint*, byte*, nint, nint, int>)Export("clBuildProgram");

    private static readonly delegate* unmanaged[Cdecl]<nint, nint, uint, nuint, void*, nuint*, int> ClGetProgramBuildInfo =
        (delegate* unmanaged[Cdecl]<nint, nint, uint, nuint, void*, nuint*, int>)Export("clGetProgramBuildInfo");

    private static readonly delegate* unmanaged[Cdecl]<nint, byte*, int*, nint> ClCreateKernel =
        (delegate* unmanaged[Cdecl]<nint, byte*, int*, nint>)Export("clCreateKernel");

    private static readonly delegate* unmanaged[Cdecl]<nint, ulong, nuint, void*, int*, nint> ClCreateBuffer =
        (delegate* unmanaged[Cdecl]<nint, ulong, nuint, void*, int*, nint>)Export("clCreateBuffer");

    private static readonly delegate* unmanaged[Cdecl]<nint, nint, uint, nuint, nuint, void*, uint, nint*, nint*, int> ClEnqueueWriteBuffer =
        (delegate* unmanaged[Cdecl]<nint, nint, uint, nuint, nuint, void*, uint, nint*, nint*, int>)Export("clEnqueueWriteBuffer");

    private static readonly delegate* unmanaged[Cdecl]<nint, nint, uint, nuint, nuint, void*, uint, nint*, nint*, int> ClEnqueueReadBuffer =
        (delegate* unmanaged[Cdecl]<nint, nint, uint, nuint, nuint, void*, uint, nint*, nint*, int>)Export("clEnqueueReadBuffer");

    private static readonly delegate* unmanaged[Cdecl]<nint, nint, nint, nuint, nuint, nuint, uint, nint*, nint*, int> ClEnqueueCopyBuffer =
        (delegate* unmanaged[Cdecl]<nint, nint, nint, nuint, nuint, nuint, uint, nint*, nint*, int>)Export("clEnqueueCopyBuffer");

    private static readonly delegate* unmanaged[Cdecl]<nint, uint, nuint, void*, int> ClSetKernelArg =
        (delegate* unmanaged[Cdecl]<nint, uint, nuint, void*, int>)Export("clSetKernelArg");

    private static readonly delegate* unmanaged[Cdecl]<nint, nint, uint, nuint*, nuint*, nuint*, uint, nint*, nint*, int> ClEnqueueNDRangeKernel =
        (delegate* unmanaged[Cdecl]<nint, nint, uint, nuint*, nuint*, nuint*, uint, nint*, nint*, int>)Export("clEnqueueNDRangeKernel");

    private static readonly delegate* unmanaged[Cdecl]<nint, int> ClFinish =
        (delegate* unmanaged[Cdecl]<nint, int>)Export("clFinish");

    private static readonly delegate* unmanaged[Cdecl]<nint, int> ClFlush =
        (delegate* unmanaged[Cdecl]<nint, int>)Export("clFlush");

    private static readonly delegate* unmanaged[Cdecl]<uint, nint*, int> ClWaitForEvents =
        (delegate* unmanaged[Cdecl]<uint, nint*, int>)Export("clWaitForEvents");

    private static readonly delegate* unmanaged[Cdecl]<nint, int> ClReleaseEvent =
        (delegate* unmanaged[Cdecl]<nint, int>)Export("clReleaseEvent");

    private static readonly delegate* unmanaged[Cdecl]<nint, int> ClReleaseMemObject =
        (delegate* unmanaged[Cdecl]<nint, int>)Export("clReleaseMemObject");

    private static readonly delegate* unmanaged[Cdecl]<nint, int> ClReleaseKernel =
        (delegate* unmanaged[Cdecl]<nint, int>)Export("clReleaseKernel");

    private static readonly delegate* unmanaged[Cdecl]<nint, int> ClReleaseProgram =
        (delegate* unmanaged[Cdecl]<nint, int>)Export("clReleaseProgram");

    private static readonly delegate* unmanaged[Cdecl]<nint, int> ClReleaseCommandQueue =
        (delegate* unmanaged[Cdecl]<nint, int>)Export("clReleaseCommandQueue");

    private static readonly delegate* unmanaged[Cdecl]<nint, int> ClReleaseContext =
        (delegate* unmanaged[Cdecl]<nint, int>)Export("clReleaseContext");

    /// <summary>Whether an ICD loader with every entry point the convolver uses was found.</summary>
    public static bool IsPresent =>
        Handle != 0 && ClGetPlatformIDs is not null && ClCreateContext is not null && ClCreateBuffer is not null
        && ClCreateProgramWithSource is not null && ClEnqueueNDRangeKernel is not null && ClFinish is not null;

    /// <summary>Why the loader could not be used, for the settings page.</summary>
    public static string? LoadError { get; private set; }

    public static nint[] GetPlatforms()
    {
        uint count = 0;
        if (ClGetPlatformIDs(0, null, &count) != 0 || count == 0)
        {
            return [];
        }

        var platforms = new nint[count];
        fixed (nint* p = platforms)
        {
            Check(ClGetPlatformIDs(count, p, null), "clGetPlatformIDs");
        }

        return platforms;
    }

    public static nint[] GetDevices(nint platform, ulong type)
    {
        uint count = 0;
        // No device of this type is not an error worth reporting; it is the normal answer on many machines.
        if (ClGetDeviceIDs(platform, type, 0, null, &count) != 0 || count == 0)
        {
            return [];
        }

        var devices = new nint[count];
        fixed (nint* d = devices)
        {
            Check(ClGetDeviceIDs(platform, type, count, d, null), "clGetDeviceIDs");
        }

        return devices;
    }

    public static string GetPlatformText(nint platform, uint parameter)
    {
        nuint size = 0;
        if (ClGetPlatformInfo(platform, parameter, 0, null, &size) != 0 || size == 0)
        {
            return string.Empty;
        }

        var buffer = new byte[(int)size];
        fixed (byte* b = buffer)
        {
            return ClGetPlatformInfo(platform, parameter, size, b, null) == 0 ? Text(buffer) : string.Empty;
        }
    }

    public static string GetDeviceText(nint device, uint parameter)
    {
        nuint size = 0;
        if (ClGetDeviceInfo(device, parameter, 0, null, &size) != 0 || size == 0)
        {
            return string.Empty;
        }

        var buffer = new byte[(int)size];
        fixed (byte* b = buffer)
        {
            return ClGetDeviceInfo(device, parameter, size, b, null) == 0 ? Text(buffer) : string.Empty;
        }
    }

    public static T GetDeviceValue<T>(nint device, uint parameter)
        where T : unmanaged
    {
        T value = default;
        return ClGetDeviceInfo(device, parameter, (nuint)sizeof(T), &value, null) == 0 ? value : default;
    }

    public static nint CreateContext(nint device)
    {
        int status;
        nint context = ClCreateContext(null, 1, &device, 0, 0, &status);
        Check(status, "clCreateContext");
        return context;
    }

    public static nint CreateQueue(nint context, nint device)
    {
        int status;
        nint queue;
        if (ClCreateCommandQueueWithProperties is not null)
        {
            nint none = 0;
            queue = ClCreateCommandQueueWithProperties(context, device, &none, &status);
            if (status == 0)
            {
                return queue;
            }
        }

        if (ClCreateCommandQueue is null)
        {
            throw new OpenClException("The OpenCL loader exports no way to create a command queue.");
        }

        queue = ClCreateCommandQueue(context, device, 0, &status);
        Check(status, "clCreateCommandQueue");
        return queue;
    }

    public static nint BuildProgram(nint context, nint device, string source, string options)
    {
        byte[] sourceBytes = Encoding.UTF8.GetBytes(source);
        byte[] optionBytes = Encoding.UTF8.GetBytes(options + '\0');
        nint program;
        int status;
        fixed (byte* s = sourceBytes)
        {
            byte* text = s;
            nuint length = (nuint)sourceBytes.Length;
            program = ClCreateProgramWithSource(context, 1, &text, &length, &status);
        }

        Check(status, "clCreateProgramWithSource");

        int build;
        fixed (byte* o = optionBytes)
        {
            build = ClBuildProgram(program, 1, &device, o, 0, 0);
        }

        if (build != 0)
        {
            string log = GetBuildLog(program, device);
            ClReleaseProgram(program);
            throw new OpenClException($"The GPU compiler rejected the convolution kernels ({Describe(build)}). {log}".TrimEnd());
        }

        return program;
    }

    public static nint CreateKernel(nint program, string name)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(name + '\0');
        int status;
        nint kernel;
        fixed (byte* n = bytes)
        {
            kernel = ClCreateKernel(program, n, &status);
        }

        Check(status, $"clCreateKernel({name})");
        return kernel;
    }

    public static nint CreateBuffer(nint context, ulong flags, nuint bytes)
    {
        int status;
        nint buffer = ClCreateBuffer(context, flags, bytes, null, &status);
        Check(status, $"clCreateBuffer({bytes} bytes)");
        return buffer;
    }

    public static void Write(nint queue, nint buffer, nuint offsetBytes, ReadOnlySpan<byte> data)
    {
        fixed (byte* p = data)
        {
            Check(ClEnqueueWriteBuffer(queue, buffer, 1, offsetBytes, (nuint)data.Length, p, 0, null, null), "clEnqueueWriteBuffer");
        }
    }

    /// <summary>
    /// Enqueues a write and returns without waiting for it, handing back the event that says when it is done.
    /// </summary>
    /// <remarks>
    /// The data is read by the driver after this returns, so <paramref name="data"/> must be pinned and must not
    /// be touched again until <see cref="Wait"/> has been called on the event. That is the whole point: the
    /// processor can convolve its own phases of a block while the device is still being given it.
    /// </remarks>
    public static nint WriteAsync(nint queue, nint buffer, nuint offsetBytes, ReadOnlySpan<byte> data)
    {
        nint completed = 0;
        fixed (byte* p = data)
        {
            Check(
                ClEnqueueWriteBuffer(queue, buffer, 0, offsetBytes, (nuint)data.Length, p, 0, null, &completed),
                "clEnqueueWriteBuffer");
        }

        return completed;
    }

    /// <summary>Lets go of an event nobody will wait for; the command it belongs to still runs.</summary>
    public static void ReleaseEvent(nint completed)
    {
        if (completed != 0)
        {
            ClReleaseEvent(completed);
        }
    }

    /// <summary>Waits for an event from <see cref="WriteAsync"/> and releases it.</summary>
    public static void Wait(nint completed)
    {
        if (completed == 0)
        {
            return;
        }

        Check(ClWaitForEvents(1, &completed), "clWaitForEvents");
        Check(ClReleaseEvent(completed), "clReleaseEvent");
    }

    public static void Read(nint queue, nint buffer, nuint offsetBytes, Span<byte> data)
    {
        fixed (byte* p = data)
        {
            Check(ClEnqueueReadBuffer(queue, buffer, 1, offsetBytes, (nuint)data.Length, p, 0, null, null), "clEnqueueReadBuffer");
        }
    }

    public static void Copy(nint queue, nint source, nint destination, nuint bytes) =>
        Check(ClEnqueueCopyBuffer(queue, source, destination, 0, 0, bytes, 0, null, null), "clEnqueueCopyBuffer");

    /// <summary>
    /// Copies a range of one buffer into another on the device. The two must be different buffers: OpenCL leaves
    /// a copy whose source and destination overlap undefined.
    /// </summary>
    public static void Copy(nint queue, nint source, nuint sourceOffset, nint destination, nuint destinationOffset, nuint bytes) =>
        Check(ClEnqueueCopyBuffer(queue, source, destination, sourceOffset, destinationOffset, bytes, 0, null, null),
            "clEnqueueCopyBuffer");

    public static void SetArg(nint kernel, uint index, nint buffer) =>
        Check(ClSetKernelArg(kernel, index, (nuint)sizeof(nint), &buffer), "clSetKernelArg(buffer)");

    public static void SetArg(nint kernel, uint index, int value) =>
        Check(ClSetKernelArg(kernel, index, sizeof(int), &value), "clSetKernelArg(int)");

    public static void SetArg(nint kernel, uint index, float value) =>
        Check(ClSetKernelArg(kernel, index, sizeof(float), &value), "clSetKernelArg(float)");

    public static void SetArg(nint kernel, uint index, double value) =>
        Check(ClSetKernelArg(kernel, index, sizeof(double), &value), "clSetKernelArg(double)");

    /// <summary>Reserves <paramref name="bytes"/> of a work group's local memory for the argument at that index.</summary>
    public static void SetLocalArg(nint kernel, uint index, nuint bytes) =>
        Check(ClSetKernelArg(kernel, index, bytes, null), "clSetKernelArg(local)");

    public static void Run(nint queue, nint kernel, nuint globalX, nuint globalY)
    {
        nuint* global = stackalloc nuint[2] { globalX, globalY };
        Check(ClEnqueueNDRangeKernel(queue, kernel, globalY > 1 ? 2u : 1u, null, global, null, 0, null, null), "clEnqueueNDRangeKernel");
    }

    /// <summary>Runs a kernel whose work groups matter: <paramref name="globalX"/> must be a multiple of the group.</summary>
    public static void Run(nint queue, nint kernel, nuint globalX, nuint globalY, nuint groupX)
    {
        nuint* global = stackalloc nuint[2] { globalX, globalY };
        nuint* local = stackalloc nuint[2] { groupX, 1 };
        Check(ClEnqueueNDRangeKernel(queue, kernel, 2, null, global, local, 0, null, null), "clEnqueueNDRangeKernel");
    }

    public static void Finish(nint queue) => Check(ClFinish(queue), "clFinish");

    /// <summary>
    /// Hands what has been enqueued to the device and returns at once. Without it a driver is free to sit on the
    /// commands until something waits for them, which would mean the device only ever started work at the moment
    /// the processor had already finished its own. The two would take turns rather than run together.
    /// </summary>
    public static void Flush(nint queue)
    {
        if (ClFlush is not null)
        {
            Check(ClFlush(queue), "clFlush");
        }
    }

    public static void ReleaseMemory(nint buffer)
    {
        if (buffer != 0)
        {
            ClReleaseMemObject(buffer);
        }
    }

    public static void ReleaseKernel(nint kernel)
    {
        if (kernel != 0)
        {
            ClReleaseKernel(kernel);
        }
    }

    public static void ReleaseProgram(nint program)
    {
        if (program != 0)
        {
            ClReleaseProgram(program);
        }
    }

    public static void ReleaseQueue(nint queue)
    {
        if (queue != 0)
        {
            ClReleaseCommandQueue(queue);
        }
    }

    public static void ReleaseContext(nint context)
    {
        if (context != 0)
        {
            ClReleaseContext(context);
        }
    }

    private static string GetBuildLog(nint program, nint device)
    {
        nuint size = 0;
        if (ClGetProgramBuildInfo(program, device, ProgramBuildLog, 0, null, &size) != 0 || size <= 1)
        {
            return string.Empty;
        }

        var buffer = new byte[(int)Math.Min(size, 4096)];
        fixed (byte* b = buffer)
        {
            return ClGetProgramBuildInfo(program, device, ProgramBuildLog, (nuint)buffer.Length, b, null) == 0 ? Text(buffer) : string.Empty;
        }
    }

    private static void Check(int status, string what)
    {
        if (status != 0)
        {
            throw new OpenClException($"{what} failed ({Describe(status)}).");
        }
    }

    private static string Describe(int status) => status switch
    {
        -1 => "no device found",
        -2 => "the device is not available",
        -4 => "the device could not allocate memory",
        -5 => "the driver ran out of resources",
        -6 => "the host ran out of memory",
        -11 => "the kernels failed to compile",
        -30 => "invalid value",
        -34 => "invalid context",
        -36 => "invalid command queue",
        -38 => "invalid memory object",
        -48 => "invalid kernel",
        -54 => "invalid work-group size",
        -61 => "the requested buffer is larger than the device allows",
        _ => $"error {status}",
    };

    private static string Text(byte[] buffer)
    {
        int end = Array.IndexOf(buffer, (byte)0);
        return Encoding.UTF8.GetString(buffer, 0, end < 0 ? buffer.Length : end).Trim();
    }

    private static nint Export(string name) =>
        Handle != 0 && NativeLibrary.TryGetExport(Handle, name, out nint address) ? address : 0;

    private static nint LoadLoader()
    {
        string[] candidates = OperatingSystem.IsWindows()
            ? ["OpenCL.dll"]
            : OperatingSystem.IsMacOS()
                ? ["/System/Library/Frameworks/OpenCL.framework/OpenCL", "libOpenCL.dylib"]
                : ["libOpenCL.so.1", "libOpenCL.so"];

        foreach (string candidate in candidates)
        {
            if (NativeLibrary.TryLoad(candidate, out nint handle))
            {
                return handle;
            }
        }

        LoadError = $"No OpenCL loader ({string.Join(", ", candidates)}) is installed.";
        return 0;
    }
}
