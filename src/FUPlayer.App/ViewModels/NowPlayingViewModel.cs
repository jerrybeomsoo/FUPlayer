using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using FUPlayer.App.Services;
using FUPlayer.Core.Audio;
using FUPlayer.Core.Dsp.Analysis;
using FUPlayer.Core.Engine;
using FUPlayer.Core.Metadata;
using FUPlayer.Core.Output;

namespace FUPlayer.App.ViewModels;

/// <summary>One bar of a level meter.</summary>
public sealed partial class MeterChannelViewModel(string label) : ObservableObject
{
    [ObservableProperty]
    private double _peakDb = LevelMeter.FloorDb;

    [ObservableProperty]
    private double _rmsDb = LevelMeter.FloorDb;

    [ObservableProperty]
    private double _holdDb = LevelMeter.FloorDb;

    [ObservableProperty]
    private bool _isOver;

    [ObservableProperty]
    private string _peakText = "−∞";

    public string Label { get; } = label;

    public void Update(ChannelLevel level)
    {
        PeakDb = level.PeakDb;
        RmsDb = level.RmsDb;
        HoldDb = level.HoldDb;
        IsOver = level.Over;
        PeakText = level.HoldDb <= LevelMeter.FloorDb ? "−∞" : level.HoldDb.ToString("0.0", CultureInfo.CurrentCulture).Replace('-', Formatting.Minus);
    }

    public void Reset() => Update(new ChannelLevel(LevelMeter.FloorDb, LevelMeter.FloorDb, LevelMeter.FloorDb, false));

    public static string LabelFor(int channel, int channels) => channels switch
    {
        1 => "M",
        2 => channel == 0 ? "L" : "R",
        6 => new[] { "L", "R", "C", "LFE", "Ls", "Rs" }[channel],
        8 => new[] { "L", "R", "C", "LFE", "Lb", "Rb", "Ls", "Rs" }[channel],
        _ => (channel + 1).ToString(CultureInfo.InvariantCulture),
    };
}

/// <summary>The Now Playing page: artwork, metadata, signal path, meters and the analyzer.</summary>
public sealed partial class NowPlayingViewModel : ObservableObject
{
    [ObservableProperty]
    private string _title = "Nothing playing";

    [ObservableProperty]
    private string _artist = "Pick an album in the library, or drop music files anywhere in this window.";

    [ObservableProperty]
    private string _album = string.Empty;

    [ObservableProperty]
    private string _details = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCover))]
    private Bitmap? _cover;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private bool _isTestTone;

    [ObservableProperty]
    private string _sourceFormat = "-";

    [ObservableProperty]
    private string _outputFormat = "-";

    [ObservableProperty]
    private string _outputNote = string.Empty;

    [ObservableProperty]
    private bool _hasOutputNote;

    [ObservableProperty]
    private string _filterName = "-";

    [ObservableProperty]
    private string _quantizerLabel = "DITHER";

    [ObservableProperty]
    private string _quantizerName = "-";

    [ObservableProperty]
    private string _bandwidth = string.Empty;

    [ObservableProperty]
    private bool _hasBandwidth;

    [ObservableProperty]
    private string _resampler = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAcceleration))]
    private string _acceleration = string.Empty;

    [ObservableProperty]
    private string _processing = string.Empty;

    [ObservableProperty]
    private string _notes = string.Empty;

    [ObservableProperty]
    private bool _hasNotes;

    [ObservableProperty]
    private bool _isDsdOutput;

    [ObservableProperty]
    private string _device = string.Empty;

    [ObservableProperty]
    private string _dspLoad = "-";

    [ObservableProperty]
    private string _buffer = "-";

    [ObservableProperty]
    private string _latency = "-";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBlockDelay))]
    private string _blockDelay = "-";

    [ObservableProperty]
    private string _limiterEvents = "0";

    [ObservableProperty]
    private string _clippedSamples = "0";

    [ObservableProperty]
    private string _modulatorResets = "0";

    [ObservableProperty]
    private string _underruns = "0";

    [ObservableProperty]
    private string _apodizationEvents = "0";

    [ObservableProperty]
    private bool _hasLimiterEvents;

    [ObservableProperty]
    private bool _hasClipping;

    [ObservableProperty]
    private bool _hasModulatorResets;

    [ObservableProperty]
    private bool _hasUnderruns;

    [ObservableProperty]
    private bool _hasApodization;

    [ObservableProperty]
    private bool _hasOutputMeters;

    public NowPlayingViewModel(PlayerServices services)
    {
        Analyzer = new AnalyzerViewModel(services);
    }

    public AnalyzerViewModel Analyzer { get; }

    public bool HasCover => Cover is not null;

    public bool HasAcceleration => Acceleration.Length > 0;

    /// <summary>Only block-based filtering has a block delay; tap by tap has none to show.</summary>
    public bool HasBlockDelay => BlockDelay != "-";

    public ObservableCollection<MeterChannelViewModel> SourceMeters { get; } = [];

    public ObservableCollection<MeterChannelViewModel> OutputMeters { get; } = [];

    public void SetTrack(TrackMetadata metadata)
    {
        Title = metadata.DisplayTitle;
        Artist = metadata.DisplayArtist;
        Album = metadata.DisplayAlbum;
        var parts = new List<string>();
        if (metadata.Year > 0)
        {
            parts.Add(metadata.Year.ToString(CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrWhiteSpace(metadata.Codec))
        {
            parts.Add(metadata.Codec);
        }

        if (metadata.BitrateKbps > 0)
        {
            parts.Add($"{metadata.BitrateKbps} kbps");
        }

        if (metadata.TrackGainDb is { } gain)
        {
            parts.Add("RG " + Formatting.Db(gain, "0.00"));
        }

        Details = string.Join("  ·  ", parts);
    }

    public void SetPlaceholder(string title, string subtitle)
    {
        Title = title;
        Artist = subtitle;
        Album = string.Empty;
        Details = string.Empty;
        Cover = null;
    }

    public void UpdateStatus(PlaybackStatus status)
    {
        IsActive = status.State != EngineState.Stopped;
        IsTestTone = status.IsTestTone;
        if (status.Plan is { } plan && IsActive)
        {
            SourceFormat = plan.Source.Describe();
            OutputFormat = plan.Output.Describe();
            OutputNote = DescribeTransport(plan.Output);
            HasOutputNote = OutputNote.Length > 0;
            FilterName = plan.Filter is { } filter && !plan.PassThrough ? $"{filter.Name}  ·  {filter.PhaseLabel}" : plan.FilterName;
            QuantizerLabel = plan.Output.IsDsd ? "MODULATOR" : "DITHER";
            QuantizerName = plan.QuantizerName;
            IsDsdOutput = plan.Output.IsDsd;
            Processing = plan.ProcessingRate > 0 ? "DSP at " + AudioRates.Format(plan.ProcessingRate) : "Bit-perfect DSD path";
            Notes = string.Join(Environment.NewLine, plan.Notes);
            HasNotes = plan.Notes.Count > 0;
        }
        else if (!IsActive)
        {
            SourceFormat = OutputFormat = FilterName = QuantizerName = "-";
            Processing = Notes = OutputNote = string.Empty;
            HasNotes = HasOutputNote = false;
        }

        // What the source's own spectrum says, which is the only way to tell a coded file from a
        // lossless one when the container does not say.
        Bandwidth = IsActive && status.Bandwidth is { } verdict
            ? verdict + (status.IsNeuralRepair ? "  ·  repaired by a trained network" : string.Empty)
            : string.Empty;
        HasBandwidth = Bandwidth.Length > 0;

        Resampler = IsActive ? status.ResamplerSummary ?? string.Empty : string.Empty;
        Acceleration = IsActive ? status.Acceleration ?? string.Empty : string.Empty;
        Device = status.BackendName is null ? string.Empty : $"{status.BackendName}  ·  {status.DeviceName}";
        DspLoad = IsActive ? Formatting.Percent(status.DspLoad) : "-";
        Buffer = IsActive ? Formatting.Percent(status.BufferFill) : "-";
        Latency = IsActive ? Formatting.Milliseconds(status.PipelineLatencySeconds) : "-";
        BlockDelay = IsActive && status.FilterBlockSeconds > 0 ? Formatting.Milliseconds(status.FilterBlockSeconds) : "-";
        LimiterEvents = Formatting.Count(status.LimiterEvents);
        ClippedSamples = Formatting.Count(status.ClippedSamples);
        ModulatorResets = Formatting.Count(status.ModulatorResets);
        Underruns = Formatting.Count(status.UnderrunFrames);
        ApodizationEvents = Formatting.Count(status.ApodizationEvents);
        HasLimiterEvents = status.LimiterEvents > 0;
        HasClipping = status.ClippedSamples > 0;
        HasModulatorResets = status.ModulatorResets > 0;
        HasUnderruns = status.UnderrunFrames > 0;
        HasApodization = status.ApodizationEvents > 0;
    }

    public void UpdateMeters(ChannelLevel[] source, ChannelLevel[]? output)
    {
        Sync(SourceMeters, source);
        if (output is null)
        {
            if (OutputMeters.Count > 0)
            {
                OutputMeters.Clear();
            }
        }
        else
        {
            Sync(OutputMeters, output);
        }

        HasOutputMeters = output is not null;
    }

    public void ResetMeters()
    {
        foreach (MeterChannelViewModel meter in SourceMeters.Concat(OutputMeters))
        {
            meter.Reset();
        }
    }

    /// <summary>Explains how DSD reaches the DAC, including why DoP shows up as a PCM rate.</summary>
    private static string DescribeTransport(OutputFormat output) => output.Kind switch
    {
        OutputSampleKind.Dop =>
            $"DoP packs DSD{AudioRates.DsdMultiplier(output.DsdRate)} ({AudioRates.Format(output.DsdRate)}) into 24-bit PCM frames at 1/16 of that rate, " +
            $"so drivers and many DAC displays report {AudioRates.Format(output.SampleRate)}. The DAC plays DSD once it recognises the DoP markers.",
        OutputSampleKind.NativeDsd =>
            $"Native DSD: the driver receives the 1-bit stream directly at {AudioRates.Format(output.SampleRate)}.",
        _ => string.Empty,
    };

    private static void Sync(ObservableCollection<MeterChannelViewModel> meters, ChannelLevel[] levels)
    {
        if (meters.Count != levels.Length)
        {
            meters.Clear();
            for (int c = 0; c < levels.Length; c++)
            {
                meters.Add(new MeterChannelViewModel(MeterChannelViewModel.LabelFor(c, levels.Length)));
            }
        }

        for (int c = 0; c < levels.Length; c++)
        {
            meters[c].Update(levels[c]);
        }
    }
}
