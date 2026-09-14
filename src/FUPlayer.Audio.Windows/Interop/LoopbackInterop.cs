using System.Runtime.InteropServices;

namespace FUPlayer.Audio.Windows.Interop;

/// <summary>Which audio a process-loopback client collects.</summary>
internal enum ProcessLoopbackMode
{
    /// <summary>The target process and everything it launched.</summary>
    IncludeTargetProcessTree = 0,

    /// <summary>Everything except the target process and its children.</summary>
    ExcludeTargetProcessTree = 1,
}

internal enum AudioClientActivationType
{
    Default = 0,
    ProcessLoopback = 1,
}

[StructLayout(LayoutKind.Sequential)]
internal struct AudioClientProcessLoopbackParams
{
    public uint TargetProcessId;
    public ProcessLoopbackMode LoopbackMode;
}

/// <summary>
/// AUDIOCLIENT_ACTIVATION_PARAMS. The native type holds a union whose only member today is the
/// process-loopback parameters, so a plain struct matches its layout.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct AudioClientActivationParams
{
    public AudioClientActivationType ActivationType;
    public AudioClientProcessLoopbackParams ProcessLoopbackParams;
}

/// <summary>PROPVARIANT holding a BLOB, which is how the activation parameters are passed.</summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct BlobPropVariant
{
    public const ushort VtBlob = 65;

    [FieldOffset(0)]
    public ushort VarType;

    [FieldOffset(8)]
    public uint BlobSize;

    [FieldOffset(16)]
    public IntPtr BlobData;
}

internal static class LoopbackConstants
{
    /// <summary>The pseudo-device that carries one process's render stream.</summary>
    public const string ProcessLoopbackDevice = "VAD\\Process_Loopback";

    public const uint StreamFlagsLoopback = 0x00020000;

    /// <summary>AUDCLNT_BUFFERFLAGS_SILENT: the packet is silence and its contents must be ignored.</summary>
    public const uint BufferFlagsSilent = 0x2;

    /// <summary>AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY: a gap precedes this packet.</summary>
    public const uint BufferFlagsDiscontinuity = 0x1;

    public static readonly Guid AudioCaptureClientIid = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    /// <summary>Process loopback needs Windows 10 build 20348; earlier builds fail the activation.</summary>
    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348);
}

[ComImport]
[Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig]
    int GetBuffer(out IntPtr data, out uint frames, out uint flags, out long devicePosition, out long counterPosition);

    [PreserveSig]
    int ReleaseBuffer(uint framesRead);

    [PreserveSig]
    int GetNextPacketSize(out uint frames);
}

[ComImport]
[Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceAsyncOperation
{
    [PreserveSig]
    int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
}

[ComImport]
[Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceCompletionHandler
{
    [PreserveSig]
    int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
}

/// <summary>
/// Signals a wait handle when the asynchronous activation finishes. The call back arrives on a
/// multi-threaded apartment thread supplied by the audio service, not on the caller's thread.
/// </summary>
internal sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IDisposable
{
    private readonly ManualResetEventSlim _done = new(false);

    public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
    {
        _done.Set();
        return WasapiConstants.SOk;
    }

    public bool Wait(TimeSpan timeout) => _done.Wait(timeout);

    public void Dispose() => _done.Dispose();
}

internal static class LoopbackNative
{
    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    public static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string devicePath,
        ref Guid iid,
        ref BlobPropVariant activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation operation);
}
