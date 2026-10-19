using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace FUPlayer.Core.Dsp.Numerics;

/// <summary>
/// The inner loop of a long FIR: one run of coefficients against a series of evenly spaced windows of the signal.
/// </summary>
/// <remarks>
/// <para>
/// Taken one output sample at a time, a filter of tens of thousands of taps reads its entire coefficient run for
/// every sample it produces. Past a few thousand taps that run no longer fits in the cache nearest the core, and
/// the loop spends its time waiting for memory instead of multiplying, which is how a processor can sit at a
/// fraction of its capacity while the DSP load reads several hundred per cent. Computing a handful of output
/// samples in the same sweep reads the coefficients once for all of them and turns the traffic back into
/// arithmetic; measured on the filters this player offers, it is worth three to four times the throughput.
/// </para>
/// <para>
/// On a processor with fused multiply-add, each tap is one instruction instead of a multiply and an add, and the
/// coefficients are taken <see cref="Tile"/> at a time across every output sample of the call, so the run being
/// worked on stays in the nearest cache. Measured on an i7-7820HQ at 32,768 taps, one thread went from 8.7 to 14.3
/// thousand million multiply-adds a second and all eight from 35 to 51. A fused multiply-add rounds once where the
/// pair rounds twice; the two answers differ by a few parts in 10<sup>15</sup> of the result.
/// </para>
/// <para>
/// Every output sample keeps its own accumulator and sums its taps in the same order no matter what is computed
/// beside it. A sample therefore does not depend on how many of its neighbours were swept with it, on where a
/// block boundary happened to fall, on the tiling, or on how the work was divided between threads.
/// </para>
/// </remarks>
public static class FirKernel
{
    /// <summary>Output samples computed in one sweep of the coefficients.</summary>
    private const int Width = 8;

    /// <summary>Coefficients swept across all the output samples before moving on: 8 KB, a quarter of a first-level cache.</summary>
    private const int Tile = 1024;

    [ThreadStatic]
    private static Vector256<double>[]? t_accumulators;

    /// <summary>
    /// Computes <c>count</c> output samples, the <c>j</c>th being Σ<sub>k</sub> coefficients[k]·signal[j·signalStride + k].
    /// </summary>
    /// <param name="coefficients">Filter run of <paramref name="taps"/> coefficients.</param>
    /// <param name="taps">Length of the coefficient run; must be at least one.</param>
    /// <param name="signal">First sample of the first window; must hold <c>(count − 1)·signalStride + taps</c> samples.</param>
    /// <param name="signalStride">Samples between the start of one window and the next.</param>
    /// <param name="results">Where the first output sample goes.</param>
    /// <param name="resultStride">Elements between one output sample and the next.</param>
    /// <param name="count">Output samples to compute.</param>
    public static void Convolve(
        ref double coefficients,
        nuint taps,
        ref double signal,
        nuint signalStride,
        ref double results,
        nuint resultStride,
        int count)
    {
        if (Fma.IsSupported && taps >= 4)
        {
            ConvolveFused(ref coefficients, taps, ref signal, signalStride, ref results, resultStride, count);
            return;
        }

        int j = 0;
        for (; j + Width <= count; j += Width)
        {
            Sweep(ref coefficients, taps, ref Unsafe.Add(ref signal, (nuint)j * signalStride), signalStride,
                ref Unsafe.Add(ref results, (nuint)j * resultStride), resultStride);
        }

        if (j < count && count >= Width)
        {
            // The last few share a sweep with the samples before them rather than being computed one by one.
            // Recomputing at most seven output samples costs less than sweeping the coefficients again for each.
            int start = count - Width;
            Span<double> tail = stackalloc double[Width];
            Sweep(ref coefficients, taps, ref Unsafe.Add(ref signal, (nuint)start * signalStride), signalStride,
                ref MemoryMarshal.GetReference(tail), 1);
            for (; j < count; j++)
            {
                Unsafe.Add(ref results, (nuint)j * resultStride) = tail[j - start];
            }

            return;
        }

        for (; j < count; j++)
        {
            Unsafe.Add(ref results, (nuint)j * resultStride) =
                Single(ref coefficients, taps, ref Unsafe.Add(ref signal, (nuint)j * signalStride));
        }
    }

    /// <summary>
    /// The fused path: accumulators held per output sample across the tiles. Each lane of an output's accumulator
    /// sums the taps congruent to it modulo four in increasing order, which the tiling does not change, so the last
    /// group can overlap the one before it and a single output computed alone comes out the same.
    /// </summary>
    private static void ConvolveFused(
        ref double c, nuint taps, ref double x, nuint signalStride, ref double results, nuint resultStride, int count)
    {
        if (count <= 0)
        {
            return;
        }

        nuint body = taps & ~(nuint)3;
        if (count < Width)
        {
            for (int j = 0; j < count; j++)
            {
                ref double window = ref Unsafe.Add(ref x, (nuint)j * signalStride);
                Vector256<double> acc = Vector256<double>.Zero;
                for (nuint i = 0; i < body; i += 4)
                {
                    acc = Fma.MultiplyAdd(Vector256.LoadUnsafe(ref c, i), Vector256.LoadUnsafe(ref window, i), acc);
                }

                Unsafe.Add(ref results, (nuint)j * resultStride) = Finish(acc, ref c, taps, ref window, body);
            }

            return;
        }

        int groups = (count + Width - 1) / Width;
        int needed = groups * Width;
        Vector256<double>[] accumulators = t_accumulators is { } held && held.Length >= needed
            ? held
            : t_accumulators = new Vector256<double>[Math.Max(needed, 512)];
        Array.Clear(accumulators, 0, needed);

        for (nuint t0 = 0; t0 < body; t0 += Tile)
        {
            nuint n = Math.Min(Tile, body - t0);
            for (int g = 0; g < groups; g++)
            {
                int j = g == groups - 1 ? count - Width : g * Width;
                SweepFused(
                    ref Unsafe.Add(ref c, t0), n, ref Unsafe.Add(ref x, ((nuint)j * signalStride) + t0), signalStride,
                    ref accumulators[g * Width]);
            }
        }

        for (int g = 0; g < groups; g++)
        {
            int j = g == groups - 1 ? count - Width : g * Width;
            for (int w = 0; w < Width; w++)
            {
                Unsafe.Add(ref results, (nuint)(j + w) * resultStride) = Finish(
                    accumulators[(g * Width) + w], ref c, taps, ref Unsafe.Add(ref x, (nuint)(j + w) * signalStride), body);
            }
        }
    }

    /// <summary>Adds <paramref name="n"/> taps, a multiple of four, to the accumulators of eight consecutive output samples.</summary>
    private static void SweepFused(ref double c, nuint n, ref double x, nuint signalStride, ref Vector256<double> acc)
    {
        ref double x0 = ref x;
        ref double x1 = ref Unsafe.Add(ref x, signalStride);
        ref double x2 = ref Unsafe.Add(ref x, 2 * signalStride);
        ref double x3 = ref Unsafe.Add(ref x, 3 * signalStride);
        ref double x4 = ref Unsafe.Add(ref x, 4 * signalStride);
        ref double x5 = ref Unsafe.Add(ref x, 5 * signalStride);
        ref double x6 = ref Unsafe.Add(ref x, 6 * signalStride);
        ref double x7 = ref Unsafe.Add(ref x, 7 * signalStride);

        Vector256<double> a0 = acc;
        Vector256<double> a1 = Unsafe.Add(ref acc, 1);
        Vector256<double> a2 = Unsafe.Add(ref acc, 2);
        Vector256<double> a3 = Unsafe.Add(ref acc, 3);
        Vector256<double> a4 = Unsafe.Add(ref acc, 4);
        Vector256<double> a5 = Unsafe.Add(ref acc, 5);
        Vector256<double> a6 = Unsafe.Add(ref acc, 6);
        Vector256<double> a7 = Unsafe.Add(ref acc, 7);
        for (nuint i = 0; i < n; i += 4)
        {
            Vector256<double> cv = Vector256.LoadUnsafe(ref c, i);
            a0 = Fma.MultiplyAdd(cv, Vector256.LoadUnsafe(ref x0, i), a0);
            a1 = Fma.MultiplyAdd(cv, Vector256.LoadUnsafe(ref x1, i), a1);
            a2 = Fma.MultiplyAdd(cv, Vector256.LoadUnsafe(ref x2, i), a2);
            a3 = Fma.MultiplyAdd(cv, Vector256.LoadUnsafe(ref x3, i), a3);
            a4 = Fma.MultiplyAdd(cv, Vector256.LoadUnsafe(ref x4, i), a4);
            a5 = Fma.MultiplyAdd(cv, Vector256.LoadUnsafe(ref x5, i), a5);
            a6 = Fma.MultiplyAdd(cv, Vector256.LoadUnsafe(ref x6, i), a6);
            a7 = Fma.MultiplyAdd(cv, Vector256.LoadUnsafe(ref x7, i), a7);
        }

        acc = a0;
        Unsafe.Add(ref acc, 1) = a1;
        Unsafe.Add(ref acc, 2) = a2;
        Unsafe.Add(ref acc, 3) = a3;
        Unsafe.Add(ref acc, 4) = a4;
        Unsafe.Add(ref acc, 5) = a5;
        Unsafe.Add(ref acc, 6) = a6;
        Unsafe.Add(ref acc, 7) = a7;
    }

    /// <summary>One pass over the coefficients producing <see cref="Width"/> consecutive output samples.</summary>
    private static void Sweep(
        ref double c, nuint taps, ref double x, nuint signalStride, ref double results, nuint resultStride)
    {
        if (!Vector256.IsHardwareAccelerated || taps < 4)
        {
            for (nuint j = 0; j < Width; j++)
            {
                Unsafe.Add(ref results, j * resultStride) = Single(ref c, taps, ref Unsafe.Add(ref x, j * signalStride));
            }

            return;
        }

        // Eight window origins held apart, so the coefficient vector loaded each turn is used eight times before
        // it is dropped, and the eight accumulator chains hide one another's latency.
        ref double x0 = ref x;
        ref double x1 = ref Unsafe.Add(ref x, signalStride);
        ref double x2 = ref Unsafe.Add(ref x, 2 * signalStride);
        ref double x3 = ref Unsafe.Add(ref x, 3 * signalStride);
        ref double x4 = ref Unsafe.Add(ref x, 4 * signalStride);
        ref double x5 = ref Unsafe.Add(ref x, 5 * signalStride);
        ref double x6 = ref Unsafe.Add(ref x, 6 * signalStride);
        ref double x7 = ref Unsafe.Add(ref x, 7 * signalStride);

        Vector256<double> a0 = Vector256<double>.Zero;
        Vector256<double> a1 = Vector256<double>.Zero;
        Vector256<double> a2 = Vector256<double>.Zero;
        Vector256<double> a3 = Vector256<double>.Zero;
        Vector256<double> a4 = Vector256<double>.Zero;
        Vector256<double> a5 = Vector256<double>.Zero;
        Vector256<double> a6 = Vector256<double>.Zero;
        Vector256<double> a7 = Vector256<double>.Zero;

        nuint i = 0;
        nuint last = taps - 4;
        for (; i <= last; i += 4)
        {
            Vector256<double> cv = Vector256.LoadUnsafe(ref c, i);
            a0 += cv * Vector256.LoadUnsafe(ref x0, i);
            a1 += cv * Vector256.LoadUnsafe(ref x1, i);
            a2 += cv * Vector256.LoadUnsafe(ref x2, i);
            a3 += cv * Vector256.LoadUnsafe(ref x3, i);
            a4 += cv * Vector256.LoadUnsafe(ref x4, i);
            a5 += cv * Vector256.LoadUnsafe(ref x5, i);
            a6 += cv * Vector256.LoadUnsafe(ref x6, i);
            a7 += cv * Vector256.LoadUnsafe(ref x7, i);
        }

        Unsafe.Add(ref results, 0) = Finish(a0, ref c, taps, ref x0, i);
        Unsafe.Add(ref results, resultStride) = Finish(a1, ref c, taps, ref x1, i);
        Unsafe.Add(ref results, 2 * resultStride) = Finish(a2, ref c, taps, ref x2, i);
        Unsafe.Add(ref results, 3 * resultStride) = Finish(a3, ref c, taps, ref x3, i);
        Unsafe.Add(ref results, 4 * resultStride) = Finish(a4, ref c, taps, ref x4, i);
        Unsafe.Add(ref results, 5 * resultStride) = Finish(a5, ref c, taps, ref x5, i);
        Unsafe.Add(ref results, 6 * resultStride) = Finish(a6, ref c, taps, ref x6, i);
        Unsafe.Add(ref results, 7 * resultStride) = Finish(a7, ref c, taps, ref x7, i);
    }

    /// <summary>One output sample, summed exactly as <see cref="Sweep"/> sums each of its eight.</summary>
    private static double Single(ref double c, nuint taps, ref double x)
    {
        nuint i = 0;
        Vector256<double> acc = Vector256<double>.Zero;
        if (Vector256.IsHardwareAccelerated && taps >= 4)
        {
            nuint last = taps - 4;
            for (; i <= last; i += 4)
            {
                acc += Vector256.LoadUnsafe(ref c, i) * Vector256.LoadUnsafe(ref x, i);
            }
        }

        return Finish(acc, ref c, taps, ref x, i);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double Finish(Vector256<double> acc, ref double c, nuint taps, ref double x, nuint i)
    {
        double sum = Vector256.Sum(acc);
        for (; i < taps; i++)
        {
            sum += Unsafe.Add(ref c, i) * Unsafe.Add(ref x, i);
        }

        return sum;
    }
}
