using System.Runtime.InteropServices;

namespace FUPlayer.Audio.Windows.Interop;

internal enum DataFlow
{
    Render = 0,
    Capture = 1,
    All = 2,
}

internal enum Role
{
    Console = 0,
    Multimedia = 1,
    Communications = 2,
}

internal enum AudioClientShareMode
{
    Shared = 0,
    Exclusive = 1,
}

internal static class WasapiConstants
{
    public const int DeviceStateActive = 0x1;
    public const int StgmRead = 0;

    public const uint StreamFlagsEventCallback = 0x00040000;
    public const uint StreamFlagsNoPersist = 0x00080000;
    public const uint StreamFlagsAutoConvertPcm = 0x80000000;
    public const uint StreamFlagsSrcDefaultQuality = 0x08000000;

    public const int SOk = 0;
    public const int SFalse = 1;
    public const int ErrorBufferSizeNotAligned = unchecked((int)0x88890019);
    public const int ErrorUnsupportedFormat = unchecked((int)0x88890008);
    public const int ErrorExclusiveModeNotAllowed = unchecked((int)0x8889000E);
    public const int ErrorDeviceInUse = unchecked((int)0x8889000A);
    public const int ErrorDeviceInvalidated = unchecked((int)0x88890004);
    public const int ErrorNotFound = unchecked((int)0x80070490);

    public const ushort WaveFormatExtensible = 0xFFFE;

    public static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");
    public static readonly Guid SubtypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");
    public static readonly Guid AudioClientIid = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    public static readonly Guid AudioRenderClientIid = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
    public static readonly PropertyKey DeviceFriendlyName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);

    public static string Describe(int hresult) => hresult switch
    {
        ErrorUnsupportedFormat => "The device does not support this format in exclusive mode.",
        ErrorExclusiveModeNotAllowed => "Exclusive mode is disabled for this device (Sound settings → device properties → Advanced).",
        ErrorDeviceInUse => "The device is in use by another application in exclusive mode.",
        ErrorDeviceInvalidated => "The device was removed or disabled.",
        ErrorNotFound => "The audio device was not found.",
        _ => $"WASAPI error 0x{hresult:X8}.",
    };
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid FormatId;
    public int PropertyId;

    public PropertyKey(Guid formatId, int propertyId)
    {
        FormatId = formatId;
        PropertyId = propertyId;
    }
}

/// <summary>PROPVARIANT (24 bytes on 64-bit Windows). Only string values are read.</summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariant
{
    public const ushort VtLpwstr = 31;

    [FieldOffset(0)]
    public ushort VarType;

    [FieldOffset(8)]
    public IntPtr Pointer;

    [FieldOffset(16)]
    public IntPtr Reserved;
}

/// <summary>WAVEFORMATEXTENSIBLE (packed, 40 bytes).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct WaveFormatExtensibleNative
{
    public ushort FormatTag;
    public ushort Channels;
    public uint SamplesPerSec;
    public uint AvgBytesPerSec;
    public ushort BlockAlign;
    public ushort BitsPerSample;
    public ushort Size;
    public ushort ValidBitsPerSample;
    public uint ChannelMask;
    public Guid SubFormat;

    public static WaveFormatExtensibleNative Create(int sampleRate, int channels, int containerBits, int validBits, bool isFloat)
    {
        int blockAlign = channels * containerBits / 8;
        return new WaveFormatExtensibleNative
        {
            FormatTag = WasapiConstants.WaveFormatExtensible,
            Channels = (ushort)channels,
            SamplesPerSec = (uint)sampleRate,
            AvgBytesPerSec = (uint)(sampleRate * blockAlign),
            BlockAlign = (ushort)blockAlign,
            BitsPerSample = (ushort)containerBits,
            Size = 22,
            ValidBitsPerSample = (ushort)validBits,
            ChannelMask = ChannelMaskFor(channels),
            SubFormat = isFloat ? WasapiConstants.SubtypeIeeeFloat : WasapiConstants.SubtypePcm,
        };
    }

    /// <summary>Standard speaker masks in the FL, FR, FC, LFE, BL, BR, SL, SR order.</summary>
    public static uint ChannelMaskFor(int channels) => channels switch
    {
        1 => 0x4,
        2 => 0x3,
        3 => 0x7,
        4 => 0x33,
        5 => 0x37,
        6 => 0x3F,
        7 => 0x13F,
        8 => 0x63F,
        _ => 0,
    };
}

[ComImport]
[Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorComObject
{
}

[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig]
    int EnumAudioEndpoints(DataFlow dataFlow, int stateMask, out IMMDeviceCollection devices);

    [PreserveSig]
    int GetDefaultAudioEndpoint(DataFlow dataFlow, Role role, out IMMDevice endpoint);

    [PreserveSig]
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

    [PreserveSig]
    int RegisterEndpointNotificationCallback(IntPtr client);

    [PreserveSig]
    int UnregisterEndpointNotificationCallback(IntPtr client);
}

[ComImport]
[Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    [PreserveSig]
    int GetCount(out int count);

    [PreserveSig]
    int Item(int index, out IMMDevice device);
}

[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig]
    int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);

    [PreserveSig]
    int OpenPropertyStore(int access, out IPropertyStore properties);

    [PreserveSig]
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

    [PreserveSig]
    int GetState(out int state);
}

[ComImport]
[Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    [PreserveSig]
    int GetCount(out int count);

    [PreserveSig]
    int GetAt(int index, out PropertyKey key);

    [PreserveSig]
    int GetValue(ref PropertyKey key, out PropVariant value);

    [PreserveSig]
    int SetValue(ref PropertyKey key, ref PropVariant value);

    [PreserveSig]
    int Commit();
}

[ComImport]
[Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig]
    int Initialize(AudioClientShareMode shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr audioSessionGuid);

    [PreserveSig]
    int GetBufferSize(out uint bufferFrames);

    [PreserveSig]
    int GetStreamLatency(out long latency);

    [PreserveSig]
    int GetCurrentPadding(out uint paddingFrames);

    [PreserveSig]
    int IsFormatSupported(AudioClientShareMode shareMode, IntPtr format, IntPtr closestMatch);

    [PreserveSig]
    int GetMixFormat(out IntPtr deviceFormat);

    [PreserveSig]
    int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);

    [PreserveSig]
    int Start();

    [PreserveSig]
    int Stop();

    [PreserveSig]
    int Reset();

    [PreserveSig]
    int SetEventHandle(IntPtr eventHandle);

    [PreserveSig]
    int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
}

[ComImport]
[Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioRenderClient
{
    [PreserveSig]
    int GetBuffer(uint framesRequested, out IntPtr data);

    [PreserveSig]
    int ReleaseBuffer(uint framesWritten, uint flags);
}
