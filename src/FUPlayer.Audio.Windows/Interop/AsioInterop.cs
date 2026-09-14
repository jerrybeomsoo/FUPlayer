using System.Runtime.InteropServices;
using System.Text;
using FUPlayer.Audio.Windows;
using Microsoft.Win32;

namespace FUPlayer.Audio.Windows.Interop;

/// <summary>ASIO sample formats (ASIOSampleType).</summary>
internal enum AsioSampleType
{
    Int16Msb = 0,
    Int24Msb = 1,
    Int32Msb = 2,
    Float32Msb = 3,
    Float64Msb = 4,
    Int32Msb16 = 8,
    Int32Msb18 = 9,
    Int32Msb20 = 10,
    Int32Msb24 = 11,
    Int16Lsb = 16,
    Int24Lsb = 17,
    Int32Lsb = 18,
    Float32Lsb = 19,
    Float64Lsb = 20,
    Int32Lsb16 = 24,
    Int32Lsb18 = 25,
    Int32Lsb20 = 26,
    Int32Lsb24 = 27,
    DsdInt8Lsb1 = 32,
    DsdInt8Msb1 = 33,
    DsdInt8Ner8 = 40,
}

internal enum AsioError
{
    Ok = 0,
    Success = 0x3f4847a0,
    NotPresent = -1000,
    HardwareMalfunction = -999,
    InvalidParameter = -998,
    InvalidMode = -997,
    SamplePositionNotAdvancing = -996,
    NoClock = -995,
    NoMemory = -994,
}

internal static class AsioConstants
{
    public const int MessageSelectorSupported = 1;
    public const int MessageEngineVersion = 2;
    public const int MessageResetRequest = 3;
    public const int MessageBufferSizeChange = 4;
    public const int MessageResyncRequest = 5;
    public const int MessageLatenciesChanged = 6;
    public const int MessageSupportsTimeInfo = 7;
    public const int MessageSupportsTimeCode = 8;
    public const int MessageOverload = 15;

    public const int FutureSetIoFormat = 0x23111961;
    public const int FutureGetIoFormat = 0x23111983;
    public const int FutureCanDoIoFormat = 0x23112004;

    public const int IoFormatPcm = 0;
    public const int IoFormatDsd = 1;

    public static bool Succeeded(AsioError error) => error is AsioError.Ok or AsioError.Success;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AsioChannelInfo
{
    public int Channel;
    public int IsInput;
    public int IsActive;
    public int ChannelGroup;
    public AsioSampleType Type;
    public fixed byte Name[32];
}

[StructLayout(LayoutKind.Sequential)]
internal struct AsioBufferInfo
{
    public int IsInput;
    public int ChannelNumber;
    public IntPtr Buffer0;
    public IntPtr Buffer1;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AsioCallbacks
{
    public delegate* unmanaged<int, int, void> BufferSwitch;
    public delegate* unmanaged<double, void> SampleRateDidChange;
    public delegate* unmanaged<int, int, IntPtr, double*, int> AsioMessage;
    public delegate* unmanaged<IntPtr, int, int, IntPtr> BufferSwitchTimeInfo;
}

[StructLayout(LayoutKind.Sequential, Size = 512)]
internal struct AsioIoFormat
{
    public int FormatType;
}

/// <summary>Installed ASIO driver as registered under HKLM\SOFTWARE\ASIO.</summary>
internal sealed record AsioDriverEntry(string Name, Guid ClassId, string Description);

/// <summary>
/// Thin wrapper over the IASIO COM-style virtual table. On 64-bit Windows every method uses the single
/// platform calling convention, so function pointers can be invoked directly. Must be used from the
/// thread that created it.
/// </summary>
internal sealed unsafe class AsioDriver : IDisposable
{
    private IntPtr _instance;

    private AsioDriver(IntPtr instance)
    {
        _instance = instance;
    }

    /// <summary>Name of the driver as registered, for logs.</summary>
    public string Name { get; private init; } = string.Empty;

    private IntPtr* Table => *(IntPtr**)_instance;

    public static IReadOnlyList<AsioDriverEntry> EnumerateInstalled()
    {
        var result = new List<AsioDriverEntry>();
        using RegistryKey? root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\ASIO");
        if (root is null)
        {
            return result;
        }

        foreach (string name in root.GetSubKeyNames())
        {
            using RegistryKey? key = root.OpenSubKey(name);
            if (key?.GetValue("CLSID") is string clsid && Guid.TryParse(clsid, out Guid classId))
            {
                result.Add(new AsioDriverEntry(name, classId, key.GetValue("Description") as string ?? name));
            }
        }

        return result;
    }

    public static AsioDriver Load(Guid classId, string name = "")
    {
        if (!Environment.Is64BitProcess)
        {
            throw new PlatformNotSupportedException("ASIO support requires a 64-bit process.");
        }

        int hr = NativeMethods.CoCreateInstance(classId, IntPtr.Zero, NativeMethods.ClsCtxInprocServer, classId, out IntPtr instance);
        if (hr != 0 || instance == IntPtr.Zero)
        {
            throw new COMException("Could not instantiate the ASIO driver.", hr);
        }

        var driver = new AsioDriver(instance) { Name = name.Length > 0 ? name : classId.ToString("B") };
        AsioTrace.DriverLoaded(driver.Name);
        return driver;
    }

    public bool Init(IntPtr systemHandle) =>
        ((delegate* unmanaged<IntPtr, IntPtr, int>)Table[3])(_instance, systemHandle) != 0;

    public string GetDriverName()
    {
        byte* buffer = stackalloc byte[256];
        buffer[0] = 0;
        ((delegate* unmanaged<IntPtr, byte*, void>)Table[4])(_instance, buffer);
        return Decode(buffer, 256);
    }

    public int GetDriverVersion() => ((delegate* unmanaged<IntPtr, int>)Table[5])(_instance);

    public string GetErrorMessage()
    {
        byte* buffer = stackalloc byte[512];
        buffer[0] = 0;
        ((delegate* unmanaged<IntPtr, byte*, void>)Table[6])(_instance, buffer);
        return Decode(buffer, 512);
    }

    public AsioError Start() => (AsioError)((delegate* unmanaged<IntPtr, int>)Table[7])(_instance);

    public AsioError Stop() => (AsioError)((delegate* unmanaged<IntPtr, int>)Table[8])(_instance);

    public AsioError GetChannels(out int inputs, out int outputs)
    {
        int i = 0;
        int o = 0;
        var error = (AsioError)((delegate* unmanaged<IntPtr, int*, int*, int>)Table[9])(_instance, &i, &o);
        inputs = i;
        outputs = o;
        return error;
    }

    public AsioError GetLatencies(out int input, out int output)
    {
        int i = 0;
        int o = 0;
        var error = (AsioError)((delegate* unmanaged<IntPtr, int*, int*, int>)Table[10])(_instance, &i, &o);
        input = i;
        output = o;
        return error;
    }

    public AsioError GetBufferSize(out int minimum, out int maximum, out int preferred, out int granularity)
    {
        int min = 0, max = 0, pref = 0, gran = 0;
        var error = (AsioError)((delegate* unmanaged<IntPtr, int*, int*, int*, int*, int>)Table[11])(_instance, &min, &max, &pref, &gran);
        minimum = min;
        maximum = max;
        preferred = pref;
        granularity = gran;
        return error;
    }

    public AsioError CanSampleRate(double rate) =>
        (AsioError)((delegate* unmanaged<IntPtr, double, int>)Table[12])(_instance, rate);

    public AsioError GetSampleRate(out double rate)
    {
        double r = 0;
        var error = (AsioError)((delegate* unmanaged<IntPtr, double*, int>)Table[13])(_instance, &r);
        rate = r;
        return error;
    }

    public AsioError SetSampleRate(double rate) =>
        (AsioError)((delegate* unmanaged<IntPtr, double, int>)Table[14])(_instance, rate);

    public AsioError GetChannelInfo(ref AsioChannelInfo info)
    {
        fixed (AsioChannelInfo* pointer = &info)
        {
            return (AsioError)((delegate* unmanaged<IntPtr, AsioChannelInfo*, int>)Table[18])(_instance, pointer);
        }
    }

    public AsioError CreateBuffers(AsioBufferInfo* infos, int channelCount, int bufferSize, AsioCallbacks* callbacks) =>
        (AsioError)((delegate* unmanaged<IntPtr, AsioBufferInfo*, int, int, AsioCallbacks*, int>)Table[19])(_instance, infos, channelCount, bufferSize, callbacks);

    public AsioError DisposeBuffers() => (AsioError)((delegate* unmanaged<IntPtr, int>)Table[20])(_instance);

    public AsioError ControlPanel() => (AsioError)((delegate* unmanaged<IntPtr, int>)Table[21])(_instance);

    public AsioError Future(int selector, void* argument) =>
        (AsioError)((delegate* unmanaged<IntPtr, int, void*, int>)Table[22])(_instance, selector, argument);

    public AsioError OutputReady() => (AsioError)((delegate* unmanaged<IntPtr, int>)Table[23])(_instance);

    /// <summary>Switches the driver between PCM and DSD I/O (ASIO 2.2 DSD extension).</summary>
    public bool TrySetIoFormat(int formatType)
    {
        AsioIoFormat format = default;
        format.FormatType = formatType;
        AsioError error = Future(AsioConstants.FutureSetIoFormat, &format);
        return AsioConstants.Succeeded(error);
    }

    public bool CanDoIoFormat(int formatType)
    {
        AsioIoFormat format = default;
        format.FormatType = formatType;
        return AsioConstants.Succeeded(Future(AsioConstants.FutureCanDoIoFormat, &format));
    }

    public void Dispose()
    {
        if (_instance != IntPtr.Zero)
        {
            uint remaining = ((delegate* unmanaged<IntPtr, uint>)Table[2])(_instance);
            _instance = IntPtr.Zero;
            AsioTrace.DriverReleased($"{Name} (COM references left: {remaining})");
        }
    }

    private static string Decode(byte* buffer, int capacity)
    {
        int length = 0;
        while (length < capacity && buffer[length] != 0)
        {
            length++;
        }

        return Encoding.Default.GetString(buffer, length);
    }
}
