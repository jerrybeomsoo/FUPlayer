namespace FUPlayer.Core.Dsp.Numerics;

/// <summary>
/// Transforms between <see cref="Size"/> real samples and the <see cref="Bins"/> spectrum bins that describe
/// them, using a complex transform of half the length.
/// </summary>
/// <remarks>
/// <para>
/// A real signal's spectrum is conjugate-symmetric: the bins above the halfway point repeat the ones below it.
/// Handing such a signal to a complex transform therefore does about twice the arithmetic it needs to, and
/// carries an imaginary part that is zero all the way through. The way round it is old and exact. Read the
/// samples in pairs as one complex sequence of half the length, transform that, and untangle the result into
/// the spectrum the full-length transform would have produced; the inverse runs the same steps backwards.
/// </para>
/// <para>
/// It matters here because a polyphase stage's transforms are most of its cost. Every block it transforms the
/// input once and inverts once per phase, so a conversion at thirty-two phases pays for thirty-three transforms
/// against a handful of partition multiplies, and every one of those transforms is of real data.
/// </para>
/// <para>
/// Instances hold no working state, so one plan can be shared by every thread and every channel, which is what
/// the stages do.
/// </para>
/// </remarks>
public sealed class RealFftPlan
{
    private readonly FftPlan _half;
    private readonly double[] _cos;
    private readonly double[] _sin;

    /// <param name="size">Real samples per transform: a power of two, at least four.</param>
    public RealFftPlan(int size)
    {
        if (size < 4 || (size & (size - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "The length must be a power of two and at least four.");
        }

        Size = size;
        int half = size / 2;
        _half = new FftPlan(half);
        _cos = new double[half + 1];
        _sin = new double[half + 1];
        for (int k = 0; k <= half; k++)
        {
            double angle = Math.PI * k / half;
            _cos[k] = Math.Cos(angle);
            _sin[k] = Math.Sin(angle);
        }
    }

    /// <summary>Real samples per transform.</summary>
    public int Size { get; }

    /// <summary>Bins the spectrum is described by: everything above them is the conjugate of something below.</summary>
    public int Bins => (Size / 2) + 1;

    /// <summary>
    /// Transforms <paramref name="samples"/> into <see cref="Bins"/> bins, which are left in the first
    /// <see cref="Bins"/> entries of <paramref name="re"/> and <paramref name="im"/>.
    /// </summary>
    /// <param name="samples">The <see cref="Size"/> real samples.</param>
    /// <param name="re">Working space and output; at least <see cref="Bins"/> long.</param>
    /// <param name="im">Working space and output; at least <see cref="Bins"/> long.</param>
    public void Forward(ReadOnlySpan<double> samples, Span<double> re, Span<double> im)
    {
        int half = Size / 2;
        if (samples.Length < Size || re.Length <= half || im.Length <= half)
        {
            throw new ArgumentException("The buffers are too short for this transform.", nameof(samples));
        }

        // Consecutive pairs of samples become one complex point, so half a transform covers all of them.
        for (int n = 0; n < half; n++)
        {
            re[n] = samples[2 * n];
            im[n] = samples[(2 * n) + 1];
        }

        _half.Forward(re[..half], im[..half]);

        // Bin 0 and the halfway bin are both real, and both come out of the transform's own first point.
        double zeroRe = re[0];
        double zeroIm = im[0];
        re[0] = zeroRe + zeroIm;
        im[0] = 0.0;
        re[half] = zeroRe - zeroIm;
        im[half] = 0.0;

        // The rest come in pairs, each of which needs the other's value before either is overwritten.
        for (int k = 1; k <= half / 2; k++)
        {
            int j = half - k;
            double kr = re[k];
            double ki = im[k];
            double jr = re[j];
            double ji = im[j];

            Untangle(kr, ki, jr, ji, _cos[k], -_sin[k], out double outKr, out double outKi);
            if (j != k)
            {
                Untangle(jr, ji, kr, ki, _cos[j], -_sin[j], out double outJr, out double outJi);
                re[j] = outJr;
                im[j] = outJi;
            }

            re[k] = outKr;
            im[k] = outKi;
        }
    }

    /// <summary>
    /// Turns <see cref="Bins"/> bins back into <see cref="Size"/> real samples, scaled as
    /// <see cref="FftPlan.Inverse"/> scales its own, so the samples that went in come back out.
    /// </summary>
    /// <param name="re">The bins; destroyed by the call. At least <see cref="Bins"/> long.</param>
    /// <param name="im">The bins; destroyed by the call. At least <see cref="Bins"/> long.</param>
    /// <param name="samples">Where the <see cref="Size"/> real samples go.</param>
    public void Inverse(Span<double> re, Span<double> im, Span<double> samples)
    {
        int half = Size / 2;
        if (samples.Length < Size || re.Length <= half || im.Length <= half)
        {
            throw new ArgumentException("The buffers are too short for this transform.", nameof(samples));
        }

        double zero = re[0];
        double middle = re[half];
        re[0] = (zero + middle) * 0.5;
        im[0] = (zero - middle) * 0.5;

        for (int k = 1; k <= half / 2; k++)
        {
            int j = half - k;
            double kr = re[k];
            double ki = im[k];
            double jr = re[j];
            double ji = im[j];

            Retangle(kr, ki, jr, ji, _cos[k], _sin[k], out double outKr, out double outKi);
            if (j != k)
            {
                Retangle(jr, ji, kr, ki, _cos[j], _sin[j], out double outJr, out double outJi);
                re[j] = outJr;
                im[j] = outJi;
            }

            re[k] = outKr;
            im[k] = outKi;
        }

        _half.Inverse(re[..half], im[..half]);

        for (int n = 0; n < half; n++)
        {
            samples[2 * n] = re[n];
            samples[(2 * n) + 1] = im[n];
        }
    }

    /// <summary>
    /// One bin of the full-length spectrum from the half-length transform: the even and odd samples' own
    /// spectra recovered from the pair, then recombined with the twiddle the full-length transform would use.
    /// </summary>
    /// <param name="ar">The half-length transform at this bin.</param>
    /// <param name="ai">The half-length transform at this bin.</param>
    /// <param name="br">The half-length transform at the mirrored bin, whose conjugate is wanted.</param>
    /// <param name="bi">The half-length transform at the mirrored bin, whose conjugate is wanted.</param>
    private static void Untangle(
        double ar, double ai, double br, double bi, double wr, double wi, out double outRe, out double outIm)
    {
        double sumRe = (ar + br) * 0.5;
        double sumIm = (ai - bi) * 0.5;
        double diffRe = (ar - br) * 0.5;
        double diffIm = (ai + bi) * 0.5;
        outRe = sumRe + (wr * diffIm) + (wi * diffRe);
        outIm = sumIm - (wr * diffRe) + (wi * diffIm);
    }

    /// <summary>The step above run backwards: the half-length transform's bin from two bins of the full one.</summary>
    private static void Retangle(
        double ar, double ai, double br, double bi, double wr, double wi, out double outRe, out double outIm)
    {
        double evenRe = (ar + br) * 0.5;
        double evenIm = (ai - bi) * 0.5;
        double oddRe = (ar - br) * 0.5;
        double oddIm = (ai + bi) * 0.5;

        // Undo the twiddle the forward applied, then put the odd samples back on the imaginary axis.
        double turnedRe = (oddRe * wr) - (oddIm * wi);
        double turnedIm = (oddRe * wi) + (oddIm * wr);
        outRe = evenRe - turnedIm;
        outIm = evenIm + turnedRe;
    }
}
