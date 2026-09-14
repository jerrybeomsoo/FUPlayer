using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace FUPlayer.Core.Dsp.Numerics;

/// <summary>Vectorised numeric kernels used in the audio hot paths.</summary>
public static class SimdMath
{
    /// <summary>Returns Σ a[i]·b[i] over <c>a.Length</c> elements.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Dot(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        if (b.Length < a.Length)
        {
            ThrowLengthMismatch();
        }

        return Dot(ref MemoryMarshal.GetReference(a), ref MemoryMarshal.GetReference(b), (nuint)a.Length);
    }

    /// <summary>Unchecked dot product of two element runs.</summary>
    public static double Dot(ref double a, ref double b, nuint length)
    {
        nuint i = 0;
        double sum = 0.0;

        if (Vector512.IsHardwareAccelerated && length >= 32)
        {
            Vector512<double> acc0 = Vector512<double>.Zero;
            Vector512<double> acc1 = Vector512<double>.Zero;
            nuint last = length - 16;
            for (; i <= last; i += 16)
            {
                acc0 += Vector512.LoadUnsafe(ref a, i) * Vector512.LoadUnsafe(ref b, i);
                acc1 += Vector512.LoadUnsafe(ref a, i + 8) * Vector512.LoadUnsafe(ref b, i + 8);
            }

            sum = Vector512.Sum(acc0 + acc1);
        }
        else if (Vector256.IsHardwareAccelerated && length >= 16)
        {
            Vector256<double> acc0 = Vector256<double>.Zero;
            Vector256<double> acc1 = Vector256<double>.Zero;
            nuint last = length - 8;
            for (; i <= last; i += 8)
            {
                acc0 += Vector256.LoadUnsafe(ref a, i) * Vector256.LoadUnsafe(ref b, i);
                acc1 += Vector256.LoadUnsafe(ref a, i + 4) * Vector256.LoadUnsafe(ref b, i + 4);
            }

            sum = Vector256.Sum(acc0 + acc1);
        }
        else if (Vector128.IsHardwareAccelerated && length >= 8)
        {
            Vector128<double> acc0 = Vector128<double>.Zero;
            Vector128<double> acc1 = Vector128<double>.Zero;
            nuint last = length - 4;
            for (; i <= last; i += 4)
            {
                acc0 += Vector128.LoadUnsafe(ref a, i) * Vector128.LoadUnsafe(ref b, i);
                acc1 += Vector128.LoadUnsafe(ref a, i + 2) * Vector128.LoadUnsafe(ref b, i + 2);
            }

            sum = Vector128.Sum(acc0 + acc1);
        }

        for (; i < length; i++)
        {
            sum += Unsafe.Add(ref a, i) * Unsafe.Add(ref b, i);
        }

        return sum;
    }

    /// <summary>Multiplies every element by <paramref name="gain"/>.</summary>
    public static void Scale(Span<double> data, double gain)
    {
        ref double r = ref MemoryMarshal.GetReference(data);
        nuint n = (nuint)data.Length;
        nuint i = 0;

        if (Vector512.IsHardwareAccelerated && n >= 8)
        {
            Vector512<double> g = Vector512.Create(gain);
            for (; i + 8 <= n; i += 8)
            {
                (Vector512.LoadUnsafe(ref r, i) * g).StoreUnsafe(ref r, i);
            }
        }
        else if (Vector256.IsHardwareAccelerated && n >= 4)
        {
            Vector256<double> g = Vector256.Create(gain);
            for (; i + 4 <= n; i += 4)
            {
                (Vector256.LoadUnsafe(ref r, i) * g).StoreUnsafe(ref r, i);
            }
        }
        else if (Vector128.IsHardwareAccelerated && n >= 2)
        {
            Vector128<double> g = Vector128.Create(gain);
            for (; i + 2 <= n; i += 2)
            {
                (Vector128.LoadUnsafe(ref r, i) * g).StoreUnsafe(ref r, i);
            }
        }

        for (; i < n; i++)
        {
            Unsafe.Add(ref r, i) *= gain;
        }
    }

    /// <summary>Adds <paramref name="source"/>·<paramref name="gain"/> into <paramref name="destination"/>.</summary>
    /// <remarks>Multiply and add stay separate operations (no FMA), so every element gets the same result on all code paths.</remarks>
    public static void AddScaled(Span<double> destination, ReadOnlySpan<double> source, double gain)
    {
        if (source.Length < destination.Length)
        {
            ThrowLengthMismatch();
        }

        ref double d = ref MemoryMarshal.GetReference(destination);
        ref double s = ref MemoryMarshal.GetReference(source);
        nuint length = (nuint)destination.Length;
        nuint i = 0;
        if (Vector512.IsHardwareAccelerated && length >= 8)
        {
            Vector512<double> g = Vector512.Create(gain);
            for (; i + 8 <= length; i += 8)
            {
                (Vector512.LoadUnsafe(ref d, i) + Vector512.LoadUnsafe(ref s, i) * g).StoreUnsafe(ref d, i);
            }
        }
        else if (Vector256.IsHardwareAccelerated && length >= 4)
        {
            Vector256<double> g = Vector256.Create(gain);
            for (; i + 4 <= length; i += 4)
            {
                (Vector256.LoadUnsafe(ref d, i) + Vector256.LoadUnsafe(ref s, i) * g).StoreUnsafe(ref d, i);
            }
        }
        else if (Vector128.IsHardwareAccelerated && length >= 2)
        {
            Vector128<double> g = Vector128.Create(gain);
            for (; i + 2 <= length; i += 2)
            {
                (Vector128.LoadUnsafe(ref d, i) + Vector128.LoadUnsafe(ref s, i) * g).StoreUnsafe(ref d, i);
            }
        }

        for (; i < length; i++)
        {
            Unsafe.Add(ref d, i) += Unsafe.Add(ref s, i) * gain;
        }
    }

    /// <summary>Largest absolute sample value.</summary>
    public static double MaxAbs(ReadOnlySpan<double> data)
    {
        ref double r = ref MemoryMarshal.GetReference(data);
        nuint n = (nuint)data.Length;
        nuint i = 0;
        double max = 0.0;

        if (Vector512.IsHardwareAccelerated && n >= 8)
        {
            Vector512<double> acc = Vector512<double>.Zero;
            for (; i + 8 <= n; i += 8)
            {
                acc = Vector512.Max(acc, Vector512.Abs(Vector512.LoadUnsafe(ref r, i)));
            }

            for (int lane = 0; lane < Vector512<double>.Count; lane++)
            {
                max = Math.Max(max, acc[lane]);
            }
        }
        else if (Vector256.IsHardwareAccelerated && n >= 4)
        {
            Vector256<double> acc = Vector256<double>.Zero;
            for (; i + 4 <= n; i += 4)
            {
                acc = Vector256.Max(acc, Vector256.Abs(Vector256.LoadUnsafe(ref r, i)));
            }

            max = Math.Max(Math.Max(acc[0], acc[1]), Math.Max(acc[2], acc[3]));
        }
        else if (Vector128.IsHardwareAccelerated && n >= 2)
        {
            Vector128<double> acc = Vector128<double>.Zero;
            for (; i + 2 <= n; i += 2)
            {
                acc = Vector128.Max(acc, Vector128.Abs(Vector128.LoadUnsafe(ref r, i)));
            }

            max = Math.Max(acc[0], acc[1]);
        }

        for (; i < n; i++)
        {
            double v = Math.Abs(Unsafe.Add(ref r, i));
            if (v > max)
            {
                max = v;
            }
        }

        return max;
    }

    public static double SumOfSquares(ReadOnlySpan<double> data) => Dot(data, data);

    private static void ThrowLengthMismatch() =>
        throw new ArgumentException("Input spans have mismatching lengths.");
}
