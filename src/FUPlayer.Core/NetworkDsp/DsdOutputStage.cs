using System.Numerics;
using FUPlayer.Core.Dsp.Design;
using FUPlayer.Core.Dsp.Modulation;
using FUPlayer.Core.Dsp.Numerics;
using FUPlayer.Core.Dsp.Processing;
using FUPlayer.Core.Dsp.Resampling;
using FUPlayer.Core.Engine;
using FUPlayer.Core.Output;
using FUPlayer.Core.Settings;

namespace FUPlayer.Core.NetworkDsp;

/// <summary>The same final DSD path as the player, executed after a remotely owned filter.</summary>
internal sealed class DsdOutputStage
{
    private readonly PlaybackPlan _plan;
    private readonly ParallelWorkers _workers;
    private readonly ResamplerChain _chain;
    private readonly bool _doubleRate;
    private readonly int _alignment;
    private readonly Channel[] _channels;

    private sealed class Channel
    {
        public readonly SmoothedGain Volume = new(1.0);
        public SoftLimiter? Limiter;
        public DelayLine? Delay;
        public double Trim;
        public required DeltaSigmaModulator Modulator;
        public required ResamplerChainState Interpolator;
        public double[] Buffer = [];
        public byte[] Output = [];
        public int Carry, Count;
    }

    internal DsdOutputStage(PlaybackPlan plan, SpeakerSettings speakers, ParallelWorkers workers)
    {
        int dsdRate = plan.Output.DsdRate;
        bool standardRate = new[] { 44100, 48000 }.Any(rate => dsdRate % rate == 0
            && dsdRate / rate is >= 64 and <= 1024 && BitOperations.IsPow2((uint)(dsdRate / rate)));
        if (!standardRate || plan.Modulator is null || plan.ProcessingRate < 44100 || plan.ProcessingRate > 768000
            || dsdRate % plan.ProcessingRate != 0 || !BitOperations.IsPow2((uint)(dsdRate / plan.ProcessingRate)))
            throw new InvalidDataException("Invalid server DSD output plan.");
        if (speakers.Channels.Count > 8 || speakers.Channels.Any(c => !double.IsFinite(c.LevelDb) || c.LevelDb is < -120 or > 24
            || !double.IsFinite(c.DistanceCm) || c.DistanceCm is < 0 or > 10000))
            throw new InvalidDataException("Invalid server speaker settings.");
        _plan = plan; _workers = workers;
        _doubleRate = DspPipeline.ShouldFoldLastInterpolation(plan);
        _alignment = (plan.Output.Kind == OutputSampleKind.Dop ? 16 : 8) / (_doubleRate ? 2 : 1);
        _chain = DspPipeline.CreateModulatorInterpolator(plan, _doubleRate);
        var preset = ModulatorCatalog.All.FirstOrDefault(p => p.Id == plan.Modulator!.Id);
        if (preset is null || preset.Order != plan.Modulator!.Order || preset.MaxGain != plan.Modulator.MaxGain
            || preset.BandwidthHz != plan.Modulator.BandwidthHz || preset.ScaleBandwidth != plan.Modulator.ScaleBandwidth)
            throw new InvalidDataException("Unsupported server modulator preset.");
        NoiseTransferFunction ntf = ModulatorCatalog.DesignNtf(preset, plan.Output.DsdRate);
        var (gains, delays) = DspPipeline.ComputeSpeakerTrims(speakers, plan, plan.Output.Channels);
        _channels = Enumerable.Range(0, plan.Output.Channels).Select(c => new Channel
        {
            Trim = gains[c], Delay = delays[c] > 0 ? new DelayLine(delays[c]) : null,
            Limiter = plan.Limiter ? new SoftLimiter(plan.ProcessingRate) : null,
            Modulator = new DeltaSigmaModulator(ntf), Interpolator = _chain.CreateState(),
        }).ToArray();
    }

    internal double LatencySeconds => (_channels[0].Limiter?.LatencySamples ?? 0) / (double)_plan.ProcessingRate
        + _chain.DelayOutputSamples / _chain.OutputRate;
    internal long LimiterEvents => _channels.Sum(c => c.Limiter?.Events ?? 0);
    internal long ModulatorResets => _channels.Sum(c => c.Modulator.InstabilityResets);
    internal double MinimumGain => _channels.Min(c => c.Limiter?.TakeMinimumGain() ?? 1);
    internal double PeakModulation => _channels.Max(c => c.Modulator.TakePeakModulation());
    internal byte[][] Output => _channels.Select(c => c.Output).ToArray();
    internal int Count => _channels[0].Count;

    internal void Reset(double volume)
    {
        foreach (Channel channel in _channels)
        {
            channel.Volume.Jump(volume); channel.Limiter?.Reset(); channel.Delay?.Reset();
            channel.Interpolator.Reset(); channel.Modulator.Reset(); channel.Carry = channel.Count = 0;
        }
    }

    internal void Process(CoreBlock block, double volume) => _workers.For(_channels.Length, c =>
    {
        Channel channel = _channels[c];
        Span<double> signal = block.Signal[c].AsSpan(0, block.Counts[c]);
        channel.Volume.Apply(signal, volume, Math.Max(1, (int)(_plan.ProcessingRate * .03)));
        channel.Limiter?.Process(signal);
        if (channel.Trim != 1) SimdMath.Scale(signal, channel.Trim);
        channel.Delay?.Process(signal);
        int ratio = _plan.Output.DsdRate / _plan.ProcessingRate;
        int chunk = Math.Max(1, (1 << 20) / ratio);
        Grow(ref channel.Output, (int)(((long)signal.Length * ratio + 32) / 8) + 16);
        int count = 0;
        for (int offset = 0; offset < signal.Length; offset += chunk)
        {
            int take = Math.Min(chunk, signal.Length - offset), carry = channel.Carry;
            Grow(ref channel.Buffer, carry + _chain.MaxOutput(take) + 32);
            int total = carry + channel.Interpolator.Process(signal.Slice(offset, take), channel.Buffer.AsSpan(carry));
            int usable = total - total % _alignment;
            count += channel.Modulator.Process(channel.Buffer.AsSpan(0, usable), channel.Output.AsSpan(count), _doubleRate);
            channel.Carry = total - usable;
            channel.Buffer.AsSpan(usable, channel.Carry).CopyTo(channel.Buffer);
        }
        channel.Count = count;
    });

    private static void Grow<T>(ref T[] buffer, int count)
    {
        if (buffer.Length < count) buffer = new T[(int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(16, count))];
    }
}
