using System.Numerics;
using System.Text.Json.Serialization;
using FUPlayer.Core.Dsp.Numerics;
using FUPlayer.Core.Dsp.Processing;
using FUPlayer.Core.Dsp.Resampling;
using FUPlayer.Core.Dsp.Restoration;

namespace FUPlayer.Core.Engine;

/// <summary>The stages of the processing core, in the order the signal passes them.</summary>
internal enum CoreStage
{
    /// <summary>The neural restorer, over the source's channels together, at the source rate.</summary>
    Restore,

    /// <summary>Each output channel taken from its source channel, replay gain applied, ultrasonics removed.</summary>
    Map,

    /// <summary>Interpolation to twice the source rate, and the neural upscaler there.</summary>
    Upscale,

    /// <summary>The exciter, and the output delta taken.</summary>
    Excite,

    /// <summary>The chosen filter, to the processing rate.</summary>
    Filter,
}

/// <summary>
/// What the processing core of one plan is made of. The player works it out from the plan and the settings, and a DSP
/// server it hands some of the stages to receives it as it is, so that both build their halves of the same chain.
/// </summary>
internal sealed record CoreLayout
{
    public int SourceChannels { get; init; }

    public int OutputChannels { get; init; }

    /// <summary>The rate of the PCM entering the core: the source's, or what a DSD source is converted to.</summary>
    public int SourceRate { get; init; }

    /// <summary>Twice the source rate while the upscaler or the exciter runs there, otherwise zero.</summary>
    public int UpsampleRate { get; init; }

    public int ProcessingRate { get; init; }

    /// <summary>The restorer runs.</summary>
    public bool Restore { get; init; }

    /// <summary>The upscaler's network runs (rather than only the interpolation in front of it).</summary>
    public bool Upscale { get; init; }

    /// <summary>The rate the exciter runs at, or zero.</summary>
    public int ExciteRate { get; init; }

    /// <summary>The exciter's delay at its rate, which the output delta's dry path is held back by.</summary>
    public int ExciterLatency { get; init; }

    /// <summary>The output delta is taken: what the stages in front of the filter changed, rather than the music.</summary>
    public bool Delta { get; init; }

    /// <summary>The output delta was asked for and nothing runs to take one of; the core passes silence.</summary>
    public bool SilentDelta { get; init; }

    /// <summary>Ultrasonics above 20 kHz are removed from a high-rate source.</summary>
    public bool Clean { get; init; }

    /// <summary>The source is DSD, converted to PCM in front of the core; it enters at <see cref="CoreStage.Upscale"/>.</summary>
    public bool DsdSource { get; init; }

    /// <summary>The filter runs; false for DSD passed through.</summary>
    public bool Filter { get; init; }

    /// <summary>The source is coded, which the upscaler's network is told.</summary>
    public bool Lossy { get; init; } = true;

    /// <summary>Gain on the band the upscaler synthesises above the source's Nyquist frequency.</summary>
    public double UpscalerBandDb { get; init; }

    /// <summary>The restorer's dry signal is carried to the stages after it: an output delta taken after the interpolation or the exciter.</summary>
    [JsonIgnore]
    public bool CarriesDry => Restore && Delta && (UpsampleRate > 0 || ExciteRate > 0);

    /// <summary>Whether a stage does anything for this plan.</summary>
    public bool Has(CoreStage stage) => stage switch
    {
        CoreStage.Restore => Restore,
        CoreStage.Map => !DsdSource,
        CoreStage.Upscale => UpsampleRate > 0,
        CoreStage.Excite => ExciteRate > 0 || (Delta && UpsampleRate > 0),
        _ => Filter,
    };

    /// <summary>The first stage a block enters at.</summary>
    [JsonIgnore]
    public CoreStage Entry => DsdSource ? CoreStage.Upscale : Restore ? CoreStage.Restore : CoreStage.Map;

    /// <summary>The channels a block has before <paramref name="stage"/>: the source's in front of the map, the output's after it.</summary>
    public int ChannelsBefore(CoreStage stage) => stage <= CoreStage.Map && !DsdSource ? SourceChannels : OutputChannels;

    /// <summary>The rate a block has before <paramref name="stage"/>.</summary>
    public int RateBefore(CoreStage stage) => stage switch
    {
        <= CoreStage.Upscale => SourceRate,
        CoreStage.Excite => UpsampleRate > 0 ? UpsampleRate : SourceRate,
        _ => UpsampleRate > 0 ? UpsampleRate : SourceRate,
    };

    /// <summary>Whether a block carries the dry lane between <paramref name="stage"/> and the stage before it.</summary>
    public bool DryBefore(CoreStage stage) => stage switch
    {
        CoreStage.Map => Restore && Delta,
        CoreStage.Upscale => CarriesDry,
        CoreStage.Excite => Delta && UpsampleRate > 0,
        _ => false,
    };
}

/// <summary>One block on its way through the core: a signal per channel, and the dry lane of the output delta when it is carried.</summary>
internal sealed class CoreBlock
{
    public CoreBlock(int channels)
    {
        Signal = new double[channels][];
        Dry = new double[channels][];
        SignalStore = new double[channels][];
        DryStore = new double[channels][];
        Counts = new int[channels];
        for (int c = 0; c < channels; c++)
        {
            Signal[c] = SignalStore[c] = [];
            Dry[c] = DryStore[c] = [];
        }
    }

    /// <summary>Arrays of the block's own, which a block read off the network is written into.</summary>
    public double[][] SignalStore { get; }

    public double[][] DryStore { get; }

    /// <summary>Channels in use: the source's in front of the map, the output's after it.</summary>
    public int Channels { get; set; }

    public double[][] Signal { get; }

    public double[][] Dry { get; }

    /// <summary>Samples in each channel; the dry lane, when carried, has as many.</summary>
    public int[] Counts { get; }

    public bool HasDry { get; set; }

    /// <summary>Replay gain, applied by the map.</summary>
    public double Gain { get; set; } = 1.0;

    /// <summary>Where the exciter takes the music to end for this block, and the top and the wall of what it adds.</summary>
    public double EdgeHz { get; set; }

    public double TopHz { get; set; }

    public double WallHz { get; set; }

    /// <summary>Sets every channel to <paramref name="frames"/> samples of the arrays given, without copying them.</summary>
    public void Wrap(double[][] signal, int channels, int frames)
    {
        Channels = channels;
        HasDry = false;
        for (int c = 0; c < channels; c++)
        {
            Signal[c] = signal[c];
            Counts[c] = frames;
        }
    }
}

/// <summary>The parts of the core that are expensive to make, made by its owner, which also reports where they run.</summary>
internal sealed class CoreParts
{
    /// <summary>The restorer, when <see cref="CoreStage.Restore"/> is built.</summary>
    public INeuralRestorer? Restorer { get; init; }

    /// <summary>The upscaler's network, when <see cref="CoreStage.Upscale"/> is built and the network runs.</summary>
    public NeuralUpscalerModel? UpscalerModel { get; init; }

    /// <summary>The filter, when <see cref="CoreStage.Filter"/> is built.</summary>
    public ResamplerChain? Resampler { get; init; }

    /// <summary>Threads each channel's filter may use.</summary>
    public int FilterParallelism { get; init; } = 1;

    /// <summary>The graphics device the filter may run on.</summary>
    public Dsp.Acceleration.GpuAccelerator? Accelerator { get; init; }

    /// <summary>The ultrasonic filter of <see cref="CoreLayout.Clean"/>, when the map is built.</summary>
    public ResamplerChain? Ultrasonic { get; init; }

    /// <summary>The upscaler's delay, for an output delta whose dry path is built here while the upscaler runs elsewhere.</summary>
    public int UpscalerLatency { get; init; }
}

/// <summary>
/// The stages between the source and the output's own processing: the restorer, the channel map, the upscaler, the
/// exciter with the output delta, and the filter. The player runs all of them, or hands a contiguous run of them to a
/// DSP server, which builds that run of the same core and runs the same code; what a block goes through is the same
/// either way, and so is the sound.
///
/// A block's channels are processed side by side; the restorer takes the source's channels together first. Each
/// stage writes into buffers of its own and points the block at them, so a block entering the core never has its own
/// arrays written to.
/// </summary>
internal sealed class ProcessingCore : IDisposable
{
    private readonly CoreLayout _layout;
    private readonly CoreStage _first;
    private readonly CoreStage _last;
    private readonly ParallelWorkers _workers;
    private readonly CoreParts _parts;
    private readonly Channel[] _channels;
    private readonly double[][] _mapSources, _mapDry;
    private readonly Action<int> _mapWork, _channelWork;
    private CoreBlock _activeBlock = null!;
    private int _mapFrames;
    private bool _mapHasDry;
    private CoreStage _runFrom, _runTo;

    // The restorer's own buffers: two channels always, a mono source played as both.
    private readonly double[][] _restored = [[], []];
    private readonly DelayLine[] _restorerDryDelays = [];
    private readonly double[][] _restorerDry = [];

    public ProcessingCore(CoreLayout layout, CoreStage first, CoreStage last, CoreParts parts, ParallelWorkers workers)
    {
        _layout = layout;
        _first = first;
        _last = last;
        _parts = parts;
        _workers = workers;
        _mapSources = new double[layout.SourceChannels][];
        _mapDry = new double[layout.SourceChannels][];
        _mapWork = c => Map(c, _activeBlock, _mapSources, _mapHasDry ? _mapDry : [], _mapFrames);
        _channelWork = c => RunChannel(c, _activeBlock, _runFrom, _runTo);
        if (Builds(CoreStage.Restore))
        {
            if (parts.Restorer is null)
            {
                throw new InvalidOperationException("The restorer stage needs a restorer.");
            }

            if (layout.Delta)
            {
                _restorerDryDelays = Enumerable.Range(0, layout.SourceChannels).Select(_ => new DelayLine(parts.Restorer.Latency)).ToArray();
                _restorerDry = Enumerable.Range(0, layout.SourceChannels).Select(_ => Array.Empty<double>()).ToArray();
            }
        }

        if (Builds(CoreStage.Filter) && parts.Resampler is null)
        {
            throw new InvalidOperationException("The filter stage needs a filter.");
        }

        _channels = new Channel[layout.OutputChannels];
        for (int c = 0; c < _channels.Length; c++)
        {
            _channels[c] = CreateChannel();
        }
    }

    /// <summary>
    /// Called for each output channel as the map runs here, with the signal before and after the ultrasonic filter: what
    /// the source meters and detectors read when the restorer does not run.
    /// </summary>
    public Action<int, ReadOnlySpan<double>, ReadOnlySpan<double>>? MapTap { get; set; }

    public CoreLayout Layout => _layout;

    /// <summary>The restorer's delay, in source samples, when it is built here.</summary>
    public int RestorerLatency => _parts.Restorer?.Latency ?? 0;

    /// <summary>The upscaler's delay at the upsampled rate, when it is built here.</summary>
    public int UpscalerLatency => _channels.Length > 0 && _channels[0].Upscaler is { } upscaler ? upscaler.Latency : 0;

    /// <summary>The first channel's exciter, when it is built here, for what Now playing says it is doing.</summary>
    public HarmonicExciter? Exciter => _channels.Length > 0 ? _channels[0].Exciter : null;

    /// <summary>Whether this core builds <paramref name="stage"/>.</summary>
    public bool Builds(CoreStage stage) => stage >= _first && stage <= _last && _layout.Has(stage);

    /// <summary>Runs the stages from <paramref name="from"/> to <paramref name="to"/>, those of them that this core builds.</summary>
    public void Run(CoreBlock block, CoreStage from, CoreStage to)
    {
        _activeBlock = block;
        if (from <= CoreStage.Restore && to >= CoreStage.Restore && Builds(CoreStage.Restore))
        {
            Restore(block);
        }

        bool map = from <= CoreStage.Map && to >= CoreStage.Map && Builds(CoreStage.Map);
        if (map || to > CoreStage.Map)
        {
            CoreStage start = (CoreStage)Math.Max((int)from, (int)CoreStage.Upscale);
            if (map)
            {
                // The map reads every source channel, so it runs before the channels split.
                Array.Copy(block.Signal, _mapSources, block.Channels);
                _mapHasDry = block.HasDry;
                if (_mapHasDry) Array.Copy(block.Dry, _mapDry, block.Channels);
                _mapFrames = block.Counts[0];
                _workers.For(_layout.OutputChannels, _mapWork);
                block.Channels = _layout.OutputChannels;
                block.HasDry = _layout.DryBefore(CoreStage.Upscale);
            }

            if (to >= start)
            {
                _runFrom = start; _runTo = to;
                _workers.For(block.Channels, _channelWork);
                block.HasDry = to < CoreStage.Excite && _layout.DryBefore(to + 1);
            }
        }
    }

    public void Reset()
    {
        _parts.Restorer?.Reset();
        foreach (DelayLine delay in _restorerDryDelays)
        {
            delay.Reset();
        }

        foreach (Channel channel in _channels)
        {
            channel.Reset();
        }
    }

    public void Dispose()
    {
    }

    private Channel CreateChannel()
    {
        var channel = new Channel();
        CoreLayout layout = _layout;
        if (Builds(CoreStage.Map) && _parts.Ultrasonic is not null)
        {
            channel.Ultrasonic = _parts.Ultrasonic.CreateState();
        }

        if (Builds(CoreStage.Upscale))
        {
            channel.PreUpsampler = new TrainingUpsampler();
            if (layout.Upscale)
            {
                channel.Upscaler = _parts.UpscalerModel is { } model
                    ? new NeuralUpscaler(model, rate: layout.UpsampleRate, bandFromHz: layout.UpsampleRate / 4.0, bandGainDb: layout.UpscalerBandDb)
                    {
                        Lossy = layout.Lossy,
                    }
                    : throw new InvalidOperationException("The upscaler stage needs its network.");
            }

            if (layout.Delta)
            {
                int upscaler = channel.Upscaler?.Latency ?? 0;
                int exciter = layout.ExciteRate > 0 ? layout.ExciterLatency : 0;
                channel.UpscaledDryDelay = upscaler + exciter == 0 ? null : new DelayLine(upscaler + exciter);
                channel.DryUpsampler = layout.CarriesDry ? new TrainingUpsampler() : null;
            }
        }

        if (Builds(CoreStage.Excite) && layout.ExciteRate > 0)
        {
            channel.Exciter = new HarmonicExciter(layout.ExciteRate, layout.SourceRate);
            if (layout.UpsampleRate == 0 && layout.Delta)
            {
                channel.ExcitedDryDelay = new DelayLine(channel.Exciter.Latency);
            }
        }

        if (Builds(CoreStage.Filter))
        {
            channel.Resampler = _parts.Resampler!.CreateState(_parts.FilterParallelism, _parts.Accelerator);
        }

        return channel;
    }

    /// <summary>
    /// The restorer over the source's channels, both together or a mono source as both, into buffers of its own; with
    /// the output delta, the source delayed by the restorer's latency beside it, which is what its output is compared with.
    /// </summary>
    private void Restore(CoreBlock block)
    {
        int frames = block.Counts[0];
        int sources = block.Channels;
        EnsureCapacity(ref _restored[0], frames);
        EnsureCapacity(ref _restored[1], frames);
        block.Signal[0].AsSpan(0, frames).CopyTo(_restored[0]);
        block.Signal[sources > 1 ? 1 : 0].AsSpan(0, frames).CopyTo(_restored[1]);
        for (int c = 0; c < _restorerDryDelays.Length; c++)
        {
            EnsureCapacity(ref _restorerDry[c], frames);
            block.Signal[c].AsSpan(0, frames).CopyTo(_restorerDry[c]);
            _restorerDryDelays[c].Process(_restorerDry[c].AsSpan(0, frames));
        }

        _parts.Restorer!.Process(_restored[0].AsSpan(0, frames), _restored[1].AsSpan(0, frames));
        for (int c = 0; c < sources; c++)
        {
            block.Signal[c] = _restored[c];
            block.Counts[c] = frames;
            if (_restorerDry.Length > 0)
            {
                block.Dry[c] = _restorerDry[c];
            }
        }

        block.HasDry = _restorerDry.Length > 0;
    }

    private int SourceChannel(int output)
    {
        if (_layout.SourceChannels == 1)
        {
            return output < 2 ? 0 : -1;
        }

        return output < _layout.SourceChannels ? output : -1;
    }

    /// <summary>
    /// One output channel from its source channel with replay gain. After the restorer with the output delta and nothing
    /// in front of the filter to take it later, the delta is taken here, at the source rate.
    /// </summary>
    private void Map(int c, CoreBlock block, double[][] sources, double[][] dry, int frames)
    {
        Channel channel = _channels[c];
        int source = SourceChannel(c);
        EnsureCapacity(ref channel.Conversion, frames + 16);
        Span<double> signal = channel.Conversion.AsSpan(0, frames);
        if (source < 0)
        {
            signal.Clear();
        }
        else
        {
            sources[source].AsSpan(0, frames).CopyTo(signal);
        }

        if (block.Gain != 1.0)
        {
            SimdMath.Scale(signal, block.Gain);
        }

        bool carried = false;
        if (dry.Length > 0)
        {
            EnsureCapacity(ref channel.MapDry, frames);
            Span<double> delayed = channel.MapDry.AsSpan(0, frames);
            if (source < 0)
            {
                delayed.Clear();
            }
            else
            {
                dry[source].AsSpan(0, frames).CopyTo(delayed);
                if (block.Gain != 1.0)
                {
                    SimdMath.Scale(delayed, block.Gain);
                }
            }

            if (_layout.UpsampleRate == 0 && _layout.ExciteRate == 0)
            {
                for (int i = 0; i < frames; i++)
                {
                    signal[i] -= delayed[i];
                }
            }
            else
            {
                carried = true;
            }
        }

        ReadOnlySpan<double> before = signal;
        int count = frames;
        if (channel.Ultrasonic is not null)
        {
            EnsureCapacity(ref channel.Clean, _parts.Ultrasonic!.MaxOutput(frames) + 16);
            count = channel.Ultrasonic.Process(signal, channel.Clean);
            block.Signal[c] = channel.Clean;
        }
        else
        {
            block.Signal[c] = channel.Conversion;
        }

        MapTap?.Invoke(c, before, block.Signal[c].AsSpan(0, count));
        if (_layout.SilentDelta)
        {
            block.Signal[c].AsSpan(0, count).Clear();
        }

        block.Counts[c] = count;
        if (carried)
        {
            block.Dry[c] = channel.MapDry;
        }
    }

    private void RunChannel(int c, CoreBlock block, CoreStage from, CoreStage to)
    {
        Channel channel = _channels[c];
        if (from <= CoreStage.Upscale && to >= CoreStage.Upscale && Builds(CoreStage.Upscale))
        {
            Upscale(c, channel, block);
        }

        if (from <= CoreStage.Excite && to >= CoreStage.Excite && Builds(CoreStage.Excite))
        {
            Excite(c, channel, block);
        }

        if (from <= CoreStage.Filter && to >= CoreStage.Filter && Builds(CoreStage.Filter))
        {
            int count = block.Counts[c];
            EnsureCapacity(ref channel.Processing, _parts.Resampler!.MaxOutput(count) + 16);
            int produced = channel.Resampler!.Process(block.Signal[c].AsSpan(0, count), channel.Processing);
            block.Signal[c] = channel.Processing;
            block.Counts[c] = produced;
        }
    }

    /// <summary>
    /// Interpolation to twice the source rate and the upscaler's network there. With the output delta, the dry path is
    /// interpolated alongside, the unrestored source after the restorer or this signal otherwise, and held back by the
    /// upscaler's and the exciter's delays, ready to be taken off after the exciter.
    /// </summary>
    private void Upscale(int c, Channel channel, CoreBlock block)
    {
        int count = block.Counts[c];
        EnsureCapacity(ref channel.Upscaled, (2 * count) + 64);
        int doubled = channel.PreUpsampler!.Process(block.Signal[c].AsSpan(0, count), channel.Upscaled);
        Span<double> up = channel.Upscaled.AsSpan(0, doubled);
        if (_layout.Delta)
        {
            EnsureCapacity(ref channel.UpscaledDry, channel.Upscaled.Length);
            if (channel.DryUpsampler is not null)
            {
                channel.DryUpsampler.Process(block.Dry[c].AsSpan(0, count), channel.UpscaledDry);
            }
            else
            {
                up.CopyTo(channel.UpscaledDry);
            }

            channel.UpscaledDryDelay?.Process(channel.UpscaledDry.AsSpan(0, doubled));
            block.Dry[c] = channel.UpscaledDry;
        }

        channel.Upscaler?.Process(up);
        block.Signal[c] = channel.Upscaled;
        block.Counts[c] = doubled;
    }

    /// <summary>
    /// The exciter: at twice the source rate after the upscaler, in place, or at the source rate on a copy of its own;
    /// then the output delta, the dry path taken off what the stages made of it.
    /// </summary>
    private void Excite(int c, Channel channel, CoreBlock block)
    {
        int count = block.Counts[c];
        if (_layout.UpsampleRate > 0)
        {
            Span<double> up = block.Signal[c].AsSpan(0, count);
            if (channel.Exciter is { } exciter)
            {
                exciter.SetEdges(block.EdgeHz, block.TopHz, block.WallHz);
                exciter.Process(up);
            }

            if (_layout.Delta)
            {
                ReadOnlySpan<double> dry = block.Dry[c].AsSpan(0, count);
                for (int i = 0; i < count; i++)
                {
                    up[i] -= dry[i];
                }
            }

            return;
        }

        EnsureCapacity(ref channel.Excited, count);
        Span<double> excited = channel.Excited.AsSpan(0, count);
        ReadOnlySpan<double> signal = block.Signal[c].AsSpan(0, count);
        signal.CopyTo(excited);
        if (channel.ExcitedDryDelay is not null)
        {
            EnsureCapacity(ref channel.ExcitedDry, count);
            Span<double> excitedDry = channel.ExcitedDry.AsSpan(0, count);
            (block.HasDry ? block.Dry[c].AsSpan(0, count) : signal).CopyTo(excitedDry);
            channel.ExcitedDryDelay.Process(excitedDry);
        }

        channel.Exciter!.SetEdges(block.EdgeHz, block.TopHz, block.WallHz);
        channel.Exciter.Process(excited);
        if (channel.ExcitedDryDelay is not null)
        {
            for (int i = 0; i < count; i++)
            {
                excited[i] -= channel.ExcitedDry[i];
            }
        }

        block.Signal[c] = channel.Excited;
    }

    private static void EnsureCapacity(ref double[] array, int length)
    {
        if (array.Length < length)
        {
            array = new double[(int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(16, length))];
        }
    }

    private sealed class Channel
    {
        public ResamplerChainState? Ultrasonic;
        public TrainingUpsampler? PreUpsampler;
        public NeuralUpscaler? Upscaler;
        public TrainingUpsampler? DryUpsampler;
        public DelayLine? UpscaledDryDelay;
        public HarmonicExciter? Exciter;
        public DelayLine? ExcitedDryDelay;
        public IRateStageState? Resampler;
        public double[] Conversion = [];
        public double[] MapDry = [];
        public double[] Clean = [];
        public double[] Upscaled = [];
        public double[] UpscaledDry = [];
        public double[] Excited = [];
        public double[] ExcitedDry = [];
        public double[] Processing = [];

        public void Reset()
        {
            Ultrasonic?.Reset();
            PreUpsampler?.Reset();
            Upscaler?.Reset();
            DryUpsampler?.Reset();
            UpscaledDryDelay?.Reset();
            Exciter?.Reset();
            ExcitedDryDelay?.Reset();
            Resampler?.Reset();
        }
    }
}
