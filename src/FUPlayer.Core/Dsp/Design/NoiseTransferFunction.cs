using System.Numerics;
using FUPlayer.Core.Dsp.Filters;
using FUPlayer.Core.Dsp.Numerics;

namespace FUPlayer.Core.Dsp.Design;

/// <summary>
/// Lowpass noise transfer function NTF(z) = Π(1 − zᵢ·z⁻¹) / Π(1 − pᵢ·z⁻¹) used by PCM noise shapers
/// and 1-bit delta-sigma modulators.
/// <para>
/// Zeros sit at the roots of the Legendre polynomial scaled to the signal band, which minimises the
/// in-band noise power of an FIR NTF. Poles form a maximally flat set whose radius is tuned so the
/// peak NTF gain equals the requested H∞ (Lee's stability criterion for single-bit loops).
/// </para>
/// </summary>
public sealed class NoiseTransferFunction
{
    private const int PeakSearchPoints = 2048;

    private NoiseTransferFunction(int order, double oversamplingRatio, Complex[] zeros, Complex[] poles)
    {
        Order = order;
        OversamplingRatio = oversamplingRatio;
        Zeros = zeros;
        Poles = poles;
        Sections = BuildSections(zeros, poles);
        MaxGain = PeakGain(Sections);
    }

    public int Order { get; }

    /// <summary>fs / (2·signal bandwidth).</summary>
    public double OversamplingRatio { get; }

    /// <summary>Achieved peak gain (H∞).</summary>
    public double MaxGain { get; }

    public IReadOnlyList<Complex> Zeros { get; }

    public IReadOnlyList<Complex> Poles { get; }

    /// <summary>Sections with B0 = 1 whose cascade equals the NTF.</summary>
    public Biquad[] Sections { get; }

    /// <summary>Synthesises an NTF.</summary>
    /// <param name="order">Loop order.</param>
    /// <param name="oversamplingRatio">fs / (2·bandwidth).</param>
    /// <param name="maxGain">Target H∞ (e.g. 1.5 for a stable 1-bit loop, larger for multi-bit shapers).</param>
    /// <param name="optimizeZeros">Spread zeros across the band (true) or put them all at DC.</param>
    public static NoiseTransferFunction Synthesize(int order, double oversamplingRatio, double maxGain, bool optimizeZeros = true)
    {
        if (order is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(order), "Order must be between 1 and 32.");
        }

        if (oversamplingRatio <= 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(oversamplingRatio), "Oversampling ratio must exceed 1.");
        }

        var zeros = new Complex[order];
        if (optimizeZeros)
        {
            double[] roots = SpecialFunctions.LegendreRoots(order);
            for (int i = 0; i < order; i++)
            {
                zeros[i] = Complex.FromPolarCoordinates(1.0, Math.PI * roots[i] / oversamplingRatio);
            }
        }
        else
        {
            Array.Fill(zeros, Complex.One);
        }

        // |NTF(−1)| grows monotonically from 1 (poles at z = 1) to 2^order (FIR NTF) as the pole set shrinks;
        // for lowpass NTFs it is also the peak gain, so it is the quantity to tune.
        double target = Math.Clamp(maxGain, 1.0001, Math.Pow(2.0, order) * 0.999);
        double lo = Math.Log(1e-14);
        double hi = Math.Log(1e9);
        double Objective(double logX) => NyquistGain(zeros, PolesFor(order, Math.Exp(logX))) - target;

        if (Objective(lo) >= 0)
        {
            hi = lo;
        }
        else if (Objective(hi) <= 0)
        {
            lo = hi;
        }
        else
        {
            for (int iteration = 0; iteration < 120 && hi - lo > 1e-12; iteration++)
            {
                double mid = 0.5 * (lo + hi);
                if (Objective(mid) > 0)
                {
                    hi = mid;
                }
                else
                {
                    lo = mid;
                }
            }
        }

        return new NoiseTransferFunction(order, oversamplingRatio, zeros, PolesFor(order, Math.Exp(0.5 * (lo + hi))));
    }

    /// <summary>NTF value at a normalised frequency (cycles per sample).</summary>
    public Complex Evaluate(double frequency) => Biquad.CascadeResponse(Sections, frequency);

    /// <summary>Mean in-band noise power gain relative to unshaped (white) quantisation noise, in dB.</summary>
    public double InBandNoiseGainDb(double bandEdge)
    {
        const int points = 4096;
        double sum = 0.0;
        for (int i = 0; i < points; i++)
        {
            double f = bandEdge * (i + 0.5) / points;
            double m = Evaluate(f).Magnitude;
            sum += m * m;
        }

        double inBand = sum / points * (2.0 * bandEdge);
        return 10.0 * Math.Log10(Math.Max(inBand, 1e-300));
    }

    private static Complex[] PolesFor(int order, double x)
    {
        var poles = new Complex[order];
        double me2 = -0.5 * Math.Pow(x, 2.0 / order);
        for (int i = 0; i < order; i++)
        {
            double w = (2.0 * (i + 1) - 1.0) * Math.PI / order;
            Complex mb2 = 1.0 + me2 * Complex.FromPolarCoordinates(1.0, w);
            Complex p = mb2 - Complex.Sqrt(mb2 * mb2 - 1.0);
            if (p.Magnitude > 1.0)
            {
                p = 1.0 / p;
            }

            poles[i] = p;
        }

        return poles;
    }

    private static double NyquistGain(Complex[] zeros, Complex[] poles)
    {
        Complex value = Complex.One;
        for (int i = 0; i < zeros.Length; i++)
        {
            value *= (-1.0 - zeros[i]) / (-1.0 - poles[i]);
        }

        return value.Magnitude;
    }

    private static double PeakGain(Biquad[] sections)
    {
        double peak = 0.0;
        for (int i = 0; i <= PeakSearchPoints; i++)
        {
            double f = 0.5 * i / PeakSearchPoints;
            peak = Math.Max(peak, Biquad.CascadeResponse(sections, f).Magnitude);
        }

        return peak;
    }

    private static Biquad[] BuildSections(Complex[] zeros, Complex[] poles)
    {
        List<(double C1, double C2)> numerator = Factor(zeros);
        List<(double C1, double C2)> denominator = Factor(poles);
        var sections = new Biquad[numerator.Count];
        for (int i = 0; i < sections.Length; i++)
        {
            sections[i] = new Biquad(1.0, numerator[i].C1, numerator[i].C2, denominator[i].C1, denominator[i].C2);
        }

        return sections;
    }

    /// <summary>Groups roots into monic quadratic factors (1 + c1·z⁻¹ + c2·z⁻²), linear factor last.</summary>
    private static List<(double C1, double C2)> Factor(Complex[] roots)
    {
        const double tolerance = 1e-12;
        var factors = new List<(double C1, double C2)>();
        var real = new List<double>();
        foreach (Complex r in roots.OrderBy(r => Math.Abs(r.Phase)))
        {
            if (r.Imaginary > tolerance)
            {
                factors.Add((-2.0 * r.Real, r.Real * r.Real + r.Imaginary * r.Imaginary));
            }
            else if (Math.Abs(r.Imaginary) <= tolerance)
            {
                real.Add(r.Real);
            }
        }

        real.Sort();
        int index = 0;
        for (; index + 1 < real.Count; index += 2)
        {
            factors.Add((-(real[index] + real[index + 1]), real[index] * real[index + 1]));
        }

        if (index < real.Count)
        {
            factors.Add((-real[index], 0.0));
        }

        return factors;
    }
}
