using FUPlayer.Core.Dsp.Numerics;

namespace FUPlayer.Core.Dsp.Analysis;

/// <summary>What the long-term spectrum says about how the material was coded.</summary>
public enum BandwidthVerdict
{
    /// <summary>Not enough loud audio has been measured yet.</summary>
    Unknown,

    /// <summary>Energy continues to the edge of the band: nothing threw the top octave away.</summary>
    FullBand,

    /// <summary>A brick wall well below Nyquist, which is what a perceptual codec leaves behind.</summary>
    BandLimited,
}

/// <param name="Verdict">What the spectrum supports.</param>
/// <param name="CutoffHz">Highest frequency still carrying energy, or the Nyquist rate when nothing was cut.</param>
/// <param name="EdgeDropDb">How far the spectrum falls across the kilohertz above the cutoff.</param>
/// <param name="Seconds">Loud audio measured so far.</param>
public readonly record struct BandwidthEstimate(BandwidthVerdict Verdict, double CutoffHz, double EdgeDropDb, double Seconds)
{
    public string Describe() => Verdict switch
    {
        BandwidthVerdict.FullBand => $"full band to {CutoffHz / 1000.0:0.#} kHz",
        BandwidthVerdict.BandLimited => $"band-limited at {CutoffHz / 1000.0:0.#} kHz, {EdgeDropDb:0} dB edge",
        _ => "measuring",
    };
}

/// <summary>
/// Estimates where a signal's spectrum ends, by averaging many transforms of the loud parts.
///
/// A perceptual codec discards everything above a cutoff and leaves a brick wall there: 16 kHz for
/// MP3 at 128 kbit/s, 19 to 20 kHz for AAC and Opus at streaming rates. Material that was never
/// through a codec carries energy, even if only dither and noise, right up to its Nyquist rate. The
/// two are told apart by the steepness of the edge rather than by the cutoff alone, because quiet or
/// dull music also lacks treble but tails off gently instead of stopping dead.
///
/// This measures the signal, so it says what the spectrum looks like, not what the file was. Anything
/// that has been through a steep lowpass reads the same way, which is the honest limit of the method.
/// </summary>
public sealed class CodecBandwidthDetector
{
    /// <summary>Long enough to resolve a codec edge to about 50 Hz at 96 kHz.</summary>
    public const int FftSize = 4096;

    /// <summary>Frames below this RMS are skipped: silence carries no evidence either way.</summary>
    private const double SilenceFloor = 1e-4;

    /// <summary>Reference band, above the bass where most of the energy is and below any codec cutoff.</summary>
    private const double ReferenceLowHz = 300.0;
    private const double ReferenceHighHz = 5_000.0;

    /// <summary>Energy this far under the reference band counts as nothing.</summary>
    private const double PresenceDb = -75.0;

    /// <summary>A codec edge falls at least this far within a kilohertz; a natural roll-off does not.</summary>
    private const double EdgeDropDb = 24.0;

    /// <summary>Below this fraction of Nyquist a cutoff is worth reporting at all.</summary>
    private const double CutoffFraction = 0.92;

    private const double MinimumSeconds = 1.0;

    private readonly RealFftPlan _plan = new(FftSize);
    private readonly double[] _window = new double[FftSize];
    private readonly double[] _frame = new double[FftSize];
    private readonly double[] _re = new double[FftSize];
    private readonly double[] _im = new double[FftSize];
    private readonly double[] _sum;
    private readonly double[] _pending = new double[FftSize];

    private readonly int _sampleRate;
    private int _pendingCount;
    private long _frames;
    private long _samples;

    public CodecBandwidthDetector(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        _sampleRate = sampleRate;
        _sum = new double[_plan.Bins];

        // Blackman-Harris. Its first sidelobe is 92 dB down and the skirt falls away quickly, which
        // matters here: a Hann window leaks a full-level band across the wall for ten bins, and the
        // edge is then reported over a hundred hertz above where it really is.
        for (int i = 0; i < FftSize; i++)
        {
            double t = 2.0 * Math.PI * i / FftSize;
            _window[i] = 0.35875
                - (0.48829 * Math.Cos(t))
                + (0.14128 * Math.Cos(2.0 * t))
                - (0.01168 * Math.Cos(3.0 * t));
        }
    }

    public double Seconds => (double)_samples / _sampleRate;

    /// <summary>Adds one channel's samples. Feed a single channel, or the mid of a stereo pair.</summary>
    public void Push(ReadOnlySpan<double> samples)
    {
        foreach (double sample in samples)
        {
            _pending[_pendingCount++] = sample;
            if (_pendingCount < FftSize)
            {
                continue;
            }

            Accumulate();

            // Half-overlap, so a short loud passage still contributes several transforms.
            _pending.AsSpan(FftSize / 2, FftSize / 2).CopyTo(_pending);
            _pendingCount = FftSize / 2;
        }

        _samples += samples.Length;
    }

    public BandwidthEstimate Estimate()
    {
        if (_frames == 0 || Seconds < MinimumSeconds)
        {
            return new BandwidthEstimate(BandwidthVerdict.Unknown, 0.0, 0.0, Seconds);
        }

        double nyquist = _sampleRate / 2.0;
        double binHz = (double)_sampleRate / FftSize;
        int bins = _sum.Length;

        double reference = Average(ReferenceLowHz, ReferenceHighHz, binHz, bins);
        if (reference <= 0.0)
        {
            return new BandwidthEstimate(BandwidthVerdict.Unknown, 0.0, 0.0, Seconds);
        }

        double floor = reference * Math.Pow(10.0, PresenceDb / 10.0);

        // Walk down from Nyquist to the highest bin that still carries something.
        int top = bins - 1;
        while (top > 0 && _sum[top] / _frames < floor)
        {
            top--;
        }

        double cutoff = top * binHz;
        if (cutoff >= nyquist * CutoffFraction)
        {
            return new BandwidthEstimate(BandwidthVerdict.FullBand, nyquist, 0.0, Seconds);
        }

        // How hard the edge falls: the kilohertz below the cutoff against the kilohertz above it.
        double below = Average(Math.Max(0.0, cutoff - 1000.0), cutoff, binHz, bins);
        double above = Average(cutoff, Math.Min(nyquist, cutoff + 1000.0), binHz, bins);
        double drop = above <= 0.0 ? 200.0 : 10.0 * Math.Log10(below / above);

        return new BandwidthEstimate(
            drop >= EdgeDropDb ? BandwidthVerdict.BandLimited : BandwidthVerdict.FullBand,
            drop >= EdgeDropDb ? cutoff : nyquist,
            drop,
            Seconds);
    }

    public void Reset()
    {
        Array.Clear(_sum);
        _pendingCount = 0;
        _frames = 0;
        _samples = 0;
    }

    private void Accumulate()
    {
        double energy = 0.0;
        for (int i = 0; i < FftSize; i++)
        {
            energy += _pending[i] * _pending[i];
        }

        if (Math.Sqrt(energy / FftSize) < SilenceFloor)
        {
            return;
        }

        for (int i = 0; i < FftSize; i++)
        {
            _frame[i] = _pending[i] * _window[i];
        }

        _plan.Forward(_frame, _re, _im);
        for (int bin = 0; bin < _sum.Length; bin++)
        {
            _sum[bin] += (_re[bin] * _re[bin]) + (_im[bin] * _im[bin]);
        }

        _frames++;
    }

    private double Average(double lowHz, double highHz, double binHz, int bins)
    {
        int low = Math.Clamp((int)(lowHz / binHz), 0, bins - 1);
        int high = Math.Clamp((int)Math.Ceiling(highHz / binHz), low + 1, bins);

        double total = 0.0;
        for (int bin = low; bin < high; bin++)
        {
            total += _sum[bin] / _frames;
        }

        return total / (high - low);
    }
}
