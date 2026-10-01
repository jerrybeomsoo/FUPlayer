using System.Globalization;
using FUPlayer.Core.Audio;

namespace FUPlayer.App.Services;

/// <summary>Display strings shared by the view models.</summary>
public static class Formatting
{
    public const char Minus = '−';

    public static string Time(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
        {
            time = TimeSpan.Zero;
        }

        return time.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalMinutes}:{time.Seconds:00}");
    }

    public static string Db(double db, string format = "0.0") =>
        db <= -120 ? "−∞ dB" : db.ToString(format, CultureInfo.CurrentCulture).Replace('-', Minus) + " dB";

    public static string Percent(double fraction) => (fraction * 100).ToString("0", CultureInfo.CurrentCulture) + "%";

    public static string Rate(int rate) => rate <= 0 ? "-" : AudioRates.Format(rate);

    public static string Milliseconds(double seconds) => (seconds * 1000).ToString(seconds < 0.01 ? "0.0" : "0", CultureInfo.CurrentCulture) + " ms";

    public static string Count(long value) => value.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>
    /// Multiply-adds per second, to three significant figures: "1.48 T ops/s" rather than the "1479.8 G ops/s" a
    /// scale that stopped at giga gave a filter of tens of millions of taps run tap by tap.
    /// </summary>
    public static string Operations(double perSecond)
    {
        (double scale, string unit) = perSecond switch
        {
            >= 1e12 => (1e12, " T ops/s"),
            >= 1e9 => (1e9, " G ops/s"),
            >= 1e6 => (1e6, " M ops/s"),
            >= 1e3 => (1e3, " k ops/s"),
            _ => (1.0, " ops/s"),
        };

        double value = perSecond / scale;
        string format = value >= 100 ? "0" : value >= 10 ? "0.0" : "0.00";
        return value.ToString(scale == 1.0 ? "0" : format, CultureInfo.CurrentCulture) + unit;
    }

    public static string FileSize(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.CurrentCulture) + " GB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0.0", CultureInfo.CurrentCulture) + " MB",
        _ => (bytes / 1024.0).ToString("0", CultureInfo.CurrentCulture) + " KB",
    };
}
