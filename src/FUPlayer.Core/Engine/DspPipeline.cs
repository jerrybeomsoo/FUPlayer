using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using FUPlayer.Core.Audio;
using FUPlayer.Core.Dsp.Acceleration;
using FUPlayer.Core.Dsp.Analysis;
using FUPlayer.Core.Dsp.Design;
using FUPlayer.Core.Dsp.Dsd;
using FUPlayer.Core.Dsp.Modulation;
using FUPlayer.Core.Dsp.Numerics;
using FUPlayer.Core.Dsp.Processing;
using FUPlayer.Core.Dsp.Quantization;
using FUPlayer.Core.Dsp.Resampling;
using FUPlayer.Core.Dsp.Restoration;
using FUPlayer.Core.Localization;
using FUPlayer.Core.NetworkDsp;
using FUPlayer.Core.Output;
using FUPlayer.Core.Settings;

namespace FUPlayer.Core.Engine;

/// <summary>
/// Executes a <see cref="PlaybackPlan"/>: DSD→PCM conversion and the source's meters and detectors; the processing
/// core (restorer, channel map, upscaler, exciter, filter), here or partly on a DSP server; then per output channel
/// volume, limiter, speaker trims, and dither or delta-sigma modulation; finally interleaves the canonical stream.
///
/// With a DSP server the core is cut in up to three: the stages in front of the remote ones run here as a block is
/// read, the block goes to the server, and its result comes back a few round trips later to go through the stages
/// after them and the output's own processing. Several blocks are in flight at once, so the server is never left
/// waiting on the network; the output written to the FIFO lags what was read by that much.
/// </summary>
internal sealed class DspPipeline : IDisposable
{
    private const double BlockSeconds = 0.02;
    private const double VolumeRampSeconds = 0.03;
    private const int MaxModulatorSamplesPerChunk = 1 << 20;
    private const double SpeedOfSound = 343.0;

    /// <summary>
    /// How long a second of audio counts towards where the exciter takes the source to end: long enough for the codec
    /// detector's steepest fall to stand out of a song's own variation, short enough to follow a new source in a live
    /// stream within a few seconds.
    /// </summary>
    private const double ExciterMemorySeconds = 3.0;

    private readonly PlaybackPlan _plan;
    private readonly ParallelWorkers _workers;
    private readonly CoreLayout _layout;
    private readonly ProcessingCore? _prefix;
    private readonly ProcessingCore? _suffix;
    private readonly DspRemoteSegment? _remote;
    private readonly CoreBlock _entry;
    private readonly CoreBlock _result;
    private readonly Tail[] _tails = [];
    private readonly string _accelerationSummary = string.Empty;
    private readonly CodecBandwidthDetector? _detector;

    /// <summary>
    /// The one output channel allowed to feed the detector. Channels are processed in parallel and
    /// the detector keeps a single fill counter, so letting every channel push into it corrupts that
    /// counter and eventually walks off the end of its buffer.
    /// </summary>
    private readonly int _detectorChannel = -1;
    private readonly NeuralUpscalerModel? _upscalerModel;
    private readonly INeuralRestorer? _restorer;
    private BandwidthEstimate _bandwidth;

    /// <summary>
    /// Where the source ends over the last few seconds rather than since the start, for the exciter: a live stream that
    /// changes source, or a codec whose bandwidth moves with the music, moves its band with it.
    /// </summary>
    private readonly CodecBandwidthDetector? _edgeTracker;

    private BandwidthEstimate _recentBandwidth;
    private readonly ResamplerChain? _modulatorChain;
    private readonly int _sourceChannels;
    private readonly int _outputChannels;
    private readonly bool _meterAdjustedSource;
    private readonly double _levelOffset;
    private readonly int _modulatorAlignment;
    private readonly int _modulatorInputAlignment;
    private readonly bool _modulatorDoublesRate;
    private readonly int _rampSamples;
    private readonly int _flushInputSamples;
    private readonly DopEncoder _dop = new();
    private readonly GpuAccelerator? _accelerator;
    private readonly byte[] _silentDsd;
    private readonly int _restorerLatency;
    private readonly int _upscalerLatency;
    private byte[] _pending = [];
    private int[] _dopFrames = [];
    private double[][]? _silentPcm;
    private double _volume = 1.0;
    private double _replayGain = 1.0;
    private int _pendingOffset;
    private int _pendingLength;
    private double _waitedSeconds;

    /// <summary>
    /// The replay gain and the exciter's edges of each block in flight, as they were when it was read: a map or an
    /// exciter after the server's stages takes the ones its block was sent with, as it would have had it all run here.
    /// </summary>
    private readonly Queue<(double Gain, double Edge, double Top, double Wall)> _inFlightEdges = new();

    // The meters' clock: seconds of source read and of output made since the start or the last seek, and where in the
    // FIFO this pipeline's output began, so that what the device has played can be told in seconds.
    private double _sourceSeconds;
    private double _outputSeconds;
    private double _sourceMeterTime;
    private OutputWriter? _clockWriter;
    private long _ringStart;
    private long _discardStart;

    public DspPipeline(PlaybackPlan plan, PlayerSettings settings, ParallelWorkers workers, CancellationToken cancellation = default)
    {
        _plan = plan;
        _workers = workers;
        _sourceChannels = plan.Source.Channels;
        _outputChannels = plan.Output.Channels;
        _meterAdjustedSource = settings.Processing.MeterAdjustedSource;
        _levelOffset = plan.IsDsdOutput ? 1.0 : Math.Pow(10.0, plan.PcmLevelOffsetDb / 20.0);
        _modulatorAlignment = plan.Output.Kind == OutputSampleKind.Dop ? 16 : 8;
        _rampSamples = Math.Max(1, (int)(plan.ProcessingRate * VolumeRampSeconds));

        int meterRate = plan.Source.IsDsd && plan.PassThrough ? plan.Source.SampleRate / 16 : plan.ConversionRate;
        Meters = new MeterHub(_sourceChannels, meterRate, _outputChannels, plan.PassThrough ? 0 : plan.ProcessingRate);

        if (plan.Source.IsDsd)
        {
            int bytes = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(512, plan.Source.SampleRate / 8 * BlockSeconds)) / 2;
            InputBlock = Math.Clamp(bytes, 512, 1 << 16);
        }
        else
        {
            int frames = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(256, plan.Source.SampleRate * BlockSeconds)) / 2;
            InputBlock = Math.Clamp(frames, 256, 16384);
        }

        _silentDsd = new byte[InputBlock + 2];
        Array.Fill(_silentDsd, DsdConstants.SilenceByte);
        RestorationSettings restore = settings.Restoration;
        ProcessingSettings processing = settings.Processing;
        bool network = processing.NetworkDsp && !plan.PassThrough;
        bool remoteRestorer = network && processing.RemoteRestorer && plan.Restore;
        bool remoteUpscaler = network && processing.RemoteUpscaler && plan.UpscaleRate > 0;
        bool remoteFilter = network && processing.RemoteResampling;

        try
        {
            // The networks that run here are opened first: whether they could be is part of what the core is.
            if (plan.UpscaleRate > 0 && !remoteUpscaler)
            {
                string device = restore.UpscalerDevice;
                _upscalerModel = ModelLibrary.TryLoadUpscaler(restore.NeuralUpscalerPath, device, out string? upscalerFailure);
                UpscalerStatus = _upscalerModel is null
                    ? Loc.F("Neural upscaler not running: {0}", upscalerFailure)
                    : (plan.Restore
                        ? Loc.F("Neural upscaler: {0} at {1}, after the restorer", Path.GetFileName(_upscalerModel.Path), AudioRates.Format(plan.UpscaleRate))
                        : Loc.F("Neural upscaler: {0} at {1}", Path.GetFileName(_upscalerModel.Path), AudioRates.Format(plan.UpscaleRate)))
                      + ". " + InferenceDevices.Describe(device, _upscalerModel.Adapter, _upscalerModel.AdapterFailure);
            }

            if (plan.Restore && !remoteRestorer)
            {
                string device = restore.RestorerDevice;
                NeuralRestorerModel? restorerModel = ModelLibrary.TryLoadRestorer(restore.NeuralRestorerPath, device, out string? restorerFailure);
                if (restorerModel is null)
                {
                    RestorerStatus = Loc.F("Neural restorer not running: {0}", restorerFailure);
                }
                else
                {
                    var restorer = new NeuralRestorer(restorerModel, plan.Source.SampleRate);
                    _restorer = restorer;
                    RestorerStatus = Loc.F(
                        "Neural restorer: {0} at {1}, {2:0} ms", Path.GetFileName(restorerModel.Path), AudioRates.Format(plan.Source.SampleRate),
                        1000.0 * restorer.Latency / plan.Source.SampleRate)
                        + ". " + InferenceDevices.Describe(device, restorerModel.Adapter, restorerModel.AdapterFailure);
                }
            }

            bool restoring = remoteRestorer || _restorer is not null;
            bool upscaling = remoteUpscaler || _upscalerModel is not null;
            int exciteRate = plan.PassThrough ? 0 : plan.ExciteRate;
            _layout = new CoreLayout
            {
                SourceChannels = _sourceChannels,
                OutputChannels = _outputChannels,
                SourceRate = plan.ConversionRate,
                UpsampleRate = plan.PassThrough ? 0 : plan.PreUpsampleRate,
                ProcessingRate = plan.ProcessingRate,
                Restore = restoring,
                Upscale = upscaling,
                ExciteRate = exciteRate,
                ExciterLatency = exciteRate > 0 ? new HarmonicExciter(exciteRate, plan.ConversionRate).Latency : 0,
                Delta = restore.OutputDelta && (restoring || upscaling || exciteRate > 0),
                SilentDelta = restore.OutputDelta && (restore.NeuralUpscaler || restore.NeuralRestorer || restore.Exciter)
                    && !upscaling && !restoring && exciteRate == 0,
                Clean = plan.RemoveUltrasonics,
                DsdSource = plan.Source.IsDsd,
                Filter = !plan.PassThrough,
                Lossy = plan.SourceIsLossy,
                UpscalerBandDb = restore.UpscalerBandDb,
            };

            // The stages handed to the server are the run from the first chosen one to the last: those in between go
            // with them, as a block cannot cross the network more than once each way.
            CoreStage[] chosen = new[]
            {
                (remoteRestorer && _layout.Restore, CoreStage.Restore),
                (remoteUpscaler && _layout.Upscale, CoreStage.Upscale),
                (remoteFilter && _layout.Filter, CoreStage.Filter),
            }.Where(p => p.Item1).Select(p => p.Item2).ToArray();
            CoreStage remoteFirst = chosen.Length > 0 ? chosen.Min() : CoreStage.Filter;
            CoreStage remoteLast = chosen.Length > 0 ? chosen.Max() : CoreStage.Filter;
            if (chosen.Length > 0)
            {
                _remote = new DspRemoteSegment(plan, settings, _layout, remoteFirst, remoteLast, InputBlock * 2, cancellation);
                RemoteStages = Enumerable.Range((int)remoteFirst, remoteLast - remoteFirst + 1).Select(i => (CoreStage)i).Where(_layout.Has).ToArray();
                // Over the Internet the FIFO also has to ride out jitter that a simulation does not show: at least
                // 0.6 s in flight's worth, which the engine makes a FIFO of 1.5 s.
                NetworkBlockSeconds = _remote.RoundTripMilliseconds > 5 ? Math.Max(InFlightLimit(), 0.6) : InFlightLimit();
            }

            bool local(CoreStage stage) => _remote is null || stage < remoteFirst || stage > remoteLast;
            if (_remote is not null && _layout.Restore && !local(CoreStage.Restore))
            {
                RestorerStatus = Loc.F("Remote neural restorer: {0}", _remote.Metadata.Restorer);
            }

            if (_remote is not null && plan.UpscaleRate > 0 && !local(CoreStage.Upscale))
            {
                UpscalerStatus = _layout.Upscale ? Loc.F("Remote neural upscaler: {0}", _remote.Metadata.Upscaler) : UpscalerStatus;
            }

            ResamplerChain? resampler = plan.PassThrough || !local(CoreStage.Filter)
                ? null
                : ResamplerFactory.Create(plan.Filter!, ResamplerInputRate(plan), plan.ProcessingRate, plan.Staging, plan.FilterTaps, plan.Convolution, plan.ConvolutionOptions);

            // Channels run side by side, and a filter then divides its own work over whatever cores that leaves. Two
            // channels on an eight-core machine would otherwise use two cores and report a DSP load of several
            // hundred per cent while most of the processor stood still.
            int threadBudget = processing.DspThreads >= 0 ? Math.Max(1, processing.DspThreads + 1) : Environment.ProcessorCount;
            int stageParallelism = Math.Clamp(threadBudget / Math.Max(1, _outputChannels), 1, 16);

            // A graphics device is only worth waking for a long filter stage; everything else stays here.
            string acceleration;
            if (processing.GpuAcceleration && resampler is not null && resampler.Stages.Any(s => GpuAccelerator.CanAccelerate(s, processing.GpuForce)))
            {
                _accelerator = GpuAccelerator.TryCreate(
                    processing.GpuDeviceId, processing.GpuHighPrecision, out acceleration, processing.GpuForce,
                    processing.GpuPhaseShare, processing.GpuRetimeWhilePlaying);

                // Upload, check and race the filter before any channel is handed its state, so the first block of
                // music is not the one that pays for it.
                _accelerator?.PlanChannels(resampler.Stages, _outputChannels, stageParallelism);

                // The device takes the earlier channels whole, which leaves their threads with nothing to do. Letting
                // every channel draw on the whole budget rather than its own share of it is what puts those threads
                // behind the channels that are still convolving here; the pool still only has so many of them. Only
                // where the device took a filter at all, though: one that refused every one of them is not going to
                // free any thread, and letting every channel ask for the whole budget then just oversubscribes the
                // cores, which costs a 2,097,152-tap conversion about 13 per cent.
                if (_accelerator?.HasFilters == true)
                {
                    stageParallelism = Math.Clamp(threadBudget, 1, 16);
                }
            }
            else
            {
                int channelThreads = Math.Min(workers.Parallelism, _outputChannels);
                int total = channelThreads * stageParallelism;
                acceleration = total == 1
                    ? Loc.T("Filtering on one processor thread.")
                    : Loc.F("Filtering on {0} processor threads ({1} channels, {2} each).", total, channelThreads, stageParallelism);

                // Asked for a device and got none: say that the conversion is the reason, not the device, and say
                // which conversion it is, since a filter layered to answer sooner is a choice that can be taken back.
                if (processing.GpuAcceleration && resampler is not null)
                {
                    acceleration += resampler.Stages.Any(s => s is LayeredFftPolyphaseStage)
                        ? " " + Loc.T("The filter uses layered partitioning, which no graphics device accepts. Select one block length to allow offload.")
                        : " " + Loc.T("No stage in this conversion can be offloaded.");
                }
            }

            var parts = new CoreParts
            {
                Restorer = _restorer,
                UpscalerModel = _upscalerModel,
                Resampler = resampler,
                FilterParallelism = stageParallelism,
                Accelerator = _accelerator,
                Ultrasonic = plan.RemoveUltrasonics && local(CoreStage.Map) ? ProcessingFilters.CreateUltrasonicFilter(plan.ConversionRate) : null,
            };
            if (!plan.PassThrough)
            {
                CoreStage entry = _layout.Entry;
                _prefix = _remote is null
                    ? new ProcessingCore(_layout, entry, CoreStage.Filter, parts, workers)
                    : remoteFirst > entry ? new ProcessingCore(_layout, entry, remoteFirst - 1, parts, workers) : null;
                _suffix = _remote is not null && remoteLast < CoreStage.Filter ? new ProcessingCore(_layout, remoteLast + 1, CoreStage.Filter, parts, workers) : null;
                ProcessingCore? mapper = _prefix is not null && _prefix.Builds(CoreStage.Map) ? _prefix : null;
                if (mapper is not null && !_layout.Restore)
                {
                    mapper.MapTap = MeterMapped;
                }
            }

            _restorerLatency = _layout.Restore ? (_restorer?.Latency ?? _remote!.Metadata.RestorerLatency) : 0;
            _upscalerLatency = _layout.Upscale
                ? (_prefix?.UpscalerLatency is > 0 ? _prefix.UpscalerLatency : _suffix?.UpscalerLatency is > 0 ? _suffix.UpscalerLatency : _remote?.Metadata.UpscalerLatency ?? 0)
                : 0;

            DsdToPcmDesign? dsd = plan.Source.IsDsd
                ? DsdToPcmDesign.Create(
                    plan.Source.SampleRate,
                    meterRate,
                    plan.PassThrough ? DsdFilterCatalog.Get(null) : plan.DsdFilter!,
                    plan.PassThrough ? 2.0 : plan.DsdConversionGain)
                : null;
            ResamplerChain? ultrasonic = parts.Ultrasonic;
            _modulatorDoublesRate = ShouldFoldLastInterpolation(plan);
            _modulatorInputAlignment = _modulatorDoublesRate ? _modulatorAlignment / 2 : _modulatorAlignment;
            ResamplerChain? interpolator = plan.IsDsdOutput && !plan.PassThrough ? CreateModulatorInterpolator(plan, _modulatorDoublesRate) : null;
            _modulatorChain = interpolator;
            NoiseTransferFunction? ntf = plan.Modulator is not null && !plan.PassThrough ? ModulatorCatalog.DesignNtf(plan.Modulator, plan.Output.DsdRate) : null;
            (double[] gains, int[] delays) = ComputeSpeakerTrims(settings.Speakers, plan, _outputChannels);
            bool detectApodization = processing.ApodizationDetection && !plan.Source.IsDsd && plan.ConversionRate <= ApodizationDetector.MaximumSampleRate;

            // Where the source's spectrum ends, reported in Now playing while lossy repair is on and followed by the
            // exciter: measured on one channel of the source as it arrived, for the PCM rates the networks serve.
            if ((restore.NeuralUpscaler || plan.ExciteRate > 0) && !plan.Source.IsDsd && plan.ConversionRate <= 48_000)
            {
                _detector = new CodecBandwidthDetector(plan.ConversionRate);
                for (int c = 0; c < _outputChannels && _detectorChannel < 0; c++)
                {
                    if (SourceChannel(c) >= 0)
                    {
                        _detectorChannel = c;
                    }
                }
            }

            if (plan.ExciteRate > 0 && _detector is not null)
            {
                _edgeTracker = new CodecBandwidthDetector(plan.ConversionRate, ExciterMemorySeconds);
            }

            _entry = new CoreBlock(Math.Max(_sourceChannels, _outputChannels));
            _result = new CoreBlock(Math.Max(_sourceChannels, _outputChannels));
            _tails = new Tail[_outputChannels];
            for (int c = 0; c < _outputChannels; c++)
            {
                int source = SourceChannel(c);
                bool meters = source >= 0 && source == c;
                var tail = new Tail { TrimGain = gains[c] };
                if (dsd is not null && (!plan.PassThrough || meters))
                {
                    tail.DsdConverter = dsd.CreateState();
                    tail.Conversion = new double[tail.DsdConverter.MaxOutput(InputBlock + 2) + 16];
                }

                if (plan.PassThrough)
                {
                    tail.DsdOutput = new byte[InputBlock + 2];
                    tail.ByteDelay = delays[c] > 0 ? new ByteDelayLine(delays[c], DsdConstants.SilenceByte) : null;
                    _tails[c] = tail;
                    continue;
                }

                if (detectApodization && meters)
                {
                    tail.Apodization = new ApodizationDetector(plan.ConversionRate);
                }

                tail.Limiter = plan.Limiter ? new SoftLimiter(plan.ProcessingRate) : null;
                tail.Delay = delays[c] > 0 ? new DelayLine(delays[c]) : null;
                if (plan.IsDsdOutput)
                {
                    tail.ModulatorInterpolator = interpolator!.CreateState();
                    tail.Modulator = new DeltaSigmaModulator(ntf!);
                }
                else
                {
                    tail.Quantizer = new PcmQuantizer(plan.Dither!, plan.Output.ValidBits, plan.ProcessingRate, (ulong)(c + 1) * 0x9E3779B97F4A7C15UL);
                }

                _tails[c] = tail;
            }

            double latency = 0.0;
            if (dsd is not null && !plan.PassThrough)
            {
                latency += dsd.DelayOutputSamples / dsd.OutputRate;
            }

            if (ultrasonic is not null)
            {
                latency += ultrasonic.DelayOutputSamples / ultrasonic.OutputRate;
            }

            latency += (double)_restorerLatency / plan.Source.SampleRate;
            if (_layout.UpsampleRate > 0)
            {
                latency += (double)(TrainingUpsampler.Latency + _upscalerLatency) / _layout.UpsampleRate;
            }

            if (_layout.ExciteRate > 0)
            {
                latency += (double)_layout.ExciterLatency / _layout.ExciteRate;
            }

            double filterDelay = resampler?.DelayOutputSamples ?? _remote?.Metadata.FilterDelayOutputSamples ?? 0;
            int filterFlush = resampler?.FlushInputSamples ?? _remote?.Metadata.FilterFlushInputSamples ?? 0;
            int filterInputRate = ResamplerInputRate(plan);
            if (!plan.PassThrough)
            {
                latency += filterDelay / plan.ProcessingRate;
                latency += (double)(_tails[0].Limiter?.LatencySamples ?? 0) / plan.ProcessingRate;
            }

            if (interpolator is not null)
            {
                latency += interpolator.DelayOutputSamples / interpolator.OutputRate;
            }

            // Block-based filter stages hold input until a block is complete; flushing has to push that much extra.
            double sourceInputRate = plan.Source.IsDsd ? plan.Source.SampleRate / 8.0 : plan.Source.SampleRate;
            _flushInputSamples = (int)Math.Ceiling(filterFlush * (sourceInputRate / Math.Max(1, filterInputRate)));

            LatencySeconds = latency;
            bool remoteResampling = _remote is not null && !local(CoreStage.Filter) && !plan.PassThrough;
            FilterBlockSeconds = remoteResampling ? _remote!.Metadata.FilterBlockSeconds : BlockSecondsOf(resampler);
            ResamplerSummary = plan.PassThrough
                ? Loc.T("DSD passed through unchanged")
                : resampler?.Summary ?? _remote?.Metadata.Resampler ?? string.Empty;
            OperationsPerSecond = (resampler?.OperationsPerInputSecond ?? (remoteResampling ? _remote!.Metadata.FilterOperationsPerInputSecond : 0))
                + (interpolator?.OperationsPerInputSecond ?? 0);
            _accelerationSummary = remoteResampling
                ? Loc.F("Remote resampling: {0}", _remote!.Metadata.Acceleration)
                : _accelerator?.Status ?? acceleration;
        }
        catch
        {
            _remote?.Dispose();
            _accelerator?.Dispose();
            throw;
        }
    }

    /// <summary>Releases the accelerator's device memory and the server's session; the meters stay readable.</summary>
    public void Dispose()
    {
        _remote?.Dispose();
        _accelerator?.Dispose();
    }

    /// <summary>Closes a blocked server connection when the user stops playback.</summary>
    internal void CancelNetwork() => _remote?.Abort();

    /// <summary>Source units needed to emit the entire filter tail, including block buffering.</summary>
    internal long FlushInputUnits => _plan.PassThrough
        ? 0
        : (long)Math.Ceiling(LatencySeconds * (_plan.Source.IsDsd ? _plan.Source.SampleRate / 8.0 : _plan.Source.SampleRate)) + InputBlock + _flushInputSamples;

    /// <summary>
    /// FIFO a plan needs whatever the user asked for. A stage that convolves a block at a time holds that much
    /// input back and then produces all of it in one go, so nothing reaches the device while it is working and
    /// the buffer has to be able to play through the pause. Building the chain here is free: it is cached, and
    /// the pipeline is about to ask for the same one.
    /// </summary>
    public static double RequiredFifoSeconds(PlaybackPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.PassThrough || plan.Filter is null || plan.ConversionRate <= 0)
        {
            return 0.0;
        }

        ResamplerChain chain = ResamplerFactory.Create(
            plan.Filter, ResamplerInputRate(plan), plan.ProcessingRate, plan.Staging, plan.FilterTaps, plan.Convolution, plan.ConvolutionOptions);
        return 2.5 * BlockSecondsOf(chain);
    }

    /// <summary>What the chosen filter converts from: twice the source rate while the upscaler, or the exciter, runs there.</summary>
    private static int ResamplerInputRate(PlaybackPlan plan) => plan.PreUpsampleRate > 0 ? plan.PreUpsampleRate : plan.ConversionRate;

    /// <summary>What the neural upscaler is doing, or null when it was not asked for.</summary>
    public string? UpscalerStatus { get; }

    /// <summary>True while the network itself is running, rather than only the interpolation in front of it.</summary>
    public bool IsUpscaling => _layout.Upscale;

    /// <summary>What the neural restorer is doing, or null when it does not run for this source.</summary>
    public string? RestorerStatus { get; }

    /// <summary>True while the restorer's network is running.</summary>
    public bool IsRestoring => _layout.Restore;

    /// <summary>The core stages the DSP server runs, in order; empty without one.</summary>
    internal IReadOnlyList<CoreStage> RemoteStages { get; } = [];

    /// <summary>What the exciter is doing just now, or null when it does not run.</summary>
    public string? ExciterStatus
    {
        get
        {
            ExciterReport report;
            if ((_prefix?.Exciter ?? _suffix?.Exciter) is { } exciter)
            {
                report = new ExciterReport(exciter.EdgeHz, exciter.StartHz, exciter.AddedDb, exciter.DriveLowHz, exciter.Rate);
            }
            else if (_layout.ExciteRate > 0 && _remote is not null)
            {
                if (_remote.Exciter is not { } remote)
                {
                    return Loc.T("Oversampling exciter: running on DSP server");
                }

                report = remote;
            }
            else
            {
                return null;
            }

            if (report.EdgeHz <= 0.0)
            {
                return Loc.T("Oversampling exciter: measuring where the music ends");
            }

            // What reaches the output: the filter after the exciter keeps nothing above the output's Nyquist frequency.
            double upTo = Math.Min(report.Rate, _plan.ProcessingRate) / 2000.0;
            return double.IsNegativeInfinity(report.AddedDb)
                ? Loc.F("Oversampling exciter: nothing to add above {0:0.0} kHz just now", report.StartHz / 1000.0)
                : Loc.F("Oversampling exciter: harmonics from {0:0.0} to {1:0.#} kHz, {2:0} dB under the band above {3:0} kHz",
                    report.StartHz / 1000.0, upTo, -report.AddedDb, report.DriveLowHz / 1000.0);
        }
    }

    private static double BlockSecondsOf(ResamplerChain? chain) =>
        chain is null ? 0.0 : chain.FlushInputSamples / (double)Math.Max(1, chain.InputRate);

    public PlaybackPlan Plan => _plan;

    public MeterHub Meters { get; }

    /// <summary>Source frames (PCM) or bytes per channel (DSD) per processing block.</summary>
    public int InputBlock { get; }

    /// <summary>Delay from pipeline input to canonical output.</summary>
    public double LatencySeconds { get; }

    /// <summary>
    /// Audio one filter block covers, or zero when no stage works in blocks. The DSP load has to be read over at
    /// least this long: a block-based stage does nothing for many calls and then everything in one.
    /// </summary>
    public double FilterBlockSeconds { get; }

    public string ResamplerSummary { get; }

    /// <summary>Where the filtering runs: the processor, the graphics device that took it over, or the DSP server.</summary>
    public string AccelerationSummary => _accelerationSummary;

    internal double? NetworkRoundTripMilliseconds => _remote?.RoundTripMilliseconds;

    internal string? NetworkServer => _remote?.Summary;

    internal long NetworkRetransmits => _remote?.RetransmittedDatagrams ?? 0;

    /// <summary>The DSP server's processing time over the audio it processed; above one it cannot keep up.</summary>
    internal double? NetworkServerLoad => _remote?.ServerLoad;

    /// <summary>
    /// Audio kept in flight to the DSP server, which the FIFO has to be able to cover: enough for a few round trips
    /// and the server's own block, at least 0.3 s, and 0.6 s over the Internet.
    /// </summary>
    internal double NetworkBlockSeconds { get; }

    /// <summary>Approximate filter multiply-adds per second per channel.</summary>
    public double OperationsPerSecond { get; }

    /// <summary>Wall-clock DSP time of the most recent block, excluding FIFO waits and waits for the DSP server.</summary>
    public double LastProcessingSeconds { get; private set; }

    /// <summary>What the source's spectrum says about how it was coded, once enough has gone by.</summary>
    public BandwidthEstimate Bandwidth => _bandwidth;

    /// <summary>Re-reads where the spectrum ends; the estimate firms up as audio goes by.</summary>
    private void UpdateBandwidth()
    {
        if (_detector is not null)
        {
            _bandwidth = _detector.Estimate();
        }

        if (_edgeTracker is not null)
        {
            _recentBandwidth = _edgeTracker.Estimate();
        }
    }

    public long LimiterEvents => _tails.Sum(tail => tail.Limiter?.Events ?? 0);

    public long ApodizationEvents => _tails.Sum(tail => tail.Apodization?.Events ?? 0);

    public long ClippedSamples => _tails.Sum(tail => tail.Quantizer?.ClippedSamples ?? 0);

    public long ModulatorResets => _tails.Sum(tail => tail.Modulator?.InstabilityResets ?? 0);

    /// <summary>Largest modulator input since the last call (1.0 = 100 % modulation).</summary>
    public double TakePeakModulation() => _tails.Max(tail => tail.Modulator?.TakePeakModulation() ?? 0.0);

    /// <summary>Lowest limiter gain since the last call.</summary>
    public double TakeMinimumLimiterGain() => _tails.Min(tail => tail.Limiter?.TakeMinimumGain() ?? 1.0);

    public void SetVolume(double linear) => Volatile.Write(ref _volume, linear);

    public void SetReplayGain(double linear) => Volatile.Write(ref _replayGain, linear);

    /// <summary>True while output could not be written because a command interrupted the FIFO wait.</summary>
    public bool HasPendingOutput => _pendingOffset < _pendingLength;

    /// <summary>
    /// Writes what is waiting to go to the FIFO, and with a DSP server whatever results have come back meanwhile;
    /// returns true once all of it is written.
    /// </summary>
    public bool WritePending(OutputWriter writer)
    {
        if (!WriteOut(writer))
        {
            return false;
        }

        if (_remote is not null)
        {
            Collect(writer, wait: false);
            return WriteOut(writer);
        }

        return true;
    }

    /// <summary>Processes one block of PCM; returns false if the output write was interrupted (see <see cref="HasPendingOutput"/>).</summary>
    public bool ProcessPcm(double[][] input, int frames, OutputWriter writer)
    {
        long start = Stopwatch.GetTimestamp();
        _waitedSeconds = 0;
        double volume = Volatile.Read(ref _volume) * _levelOffset;
        double replayGain = Volatile.Read(ref _replayGain);
        _sourceSeconds += frames / (double)_plan.Source.SampleRate;
        _sourceMeterTime = _sourceSeconds + LatencySeconds;

        // The source's meters and detectors read the source as it arrived. After the restorer that is the input itself;
        // otherwise it is what the map makes of it, and the map hands it over as it runs.
        if (_layout.Restore)
        {
            _workers.For(_outputChannels, c => MeterSource(c, input, frames, replayGain));
        }

        _entry.Wrap(input, _sourceChannels, frames);
        _entry.Gain = replayGain;
        SetExciterEdges(_entry);
        Process(_entry, writer, volume);
        UpdateBandwidth();
        LastProcessingSeconds = Math.Max(0, Stopwatch.GetElapsedTime(start).TotalSeconds - _waitedSeconds);

        // Measured before the write, which can block on a device that is not ready and has nothing to do with
        // how the filtering was divided.
        _accelerator?.ReportBlock(LastProcessingSeconds, frames);
        return WriteOut(writer);
    }

    /// <summary>Processes one block of DSD bytes; returns false if the output write was interrupted.</summary>
    public bool ProcessDsd(byte[][] input, int bytes, OutputWriter writer)
    {
        long start = Stopwatch.GetTimestamp();
        _waitedSeconds = 0;
        double volume = Volatile.Read(ref _volume) * _levelOffset;
        _sourceSeconds += bytes * 8.0 / _plan.Source.SampleRate;
        _sourceMeterTime = _sourceSeconds + LatencySeconds;
        if (_plan.PassThrough)
        {
            _workers.For(_outputChannels, c => PassDsd(c, input, bytes));
            AppendOutput();
        }
        else
        {
            _workers.For(_outputChannels, c => ConvertDsd(c, input, bytes));
            _entry.Channels = _outputChannels;
            _entry.HasDry = false;
            _entry.Gain = 1.0;
            Process(_entry, writer, volume);
        }

        LastProcessingSeconds = Math.Max(0, Stopwatch.GetElapsedTime(start).TotalSeconds - _waitedSeconds);
        _accelerator?.ReportBlock(LastProcessingSeconds, bytes * 8);
        return WriteOut(writer);
    }

    /// <summary>Pushes silence through the filters so the tail of the last track is heard. Restartable after interruption.</summary>
    public bool Flush(OutputWriter writer)
    {
        if (!WritePending(writer))
        {
            return false;
        }

        if (_plan.PassThrough)
        {
            return true;
        }

        double inputRate = _plan.Source.IsDsd ? _plan.Source.SampleRate / 8.0 : _plan.Source.SampleRate;
        long remaining = (long)Math.Ceiling(LatencySeconds * inputRate) + InputBlock + _flushInputSamples;
        while (remaining > 0)
        {
            bool ok;
            if (_plan.Source.IsDsd)
            {
                var silent = new byte[_sourceChannels][];
                Array.Fill(silent, _silentDsd);
                ok = ProcessDsd(silent, InputBlock, writer);
            }
            else
            {
                if (_silentPcm is null)
                {
                    _silentPcm = new double[_sourceChannels][];
                    for (int c = 0; c < _sourceChannels; c++)
                    {
                        _silentPcm[c] = new double[InputBlock];
                    }
                }

                ok = ProcessPcm(_silentPcm, InputBlock, writer);
            }

            if (!ok)
            {
                return false;
            }

            remaining -= InputBlock;
        }

        return DrainNetworkPrefetch(writer);
    }

    /// <summary>
    /// Waits for every block still at the DSP server and writes it out, so that a track boundary or a flush does not
    /// leave the end of the music behind. Returns false if a command interrupted the wait or the write.
    /// </summary>
    internal bool DrainNetworkPrefetch(OutputWriter writer)
    {
        if (!WriteOut(writer))
        {
            return false;
        }

        while (_remote is not null && _remote.InFlight > 0)
        {
            if (writer.ShouldAbort)
            {
                return false;
            }

            Collect(writer, wait: true);
            if (!WriteOut(writer))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Clears all filter state (after a seek).</summary>
    public void Reset()
    {
        _remote?.Reset();
        _inFlightEdges.Clear();
        _pendingOffset = 0;
        _pendingLength = 0;
        double volume = Volatile.Read(ref _volume) * _levelOffset;
        _prefix?.Reset();
        _suffix?.Reset();
        foreach (Tail tail in _tails)
        {
            tail.Reset(volume);
        }

        Meters.Discard();
        Meters.Source.Reset();
        Meters.Output?.Reset();
        Meters.Spectrum.Reset();
        Meters.OutputSpectrum?.Reset();
        _sourceSeconds = 0;
        _outputSeconds = 0;
        Volatile.Write(ref _clockWriter, null);
    }

    /// <summary>
    /// A block through the core: here, or through the stages in front of the server's, then to the server; then
    /// whatever results have come back, and while too much is in flight, waits for the oldest.
    /// </summary>
    private void Process(CoreBlock block, OutputWriter writer, double volume)
    {
        if (_remote is null)
        {
            _prefix!.Run(block, _layout.Entry, CoreStage.Filter);
            FinishBlock(block, volume);
            return;
        }

        _prefix?.Run(block, _layout.Entry, _remote.First - 1);
        _remote.Submit(block);
        _inFlightEdges.Enqueue((block.Gain, block.EdgeHz, block.TopHz, block.WallHz));
        Collect(writer, wait: true);
    }

    /// <summary>
    /// Takes the results that have come back from the server through the stages after its own and the output's
    /// processing. With <paramref name="wait"/>, waits while more audio is in flight than <see cref="InFlightLimit"/>,
    /// unless a command needs the engine.
    /// </summary>
    private void Collect(OutputWriter writer, bool wait)
    {
        DspRemoteSegment remote = _remote!;
        while (true)
        {
            bool must = wait && remote.InFlight > 0 && (remote.InFlightSeconds > InFlightLimit() || remote.InFlight >= 256);
            long started = Stopwatch.GetTimestamp();
            bool taken = remote.TryTake(_result, must ? TimeSpan.FromMilliseconds(20) : TimeSpan.Zero);
            if (must)
            {
                _waitedSeconds += Stopwatch.GetElapsedTime(started).TotalSeconds;
            }

            if (!taken)
            {
                if (!must || writer.ShouldAbort)
                {
                    return;
                }

                continue;
            }

            if (_inFlightEdges.TryDequeue(out var edges))
            {
                (_result.Gain, _result.EdgeHz, _result.TopHz, _result.WallHz) = edges;
            }

            if (_suffix is not null)
            {
                _suffix.Run(_result, remote.Last + 1, CoreStage.Filter);
            }

            FinishBlock(_result, Volatile.Read(ref _volume) * _levelOffset);
        }
    }

    /// <summary>Audio to keep in flight: six round trips and a tenth of a second, from 0.3 to 2 s.</summary>
    private double InFlightLimit() => Math.Clamp(0.1 + (6 * (_remote?.RoundTripMilliseconds ?? 0) / 1000), 0.3, 2.0);

    /// <summary>The output's own processing of a block that has been through the whole core, then interleaved for the FIFO.</summary>
    private void FinishBlock(CoreBlock block, double volume)
    {
        _outputSeconds += block.Counts[0] / (double)_plan.ProcessingRate;
        double heard = _outputSeconds;
        _workers.For(_outputChannels, c => FinishOutput(c, _tails[c], block.Signal[c], block.Counts[c], volume, heard));
        AppendOutput();
    }

    /// <summary>
    /// Where the source ends over the last few seconds, and where the signal reaching the exciter does: the same, or just
    /// under Nyquist after the restorer, which fills up to it and matches the masters there better than harmonics do,
    /// so after it the harmonics above the edge go only under its band. Until the source has been measured the exciter
    /// takes it to run to 0.45 of its rate, and glides to the edge once it is known.
    /// </summary>
    private void SetExciterEdges(CoreBlock block)
    {
        if (_layout.ExciteRate <= 0)
        {
            return;
        }

        (double edge, double wall) = HarmonicExciter.EdgesFor(_recentBandwidth, _plan.ConversionRate);
        if (_layout.Restore)
        {
            block.EdgeHz = edge;
            block.TopHz = HarmonicExciter.AfterRestorerTop * _plan.ConversionRate;
            block.WallHz = 0.5 * _plan.ConversionRate;
        }
        else
        {
            block.EdgeHz = edge;
            block.TopHz = edge;
            block.WallHz = wall;
        }
    }

    /// <summary>
    /// Half-band cascade from the processing rate up to the modulator. When the last doubling is folded into the
    /// modulator, the cascade stops one octave lower.
    /// </summary>
    private static ResamplerChain CreateModulatorInterpolator(PlaybackPlan plan, bool foldLastStage)
    {
        int target = foldLastStage ? plan.Output.DsdRate / 2 : plan.Output.DsdRate;
        double band = Math.Min(Math.Min(plan.ConversionRate / 2.0, 100_000.0), plan.ProcessingRate * 0.2);
        var stages = new List<IRateStage>();
        for (int rate = plan.ProcessingRate; rate < target; rate *= 2)
        {
            stages.Add(new HalfbandInterpolatorStage(rate, band, 120.0));
        }

        return new ResamplerChain(plan.ProcessingRate, target, stages, Loc.T("Modulator interpolation"));
    }

    /// <summary>
    /// Whether the modulator itself performs the final ×2. It is worth it above 8 MHz, where the audio band is a
    /// thousandth of the Nyquist frequency and linear interpolation leaves images about 120 dB down, while saving
    /// a filter pass and a buffer at the full DSD rate. At least one half-band stage must remain before it.
    /// </summary>
    private static bool ShouldFoldLastInterpolation(PlaybackPlan plan) =>
        plan.IsDsdOutput
        && !plan.PassThrough
        && plan.Output.DsdRate >= 8_000_000
        && plan.ProcessingRate <= plan.Output.DsdRate / 4;

    private static (double[] Gains, int[] Delays) ComputeSpeakerTrims(SpeakerSettings speakers, PlaybackPlan plan, int channels)
    {
        double[] gains = Enumerable.Repeat(1.0, channels).ToArray();
        var delays = new int[channels];
        if (!speakers.Enabled || speakers.Channels.Count == 0)
        {
            return (gains, delays);
        }

        int count = Math.Min(channels, speakers.Channels.Count);
        double farthest = speakers.Channels.Take(count).Max(t => t.DistanceCm);
        double rate = plan.PassThrough ? plan.Output.DsdRate / 8.0 : plan.ProcessingRate;
        for (int c = 0; c < count; c++)
        {
            SpeakerTrim trim = speakers.Channels[c];
            gains[c] = plan.PassThrough ? 1.0 : Math.Pow(10.0, trim.LevelDb / 20.0);
            delays[c] = (int)Math.Round((farthest - trim.DistanceCm) / 100.0 / SpeedOfSound * rate);
        }

        return (gains, delays);
    }

    private int SourceChannel(int outputChannel)
    {
        if (_sourceChannels == 1)
        {
            return outputChannel < 2 ? 0 : -1;
        }

        return outputChannel < _sourceChannels ? outputChannel : -1;
    }

    private void Meter(int channel, ReadOnlySpan<double> signal) => Meters.QueueSource(_sourceMeterTime, channel, signal);

    /// <summary>
    /// The source's meters and detectors after the restorer, which read the source as it arrived, with replay gain,
    /// rather than what the restorer made of it.
    /// </summary>
    private void MeterSource(int c, double[][] input, int frames, double replayGain)
    {
        Tail tail = _tails[c];
        int source = SourceChannel(c);
        bool meter = source >= 0 && source == c;
        if (source < 0 || !(meter || c == _detectorChannel || tail.Apodization is not null))
        {
            return;
        }

        EnsureCapacity(ref tail.Original, frames);
        Span<double> original = tail.Original.AsSpan(0, frames);
        input[source].AsSpan(0, frames).CopyTo(original);
        if (replayGain != 1.0)
        {
            SimdMath.Scale(original, replayGain);
        }

        if (meter)
        {
            Meter(source, original);
        }

        if (c == _detectorChannel)
        {
            _detector?.Push(original);
            _edgeTracker?.Push(original);
        }

        tail.Apodization?.Process(original);
    }

    /// <summary>
    /// The source's meters and detectors without the restorer, handed the map's signal before and after the ultrasonic
    /// filter: the meters read the one the settings ask for, the detectors the filtered one.
    /// </summary>
    private void MeterMapped(int c, ReadOnlySpan<double> before, ReadOnlySpan<double> after)
    {
        int source = SourceChannel(c);
        bool meter = source >= 0 && source == c;
        if (meter)
        {
            Meter(source, _meterAdjustedSource ? after : before);
        }

        if (c == _detectorChannel)
        {
            _detector?.Push(after);
            _edgeTracker?.Push(after);
        }

        _tails[c].Apodization?.Process(after);
    }

    private void PassDsd(int c, byte[][] input, int bytes)
    {
        Tail tail = _tails[c];
        int source = SourceChannel(c);
        bool meter = source >= 0 && source == c;
        ReadOnlySpan<byte> dsd = source < 0 ? _silentDsd.AsSpan(0, bytes) : input[source].AsSpan(0, bytes);
        Span<byte> output = tail.DsdOutput.AsSpan(0, bytes);
        dsd.CopyTo(output);
        tail.ByteDelay?.Process(output);
        if (meter && tail.DsdConverter is not null)
        {
            int converted = tail.DsdConverter.Process(dsd, tail.Conversion);
            Meter(source, tail.Conversion.AsSpan(0, converted));
        }

        tail.OutputCount = bytes;
    }

    private void ConvertDsd(int c, byte[][] input, int bytes)
    {
        Tail tail = _tails[c];
        int source = SourceChannel(c);
        ReadOnlySpan<byte> dsd = source < 0 ? _silentDsd.AsSpan(0, bytes) : input[source].AsSpan(0, bytes);
        int produced = tail.DsdConverter!.Process(dsd, tail.Conversion);
        if (source >= 0 && source == c)
        {
            Meter(source, tail.Conversion.AsSpan(0, produced));
        }

        _entry.Signal[c] = tail.Conversion;
        _entry.Counts[c] = produced;
    }

    private void FinishOutput(int c, Tail tail, double[] signal, int count, double volume, double heard)
    {
        Span<double> x = signal.AsSpan(0, count);

        tail.Volume.Apply(x, volume, _rampSamples);
        tail.Limiter?.Process(x);
        if (tail.TrimGain != 1.0)
        {
            SimdMath.Scale(x, tail.TrimGain);
        }

        tail.Delay?.Process(x);
        Meters.QueueOutput(heard, c, x);

        if (tail.Quantizer is not null)
        {
            EnsureCapacity(ref tail.PcmOutput, count);
            tail.Quantizer.Process(x, tail.PcmOutput);
            tail.OutputCount = count;
            return;
        }

        int ratio = _plan.Output.DsdRate / _plan.ProcessingRate;
        int chunkInput = Math.Max(1, MaxModulatorSamplesPerChunk / ratio);
        EnsureCapacity(ref tail.DsdOutput, (int)(((long)count * ratio + 32) / 8) + 16);
        int bytesOut = 0;
        for (int offset = 0; offset < count; offset += chunkInput)
        {
            int take = Math.Min(chunkInput, count - offset);
            int carry = tail.ModulatorCarry;
            EnsureCapacity(ref tail.ModulatorBuffer, carry + _modulatorChain!.MaxOutput(take) + 32);
            int interpolated = tail.ModulatorInterpolator!.Process(x.Slice(offset, take), tail.ModulatorBuffer.AsSpan(carry));
            int total = carry + interpolated;
            int usable = total - total % _modulatorInputAlignment;
            bytesOut += tail.Modulator!.Process(tail.ModulatorBuffer.AsSpan(0, usable), tail.DsdOutput.AsSpan(bytesOut), _modulatorDoublesRate);
            int left = total - usable;
            tail.ModulatorBuffer.AsSpan(usable, left).CopyTo(tail.ModulatorBuffer);
            tail.ModulatorCarry = left;
        }

        tail.OutputCount = bytesOut;
    }

    /// <summary>Interleaves the channels' last output onto what is waiting for the FIFO.</summary>
    private void AppendOutput()
    {
        int count = _tails[0].OutputCount;
        foreach (Tail tail in _tails)
        {
            if (tail.OutputCount != count)
            {
                throw new InvalidOperationException("Channel processing chains produced different amounts of output.");
            }
        }

        if (count == 0)
        {
            return;
        }

        int channels = _outputChannels;
        int bytes = _plan.Output.Kind switch
        {
            OutputSampleKind.Pcm => count * channels * 4,
            OutputSampleKind.NativeDsd => count * channels,
            _ => count / 2 * channels * 4,
        };

        // Written out from the front; what is left of earlier blocks moves down before this one goes behind it.
        if (_pendingOffset > 0)
        {
            _pending.AsSpan(_pendingOffset, _pendingLength - _pendingOffset).CopyTo(_pending);
            _pendingLength -= _pendingOffset;
            _pendingOffset = 0;
        }

        if (_pending.Length < _pendingLength + bytes)
        {
            Array.Resize(ref _pending, (int)BitOperations.RoundUpToPowerOf2((uint)(_pendingLength + bytes)));
        }

        Span<byte> target = _pending.AsSpan(_pendingLength, bytes);
        switch (_plan.Output.Kind)
        {
            case OutputSampleKind.Pcm:
            {
                Span<int> frames = MemoryMarshal.Cast<byte, int>(target);
                for (int c = 0; c < channels; c++)
                {
                    int[] source = _tails[c].PcmOutput;
                    for (int f = 0; f < count; f++)
                    {
                        frames[f * channels + c] = source[f];
                    }
                }

                break;
            }

            case OutputSampleKind.NativeDsd:
            {
                for (int c = 0; c < channels; c++)
                {
                    byte[] source = _tails[c].DsdOutput;
                    for (int f = 0; f < count; f++)
                    {
                        target[f * channels + c] = source[f];
                    }
                }

                break;
            }

            default:
            {
                int frameCount = count / 2;
                EnsureCapacity(ref _dopFrames, frameCount * channels);
                _dop.Encode(_tails.Select(t => t.DsdOutput).ToArray(), channels, frameCount * 2, _dopFrames);
                MemoryMarshal.AsBytes(_dopFrames.AsSpan(0, frameCount * channels)).CopyTo(target);
                break;
            }
        }

        _pendingLength += bytes;
        foreach (Tail tail in _tails)
        {
            tail.OutputCount = 0;
        }
    }

    /// <summary>
    /// Lets the meters show what the device has played: <paramref name="ringRead"/> is how many bytes the device has
    /// taken from the FIFO in all. Called on the interface's timer.
    /// </summary>
    internal void ReleaseMeters(long ringRead)
    {
        if (Volatile.Read(ref _clockWriter) is not { } writer)
        {
            return;
        }

        OutputFormat format = _plan.Output;
        long played = ringRead - Interlocked.Read(ref _ringStart) + (writer.Discarded - Interlocked.Read(ref _discardStart));
        Meters.Release(Math.Max(0, played) / ((double)format.CanonicalBytesPerFrame * format.CanonicalFrameRate));
    }

    /// <summary>Writes what is waiting; true once all of it is in the FIFO.</summary>
    private bool WriteOut(OutputWriter writer)
    {
        if (_clockWriter is null && writer.HasRing)
        {
            // This pipeline's output starts here in the FIFO; what the device has played of it is counted from this point.
            Interlocked.Exchange(ref _ringStart, writer.RingWritten - _pendingOffset);
            Interlocked.Exchange(ref _discardStart, writer.Discarded);
            Volatile.Write(ref _clockWriter, writer);
        }

        if (HasPendingOutput)
        {
            _pendingOffset += writer.Write(_pending.AsSpan(_pendingOffset, _pendingLength - _pendingOffset));
            if (!HasPendingOutput)
            {
                _pendingOffset = 0;
                _pendingLength = 0;
            }
        }

        return !HasPendingOutput;
    }

    private static void EnsureCapacity<T>(ref T[] array, int length)
    {
        if (array.Length < length)
        {
            array = new T[(int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(16, length))];
        }
    }

    /// <summary>One output channel's own processing after the core, and the source's conversion for DSD.</summary>
    private sealed class Tail
    {
        public DsdToPcmState? DsdConverter;
        public ApodizationDetector? Apodization;
        public readonly SmoothedGain Volume = new(1.0);
        public SoftLimiter? Limiter;
        public double TrimGain = 1.0;
        public DelayLine? Delay;
        public ByteDelayLine? ByteDelay;
        public PcmQuantizer? Quantizer;
        public ResamplerChainState? ModulatorInterpolator;
        public DeltaSigmaModulator? Modulator;
        public double[] Conversion = [];
        public double[] Original = [];
        public double[] ModulatorBuffer = [];
        public int[] PcmOutput = [];
        public byte[] DsdOutput = [];
        public int ModulatorCarry;
        public int OutputCount;

        public void Reset(double volume)
        {
            DsdConverter?.Reset();
            Apodization?.Reset();
            Volume.Jump(volume);
            Limiter?.Reset();
            Delay?.Reset();
            ByteDelay?.Reset();
            Quantizer?.Reset();
            ModulatorInterpolator?.Reset();
            Modulator?.Reset();
            ModulatorCarry = 0;
            OutputCount = 0;
        }
    }
}
