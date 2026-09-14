namespace FUPlayer.Core.Dsp.Numerics;

/// <summary>Special functions needed by the filter designers.</summary>
public static class SpecialFunctions
{
    /// <summary>Normalised sinc: sin(πx)/(πx).</summary>
    public static double Sinc(double x)
    {
        if (Math.Abs(x) < 1e-12)
        {
            return 1.0;
        }

        double px = Math.PI * x;
        return Math.Sin(px) / px;
    }

    /// <summary>Modified Bessel function of the first kind, order zero.</summary>
    public static double BesselI0(double x)
    {
        double sum = 1.0;
        double term = 1.0;
        double halfX = 0.5 * x;
        for (int k = 1; k < 1000; k++)
        {
            term *= halfX / k;
            double t2 = term * term;
            sum += t2;
            if (t2 < sum * 1e-17)
            {
                break;
            }
        }

        return sum;
    }

    public static double Erf(double x) => x < 0 ? -(1.0 - Erfc(-x)) : 1.0 - Erfc(x);

    /// <summary>Complementary error function with good relative accuracy in the tail.</summary>
    public static double Erfc(double x)
    {
        if (x < 0)
        {
            return 2.0 - Erfc(-x);
        }

        return x < 2.5 ? 1.0 - ErfSeries(x) : ErfcContinuedFraction(x);
    }

    /// <summary>Inverse of <see cref="Erfc"/> for 0 &lt; y &lt; 2.</summary>
    public static double ErfcInv(double y)
    {
        if (y <= 0)
        {
            return double.PositiveInfinity;
        }

        if (y >= 2)
        {
            return double.NegativeInfinity;
        }

        if (y > 1)
        {
            return -ErfcInv(2.0 - y);
        }

        double lo = 0.0;
        double hi = 26.0;
        double x = Math.Clamp(Math.Sqrt(-Math.Log(Math.Max(y, 1e-300))), lo, hi);
        for (int i = 0; i < 200; i++)
        {
            double f = Erfc(x) - y;
            if (f > 0)
            {
                lo = x;
            }
            else
            {
                hi = x;
            }

            double df = -2.0 / Math.Sqrt(Math.PI) * Math.Exp(-x * x);
            double next = x - f / df;
            if (double.IsNaN(next) || next <= lo || next >= hi)
            {
                next = 0.5 * (lo + hi);
            }

            if (Math.Abs(next - x) <= 1e-15 * Math.Max(1.0, x))
            {
                return next;
            }

            x = next;
        }

        return x;
    }

    /// <summary>Roots of the Legendre polynomial Pₙ in ascending order.</summary>
    public static double[] LegendreRoots(int n)
    {
        if (n < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(n));
        }

        var roots = new double[n];
        for (int i = 0; i < n; i++)
        {
            double x = Math.Cos(Math.PI * (i + 0.75) / (n + 0.5));
            for (int iteration = 0; iteration < 100; iteration++)
            {
                double p0 = 1.0;
                double p1 = x;
                for (int k = 2; k <= n; k++)
                {
                    double p2 = ((2.0 * k - 1.0) * x * p1 - (k - 1.0) * p0) / k;
                    p0 = p1;
                    p1 = p2;
                }

                double derivative = n * (x * p1 - p0) / (x * x - 1.0);
                double dx = p1 / derivative;
                x -= dx;
                if (Math.Abs(dx) < 1e-16)
                {
                    break;
                }
            }

            roots[i] = x;
        }

        Array.Sort(roots);
        return roots;
    }

    private static double ErfSeries(double x)
    {
        // erf(x) = 2/√π · e^(−x²) · Σ 2ⁿ x^(2n+1) / (1·3·…·(2n+1)); all terms positive.
        double x2 = x * x;
        double term = x;
        double sum = x;
        for (int n = 1; n < 1000; n++)
        {
            term *= 2.0 * x2 / (2.0 * n + 1.0);
            sum += term;
            if (term < sum * 1e-17)
            {
                break;
            }
        }

        return 2.0 / Math.Sqrt(Math.PI) * Math.Exp(-x2) * sum;
    }

    private static double ErfcContinuedFraction(double x)
    {
        // erfc(x) = e^(−x²)/√π · 1/(x + (1/2)/(x + 1/(x + (3/2)/(x + …)))) evaluated with modified Lentz.
        const double tiny = 1e-300;
        double f = x;
        double c = f;
        double d = 0.0;
        for (int n = 1; n < 5000; n++)
        {
            double an = 0.5 * n;
            d = x + an * d;
            if (Math.Abs(d) < tiny)
            {
                d = tiny;
            }

            d = 1.0 / d;
            c = x + an / c;
            if (Math.Abs(c) < tiny)
            {
                c = tiny;
            }

            double delta = c * d;
            f *= delta;
            if (Math.Abs(delta - 1.0) < 1e-16)
            {
                break;
            }
        }

        return Math.Exp(-x * x) / (Math.Sqrt(Math.PI) * f);
    }
}
