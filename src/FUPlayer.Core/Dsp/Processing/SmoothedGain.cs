using FUPlayer.Core.Dsp.Numerics;

namespace FUPlayer.Core.Dsp.Processing;

/// <summary>Gain stage that ramps linearly to new targets to avoid zipper noise.</summary>
public sealed class SmoothedGain
{
    private double _current;
    private double _target;
    private double _step;
    private int _remaining;

    public SmoothedGain(double initialGain)
    {
        Jump(initialGain);
    }

    public double Current => _current;

    /// <summary>Sets the gain immediately (e.g. after a seek, while the output is silent).</summary>
    public void Jump(double gain)
    {
        _current = _target = gain;
        _remaining = 0;
        _step = 0;
    }

    /// <summary>Applies the gain, moving toward <paramref name="target"/> over <paramref name="rampSamples"/> samples.</summary>
    public void Apply(Span<double> data, double target, int rampSamples)
    {
        if (data.IsEmpty)
        {
            return;
        }

        if (target != _target)
        {
            _target = target;
            _remaining = Math.Max(1, rampSamples);
            _step = (target - _current) / _remaining;
        }

        if (_remaining == 0 || Math.Abs(target - _current) < 1e-12)
        {
            _current = target;
            _remaining = 0;
            if (target != 1.0)
            {
                SimdMath.Scale(data, target);
            }

            return;
        }

        int count = Math.Min(_remaining, data.Length);
        double gain = _current;
        for (int i = 0; i < count; i++)
        {
            gain += _step;
            data[i] *= gain;
        }

        _remaining -= count;
        _current = _remaining == 0 ? target : gain;
        if (count < data.Length)
        {
            SimdMath.Scale(data[count..], _current);
        }
    }
}
