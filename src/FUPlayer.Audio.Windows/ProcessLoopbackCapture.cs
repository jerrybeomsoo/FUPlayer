using System.Runtime.InteropServices;
using FUPlayer.Audio.Windows.Interop;
using FUPlayer.Core.Engine;

namespace FUPlayer.Audio.Windows;

/// <summary>
/// Captures what one process renders, using the process-loopback pseudo-device that Windows 10 build
/// 20348 and later expose. The stream is the process's audio after the session mixer and after any
/// rate conversion the audio engine applied, not the bits the application decoded.
/// </summary>
public sealed class ProcessLoopbackCapture : IDisposable
{
    private const int ActivationTimeoutSeconds = 5;

    /// <summary>Roughly two seconds of stereo float, which is far more than the reader should ever be behind by.</summary>
    private const double RingSeconds = 2.0;

    private readonly int _processId;
    private readonly bool _includeTree;
    private readonly SpscByteRing _ring;
    private readonly ManualResetEventSlim _started = new(false);
    private readonly CancellationTokenSource _stop = new();

    private Thread? _thread;
    private bool _float32 = true;
    private long _silentFrames;
    private long _capturedFrames;

    public ProcessLoopbackCapture(int processId, int sampleRate, int channels, bool includeProcessTree = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);

        _processId = processId;
        _includeTree = includeProcessTree;
        SampleRate = sampleRate;
        Channels = channels;

        int bytesPerFrame = channels * sizeof(float);
        _ring = new SpscByteRing((int)(sampleRate * RingSeconds) * bytesPerFrame);
    }

    public int SampleRate { get; }

    public int Channels { get; }

    /// <summary>Null while the capture is healthy; the reason it stopped otherwise.</summary>
    public string? Error { get; private set; }

    /// <summary>Frames the reader had to invent because the process was not rendering.</summary>
    public long SilentFrames => Interlocked.Read(ref _silentFrames);

    /// <summary>Frames the process actually rendered.</summary>
    public long CapturedFrames => Interlocked.Read(ref _capturedFrames);

    public bool IsRunning => _thread is { IsAlive: true } && Error is null;

    /// <summary>Starts the capture thread and waits for the device to open, so failures surface here.</summary>
    public void Start()
    {
        if (_thread is not null)
        {
            return;
        }

        if (!LoopbackConstants.IsSupported)
        {
            throw new NotSupportedException(
                "Capturing one application's audio needs Windows 10 build 20348 or later.");
        }

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = $"FUPlayer capture {_processId}",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();

        if (!_started.Wait(TimeSpan.FromSeconds(ActivationTimeoutSeconds + 2)) && Error is null)
        {
            Error = "The capture device did not open in time.";
        }

        if (Error is not null)
        {
            throw new InvalidOperationException(Error);
        }
    }

    /// <summary>
    /// Reads interleaved frames. A process that is not rendering produces nothing at all rather than
    /// silence, so the shortfall is filled with zeros to keep the player's clock moving.
    /// </summary>
    public int Read(Span<float> destination, int frames)
    {
        int wanted = frames * Channels;
        Span<byte> bytes = MemoryMarshal.AsBytes(destination[..wanted]);
        int got = _ring.Read(bytes);
        if (got < bytes.Length)
        {
            bytes[got..].Clear();
            Interlocked.Add(ref _silentFrames, (bytes.Length - got) / (Channels * sizeof(float)));
        }

        return frames;
    }

    public void Stop()
    {
        _stop.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
    }

    public void Dispose()
    {
        Stop();
        _stop.Dispose();
        _started.Dispose();
    }

    private void Run()
    {
        IntPtr sampleReady = IntPtr.Zero;
        IAudioClient? client = null;
        try
        {
            NativeMethods.CoInitializeEx(IntPtr.Zero, 0);
            client = Activate();
            sampleReady = NativeMethods.CreateEvent(IntPtr.Zero, false, false, IntPtr.Zero);
            IAudioCaptureClient capture = Open(client, sampleReady);

            _started.Set();
            Capture(capture, sampleReady);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            _started.Set();
        }
        finally
        {
            if (client is not null)
            {
                client.Stop();
                Marshal.ReleaseComObject(client);
            }

            if (sampleReady != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(sampleReady);
            }

            NativeMethods.CoUninitialize();
        }
    }

    private IAudioClient Activate()
    {
        var parameters = new AudioClientActivationParams
        {
            ActivationType = AudioClientActivationType.ProcessLoopback,
            ProcessLoopbackParams = new AudioClientProcessLoopbackParams
            {
                TargetProcessId = (uint)_processId,
                LoopbackMode = _includeTree
                    ? ProcessLoopbackMode.IncludeTargetProcessTree
                    : ProcessLoopbackMode.ExcludeTargetProcessTree,
            },
        };

        int size = Marshal.SizeOf<AudioClientActivationParams>();
        IntPtr block = Marshal.AllocHGlobal(size);
        using var handler = new ActivationHandler();
        try
        {
            Marshal.StructureToPtr(parameters, block, false);
            var variant = new BlobPropVariant
            {
                VarType = BlobPropVariant.VtBlob,
                BlobSize = (uint)size,
                BlobData = block,
            };

            Guid iid = WasapiConstants.AudioClientIid;
            LoopbackNative.ActivateAudioInterfaceAsync(
                LoopbackConstants.ProcessLoopbackDevice, ref iid, ref variant, handler, out IActivateAudioInterfaceAsyncOperation operation);

            if (!handler.Wait(TimeSpan.FromSeconds(ActivationTimeoutSeconds)))
            {
                throw new TimeoutException("Windows did not answer the capture request.");
            }

            int hr = operation.GetActivateResult(out int activateResult, out object activated);
            Marshal.ReleaseComObject(operation);
            if (hr != WasapiConstants.SOk)
            {
                throw new InvalidOperationException(WasapiConstants.Describe(hr));
            }

            if (activateResult != WasapiConstants.SOk)
            {
                throw new InvalidOperationException(
                    $"Process {_processId} cannot be captured: {WasapiConstants.Describe(activateResult)}");
            }

            return (IAudioClient)activated;
        }
        finally
        {
            Marshal.FreeHGlobal(block);
        }
    }

    private IAudioCaptureClient Open(IAudioClient client, IntPtr sampleReady)
    {
        // The process-loopback client has no mix format of its own: it converts to whatever is asked
        // for. Float is asked for first so that nothing is quantised on the way in.
        int hr = Initialize(client, float32: true);
        if (hr != WasapiConstants.SOk)
        {
            hr = Initialize(client, float32: false);
        }

        if (hr != WasapiConstants.SOk)
        {
            throw new InvalidOperationException(WasapiConstants.Describe(hr));
        }

        hr = client.SetEventHandle(sampleReady);
        if (hr != WasapiConstants.SOk)
        {
            throw new InvalidOperationException(WasapiConstants.Describe(hr));
        }

        Guid iid = LoopbackConstants.AudioCaptureClientIid;
        hr = client.GetService(ref iid, out object service);
        if (hr != WasapiConstants.SOk)
        {
            throw new InvalidOperationException(WasapiConstants.Describe(hr));
        }

        hr = client.Start();
        if (hr != WasapiConstants.SOk)
        {
            throw new InvalidOperationException(WasapiConstants.Describe(hr));
        }

        return (IAudioCaptureClient)service;
    }

    private int Initialize(IAudioClient client, bool float32)
    {
        WaveFormatExtensibleNative format = WaveFormatExtensibleNative.Create(
            SampleRate, Channels, float32 ? 32 : 16, float32 ? 32 : 16, float32);

        IntPtr block = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormatExtensibleNative>());
        try
        {
            Marshal.StructureToPtr(format, block, false);
            _float32 = float32;
            return client.Initialize(
                AudioClientShareMode.Shared,
                LoopbackConstants.StreamFlagsLoopback | WasapiConstants.StreamFlagsEventCallback,
                200_000,
                0,
                block,
                IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeHGlobal(block);
        }
    }

    private void Capture(IAudioCaptureClient capture, IntPtr sampleReady)
    {
        float[] scratch = new float[4096 * Channels];

        try
        {
            while (!_stop.IsCancellationRequested)
            {
                // A process that stops rendering stops signalling, so the wait has to time out rather
                // than block for ever. The reader fills the gap with silence.
                NativeMethods.WaitForSingleObject(sampleReady, 100);

                while (capture.GetNextPacketSize(out uint packet) == WasapiConstants.SOk && packet > 0)
                {
                    int hr = capture.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _);
                    if (hr != WasapiConstants.SOk || frames == 0)
                    {
                        if (hr == WasapiConstants.SOk)
                        {
                            capture.ReleaseBuffer(frames);
                        }

                        break;
                    }

                    int samples = (int)frames * Channels;
                    if (samples > scratch.Length)
                    {
                        scratch = new float[samples];
                    }

                    if ((flags & LoopbackConstants.BufferFlagsSilent) != 0)
                    {
                        Array.Clear(scratch, 0, samples);
                    }
                    else if (_float32)
                    {
                        CopyFloat(data, scratch, samples);
                    }
                    else
                    {
                        CopyInt16(data, scratch, samples);
                    }

                    _ring.Write(MemoryMarshal.AsBytes(scratch.AsSpan(0, samples)));
                    Interlocked.Add(ref _capturedFrames, frames);
                    capture.ReleaseBuffer(frames);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(capture);
        }
    }

    private static unsafe void CopyFloat(IntPtr source, float[] destination, int samples)
    {
        new ReadOnlySpan<float>((void*)source, samples).CopyTo(destination.AsSpan(0, samples));
    }

    private static unsafe void CopyInt16(IntPtr source, float[] destination, int samples)
    {
        var input = new ReadOnlySpan<short>((void*)source, samples);
        for (int i = 0; i < samples; i++)
        {
            destination[i] = input[i] / 32768f;
        }
    }
}
