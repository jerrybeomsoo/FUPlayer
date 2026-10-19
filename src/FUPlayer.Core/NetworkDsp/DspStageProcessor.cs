using FUPlayer.Core.Dsp.Acceleration;
using FUPlayer.Core.Dsp.Resampling;
using FUPlayer.Core.Dsp.Restoration;
using FUPlayer.Core.Engine;
using FUPlayer.Core.Settings;
using FUPlayer.Core.Localization;
using FUPlayer.Core.Dsp.Numerics;
using FUPlayer.Core.Dsp.Analysis;

namespace FUPlayer.Core.NetworkDsp;

/// <summary>Only selected expensive stages are allocated; final gain, limiting and device formatting stay on the client.</summary>
internal sealed class DspStageProcessor : IDisposable
{
    private readonly NeuralRestorer? _restorer;
    private readonly NeuralUpscaler[] _upscalers = [];
    private readonly ResamplerChainState[] _resamplers = [];
    private readonly GpuAccelerator? _accelerator;
    private readonly ParallelWorkers _workers;
    private readonly int _maximumInput;
    private readonly ResamplerChain? _resamplerDefinition;
    private readonly TrainingUpsampler[] _combinedInterpolators = [];
    private readonly HarmonicExciter[] _combinedExciters = [];
    private readonly CodecBandwidthDetector? _combinedEdges;
    private readonly int _sourceRate;
    private BandwidthEstimate _combinedBandwidth;
    internal DspMetadata Metadata { get; }

    internal DspStageProcessor(DspHello hello, DspServerOptions options)
    {
        PlaybackPlan plan = hello.Plan;
        _sourceRate = plan.Source.SampleRate;
        if (hello.Version != DspProtocol.Version || !plan.Source.IsValid || plan.Output.Channels is < 1 or > 8
            || plan.FilterTaps is < 0 or > 33_554_432 || hello.MaximumInput is < 1 or > 131_200
            || plan.ProcessingRate is < 1 or > 50_000_000)
            throw new InvalidDataException("Invalid LAN DSP plan or protocol version.");
        _maximumInput = hello.MaximumInput;
        int budget = Math.Clamp(options.Threads + 1, 1, 64);
        int channelThreads = Math.Min(budget, plan.Output.Channels);
        string? restorerStatus = null, upscalerStatus = null;
        if (hello.Restorer && plan.Restore)
        {
            NeuralRestorerModel model = ModelLibrary.TryLoadRestorer(LocalModel(hello.Restoration.NeuralRestorerPath), options.RestorerDevice, out string? failure, Math.Min(budget, 4), allowSpinning: false)
                ?? throw new InvalidDataException(failure);
            _restorer = new NeuralRestorer(model, plan.Source.SampleRate);
            restorerStatus = Path.GetFileName(model.Path) + ". " + InferenceDevices.Describe(options.RestorerDevice, model.Adapter, model.AdapterFailure);
        }
        if (hello.Upscaler && plan.UpscaleRate > 0)
        {
            NeuralUpscalerModel model = ModelLibrary.TryLoadUpscaler(LocalModel(hello.Restoration.NeuralUpscalerPath), options.UpscalerDevice, out string? failure, Math.Clamp(budget / channelThreads, 1, 4), allowSpinning: false)
                ?? throw new InvalidDataException(failure);
            _upscalers = Enumerable.Range(0, plan.Output.Channels).Select(_ => new NeuralUpscaler(model,
                rate: plan.UpscaleRate, bandFromHz: plan.UpscaleRate / 4.0, bandGainDb: hello.Restoration.UpscalerBandDb)
                { Lossy = plan.SourceIsLossy }).ToArray();
            upscalerStatus = Path.GetFileName(model.Path) + ". " + InferenceDevices.Describe(options.UpscalerDevice, model.Adapter, model.AdapterFailure);
        }
        ResamplerChain? resampler = null;
        string acceleration = Loc.T("On the processor.");
        if (hello.Resampling && !plan.PassThrough)
        {
            int inputRate = plan.PreUpsampleRate > 0 ? plan.PreUpsampleRate : plan.ConversionRate;
            // Automatic execution belongs to the processing machine. A high client crossover must not force
            // thousands of tap products per output sample on the server; retain its requested block-delay limit.
            ConvolutionOptions convolution = plan.ConvolutionOptions;
            if (plan.Convolution == ConvolutionMode.Automatic)
                convolution = convolution with { ThresholdTapsPerPhase = Math.Min(
                    convolution.ThresholdTapsPerPhase > 0 ? convolution.ThresholdTapsPerPhase : FftPolyphaseStage.DefaultThresholdTapsPerPhase,
                    FftPolyphaseStage.DefaultThresholdTapsPerPhase) };
            resampler = ResamplerFactory.Create(plan.Filter!, inputRate, plan.ProcessingRate, plan.Staging,
                plan.FilterTaps, plan.Convolution, convolution);
            int parallelism = Math.Clamp(budget / channelThreads, 1, 16);
            if (options.GpuAcceleration && resampler.Stages.Any(s => GpuAccelerator.CanAccelerate(s, options.GpuForce)))
            {
                _accelerator = GpuAccelerator.TryCreate(options.GpuDevice, options.GpuHighPrecision, out acceleration,
                    options.GpuForce, options.GpuPhaseShare, options.GpuRetime);
                _accelerator?.PlanChannels(resampler.Stages, plan.Output.Channels, parallelism);
            }
            _resamplers = Enumerable.Range(0, plan.Output.Channels).Select(_ => resampler.CreateState(parallelism, _accelerator)).ToArray();
        }
        _resamplerDefinition = resampler;
        bool combined = hello.Resampling && hello.Restorer && hello.Upscaler && _restorer is not null
            && _upscalers.Length == 2 && _resamplers.Length == 2 && plan.Source.Channels == 2
            && !plan.Source.IsDsd && plan.PreUpsampleRate == plan.Source.SampleRate * 2
            && !plan.RemoveUltrasonics && !hello.Restoration.OutputDelta;
        if (combined) _combinedInterpolators = [new TrainingUpsampler(), new TrainingUpsampler()];
        if (combined && plan.ExciteRate > 0)
        {
            _combinedExciters = [new HarmonicExciter(plan.ExciteRate, plan.ConversionRate), new HarmonicExciter(plan.ExciteRate, plan.ConversionRate)];
            _combinedEdges = new CodecBandwidthDetector(plan.ConversionRate, 3);
        }
        Metadata = new DspMetadata(resampler?.DelayOutputSamples ?? 0, resampler?.FlushInputSamples ?? 0,
            resampler?.MaxOutput(_maximumInput) ?? _maximumInput, resampler?.OperationsPerInputSecond ?? 0,
            resampler?.Summary ?? "", _accelerator?.Status ?? acceleration,
            resampler?.Stages.Select(s => s.FlushInputSamples / (double)s.InputRate).DefaultIfEmpty(0).Max() ?? 0,
            restorerStatus, _restorer?.Latency ?? 0, upscalerStatus, _upscalers.FirstOrDefault()?.Latency ?? 0, combined);
        _workers = new ParallelWorkers(channelThreads - 1);
    }
    private static string? LocalModel(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string name = path.Split('/', '\\').Last();
        if (!name.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase) || name.Contains(':')) throw new InvalidDataException("Invalid model name.");
        return Path.Combine(ModelLibrary.Directory, name);
    }
    internal byte[] Process(DspMessage type, byte[] bytes)
    {
        if (type is DspMessage.UpscaleChannels or DspMessage.ResampleChannels) return ProcessChannels(type, bytes);
        if (type == DspMessage.CombinedChannels) return ProcessCombined(bytes);
        if (type == DspMessage.Reset)
        {
            _restorer?.Reset(); foreach (var s in _upscalers) s.Reset(); foreach (var s in _resamplers) s.Reset();
            foreach (var s in _combinedInterpolators) s.Reset();
            foreach (var s in _combinedExciters) s.Reset(); return [];
        }
        using var reader = new BinaryReader(new MemoryStream(bytes, false));
        int channel = reader.ReadInt32(), count = reader.ReadInt32();
        if (count < 0 || count > _maximumInput || channel < 0 || channel >= 8) throw new InvalidDataException("Invalid stage block.");
        var input = new double[count]; DspProtocol.ReadSamples(reader, input);
        double[] output;
        if (type == DspMessage.Restore && _restorer is not null)
        {
            var right = new double[count]; DspProtocol.ReadSamples(reader, right); _restorer.Process(input, right);
            output = new double[count * 2]; input.CopyTo(output, 0); right.CopyTo(output, count);
        }
        else if (type == DspMessage.Upscale && channel < _upscalers.Length)
        { _upscalers[channel].Process(input); output = input; }
        else if (type == DspMessage.Resample && channel < _resamplers.Length)
        { output = new double[_resamplerDefinition!.MaxOutput(count)]; int produced = _resamplers[channel].Process(input, output); Array.Resize(ref output, produced); }
        else throw new InvalidDataException("DSP stage was not selected for this session.");
        if (reader.BaseStream.Position != reader.BaseStream.Length) throw new InvalidDataException("Unexpected stage data.");
        using var result = new MemoryStream(); using var writer = new BinaryWriter(result);
        writer.Write(output.Length); DspProtocol.WriteSamples(writer, output); return result.ToArray();
    }
    private byte[] ProcessCombined(byte[] bytes)
    {
        if (!Metadata.CombinedStages) throw new InvalidDataException("Combined DSP stages are not available for this session.");
        using var reader = new BinaryReader(new MemoryStream(bytes, false));
        if (reader.ReadInt32() != 2) throw new InvalidDataException("Invalid combined channel count.");
        double gain = reader.ReadDouble();
        if (!double.IsFinite(gain) || gain < 0 || gain > 1_000_000) throw new InvalidDataException("Invalid replay gain.");
        var input = new double[2][];
        for (int c = 0; c < 2; c++)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > _maximumInput / 2) throw new InvalidDataException("Invalid combined input length.");
            input[c] = new double[count]; DspProtocol.ReadSamples(reader, input[c]);
        }
        if (input[0].Length != input[1].Length || reader.BaseStream.Position != bytes.Length)
            throw new InvalidDataException("Unexpected combined stage data.");
        double[][] restored = [new double[512], new double[512]];
        double[][] doubled = [new double[1088], new double[1088]];
        double[][] filtered = [new double[_resamplerDefinition!.MaxOutput(1088)], new double[_resamplerDefinition.MaxOutput(1088)]];
        int capacity = _resamplerDefinition.MaxOutput(Math.Min(_maximumInput, input[0].Length * 2 + 128));
        double[][] output = [new double[capacity], new double[capacity]];
        int[] written = [0, 0], counts = [0, 0];
        // Keep the ordinary 512-frame DSP cadence inside the larger network batch. In particular, the
        // exciter follows its source-band estimate at the same sample positions as local processing.
        for (int offset = 0; offset < input[0].Length; offset += 512)
        {
            int count = Math.Min(512, input[0].Length - offset);
            input[0].AsSpan(offset, count).CopyTo(restored[0]);
            input[1].AsSpan(offset, count).CopyTo(restored[1]);
            if (_combinedEdges is not null)
            {
                double[] original = restored[0].AsSpan(0, count).ToArray();
                if (gain != 1) SimdMath.Scale(original, gain);
                _combinedEdges.Push(original);
            }
            _restorer!.Process(restored[0].AsSpan(0, count), restored[1].AsSpan(0, count));
            _workers.For(2, c =>
            {
                Span<double> signal = restored[c].AsSpan(0, count);
                if (gain != 1) SimdMath.Scale(signal, gain);
                counts[c] = _combinedInterpolators[c].Process(signal, doubled[c]);
                Span<double> up = doubled[c].AsSpan(0, counts[c]);
                _upscalers[c].Process(up);
                if (_combinedExciters.Length > 0)
                {
                    (double edge, _) = HarmonicExciter.EdgesFor(_combinedBandwidth, _sourceRate);
                    _combinedExciters[c].SetEdges(edge, HarmonicExciter.AfterRestorerTop * _sourceRate, .5 * _sourceRate);
                    _combinedExciters[c].Process(up);
                }
            });
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            _workers.For(2, c =>
            {
                int produced = _resamplers[c].Process(doubled[c].AsSpan(0, counts[c]), filtered[c]);
                filtered[c].AsSpan(0, produced).CopyTo(output[c].AsSpan(written[c]));
                written[c] += produced;
            });
            _accelerator?.ReportBlock(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalSeconds, counts[0]);
            if (_combinedEdges is not null) _combinedBandwidth = _combinedEdges.Estimate();
        }
        return EncodeChannels(output, written);
    }

    private byte[] ProcessChannels(DspMessage type, byte[] bytes)
    {
        using var reader = new BinaryReader(new MemoryStream(bytes, false));
        int channels = reader.ReadInt32();
        int expected = type == DspMessage.UpscaleChannels ? _upscalers.Length : _resamplers.Length;
        if (channels < 1 || channels != expected) throw new InvalidDataException("Invalid DSP channel count.");
        var input = new double[channels][];
        for (int c = 0; c < channels; c++)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > _maximumInput) throw new InvalidDataException("Invalid DSP channel length.");
            input[c] = new double[count]; DspProtocol.ReadSamples(reader, input[c]);
        }
        if (reader.BaseStream.Position != bytes.Length) throw new InvalidDataException("Unexpected DSP channel data.");
        var output = new double[channels][];
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        _workers.For(channels, c =>
        {
            if (type == DspMessage.UpscaleChannels)
            { _upscalers[c].Process(input[c]); output[c] = input[c]; }
            else
            {
                output[c] = new double[_resamplerDefinition!.MaxOutput(input[c].Length)];
                int produced = _resamplers[c].Process(input[c], output[c]); Array.Resize(ref output[c], produced);
            }
        });
        if (type == DspMessage.ResampleChannels)
            _accelerator?.ReportBlock(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalSeconds, input[0].Length);
        return EncodeChannels(output);
    }
    private static byte[] EncodeChannels(double[][] output, int[]? counts = null)
    {
        int length = 4;
        for (int c = 0; c < output.Length; c++) length = checked(length + 4 + (counts?[c] ?? output[c].Length) * 8);
        byte[] bytes = GC.AllocateUninitializedArray<byte>(length);
        using var result = new MemoryStream(bytes, writable: true); using var writer = new BinaryWriter(result);
        writer.Write(output.Length);
        for (int c = 0; c < output.Length; c++)
        {
            int count = counts?[c] ?? output[c].Length;
            writer.Write(count); DspProtocol.WriteSamples(writer, output[c].AsSpan(0, count));
        }
        return bytes;
    }
    public void Dispose() { _workers.Dispose(); _accelerator?.Dispose(); }
}
