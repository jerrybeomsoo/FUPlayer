using System.Globalization;

namespace FUPlayer.Core.Audio;

/// <summary>Sample-rate helpers (rate families, DSD multiples, formatting).</summary>
public static class AudioRates
{
    /// <summary>Common PCM output rates, ascending.</summary>
    public static readonly int[] StandardPcmRates =
    [
        44_100, 48_000, 88_200, 96_000, 176_400, 192_000, 352_800, 384_000,
        705_600, 768_000, 1_411_200, 1_536_000,
    ];

    /// <summary>DSD rate multiples relative to 44.1 kHz (or 48 kHz) that can be produced.</summary>
    public static readonly int[] DsdMultipliers = [64, 128, 256, 512, 1024];

    /// <summary>True when the rate belongs to the 11.025/22.05/44.1 kHz family.</summary>
    public static bool Is44k1Family(int rate) => rate % 11_025 == 0;

    public static int FamilyBase(int rate) => Is44k1Family(rate) ? 44_100 : 48_000;

    public static bool IsSameFamily(int a, int b) => Is44k1Family(a) == Is44k1Family(b);

    /// <summary>Returns 64 for 2.8224 MHz, 128 for 5.6448 MHz and so on.</summary>
    public static int DsdMultiplier(int dsdRate) => (int)Math.Round((double)dsdRate / FamilyBase(dsdRate));

    public static int DsdRate(int multiplier, bool family48k) => multiplier * (family48k ? 48_000 : 44_100);

    public static long Gcd(long a, long b)
    {
        a = Math.Abs(a);
        b = Math.Abs(b);
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a;
    }

    /// <summary>Reduced integer ratio <c>to/from = Up/Down</c>.</summary>
    public static (int Up, int Down) Ratio(int from, int to)
    {
        long g = Gcd(from, to);
        return ((int)(to / g), (int)(from / g));
    }

    public static bool IsIntegerMultiple(int from, int to) => to >= from && to % from == 0;

    public static bool IsPowerOfTwoMultiple(int from, int to)
    {
        if (!IsIntegerMultiple(from, to))
        {
            return false;
        }

        int m = to / from;
        return (m & (m - 1)) == 0;
    }

    public static string Format(int rate) => rate >= 1_000_000
        ? (rate / 1_000_000.0).ToString("0.####", CultureInfo.InvariantCulture) + " MHz"
        : (rate / 1000.0).ToString("0.###", CultureInfo.InvariantCulture) + " kHz";

    public static string FormatShort(int rate) => rate >= 1_000_000
        ? (rate / 1_000_000.0).ToString("0.###", CultureInfo.InvariantCulture) + "M"
        : (rate / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k";
}
