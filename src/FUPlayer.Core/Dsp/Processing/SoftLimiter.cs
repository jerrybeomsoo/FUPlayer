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

    /// <remarks>
    /// It runs at the output rate, 705.6 kHz and more, so the loop keeps its state in locals and wraps its ring indices
    /// by comparison rather than by division: four divisions a sample were most of what it cost.
    /// </remarks>
    public void Process(Span<double> data)
    {
        long[] queueIndex = _queueIndex;
        double[] queueValue = _queueValue;
        double[] delay = _delay;
        int capacity = queueValue.Length;
        int lookahead = _lookahead;
        double threshold = _threshold;
        double attack = _attack;
        double release = _release;
        int head = _queueHead;
        int count = _queueCount;
        int delayPosition = _delayPosition;
        long time = _time;
        double gain = _gain;
        double minimumGain = _minimumGain;
        bool limiting = _limiting;
        long events = 0;

        for (int i = 0; i < data.Length; i++)
        {
            double x = data[i];
            double magnitude = Math.Abs(x);

            // The window is this sample and the look-ahead before it, and what has left it goes first. Taken the
            // other way round, a level that had been falling for the whole window left the queue full of it, and
            // the new sample was written over the oldest entry instead of beside it.
            long oldest = time - lookahead;
            while (count > 0 && queueIndex[head] < oldest)
            {
                head = head + 1 == capacity ? 0 : head + 1;
                count--;
            }

            while (count > 0)
            {
                int back = head + count - 1;
                if (back >= capacity)
                {
                    back -= capacity;
                }

                if (queueValue[back] > magnitude)
                {
                    break;
                }

                count--;
            }

            int slot = head + count;
            if (slot >= capacity)
            {
                slot -= capacity;
            }

            queueIndex[slot] = time;
            queueValue[slot] = magnitude;
            count++;

            double peak = queueValue[head];
            double target = peak > threshold ? threshold / peak : 1.0;
            gain += (target - gain) * (target < gain ? attack : release);
            if (gain < minimumGain)
            {
                minimumGain = gain;
            }

            if (!limiting && gain < 0.999)
            {
                limiting = true;
                events++;
            }
            else if (limiting && gain > 0.9999)
            {
                limiting = false;
            }

            double delayed = delay[delayPosition];
            delay[delayPosition] = x;
            delayPosition = delayPosition + 1 == lookahead ? 0 : delayPosition + 1;
            data[i] = delayed * gain;
            time++;
        }

        _queueHead = head;
        _queueCount = count;
        _delayPosition = delayPosition;
        _time = time;
        _gain = gain;
        _minimumGain = minimumGain;
        _limiting = limiting;
        Events += events;
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
