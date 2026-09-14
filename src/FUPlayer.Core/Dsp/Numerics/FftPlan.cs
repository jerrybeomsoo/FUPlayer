using System.Numerics;

namespace FUPlayer.Core.Dsp.Numerics;

/// <summary>
/// Radix-2 complex FFT with precomputed tables.
/// Forward: X[k] = Σ x[n]·e^(−j2πkn/N). <see cref="Inverse"/> applies the 1/N scaling.
/// </summary>
public sealed class FftPlan
{
    private readonly int[] _reverse;
    private readonly double[] _cos;
    private readonly double[] _sin;

    public FftPlan(int size)
    {
        if (size < 2 || (size & (size - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "FFT size must be a power of two and at least 2.");
        }

        Size = size;
        int bits = BitOperations.Log2((uint)size);
        _reverse = new int[size];
        for (int i = 0; i < size; i++)
        {
            _reverse[i] = (int)(ReverseBits((uint)i) >> (32 - bits));
        }

        int half = size / 2;
        _cos = new double[half];
        _sin = new double[half];
        for (int k = 0; k < half; k++)
        {
            double angle = 2.0 * Math.PI * k / size;
            _cos[k] = Math.Cos(angle);
            _sin[k] = Math.Sin(angle);
        }
    }

    public int Size { get; }

    public void Forward(Span<double> re, Span<double> im) => Run(re, im, inverse: false);

    public void Inverse(Span<double> re, Span<double> im) => Run(re, im, inverse: true);

    public static int NextPowerOfTwo(long value)
    {
        if (value <= 1)
        {
            return 1;
        }

        if (value > (1L << 30))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "FFT size too large.");
        }

        return (int)BitOperations.RoundUpToPowerOf2((ulong)value);
    }

    private unsafe void Run(Span<double> re, Span<double> im, bool inverse)
    {
        int n = Size;
        if (re.Length != n || im.Length != n)
        {
            throw new ArgumentException("Buffer length must equal the FFT size.");
        }

        fixed (double* pr = re, pi = im, pc = _cos, ps = _sin)
        fixed (int* rev = _reverse)
        {
            for (int i = 0; i < n; i++)
            {
                int j = rev[i];
                if (i < j)
                {
                    (pr[i], pr[j]) = (pr[j], pr[i]);
                    (pi[i], pi[j]) = (pi[j], pi[i]);
                }
            }

            double sign = inverse ? 1.0 : -1.0;
            for (int len = 2; len <= n; len <<= 1)
            {
                int half = len >> 1;
                int step = n / len;
                for (int start = 0; start < n; start += len)
                {
                    for (int k = 0; k < half; k++)
                    {
                        double c = pc[k * step];
                        double s = sign * ps[k * step];
                        int a = start + k;
                        int b = a + half;
                        double tr = pr[b] * c - pi[b] * s;
                        double ti = pr[b] * s + pi[b] * c;
                        pr[b] = pr[a] - tr;
                        pi[b] = pi[a] - ti;
                        pr[a] += tr;
                        pi[a] += ti;
                    }
                }
            }

            if (inverse)
            {
                double scale = 1.0 / n;
                for (int i = 0; i < n; i++)
                {
                    pr[i] *= scale;
                    pi[i] *= scale;
                }
            }
        }
    }

    private static uint ReverseBits(uint v)
    {
        v = ((v >> 1) & 0x55555555u) | ((v & 0x55555555u) << 1);
        v = ((v >> 2) & 0x33333333u) | ((v & 0x33333333u) << 2);
        v = ((v >> 4) & 0x0F0F0F0Fu) | ((v & 0x0F0F0F0Fu) << 4);
        v = ((v >> 8) & 0x00FF00FFu) | ((v & 0x00FF00FFu) << 8);
        return (v >> 16) | (v << 16);
    }
}
