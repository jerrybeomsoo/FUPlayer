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
/// <param name="TransitionHz">
/// Where the fall begins, rather than where it ends: the frequency at which the spectrum has dropped
/// 3 dB from its level well below the edge.
///
/// Not every encoder stops dead. Vorbis rolls off over about a fifth of an octave, and a rebuild that
/// starts at the cutoff leaves that roll-off standing at its coded level while everything above it is
/// lifted to where the music should be, which puts a trough between the two. Measured on a 128 kbit/s
/// Vorbis file: full level to 14.4 kHz, minus 14 dB at 15.4, and the rebuilt band at full level again
/// from 15.8. Filling from here instead closes that.
/// </param>
public readonly record struct BandwidthEstimate(
    BandwidthVerdict Verdict, double CutoffHz, double EdgeDropDb, double Seconds, double TransitionHz = 0.0)
{
    /// <summary>
    /// What was measured, not what it implies.
    ///
    /// "Full band" used to be reported as the whole story, and it reads as "this is lossless", which
    /// is a conclusion the measurement does not support: AAC at 256 kbit/s throws nothing away that
    /// this test can see. Every band of it is within 0.6 dB of the master, top octave included. So the
    /// verdict says where the spectrum runs and that no codec edge was found, and leaves the inference
    /// to whoever is reading it.
    /// </summary>
    public string Describe() => Verdict switch
    {
        BandwidthVerdict.FullBand => $"no codec edge; spectrum runs to {CutoffHz / 1000.0:0.#} kHz",
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

    /// <summary>A codec edge falls at least this far across a kilohertz; a natural roll-off does not.</summary>
    private const double EdgeDropDb = 20.0;

    /// <summary>Width either side of a candidate edge that the drop is measured over.</summary>
    private const double EdgeSpanHz = 1_000.0;

    /// <summary>No point looking for a codec wall below this.</summary>
    private const double LowestCutoffHz = 5_000.0;

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

        // Look for the edge itself rather than for silence above it. A real encoder does not zero the
        // band it discards, it leaves its own quantisation noise there, so a level threshold either
        // misses the wall or has to be set so high that quiet music trips it. The steepest fall over
        // a kilohertz is the wall, wherever the noise under it happens to sit.
        double highest = nyquist * CutoffFraction;
        double best = 0.0;
        double at = 0.0;

        for (double hz = LowestCutoffHz; hz <= highest; hz += binHz)
        {
            double below = Average(hz - EdgeSpanHz, hz, binHz, bins);
            double above = Average(hz, hz + EdgeSpanHz, binHz, bins);
            if (below <= 0.0)
            {
                continue;
            }

            double drop = above <= 0.0 ? 200.0 : 10.0 * Math.Log10(below / above);
            if (drop > best)
            {
                best = drop;
                at = hz;
            }
        }

        if (best < EdgeDropDb)
        {
            return new BandwidthEstimate(BandwidthVerdict.FullBand, nyquist, best, Seconds, nyquist);
        }

        double cutoff = Refine(at, binHz, bins);
        return new BandwidthEstimate(BandwidthVerdict.BandLimited, cutoff, best, Seconds, Transition(at, cutoff, binHz, bins));
    }

    /// <summary>
    /// The steepest point sits a little above the wall, because the window's own skirt carries the
    /// band past where it really ends. The cutoff is defined instead as the frequency where the level
    /// has fallen 6 dB from the plateau just below the edge, which is both closer to the truth and a
    /// definition that means something on a codec whose edge is not vertical.
    /// </summary>
    private double Refine(double edgeHz, double binHz, int bins)
    {
        double plateau = Average(edgeHz - EdgeSpanHz, edgeHz - (EdgeSpanHz / 2.0), binHz, bins);
        if (plateau <= 0.0)
        {
            return edgeHz;
        }

        double target = plateau * Math.Pow(10.0, -6.0 / 10.0);
        double step = binHz * 2.0;

        for (double hz = edgeHz - EdgeSpanHz; hz <= edgeHz + EdgeSpanHz; hz += step)
        {
            if (Average(hz, hz + step, binHz, bins) <= target)
            {
                return hz;
            }
        }

        return edgeHz;
    }

    /// <summary>
    /// Where the roll-off starts, measured from a plateau taken two to three kilohertz below the edge
    /// rather than from the last half kilohertz before it, which is already inside the roll-off and so
    /// reads as a plateau several decibels too low.
    ///
    /// A brick wall answers almost exactly the cutoff, because the level is still full a bin below it.
    /// A gentle roll-off answers lower, which is the point. The result is held within a third of an
    /// octave of the cutoff so that music which is simply quiet at the top cannot drag it down.
    /// </summary>
    private double Transition(double edgeHz, double cutoffHz, double binHz, int bins)
    {
        const double PlateauLowHz = 3_000.0;
        const double PlateauHighHz = 2_000.0;
        const double DropDb = 3.0;

        double plateau = Average(edgeHz - PlateauLowHz, edgeHz - PlateauHighHz, binHz, bins);
        if (plateau <= 0.0)
        {
            return cutoffHz;
        }

        double target = plateau * Math.Pow(10.0, -DropDb / 10.0);
        double step = binHz * 2.0;
        double lowest = cutoffHz / 1.26;

        for (double hz = edgeHz - PlateauHighHz; hz <= cutoffHz; hz += step)
        {
            if (Average(hz, hz + step, binHz, bins) <= target)
            {
                return Math.Clamp(hz, lowest, cutoffHz);
            }
        }

        return cutoffHz;
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
