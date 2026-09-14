using FUPlayer.Core.Audio;

namespace FUPlayer.Core.Output;

/// <summary>What the device receives.</summary>
public enum OutputSampleKind
{
    /// <summary>Integer PCM.</summary>
    Pcm,

    /// <summary>Native 1-bit DSD (for example ASIO DSD mode).</summary>
    NativeDsd,

    /// <summary>DSD packed into 24-bit PCM frames (DoP v1.1).</summary>
    Dop,
}

/// <summary>
/// Format negotiated with the device. The engine hands back-ends a canonical byte stream:
/// PCM and DoP as little-endian 32-bit integers left-justified, native DSD as one byte (8 bits, MSB first) per channel.
/// </summary>
public sealed record OutputFormat(OutputSampleKind Kind, int SampleRate, int Channels, int ValidBits, int ContainerBits)
{
    public static OutputFormat Pcm(int sampleRate, int channels, int validBits, int containerBits) =>
        new(OutputSampleKind.Pcm, sampleRate, channels, validBits, containerBits);

    public static OutputFormat NativeDsd(int dsdRate, int channels) =>
        new(OutputSampleKind.NativeDsd, dsdRate, channels, 1, 8);

    /// <summary>DoP carrier: PCM at 1/16 of the DSD rate with 24 valid bits.</summary>
    public static OutputFormat Dop(int dsdRate, int channels, int containerBits = 24) =>
        new(OutputSampleKind.Dop, dsdRate / 16, channels, 24, containerBits);

    public bool IsDsd => Kind != OutputSampleKind.Pcm;

    /// <summary>1-bit rate for DSD outputs, 0 for PCM.</summary>
    public int DsdRate => Kind switch
    {
        OutputSampleKind.NativeDsd => SampleRate,
        OutputSampleKind.Dop => SampleRate * 16,
        _ => 0,
    };

    /// <summary>Canonical frames per second (native DSD: bytes per channel per second).</summary>
    public int CanonicalFrameRate => Kind == OutputSampleKind.NativeDsd ? SampleRate / 8 : SampleRate;

    /// <summary>Bytes per canonical frame.</summary>
    public int CanonicalBytesPerFrame => Kind == OutputSampleKind.NativeDsd ? Channels : 4 * Channels;

    public string Describe() => Kind switch
    {
        OutputSampleKind.NativeDsd => $"DSD{AudioRates.DsdMultiplier(SampleRate)} native · {AudioRates.Format(SampleRate)} · {Channels} ch",
        OutputSampleKind.Dop => $"DSD{AudioRates.DsdMultiplier(DsdRate)} DoP · {AudioRates.Format(SampleRate)} carrier · {Channels} ch",
        _ => $"PCM · {AudioRates.Format(SampleRate)} · {ValidBits}-bit · {Channels} ch",
    };

    public string DescribeShort() => Kind switch
    {
        OutputSampleKind.NativeDsd => $"DSD{AudioRates.DsdMultiplier(SampleRate)}",
        OutputSampleKind.Dop => $"DSD{AudioRates.DsdMultiplier(DsdRate)} (DoP)",
        _ => $"{AudioRates.FormatShort(SampleRate)}/{ValidBits}",
    };

    public override string ToString() => Describe();
}

/// <summary>An output endpoint of a back-end.</summary>
public sealed record AudioDevice(string BackendId, string Id, string Name, bool IsDefault = false);

/// <summary>What a device accepts.</summary>
public sealed class DeviceCapabilities
{
    public int MaxChannels { get; init; } = 2;

    /// <summary>Accepted PCM rates, ascending.</summary>
    public IReadOnlyList<int> PcmRates { get; init; } = [];

    /// <summary>Accepted integer container sizes (16, 24, 32).</summary>
    public IReadOnlyList<int> ContainerBits { get; init; } = [];

    /// <summary>Accepted native DSD bit rates, ascending.</summary>
    public IReadOnlyList<int> NativeDsdRates { get; init; } = [];

    /// <summary>
    /// False when samples pass through a conversion that may not keep 24-bit values exact (for example a driver that
    /// takes floating-point samples), which would corrupt DoP markers.
    /// </summary>
    public bool CarriesDop { get; init; } = true;

    /// <summary>Extra information for the user (driver limitations, probing notes).</summary>
    public string? Notes { get; init; }

    public bool SupportsDop(int dsdRate) =>
        CarriesDop && PcmRates.Contains(dsdRate / 16) && ContainerBits.Any(b => b >= 24);

    /// <summary>Everything, for back-ends that cannot be probed (file output, test sink).</summary>
    public static DeviceCapabilities Unrestricted(int maxChannels = 8) => new()
    {
        MaxChannels = maxChannels,
        PcmRates = AudioRates.StandardPcmRates,
        ContainerBits = [16, 24, 32],
        NativeDsdRates = AudioRates.DsdMultipliers.SelectMany(m => new[] { m * 44_100, m * 48_000 }).Order().ToArray(),
    };
}

/// <summary>Stream options chosen by the user.</summary>
/// <param name="BufferMilliseconds">Device buffer length, 0 for the driver default.</param>
/// <param name="ChannelOffset">First device channel (ASIO).</param>
/// <param name="WindowHandle">Native window handle for drivers that need one (ASIO), or zero.</param>
public sealed record AudioStreamOptions(int BufferMilliseconds, int ChannelOffset, IntPtr WindowHandle);

/// <summary>Supplies canonical output data to a device stream.</summary>
public interface IAudioRenderSource
{
    /// <summary>
    /// Fills <paramref name="destination"/> with up to <paramref name="frames"/> canonical frames.
    /// Real-time callers always get a full buffer (silence on underflow) and the return value equals <paramref name="frames"/>;
    /// non-real-time callers get only the frames that are ready.
    /// </summary>
    int Render(Span<byte> destination, int frames, bool realtime);

    /// <summary>True when playback has ended and every buffered frame has been rendered.</summary>
    bool IsDrained { get; }
}

/// <summary>An open device stream.</summary>
public interface IAudioStream : IDisposable
{
    OutputFormat Format { get; }

    /// <summary>Canonical frames the device requests per period.</summary>
    int BufferFrames { get; }

    /// <summary>Delay between rendering and sound leaving the device.</summary>
    double LatencySeconds { get; }

    /// <summary>False for sinks that consume data as fast as it is produced (file output).</summary>
    bool IsRealtime { get; }

    void Start();

    void Stop();

    /// <summary>Raised from the device thread when the stream fails (device removed, driver reset …).</summary>
    event EventHandler<Exception>? Failed;
}

/// <summary>An audio output technology (WASAPI, ASIO, file …).</summary>
public interface IAudioBackend
{
    string Id { get; }

    string DisplayName { get; }

    string Description { get; }

    bool IsAvailable { get; }

    bool SupportsNativeDsd { get; }

    /// <summary>False for mixers that may alter the signal (shared mode).</summary>
    bool IsBitPerfect { get; }

    bool HasControlPanel { get; }

    IReadOnlyList<AudioDevice> GetDevices();

    DeviceCapabilities GetCapabilities(string? deviceId, int channels);

    IAudioStream OpenStream(string? deviceId, OutputFormat format, AudioStreamOptions options, IAudioRenderSource source);

    void ShowControlPanel(string? deviceId);
}

/// <summary>Set of available back-ends.</summary>
public sealed class AudioBackendRegistry
{
    private readonly List<IAudioBackend> _backends;

    public AudioBackendRegistry(IEnumerable<IAudioBackend> backends)
    {
        _backends = backends.ToList();
    }

    public IReadOnlyList<IAudioBackend> Backends => _backends;

    public IAudioBackend? Find(string? id) => _backends.FirstOrDefault(b => b.Id == id);

    /// <summary>The requested back-end if usable, otherwise the first available one.</summary>
    public IAudioBackend Resolve(string? id) =>
        Find(id) is { IsAvailable: true } requested ? requested : _backends.First(b => b.IsAvailable);
}
