using System.Text.Json.Serialization;
using FUPlayer.Core.Dsp.Analysis;
using FUPlayer.Core.Dsp.Dsd;
using FUPlayer.Core.Dsp.Modulation;
using FUPlayer.Core.Dsp.Quantization;
using FUPlayer.Core.Dsp.Resampling;

namespace FUPlayer.Core.Settings;

/// <summary>What the player sends to the DAC.</summary>
public enum OutputMode
{
    /// <summary>Everything is played as PCM.</summary>
    Pcm,

    /// <summary>Everything is delta-sigma modulated to DSD.</summary>
    Dsd,

    /// <summary>PCM files as PCM, DSD files as DSD.</summary>
    FollowSource,
}

/// <summary>How the output sample rate is chosen.</summary>
public enum RateSelection
{
    /// <summary>Highest usable rate, preferring the source's rate family.</summary>
    Automatic,

    /// <summary>Only integer multiples of the source's rate family.</summary>
    SameFamily,

    /// <summary>Always the configured rate.</summary>
    Fixed,
}

public enum DsdTransport
{
    /// <summary>Native DSD where the back-end supports it, DoP otherwise.</summary>
    Native,

    /// <summary>Always DoP.</summary>
    Dop,
}

public enum RepeatMode
{
    Off,
    All,
    One,
}

public enum ReplayGainMode
{
    Off,
    Track,
    Album,
}

public enum TimeDisplayMode
{
    Elapsed,
    Remaining,
    QueueRemaining,
}

public enum AnalyzerView
{
    Spectrum,
    Spectrogram,
    Waterfall,
}

public enum AnalyzerSignal
{
    /// <summary>The decoded source before any processing.</summary>
    Source,

    /// <summary>The processed PCM stream just before dither or modulation.</summary>
    Output,
}

public enum AnalyzerChannels
{
    /// <summary>All channels mixed to one trace.</summary>
    Mono,

    /// <summary>Left and right as two traces.</summary>
    Stereo,

    /// <summary>Every channel as its own trace.</summary>
    All,

    /// <summary>One chosen channel.</summary>
    Single,
}

public enum FrequencyScale
{
    Logarithmic,
    Linear,
}

public enum AnalyzerAveraging
{
    None,
    Light,
    Heavy,
}

/// <summary>Whether the spectrum keeps a second line at the highest level each bin has reached.</summary>
public enum AnalyzerPeakHold
{
    None,

    /// <summary>Holds a peak briefly, then lets it fall, so the line follows the music.</summary>
    Falling,

    /// <summary>Keeps every peak until the display is cleared: catches one-off transients.</summary>
    Infinite,
}

/// <summary>All persisted player settings.</summary>
public sealed class PlayerSettings
{
    /// <summary>Settings layout version; older files are upgraded when loaded.</summary>
    public const int CurrentVersion = 3;

    public int Version { get; set; } = CurrentVersion;

    public OutputSettings Output { get; set; } = new();

    public PcmSettings Pcm { get; set; } = new();

    public DsdOutputSettings Dsd { get; set; } = new();

    public DsdToPcmSettings DsdToPcm { get; set; } = new();

    public VolumeSettings Volume { get; set; } = new();

    public ProcessingSettings Processing { get; set; } = new();

    public SpeakerSettings Speakers { get; set; } = new();

    public PlaybackSettings Playback { get; set; } = new();

    public LibrarySettings Library { get; set; } = new();

    public AnalyzerSettings Analyzer { get; set; } = new();

    public UiSettings Ui { get; set; } = new();

    public PlayerSettings Clone() => SettingsStore.Clone(this);
}

public sealed class OutputSettings
{
    public string BackendId { get; set; } = "wasapi-exclusive";

    public string? DeviceId { get; set; }

    public int Channels { get; set; } = 2;

    /// <summary>First device channel used (multichannel interfaces).</summary>
    public int FirstChannel { get; set; }

    public OutputMode Mode { get; set; } = OutputMode.Pcm;

    public DsdTransport DsdTransport { get; set; } = DsdTransport.Native;

    /// <summary>Use DSD rates based on 48 kHz for sources of the 48 kHz family.</summary>
    public bool Use48kDsdRates { get; set; }

    /// <summary>Device buffer in milliseconds; 0 uses the driver default.</summary>
    public int BufferMilliseconds { get; set; }

    /// <summary>Halves the engine FIFO for faster response at a higher dropout risk.</summary>
    public bool LowLatencyFifo { get; set; }

    /// <summary>Pause switches straight to silence instead of fading out.</summary>
    public bool InstantPause { get; set; }

    public string FileOutputDirectory { get; set; } = string.Empty;
}

public sealed class PcmSettings
{
    /// <summary>Resampling filter for PCM output.</summary>
    public string Filter { get; set; } = FilterCatalog.DefaultId;

    /// <summary>Filter length in taps at the output rate; 0 lets the filter's own specification decide.</summary>
    public int FilterTaps { get; set; }

    public StagingMode Staging { get; set; } = StagingMode.SingleStage;

    public RateSelection RateSelection { get; set; } = RateSelection.Automatic;

    /// <summary>Highest output rate to use; 0 means the device maximum.</summary>
    public int RateLimit { get; set; }

    public int FixedRate { get; set; } = 352_800;

    public string DitherId { get; set; } = DitherCatalog.AutoId;

    /// <summary>Resolution of the DAC input (valid bits sent to the device).</summary>
    public int DacBits { get; set; } = 24;
}

public sealed class DsdOutputSettings
{
    /// <summary>Resampling filter that feeds the modulator.</summary>
    public string Filter { get; set; } = FilterCatalog.DefaultId;

    /// <summary>Filter length in taps at the modulator rate; 0 lets the filter's own specification decide.</summary>
    public int FilterTaps { get; set; }

    public StagingMode Staging { get; set; } = StagingMode.SingleStage;

    public string ModulatorId { get; set; } = ModulatorCatalog.DefaultId;

    /// <summary>Highest DSD multiple (64, 128, 256, 512, 1024).</summary>
    public int HighestMultiplier { get; set; } = 256;

    public RateSelection RateSelection { get; set; } = RateSelection.Automatic;

    /// <summary>Send DSD files unchanged when the output rate matches.</summary>
    public bool PassThrough { get; set; }

    /// <summary>Low-pass applied to DSD files before they are modulated again.</summary>
    public string InputFilterId { get; set; } = DsdFilterCatalog.WidebandId;
}

public sealed class DsdToPcmSettings
{
    public string FilterId { get; set; } = DsdFilterCatalog.DefaultId;

    /// <summary>Add back the 6 dB of headroom DSD recordings carry when converting them to PCM.</summary>
    public bool RestoreLevel { get; set; } = true;
}

public sealed class VolumeSettings
{
    public double MinimumDb { get; set; } = -60.0;

    public double MaximumDb { get; set; }

    public double VolumeDb { get; set; } = -3.0;

    /// <summary>Extra gain for PCM output so it matches the DAC's DSD playback level.</summary>
    public double PcmLevelOffsetDb { get; set; }

    /// <summary>Volume control is bypassed when minimum and maximum are both 0 dB.</summary>
    [JsonIgnore]
    public bool IsBypassed => MinimumDb == 0.0 && MaximumDb == 0.0;
}

public sealed class ProcessingSettings
{
    /// <summary>Extra DSP threads; −1 selects automatically.</summary>
    public int DspThreads { get; set; } = -1;

    /// <summary>Low-pass high-rate PCM files at 20 kHz before conversion.</summary>
    public bool RemoveUltrasonics { get; set; }

    /// <summary>How filter arithmetic is carried out; both settings convolve the same coefficients.</summary>
    public ConvolutionMode Convolution { get; set; } = ConvolutionMode.Automatic;

    /// <summary>
    /// Taps per phase from which frequency-domain convolution takes over from tap by tap; 0 uses the built-in
    /// crossover of 1,024. Any number is allowed. Lowering it moves shorter filters onto the frequency domain,
    /// which costs less arithmetic and adds a block of delay; raising it keeps longer ones tap by tap, which
    /// adds no delay at all and costs one multiply-add per tap per output sample.
    /// </summary>
    public int ConvolutionThresholdTaps { get; set; }

    /// <summary>
    /// Longest a frequency-domain stage may hold input back, in milliseconds; 0 lets it choose. This is delay
    /// the filter does not need. The samples that come out are the same either way, so shortening it has no
    /// effect on the sound and costs arithmetic instead.
    /// </summary>
    public double ConvolutionMaxBlockMs { get; set; }

    /// <summary>
    /// Convolve every tap in blocks of one length: uniformly partitioned overlap-save, one FFT window size for
    /// the whole filter. On by default, because it is the only layout a graphics device takes. Turning it off
    /// allows non-uniform partitioning, which puts the early taps in short blocks and later ones in longer
    /// blocks: far less arithmetic for the same wait, and no device.
    /// </summary>
    public bool ConvolutionUniformBlocks { get; set; } = true;

    /// <summary>Source meters show the signal after ReplayGain and the ultrasonic low-pass.</summary>
    public bool MeterAdjustedSource { get; set; } = true;

    /// <summary>
    /// Catch peaks that rise above full scale after filtering with a look-ahead limiter. Turning it off leaves the
    /// signal untouched, so intersample peaks clip in the DAC (PCM) or push the modulator harder (DSD).
    /// </summary>
    public bool Limiter { get; set; } = true;

    public bool ApodizationDetection { get; set; } = true;

    /// <summary>
    /// Convolve long filters on an OpenCL device instead of the processor. Off by default: it only pays for
    /// filters of tens of thousands of taps, and the device has to reproduce the processor's own output first.
    /// </summary>
    public bool GpuAcceleration { get; set; }

    /// <summary>Chosen OpenCL device, or empty to pick the most capable one automatically.</summary>
    public string GpuDeviceId { get; set; } = string.Empty;

    /// <summary>
    /// Share of a stage's polyphase phases the device convolves, as a percentage of the phases every channel has
    /// between them; −1, the default, times every division of them and keeps whichever finished soonest.
    /// </summary>
    /// <remarks>
    /// The processor convolves the rest of the same blocks at the same time, so this is the balance point of the
    /// two. A fixed share is worth having where a machine's own measurements cannot be trusted, such as a
    /// laptop whose clocks move with its temperature, and for reproducing one machine's division on another.
    /// </remarks>
    public int GpuPhaseShare { get; set; } = -1;

    /// <summary>
    /// Go back over the divisions every few hundred blocks while the stream plays, rather than settling on the
    /// first sweep's answer for good. Only applies while the share is being measured.
    /// </summary>
    public bool GpuRetimeWhilePlaying { get; set; } = true;

    /// <summary>
    /// Keep the device's arithmetic at 64 bits, as the processor's is. Consumer cards run 64-bit maths at a
    /// small fraction of their 32-bit rate, so turning this off is what makes most of them worth using.
    /// </summary>
    public bool GpuHighPrecision { get; set; } = true;

    /// <summary>
    /// Give the device the filter even when the processor measured faster at it. The check that it reproduces
    /// the processor's own output still has to pass; only the timed run is overruled.
    /// </summary>
    public bool GpuForce { get; set; }

    /// <summary>Engine FIFO length in milliseconds.</summary>
    /// <summary>
    /// Audio buffered ahead of the device, in milliseconds. It is a floor rather than the whole story: the engine
    /// takes twice the device buffer and whatever a block-based filter needs to ride out a block if either is
    /// longer, and caps the ring at 256 MB however long the wait would be.
    /// </summary>
    public int FifoMilliseconds { get; set; } = 250;
}

public sealed class SpeakerTrim
{
    public string Name { get; set; } = string.Empty;

    public double LevelDb { get; set; }

    public double DistanceCm { get; set; } = 200.0;
}

public sealed class SpeakerSettings
{
    public static readonly string[] ChannelNames = ["Front left", "Front right", "Center", "LFE", "Back left", "Back right", "Side left", "Side right"];

    public bool Enabled { get; set; }

    public List<SpeakerTrim> Channels { get; set; } = ChannelNames.Select(n => new SpeakerTrim { Name = n }).ToList();
}

public sealed class PlaybackSettings
{
    public RepeatMode Repeat { get; set; } = RepeatMode.Off;

    public bool Shuffle { get; set; }

    public ReplayGainMode ReplayGain { get; set; } = ReplayGainMode.Off;

    /// <summary>Limit positive ReplayGain so the tagged peak does not clip.</summary>
    public bool PreventReplayGainClipping { get; set; } = true;

    public bool InvertPolarity { get; set; }

    public bool Gapless { get; set; } = true;

    public TimeDisplayMode TimeDisplay { get; set; } = TimeDisplayMode.Elapsed;
}

public sealed class LibrarySettings
{
    public List<string> Folders { get; set; } = [];

    /// <summary>Pick up files added, changed or removed under the library folders without being asked to.</summary>
    public bool WatchFolders { get; set; } = true;

    /// <summary>How the Library page groups what it shows.</summary>
    public LibraryBrowse Browse { get; set; } = LibraryBrowse.Albums;
}

/// <summary>What the Library page lists down its side.</summary>
public enum LibraryBrowse
{
    Albums,
    Artists,
    Genres,
}

/// <summary>Spectrum, spectrogram and waterfall display options.</summary>
public sealed class AnalyzerSettings
{
    public AnalyzerView View { get; set; } = AnalyzerView.Spectrum;

    public AnalyzerSignal Signal { get; set; } = AnalyzerSignal.Source;

    public AnalyzerChannels Channels { get; set; } = AnalyzerChannels.Stereo;

    /// <summary>Channel shown when <see cref="Channels"/> is <see cref="AnalyzerChannels.Single"/>.</summary>
    public int ChannelIndex { get; set; }

    public SpectrumWindow Window { get; set; } = SpectrumWindow.BlackmanHarris;

    public int FftSize { get; set; } = 8192;

    /// <summary>Upper edge of the displayed band in Hz; 0 shows everything up to the signal's Nyquist frequency.</summary>
    public double BandHz { get; set; }

    public FrequencyScale Scale { get; set; } = FrequencyScale.Logarithmic;

    /// <summary>Lowest level on the display, in dBFS.</summary>
    public double FloorDb { get; set; } = -140.0;

    public AnalyzerAveraging Averaging { get; set; } = AnalyzerAveraging.Light;

    /// <summary>Second spectrum line marking the highest level each bin has reached.</summary>
    public AnalyzerPeakHold PeakHold { get; set; } = AnalyzerPeakHold.None;
}

public sealed class UiSettings
{
    public string Theme { get; set; } = "Dark";

    public double WindowWidth { get; set; } = 1440;

    public double WindowHeight { get; set; } = 900;

    public string LastPage { get; set; } = "NowPlaying";
}
