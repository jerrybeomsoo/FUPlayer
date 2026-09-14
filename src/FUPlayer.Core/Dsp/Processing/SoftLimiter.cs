namespace FUPlayer.Core.Dsp.Processing;

/// <summary>
/// Per-channel look-ahead peak limiter. A sliding maximum over the look-ahead window drives a gain that
/// attacks within the window (so peaks are already reduced when they reach the output) and releases slowly.
/// Adds <see cref="LatencySamples"/> of delay.
/// </summary>
public sealed class SoftLimiter
{
    private readonly double _threshold;
    private readonly int _lookahead;
    private readonly double _attack;
    private readonly double _release;
    private readonly double[] _delay;
    private readonly long[] _queueIndex;
    private readonly double[] _queueValue;
    private int _delayPosition;
    private int _queueHead;
    private int _queueCount;
    private long _time;
    private double _gain = 1.0;
    private double _minimumGain = 1.0;
    private bool _limiting;

    public SoftLimiter(int sampleRate, double thresholdDb = -0.1, double lookaheadMs = 1.0, double releaseMs = 250.0)
    {
        _threshold = Math.Pow(10.0, thresholdDb / 20.0);
        _lookahead = Math.Max(1, (int)Math.Round(sampleRate * lookaheadMs / 1000.0));
        _attack = 1.0 - Math.Exp(-6.0 / _lookahead);
        _release = 1.0 - Math.Exp(-1.0 / Math.Max(1.0, sampleRate * releaseMs / 1000.0));
        _delay = new double[_lookahead];
        _queueIndex = new long[_lookahead + 1];
        _queueValue = new double[_lookahead + 1];
    }

    public int LatencySamples => _lookahead;

    /// <summary>Number of limiting episodes since creation.</summary>
    public long Events { get; private set; }

    /// <summary>Returns and clears the lowest gain applied since the last call.</summary>
    public double TakeMinimumGain()
    {
        double value = _minimumGain;
        _minimumGain = _gain;
        return value;
    }

    public void Process(Span<double> data)
    {
        int capacity = _queueValue.Length;
        for (int i = 0; i < data.Length; i++)
        {
            double x = data[i];
            double magnitude = Math.Abs(x);

            while (_queueCount > 0)
            {
                int back = (_queueHead + _queueCount - 1) % capacity;
                if (_queueValue[back] > magnitude)
                {
                    break;
                }

                _queueCount--;
            }

            int slot = (_queueHead + _queueCount) % capacity;
            _queueIndex[slot] = _time;
            _queueValue[slot] = magnitude;
            _queueCount++;

            while (_queueIndex[_queueHead] < _time - _lookahead)
            {
                _queueHead = (_queueHead + 1) % capacity;
                _queueCount--;
            }

            double peak = _queueValue[_queueHead];
            double target = peak > _threshold ? _threshold / peak : 1.0;
            _gain += (target - _gain) * (target < _gain ? _attack : _release);
            if (_gain < _minimumGain)
            {
                _minimumGain = _gain;
            }

            if (!_limiting && _gain < 0.999)
            {
                _limiting = true;
                Events++;
            }
            else if (_limiting && _gain > 0.9999)
            {
                _limiting = false;
            }

            double delayed = _delay[_delayPosition];
            _delay[_delayPosition] = x;
            _delayPosition = (_delayPosition + 1) % _lookahead;
            data[i] = delayed * _gain;
            _time++;
        }
    }

    public void Reset()
    {
        Array.Clear(_delay);
        _delayPosition = 0;
        _queueHead = 0;
        _queueCount = 0;
        _time = 0;
        _gain = 1.0;
        _minimumGain = 1.0;
        _limiting = false;
    }
}
