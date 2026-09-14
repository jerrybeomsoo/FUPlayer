using FUPlayer.Core.Dsp.Numerics;

namespace FUPlayer.Core.Dsp.Analysis;

/// <summary>Level readings of one channel in dBFS.</summary>
public readonly record struct ChannelLevel(double PeakDb, double RmsDb, double HoldDb, bool Over);

/// <summary>Thread-safe peak / RMS / peak-hold meter fed from the DSP thread and read from the UI.</summary>
public sealed class LevelMeter
{
    public const double FloorDb = -120.0;
    private const double RmsTimeConstantSeconds = 0.3;
    private const double HoldSeconds = 1.5;

    private readonly object _gate = new();
    private readonly double[] _peak;
    private readonly double[] _meanSquare;
    private readonly double[] _hold;
    private readonly long[] _holdAge;
    private readonly bool[] _over;

    public LevelMeter(int channels, int sampleRate)
    {
        Channels = channels;
        SampleRate = sampleRate;
        _peak = new double[channels];
        _meanSquare = new double[channels];
        _hold = new double[channels];
        _holdAge = new long[channels];
        _over = new bool[channels];
    }

    public int Channels { get; }

    public int SampleRate { get; }

    public void Process(int channel, ReadOnlySpan<double> data)
    {
        if (data.IsEmpty || (uint)channel >= (uint)Channels)
        {
            return;
        }

        double peak = SimdMath.MaxAbs(data);
        double meanSquare = SimdMath.SumOfSquares(data) / data.Length;
        double decay = Math.Exp(-data.Length / (RmsTimeConstantSeconds * SampleRate));
        lock (_gate)
        {
            _peak[channel] = Math.Max(_peak[channel], peak);
            _meanSquare[channel] = _meanSquare[channel] * decay + meanSquare * (1.0 - decay);
            if (peak >= _hold[channel])
            {
                _hold[channel] = peak;
                _holdAge[channel] = 0;
            }
            else
            {
                _holdAge[channel] += data.Length;
            }

            if (peak >= 1.0)
            {
                _over[channel] = true;
            }
        }
    }

    /// <summary>Reads all channels; instantaneous peaks restart after each read.</summary>
    public ChannelLevel[] Read()
    {
        var result = new ChannelLevel[Channels];
        lock (_gate)
        {
            long holdSamples = (long)(HoldSeconds * SampleRate);
            for (int c = 0; c < Channels; c++)
            {
                if (_holdAge[c] > holdSamples)
                {
                    _hold[c] = _peak[c];
                    _holdAge[c] = 0;
                }

                result[c] = new ChannelLevel(ToDb(_peak[c]), ToDb(Math.Sqrt(_meanSquare[c])), ToDb(_hold[c]), _over[c]);
                _peak[c] = 0.0;
            }
        }

        return result;
    }

    public void Reset()
    {
        lock (_gate)
        {
            Array.Clear(_peak);
            Array.Clear(_meanSquare);
            Array.Clear(_hold);
            Array.Clear(_holdAge);
            Array.Clear(_over);
        }
    }

    public static double ToDb(double value) => value > 0.0 ? Math.Max(FloorDb, 20.0 * Math.Log10(value)) : FloorDb;
}
