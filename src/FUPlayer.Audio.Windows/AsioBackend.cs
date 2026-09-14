using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using FUPlayer.Audio.Windows.Interop;
using FUPlayer.Core.Audio;
using FUPlayer.Core.Dsp.Dsd;
using FUPlayer.Core.Output;

namespace FUPlayer.Audio.Windows;

/// <summary>ASIO output with PCM, DoP and native DSD (ASIO 2.2 DSD I/O format) support.</summary>
public sealed class AsioBackend : IAudioBackend
{
    public const string BackendId = "asio";

    private static readonly ConcurrentDictionary<Guid, DeviceCapabilities> CapabilityCache = new();

    public string Id => BackendId;

    public string DisplayName => "ASIO";

    public string Description => "Professional low-latency drivers. Native DSD where the driver supports ASIO DSD mode, DoP otherwise.";

    public bool IsAvailable => Environment.Is64BitProcess && AsioDriver.EnumerateInstalled().Count > 0;

    public bool SupportsNativeDsd => true;

    public bool IsBitPerfect => true;

    public bool HasControlPanel => true;

    public IReadOnlyList<AudioDevice> GetDevices() =>
        AsioDriver.EnumerateInstalled()
            .Select((driver, index) => new AudioDevice(Id, driver.ClassId.ToString("B"), driver.Name, index == 0))
            .ToList();

    public DeviceCapabilities GetCapabilities(string? deviceId, int channels)
    {
        AsioDriverEntry? entry = Find(deviceId);
        if (entry is null)
        {
            return new DeviceCapabilities { MaxChannels = 2, Notes = "No ASIO driver is installed." };
        }

        if (AsioStream.ActiveDriver == entry.ClassId)
        {
            // Loading a second instance of a driver that is streaming can disturb it: reuse the last probe.
            return CapabilityCache.TryGetValue(entry.ClassId, out DeviceCapabilities? cached)
                ? cached
                : new DeviceCapabilities { MaxChannels = channels, Notes = "The driver is currently streaming." };
        }

        try
        {
            DeviceCapabilities capabilities = AsioThread.Shared.Invoke(() =>
            {
                try
                {
                    return Probe(entry);
                }
                finally
                {
                    // The probe loads and initialises the driver; let its DLL go again right away.
                    NativeMethods.CoFreeUnusedLibrariesEx(0, 0);
                }
            });
            CapabilityCache[entry.ClassId] = capabilities;
            return capabilities;
        }
        catch (Exception ex) when (ex is COMException or PlatformNotSupportedException or InvalidOperationException)
        {
            return new DeviceCapabilities { MaxChannels = 2, Notes = $"The ASIO driver could not be queried: {ex.Message}" };
        }
    }

    public IAudioStream OpenStream(string? deviceId, OutputFormat format, AudioStreamOptions options, IAudioRenderSource source)
    {
        AsioDriverEntry entry = Find(deviceId) ?? throw new InvalidOperationException("No ASIO driver is installed.");
        return new AsioStream(entry, format, options, source);
    }

    public void ShowControlPanel(string? deviceId)
    {
        AsioDriverEntry? entry = Find(deviceId);
        if (entry is null)
        {
            return;
        }

        AsioThread.Shared.Invoke(() =>
        {
            if (AsioStream.ActiveDriver == entry.ClassId)
            {
                AsioStream.ShowActiveControlPanel();
                return;
            }

            using (AsioDriver driver = AsioDriver.Load(entry.ClassId, entry.Name))
            {
                if (driver.Init(IntPtr.Zero))
                {
                    driver.ControlPanel();
                }
            }

            NativeMethods.CoFreeUnusedLibrariesEx(0, 0);
        });
    }

    internal static int[] ContainerBitsFor(AsioSampleType type) => type switch
    {
        AsioSampleType.Int16Lsb or AsioSampleType.Int16Msb or AsioSampleType.Int32Lsb16 or AsioSampleType.Int32Msb16 => [16],
        AsioSampleType.Int24Lsb or AsioSampleType.Int24Msb or AsioSampleType.Int32Lsb18 or AsioSampleType.Int32Lsb20
            or AsioSampleType.Int32Lsb24 or AsioSampleType.Int32Msb18 or AsioSampleType.Int32Msb20 or AsioSampleType.Int32Msb24 => [16, 24],
        _ => [16, 24, 32],
    };

    internal static bool IsFloat(AsioSampleType type) =>
        type is AsioSampleType.Float32Lsb or AsioSampleType.Float64Lsb or AsioSampleType.Float32Msb or AsioSampleType.Float64Msb;

    /// <summary>PCM sample types the stream can write.</summary>
    internal static bool IsWritablePcm(AsioSampleType type) => type is AsioSampleType.Int32Lsb or AsioSampleType.Int24Lsb or AsioSampleType.Int16Lsb
        or AsioSampleType.Float32Lsb or AsioSampleType.Float64Lsb or AsioSampleType.Int32Lsb16 or AsioSampleType.Int32Lsb18
        or AsioSampleType.Int32Lsb20 or AsioSampleType.Int32Lsb24 or AsioSampleType.Int32Msb or AsioSampleType.Int24Msb or AsioSampleType.Int16Msb;

    private static string? ProbeNotes(bool nativeDsd, bool floatSamples) => (nativeDsd, floatSamples) switch
    {
        (false, false) => "The driver does not offer native DSD; DSD output uses DoP.",
        (false, true) => "The driver takes floating-point samples and has no native DSD mode, so DSD output is not possible (DoP needs exact 24-bit samples). PCM output works.",
        (true, true) => "The driver takes floating-point samples, so DSD always uses its native DSD mode rather than DoP.",
        _ => null,
    };

    private static DeviceCapabilities Probe(AsioDriverEntry entry)
    {
        AsioTrace.Write($"probe {entry.Name}");
        using AsioDriver driver = AsioDriver.Load(entry.ClassId, entry.Name);
        if (!driver.Init(IntPtr.Zero))
        {
            return new DeviceCapabilities { MaxChannels = 2, Notes = $"The driver did not initialise: {driver.GetErrorMessage()}" };
        }

        driver.GetChannels(out _, out int outputs);
        if (outputs <= 0)
        {
            return new DeviceCapabilities
            {
                MaxChannels = 2,
                Notes = "The driver reports no output channels. Is the interface connected and switched on?",
            };
        }

        driver.TrySetIoFormat(AsioConstants.IoFormatPcm);
        int[] rates = AudioRates.StandardPcmRates.Where(r => AsioConstants.Succeeded(driver.CanSampleRate(r))).ToArray();
        var info = new AsioChannelInfo { Channel = 0, IsInput = 0 };
        driver.GetChannelInfo(ref info);

        int[] dsdRates = [];
        if (driver.CanDoIoFormat(AsioConstants.IoFormatDsd) && driver.TrySetIoFormat(AsioConstants.IoFormatDsd))
        {
            dsdRates = AudioRates.DsdMultipliers
                .SelectMany(m => new[] { m * 44_100, m * 48_000 })
                .Where(r => AsioConstants.Succeeded(driver.CanSampleRate(r)))
                .Order()
                .ToArray();
            driver.TrySetIoFormat(AsioConstants.IoFormatPcm);
        }

        bool floatSamples = IsFloat(info.Type);
        return new DeviceCapabilities
        {
            MaxChannels = Math.Max(1, outputs),
            PcmRates = rates,
            ContainerBits = ContainerBitsFor(info.Type),
            CarriesDop = !floatSamples,
            NativeDsdRates = dsdRates,
            Notes = ProbeNotes(dsdRates.Length > 0, floatSamples),
        };
    }

    private static AsioDriverEntry? Find(string? deviceId)
    {
        IReadOnlyList<AsioDriverEntry> drivers = AsioDriver.EnumerateInstalled();
        if (!string.IsNullOrEmpty(deviceId) && Guid.TryParse(deviceId, out Guid classId))
        {
            AsioDriverEntry? match = drivers.FirstOrDefault(d => d.ClassId == classId);
            if (match is not null)
            {
                return match;
            }
        }

        return drivers.FirstOrDefault();
    }
}

/// <summary>Single-threaded apartment thread that owns ASIO drivers and pumps their window messages.</summary>
internal sealed class AsioThread
{
    private static readonly Lazy<AsioThread> Instance = new(() => new AsioThread());

    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;

    private AsioThread()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "FUPLAYER ASIO host" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public static AsioThread Shared => Instance.Value;

    public T Invoke<T>(Func<T> function)
    {
        if (Thread.CurrentThread == _thread)
        {
            return function();
        }

        T result = default!;
        Exception? error = null;
        using var done = new ManualResetEventSlim(false);
        _queue.Add(() =>
        {
            try
            {
                result = function();
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                done.Set();
            }
        });
        done.Wait();
        if (error is not null)
        {
            ExceptionDispatchInfo.Throw(error);
        }

        return result;
    }

    public void Invoke(Action action) => Invoke(() =>
    {
        action();
        return 0;
    });

    private void Run()
    {
        long nextUnload = 0;
        while (true)
        {
            // Every caller waits on this thread, so it must outlive anything a driver throws at it: were the loop
            // to end, the next Invoke would wait for a reply that could never come and hang the player silently.
            try
            {
                if (_queue.TryTake(out Action? action, 15))
                {
                    action();
                }

                NativeMethods.PumpMessages();

                // Some drivers keep the hardware claimed until their DLL leaves the process. COM unloads a driver
                // DLL only once it reports no objects are alive and it has stayed unused for the given delay.
                long now = Environment.TickCount64;
                if (now >= nextUnload)
                {
                    NativeMethods.CoFreeUnusedLibrariesEx(1000, 0);
                    nextUnload = now + 1000;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AsioTrace.Write($"host thread recovered from {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}

/// <summary>An open ASIO stream. ASIO callbacks carry no context, so only one stream can be active per process.</summary>
internal sealed unsafe class AsioStream : IAudioStream
{
    private static AsioStream? _active;

    private readonly AsioDriverEntry _entry;
    private readonly AudioStreamOptions _options;
    private readonly IAudioRenderSource _source;
    private readonly int _channels;
    private AsioDriver? _driver;
    private AsioBufferInfo* _buffers;
    private AsioCallbacks* _callbacks;
    private double _previousSampleRate;
    private bool _buffersCreated;
    private int _driverBufferSize;
    private AsioSampleType _sampleType;
    private bool _outputReady;
    private byte[] _canonical = [];
    private bool _running;
    private volatile bool _failed;

    public AsioStream(AsioDriverEntry entry, OutputFormat format, AudioStreamOptions options, IAudioRenderSource source)
    {
        _entry = entry;
        _options = options;
        _source = source;
        _channels = format.Channels;
        Format = format;
        AsioThread.Shared.Invoke(Open);
    }

    public event EventHandler<Exception>? Failed;

    public static Guid? ActiveDriver => _active?._entry.ClassId;

    public OutputFormat Format { get; }

    public int BufferFrames { get; private set; }

    public double LatencySeconds { get; private set; }

    public bool IsRealtime => true;

    public static void ShowActiveControlPanel() => _active?._driver?.ControlPanel();

    public void Start() => AsioThread.Shared.Invoke(() =>
    {
        if (!_running && _driver is not null)
        {
            AsioTrace.Write($"start {_entry.Name} ({Format.Describe()})");
            Check(_driver.Start(), "start streaming");
            _running = true;
            AsioTrace.Streaming(_entry.Name);
        }
    });

    public void Stop() => AsioThread.Shared.Invoke(() =>
    {
        if (_running && _driver is not null)
        {
            AsioError error = _driver.Stop();
            _running = false;
            AsioTrace.Streaming(null);
            AsioTrace.Write($"stop {_entry.Name} = {error}");
        }
    });

    public void Dispose() => AsioThread.Shared.Invoke(Cleanup);

    [UnmanagedCallersOnly]
    private static void OnBufferSwitch(int bufferIndex, int directProcess) => _active?.Render(bufferIndex);

    [UnmanagedCallersOnly]
    private static IntPtr OnBufferSwitchTimeInfo(IntPtr timeInfo, int bufferIndex, int directProcess)
    {
        _active?.Render(bufferIndex);
        return timeInfo;
    }

    [UnmanagedCallersOnly]
    private static void OnSampleRateDidChange(double rate) =>
        _active?.Fail(new InvalidOperationException($"The ASIO driver switched to {rate:0} Hz."));

    [UnmanagedCallersOnly]
    private static int OnAsioMessage(int selector, int value, IntPtr message, double* optional)
    {
        switch (selector)
        {
            case AsioConstants.MessageSelectorSupported:
                return value is AsioConstants.MessageEngineVersion or AsioConstants.MessageResetRequest
                    or AsioConstants.MessageResyncRequest or AsioConstants.MessageLatenciesChanged ? 1 : 0;
            case AsioConstants.MessageEngineVersion:
                return 2;
            case AsioConstants.MessageResetRequest:
                _active?.Fail(new InvalidOperationException("The ASIO driver requested a reset (for example after a settings change)."));
                return 1;
            case AsioConstants.MessageResyncRequest:
            case AsioConstants.MessageLatenciesChanged:
                return 1;
            default:
                return 0;
        }
    }

    private static void Check(AsioError error, string action)
    {
        if (!AsioConstants.Succeeded(error))
        {
            throw new InvalidOperationException($"The ASIO driver could not {action} ({error}).");
        }
    }

    private void Open()
    {
        if (_active is not null)
        {
            throw new InvalidOperationException("Another ASIO stream is already open.");
        }

        _driver = AsioDriver.Load(_entry.ClassId, _entry.Name);
        try
        {
            if (!_driver.Init(_options.WindowHandle))
            {
                throw new InvalidOperationException($"The ASIO driver did not initialise: {_driver.GetErrorMessage()}");
            }

            // Remember the rate the driver was running at, so the device is handed back the way it was found.
            _driver.GetSampleRate(out _previousSampleRate);
            AsioTrace.Write($"init {_entry.Name}: driver at {_previousSampleRate:0} Hz, opening {Format.Describe()}");

            _driver.GetChannels(out _, out int outputs);
            if (_options.ChannelOffset + _channels > outputs)
            {
                throw new InvalidOperationException($"The driver has {outputs} outputs; {_channels} channels starting at {_options.ChannelOffset} do not fit.");
            }

            bool dsd = Format.Kind == OutputSampleKind.NativeDsd;
            if (dsd)
            {
                if (!_driver.TrySetIoFormat(AsioConstants.IoFormatDsd))
                {
                    throw new NotSupportedException("The ASIO driver does not support native DSD; choose DoP.");
                }
            }
            else
            {
                _driver.TrySetIoFormat(AsioConstants.IoFormatPcm);
            }

            if (!AsioConstants.Succeeded(_driver.CanSampleRate(Format.SampleRate)))
            {
                throw new InvalidOperationException($"The ASIO driver cannot run at {AudioRates.Format(Format.SampleRate)}.");
            }

            AsioTrace.Write($"setSampleRate({Format.SampleRate})");
            Check(_driver.SetSampleRate(Format.SampleRate), "set the sample rate");
            var info = new AsioChannelInfo { Channel = _options.ChannelOffset, IsInput = 0 };
            Check(_driver.GetChannelInfo(ref info), "describe its output channels");
            _sampleType = info.Type;
            ValidateSampleType(dsd);

            Check(_driver.GetBufferSize(out int minimum, out int maximum, out int preferred, out int granularity), "report its buffer size");
            _driverBufferSize = ChooseBufferSize(minimum, maximum, preferred, granularity);
            BufferFrames = dsd ? DsdBytesPerBuffer(_driverBufferSize) : _driverBufferSize;
            _canonical = new byte[BufferFrames * Format.CanonicalBytesPerFrame];

            _buffers = (AsioBufferInfo*)NativeMemory.AllocZeroed((nuint)(_channels * sizeof(AsioBufferInfo)));
            for (int c = 0; c < _channels; c++)
            {
                _buffers[c].IsInput = 0;
                _buffers[c].ChannelNumber = _options.ChannelOffset + c;
            }

            _callbacks = (AsioCallbacks*)NativeMemory.AllocZeroed((nuint)sizeof(AsioCallbacks));
            _callbacks->BufferSwitch = &OnBufferSwitch;
            _callbacks->SampleRateDidChange = &OnSampleRateDidChange;
            _callbacks->AsioMessage = &OnAsioMessage;
            _callbacks->BufferSwitchTimeInfo = &OnBufferSwitchTimeInfo;

            _active = this;
            AsioTrace.Write($"createBuffers({_channels} ch, {_driverBufferSize} samples)");
            Check(_driver.CreateBuffers(_buffers, _channels, _driverBufferSize, _callbacks), "create its buffers");
            _buffersCreated = true;

            _driver.GetLatencies(out _, out int outputLatency);
            LatencySeconds = (double)(outputLatency > 0 ? outputLatency : _driverBufferSize) / Format.SampleRate;
            _outputReady = AsioConstants.Succeeded(_driver.OutputReady());
        }
        catch
        {
            Cleanup();
            throw;
        }
    }

    private void ValidateSampleType(bool dsd)
    {
        bool isDsdType = _sampleType is AsioSampleType.DsdInt8Lsb1 or AsioSampleType.DsdInt8Msb1 or AsioSampleType.DsdInt8Ner8;
        if (dsd != isDsdType)
        {
            throw new NotSupportedException($"The driver reports sample type {_sampleType} for this mode.");
        }

        if (_sampleType is AsioSampleType.DsdInt8Ner8)
        {
            throw new NotSupportedException("DSD Int8 NER8 buffers are not supported; choose DoP.");
        }

        if (!dsd && !AsioBackend.IsWritablePcm(_sampleType))
        {
            throw new NotSupportedException($"ASIO sample type {_sampleType} is not supported.");
        }

        if (Format.Kind == OutputSampleKind.Dop && AsioBackend.IsFloat(_sampleType))
        {
            throw new NotSupportedException("The driver takes floating-point samples, which cannot carry DoP reliably; use native DSD or PCM output.");
        }
    }

    private int ChooseBufferSize(int minimum, int maximum, int preferred, int granularity)
    {
        if (_options.BufferMilliseconds <= 0 || maximum <= 0)
        {
            return preferred;
        }

        int size = Math.Clamp((int)((long)Format.SampleRate * _options.BufferMilliseconds / 1000), minimum, maximum);
        if (granularity > 0)
        {
            size = minimum + (size - minimum) / granularity * granularity;
        }
        else if (granularity == -1)
        {
            size = (int)BitOperations.RoundUpToPowerOf2((uint)size);
        }

        return Math.Clamp(size, minimum, maximum);
    }

    /// <summary>
    /// ASIO buffer sizes are counted in samples; DSD Int8 LSB1/MSB1 buffers hold 8 samples per byte.
    /// Set FUPLAYER_ASIO_DSD_BUFFER_UNIT=bytes for drivers that report DSD buffer sizes in bytes.
    /// </summary>
    private static int DsdBytesPerBuffer(int bufferSize) =>
        string.Equals(Environment.GetEnvironmentVariable("FUPLAYER_ASIO_DSD_BUFFER_UNIT"), "bytes", StringComparison.OrdinalIgnoreCase)
            ? bufferSize
            : Math.Max(1, bufferSize / 8);

    private void Render(int bufferIndex)
    {
        if (_failed || _buffers == null)
        {
            return;
        }

        try
        {
            int frames = BufferFrames;
            Span<byte> canonical = _canonical.AsSpan(0, frames * Format.CanonicalBytesPerFrame);
            _source.Render(canonical, frames, realtime: true);
            for (int c = 0; c < _channels; c++)
            {
                byte* target = (byte*)(bufferIndex == 0 ? _buffers[c].Buffer0 : _buffers[c].Buffer1);
                if (target != null)
                {
                    WriteChannel(canonical, c, frames, target);
                }
            }

            if (_outputReady)
            {
                _driver?.OutputReady();
            }
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    private void WriteChannel(ReadOnlySpan<byte> canonical, int channel, int frames, byte* target)
    {
        int channels = _channels;
        if (Format.Kind == OutputSampleKind.NativeDsd)
        {
            bool reverse = _sampleType == AsioSampleType.DsdInt8Lsb1;
            for (int f = 0; f < frames; f++)
            {
                byte value = canonical[f * channels + channel];
                target[f] = reverse ? DsdConstants.Reverse(value) : value;
            }

            return;
        }

        fixed (byte* source = canonical)
        {
            for (int f = 0; f < frames; f++)
            {
                int sample = Unsafe.ReadUnaligned<int>(source + (f * channels + channel) * 4);
                switch (_sampleType)
                {
                    case AsioSampleType.Int32Lsb:
                        Unsafe.WriteUnaligned(target + f * 4, sample);
                        break;
                    case AsioSampleType.Int24Lsb:
                    {
                        byte* p = target + f * 3;
                        p[0] = (byte)(sample >> 8);
                        p[1] = (byte)(sample >> 16);
                        p[2] = (byte)(sample >> 24);
                        break;
                    }

                    case AsioSampleType.Int16Lsb:
                        Unsafe.WriteUnaligned(target + f * 2, (short)(sample >> 16));
                        break;
                    case AsioSampleType.Float32Lsb:
                        Unsafe.WriteUnaligned(target + f * 4, sample / 2147483648f);
                        break;
                    case AsioSampleType.Float64Lsb:
                        Unsafe.WriteUnaligned(target + f * 8, sample / 2147483648.0);
                        break;
                    case AsioSampleType.Int32Lsb16:
                        Unsafe.WriteUnaligned(target + f * 4, sample >> 16);
                        break;
                    case AsioSampleType.Int32Lsb18:
                        Unsafe.WriteUnaligned(target + f * 4, sample >> 14);
                        break;
                    case AsioSampleType.Int32Lsb20:
                        Unsafe.WriteUnaligned(target + f * 4, sample >> 12);
                        break;
                    case AsioSampleType.Int32Lsb24:
                        Unsafe.WriteUnaligned(target + f * 4, sample >> 8);
                        break;
                    case AsioSampleType.Int32Msb:
                        Unsafe.WriteUnaligned(target + f * 4, System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(sample));
                        break;
                    case AsioSampleType.Int24Msb:
                    {
                        byte* p = target + f * 3;
                        p[0] = (byte)(sample >> 24);
                        p[1] = (byte)(sample >> 16);
                        p[2] = (byte)(sample >> 8);
                        break;
                    }

                    case AsioSampleType.Int16Msb:
                        Unsafe.WriteUnaligned(target + f * 2, System.Buffers.Binary.BinaryPrimitives.ReverseEndianness((short)(sample >> 16)));
                        break;
                    default:
                        throw new NotSupportedException($"ASIO sample type {_sampleType} is not supported.");
                }
            }
        }
    }

    /// <summary>
    /// Leaves the driver at an ordinary PCM rate before it is released. Drivers keep the last rate they were given,
    /// so a DAC can stay locked to a DSD or 768 kHz clock and refuse other applications until its rate is reset.
    /// </summary>
    private void RestoreSampleRate(AsioDriver driver)
    {
        double wanted = _previousSampleRate;
        bool sameAsPlayback = Math.Abs(wanted - Format.SampleRate) < 1.0;
        if (wanted < 8_000 || wanted > 800_000 || sameAsPlayback)
        {
            // Unknown, a DSD rate, or the rate we set ourselves: go back to the base rate of the same family.
            wanted = AudioRates.Is44k1Family(Format.SampleRate) ? 44_100 : 48_000;
        }

        if (!AsioConstants.Succeeded(driver.CanSampleRate(wanted)))
        {
            wanted = 48_000;
        }

        AsioTrace.Write($"restore setSampleRate({wanted:0}) = {driver.SetSampleRate(wanted)}");
    }

    private void Fail(Exception exception)
    {
        if (_failed)
        {
            return;
        }

        _failed = true;
        ThreadPool.QueueUserWorkItem(_ => Failed?.Invoke(this, exception));
    }

    private void Cleanup()
    {
        AsioDriver? driver = _driver;
        _driver = null;
        try
        {
            if (driver is not null)
            {
                try
                {
                    if (_running)
                    {
                        AsioTrace.Write($"stop {_entry.Name} = {driver.Stop()}");
                    }

                    if (_buffersCreated)
                    {
                        AsioTrace.Write($"disposeBuffers = {driver.DisposeBuffers()}");
                    }

                    if (Format.Kind == OutputSampleKind.NativeDsd)
                    {
                        AsioTrace.Write($"setIoFormat(PCM) = {driver.TrySetIoFormat(AsioConstants.IoFormatPcm)}");
                    }

                    RestoreSampleRate(driver);
                }
                finally
                {
                    // Always release the driver, even if one of the calls above failed, so the device is let go.
                    _running = false;
                    _buffersCreated = false;
                    AsioTrace.Streaming(null);
                    driver.Dispose();

                    // Some drivers only hand the hardware back when their DLL leaves the process.
                    NativeMethods.CoFreeUnusedLibrariesEx(0, 0);
                    AsioTrace.Write("CoFreeUnusedLibrariesEx(0)");
                }
            }
        }
        finally
        {
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }

            if (_buffers != null)
            {
                NativeMemory.Free(_buffers);
                _buffers = null;
            }

            if (_callbacks != null)
            {
                NativeMemory.Free(_callbacks);
                _callbacks = null;
            }
        }
    }
}
