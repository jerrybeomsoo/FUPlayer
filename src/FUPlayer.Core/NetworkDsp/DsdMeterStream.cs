using FUPlayer.Core.Dsp.Analysis;
using FUPlayer.Core.Engine;

namespace FUPlayer.Core.NetworkDsp;

/// <summary>Reduces only display PCM after DSD encoding; it never changes the audio buffers.</summary>
internal sealed class DsdMeterStream
{
    private readonly AnalysisDecimator[] _channels;
    private readonly CoreBlock _meter;

    internal DsdMeterStream(int rate, int channels, int divisor)
    {
        var design = AnalysisDecimator.Design.For(rate, divisor);
        _channels = Enumerable.Range(0, channels).Select(_ => AnalysisDecimator.Create(design)).ToArray();
        _meter = new CoreBlock(channels) { Channels = channels };
    }

    internal CoreBlock Process(CoreBlock audio)
    {
        for (int c = 0; c < _channels.Length; c++)
        {
            ReadOnlySpan<double> samples = _channels[c].Process(audio.Signal[c].AsSpan(0, audio.Counts[c]));
            if (_meter.Signal[c].Length < samples.Length) _meter.Signal[c] = new double[samples.Length];
            samples.CopyTo(_meter.Signal[c]);
            _meter.Counts[c] = samples.Length;
        }
        return _meter;
    }

    internal void Reset() { foreach (var channel in _channels) channel.Reset(); }
}
