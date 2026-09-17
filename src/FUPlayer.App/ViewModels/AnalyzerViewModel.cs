using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FUPlayer.App.Controls;
using FUPlayer.App.Services;
using FUPlayer.Core.Audio;
using FUPlayer.Core.Dsp.Analysis;
using FUPlayer.Core.Engine;
using FUPlayer.Core.Settings;

namespace FUPlayer.App.ViewModels;

/// <summary>One entry of the analyzer colour legend.</summary>
public sealed record LegendItem(string Label, string Name, IBrush Brush);

/// <summary>
/// Options and data flow of the Now Playing analyzer: which signal and channels are analysed, with which window and
/// resolution, and how the result is drawn. Spectra are computed on a worker thread and handed to the view as frames.
/// </summary>
public sealed partial class AnalyzerViewModel : ObservableObject
{
    private const string MixKey = "mix";
    private const string StereoKey = "stereo";
    private const string AllKey = "all";
    private const string IdleSummary = "Start playback to analyse the signal.";

    private readonly PlayerServices _services;
    private readonly bool _ready;
    private bool _suspend;
    private int _channelCount;
    private MeterHub? _hub;
    private bool _busy;
    private bool _idle = true;
    private int _generation;
    private long _lastComputeTicks;
    private string _legendKey = string.Empty;
    private double[][] _magnitudes = [];
    private double[][] _averages = [];
    private bool _averagesValid;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSpectrum), nameof(IsSpectrogram), nameof(IsWaterfall))]
    private Choice? _selectedView;

    [ObservableProperty]
    private Choice? _selectedSignal;

    [ObservableProperty]
    private Choice? _selectedChannels;

    [ObservableProperty]
    private Choice? _selectedWindow;

    [ObservableProperty]
    private Choice? _selectedFftSize;

    [ObservableProperty]
    private Choice? _selectedBand;

    [ObservableProperty]
    private Choice? _selectedScale;

    [ObservableProperty]
    private Choice? _selectedFloor;

    [ObservableProperty]
    private Choice? _selectedAveraging;

    [ObservableProperty]
    private Choice? _selectedPeakHold;

    [ObservableProperty]
    private bool _showOptions;

    [ObservableProperty]
    private string _summary = IdleSummary;

    public AnalyzerViewModel(PlayerServices services)
    {
        _services = services;
        AnalyzerSettings s = services.Settings.Analyzer;
        SelectedView = Choice.Find(ViewChoices, s.View) ?? ViewChoices[0];
        SelectedSignal = Choice.Find(SignalChoices, s.Signal) ?? SignalChoices[0];
        SelectedWindow = Choice.Find(WindowChoices, s.Window) ?? WindowChoices[4];
        SelectedFftSize = Choice.Find(FftSizeChoices, SpectrumAnalyzer.NormalizeFftSize(s.FftSize)) ?? FftSizeChoices[3];
        SelectedBand = Choice.Find(BandChoices, s.BandHz) ?? BandChoices[0];
        SelectedScale = Choice.Find(ScaleChoices, s.Scale) ?? ScaleChoices[0];
        SelectedFloor = Choice.Find(FloorChoices, s.FloorDb) ?? FloorChoices[2];
        SelectedAveraging = Choice.Find(AveragingChoices, s.Averaging) ?? AveragingChoices[1];
        SelectedPeakHold = Choice.Find(PeakHoldChoices, s.PeakHold) ?? PeakHoldChoices[0];
        EnsureChannelChoices(2);
        _ready = true;
    }

    /// <summary>Raised on the UI thread with a new set of spectra.</summary>
    public event Action<AnalyzerFrame>? FrameReady;

    /// <summary>Raised when displays should forget what they show (playback stopped, or the layout changed).</summary>
    public event Action? Cleared;

    public IReadOnlyList<Choice> ViewChoices { get; } =
    [
        new("Spectrum", AnalyzerView.Spectrum),
        new("Spectrogram", AnalyzerView.Spectrogram),
        new("Waterfall", AnalyzerView.Waterfall),
    ];

    public IReadOnlyList<Choice> SignalChoices { get; } =
    [
        new("Source file", AnalyzerSignal.Source, "The decoded file, before volume, filters and rate conversion."),
        new("Processed output", AnalyzerSignal.Output, "After volume, filters and rate conversion, just before dither or delta-sigma modulation."),
    ];

    public ObservableCollection<Choice> ChannelChoices { get; } = [];

    public IReadOnlyList<Choice> WindowChoices { get; } =
    [
        new("Rectangular", SpectrumWindow.Rectangular, "No window: the narrowest peaks, but leakage (−13 dB side lobes) hides quiet detail."),
        new("Hann", SpectrumWindow.Hann, "General-purpose window, side lobes at −31 dB."),
        new("Hamming", SpectrumWindow.Hamming, "Lower first side lobe than Hann (−43 dB) but slower fall-off further away."),
        new("Blackman", SpectrumWindow.Blackman, "Wider peaks, side lobes at −58 dB."),
        new("Blackman-Harris", SpectrumWindow.BlackmanHarris, "Four-term window with side lobes at −92 dB: shows low-level noise next to loud tones."),
        new("Flat top", SpectrumWindow.FlatTop, "Very wide peaks, but tone levels read correctly even between bins."),
        new("Kaiser (β = 9)", SpectrumWindow.Kaiser, "Side lobes near −66 dB with narrower peaks than Blackman-Harris."),
    ];

    public IReadOnlyList<Choice> FftSizeChoices { get; } =
        new[] { 1024, 2048, 4096, 8192, 16384, 32768, 65536 }
            .Select(n => new Choice(n.ToString("N0", CultureInfo.InvariantCulture) + " points", n))
            .ToArray();

    public IReadOnlyList<Choice> BandChoices { get; } =
    [
        new("Everything", 0.0, "Up to half the sample rate of the analysed signal."),
        new("Audio band (20 kHz)", 20_000.0),
        Band(44_100),
        Band(48_000),
        Band(88_200),
        Band(96_000),
        Band(176_400),
        Band(192_000),
        Band(352_800),
        Band(384_000),
        Band(705_600),
        Band(768_000),
        Band(1_411_200),
        Band(1_536_000),
    ];

    public IReadOnlyList<Choice> ScaleChoices { get; } =
    [
        new("Logarithmic", FrequencyScale.Logarithmic),
        new("Linear", FrequencyScale.Linear),
    ];

    public IReadOnlyList<Choice> FloorChoices { get; } =
        new[] { -90.0, -120.0, -140.0, -160.0, -180.0, -200.0 }
            .Select(db => new Choice(AnalyzerDrawing.DbLabel(db) + " dB", db))
            .ToArray();

    public IReadOnlyList<Choice> PeakHoldChoices { get; } =
    [
        new("Off", AnalyzerPeakHold.None),
        new("Falling", AnalyzerPeakHold.Falling, "Holds each peak for a moment, then lets it fall back."),
        new("Hold", AnalyzerPeakHold.Infinite, "Keeps every peak until the display is cleared."),
    ];

    public IReadOnlyList<Choice> AveragingChoices { get; } =
    [
        new("Off", AnalyzerAveraging.None),
        new("Light", AnalyzerAveraging.Light),
        new("Heavy", AnalyzerAveraging.Heavy),
    ];

    /// <summary>Colour key for the traces currently shown.</summary>
    public ObservableCollection<LegendItem> Legend { get; } = [];

    public bool IsSpectrum => SelectedView?.Value is null or AnalyzerView.Spectrum;

    public bool IsSpectrogram => SelectedView?.Value is AnalyzerView.Spectrogram;

    public bool IsWaterfall => SelectedView?.Value is AnalyzerView.Waterfall;

    private AnalyzerSettings Settings => _services.Settings.Analyzer;

    /// <summary>Called by the shell's status timer on the UI thread.</summary>
    /// <param name="visible">Whether the analyzer is on screen; analyzers that nobody watches stop collecting samples.</param>
    public void Update(MeterHub? hub, PlaybackPlan? plan, bool visible, bool playing)
    {
        if (hub is null)
        {
            _hub = null;
            if (!_idle)
            {
                _idle = true;
                _generation++;
                Summary = IdleSummary;
                SetLegend([]);
                Cleared?.Invoke();
            }

            return;
        }

        AnalyzerSettings options = Settings;
        SpectrumAnalyzer? output = hub.OutputSpectrum;
        bool useOutput = options.Signal == AnalyzerSignal.Output && output is not null;
        SpectrumAnalyzer analyzer = useOutput ? output! : hub.Spectrum;

        // Both are taken no faster than the band shown needs, so neither spends its bins above it.
        hub.Spectrum.SetAnalysisBand(options.BandHz);
        output?.SetAnalysisBand(options.BandHz);
        hub.Spectrum.IsEnabled = visible && !useOutput;
        if (output is not null)
        {
            output.IsEnabled = visible && useOutput;
        }

        if (!ReferenceEquals(hub, _hub))
        {
            _hub = hub;
            _generation++;
            _averagesValid = false;
        }

        EnsureChannelChoices(analyzer.Channels);
        if (!visible || !playing || _busy)
        {
            return;
        }

        // The processed output runs at several times the file's rate. Taken with as many points as the file, its bins
        // would be that many times wider and its window that much shorter: the low octaves drawn as a few blocks, and
        // every frame a different slice of music. It gets the points that give it the file's resolution instead, up
        // to four times as many, which every band but the whole of a DSD stream's needs no more than.
        int requested = SpectrumAnalyzer.NormalizeFftSize(options.FftSize);
        int fftSize = useOutput
            ? Math.Min(SpectrumAnalyzer.MatchedFftSize(requested, analyzer.AnalysisRate, hub.Spectrum.AnalysisRate), 4 * requested)
            : requested;

        long now = Environment.TickCount64;
        if (now - _lastComputeTicks < RefreshMilliseconds(requested))
        {
            return;
        }

        _lastComputeTicks = now;
        TraceSpec[] traces = BuildTraces(analyzer.Channels, options);
        int bins = SpectrumAnalyzer.BinCount(fftSize);
        if (_magnitudes.Length != traces.Length || (_magnitudes.Length > 0 && _magnitudes[0].Length != bins))
        {
            _magnitudes = Allocate(traces.Length, bins);
            _averages = Allocate(traces.Length, bins);
            _averagesValid = false;
        }

        double alpha = options.Averaging switch
        {
            AnalyzerAveraging.None => 1.0,
            AnalyzerAveraging.Light => 0.5,
            _ => 0.18,
        };
        bool blend = alpha < 1.0 && _averagesValid;
        SpectrumWindow window = options.Window;
        (double markerHz, string? markerLabel) = Marker(plan, useOutput);
        var frame = new AnalyzerFrame(
            traces.Select((t, i) => new AnalyzerTrace(t.Label, t.Color, _magnitudes[i])).ToArray(),
            analyzer.AnalysisRate,
            options.BandHz,
            options.Scale,
            options.FloorDb,
            markerHz,
            markerLabel,
            options.PeakHold);
        string summary = Describe(options, useOutput, output is not null, analyzer.SampleRate, analyzer.AnalysisRate, fftSize);
        int generation = _generation;
        double[][] magnitudes = _magnitudes;
        double[][] averages = _averages;
        _busy = true;

        _ = Task.Run(() =>
        {
            bool computed = false;
            try
            {
                computed = Compute(analyzer, traces, fftSize, window, magnitudes, averages, alpha, blend);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                computed = false;
            }
            finally
            {
                Dispatcher.UIThread.Post(() =>
                {
                    _busy = false;
                    if (!computed || generation != _generation)
                    {
                        return;
                    }

                    _averagesValid = alpha < 1.0;
                    _idle = false;
                    Summary = summary;
                    SetLegend(traces);
                    FrameReady?.Invoke(frame);
                });
            }
        });
    }

    partial void OnSelectedViewChanged(Choice? value) => Apply(value, (AnalyzerView v) => Settings.View = v, clear: true);

    partial void OnSelectedSignalChanged(Choice? value) => Apply(value, (AnalyzerSignal v) => Settings.Signal = v, clear: true);

    partial void OnSelectedChannelsChanged(Choice? value) => Apply(value, (string key) =>
    {
        switch (key)
        {
            case MixKey:
                Settings.Channels = AnalyzerChannels.Mono;
                break;
            case StereoKey:
                Settings.Channels = AnalyzerChannels.Stereo;
                break;
            case AllKey:
                Settings.Channels = AnalyzerChannels.All;
                break;
            default:
                Settings.Channels = AnalyzerChannels.Single;
                Settings.ChannelIndex = int.Parse(key, CultureInfo.InvariantCulture);
                break;
        }
    }, clear: true);

    partial void OnSelectedWindowChanged(Choice? value) => Apply(value, (SpectrumWindow v) => Settings.Window = v, clear: false);

    partial void OnSelectedFftSizeChanged(Choice? value) => Apply(value, (int v) => Settings.FftSize = v, clear: true);

    partial void OnSelectedBandChanged(Choice? value) => Apply(value, (double v) => Settings.BandHz = v, clear: true);

    partial void OnSelectedScaleChanged(Choice? value) => Apply(value, (FrequencyScale v) => Settings.Scale = v, clear: true);

    partial void OnSelectedFloorChanged(Choice? value) => Apply(value, (double v) => Settings.FloorDb = v, clear: true);

    partial void OnSelectedAveragingChanged(Choice? value) => Apply(value, (AnalyzerAveraging v) => Settings.Averaging = v, clear: false);

    // Clearing drops the held peaks, which is what switching the mode is asking for.
    partial void OnSelectedPeakHoldChanged(Choice? value) => Apply(value, (AnalyzerPeakHold v) => Settings.PeakHold = v, clear: true);

    [RelayCommand]
    private void ToggleOptions() => ShowOptions = !ShowOptions;

    private static Choice Band(int rate) =>
        new($"{AudioRates.Format(rate)} rate · to {AudioRates.Format(rate / 2)}", rate / 2.0, $"Shows 0 Hz up to {AudioRates.Format(rate / 2)}, the highest frequency a {AudioRates.Format(rate)} file can hold.");

    private static int RefreshMilliseconds(int fftSize) => fftSize switch
    {
        <= 8192 => 33,
        <= 16384 => 50,
        <= 32768 => 80,
        <= 65536 => 130,
        _ => 250,
    };

    private static double[][] Allocate(int count, int length)
    {
        var arrays = new double[count][];
        for (int i = 0; i < count; i++)
        {
            arrays[i] = new double[length];
        }

        return arrays;
    }

    private static bool Compute(SpectrumAnalyzer analyzer, TraceSpec[] traces, int fftSize, SpectrumWindow window, double[][] magnitudes, double[][] averages, double alpha, bool blend)
    {
        for (int i = 0; i < traces.Length; i++)
        {
            if (!analyzer.TryCompute(traces[i].Channels, fftSize, window, magnitudes[i]))
            {
                return false;
            }
        }

        if (alpha >= 1.0)
        {
            return true;
        }

        // Exponential averaging of power, so quiet bins are not dragged down by averaging decibels.
        for (int i = 0; i < traces.Length; i++)
        {
            double[] db = magnitudes[i];
            double[] power = averages[i];
            for (int k = 0; k < db.Length; k++)
            {
                double p = Math.Pow(10.0, db[k] / 10.0);
                power[k] = blend ? power[k] + alpha * (p - power[k]) : p;
                db[k] = 10.0 * Math.Log10(Math.Max(power[k], 1e-30));
            }
        }

        return true;
    }

    private static TraceSpec[] BuildTraces(int channels, AnalyzerSettings options)
    {
        if (channels <= 1)
        {
            return [new TraceSpec([0], "M", "Mono", Palette.Accent)];
        }

        return options.Channels switch
        {
            AnalyzerChannels.Mono => [new TraceSpec(Enumerable.Range(0, channels).ToArray(), "Mix", channels == 2 ? "Left and right mixed" : "All channels mixed", Palette.Accent)],
            AnalyzerChannels.Single => [Channel(Math.Clamp(options.ChannelIndex, 0, channels - 1), channels)],
            AnalyzerChannels.Stereo => [Channel(0, channels), Channel(1, channels)],
            _ => Enumerable.Range(0, channels).Select(c => Channel(c, channels)).ToArray(),
        };
    }

    private static TraceSpec Channel(int channel, int channels) => new(
        [channel],
        MeterChannelViewModel.LabelFor(channel, channels),
        channels == 2 ? (channel == 0 ? "Left" : "Right") : $"Channel {channel + 1}",
        Palette.ChannelColor(channel));

    private static (double Hz, string? Label) Marker(PlaybackPlan? plan, bool output)
    {
        if (plan is null || plan.Source.IsDsd)
        {
            return (0, null);
        }

        int rate = plan.Source.SampleRate;
        if (output)
        {
            // Everything above the file's own Nyquist frequency was created by the conversion.
            return (rate / 2.0, "source fs/2");
        }

        if (rate > 50_000)
        {
            // Where a 44.1 or 48 kHz master would end: content above it shows the file is more than an upsampled copy.
            int family = AudioRates.FamilyBase(rate);
            return (family / 2.0, $"{AudioRates.FormatShort(family)} fs/2");
        }

        return (0, null);
    }

    private static string ChannelKey(AnalyzerSettings options) => options.Channels switch
    {
        AnalyzerChannels.Mono => MixKey,
        AnalyzerChannels.Stereo => StereoKey,
        AnalyzerChannels.All => AllKey,
        _ => options.ChannelIndex.ToString(CultureInfo.InvariantCulture),
    };

    private void Apply<T>(Choice? choice, Action<T> apply, bool clear)
    {
        if (!_ready || _suspend || choice?.Value is not T value)
        {
            return;
        }

        apply(value);
        _generation++;
        _averagesValid = false;
        _lastComputeTicks = 0;
        if (clear)
        {
            Cleared?.Invoke();
        }

        _services.NotifySettingsChanged(applyToEngine: false);
    }

    private string Describe(AnalyzerSettings options, bool useOutput, bool outputAvailable, int streamRate, int sampleRate, int fftSize)
    {
        string signal = useOutput
            ? "Processed output"
            : options.Signal == AnalyzerSignal.Output && !outputAvailable
                ? "Source file (DSD pass-through has no processed output)"
                : "Source file";
        string window = Choice.Find(WindowChoices, options.Window)?.Label ?? options.Window.ToString();
        double resolution = (double)sampleRate / fftSize;
        string perBin = resolution switch
        {
            < 10 => resolution.ToString("0.0", CultureInfo.CurrentCulture) + " Hz",
            < 1000 => resolution.ToString("0", CultureInfo.CurrentCulture) + " Hz",
            _ => (resolution / 1000).ToString("0.0", CultureInfo.CurrentCulture) + " kHz",
        };
        string rate = streamRate == sampleRate
            ? AudioRates.Format(sampleRate)
            : $"{AudioRates.Format(streamRate)}, analysed at {AudioRates.Format(sampleRate)}";
        return $"{signal}  ·  {rate}  ·  {window} window  ·  {fftSize.ToString("N0", CultureInfo.InvariantCulture)}-point FFT, {perBin} per bin";
    }

    private void EnsureChannelChoices(int channels)
    {
        channels = Math.Max(1, channels);
        if (channels == _channelCount)
        {
            return;
        }

        _channelCount = channels;
        _suspend = true;
        try
        {
            ChannelChoices.Clear();
            if (channels == 1)
            {
                ChannelChoices.Add(new Choice("Mono", MixKey));
            }
            else
            {
                ChannelChoices.Add(new Choice(channels == 2 ? "Mix of both" : "Mix of all", MixKey));
                ChannelChoices.Add(new Choice(channels == 2 ? "Left and right" : "Front left and right", StereoKey));
                if (channels > 2)
                {
                    ChannelChoices.Add(new Choice("Every channel", AllKey));
                }

                for (int c = 0; c < channels; c++)
                {
                    string name = channels == 2
                        ? c == 0 ? "Left only" : "Right only"
                        : $"{MeterChannelViewModel.LabelFor(c, channels)} only (channel {c + 1})";
                    ChannelChoices.Add(new Choice(name, c.ToString(CultureInfo.InvariantCulture)));
                }
            }

            SelectedChannels = Choice.Find(ChannelChoices, ChannelKey(Settings))
                ?? Choice.Find(ChannelChoices, StereoKey)
                ?? ChannelChoices[0];
        }
        finally
        {
            _suspend = false;
        }
    }

    private void SetLegend(IReadOnlyList<TraceSpec> traces)
    {
        string key = string.Join("|", traces.Select(t => $"{t.Label}:{t.Color}"));
        if (key == _legendKey)
        {
            return;
        }

        _legendKey = key;
        Legend.Clear();
        foreach (TraceSpec trace in traces)
        {
            Legend.Add(new LegendItem(trace.Label, trace.Name, new ImmutableSolidColorBrush(trace.Color)));
        }
    }

    private sealed record TraceSpec(int[] Channels, string Label, string Name, Color Color);
}
