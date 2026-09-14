using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
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
using FUPlayer.Core.Output;
using FUPlayer.Core.Settings;

namespace FUPlayer.Core.Engine;

/// <summary>
/// Executes a <see cref="PlaybackPlan"/>: per output channel (in parallel) DSD→PCM conversion, pre-processing,
/// metering, resampling, volume, limiter, speaker trims, then dither or delta-sigma modulation; finally interleaves the
/// canonical output stream.
/// </summary>
internal sealed class DspPipeline : IDisposable
{
    private const double BlockSeconds = 0.02;
    private const double VolumeRampSeconds = 0.03;
    private const int MaxModulatorSamplesPerChunk = 1 << 20;
    private const double SpeedOfSound = 343.0;

    private readonly PlaybackPlan _plan;
    private readonly ParallelWorkers _workers;
    private readonly ChannelChain[] _chains;
    private RestorationSettings? _restoration;
    private CodecBandwidthDetector? _detector;

    /// <summary>
    /// The one output channel allowed to feed the detector. Channels are processed in parallel and
    /// the detector keeps a single fill counter, so letting every channel push into it corrupts that
    /// counter and eventually walks off the end of its buffer.
    /// </summary>
    private int _detectorChannel = -1;
    private HighBandModel? _model;
    private NeuralRepairModel? _network;
    private double _cutoffHz;
    private BandwidthEstimate _bandwidth;
    private readonly byte[][] _dsdOutputs;
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
    private byte[] _canonical = [];
    private int[] _dopFrames = [];
    private double[][]? _silentPcm;
    private double _volume = 1.0;
    private double _replayGain = 1.0;
    private int _pendingOffset;
    private int _pendingLength;

    public DspPipeline(PlaybackPlan plan, PlayerSettings settings, ParallelWorkers workers)
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

        ResamplerChain? resampler = plan.PassThrough
            ? null
            : ResamplerFactory.Create(
                plan.Filter!, plan.ConversionRate, plan.ProcessingRate, plan.Staging, plan.FilterTaps, plan.Convolution, plan.ConvolutionOptions);

        // Channels run side by side, and a filter then divides its own work over whatever cores that leaves. Two
        // channels on an eight-core machine would otherwise use two cores and report a DSP load of several
        // hundred per cent while most of the processor stood still.
        int threadBudget = settings.Processing.DspThreads >= 0
            ? Math.Max(1, settings.Processing.DspThreads + 1)
            : Environment.ProcessorCount;
        int stageParallelism = Math.Clamp(threadBudget / Math.Max(1, _outputChannels), 1, 16);

        // A graphics device is only worth waking for a long filter stage; everything else stays here.
        bool force = settings.Processing.GpuForce;
        string acceleration;
        if (settings.Processing.GpuAcceleration && resampler is not null
            && resampler.Stages.Any(s => GpuAccelerator.CanAccelerate(s, force)))
        {
            _accelerator = GpuAccelerator.TryCreate(
                settings.Processing.GpuDeviceId, settings.Processing.GpuHighPrecision, out acceleration, force,
                settings.Processing.GpuPhaseShare, settings.Processing.GpuRetimeWhilePlaying);

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
                ? "Filtering on one processor thread."
                : $"Filtering on {total} processor threads ({channelThreads} channels, {stageParallelism} each).";

            // Asked for a device and got none: say that the conversion is the reason, not the device, and say
            // which conversion it is, since a filter layered to answer sooner is a choice that can be taken back.
            if (settings.Processing.GpuAcceleration)
            {
                acceleration += resampler?.Stages.Any(s => s is LayeredFftPolyphaseStage) == true
                    ? " The filter uses layered partitioning, which no graphics device accepts. Select one block" +
                      " length to allow offload."
                    : " No stage in this conversion can be offloaded.";
            }
        }

        DsdToPcmDesign? dsd = plan.Source.IsDsd
            ? DsdToPcmDesign.Create(
                plan.Source.SampleRate,
                meterRate,
                plan.PassThrough ? DsdFilterCatalog.Get(null) : plan.DsdFilter!,
                plan.PassThrough ? 2.0 : plan.DsdConversionGain)
            : null;
        ResamplerChain? ultrasonic = plan.RemoveUltrasonics ? ProcessingFilters.CreateUltrasonicFilter(plan.ConversionRate) : null;
        _modulatorDoublesRate = ShouldFoldLastInterpolation(plan);
        _modulatorInputAlignment = _modulatorDoublesRate ? _modulatorAlignment / 2 : _modulatorAlignment;
        ResamplerChain? interpolator = plan.IsDsdOutput && !plan.PassThrough ? CreateModulatorInterpolator(plan, _modulatorDoublesRate) : null;
        NoiseTransferFunction? ntf = plan.Modulator is not null && !plan.PassThrough ? ModulatorCatalog.DesignNtf(plan.Modulator, plan.Output.DsdRate) : null;
        (double[] gains, int[] delays) = ComputeSpeakerTrims(settings.Speakers, plan, _outputChannels);
        bool detectApodization = settings.Processing.ApodizationDetection && !plan.Source.IsDsd && plan.ConversionRate <= ApodizationDetector.MaximumSampleRate;

        RestorationSettings restore = settings.Restoration;
        bool canRestore = !plan.Source.IsDsd && (restore.ReduceArtifacts || restore.RebuildHarmonics);
        if (canRestore)
        {
            _restoration = restore;
            _detector = restore.ManualCutoffHz > 0.0 ? null : new CodecBandwidthDetector(plan.ConversionRate);

            for (int c = 0; c < _outputChannels && _detectorChannel < 0; c++)
            {
                if (SourceChannel(c) >= 0)
                {
                    _detectorChannel = c;
                }
            }
            _cutoffHz = restore.ManualCutoffHz;
            _model = LoadModel(restore);
            _network = restore.Predict ? ModelLibrary.TryLoadNetwork(restore.NetworkPath, out _) : null;
        }

        _chains = new ChannelChain[_outputChannels];
        for (int c = 0; c < _outputChannels; c++)
        {
            int source = SourceChannel(c);
            bool meters = source >= 0 && source == c;
            var chain = new ChannelChain
            {
                TrimGain = gains[c],
            };

            int conversionCapacity = InputBlock + 16;
            if (dsd is not null && (!plan.PassThrough || meters))
            {
                chain.DsdConverter = dsd.CreateState();
                conversionCapacity = chain.DsdConverter.MaxOutput(InputBlock + 2) + 16;
            }

            chain.Conversion = new double[conversionCapacity];

            if (canRestore)
            {
                if (_network is not null)
                {
                    // One network does both jobs, so the two switches decide which half of its
                    // answer is used rather than which stages exist.
                    chain.Network = new NeuralRepair(_network, plan.ConversionRate)
                    {
                        CutoffHz = _cutoffHz,
                        CeilingHz = Math.Min(restore.CeilingHz, plan.ConversionRate / 2.0 * 0.98),
                        Rebuild = restore.RebuildHarmonics,
                        Reduce = restore.ReduceArtifacts,
                        Amount = restore.NetworkAmount,
                    };
                }
                else
                {
                    if (restore.ReduceArtifacts)
                    {
                        chain.Reducer = new ArtifactReducer(plan.ConversionRate) { Strength = restore.ArtifactStrength };
                    }

                    if (restore.RebuildHarmonics)
                    {
                        chain.Rebuilder = new HarmonicRebuilder(plan.ConversionRate)
                        {
                            AmountDb = restore.RebuildAmountDb,
                            CeilingHz = Math.Min(restore.CeilingHz, plan.ConversionRate / 2.0 * 0.98),
                            CutoffHz = _cutoffHz,
                            Model = restore.Predict ? _model : null,
                        };
                    }
                }
            }

            if (plan.PassThrough)
            {
                chain.DsdOutput = new byte[InputBlock + 2];
                chain.ByteDelay = delays[c] > 0 ? new ByteDelayLine(delays[c], DsdConstants.SilenceByte) : null;
                _chains[c] = chain;
                continue;
            }

            if (ultrasonic is not null)
            {
                chain.Ultrasonic = ultrasonic.CreateState();
                chain.Clean = new double[conversionCapacity];
            }

            if (detectApodization && meters)
            {
                chain.Apodization = new ApodizationDetector(plan.ConversionRate);
            }

            chain.Resampler = resampler!.CreateState(stageParallelism, _accelerator);
            int processingCapacity = resampler.MaxOutput(conversionCapacity) + 16;
            chain.Processing = new double[processingCapacity];
            chain.Limiter = plan.Limiter ? new SoftLimiter(plan.ProcessingRate) : null;
            chain.Delay = delays[c] > 0 ? new DelayLine(delays[c]) : null;

            if (plan.IsDsdOutput)
            {
                int ratio = plan.Output.DsdRate / plan.ProcessingRate;
                int chunkInput = Math.Max(1, MaxModulatorSamplesPerChunk / ratio);
                chain.ModulatorInterpolator = interpolator!.CreateState();
                chain.ModulatorBuffer = new double[interpolator.MaxOutput(Math.Min(chunkInput, processingCapacity)) + 32];
                chain.DsdOutput = new byte[(int)(((long)processingCapacity * ratio + 32) / 8) + 16];
                chain.Modulator = new DeltaSigmaModulator(ntf!);
            }
            else
            {
                chain.PcmOutput = new int[processingCapacity];
                chain.Quantizer = new PcmQuantizer(plan.Dither!, plan.Output.ValidBits, plan.ProcessingRate, (ulong)(c + 1) * 0x9E3779B97F4A7C15UL);
            }

            _chains[c] = chain;
        }

        _dsdOutputs = _chains.Select(chain => chain.DsdOutput).ToArray();

        double latency = 0.0;
        if (dsd is not null && !plan.PassThrough)
        {
            latency += dsd.DelayOutputSamples / dsd.OutputRate;
        }

        if (ultrasonic is not null)
        {
            latency += ultrasonic.DelayOutputSamples / ultrasonic.OutputRate;
        }

        if (resampler is not null)
        {
            latency += resampler.DelayOutputSamples / resampler.OutputRate;
            latency += (double)(_chains[0].Limiter?.LatencySamples ?? 0) / plan.ProcessingRate;
        }

        if (interpolator is not null)
        {
            latency += interpolator.DelayOutputSamples / interpolator.OutputRate;
        }

        // Block-based filter stages hold input until a block is complete; flushing has to push that much extra.
        double sourceInputRate = plan.Source.IsDsd ? plan.Source.SampleRate / 8.0 : plan.Source.SampleRate;
        _flushInputSamples = resampler is null
            ? 0
            : (int)Math.Ceiling(resampler.FlushInputSamples * (sourceInputRate / resampler.InputRate));

        LatencySeconds = latency;
        FilterBlockSeconds = BlockSecondsOf(resampler);
        ResamplerSummary = resampler?.Summary ?? "DSD passed through unchanged";
        OperationsPerSecond = (resampler?.OperationsPerInputSecond ?? 0) + (interpolator?.OperationsPerInputSecond ?? 0);
        AccelerationSummary = _accelerator?.Status ?? acceleration;
    }

    /// <summary>Releases the accelerator's device memory; the processing chains stay readable for the meters.</summary>
    public void Dispose() => _accelerator?.Dispose();

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
            plan.Filter, plan.ConversionRate, plan.ProcessingRate, plan.Staging, plan.FilterTaps, plan.Convolution, plan.ConvolutionOptions);
        return 2.5 * BlockSecondsOf(chain);
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

    /// <summary>Where the filtering runs: the processor, or the OpenCL device that took it over.</summary>
    public string AccelerationSummary { get; }

    /// <summary>Approximate filter multiply-adds per second per channel.</summary>
    public double OperationsPerSecond { get; }

    /// <summary>Wall-clock DSP time of the most recent block, excluding FIFO waits.</summary>
    public double LastProcessingSeconds { get; private set; }

    /// <summary>What the source's spectrum says about how it was coded, once enough has gone by.</summary>
    public BandwidthEstimate Bandwidth => _bandwidth;

    /// <summary>True when a trained model or network is deciding the repair.</summary>
    public bool IsPredicting => _model is not null || _network is not null;

    /// <summary>True when a network is doing the repair rather than the fixed stages.</summary>
    public bool IsNeural => _network is not null;

    private static HighBandModel? LoadModel(RestorationSettings restore) =>
        restore.Predict ? ModelLibrary.TryLoad(restore.ModelPath, out _) : null;

    /// <summary>
    /// Re-reads where the spectrum ends. The estimate only firms up as audio goes by, so the rebuilt
    /// band starts wherever it was told to and moves to the measured cutoff within a second or two.
    /// </summary>
    private void UpdateBandwidth()
    {
        if (_detector is null)
        {
            return;
        }

        _bandwidth = _detector.Estimate();
        _cutoffHz = _bandwidth.Verdict switch
        {
            // Nothing was cut, so there is nothing to rebuild.
            BandwidthVerdict.FullBand => 0.0,
            BandwidthVerdict.BandLimited => _bandwidth.CutoffHz,
            _ => _cutoffHz,
        };
    }

    public long LimiterEvents => _chains.Sum(chain => chain.Limiter?.Events ?? 0);

    public long ApodizationEvents => _chains.Sum(chain => chain.Apodization?.Events ?? 0);

    public long ClippedSamples => _chains.Sum(chain => chain.Quantizer?.ClippedSamples ?? 0);

    public long ModulatorResets => _chains.Sum(chain => chain.Modulator?.InstabilityResets ?? 0);

    /// <summary>Largest modulator input since the last call (1.0 = 100 % modulation).</summary>
    public double TakePeakModulation() => _chains.Max(chain => chain.Modulator?.TakePeakModulation() ?? 0.0);

    /// <summary>Lowest limiter gain since the last call.</summary>
    public double TakeMinimumLimiterGain() => _chains.Min(chain => chain.Limiter?.TakeMinimumGain() ?? 1.0);

    public void SetVolume(double linear) => Volatile.Write(ref _volume, linear);

    public void SetReplayGain(double linear) => Volatile.Write(ref _replayGain, linear);

    /// <summary>True while output of the last block could not be written because a command interrupted the FIFO wait.</summary>
    public bool HasPendingOutput => _pendingOffset < _pendingLength;

    /// <summary>Writes the remainder of the last block; returns true once it is fully written.</summary>
    public bool WritePending(OutputWriter writer)
    {
        if (HasPendingOutput)
        {
            _pendingOffset += writer.Write(_canonical.AsSpan(_pendingOffset, _pendingLength - _pendingOffset));
        }

        return !HasPendingOutput;
    }

    /// <summary>Processes one block of PCM; returns false if the output write was interrupted (see <see cref="HasPendingOutput"/>).</summary>
    public bool ProcessPcm(double[][] input, int frames, OutputWriter writer)
    {
        long start = Stopwatch.GetTimestamp();
        double volume = Volatile.Read(ref _volume) * _levelOffset;
        double replayGain = Volatile.Read(ref _replayGain);
        _workers.For(_outputChannels, c => ProcessPcmChannel(c, input, frames, volume, replayGain));
        _pendingLength = Interleave().Length;
        _pendingOffset = 0;
        LastProcessingSeconds = Stopwatch.GetElapsedTime(start).TotalSeconds;

        // Measured before the write, which can block on a device that is not ready and has nothing to do with
        // how the filtering was divided.
        _accelerator?.ReportBlock(LastProcessingSeconds, frames);
        UpdateBandwidth();
        return WritePending(writer);
    }

    /// <summary>Processes one block of DSD bytes; returns false if the output write was interrupted.</summary>
    public bool ProcessDsd(byte[][] input, int bytes, OutputWriter writer)
    {
        long start = Stopwatch.GetTimestamp();
        double volume = Volatile.Read(ref _volume) * _levelOffset;
        _workers.For(_outputChannels, c => ProcessDsdChannel(c, input, bytes, volume));
        _pendingLength = Interleave().Length;
        _pendingOffset = 0;
        LastProcessingSeconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
        _accelerator?.ReportBlock(LastProcessingSeconds, bytes * 8);
        return WritePending(writer);
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

        return true;
    }

    /// <summary>Clears all filter state (after a seek).</summary>
    public void Reset()
    {
        _pendingOffset = 0;
        _pendingLength = 0;
        double volume = Volatile.Read(ref _volume) * _levelOffset;
        foreach (ChannelChain chain in _chains)
        {
            chain.Reset(volume);
        }

        Meters.Source.Reset();
        Meters.Output?.Reset();
        Meters.Spectrum.Reset();
        Meters.OutputSpectrum?.Reset();
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

        return new ResamplerChain(plan.ProcessingRate, target, stages, "Modulator interpolation");
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

    private void MeterSource(int channel, ReadOnlySpan<double> signal)
    {
        Meters.Source.Process(channel, signal);
        Meters.Spectrum.Push(channel, signal);
    }

    private void ProcessPcmChannel(int c, double[][] input, int frames, double volume, double replayGain)
    {
        ChannelChain chain = _chains[c];
        int source = SourceChannel(c);
        bool meter = source >= 0 && source == c;
        Span<double> signal = chain.Conversion.AsSpan(0, frames);
        if (source < 0)
        {
            signal.Clear();
        }
        else
        {
            input[source].AsSpan(0, frames).CopyTo(signal);
        }

        if (replayGain != 1.0)
        {
            SimdMath.Scale(signal, replayGain);
        }

        if (meter && !_meterAdjustedSource)
        {
            MeterSource(source, signal);
        }

        if (chain.Ultrasonic is not null)
        {
            int cleaned = chain.Ultrasonic.Process(signal, chain.Clean);
            signal = chain.Clean.AsSpan(0, cleaned);
        }

        if (meter && _meterAdjustedSource)
        {
            MeterSource(source, signal);
        }

        if (chain.Network is not null)
        {
            if (c == _detectorChannel && _detector is not null)
            {
                _detector.Push(signal);
            }

            chain.Network.CutoffHz = _cutoffHz;
            chain.Network.Process(signal);
        }
        else if (chain.Reducer is not null || chain.Rebuilder is not null)
        {
            // Both work in place. Artefacts are damped first so the rebuilder copies a band that has
            // stopped flapping, rather than carrying the flapping upwards with it.
            if (c == _detectorChannel && _detector is not null)
            {
                _detector.Push(signal);
            }

            chain.Reducer?.Process(signal);

            if (chain.Rebuilder is not null)
            {
                chain.Rebuilder.CutoffHz = _cutoffHz;
                chain.Rebuilder.Process(signal);
            }
        }

        chain.Apodization?.Process(signal);
        ProcessConverted(c, chain, signal, volume);
    }

    private void ProcessDsdChannel(int c, byte[][] input, int bytes, double volume)
    {
        ChannelChain chain = _chains[c];
        int source = SourceChannel(c);
        bool meter = source >= 0 && source == c;
        ReadOnlySpan<byte> dsd = source < 0 ? _silentDsd.AsSpan(0, bytes) : input[source].AsSpan(0, bytes);

        if (_plan.PassThrough)
        {
            Span<byte> output = chain.DsdOutput.AsSpan(0, bytes);
            dsd.CopyTo(output);
            chain.ByteDelay?.Process(output);
            if (meter && chain.DsdConverter is not null)
            {
                int converted = chain.DsdConverter.Process(dsd, chain.Conversion);
                MeterSource(source, chain.Conversion.AsSpan(0, converted));
            }

            chain.OutputCount = bytes;
            return;
        }

        int produced = chain.DsdConverter!.Process(dsd, chain.Conversion);
        Span<double> signal = chain.Conversion.AsSpan(0, produced);
        if (meter)
        {
            MeterSource(source, signal);
        }

        ProcessConverted(c, chain, signal, volume);
    }

    private void ProcessConverted(int c, ChannelChain chain, ReadOnlySpan<double> signal, double volume)
    {
        int count = chain.Resampler!.Process(signal, chain.Processing);
        Span<double> x = chain.Processing.AsSpan(0, count);
        chain.Volume.Apply(x, volume, _rampSamples);
        chain.Limiter?.Process(x);
        if (chain.TrimGain != 1.0)
        {
            SimdMath.Scale(x, chain.TrimGain);
        }

        chain.Delay?.Process(x);
        Meters.Output?.Process(c, x);
        Meters.OutputSpectrum?.Push(c, x);

        if (chain.Quantizer is not null)
        {
            chain.Quantizer.Process(x, chain.PcmOutput);
            chain.OutputCount = count;
            return;
        }

        int ratio = _plan.Output.DsdRate / _plan.ProcessingRate;
        int chunkInput = Math.Max(1, MaxModulatorSamplesPerChunk / ratio);
        int bytesOut = 0;
        for (int offset = 0; offset < count; offset += chunkInput)
        {
            int take = Math.Min(chunkInput, count - offset);
            int carry = chain.ModulatorCarry;
            int interpolated = chain.ModulatorInterpolator!.Process(x.Slice(offset, take), chain.ModulatorBuffer.AsSpan(carry));
            int total = carry + interpolated;
            int usable = total - total % _modulatorInputAlignment;
            bytesOut += chain.Modulator!.Process(chain.ModulatorBuffer.AsSpan(0, usable), chain.DsdOutput.AsSpan(bytesOut), _modulatorDoublesRate);
            int left = total - usable;
            chain.ModulatorBuffer.AsSpan(usable, left).CopyTo(chain.ModulatorBuffer);
            chain.ModulatorCarry = left;
        }

        chain.OutputCount = bytesOut;
    }

    private ReadOnlySpan<byte> Interleave()
    {
        int count = _chains[0].OutputCount;
        foreach (ChannelChain chain in _chains)
        {
            if (chain.OutputCount != count)
            {
                throw new InvalidOperationException("Channel processing chains produced different amounts of output.");
            }
        }

        if (count == 0)
        {
            return ReadOnlySpan<byte>.Empty;
        }

        int channels = _outputChannels;
        switch (_plan.Output.Kind)
        {
            case OutputSampleKind.Pcm:
            {
                int bytes = count * channels * 4;
                EnsureCapacity(ref _canonical, bytes);
                Span<int> frames = MemoryMarshal.Cast<byte, int>(_canonical.AsSpan(0, bytes));
                for (int c = 0; c < channels; c++)
                {
                    int[] source = _chains[c].PcmOutput;
                    for (int f = 0; f < count; f++)
                    {
                        frames[f * channels + c] = source[f];
                    }
                }

                return _canonical.AsSpan(0, bytes);
            }

            case OutputSampleKind.NativeDsd:
            {
                int bytes = count * channels;
                EnsureCapacity(ref _canonical, bytes);
                for (int c = 0; c < channels; c++)
                {
                    byte[] source = _dsdOutputs[c];
                    for (int f = 0; f < count; f++)
                    {
                        _canonical[f * channels + c] = source[f];
                    }
                }

                return _canonical.AsSpan(0, bytes);
            }

            default:
            {
                int frameCount = count / 2;
                EnsureCapacity(ref _dopFrames, frameCount * channels);
                _dop.Encode(_dsdOutputs, channels, frameCount * 2, _dopFrames);
                int bytes = frameCount * channels * 4;
                EnsureCapacity(ref _canonical, bytes);
                MemoryMarshal.AsBytes(_dopFrames.AsSpan(0, frameCount * channels)).CopyTo(_canonical);
                return _canonical.AsSpan(0, bytes);
            }
        }
    }

    private static void EnsureCapacity<T>(ref T[] array, int length)
    {
        if (array.Length < length)
        {
            array = new T[(int)BitOperations.RoundUpToPowerOf2((uint)length)];
        }
    }

    private sealed class ChannelChain
    {
        public DsdToPcmState? DsdConverter;
        public ResamplerChainState? Ultrasonic;
        public ArtifactReducer? Reducer;
        public HarmonicRebuilder? Rebuilder;
        public NeuralRepair? Network;
        public ApodizationDetector? Apodization;
        public ResamplerChainState? Resampler;
        public readonly SmoothedGain Volume = new(1.0);
        public SoftLimiter? Limiter;
        public double TrimGain = 1.0;
        public DelayLine? Delay;
        public ByteDelayLine? ByteDelay;
        public PcmQuantizer? Quantizer;
        public ResamplerChainState? ModulatorInterpolator;
        public DeltaSigmaModulator? Modulator;
        public double[] Conversion = [];
        public double[] Clean = [];
        public double[] Processing = [];
        public double[] ModulatorBuffer = [];
        public int[] PcmOutput = [];
        public byte[] DsdOutput = [];
        public int ModulatorCarry;
        public int OutputCount;

        public void Reset(double volume)
        {
            DsdConverter?.Reset();
            Ultrasonic?.Reset();
            Apodization?.Reset();
            Resampler?.Reset();
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
