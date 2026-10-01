using System.Collections.Concurrent;
using FUPlayer.Core.Dsp.Numerics;

namespace FUPlayer.Core.Dsp.Design;

/// <summary>What a transient-aligned design came out as.</summary>
/// <param name="Length">Taps.</param>
/// <param name="Taper">Taps at each end that taper; the rest are the sinc's own coefficients.</param>
/// <param name="HalfWidth">How far either side of the cut-off the response takes to go from flat to fully rejected, in cycles per sample.</param>
public readonly record struct TransientAlignedShape(int Length, int Taper, double HalfWidth)
{
    /// <summary>The share of the coefficients that are exactly the sinc's.</summary>
    public double ExactShare => (Length - (2.0 * Taper)) / Length;
}

/// <summary>
/// The transient-aligned lowpass: the ideal reconstruction filter, a sinc cut off at a Nyquist frequency, kept as it is
/// for as much of its length as the length allows, and tapered to zero only at its two ends.
///
/// Sampling theory reconstructs a band-limited signal exactly with an infinitely long sinc. A finite filter has to end,
/// and how it ends is the design. A conventional window shapes the whole length: every coefficient but the centre one
/// is made smaller than the sinc's, so the reconstruction differs from the ideal one right next to every transient. Here
/// the middle of the filter is the sinc itself, coefficient for coefficient, and only the ends are shaped, as smoothly as
/// is needed for images to be rejected by <see cref="AttenuationDb"/> from just past the cut-off: the taper is the running
/// sum of a Kaiser kernel, so the window is a rectangle convolved with that kernel and its spectrum is the rectangle's
/// times the kernel's. The kernel's main lobe sets where the response closes; the rectangle's own sidelobes are
/// multiplied down by the kernel's beyond it.
///
/// Cut off at 1/(2·<c>period</c>) cycles per sample with an integer period, every coefficient a whole period from the
/// centre is exactly zero and the centre one is exactly the gain divided by the period. As an interpolator by that period
/// it is then a Nyquist filter: the original samples come through unchanged, to the last bit, and only the samples
/// between them are computed. Nothing is normalised afterwards, which would undo that; the DC gain is the sinc's, which
/// a long filter has to within 10⁻⁸.
///
/// The length decides most of how far the result is from the ideal sinc: measured over a 16× interpolator of 1,015,809
/// taps, the squared error of the coefficients against the infinite sinc changes by less than a factor of two from a
/// window that tapers the whole length to one that keeps 95 % of it exact, and the coefficients beyond the end, which no
/// window has, are the larger part of it. What keeping the middle exact buys is where the error is: none of it lies
/// near the transient; all of it lies in the last stretch of the filter at either end, a third of a second and more
/// away at that length, at coefficients 95 dB and more under the peak.
/// </summary>
public static class TransientAlignedDesign
{
    /// <summary>The image rejection the tapers are made for, below the noise of 24-bit audio.</summary>
    public const double AttenuationDb = 150.0;

    /// <summary>
    /// How close to the cut-off the response is fully controlled, as a fraction of the cut-off frequency either side: within
    /// a thousandth of the Nyquist frequency, 22 Hz either side of 22.05 kHz for a 44.1 kHz source. Outside it the passband
    /// is flat to within 10⁻⁷ dB and images are at least <see cref="AttenuationDb"/> down.
    /// </summary>
    public const double TransitionFraction = 0.001;

    /// <summary>The share of the coefficients that stays the sinc's own however short the filter: the middle half.</summary>
    public const double MinimumExactShare = 0.5;

    /// <summary>The Kaiser parameter of the taper's kernel.</summary>
    public static double Beta { get; } = FirDesign.KaiserBeta(AttenuationDb);

    /// <summary>
    /// The kernel's main lobe, half its width times its length: a kernel of <c>L</c> + 1 points closes within
    /// <c>√((β/π)² + 1) / L</c> cycles per sample of its centre, 5.06 / L for 150 dB.
    /// </summary>
    public static double LobeTimesLength { get; } = Math.Sqrt(Math.Pow(Beta / Math.PI, 2) + 1.0);

    /// <summary>The taper that closes the response within <paramref name="halfWidth"/> cycles per sample either side of the cut-off.</summary>
    public static int TaperFor(double halfWidth)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(halfWidth);
        return (int)Math.Min(int.MaxValue / 4, Math.Ceiling(LobeTimesLength / halfWidth));
    }

    /// <summary>
    /// The length a filter of this period takes when none is asked for: the shortest that closes within
    /// <see cref="TransitionFraction"/> of the cut-off and still keeps <see cref="MinimumExactShare"/> of its coefficients
    /// the sinc's. That is 40,480 periods, whatever the period: 0.92 s, 647,681 taps at 16× a 44.1 kHz source.
    /// </summary>
    public static int DefaultLength(int period)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(period);
        double halfWidth = TransitionFraction / (2.0 * period);
        long taper = TaperFor(halfWidth);
        long length = (long)Math.Ceiling(taper / ((1.0 - MinimumExactShare) / 2.0));
        return (int)Math.Min(int.MaxValue - 1, length) | 1;
    }

    /// <summary>
    /// Designs the filter: <paramref name="length"/> taps (made odd), cut off at 1/(2·<paramref name="period"/>) cycles per
    /// sample, DC gain <paramref name="gain"/>, its tapers as short as closing within <paramref name="halfWidth"/> of the
    /// cut-off allows and never longer than a quarter of the length each.
    /// </summary>
    public static double[] Lowpass(int length, int period, double gain, double halfWidth, out TransientAlignedShape shape)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 3);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(period);
        int n = length | 1;
        int center = n / 2;
        int taper = Math.Clamp(TaperFor(halfWidth), 1, Math.Max(1, (n - 1) / 4));
        double[] edge = Edge(taper);
        double scale = gain / period;
        var h = new double[n];

        // The half up to the centre, mirrored: distance d from the centre runs from center down to 0.
        int flatFrom = center - taper;
        Fill(center + 1, i =>
        {
            long d = center - i;
            double w = d <= flatFrom ? 1.0 : edge[d - flatFrom - 1];
            return scale * Sinc(d, period) * w;
        }, h);

        for (int i = 0; i < center; i++)
        {
            h[n - 1 - i] = h[i];
        }

        shape = new TransientAlignedShape(n, taper, LobeTimesLength / taper);
        return h;
    }

    /// <summary>
    /// The falling edge, nearest the flat part first: entry j is the share of a Kaiser kernel of <paramref name="taper"/> + 1
    /// points that lies beyond its j-th point, so the edge starts a hair under one and ends a hair over zero, one sample
    /// before the coefficients would reach zero. Summed from the far end, where the values are smallest, so they keep
    /// their precision down there.
    /// </summary>
    private static double[] Edge(int taper)
    {
        var kernel = new double[taper + 1];
        double norm = 1.0 / SpecialFunctions.BesselI0(Beta);
        for (int i = 0; i <= taper; i++)
        {
            double t = (2.0 * i / taper) - 1.0;
            kernel[i] = SpecialFunctions.BesselI0(Beta * Math.Sqrt(Math.Max(0.0, 1.0 - (t * t)))) * norm;
        }

        // Suffix sums, Neumaier-compensated: tail[j] = kernel[j] + … + kernel[taper].
        var tail = new double[taper + 2];
        double sum = 0.0;
        double compensation = 0.0;
        for (int i = taper; i >= 0; i--)
        {
            double v = kernel[i];
            double t = sum + v;
            compensation += Math.Abs(sum) >= Math.Abs(v) ? (sum - t) + v : (v - t) + sum;
            sum = t;
            tail[i] = sum + compensation;
        }

        double total = tail[0];
        var edge = new double[taper];
        for (int j = 1; j <= taper; j++)
        {
            edge[j - 1] = tail[j] / total;
        }

        return edge;
    }

    /// <summary>
    /// sin(π·m/p)/(π·m/p) with the argument reduced exactly: m = q·p + r in integers, so that sin(π(q + r/p)) is
    /// (−1)^q·sin(πr/p), and that sine taken by <see cref="double.SinPi"/>, which reduces r/p itself rather than rounding
    /// π·r/p first. Taking the sine of π·m/p directly loses the argument's low bits a million periods out, where the
    /// coefficients are a few millionths and those bits are what they are made of. Whole periods are exactly zero.
    /// </summary>
    internal static double Sinc(long m, int p)
    {
        if (m == 0)
        {
            return 1.0;
        }

        long q = Math.DivRem(m, p, out long r);
        if (r == 0)
        {
            return 0.0;
        }

        double s = double.SinPi((double)r / p);
        if ((q & 1) != 0)
        {
            s = -s;
        }

        return s / (Math.PI * m / p);
    }

    private static void Fill(int count, Func<int, double> generator, double[] target)
    {
        if (count < 50_000)
        {
            for (int i = 0; i < count; i++)
            {
                target[i] = generator(i);
            }

            return;
        }

        Parallel.ForEach(Partitioner.Create(0, count), range =>
        {
            for (int i = range.Item1; i < range.Item2; i++)
            {
                target[i] = generator(i);
            }
        });
    }
}
