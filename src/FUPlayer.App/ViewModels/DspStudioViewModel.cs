using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FUPlayer.App.Controls;
using FUPlayer.App.Services;
using FUPlayer.Core.Audio;
using FUPlayer.Core.Dsp.Design;
using FUPlayer.Core.Dsp.Dsd;
using FUPlayer.Core.Dsp.Modulation;
using FUPlayer.Core.Dsp.Quantization;
using FUPlayer.Core.Dsp.Resampling;
using FUPlayer.Core.Dsp.Restoration;
using FUPlayer.Core.Engine;
using FUPlayer.Core.Output;
using FUPlayer.Core.Settings;

namespace FUPlayer.App.ViewModels;

public enum AnalysisView
{
    Magnitude,
    Passband,
    Impulse,
    Step,
    NoiseShaping,
}

/// <summary>A resampling filter in the filter browser.</summary>
public sealed partial class FilterOptionViewModel(FilterPreset preset, DspStudioViewModel owner) : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    public FilterPreset Preset { get; } = preset;

    public string Name => Preset.Name;

    public string Description => Preset.Description;

    public string Traits { get; } = BuildTraits(preset);

    [RelayCommand]
    private void Select() => owner.SelectFilter(this);

    private static string BuildTraits(FilterPreset preset)
    {
        if (preset.Family == FilterFamily.None)
        {
            return "no conversion";
        }

        var parts = new List<string> { preset.PhaseLabel };
        if (preset.AttenuationDb > 0)
        {
            parts.Add(preset.AttenuationDb.ToString("0", CultureInfo.InvariantCulture) + " dB");
        }

        if (preset.Apodizing)
        {
            parts.Add("apodizing");
        }

        if (preset.EarlyRollOff)
        {
            parts.Add("above 48 kHz");
        }

        if (preset.IntegerRatioOnly)
        {
            parts.Add("integer ratios");
        }

        return string.Join("  ·  ", parts);
    }
}

public sealed record FilterGroupViewModel(string Name, IReadOnlyList<FilterOptionViewModel> Filters);

/// <summary>How one typical source format would be played with the current settings.</summary>
public sealed record PlanPreviewRow(string Source, string Output, string Filter, string Quantizer, string Notes, bool IsDsdOutput)
{
    public bool HasNotes => Notes.Length > 0;
}

/// <summary>The DSP studio: output mode, filter, dither / modulator selection with live analysis and a plan preview.</summary>
public sealed partial class DspStudioViewModel : ObservableObject
{
    private static readonly (string Label, StreamFormat Format)[] PreviewSources =
    [
        ("44.1 kHz / 16-bit", StreamFormat.Pcm(44_100, 2, 16)),
        ("48 kHz / 24-bit", StreamFormat.Pcm(48_000, 2, 24)),
        ("96 kHz / 24-bit", StreamFormat.Pcm(96_000, 2, 24)),
        ("192 kHz / 24-bit", StreamFormat.Pcm(192_000, 2, 24)),
        ("DSD64 file", StreamFormat.Dsd(2_822_400, 2)),
        ("DSD128 file", StreamFormat.Dsd(5_644_800, 2)),
    ];

    private readonly PlayerServices _services;
    private readonly DispatcherTimer _refreshTimer;
    private readonly List<FilterOptionViewModel> _filters;
    private readonly bool _ready;
    private bool _suspend;
    private int _generation;
    private FilterAnalysisResult? _analysis;
    private NoiseCurve? _noise;
    private string? _analysisMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDsdMode), nameof(IsPcmSettingsVisible), nameof(IsDsdTransportVisible), nameof(ModeDescription), nameof(FilterCardTitle), nameof(FilterCardDescription))]
    private Choice? _selectedMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFixedRate))]
    private Choice? _selectedRateSelection;

    [ObservableProperty]
    private Choice? _selectedRateLimit;

    [ObservableProperty]
    private Choice? _selectedFixedRate;

    [ObservableProperty]
    private Choice? _selectedBits;

    [ObservableProperty]
    private Choice? _selectedDither;

    [ObservableProperty]
    private Choice? _selectedModulator;

    [ObservableProperty]
    private Choice? _selectedDsdLimit;

    [ObservableProperty]
    private Choice? _selectedTransport;

    [ObservableProperty]
    private bool _use48kDsdRates;

    [ObservableProperty]
    private bool _passThrough;

    [ObservableProperty]
    private Choice? _selectedRemodulationFilter;

    [ObservableProperty]
    private Choice? _selectedDsdToPcmFilter;

    [ObservableProperty]
    private bool _restoreDsdLevel;

    [ObservableProperty]
    private Choice? _selectedStaging;

    [ObservableProperty]
    private Choice? _selectedFilterLength;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAutomaticConvolution))]
    private Choice? _selectedConvolution;

    [ObservableProperty]
    private Choice? _selectedConvolutionThreshold;

    [ObservableProperty]
    private Choice? _selectedConvolutionBlock;

    [ObservableProperty]
    private Choice? _selectedConvolutionLayout;

    [ObservableProperty]
    private bool _removeUltrasonics;

    [ObservableProperty]
    private bool _neuralUpscaler;

    [ObservableProperty]
    private Choice? _selectedUpscalerSource;

    [ObservableProperty]
    private Choice? _selectedUpscalerBand;

    [ObservableProperty]
    private bool _outputDelta;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LimiterDescription))]
    private bool _limiter;

    [ObservableProperty]
    private bool _apodizationDetection;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFocusedFilter))]
    private FilterOptionViewModel? _focusedFilter;

    [ObservableProperty]
    private string _selectedFilterName = string.Empty;

    [ObservableProperty]
    private Choice? _selectedAnalysisView;

    [ObservableProperty]
    private Choice? _selectedAnalysisRate;

    [ObservableProperty]
    private IReadOnlyList<PlotSeries>? _plotSeries;

    [ObservableProperty]
    private IReadOnlyList<PlotMarker>? _plotMarkers;

    [ObservableProperty]
    private PlotAxisScale _plotXScale;

    [ObservableProperty]
    private PlotAxisFormat _plotXFormat = PlotAxisFormat.Frequency;

    [ObservableProperty]
    private PlotAxisFormat _plotYFormat = PlotAxisFormat.Decibels;

    [ObservableProperty]
    private double _plotXMinimum = double.NaN;

    [ObservableProperty]
    private double _plotXMaximum = double.NaN;

    [ObservableProperty]
    private double _plotYMinimum = double.NaN;

    [ObservableProperty]
    private double _plotYMaximum = double.NaN;

    [ObservableProperty]
    private bool _isAnalyzing;

    [ObservableProperty]
    private string _analysisSummary = string.Empty;

    [ObservableProperty]
    private string _conversionText = string.Empty;

    [ObservableProperty]
    private string _latencyText = "-";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBlockDelay))]
    private string _blockDelayText = "-";

    [ObservableProperty]
    private string _costText = "-";

    [ObservableProperty]
    private double _costFraction;

    [ObservableProperty]
    private string _tapsText = "-";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDirectComparison))]
    private string _directCostText = string.Empty;

    [ObservableProperty]
    private string _stagesText = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<PlanPreviewRow> _planPreview = [];

    public DspStudioViewModel(PlayerServices services)
    {
        _services = services;
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            _ = RefreshAsync();
        };

        _filters = FilterCatalog.All.Select(p => new FilterOptionViewModel(p, this)).ToList();
        FilterGroups = _filters.GroupBy(f => f.Preset.Group).Select(g => new FilterGroupViewModel(g.Key, g.ToArray())).ToArray();

        DitherChoices = DitherCatalog.All.Select(d => new Choice(d.Name, d.Id, d.Description)).ToArray();
        ModulatorChoices = ModulatorCatalog.All.Select(m => new Choice(m.Name, m.Id, m.Description)).ToArray();
        DsdFilterChoices = DsdFilterCatalog.All.Select(f => new Choice(f.Name, f.Id, f.Description)).ToArray();
        FixedRateChoices = AudioRates.StandardPcmRates.Select(r => new Choice(AudioRates.Format(r), r)).ToArray();

        LoadFromSettings();
        UpdateFilterFlags();
        FocusedFilter = FindFilter(ActiveFilter);
        SelectedAnalysisRate = AnalysisRateChoices[0];
        SelectedAnalysisView = AnalysisViewChoices[0];
        _ready = true;

        services.EngineSettingsChanged += (_, _) => ScheduleRefresh();
        ScheduleRefresh();
    }

    public IReadOnlyList<Choice> ModeChoices { get; } =
    [
        new("PCM", OutputMode.Pcm, "Every file is converted to high-rate PCM. DSD files are turned into PCM first."),
        new("DSD", OutputMode.Dsd, "Every file is delta-sigma modulated to a 1-bit stream. The DAC's own oversampling filter is bypassed."),
        new("Follow source", OutputMode.FollowSource, "PCM files are played as PCM and DSD files as DSD."),
    ];

    public IReadOnlyList<Choice> RateSelectionChoices { get; } =
    [
        new("Highest rate", RateSelection.Automatic, "The highest rate the device accepts, preferring the source's 44.1 or 48 kHz family."),
        new("Same family only", RateSelection.SameFamily, "Only integer multiples of the source family, e.g. 44.1 kHz → 88.2, 176.4 or 352.8 kHz."),
        new("Fixed rate", RateSelection.Fixed, "Always the chosen rate, whatever the source."),
    ];

    public IReadOnlyList<Choice> RateLimitChoices { get; } =
    [
        new("No limit", 0),
        new("88.2 / 96 kHz", 96_000),
        new("176.4 / 192 kHz", 192_000),
        new("352.8 / 384 kHz", 384_000),
        new("705.6 / 768 kHz", 768_000),
        new("1.4 / 1.5 MHz", 1_536_000),
    ];

    public IReadOnlyList<Choice> FixedRateChoices { get; }

    public IReadOnlyList<Choice> BitsChoices { get; } =
    [
        new("16-bit", 16),
        new("20-bit", 20),
        new("24-bit", 24),
        new("32-bit", 32),
    ];

    public IReadOnlyList<Choice> DitherChoices { get; }

    public IReadOnlyList<Choice> ModulatorChoices { get; }

    public IReadOnlyList<Choice> DsdFilterChoices { get; }

    public IReadOnlyList<Choice> DsdLimitChoices { get; } =
    [
        new("DSD64", 64),
        new("DSD128", 128),
        new("DSD256", 256),
        new("DSD512", 512),
        new("DSD1024", 1024),
    ];

    public IReadOnlyList<Choice> TransportChoices { get; } =
    [
        new("Native DSD", DsdTransport.Native, "1-bit data sent directly to drivers that support it (ASIO DSD mode). Other outputs fall back to DoP."),
        new("DoP", DsdTransport.Dop, "DSD over PCM: 1-bit data in 24-bit PCM frames at 1/16 of the DSD rate. DSD64 becomes a 176.4 kHz carrier."),
    ];

    /// <summary>
    /// Prototype length in taps, counted at the output rate. The transition width of a windowed sinc is
    /// inversely proportional to it, roughly 8.5·fs/N for a 130 dB stopband, and the group delay of a
    /// linear-phase design is exactly (N-1)/2 output samples. Both figures below are arithmetic, not taste.
    /// </summary>
    public IReadOnlyList<Choice> FilterLengthChoices { get; } =
    [
        new("Automatic", 0,
            "Length taken from the filter's stopband specification. Taps are counted at the output rate, so a stage running below it is designed with proportionally fewer."),
        new("16,384 taps", 16_384, "370 Hz transition width, 11.6 ms group delay at 705.6 kHz."),
        new("32,768 taps", 32_768, "180 Hz transition width, 23 ms group delay at 705.6 kHz."),
        new("65,536 taps", 65_536, "90 Hz transition width, 46 ms group delay at 705.6 kHz."),
        new("131,072 taps", 131_072, "45 Hz transition width, 93 ms group delay at 705.6 kHz."),
        new("262,144 taps", 262_144, "23 Hz transition width, 186 ms group delay at 705.6 kHz."),
        new("524,288 taps", 524_288, "12 Hz transition width, 371 ms group delay at 705.6 kHz."),
        new("1,048,576 taps", 1_048_576, "6 Hz transition width, 743 ms group delay at 705.6 kHz."),
        new("2,097,152 taps", 2_097_152,
            "3 Hz transition width, 1.5 s group delay. Linear phase only above 2,097,152 taps: the phase transform cannot address more."),
        new("4,194,304 taps", 4_194_304, "1.4 Hz transition width, 3.0 s group delay, 60 MB of coefficients."),
        new("8,388,608 taps", 8_388_608, "0.7 Hz transition width, 5.9 s group delay, 250 MB of partition spectra."),
        new("16,777,216 taps", 16_777_216, "0.35 Hz transition width, 11.9 s group delay, 500 MB of partition spectra."),
        new("33,554,432 taps", 33_554_432,
            "0.18 Hz transition width, 23.8 s group delay, about 1 GB. Offline rendering rather than playback."),
    ];

    public IReadOnlyList<Choice> ConvolutionChoices { get; } =
    [
        new("Frequency domain", ConvolutionMode.Automatic,
            "Partitioned overlap-save FFT above the crossover, direct convolution below it. Cost per output sample scales with log2 of the FFT size, not with tap count."),
        new("Tap by tap", ConvolutionMode.TapByTap,
            "Direct convolution: one multiply-add per tap per output sample. No block latency, and cost proportional to taps per phase. The settings below do not apply."),
    ];

    /// <summary>Whether the three tuning rows apply: they describe the automatic choice, so tap by tap hides them.</summary>
    public bool IsAutomaticConvolution => SelectedConvolution?.Value is ConvolutionMode.Automatic;

    /// <summary>
    /// Where the frequency domain takes over, in taps per polyphase phase. A crossover between two arithmetics,
    /// not a limit on the filter: the length itself is set above, and a stage below this point convolves the
    /// same coefficients tap by tap.
    /// </summary>
    public IReadOnlyList<Choice> ConvolutionThresholdChoices { get; } =
    [
        new("Built-in: 1,024", 0, "Modelled break-even between transform cost and per-tap cost."),
        new("256", 256, "Below break-even. Short filters gain block latency for little speed."),
        new("512", 512, "Below break-even."),
        new("1,024", 1_024, "The break-even point, set explicitly rather than by the cost model."),
        new("2,048", 2_048, "Above break-even. More filters use direct convolution and add no latency."),
        new("4,096", 4_096, "65,536 taps at 16 phases is 4,096 per phase, so that filter sits at this point."),
        new("16,384", 16_384, "Only long filters transform. Direct convolution costs several times more here."),
        new("65,536", 65_536, "1,048,576 taps at 16 phases is 65,536 per phase."),
        new("262,144", 262_144, "Direct convolution for effectively every filter."),
        new("Never", int.MaxValue, "Direct convolution at any length, the same as selecting tap by tap above."),
    ];

    /// <summary>
    /// Ceiling on the block of input a frequency-domain stage holds back, which sets the FFT window: the
    /// transform runs over two blocks, and half of each answer is discarded as the wrap-around. It is delay
    /// against arithmetic and nothing else, since the samples that come out are the same either way.
    /// </summary>
    /// <remarks>
    /// A longer window holds more taps per partition, so there are fewer partitions and fewer complex
    /// multiply-accumulates per output sample: the long windows are the cheap ones, and the short windows are
    /// the computationally intensive ones. The multipliers quoted are against the cheapest block for filters of
    /// 65,537 to 1,048,577 taps, and the layered figures are what non-uniform partitioning costs at the same wait.
    /// </remarks>
    public IReadOnlyList<Choice> ConvolutionBlockChoices { get; } =
    [
        new("Cheapest", 0.0,
            "Lowest modelled cost within a ceiling of 1/32 of the filter span, minimum 50 ms. Usually 2,048 samples and a 4,096-point FFT."),
        new("100 ms", 100.0,
            "4,096 samples, 8,192-point FFT at 44.1 kHz. Fewest partitions and the lowest arithmetic cost of any fixed setting."),
        new("50 ms", 50.0, "2,048 samples, 4,096-point FFT at 44.1 kHz. What Cheapest selects for most filters."),
        new("25 ms", 25.0, "1,024 samples, 2,048-point FFT at 44.1 kHz. 1.1x to 1.7x the cost of the cheapest block."),
        new("10 ms", 10.0,
            "256 samples, 512-point FFT at 44.1 kHz. 1.8x to 6x the cheapest block uniform, 1.1x to 1.8x layered."),
        new("3 ms", 3.0,
            "128 samples, 256-point FFT at 44.1 kHz. 3x to 12x the cheapest block uniform, 1.2x to 2x layered."),
        new("1.5 ms", 1.5,
            "64 samples, 128-point FFT at 44.1 kHz, the shortest block available. 5x to 24x uniform, 1.3x to 2.2x layered."),
    ];

    /// <summary>What the neural upscaler would run with. Read from the models folder each time it is asked for.</summary>
    public string UpscalerStatus => ModelLibrary.DescribeUpscaler();

    /// <summary>Reads the models folder again. Called when the page is opened and when a switch changes.</summary>
    public void RefreshModelStatus() => OnPropertyChanged(nameof(UpscalerStatus));

    public IReadOnlyList<Choice> UpscalerSourceChoices { get; } =
    [
        new("Automatic", UpscalerSource.Automatic, "Lossy codecs and application captures: lossy. PCM, FLAC, ALAC: lossless."),
        new("Lossy", UpscalerSource.Lossy, "Passband correction below the codec cutoff; synthesis above it."),
        new("Lossless", UpscalerSource.Lossless, "Passband preserved; synthesis above Nyquist only. Idle when the output is below 2 fs."),
    ];

    public IReadOnlyList<Choice> UpscalerBandChoices { get; } =
    [
        new("Quiet", -6.0, "−6 dB relative to the model output."),
        new("Measured", 0.0, "Model output: −0.7 dB against the held-out masters on average."),
        new("Lifted", 3.0, "+3 dB relative to the model output."),
        new("Strong", 6.0, "+6 dB relative to the model output."),
    ];

    public IReadOnlyList<Choice> ConvolutionLayoutChoices { get; } =
    [
        new("One block length", true,
            "Uniform partitioning: one FFT size for every tap. Required for GPU offload, and the more expensive layout at short blocks."),
        new("Layered", false,
            "Non-uniform partitioning: short FFTs for the early taps, progressively longer ones behind them. Same latency at a fraction of the cost, but no GPU offload."),
    ];

    public IReadOnlyList<Choice> StagingChoices { get; } =
    [
        new("Single stage", StagingMode.SingleStage,
            "One polyphase stage for the whole ratio, so only the selected filter's response is applied. Highest CPU cost."),
        new("Multi-stage", StagingMode.MultiStage,
            "Selected filter converts 2x, half-band stages do the rest. About half the cost, at the price of a cascade of responses."),
    ];

    public IReadOnlyList<Choice> AnalysisViewChoices { get; } =
    [
        new("Magnitude", AnalysisView.Magnitude),
        new("Passband", AnalysisView.Passband),
        new("Impulse", AnalysisView.Impulse),
        new("Step", AnalysisView.Step),
        new("Noise shaping", AnalysisView.NoiseShaping),
    ];

    public IReadOnlyList<Choice> AnalysisRateChoices { get; } =
        new[] { 44_100, 48_000, 88_200, 96_000, 176_400, 192_000 }.Select(r => new Choice(AudioRates.Format(r) + " source", r)).ToArray();

    public IReadOnlyList<FilterGroupViewModel> FilterGroups { get; }

    public bool IsDsdMode => SelectedMode?.Value is OutputMode.Dsd;

    /// <summary>PCM output settings apply (PCM mode, and PCM files in follow-source mode).</summary>
    public bool IsPcmSettingsVisible => !IsDsdMode;

    /// <summary>DSD can leave the player (DSD mode, or DSD files in follow-source mode).</summary>
    public bool IsDsdTransportVisible => SelectedMode?.Value is not OutputMode.Pcm;

    public string ModeDescription => SelectedMode?.Description ?? string.Empty;

    public string FilterCardTitle => IsDsdMode ? "Filter feeding the modulator" : "Resampling filter";

    public string FilterCardDescription => SelectedMode?.Value switch
    {
        OutputMode.Dsd => "Brings PCM files up to the modulator rate. Separate from the PCM output filter.",
        OutputMode.FollowSource => "Used for PCM files. DSD files keep their own path.",
        _ => "Used for every file played as PCM. Separate from the filter used for DSD output.",
    };

    public bool IsFixedRate => SelectedRateSelection?.Value is RateSelection.Fixed;

    public bool HasFocusedFilter => FocusedFilter is not null;

    public bool HasDirectComparison => DirectCostText.Length > 0;

    /// <summary>Whether there is a block delay to talk about at all; tap by tap has none.</summary>
    public bool HasBlockDelay => BlockDelayText != "-";

    public string LimiterDescription => Limiter
        ? "Look-ahead peak limiter, 1 ms: holds inter-sample overs below full scale. Now playing counts engagements."
        : "Off for lossless sources. Lossy codecs, captures and upscaled audio stay limited: their overs are codec or network products that would alias above 40 kHz or destabilise a DSD modulator.";

    private PlayerSettings Settings => _services.Settings;

    private string ActiveFilter
    {
        get => IsDsdMode ? Settings.Dsd.Filter : Settings.Pcm.Filter;
        set
        {
            if (IsDsdMode)
            {
                Settings.Dsd.Filter = value;
            }
            else
            {
                Settings.Pcm.Filter = value;
            }
        }
    }

    private int ActiveTaps
    {
        get => IsDsdMode ? Settings.Dsd.FilterTaps : Settings.Pcm.FilterTaps;
        set
        {
            if (IsDsdMode)
            {
                Settings.Dsd.FilterTaps = value;
            }
            else
            {
                Settings.Pcm.FilterTaps = value;
            }
        }
    }

    internal void SelectFilter(FilterOptionViewModel option)
    {
        ActiveFilter = option.Preset.Id;
        FocusedFilter = option;
        UpdateFilterFlags();
        Changed();
    }

    partial void OnSelectedModeChanged(Choice? value)
    {
        if (!_ready || value?.Value is not OutputMode mode)
        {
            return;
        }

        Settings.Output.Mode = mode;
        _suspend = true;
        try
        {
            SelectedRateSelection = Choice.Find(RateSelectionChoices, IsDsdMode ? Settings.Dsd.RateSelection : Settings.Pcm.RateSelection);
            SelectedStaging = Choice.Find(StagingChoices, IsDsdMode ? Settings.Dsd.Staging : Settings.Pcm.Staging);
            SelectedFilterLength = Choice.Find(FilterLengthChoices, ActiveTaps) ?? FilterLengthChoices[0];
        }
        finally
        {
            _suspend = false;
        }

        UpdateFilterFlags();
        FocusedFilter = FindFilter(ActiveFilter);
        Changed();
    }

    partial void OnSelectedRateSelectionChanged(Choice? value) => Update(value, (RateSelection selection) =>
    {
        if (IsDsdMode)
        {
            Settings.Dsd.RateSelection = selection;
        }
        else
        {
            Settings.Pcm.RateSelection = selection;
        }
    });

    partial void OnSelectedRateLimitChanged(Choice? value) => Update(value, (int limit) => Settings.Pcm.RateLimit = limit);

    partial void OnSelectedFixedRateChanged(Choice? value) => Update(value, (int rate) => Settings.Pcm.FixedRate = rate);

    partial void OnSelectedBitsChanged(Choice? value) => Update(value, (int bits) => Settings.Pcm.DacBits = bits);

    partial void OnSelectedDitherChanged(Choice? value) => Update(value, (string id) => Settings.Pcm.DitherId = id);

    partial void OnSelectedModulatorChanged(Choice? value) => Update(value, (string id) => Settings.Dsd.ModulatorId = id);

    partial void OnSelectedDsdLimitChanged(Choice? value) => Update(value, (int limit) => Settings.Dsd.HighestMultiplier = limit);

    partial void OnSelectedTransportChanged(Choice? value) => Update(value, (DsdTransport transport) => Settings.Output.DsdTransport = transport);

    partial void OnSelectedRemodulationFilterChanged(Choice? value) => Update(value, (string id) => Settings.Dsd.InputFilterId = id);

    partial void OnSelectedDsdToPcmFilterChanged(Choice? value) => Update(value, (string id) => Settings.DsdToPcm.FilterId = id);

    partial void OnSelectedStagingChanged(Choice? value) => Update(value, (StagingMode staging) =>
    {
        if (IsDsdMode)
        {
            Settings.Dsd.Staging = staging;
        }
        else
        {
            Settings.Pcm.Staging = staging;
        }
    });

    partial void OnSelectedFilterLengthChanged(Choice? value) => Update(value, (int taps) => ActiveTaps = taps);

    partial void OnUse48kDsdRatesChanged(bool value) => Update(value, (bool v) => Settings.Output.Use48kDsdRates = v);

    partial void OnPassThroughChanged(bool value) => Update(value, (bool v) => Settings.Dsd.PassThrough = v);

    partial void OnRestoreDsdLevelChanged(bool value) => Update(value, (bool v) => Settings.DsdToPcm.RestoreLevel = v);

    partial void OnRemoveUltrasonicsChanged(bool value) => Update(value, (bool v) => Settings.Processing.RemoveUltrasonics = v);

    partial void OnNeuralUpscalerChanged(bool value)
    {
        Update(value, (bool v) => Settings.Restoration.NeuralUpscaler = v);
        OnPropertyChanged(nameof(UpscalerStatus));
    }

    partial void OnSelectedUpscalerSourceChanged(Choice? value) =>
        Update(value, (UpscalerSource source) => Settings.Restoration.SourceType = source);

    partial void OnSelectedUpscalerBandChanged(Choice? value) =>
        Update(value, (double gainDb) => Settings.Restoration.UpscalerBandDb = gainDb);

    partial void OnOutputDeltaChanged(bool value) => Update(value, (bool v) => Settings.Restoration.OutputDelta = v);

    partial void OnLimiterChanged(bool value) => Update(value, (bool v) => Settings.Processing.Limiter = v);

    partial void OnSelectedConvolutionChanged(Choice? value) =>
        Update(value, (ConvolutionMode mode) => Settings.Processing.Convolution = mode);

    partial void OnSelectedConvolutionThresholdChanged(Choice? value) =>
        Update(value, (int taps) => Settings.Processing.ConvolutionThresholdTaps = taps);

    partial void OnSelectedConvolutionBlockChanged(Choice? value) =>
        Update(value, (double milliseconds) => Settings.Processing.ConvolutionMaxBlockMs = milliseconds);

    partial void OnSelectedConvolutionLayoutChanged(Choice? value) =>
        Update(value, (bool uniform) => Settings.Processing.ConvolutionUniformBlocks = uniform);

    partial void OnApodizationDetectionChanged(bool value) => Update(value, (bool v) => Settings.Processing.ApodizationDetection = v);

    partial void OnSelectedAnalysisViewChanged(Choice? value) => UpdatePlot();

    partial void OnSelectedAnalysisRateChanged(Choice? value) => ScheduleRefresh();

    partial void OnFocusedFilterChanged(FilterOptionViewModel? value) => ScheduleRefresh();

    private void Update<T>(Choice? choice, Action<T> apply)
    {
        if (_ready && !_suspend && choice?.Value is T value)
        {
            apply(value);
            Changed();
        }
    }

    private void Update(bool value, Action<bool> apply)
    {
        if (_ready && !_suspend)
        {
            apply(value);
            Changed();
        }
    }

    private void Changed() => _services.NotifySettingsChanged();

    private void ScheduleRefresh()
    {
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    private FilterOptionViewModel? FindFilter(string id) =>
        _filters.FirstOrDefault(f => f.Preset.Id == id) ?? _filters.FirstOrDefault(f => f.Preset.Id == FilterCatalog.FallbackId);

    private void LoadFromSettings()
    {
        PlayerSettings s = Settings;
        _suspend = true;
        try
        {
            SelectedMode = Choice.Find(ModeChoices, s.Output.Mode) ?? ModeChoices[0];
            SelectedRateSelection = Choice.Find(RateSelectionChoices, IsDsdMode ? s.Dsd.RateSelection : s.Pcm.RateSelection) ?? RateSelectionChoices[0];
            SelectedRateLimit = Choice.Find(RateLimitChoices, s.Pcm.RateLimit) ?? RateLimitChoices[0];
            SelectedFixedRate = Choice.Find(FixedRateChoices, s.Pcm.FixedRate) ?? FixedRateChoices.FirstOrDefault();
            SelectedBits = Choice.Find(BitsChoices, s.Pcm.DacBits) ?? BitsChoices[2];
            SelectedDither = Choice.Find(DitherChoices, s.Pcm.DitherId) ?? DitherChoices[0];
            SelectedModulator = Choice.Find(ModulatorChoices, s.Dsd.ModulatorId) ?? ModulatorChoices[0];
            SelectedDsdLimit = Choice.Find(DsdLimitChoices, s.Dsd.HighestMultiplier) ?? DsdLimitChoices[2];
            SelectedTransport = Choice.Find(TransportChoices, s.Output.DsdTransport) ?? TransportChoices[0];
            Use48kDsdRates = s.Output.Use48kDsdRates;
            PassThrough = s.Dsd.PassThrough;
            SelectedRemodulationFilter = Choice.Find(DsdFilterChoices, s.Dsd.InputFilterId) ?? DsdFilterChoices[0];
            SelectedDsdToPcmFilter = Choice.Find(DsdFilterChoices, s.DsdToPcm.FilterId) ?? DsdFilterChoices[0];
            RestoreDsdLevel = s.DsdToPcm.RestoreLevel;
            SelectedStaging = Choice.Find(StagingChoices, IsDsdMode ? s.Dsd.Staging : s.Pcm.Staging) ?? StagingChoices[0];
            SelectedFilterLength = Choice.Find(FilterLengthChoices, ActiveTaps) ?? FilterLengthChoices[0];
            SelectedConvolution = Choice.Find(ConvolutionChoices, s.Processing.Convolution) ?? ConvolutionChoices[0];
            SelectedConvolutionThreshold =
                Choice.Find(ConvolutionThresholdChoices, s.Processing.ConvolutionThresholdTaps) ?? ConvolutionThresholdChoices[0];
            SelectedConvolutionBlock =
                Choice.Find(ConvolutionBlockChoices, s.Processing.ConvolutionMaxBlockMs) ?? ConvolutionBlockChoices[0];
            SelectedConvolutionLayout =
                Choice.Find(ConvolutionLayoutChoices, s.Processing.ConvolutionUniformBlocks) ?? ConvolutionLayoutChoices[0];
            RemoveUltrasonics = s.Processing.RemoveUltrasonics;
            NeuralUpscaler = s.Restoration.NeuralUpscaler;
            SelectedUpscalerSource = Choice.Find(UpscalerSourceChoices, s.Restoration.SourceType) ?? UpscalerSourceChoices[0];
            SelectedUpscalerBand = Choice.Find(UpscalerBandChoices, s.Restoration.UpscalerBandDb) ?? UpscalerBandChoices[1];
            OutputDelta = s.Restoration.OutputDelta;
            Limiter = s.Processing.Limiter;
            ApodizationDetection = s.Processing.ApodizationDetection;
        }
        finally
        {
            _suspend = false;
        }
    }

    private void UpdateFilterFlags()
    {
        string id = ActiveFilter;
        foreach (FilterOptionViewModel filter in _filters)
        {
            filter.IsSelected = filter.Preset.Id == id;
        }

        SelectedFilterName = FindFilter(id)?.Name ?? id;
    }

    private async Task RefreshAsync()
    {
        int generation = ++_generation;
        PlayerSettings snapshot = Settings.Clone();
        FilterPreset? focused = FocusedFilter?.Preset;
        int analysisRate = SelectedAnalysisRate?.Value is int rate ? rate : 44_100;
        IsAnalyzing = true;

        RefreshResult result = await Task.Run(() => Compute(snapshot, focused, analysisRate));
        if (generation != _generation)
        {
            return;
        }

        IsAnalyzing = false;
        PlanPreview = result.Rows;
        _analysis = result.Analysis;
        _noise = result.Noise;
        _analysisMessage = result.Message;
        ConversionText = result.Conversion;
        if (_analysis is { Stages.Count: > 0 } analysis)
        {
            LatencyText = analysis.LatencyMilliseconds.ToString("0.00", CultureInfo.CurrentCulture) + " ms";
            BlockDelayText = analysis.BlockDelayMilliseconds > 0
                ? analysis.BlockDelayMilliseconds.ToString("0.00", CultureInfo.CurrentCulture) + " ms"
                : "none";
            CostText = Formatting.Operations(analysis.OperationsPerSecond);
            CostFraction = Math.Clamp((Math.Log10(Math.Max(1, analysis.OperationsPerSecond)) - 5) / 5, 0, 1);
            TapsText = analysis.Taps > 0 ? analysis.Taps.ToString("N0", CultureInfo.CurrentCulture) : "-";
            // Long filters convolve in the frequency domain; saying what the tap-by-tap route would have cost is
            // the only way the small figure above reads as a saving rather than as a filter that is not running.
            DirectCostText = analysis.DirectOperationsPerSecond > analysis.OperationsPerSecond * 1.5
                ? "tap by tap: " + Formatting.Operations(analysis.DirectOperationsPerSecond)
                : string.Empty;
            StagesText = string.Join("  →  ", analysis.Stages);
        }
        else
        {
            LatencyText = CostText = TapsText = BlockDelayText = "-";
            DirectCostText = string.Empty;
            CostFraction = 0;
            StagesText = string.Empty;
        }

        UpdatePlot();
    }

    private RefreshResult Compute(PlayerSettings settings, FilterPreset? focused, int analysisRate)
    {
        IAudioBackend backend = _services.Backends.Resolve(settings.Output.BackendId);
        // Previews must never open the device: they use what the Output page probed, or a permissive guess.
        DeviceCapabilities capabilities = _services.GetCapabilities(backend.Id, settings.Output.DeviceId, settings.Output.Channels, refresh: false, allowProbe: false);

        var rows = new List<PlanPreviewRow>(PreviewSources.Length);
        foreach ((string label, StreamFormat format) in PreviewSources)
        {
            try
            {
                PlaybackPlan plan = OutputPlanner.Plan(format, settings, backend, capabilities);
                rows.Add(new PlanPreviewRow(label, plan.Output.DescribeShort(), plan.FilterName, plan.QuantizerName, string.Join("  ", plan.Notes), plan.Output.IsDsd));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException)
            {
                rows.Add(new PlanPreviewRow(label, "Not playable", "-", "-", ex.Message, false));
            }
        }

        PlaybackPlan? analysisPlan = null;
        string? message = null;
        try
        {
            analysisPlan = OutputPlanner.Plan(StreamFormat.Pcm(analysisRate, 2, 24), settings, backend, capabilities);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException)
        {
            message = ex.Message;
        }

        FilterAnalysisResult? analysis = null;
        string conversion = string.Empty;
        if (analysisPlan is not null && focused is not null)
        {
            int output = analysisPlan.ProcessingRate > 0 ? analysisPlan.ProcessingRate : analysisPlan.Output.SampleRate;
            bool dsdMode = settings.Output.Mode == OutputMode.Dsd;
            StagingMode staging = dsdMode ? settings.Dsd.Staging : settings.Pcm.Staging;
            int taps = dsdMode ? settings.Dsd.FilterTaps : settings.Pcm.FilterTaps;
            FilterPreset analysed = focused;
            if (focused.EarlyRollOff && analysisRate < FilterCatalog.EarlyRollOffMinimumRate)
            {
                analysed = FilterCatalog.Get(FilterCatalog.EarlyRollOffSubstituteId);
                message = $"{focused.Name} only applies to files above 48 kHz; {AudioRates.Format(analysisRate)} files use {analysed.Name}, shown here. Pick a higher source rate to see the filter itself.";
            }

            conversion = $"{AudioRates.Format(analysisRate)}  →  {AudioRates.Format(output)}";
            if (analysed.Family != FilterFamily.None && output != analysisRate && !ResamplerFactory.IsSupported(analysed, analysisRate, output))
            {
                message = $"{analysed.Name} cannot perform this conversion, so {FilterCatalog.Get(FilterCatalog.FallbackId).Name} is used instead.";
            }
            else
            {
                analysis = FilterAnalysis.Analyze(
                    analysed, analysisRate, output, staging, taps, settings.Processing.Convolution, options: new ConvolutionOptions
                    {
                        ThresholdTapsPerPhase = settings.Processing.ConvolutionThresholdTaps,
                        MaxBlockMilliseconds = settings.Processing.ConvolutionMaxBlockMs,
                        LayeredBlocks = !settings.Processing.ConvolutionUniformBlocks,
                    });
                if (analysed.Family == FilterFamily.None || output == analysisRate)
                {
                    message = "No rate conversion happens for this source with the current settings.";
                }
            }
        }

        return new RefreshResult(rows, analysis, conversion, message, ComputeNoise(settings, analysisPlan));
    }

    private static NoiseCurve ComputeNoise(PlayerSettings settings, PlaybackPlan? plan)
    {
        if (settings.Output.Mode == OutputMode.Dsd)
        {
            ModulatorPreset modulator = ModulatorCatalog.Get(settings.Dsd.ModulatorId);
            int dsdRate = plan is { Output.IsDsd: true } ? plan.Output.DsdRate : AudioRates.DsdRate(Math.Min(256, settings.Dsd.HighestMultiplier), family48k: false);
            NoiseTransferFunction ntf = ModulatorCatalog.DesignNtf(modulator, dsdRate);
            return NoiseCurve.From(ntf, dsdRate, $"{modulator.Name} modulator at DSD{AudioRates.DsdMultiplier(dsdRate)}");
        }

        int rate = plan is { Output.IsDsd: false } ? plan.Output.SampleRate : 352_800;
        DitherPreset dither = DitherCatalog.Resolve(settings.Pcm.DitherId, rate);
        if (dither.Kind == DitherKind.NoiseShaped)
        {
            double osr = Math.Max(1.05, rate / (2.0 * DitherCatalog.ShapingBandwidthHz));
            NoiseTransferFunction ntf = NoiseTransferFunction.Synthesize(dither.ShaperOrder, osr, dither.ShaperMaxGain, dither.OptimizeZeros);
            return NoiseCurve.From(ntf, rate, $"{dither.Name} at {AudioRates.Format(rate)}");
        }

        return new NoiseCurve(
            [10.0, rate / 2.0],
            [0.0, 0.0],
            $"{dither.Name} at {AudioRates.Format(rate)}: requantization noise is left spectrally flat.");
    }

    private void UpdatePlot()
    {
        AnalysisView view = SelectedAnalysisView?.Value is AnalysisView selected ? selected : AnalysisView.Magnitude;
        PlotXMinimum = PlotXMaximum = PlotYMinimum = PlotYMaximum = double.NaN;
        PlotMarkers = null;

        if (view == AnalysisView.NoiseShaping)
        {
            if (_noise is null)
            {
                PlotSeries = null;
                return;
            }

            PlotXScale = PlotAxisScale.Logarithmic;
            PlotXFormat = PlotAxisFormat.Frequency;
            PlotYFormat = PlotAxisFormat.Decibels;
            PlotYMinimum = Math.Max(-220, Math.Floor(_noise.MagnitudeDb.Min() / 20) * 20);
            PlotYMaximum = 20;
            PlotSeries = [new PlotSeries("Noise transfer", _noise.Frequencies, _noise.MagnitudeDb, Palette.AccentDsd)];
            PlotMarkers = [new PlotMarker(20_000, "20 kHz", Palette.Warning)];
            AnalysisSummary = _noise.Summary;
            return;
        }

        if (_analysis is not { } analysis)
        {
            PlotSeries = null;
            AnalysisSummary = _analysisMessage ?? string.Empty;
            return;
        }

        AnalysisSummary = _analysisMessage ?? analysis.Summary;
        double sourceNyquist = analysis.InputRate / 2.0;
        switch (view)
        {
            case AnalysisView.Magnitude:
                PlotXScale = PlotAxisScale.Linear;
                PlotXFormat = PlotAxisFormat.Frequency;
                PlotYFormat = PlotAxisFormat.Decibels;
                PlotYMinimum = -Math.Clamp((FocusedFilter?.Preset.AttenuationDb ?? 120) + 40, 100, 280);
                PlotYMaximum = 10;
                PlotSeries = [new PlotSeries("Magnitude", analysis.Magnitude.Frequencies, analysis.Magnitude.MagnitudeDb, Palette.Accent)];
                PlotMarkers = [new PlotMarker(sourceNyquist, "source fs/2", Palette.Warning)];
                break;

            case AnalysisView.Passband:
                PlotXScale = PlotAxisScale.Linear;
                PlotXFormat = PlotAxisFormat.Frequency;
                PlotYFormat = PlotAxisFormat.Plain;
                PlotXMinimum = 0;
                PlotXMaximum = sourceNyquist * 1.1;
                PlotYMinimum = -3;
                PlotYMaximum = 0.5;
                PlotSeries = [new PlotSeries("Magnitude (dB)", analysis.Magnitude.Frequencies, analysis.Magnitude.MagnitudeDb, Palette.Accent)];
                PlotMarkers = [new PlotMarker(20_000, "20 kHz", Palette.TextSecondary), new PlotMarker(sourceNyquist, "fs/2", Palette.Warning)];
                break;

            case AnalysisView.Impulse:
                PlotXScale = PlotAxisScale.Linear;
                PlotXFormat = PlotAxisFormat.Milliseconds;
                PlotYFormat = PlotAxisFormat.Plain;
                PlotSeries = [new PlotSeries("Impulse", analysis.TimeMilliseconds, Normalize(analysis.Impulse), Palette.Accent, 1.3)];
                PlotMarkers = [new PlotMarker(0, "peak", Palette.TextSecondary)];
                break;

            case AnalysisView.Step:
                PlotXScale = PlotAxisScale.Linear;
                PlotXFormat = PlotAxisFormat.Milliseconds;
                PlotYFormat = PlotAxisFormat.Plain;
                PlotSeries = [new PlotSeries("Step", analysis.TimeMilliseconds, analysis.Step, Palette.Accent)];
                PlotMarkers = [new PlotMarker(0, "peak", Palette.TextSecondary)];
                break;
        }
    }

    private static double[] Normalize(double[] values)
    {
        double peak = values.Length == 0 ? 0 : values.Max(Math.Abs);
        return peak > 0 ? values.Select(v => v / peak).ToArray() : values;
    }

    private sealed record RefreshResult(IReadOnlyList<PlanPreviewRow> Rows, FilterAnalysisResult? Analysis, string Conversion, string? Message, NoiseCurve Noise);

    private sealed record NoiseCurve(double[] Frequencies, double[] MagnitudeDb, string Summary)
    {
        public static NoiseCurve From(NoiseTransferFunction ntf, double sampleRate, string title)
        {
            const int points = 700;
            const double lowest = 10.0;
            double highest = sampleRate / 2.0;
            var frequencies = new double[points];
            var magnitude = new double[points];
            for (int i = 0; i < points; i++)
            {
                double hz = lowest * Math.Pow(highest / lowest, i / (points - 1.0));
                frequencies[i] = hz;
                magnitude[i] = 20.0 * Math.Log10(Math.Max(1e-15, ntf.Evaluate(Math.Min(hz / sampleRate, 0.4999999)).Magnitude));
            }

            const int bandPoints = 400;
            double power = 0.0;
            for (int i = 0; i < bandPoints; i++)
            {
                double hz = 20.0 + (20_000.0 - 20.0) * (i + 0.5) / bandPoints;
                double m = ntf.Evaluate(hz / sampleRate).Magnitude;
                power += m * m;
            }

            double inBandDb = 10.0 * Math.Log10(Math.Max(1e-30, power / bandPoints));
            string summary = $"{title}: noise between 20 Hz and 20 kHz is {Formatting.Db(inBandDb, "0")} relative to unshaped quantization; the rise above the band is capped at a gain of {ntf.MaxGain.ToString("0.00", CultureInfo.CurrentCulture)}.";
            return new NoiseCurve(frequencies, magnitude, summary);
        }
    }
}
