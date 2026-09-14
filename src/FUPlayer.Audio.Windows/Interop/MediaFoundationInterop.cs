using System.Runtime.InteropServices;

namespace FUPlayer.Audio.Windows.Interop;

/// <summary>
/// Just enough Media Foundation to encode PCM to AAC and read it back.
///
/// Windows ships an AAC encoder and decoder, so a real coded copy of a file can be made without
/// fetching anything. That is what the training data is built from: the difference between a file and
/// the same file after a real encoder has been at it.
/// </summary>
internal static class MediaFoundationConstants
{
    public const int Version = 0x00020070;
    public const int StartupLite = 1;

    public const int SinkWriterAllStreams = unchecked((int)0xFFFFFFFE);
    public const int SourceReaderFirstAudioStream = unchecked((int)0xFFFFFFFD);

    public const uint BufferFlagEndOfStream = 0x2;

    public static readonly Guid MajorTypeAudio = new("73647561-0000-0010-8000-00aa00389b71");
    public static readonly Guid AudioFormatPcm = new("00000001-0000-0010-8000-00aa00389b71");
    public static readonly Guid AudioFormatFloat = new("00000003-0000-0010-8000-00aa00389b71");
    public static readonly Guid AudioFormatAac = new("00001610-0000-0010-8000-00aa00389b71");

    public static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid Channels = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    public static readonly Guid SamplesPerSecond = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    public static readonly Guid BitsPerSample = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    public static readonly Guid BlockAlignment = new("322de230-9eeb-43bd-ab7a-ff412251541d");
    public static readonly Guid AverageBytesPerSecond = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");
    public static readonly Guid AacPayloadType = new("bfbabe79-7434-4d1c-94f0-72a3b9e17188");
    public static readonly Guid AacProfileLevel = new("eb3d6c1b-1b0f-4bc0-9c4f-2f3ff1e1c4b8");
}

[ComImport]
[Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFAttributes
{
    [PreserveSig]
    int GetItem(ref Guid key, IntPtr value);

    [PreserveSig]
    int GetItemType(ref Guid key, out int type);

    [PreserveSig]
    int CompareItem(ref Guid key, IntPtr value, [MarshalAs(UnmanagedType.Bool)] out bool result);

    [PreserveSig]
    int Compare(IMFAttributes other, int matchType, [MarshalAs(UnmanagedType.Bool)] out bool result);

    [PreserveSig]
    int GetUINT32(ref Guid key, out int value);

    [PreserveSig]
    int GetUINT64(ref Guid key, out long value);

    [PreserveSig]
    int GetDouble(ref Guid key, out double value);

    [PreserveSig]
    int GetGUID(ref Guid key, out Guid value);

    [PreserveSig]
    int GetStringLength(ref Guid key, out int length);

    [PreserveSig]
    int GetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value, int size, out int length);

    [PreserveSig]
    int GetAllocatedString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] out string value, out int length);

    [PreserveSig]
    int GetBlobSize(ref Guid key, out int size);

    [PreserveSig]
    int GetBlob(ref Guid key, IntPtr buffer, int size, out int written);

    [PreserveSig]
    int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out int size);

    [PreserveSig]
    int GetUnknown(ref Guid key, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object value);

    [PreserveSig]
    int SetItem(ref Guid key, IntPtr value);

    [PreserveSig]
    int DeleteItem(ref Guid key);

    [PreserveSig]
    int DeleteAllItems();

    [PreserveSig]
    int SetUINT32(ref Guid key, int value);

    [PreserveSig]
    int SetUINT64(ref Guid key, long value);

    [PreserveSig]
    int SetDouble(ref Guid key, double value);

    [PreserveSig]
    int SetGUID(ref Guid key, ref Guid value);

    [PreserveSig]
    int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);

    [PreserveSig]
    int SetBlob(ref Guid key, IntPtr buffer, int size);

    [PreserveSig]
    int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);

    [PreserveSig]
    int LockStore();

    [PreserveSig]
    int UnlockStore();

    [PreserveSig]
    int GetCount(out int count);

    [PreserveSig]
    int GetItemByIndex(int index, out Guid key, IntPtr value);

    [PreserveSig]
    int CopyAllItems(IMFAttributes destination);
}

[ComImport]
[Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaType : IMFAttributes
{
    // IMFAttributes, repeated so the virtual table lines up.
    [PreserveSig] new int GetItem(ref Guid key, IntPtr value);
    [PreserveSig] new int GetItemType(ref Guid key, out int type);
    [PreserveSig] new int CompareItem(ref Guid key, IntPtr value, [MarshalAs(UnmanagedType.Bool)] out bool result);
    [PreserveSig] new int Compare(IMFAttributes other, int matchType, [MarshalAs(UnmanagedType.Bool)] out bool result);
    [PreserveSig] new int GetUINT32(ref Guid key, out int value);
    [PreserveSig] new int GetUINT64(ref Guid key, out long value);
    [PreserveSig] new int GetDouble(ref Guid key, out double value);
    [PreserveSig] new int GetGUID(ref Guid key, out Guid value);
    [PreserveSig] new int GetStringLength(ref Guid key, out int length);
    [PreserveSig] new int GetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value, int size, out int length);
    [PreserveSig] new int GetAllocatedString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] out string value, out int length);
    [PreserveSig] new int GetBlobSize(ref Guid key, out int size);
    [PreserveSig] new int GetBlob(ref Guid key, IntPtr buffer, int size, out int written);
    [PreserveSig] new int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out int size);
    [PreserveSig] new int GetUnknown(ref Guid key, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object value);
    [PreserveSig] new int SetItem(ref Guid key, IntPtr value);
    [PreserveSig] new int DeleteItem(ref Guid key);
    [PreserveSig] new int DeleteAllItems();
    [PreserveSig] new int SetUINT32(ref Guid key, int value);
    [PreserveSig] new int SetUINT64(ref Guid key, long value);
    [PreserveSig] new int SetDouble(ref Guid key, double value);
    [PreserveSig] new int SetGUID(ref Guid key, ref Guid value);
    [PreserveSig] new int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [PreserveSig] new int SetBlob(ref Guid key, IntPtr buffer, int size);
    [PreserveSig] new int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
    [PreserveSig] new int LockStore();
    [PreserveSig] new int UnlockStore();
    [PreserveSig] new int GetCount(out int count);
    [PreserveSig] new int GetItemByIndex(int index, out Guid key, IntPtr value);
    [PreserveSig] new int CopyAllItems(IMFAttributes destination);

    [PreserveSig]
    int GetMajorType(out Guid type);

    [PreserveSig]
    int IsCompressedFormat([MarshalAs(UnmanagedType.Bool)] out bool compressed);

    [PreserveSig]
    int IsEqual(IMFMediaType other, out int flags);

    [PreserveSig]
    int GetRepresentation(Guid representation, out IntPtr value);

    [PreserveSig]
    int FreeRepresentation(Guid representation, IntPtr value);
}

[ComImport]
[Guid("045FA593-8799-42b8-BC8D-8968C6453507")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaBuffer
{
    [PreserveSig]
    int Lock(out IntPtr buffer, out int maxLength, out int currentLength);

    [PreserveSig]
    int Unlock();

    [PreserveSig]
    int GetCurrentLength(out int length);

    [PreserveSig]
    int SetCurrentLength(int length);

    [PreserveSig]
    int GetMaxLength(out int length);
}

[ComImport]
[Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSample : IMFAttributes
{
    [PreserveSig] new int GetItem(ref Guid key, IntPtr value);
    [PreserveSig] new int GetItemType(ref Guid key, out int type);
    [PreserveSig] new int CompareItem(ref Guid key, IntPtr value, [MarshalAs(UnmanagedType.Bool)] out bool result);
    [PreserveSig] new int Compare(IMFAttributes other, int matchType, [MarshalAs(UnmanagedType.Bool)] out bool result);
    [PreserveSig] new int GetUINT32(ref Guid key, out int value);
    [PreserveSig] new int GetUINT64(ref Guid key, out long value);
    [PreserveSig] new int GetDouble(ref Guid key, out double value);
    [PreserveSig] new int GetGUID(ref Guid key, out Guid value);
    [PreserveSig] new int GetStringLength(ref Guid key, out int length);
    [PreserveSig] new int GetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value, int size, out int length);
    [PreserveSig] new int GetAllocatedString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] out string value, out int length);
    [PreserveSig] new int GetBlobSize(ref Guid key, out int size);
    [PreserveSig] new int GetBlob(ref Guid key, IntPtr buffer, int size, out int written);
    [PreserveSig] new int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out int size);
    [PreserveSig] new int GetUnknown(ref Guid key, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object value);
    [PreserveSig] new int SetItem(ref Guid key, IntPtr value);
    [PreserveSig] new int DeleteItem(ref Guid key);
    [PreserveSig] new int DeleteAllItems();
    [PreserveSig] new int SetUINT32(ref Guid key, int value);
    [PreserveSig] new int SetUINT64(ref Guid key, long value);
    [PreserveSig] new int SetDouble(ref Guid key, double value);
    [PreserveSig] new int SetGUID(ref Guid key, ref Guid value);
    [PreserveSig] new int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [PreserveSig] new int SetBlob(ref Guid key, IntPtr buffer, int size);
    [PreserveSig] new int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
    [PreserveSig] new int LockStore();
    [PreserveSig] new int UnlockStore();
    [PreserveSig] new int GetCount(out int count);
    [PreserveSig] new int GetItemByIndex(int index, out Guid key, IntPtr value);
    [PreserveSig] new int CopyAllItems(IMFAttributes destination);

    [PreserveSig]
    int GetSampleFlags(out int flags);

    [PreserveSig]
    int SetSampleFlags(int flags);

    [PreserveSig]
    int GetSampleTime(out long time);

    [PreserveSig]
    int SetSampleTime(long time);

    [PreserveSig]
    int GetSampleDuration(out long duration);

    [PreserveSig]
    int SetSampleDuration(long duration);

    [PreserveSig]
    int GetBufferCount(out int count);

    [PreserveSig]
    int GetBufferByIndex(int index, out IMFMediaBuffer buffer);

    [PreserveSig]
    int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);

    [PreserveSig]
    int AddBuffer(IMFMediaBuffer buffer);

    [PreserveSig]
    int RemoveBufferByIndex(int index);

    [PreserveSig]
    int RemoveAllBuffers();

    [PreserveSig]
    int GetTotalLength(out int length);

    [PreserveSig]
    int CopyToBuffer(IMFMediaBuffer buffer);
}

[ComImport]
[Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSinkWriter
{
    [PreserveSig]
    int AddStream(IMFMediaType targetType, out int streamIndex);

    [PreserveSig]
    int SetInputMediaType(int streamIndex, IMFMediaType inputType, IMFAttributes? parameters);

    [PreserveSig]
    int BeginWriting();

    [PreserveSig]
    int WriteSample(int streamIndex, IMFSample sample);

    [PreserveSig]
    int SendStreamTick(int streamIndex, long timestamp);

    [PreserveSig]
    int PlaceMarker(int streamIndex, IntPtr context);

    [PreserveSig]
    int NotifyEndOfSegment(int streamIndex);

    [PreserveSig]
    int Flush(int streamIndex);

    [PreserveSig]
    int Finalize_();

    [PreserveSig]
    int GetServiceForStream(int streamIndex, ref Guid service, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object value);

    [PreserveSig]
    int GetStatistics(int streamIndex, IntPtr statistics);
}

[ComImport]
[Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSourceReader
{
    [PreserveSig]
    int GetStreamSelection(int streamIndex, [MarshalAs(UnmanagedType.Bool)] out bool selected);

    [PreserveSig]
    int SetStreamSelection(int streamIndex, [MarshalAs(UnmanagedType.Bool)] bool selected);

    [PreserveSig]
    int GetNativeMediaType(int streamIndex, int typeIndex, out IMFMediaType type);

    [PreserveSig]
    int GetCurrentMediaType(int streamIndex, out IMFMediaType type);

    [PreserveSig]
    int SetCurrentMediaType(int streamIndex, IntPtr reserved, IMFMediaType type);

    [PreserveSig]
    int SetCurrentPosition(ref Guid format, IntPtr position);

    [PreserveSig]
    int ReadSample(int streamIndex, int flags, out int actualStreamIndex, out int streamFlags, out long timestamp, out IMFSample? sample);

    [PreserveSig]
    int Flush(int streamIndex);

    [PreserveSig]
    int GetServiceForStream(int streamIndex, ref Guid service, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object value);

    [PreserveSig]
    int GetPresentationAttribute(int streamIndex, ref Guid key, IntPtr value);
}

internal static class MediaFoundationNative
{
    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFStartup(int version, int flags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFShutdown();

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateMediaType(out IMFMediaType type);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateSample(out IMFSample sample);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateMemoryBuffer(int maxLength, out IMFMediaBuffer buffer);

    [DllImport("mfreadwrite.dll", ExactSpelling = true)]
    public static extern int MFCreateSinkWriterFromURL(
        [MarshalAs(UnmanagedType.LPWStr)] string url, IntPtr byteStream, IMFAttributes? attributes, out IMFSinkWriter writer);

    [DllImport("mfreadwrite.dll", ExactSpelling = true)]
    public static extern int MFCreateSourceReaderFromURL(
        [MarshalAs(UnmanagedType.LPWStr)] string url, IMFAttributes? attributes, out IMFSourceReader reader);
}
