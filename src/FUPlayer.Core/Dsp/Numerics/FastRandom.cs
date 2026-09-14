using System.Numerics;
using System.Runtime.CompilerServices;

namespace FUPlayer.Core.Dsp.Numerics;

/// <summary>xoshiro256** pseudo-random generator (public-domain algorithm by D. Blackman and S. Vigna).</summary>
public struct FastRandom
{
    private ulong _s0;
    private ulong _s1;
    private ulong _s2;
    private ulong _s3;
    private double _spare;
    private bool _hasSpare;

    public FastRandom(ulong seed)
    {
        _s0 = SplitMix(ref seed);
        _s1 = SplitMix(ref seed);
        _s2 = SplitMix(ref seed);
        _s3 = SplitMix(ref seed);
        _spare = 0.0;
        _hasSpare = false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong NextUInt64()
    {
        ulong result = BitOperations.RotateLeft(_s1 * 5, 7) * 9;
        ulong t = _s1 << 17;
        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = BitOperations.RotateLeft(_s3, 45);
        return result;
    }

    /// <summary>Uniform value in [−0.5, 0.5).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double NextCentered() => (NextUInt64() >> 11) * (1.0 / (1UL << 53)) - 0.5;

    /// <summary>Standard normal value (Box-Muller).</summary>
    public double NextGaussian()
    {
        if (_hasSpare)
        {
            _hasSpare = false;
            return _spare;
        }

        double u1;
        do
        {
            u1 = (NextUInt64() >> 11) * (1.0 / (1UL << 53));
        }
        while (u1 <= double.Epsilon);

        double u2 = (NextUInt64() >> 11) * (1.0 / (1UL << 53));
        double radius = Math.Sqrt(-2.0 * Math.Log(u1));
        double angle = 2.0 * Math.PI * u2;
        _spare = radius * Math.Sin(angle);
        _hasSpare = true;
        return radius * Math.Cos(angle);
    }

    private static ulong SplitMix(ref ulong state)
    {
        ulong z = state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
