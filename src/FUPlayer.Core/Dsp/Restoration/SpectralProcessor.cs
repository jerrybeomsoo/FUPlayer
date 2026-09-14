using FUPlayer.Core.Dsp.Numerics;

namespace FUPlayer.Core.Dsp.Restoration;

/// <summary>
/// Short-time Fourier processing with weighted overlap-add.
///
/// The frame is windowed going in and again coming out, at quarter overlap, and the sum of the
/// squared windows is divided out so that a stage which changes nothing returns its input sample for
/// sample, delayed by <see cref="Latency"/>.
///
/// The second window is what makes the stages usable. Writing into a bin adds a windowed sinusoid to
/// the output, and its skirts reach frequencies that were never meant to be touched: with an analysis
/// window alone a rebuilt band leaks about 2 percent of its amplitude back under the cutoff, which is
/// audible and wrong. Squaring the window drops the skirts by roughly 30 dB, and the leakage with it.
///
/// The first <see cref="FftSize"/> samples are the windows warming up and are attenuated.
/// </summary>
public abstract class SpectralProcessor
{
    private readonly RealFftPlan _plan;
    private readonly double[] _window;
    private readonly double[] _normalisation;
    private readonly double[] _pending;
    private readonly double[] _frame;
    private readonly double[] _re;
    private readonly double[] _im;
    private readonly double[] _overlap;
    private readonly double[] _ready;

    private int _pendingFill;
    private int _readRead;
    private int _readWrite;
    private int _readCount;

    protected SpectralProcessor(int sampleRate, int fftSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        if (fftSize < 64 || (fftSize & (fftSize - 1)) != 0)
        {
            throw new ArgumentException("The transform size must be a power of two of at least 64.", nameof(fftSize));
        }

        SampleRate = sampleRate;
        FftSize = fftSize;
        Hop = fftSize / 4;

        _plan = new RealFftPlan(fftSize);
        _window = new double[fftSize];
        _pending = new double[fftSize];
        _frame = new double[fftSize];
        _re = new double[fftSize];
        _im = new double[fftSize];
        _overlap = new double[fftSize];
        _ready = new double[fftSize * 2];

        for (int i = 0; i < fftSize; i++)
        {
            _window[i] = 0.5 - (0.5 * Math.Cos(2.0 * Math.PI * i / fftSize));
        }

        // What the overlapping squared windows add up to at each position within a hop. For a Hann
        // window at quarter overlap this is flat, but computing it keeps the reconstruction exact
        // whatever window or overlap is chosen later.
        _normalisation = new double[Hop];
        for (int i = 0; i < Hop; i++)
        {
            double total = 0.0;
            for (int at = i; at < fftSize; at += Hop)
            {
                total += _window[at] * _window[at];
            }

            _normalisation[i] = total;
        }
    }

    public int SampleRate { get; }

    public int FftSize { get; }

    public int Hop { get; }

    public int Bins => _plan.Bins;

    /// <summary>Samples of delay the stage adds.</summary>
    public int Latency => FftSize;

    public double BinHz => (double)SampleRate / FftSize;

    /// <summary>Processes in place. Each sample written entered <see cref="Latency"/> samples earlier.</summary>
    public void Process(Span<double> samples)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            // Taken before the sample goes in, so the delay is exactly one transform rather than one
            // transform less a sample.
            double ready = Take();

            _pending[_pendingFill++] = samples[i];
            if (_pendingFill == FftSize)
            {
                Analyse();
                _pending.AsSpan(Hop, FftSize - Hop).CopyTo(_pending);
                _pendingFill = FftSize - Hop;
            }

            samples[i] = ready;
        }
    }

    public virtual void Reset()
    {
        Array.Clear(_pending);
        Array.Clear(_overlap);
        Array.Clear(_ready);
        _pendingFill = 0;
        _readRead = 0;
        _readWrite = 0;
        _readCount = 0;
    }

    /// <summary>Changes the half spectrum in place. Bin <c>b</c> sits at <c>b * BinHz</c> hertz.</summary>
    protected abstract void Transform(Span<double> re, Span<double> im);

    private double Take()
    {
        if (_readCount == 0)
        {
            return 0.0;
        }

        double value = _ready[_readRead];
        _readRead = (_readRead + 1) % _ready.Length;
        _readCount--;
        return value;
    }

    private void Analyse()
    {
        for (int i = 0; i < FftSize; i++)
        {
            _frame[i] = _pending[i] * _window[i];
        }

        _plan.Forward(_frame, _re, _im);
        Transform(_re.AsSpan(0, Bins), _im.AsSpan(0, Bins));
        _plan.Inverse(_re, _im, _frame);

        for (int i = 0; i < FftSize; i++)
        {
            _overlap[i] += _frame[i] * _window[i];
        }

        // The oldest hop has now been covered by every window that reaches it.
        for (int i = 0; i < Hop; i++)
        {
            _ready[_readWrite] = _overlap[i] / _normalisation[i];
            _readWrite = (_readWrite + 1) % _ready.Length;
            _readCount++;
        }

        _overlap.AsSpan(Hop, FftSize - Hop).CopyTo(_overlap);
        Array.Clear(_overlap, FftSize - Hop, Hop);
    }
}
