using System.Runtime.InteropServices;
using FUPlayer.Audio.Windows.Interop;
using FUPlayer.Core.Audio;
using FUPlayer.Core.Output;

namespace FUPlayer.Audio.Windows;

/// <summary>Windows Audio Session API output, in exclusive (bit-perfect) or shared (mixer) mode.</summary>
public sealed class WasapiBackend : IAudioBackend
{
    public const string ExclusiveId = "wasapi-exclusive";
    public const string SharedId = "wasapi-shared";

    private static readonly int[] ProbeChannelCounts = [8, 6, 4, 2];

    private readonly bool _exclusive;

    public WasapiBackend(bool exclusive)
    {
        _exclusive = exclusive;
    }

    public string Id => _exclusive ? ExclusiveId : SharedId;

    public string DisplayName => _exclusive ? "WASAPI exclusive" : "WASAPI shared";

    public string Description => _exclusive
        ? "Bit-perfect output that bypasses the Windows mixer. DSD is sent as DoP."
        : "Output through the Windows mixer at its mix rate. Convenient, but not bit-perfect.";

    public bool IsAvailable => true;

    public bool SupportsNativeDsd => false;

    public bool IsBitPerfect => _exclusive;

    public bool HasControlPanel => false;

    public IReadOnlyList<AudioDevice> GetDevices()
    {
        var devices = new List<AudioDevice>();
        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? collection = null;
        try
        {
            enumerator = CreateEnumerator();
            string? defaultId = null;
            if (enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out IMMDevice defaultDevice) == WasapiConstants.SOk)
            {
                defaultDevice.GetId(out defaultId);
                Release(defaultDevice);
            }

            if (enumerator.EnumAudioEndpoints(DataFlow.Render, WasapiConstants.DeviceStateActive, out collection) != WasapiConstants.SOk)
            {
                return devices;
            }

            collection.GetCount(out int count);
            for (int i = 0; i < count; i++)
            {
                if (collection.Item(i, out IMMDevice device) != WasapiConstants.SOk)
                {
                    continue;
                }

                try
                {
                    device.GetId(out string id);
                    devices.Add(new AudioDevice(Id, id, ReadFriendlyName(device) ?? id, id == defaultId));
                }
                finally
                {
                    Release(device);
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            // No audio service or endpoints: return what was found.
        }
        finally
        {
            Release(collection);
            Release(enumerator);
        }

        return devices;
    }

    public DeviceCapabilities GetCapabilities(string? deviceId, int channels)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioClient? client = null;
        try
        {
            enumerator = CreateEnumerator();
            device = OpenDevice(enumerator, deviceId);
            client = ActivateClient(device);
            return _exclusive ? ProbeExclusive(client, channels) : ProbeShared(client);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or InvalidCastException)
        {
            return new DeviceCapabilities { MaxChannels = 2, Notes = $"Could not query the device: {ex.Message}" };
        }
        finally
        {
            Release(client);
            Release(device);
            Release(enumerator);
        }
    }

    public IAudioStream OpenStream(string? deviceId, OutputFormat format, AudioStreamOptions options, IAudioRenderSource source) =>
        new WasapiStream(deviceId, _exclusive, format, options, source);

    public void ShowControlPanel(string? deviceId)
    {
    }

    internal static IMMDeviceEnumerator CreateEnumerator() => (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

    internal static IMMDevice OpenDevice(IMMDeviceEnumerator enumerator, string? deviceId)
    {
        IMMDevice device;
        int hr = string.IsNullOrEmpty(deviceId)
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out device)
            : enumerator.GetDevice(deviceId, out device);
        if (hr != WasapiConstants.SOk)
        {
            throw new InvalidOperationException(WasapiConstants.Describe(hr));
        }

        return device;
    }

    internal static IAudioClient ActivateClient(IMMDevice device)
    {
        Guid iid = WasapiConstants.AudioClientIid;
        int hr = device.Activate(ref iid, NativeMethods.ClsCtxAll, IntPtr.Zero, out object instance);
        if (hr != WasapiConstants.SOk)
        {
            throw new InvalidOperationException(WasapiConstants.Describe(hr));
        }

        return (IAudioClient)instance;
    }

    internal static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            Marshal.ReleaseComObject(comObject);
        }
    }

    /// <summary>
    /// Drops every runtime reference to a COM object at once. Used for the audio and render clients of a stream, so an
    /// exclusive-mode device is handed back to Windows as soon as the stream ends.
    /// </summary>
    internal static void ReleaseAll(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            Marshal.FinalReleaseComObject(comObject);
        }
    }

    internal static unsafe bool SupportsExclusive(IAudioClient client, int rate, int channels, int containerBits, int validBits)
    {
        WaveFormatExtensibleNative format = WaveFormatExtensibleNative.Create(rate, channels, containerBits, validBits, isFloat: false);
        return client.IsFormatSupported(AudioClientShareMode.Exclusive, (IntPtr)(&format), IntPtr.Zero) == WasapiConstants.SOk;
    }

    private static DeviceCapabilities ProbeExclusive(IAudioClient client, int channels)
    {
        int maxChannels = 2;
        foreach (int count in ProbeChannelCounts)
        {
            if (SupportsExclusive(client, 48_000, count, 32, 24) || SupportsExclusive(client, 48_000, count, 24, 24)
                || SupportsExclusive(client, 48_000, count, 32, 32) || SupportsExclusive(client, 44_100, count, 16, 16))
            {
                maxChannels = count;
                break;
            }
        }

        int probeChannels = Math.Clamp(channels, 1, maxChannels);
        var rates = new List<int>();
        var containers = new SortedSet<int>();
        foreach (int rate in AudioRates.StandardPcmRates)
        {
            bool supported = false;
            foreach (int bits in (int[])[16, 24, 32])
            {
                if (SupportsExclusive(client, rate, probeChannels, bits, bits) || (bits == 32 && SupportsExclusive(client, rate, probeChannels, 32, 24)))
                {
                    containers.Add(bits);
                    supported = true;
                }
            }

            if (supported)
            {
                rates.Add(rate);
            }
        }

        return new DeviceCapabilities
        {
            MaxChannels = maxChannels,
            PcmRates = rates,
            ContainerBits = containers.ToArray(),
            Notes = rates.Count == 0
                ? "The device accepted no exclusive-mode format. Make sure exclusive mode is allowed in the Windows sound device properties."
                : null,
        };
    }

    private static unsafe DeviceCapabilities ProbeShared(IAudioClient client)
    {
        Marshal.ThrowExceptionForHR(client.GetMixFormat(out IntPtr mix));
        try
        {
            int channels = *(ushort*)(mix + 2);
            int rate = *(int*)(mix + 4);
            return new DeviceCapabilities
            {
                MaxChannels = Math.Max(1, channels),
                PcmRates = [rate],
                ContainerBits = [32],
                Notes = "Shared mode always plays at the Windows mix rate (set it in the device's advanced sound properties).",
            };
        }
        finally
        {
            Marshal.FreeCoTaskMem(mix);
        }
    }

    internal static string? ReadFriendlyName(IMMDevice device)
    {
        if (device.OpenPropertyStore(WasapiConstants.StgmRead, out IPropertyStore store) != WasapiConstants.SOk)
        {
            return null;
        }

        try
        {
            PropertyKey key = WasapiConstants.DeviceFriendlyName;
            if (store.GetValue(ref key, out PropVariant value) != WasapiConstants.SOk)
            {
                return null;
            }

            try
            {
                return value.VarType == PropVariant.VtLpwstr ? Marshal.PtrToStringUni(value.Pointer) : null;
            }
            finally
            {
                NativeMethods.PropVariantClear(ref value);
            }
        }
        finally
        {
            Release(store);
        }
    }
}

/// <summary>Event-driven WASAPI render stream. All COM objects live on the stream's own audio thread.</summary>
internal sealed unsafe class WasapiStream : IAudioStream
{
    private const double HundredNanosecondsPerSecond = 10_000_000.0;

    private readonly string? _deviceId;
    private readonly bool _exclusive;
    private readonly AudioStreamOptions _options;
    private readonly IAudioRenderSource _source;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _initialized = new(false);
    private readonly ManualResetEventSlim _startRequested = new(false);
    private Exception? _initializationError;
    private volatile bool _stopRequested;
    private IntPtr _event;
    private int _deviceContainerBits;
    private bool _float;
    private bool _disposed;

    public WasapiStream(string? deviceId, bool exclusive, OutputFormat format, AudioStreamOptions options, IAudioRenderSource source)
    {
        if (format.Kind == OutputSampleKind.NativeDsd)
        {
            throw new NotSupportedException("WASAPI cannot carry native DSD; use DoP.");
        }

        _deviceId = deviceId;
        _exclusive = exclusive;
        _options = options;
        _source = source;
        Format = format;
        _event = NativeMethods.CreateEvent(IntPtr.Zero, false, false, IntPtr.Zero);
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = exclusive ? "FUPLAYER WASAPI exclusive" : "FUPLAYER WASAPI shared",
            Priority = ThreadPriority.Highest,
        };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        _initialized.Wait();
        if (_initializationError is not null)
        {
            // The caller never gets an object to dispose, so everything this constructor took has to go back here;
            // a user cycling through devices that will not open would otherwise leak a handle set each time.
            _thread.Join();
            NativeMethods.CloseHandle(_event);
            _event = IntPtr.Zero;
            _initialized.Dispose();
            _startRequested.Dispose();
            _disposed = true;
            throw _initializationError is InvalidOperationException ? _initializationError : new InvalidOperationException(_initializationError.Message, _initializationError);
        }
    }

    public event EventHandler<Exception>? Failed;

    public OutputFormat Format { get; }

    public int BufferFrames { get; private set; }

    public double LatencySeconds { get; private set; }

    public bool IsRealtime => true;

    public void Start() => _startRequested.Set();

    public void Stop()
    {
        if (_stopRequested)
        {
            return;
        }

        _stopRequested = true;
        _startRequested.Set();
        if (_event != IntPtr.Zero)
        {
            NativeMethods.SetEvent(_event);
        }

        _thread.Join();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        if (_event != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_event);
            _event = IntPtr.Zero;
        }

        _initialized.Dispose();
        _startRequested.Dispose();
    }

    private void Run()
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioClient? client = null;
        IAudioRenderClient? render = null;
        IntPtr mmcss = IntPtr.Zero;
        bool started = false;
        try
        {
            enumerator = WasapiBackend.CreateEnumerator();
            device = WasapiBackend.OpenDevice(enumerator, _deviceId);
            client = Initialize(device);
            Marshal.ThrowExceptionForHR(client.GetBufferSize(out uint bufferFrames));
            BufferFrames = (int)bufferFrames;
            client.GetStreamLatency(out long streamLatency);
            LatencySeconds = (double)bufferFrames / Format.SampleRate + streamLatency / HundredNanosecondsPerSecond;
            Marshal.ThrowExceptionForHR(client.SetEventHandle(_event));
            Guid renderIid = WasapiConstants.AudioRenderClientIid;
            Marshal.ThrowExceptionForHR(client.GetService(ref renderIid, out object service));
            render = (IAudioRenderClient)service;
            _initialized.Set();

            _startRequested.Wait();
            if (_stopRequested)
            {
                return;
            }

            uint taskIndex = 0;
            mmcss = NativeMethods.AvSetMmThreadCharacteristics("Pro Audio", ref taskIndex);

            int deviceBytesPerFrame = Format.Channels * _deviceContainerBits / 8;
            var canonical = new byte[BufferFrames * Format.CanonicalBytesPerFrame];
            if (_exclusive)
            {
                FillBuffer(render, BufferFrames, canonical, deviceBytesPerFrame);
            }

            Marshal.ThrowExceptionForHR(client.Start());
            started = true;

            while (!_stopRequested)
            {
                uint wait = NativeMethods.WaitForSingleObject(_event, 2000);
                if (_stopRequested)
                {
                    break;
                }

                if (wait != NativeMethods.WaitObject0)
                {
                    continue;
                }

                int frames = BufferFrames;
                if (!_exclusive)
                {
                    Marshal.ThrowExceptionForHR(client.GetCurrentPadding(out uint padding));
                    frames = BufferFrames - (int)padding;
                }

                if (frames > 0)
                {
                    FillBuffer(render, frames, canonical, deviceBytesPerFrame);
                }
            }
        }
        catch (Exception ex)
        {
            if (!_initialized.IsSet)
            {
                _initializationError = ex;
            }
            else if (!_stopRequested)
            {
                Failed?.Invoke(this, ex);
            }
        }
        finally
        {
            if (started)
            {
                client?.Stop();
            }

            if (mmcss != IntPtr.Zero)
            {
                NativeMethods.AvRevertMmThreadCharacteristics(mmcss);
            }

            WasapiBackend.ReleaseAll(render);
            WasapiBackend.ReleaseAll(client);
            WasapiBackend.Release(device);
            WasapiBackend.Release(enumerator);
            _initialized.Set();
        }
    }

    private IAudioClient Initialize(IMMDevice device)
    {
        IAudioClient client = WasapiBackend.ActivateClient(device);
        if (!_exclusive)
        {
            _float = true;
            _deviceContainerBits = 32;
            WaveFormatExtensibleNative mix = WaveFormatExtensibleNative.Create(Format.SampleRate, Format.Channels, 32, 32, isFloat: true);
            int shared = client.Initialize(
                AudioClientShareMode.Shared,
                WasapiConstants.StreamFlagsEventCallback | WasapiConstants.StreamFlagsNoPersist,
                0,
                0,
                (IntPtr)(&mix),
                IntPtr.Zero);
            if (shared != WasapiConstants.SOk)
            {
                WasapiBackend.Release(client);
                throw new InvalidOperationException(WasapiConstants.Describe(shared));
            }

            return client;
        }

        int container = Format.Kind == OutputSampleKind.Dop ? Math.Max(24, Format.ContainerBits) : Format.ContainerBits;
        int valid = Math.Min(Format.ValidBits, container);
        client.GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        long period = _options.BufferMilliseconds > 0 ? Math.Max(minimumPeriod, _options.BufferMilliseconds * 10_000L) : defaultPeriod;
        (int Container, int Valid)[] attempts = valid == container
            ? [(container, valid)]
            : [(container, valid), (container, container)];

        foreach ((int attemptContainer, int attemptValid) in attempts)
        {
            WaveFormatExtensibleNative format = WaveFormatExtensibleNative.Create(Format.SampleRate, Format.Channels, attemptContainer, attemptValid, isFloat: false);
            int hr = client.Initialize(AudioClientShareMode.Exclusive, WasapiConstants.StreamFlagsEventCallback, period, period, (IntPtr)(&format), IntPtr.Zero);
            if (hr == WasapiConstants.ErrorBufferSizeNotAligned)
            {
                client.GetBufferSize(out uint aligned);
                period = (long)Math.Round(HundredNanosecondsPerSecond * aligned / Format.SampleRate);
                WasapiBackend.Release(client);
                client = WasapiBackend.ActivateClient(device);
                hr = client.Initialize(AudioClientShareMode.Exclusive, WasapiConstants.StreamFlagsEventCallback, period, period, (IntPtr)(&format), IntPtr.Zero);
            }

            if (hr == WasapiConstants.SOk)
            {
                _float = false;
                _deviceContainerBits = attemptContainer;
                return client;
            }

            WasapiBackend.Release(client);
            if (hr != WasapiConstants.ErrorUnsupportedFormat)
            {
                throw new InvalidOperationException(WasapiConstants.Describe(hr));
            }

            // A failed Initialize leaves the client unusable; activate a fresh one for the next attempt.
            client = WasapiBackend.ActivateClient(device);
        }

        WasapiBackend.Release(client);
        throw new InvalidOperationException($"The device does not accept {Format.Describe()} in exclusive mode.");
    }

    private void FillBuffer(IAudioRenderClient render, int frames, byte[] canonical, int deviceBytesPerFrame)
    {
        Marshal.ThrowExceptionForHR(render.GetBuffer((uint)frames, out IntPtr data));
        Span<byte> source = canonical.AsSpan(0, frames * Format.CanonicalBytesPerFrame);
        _source.Render(source, frames, realtime: true);
        var destination = new Span<byte>((void*)data, frames * deviceBytesPerFrame);
        if (_float)
        {
            CanonicalSamples.ToFloat32(source, destination);
        }
        else
        {
            CanonicalSamples.ToInteger(source, destination, _deviceContainerBits);
        }

        Marshal.ThrowExceptionForHR(render.ReleaseBuffer((uint)frames, 0));
    }
}
