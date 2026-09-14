using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using FUPlayer.Core.Dsp.Design;

namespace FUPlayer.Core.Dsp.Modulation;

/// <summary>
/// Per-channel 1-bit delta-sigma modulator in error-feedback form: v = u + (NTF − 1)·e, y = sign(v), e = y − v.
/// The NTF runs as a cascade of monic biquads, so (NTF − 1)·e at time n is simply the sum of the sections'
/// first state variables. Output bytes hold 8 samples each, first sample in the most significant bit.
/// </summary>
/// <remarks>
/// <para>
/// The loop runs at millions of samples per second and its speed is limited by the loop-carried dependency chain
/// rather than by the number of operations. In the textbook cascade update each section's input is the previous
/// section's output, so the error travels through every section in series. With P<sub>k</sub> = s1<sub>0</sub> + … + s1<sub>k</sub>
/// (the previous states) each section's input is e + P<sub>k−1</sub> and its output e + P<sub>k</sub>, which gives the
/// equivalent update
/// </para>
/// <code>
/// s1ₖ' = (b1ₖ − a1ₖ)·e + b1ₖ·Pₖ₋₁ − a1ₖ·Pₖ + s2ₖ
/// s2ₖ' = (b2ₖ − a2ₖ)·e + b2ₖ·Pₖ₋₁ − a2ₖ·Pₖ
/// </code>
/// <para>
/// in which everything except the final multiply-add is known before the quantizer decision. NTFs with 3, 4 or 5
/// sections (orders 5-10) use unrolled kernels of this form that hold each section's (s1, s2) pair in one
/// <see cref="Vector128{T}"/>, so both states update with the same few instructions and stay in registers.
/// Other orders use the plain cascade.
/// </para>
/// <para>
/// The loop also carries the shaped value Σs1 instead of adding the states up again each sample. Because
/// Σs1ₙ₊₁ = e·Σ(b1 − a1) + Σ(b1·Pₖ₋₁ − a1·Pₖ + s2), where only the first term involves the new error, the chain
/// from one quantizer decision to the next is a single multiply-add; the state updates run beside it.
/// </para>
/// </remarks>
public sealed class DeltaSigmaModulator
{
    /// <summary>|v| above this indicates an overloaded loop; states are cleared to recover.</summary>
    private const double InstabilityThreshold = 4.0;

    private readonly double[] _b1;
    private readonly double[] _b2;
    private readonly double[] _a1;
    private readonly double[] _a2;
    private readonly double[] _s1;
    private readonly double[] _s2;
    private readonly double _inputGain;
    private double _peakInput;
    private double _previousInput;

    /// <param name="ntf">Noise transfer function.</param>
    /// <param name="inputGain">Scale applied to the input; 0.5 maps PCM full scale to the customary 50 % modulation depth.</param>
    public DeltaSigmaModulator(NoiseTransferFunction ntf, double inputGain = 0.5)
    {
        Ntf = ntf;
        _inputGain = inputGain;
        int count = ntf.Sections.Length;
        _b1 = new double[count];
        _b2 = new double[count];
        _a1 = new double[count];
        _a2 = new double[count];
        _s1 = new double[count];
        _s2 = new double[count];
        for (int k = 0; k < count; k++)
        {
            _b1[k] = ntf.Sections[k].B1;
            _b2[k] = ntf.Sections[k].B2;
            _a1[k] = ntf.Sections[k].A1;
            _a2[k] = ntf.Sections[k].A2;
        }
    }

    private interface IKernelSize
    {
        static abstract int Sections { get; }
    }

    private interface IRateMode
    {
        /// <summary>Whether each input sample feeds two loop steps (the first at the midpoint).</summary>
        static abstract bool Doubles { get; }
    }

    public NoiseTransferFunction Ntf { get; }

    /// <summary>Times the loop overloaded and was reset.</summary>
    public long InstabilityResets { get; private set; }

    /// <summary>
    /// Returns and clears the largest modulator input (modulation depth, 1.0 = 100 %) since the last call.
    /// The first sample of every output byte is inspected, which is ample for a meter at DSD rates.
    /// </summary>
    public double TakePeakModulation()
    {
        double peak = _peakInput;
        _peakInput = 0.0;
        return peak;
    }

    /// <summary>
    /// Modulates whole bytes of output and returns how many were written: <c>input.Length / 8</c> normally, or
    /// <c>input.Length / 4</c> with <paramref name="doubleRate"/>.
    /// </summary>
    /// <param name="doubleRate">
    /// Run the loop at twice the input rate, taking the midpoint between consecutive input samples for every other
    /// step. This replaces the last ×2 interpolation of the cascade: at these oversampling ratios linear
    /// interpolation leaves the images of audio-band content about 120 dB down, and it saves both the filter and a
    /// buffer at the full DSD rate.
    /// </param>
    public unsafe int Process(ReadOnlySpan<double> input, Span<byte> output, bool doubleRate = false)
    {
        int step = doubleRate ? 4 : 8;
        int byteCount = input.Length / step;
        if (output.Length < byteCount)
        {
            throw new ArgumentException("Output buffer is too small.", nameof(output));
        }

        if (byteCount == 0)
        {
            return 0;
        }

        double peak = 0.0;
        fixed (double* x = input)
        fixed (byte* o = output)
        {
            InstabilityResets += (_b1.Length, doubleRate) switch
            {
                (3, false) => Run<ThreeSections, SingleRate>(x, o, byteCount, ref peak),
                (4, false) => Run<FourSections, SingleRate>(x, o, byteCount, ref peak),
                (5, false) => Run<FiveSections, SingleRate>(x, o, byteCount, ref peak),
                (3, true) => Run<ThreeSections, DoubleRate>(x, o, byteCount, ref peak),
                (4, true) => Run<FourSections, DoubleRate>(x, o, byteCount, ref peak),
                (5, true) => Run<FiveSections, DoubleRate>(x, o, byteCount, ref peak),
                _ => RunGeneric(x, o, byteCount, doubleRate, ref peak),
            };
        }

        _peakInput = Math.Max(_peakInput, peak * _inputGain);
        return byteCount;
    }

    public void Reset()
    {
        Array.Clear(_s1);
        Array.Clear(_s2);
        _peakInput = 0.0;
        _previousInput = 0.0;
    }

    /// <summary>a·b + c in one rounded operation where the hardware offers it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double MultiplyAdd(double a, double b, double c) =>
        Fma.IsSupported || AdvSimd.IsSupported ? Math.FusedMultiplyAdd(a, b, c) : (a * b) + c;

    /// <summary>(0, x₁): the second state of a section, shifted into place for the next update.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<double> High(Vector128<double> section) =>
        Sse2.IsSupported ? Sse2.UnpackHigh(section, Vector128<double>.Zero) : Vector128.Create(section.GetElement(1), 0.0);

    private static Vector128<double> Pair(double[] first, double[] second, int k) =>
        k < first.Length ? Vector128.Create(first[k], second[k]) : Vector128<double>.Zero;

    /// <summary>Prefix-sum kernel; the JIT specialises it per section count and rate mode and removes what is unused.</summary>
    private unsafe long Run<TSize, TRate>(double* x, byte* o, int byteCount, ref double peak)
        where TSize : struct, IKernelSize
        where TRate : struct, IRateMode
    {
        int n = TSize.Sections;
        double gain = _inputGain;
        double largest = peak;
        double previous = _previousInput;

        // Per section k: c = (b1 − a1, b2 − a2), a = (a1, a2), b = (b1, b2), s = (s1, s2).
        Vector128<double> c0 = Difference(0), c1 = Difference(1), c2 = Difference(2), c3 = Difference(3), c4 = Difference(4);
        Vector128<double> a0 = Pair(_a1, _a2, 0), a1 = Pair(_a1, _a2, 1), a2 = Pair(_a1, _a2, 2), a3 = Pair(_a1, _a2, 3), a4 = Pair(_a1, _a2, 4);
        Vector128<double> b1 = Pair(_b1, _b2, 1), b2 = Pair(_b1, _b2, 2), b3 = Pair(_b1, _b2, 3), b4 = Pair(_b1, _b2, 4);
        Vector128<double> s0 = Pair(_s1, _s2, 0), s1 = Pair(_s1, _s2, 1), s2 = Pair(_s1, _s2, 2), s3 = Pair(_s1, _s2, 3), s4 = Pair(_s1, _s2, 4);

        // The error's total feed-forward into the next shaped value, Σ(b1 − a1), and the running Σ s1.
        double errorGain = c0.ToScalar() + c1.ToScalar() + c2.ToScalar()
            + (n > 3 ? c3.ToScalar() : 0.0) + (n > 4 ? c4.ToScalar() : 0.0);
        double shaped = s0.ToScalar() + s1.ToScalar() + s2.ToScalar()
            + (n > 3 ? s3.ToScalar() : 0.0) + (n > 4 ? s4.ToScalar() : 0.0);

        long resets = 0;
        double* sample = x;
        for (int byteIndex = 0; byteIndex < byteCount; byteIndex++)
        {
            double magnitude = Math.Abs(*sample);
            if (magnitude > largest)
            {
                largest = magnitude;
            }

            int bits = 0;
            for (int bit = 0; bit < 8; bit++)
            {
                double u;
                if (TRate.Doubles)
                {
                    if ((bit & 1) == 0)
                    {
                        double next = *sample++;
                        u = 0.5 * (previous + next);
                        previous = next;
                    }
                    else
                    {
                        u = previous;
                    }
                }
                else
                {
                    u = *sample++;
                }

                double v = MultiplyAdd(u, gain, shaped);
                bits = (bits << 1) | (int)(~(ulong)BitConverter.DoubleToInt64Bits(v) >> 63);
                double e = Math.CopySign(1.0, v) - v;

                // Everything below depends only on the states before this step, so it runs while the quantizer
                // decision is still in flight; only the last multiply-add joins e to the next shaped value.
                double p0 = s0.ToScalar();
                double p1 = p0 + s1.ToScalar();
                double p2 = p1 + s2.ToScalar();
                double p3 = 0.0;
                double p4 = 0.0;
                Vector128<double> prefix0 = Vector128.Create(p0);
                Vector128<double> prefix1 = Vector128.Create(p1);
                Vector128<double> prefix2 = Vector128.Create(p2);
                Vector128<double> t0 = High(s0) - a0 * prefix0;
                Vector128<double> t1 = b1 * prefix0 - a1 * prefix1 + High(s1);
                Vector128<double> t2 = b2 * prefix1 - a2 * prefix2 + High(s2);
                Vector128<double> t3 = Vector128<double>.Zero;
                Vector128<double> t4 = Vector128<double>.Zero;
                double carried = t0.ToScalar() + t1.ToScalar() + t2.ToScalar();
                if (n > 3)
                {
                    p3 = p2 + s3.ToScalar();
                    Vector128<double> prefix3 = Vector128.Create(p3);
                    t3 = b3 * prefix2 - a3 * prefix3 + High(s3);
                    carried += t3.ToScalar();
                    if (n > 4)
                    {
                        p4 = p3 + s4.ToScalar();
                        t4 = b4 * prefix3 - a4 * Vector128.Create(p4) + High(s4);
                        carried += t4.ToScalar();
                    }
                }

                Vector128<double> error = Vector128.Create(e);
                s0 = c0 * error + t0;
                s1 = c1 * error + t1;
                s2 = c2 * error + t2;
                if (n > 3)
                {
                    s3 = c3 * error + t3;
                    if (n > 4)
                    {
                        s4 = c4 * error + t4;
                    }
                }

                // Σ s1 of the new states, without adding them up again.
                shaped = MultiplyAdd(errorGain, e, carried);

                if (Math.Abs(v) > InstabilityThreshold)
                {
                    s0 = s1 = s2 = s3 = s4 = Vector128<double>.Zero;
                    shaped = 0.0;
                    resets++;
                }
            }

            o[byteIndex] = (byte)bits;
        }

        Store(0, s0);
        Store(1, s1);
        Store(2, s2);
        if (n > 3)
        {
            Store(3, s3);
        }

        if (n > 4)
        {
            Store(4, s4);
        }

        _previousInput = previous;
        peak = largest;
        return resets;
    }

    private Vector128<double> Difference(int k) =>
        k < _b1.Length ? Vector128.Create(_b1[k] - _a1[k], _b2[k] - _a2[k]) : Vector128<double>.Zero;

    private void Store(int k, Vector128<double> state)
    {
        _s1[k] = state.ToScalar();
        _s2[k] = state.GetElement(1);
    }

    /// <summary>Plain cascade for any number of sections.</summary>
    private unsafe long RunGeneric(double* x, byte* o, int byteCount, bool doubleRate, ref double peak)
    {
        int sections = _b1.Length;
        double gain = _inputGain;
        double largest = peak;
        double previous = _previousInput;
        long resets = 0;
        fixed (double* b1 = _b1, b2 = _b2, a1 = _a1, a2 = _a2, s1 = _s1, s2 = _s2)
        {
            double* sample = x;
            for (int byteIndex = 0; byteIndex < byteCount; byteIndex++)
            {
                double magnitude = Math.Abs(*sample);
                if (magnitude > largest)
                {
                    largest = magnitude;
                }

                int bits = 0;
                for (int bit = 0; bit < 8; bit++)
                {
                    double u;
                    if (doubleRate)
                    {
                        if ((bit & 1) == 0)
                        {
                            double next = *sample++;
                            u = 0.5 * (previous + next);
                            previous = next;
                        }
                        else
                        {
                            u = previous;
                        }
                    }
                    else
                    {
                        u = *sample++;
                    }

                    double shaped = 0.0;
                    for (int k = 0; k < sections; k++)
                    {
                        shaped += s1[k];
                    }

                    double v = u * gain + shaped;
                    bits = (bits << 1) | (int)(~(ulong)BitConverter.DoubleToInt64Bits(v) >> 63);
                    double e = Math.CopySign(1.0, v) - v;

                    for (int k = 0; k < sections; k++)
                    {
                        double y = e + s1[k];
                        s1[k] = b1[k] * e - a1[k] * y + s2[k];
                        s2[k] = b2[k] * e - a2[k] * y;
                        e = y;
                    }

                    if (Math.Abs(v) > InstabilityThreshold)
                    {
                        for (int k = 0; k < sections; k++)
                        {
                            s1[k] = 0.0;
                            s2[k] = 0.0;
                        }

                        resets++;
                    }
                }

                o[byteIndex] = (byte)bits;
            }
        }

        _previousInput = previous;
        peak = largest;
        return resets;
    }

    private struct ThreeSections : IKernelSize
    {
        public static int Sections => 3;
    }

    private struct SingleRate : IRateMode
    {
        public static bool Doubles => false;
    }

    private struct DoubleRate : IRateMode
    {
        public static bool Doubles => true;
    }

    private struct FourSections : IKernelSize
    {
        public static int Sections => 4;
    }

    private struct FiveSections : IKernelSize
    {
        public static int Sections => 5;
    }
}
