using FUPlayer.Core.Dsp.Numerics;

namespace FUPlayer.Core.Dsp.Processing;

/// <summary>Gain stage that ramps linearly to new targets to avoid zipper noise.</summary>
public sealed class SmoothedGain
{
    private double _current;

    public SmoothedGain(double initialGain)
    {
        _current = initialGain;
    }

    public double Current => _current;

    /// <summary>Sets the gain immediately (e.g. after a seek, while the output is silent).</summary>
    public void Jump(double gain) => _current = gain;

    /// <summary>Applies the gain, moving toward <paramref name="target"/> over <paramref name="rampSamples"/> samples.</summary>
    public void Apply(Span<double> data, double target, int rampSamples)
    {
        if (data.IsEmpty)
        {
            return;
        }

        if (Math.Abs(target - _current) < 1e-12)
        {
            _current = target;
            if (target != 1.0)
            {
                SimdMath.Scale(data, target);
            }

            return;
        }

        rampSamples = Math.Max(1, rampSamples);
        int count = Math.Min(rampSamples, data.Length);
        double step = (target - _current) / rampSamples;
        double gain = _current;
        for (int i = 0; i < count; i++)
        {
            gain += step;
            data[i] *= gain;
        }

        _current = count == rampSamples ? target : gain;
        if (count < data.Length)
        {
            SimdMath.Scale(data[count..], _current);
        }
    }
}
