using System.Numerics;
using FUPlayer.Core.Dsp.Filters;

namespace FUPlayer.Core.Dsp.Design;

/// <summary>
/// Digital elliptic (Cauer) lowpass design: analog prototype built with Landen transformations
/// of the Jacobi elliptic functions, mapped with the pre-warped bilinear transform.
/// The formulation follows S. J. Orfanidis, "Lecture Notes on Elliptic Filter Design".
/// </summary>
public static class EllipticDesign
{
    /// <summary>Designs an elliptic lowpass.</summary>
    /// <param name="passbandEdge">Passband edge in cycles per sample.</param>
    /// <param name="stopbandEdge">Stopband edge in cycles per sample.</param>
    /// <param name="passbandRippleDb">Peak-to-peak passband ripple in dB.</param>
    /// <param name="stopbandAttenuationDb">Minimum stopband attenuation in dB.</param>
    /// <param name="order">Resulting filter order.</param>
    public static Biquad[] Lowpass(double passbandEdge, double stopbandEdge, double passbandRippleDb, double stopbandAttenuationDb, out int order)
    {
        if (!(passbandEdge > 0.0 && stopbandEdge > passbandEdge && stopbandEdge < 0.5))
        {
            throw new ArgumentException("Edges must satisfy 0 < passband < stopband < 0.5.");
        }

        double wp = Math.Tan(Math.PI * passbandEdge);
        double ws = Math.Tan(Math.PI * stopbandEdge);
        double selectivity = wp / ws;
        double ep = Math.Sqrt(Math.Pow(10.0, passbandRippleDb / 10.0) - 1.0);
        double es = Math.Sqrt(Math.Pow(10.0, stopbandAttenuationDb / 10.0) - 1.0);
        double k1 = ep / es;

        (double kSel, double kSelPrime) = CompleteIntegrals(selectivity);
        (double kDis, double kDisPrime) = CompleteIntegrals(k1);
        double exactOrder = kSel * kDisPrime / (kSelPrime * kDis);
        order = Math.Max(1, (int)Math.Ceiling(exactOrder - 1e-9));

        // Solve the degree equation for the integer order: keeps ripple and passband edge, sharpens the stopband.
        double k = DegreeEquation(order, k1);

        int pairs = order / 2;
        bool odd = (order & 1) == 1;
        Complex j = Complex.ImaginaryOne;
        double v0 = (-j * Asne(j / ep, k1) / order).Real;

        var sections = new List<Biquad>(pairs + 1);
        for (int i = 1; i <= pairs; i++)
        {
            double u = (2.0 * i - 1.0) / order;
            double zeta = Cde(u, k).Real;
            double zeroOmega = wp / (k * zeta);
            Complex pole = j * Cde(new Complex(u, -v0), k) * wp;

            double numerator0 = zeroOmega * zeroOmega;
            double denominator1 = -2.0 * pole.Real;
            double denominator0 = pole.Real * pole.Real + pole.Imaginary * pole.Imaginary;
            double unityDc = denominator0 / numerator0;
            sections.Add(BilinearQuadratic(unityDc, 0.0, numerator0 * unityDc, 1.0, denominator1, denominator0));
        }

        if (odd)
        {
            double p0 = (j * Sne(new Complex(0.0, v0), k)).Real * wp;
            sections.Add(BilinearFirstOrder(0.0, -p0, 1.0, -p0));
        }

        if (!odd && sections.Count > 0)
        {
            sections[0] = sections[0].ScaleNumerator(Math.Pow(10.0, -passbandRippleDb / 20.0));
        }

        return sections.ToArray();
    }

    /// <summary>Returns (K(k), K′(k)) using the arithmetic-geometric mean (accurate for tiny k).</summary>
    internal static (double K, double KPrime) CompleteIntegrals(double k)
    {
        double kp = Math.Sqrt((1.0 - k) * (1.0 + k));
        return (Math.PI / (2.0 * Agm(1.0, kp)), Math.PI / (2.0 * Agm(1.0, k)));
    }

    private static double Agm(double a, double b)
    {
        if (b <= 0.0)
        {
            return 0.0;
        }

        for (int i = 0; i < 100 && Math.Abs(a - b) > 1e-16 * a; i++)
        {
            double an = 0.5 * (a + b);
            b = Math.Sqrt(a * b);
            a = an;
        }

        return a;
    }

    /// <summary>Solves N·K′(k)/K(k) = K′(k1)/K(k1) for k via the elliptic nome.</summary>
    private static double DegreeEquation(int order, double k1)
    {
        (double kk1, double kk1Prime) = CompleteIntegrals(k1);
        double q1 = Math.Exp(-Math.PI * kk1Prime / kk1);
        double q = Math.Pow(q1, 1.0 / order);

        double numerator = 0.0;
        for (int m = 0; m < 64; m++)
        {
            double term = Math.Pow(q, m * (m + 1.0));
            numerator += term;
            if (term < 1e-18)
            {
                break;
            }
        }

        double denominator = 0.0;
        for (int m = 1; m < 64; m++)
        {
            double term = Math.Pow(q, (double)m * m);
            denominator += term;
            if (term < 1e-18)
            {
                break;
            }
        }

        double ratio = numerator / (1.0 + 2.0 * denominator);
        return Math.Min(1.0 - 1e-15, 4.0 * Math.Sqrt(q) * ratio * ratio);
    }

    private static double[] LandenSequence(double k)
    {
        var v = new List<double>(12);
        double kp = Math.Sqrt((1.0 - k) * (1.0 + k));
        while (k > 1e-16 && v.Count < 64)
        {
            double r = k / (1.0 + kp);
            k = r * r;
            kp = Math.Sqrt((1.0 - k) * (1.0 + k));
            v.Add(k);
        }

        return v.ToArray();
    }

    private static Complex Cde(Complex u, double k)
    {
        double[] v = LandenSequence(k);
        Complex w = Complex.Cos(u * Math.PI / 2.0);
        for (int i = v.Length - 1; i >= 0; i--)
        {
            w = (1.0 + v[i]) * w / (1.0 + v[i] * w * w);
        }

        return w;
    }

    private static Complex Sne(Complex u, double k)
    {
        double[] v = LandenSequence(k);
        Complex w = Complex.Sin(u * Math.PI / 2.0);
        for (int i = v.Length - 1; i >= 0; i--)
        {
            w = (1.0 + v[i]) * w / (1.0 + v[i] * w * w);
        }

        return w;
    }

    private static Complex Acde(Complex w, double k)
    {
        double[] v = LandenSequence(k);
        for (int i = 0; i < v.Length; i++)
        {
            double previous = i == 0 ? k : v[i - 1];
            w = w / (1.0 + Complex.Sqrt(1.0 - w * w * previous * previous)) * 2.0 / (1.0 + v[i]);
        }

        Complex u = 2.0 / Math.PI * Complex.Acos(w);
        (double kk, double kkPrime) = CompleteIntegrals(k);
        double r = kkPrime / kk;
        return new Complex(SymmetricRemainder(u.Real, 4.0), SymmetricRemainder(u.Imaginary, 2.0 * r));
    }

    private static Complex Asne(Complex w, double k) => 1.0 - Acde(w, k);

    private static double SymmetricRemainder(double x, double y) => x - y * Math.Round(x / y);

    /// <summary>Bilinear transform s = (1 − z⁻¹)/(1 + z⁻¹) of (a2·s² + a1·s + a0)/(c2·s² + c1·s + c0).</summary>
    private static Biquad BilinearQuadratic(double a2, double a1, double a0, double c2, double c1, double c0)
    {
        double n0 = a2 + a1 + a0;
        double n1 = 2.0 * (a0 - a2);
        double n2 = a2 - a1 + a0;
        double d0 = c2 + c1 + c0;
        double d1 = 2.0 * (c0 - c2);
        double d2 = c2 - c1 + c0;
        return new Biquad(n0 / d0, n1 / d0, n2 / d0, d1 / d0, d2 / d0);
    }

    /// <summary>Bilinear transform of (a1·s + a0)/(c1·s + c0).</summary>
    private static Biquad BilinearFirstOrder(double a1, double a0, double c1, double c0)
    {
        double n0 = a1 + a0;
        double n1 = a0 - a1;
        double d0 = c1 + c0;
        double d1 = c0 - c1;
        return new Biquad(n0 / d0, n1 / d0, 0.0, d1 / d0, 0.0);
    }
}
