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

    public RestorationSettings Restoration { get; set; } = new();

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

/// <summary>
/// What to do about material a perceptual codec has been through. Every one of these invents signal
/// that was not in the source, so all of them are off unless asked for.
/// </summary>
public sealed class RestorationSettings
{
    /// <summary>
    /// The whole of lossy repair, on or off in one place. Off builds none of the stages below, whatever
    /// each of them says, and leaves their settings where they were so that turning this back on
    /// restores them exactly. On by default, because every stage is off by default anyway and an
    /// existing settings file has to keep meaning what it meant.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Run the neural upscaler: a network trained on high-resolution masters and on lossy and CD-rate
    /// copies of them, which repairs what a codec did below 22 kHz and writes the band above it, up to
    /// 44.1 or 48 kHz. Needs an output of at least twice the source rate. Off until asked for.
    /// </summary>
    public bool NeuralUpscaler { get; set; }

    /// <summary>The upscaler model file, or null for the one in the models folder.</summary>
    public string? NeuralUpscalerPath { get; set; }

    /// <summary>
    /// Gain on the band the upscaler writes above the source's Nyquist rate, in decibels. Zero is what the
    /// network was trained to write, which on held-out songs sat a couple of decibels under the masters.
    /// Nothing of the recording is up there, so this changes only the invented band.
    /// </summary>
    public double UpscalerBandDb { get; set; }

    /// <summary>Hold a high band steady when the encoder keeps switching it on and off.</summary>
    public bool ReduceArtifacts { get; set; }

    /// <summary>0 leaves the signal alone, 1 holds a lone bin almost still.</summary>
    public double ArtifactStrength { get; set; } = 0.5;

    /// <summary>Synthesise a band above the cutoff from the one below it.</summary>
    public bool RebuildHarmonics { get; set; }

    /// <summary>Trim on the rebuilt band.</summary>
    public double RebuildAmountDb { get; set; } = -3.0;

    /// <summary>Use a trained model for the rebuilt levels rather than a fixed slope.</summary>
    public bool Predict { get; set; }

    /// <summary>Model file, or null for the newest one in the models folder.</summary>
    public string? ModelPath { get; set; }

    /// <summary>Network file, or null for the newest one in the models folder.</summary>
    public string? NetworkPath { get; set; }

    /// <summary>
    /// How much of the network's correction to apply. 1 is what it was fitted to say; up to 3
    /// exaggerates it and is no longer an answer to the measurement.
    /// </summary>
    public double NetworkAmount { get; set; } = 1.0;

    /// <summary>
    /// Output what the repair added instead of the repaired signal: the difference between the two,
    /// which is silence wherever the repair decided to do nothing.
    ///
    /// A monitoring mode, not a listening one. It is the only way to hear exactly how much of what
    /// comes out was invented, and the honest answer at a high bit rate is "very little".
    /// </summary>
    public bool OutputDifference { get; set; }

    /// <summary>Where the rebuilt band starts. 0 measures it from the signal.</summary>
    public double ManualCutoffHz { get; set; }

    /// <summary>Nothing is rebuilt above this.</summary>
    public double CeilingHz { get; set; } = 22_000.0;

    /// <summary>
    /// Synthesise a band above the source's own Nyquist rate when the output runs faster than the
    /// file does: 22 to 40 kHz for a CD-rate recording played at 96 kHz or above.
    ///
    /// This is invention and not recovery, and the distinction is not pedantry. A 44.1 kHz recording
    /// never had that band: the converter that made it removed everything up there before the first
    /// sample existed. What a model fitted to real 96 kHz recordings can say is what usually sits
    /// there in music of this kind, which is between 30 and 60 dB under the midband and above the
    /// range of human hearing. Nobody will hear it directly. An amplifier or a tweeter asked to
    /// reproduce it may make something audible of it, which is the argument against, and is why this
    /// is off by default and trimmed well down when it is on.
    /// </summary>
    public bool RebuildUltrasonics { get; set; }

    /// <summary>Network for the band above the source's Nyquist rate, or null for the newest one.</summary>
    public string? UltrasonicPath { get; set; }

    /// <summary>Trim on that band, in decibels, on top of what the model asks for.</summary>
    public double UltrasonicTrimDb { get; set; } = -6.0;
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
    /// Catch peaks that rise above full scale after filtering with a look-ahead limiter. Turning it off leaves a
    /// lossless signal untouched, so intersample peaks clip in the DAC (PCM) or push the modulator harder (DSD).
    /// A lossy source is limited whatever this says: its overs are codec artefacts, and unlimited they clip into
    /// noise above 40 kHz on a PCM output or destabilise a high-order modulator on a DSD one. So is anything the
    /// neural upscaler runs on, whose overs are the band it wrote.
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
