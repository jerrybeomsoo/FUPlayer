using System.Collections.Concurrent;
using FUPlayer.Core.Dsp.Numerics;

namespace FUPlayer.Core.Dsp.Design;

/// <summary>Window used to truncate the ideal sinc kernel.</summary>
public enum WindowKind
{
    /// <summary>
    /// Kaiser (Kaiser-Bessel) window, w[n] = I₀(β√(1−t²)) / I₀(β) over t ∈ [−1, 1]: a close and cheap
    /// approximation to the prolate-spheroidal window, and so the shortest filter of any window design for a
    /// given attenuation and transition width. An equiripple design would be shorter still, but is not a window.
    /// </summary>
    Kaiser,

    /// <summary>
    /// Gaussian window, w[n] = exp(−½(n/σ)²), truncated at the ends: no ripple in the transition band, and the
    /// untruncated form is the one function that attains the time-bandwidth lower bound.
    /// </summary>
    Gaussian,
}

/// <summary>Phase character of a filter.</summary>
public enum PhaseResponse
{
    Linear,
    Intermediate,
    Minimum,
}

/// <summary>Lowpass specification. Edges are expressed in cycles per sample of the rate the filter runs at.</summary>
public readonly record struct LowpassSpec(double PassbandEdge, double StopbandEdge, double AttenuationDb, WindowKind Window = WindowKind.Kaiser)
{
    /// <summary>−6 dB point.</summary>
    public double Cutoff => 0.5 * (PassbandEdge + StopbandEdge);

    public double TransitionWidth => StopbandEdge - PassbandEdge;

    public LowpassSpec Scaled(double factor) =>
        this with { PassbandEdge = PassbandEdge * factor, StopbandEdge = StopbandEdge * factor };
}

/// <summary>Windowed-sinc FIR design and phase transformations.</summary>
public static class FirDesign
{
    /// <summary>Largest FFT used by the minimum-phase transformation (bounds transient memory use).</summary>
    public const int MaxPhaseFftSize = 1 << 22;

    public static double KaiserBeta(double attenuationDb)
    {
        if (attenuationDb > 50.0)
        {
            return 0.1102 * (attenuationDb - 8.7);
        }

        if (attenuationDb >= 21.0)
        {
            return 0.5842 * Math.Pow(attenuationDb - 21.0, 0.4) + 0.07886 * (attenuationDb - 21.0);
        }

        return 0.0;
    }

    /// <summary>Estimated (odd) number of taps needed to meet the specification.</summary>
    public static int EstimateLength(in LowpassSpec spec)
    {
        ValidateSpec(spec);
        double attenuation = Math.Max(spec.AttenuationDb, 21.0);
        double n = spec.Window == WindowKind.Kaiser
            ? (attenuation - 7.95) / (14.357 * spec.TransitionWidth) + 1.0
            : 2.0 * GaussianHalfLength(spec) + 1.0;

        if (n > 1 << 28)
        {
            throw new ArgumentOutOfRangeException(nameof(spec), "Filter specification would require an unreasonably long filter.");
        }

        return (int)Math.Ceiling(n) | 1;
    }

    /// <summary>Designs a lowpass with the requested phase character.</summary>
    /// <param name="spec">Specification (edges in cycles per sample).</param>
    /// <param name="dcGain">Required DC gain.</param>
    /// <param name="phase">Phase character.</param>
    /// <param name="intermediateSplit">For intermediate phase: share of the attenuation given to the minimum-phase part.</param>
    /// <param name="length">Explicit length (made odd), or 0 to estimate from the specification.</param>
    public static double[] DesignLowpass(in LowpassSpec spec, double dcGain, PhaseResponse phase, double intermediateSplit = 0.5, int length = 0)
    {
        switch (phase)
        {
            case PhaseResponse.Linear:
                return Lowpass(spec, dcGain, length);

            case PhaseResponse.Minimum:
                return ToMinimumPhase(Lowpass(spec, dcGain, length));

            default:
            {
                // A linear-phase and a minimum-phase lowpass with the same edges, sharing the attenuation budget.
                // Their convolution meets the full specification with shortened, softened pre-ringing.
                const double margin = 6.0;
                double split = Math.Clamp(intermediateSplit, 0.1, 0.9);
                LowpassSpec linearSpec = spec with { AttenuationDb = Math.Max(30.0, spec.AttenuationDb * (1.0 - split) + margin) };
                LowpassSpec minimumSpec = spec with { AttenuationDb = Math.Max(30.0, spec.AttenuationDb * split + margin) };
                int part = length > 0 ? Math.Max(3, length / 2) : 0;
                double[] h = Convolve(Lowpass(linearSpec, 1.0, part), ToMinimumPhase(Lowpass(minimumSpec, 1.0, part)));
                NormalizeDcGain(h, dcGain);
                return h;
            }
        }
    }

    /// <summary>Linear-phase windowed-sinc lowpass with exact coefficient symmetry.</summary>
    /// <param name="spec">Specification.</param>
    /// <param name="dcGain">Required DC gain (use the interpolation factor for polyphase prototypes).</param>
    /// <param name="length">Explicit length (made odd), or 0 to estimate from the specification.</param>
    public static double[] Lowpass(in LowpassSpec spec, double dcGain = 1.0, int length = 0)
    {
        ValidateSpec(spec);
        int n = length > 0 ? length | 1 : EstimateLength(spec);
        double fc = spec.Cutoff;
        double center = (n - 1) / 2.0;
        int halfCount = (n + 1) / 2;
        var h = new double[n];

        if (spec.Window == WindowKind.Kaiser)
        {
            double beta = KaiserBeta(spec.AttenuationDb);
            double norm = 1.0 / SpecialFunctions.BesselI0(beta);
            FillHalf(halfCount, i =>
            {
                double t = center == 0 ? 0.0 : (i - center) / center;
                double w = SpecialFunctions.BesselI0(beta * Math.Sqrt(Math.Max(0.0, 1.0 - t * t))) * norm;
                return 2.0 * fc * SpecialFunctions.Sinc(2.0 * fc * (i - center)) * w;
            }, h);
        }
        else
        {
            // With an explicit length the window is stretched over it, so extra taps buy a narrower transition
            // instead of adding coefficients that are already zero.
            double sigma = length > 0 ? GaussianSigmaForLength(n, spec.AttenuationDb) : GaussianSigma(spec);
            FillHalf(halfCount, i =>
            {
                double d = (i - center) / sigma;
                return 2.0 * fc * SpecialFunctions.Sinc(2.0 * fc * (i - center)) * Math.Exp(-0.5 * d * d);
            }, h);
        }

        for (int i = 0; i < n / 2; i++)
        {
            h[n - 1 - i] = h[i];
        }

        NormalizeDcGain(h, dcGain);
        return h;
    }

    /// <summary>Linear-phase highpass by spectral inversion of a lowpass.</summary>
    public static double[] Highpass(in LowpassSpec complementLowpass, int length = 0)
    {
        double[] h = Lowpass(complementLowpass, 1.0, length);
        for (int i = 0; i < h.Length; i++)
        {
            h[i] = -h[i];
        }

        h[h.Length / 2] += 1.0;
        return h;
    }

    /// <summary>
    /// Homomorphic (cepstral) minimum-phase transformation. Keeps the magnitude response and the length,
    /// moves all zeros inside the unit circle (no pre-ringing).
    /// </summary>
    public static double[] ToMinimumPhase(double[] h)
    {
        int n = h.Length;
        long wanted = Math.Max(4096L, (long)n * 8);
        int size = FftPlan.NextPowerOfTwo(Math.Min(wanted, MaxPhaseFftSize));
        if (size < 2L * n)
        {
            size = FftPlan.NextPowerOfTwo(2L * n);
        }

        var plan = new FftPlan(size);
        var re = new double[size];
        var im = new double[size];
        Array.Copy(h, re, n);
        plan.Forward(re, im);

        var logMagnitude = new double[size];
        double peak = 0.0;
        for (int k = 0; k < size; k++)
        {
            double m = Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
            logMagnitude[k] = m;
            peak = Math.Max(peak, m);
        }

        double floor = Math.Max(peak * 1e-16, double.Epsilon);
        for (int k = 0; k < size; k++)
        {
            logMagnitude[k] = Math.Log(Math.Max(logMagnitude[k], floor));
        }

        // Real cepstrum folded onto positive quefrencies gives the minimum-phase complex cepstrum.
        Array.Copy(logMagnitude, re, size);
        Array.Clear(im);
        plan.Inverse(re, im);
        int half = size / 2;
        for (int k = 1; k < half; k++)
        {
            re[k] *= 2.0;
        }

        Array.Clear(re, half + 1, size - half - 1);
        Array.Clear(im);
        plan.Forward(re, im); // im now holds the minimum-phase response in radians.

        for (int k = 0; k < size; k++)
        {
            double magnitude = Math.Exp(logMagnitude[k]);
            double phase = im[k];
            re[k] = magnitude * Math.Cos(phase);
            im[k] = magnitude * Math.Sin(phase);
        }

        plan.Inverse(re, im);
        var result = new double[n];
        Array.Copy(re, result, n);
        NormalizeDcGain(result, Sum(h));
        return result;
    }

    /// <summary>Full convolution a ∗ b (FFT based for long inputs).</summary>
    public static double[] Convolve(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        int n = a.Length + b.Length - 1;
        var result = new double[n];
        if ((long)a.Length * b.Length <= 20_000_000L)
        {
            for (int i = 0; i < a.Length; i++)
            {
                double ai = a[i];
                if (ai != 0.0)
                {
                    SimdMath.AddScaled(result.AsSpan(i, b.Length), b, ai);
                }
            }

            return result;
        }

        int size = FftPlan.NextPowerOfTwo(n);
        var plan = new FftPlan(size);
        var aRe = new double[size];
        var aIm = new double[size];
        var bRe = new double[size];
        var bIm = new double[size];
        a.CopyTo(aRe);
        b.CopyTo(bRe);
        plan.Forward(aRe, aIm);
        plan.Forward(bRe, bIm);
        for (int k = 0; k < size; k++)
        {
            double re = aRe[k] * bRe[k] - aIm[k] * bIm[k];
            double im = aRe[k] * bIm[k] + aIm[k] * bRe[k];
            aRe[k] = re;
            aIm[k] = im;
        }

        plan.Inverse(aRe, aIm);
        Array.Copy(aRe, result, n);
        return result;
    }

    public static void NormalizeDcGain(Span<double> h, double gain)
    {
        double sum = Sum(h);
        if (Math.Abs(sum) < 1e-300)
        {
            return;
        }

        SimdMath.Scale(h, gain / sum);
    }

    public static double Sum(ReadOnlySpan<double> h)
    {
        // Neumaier summation keeps DC normalisation exact for very long filters.
        double sum = 0.0;
        double compensation = 0.0;
        foreach (double v in h)
        {
            double t = sum + v;
            compensation += Math.Abs(sum) >= Math.Abs(v) ? (sum - t) + v : (v - t) + sum;
            sum = t;
        }

        return sum + compensation;
    }

    /// <summary>Index of the largest-magnitude coefficient (the effective delay of the filter).</summary>
    public static int PeakIndex(ReadOnlySpan<double> h)
    {
        int index = 0;
        double peak = 0.0;
        for (int i = 0; i < h.Length; i++)
        {
            double v = Math.Abs(h[i]);
            if (v > peak)
            {
                peak = v;
                index = i;
            }
        }

        return index;
    }

    private static void ValidateSpec(in LowpassSpec spec)
    {
        if (!(spec.PassbandEdge >= 0.0 && spec.StopbandEdge > spec.PassbandEdge))
        {
            throw new ArgumentException("Lowpass edges must satisfy 0 ≤ passband < stopband.");
        }

        if (spec.PassbandEdge >= 0.5)
        {
            throw new ArgumentException("Passband edge must lie below the Nyquist frequency.");
        }
    }

    /// <summary>Time-domain standard deviation (in samples) of the Gaussian window for the spec.</summary>
    private static double GaussianSigma(in LowpassSpec spec)
    {
        double halfTransition = spec.TransitionWidth / 2.0;
        double level = Math.Pow(10.0, -spec.AttenuationDb / 20.0);
        double k = Math.Sqrt(2.0) * SpecialFunctions.ErfcInv(2.0 * level);
        double sigmaFrequency = halfTransition / k;
        return 1.0 / (2.0 * Math.PI * sigmaFrequency);
    }

    /// <summary>Widest Gaussian window that still decays below the attenuation target within <paramref name="length"/> taps.</summary>
    private static double GaussianSigmaForLength(int length, double attenuationDb)
    {
        double decay = 1.05 * Math.Sqrt(2.0 * (Math.Max(attenuationDb, 21.0) / 20.0) * Math.Log(10.0));
        return (length - 1) / 2.0 / decay;
    }

    private static double GaussianHalfLength(in LowpassSpec spec)
    {
        // Truncate where the window has decayed below the attenuation target (5 % margin).
        double sigma = GaussianSigma(spec);
        return 1.05 * sigma * Math.Sqrt(2.0 * (spec.AttenuationDb / 20.0) * Math.Log(10.0));
    }

    private static void FillHalf(int count, Func<int, double> generator, double[] target)
    {
        if (count < 50_000)
        {
            for (int i = 0; i < count; i++)
            {
                target[i] = generator(i);
            }

            return;
        }

        // Ranges rather than single indices: at sixteen million coefficients the loop machinery around each one
        // costs more than the Bessel function inside it.
        Parallel.ForEach(Partitioner.Create(0, count), range =>
        {
            for (int i = range.Item1; i < range.Item2; i++)
            {
                target[i] = generator(i);
            }
        });
    }
}
