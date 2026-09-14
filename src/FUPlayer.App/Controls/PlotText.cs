using System.Globalization;
using Avalonia;
using Avalonia.Media;

namespace FUPlayer.App.Controls;

/// <summary>Small cache of formatted labels for the plot controls.</summary>
internal sealed class PlotText
{
    private readonly Dictionary<(string, double, Color), FormattedText> _cache = new();

    public FormattedText Get(string text, double size, Color color)
    {
        if (!_cache.TryGetValue((text, size, color), out FormattedText? formatted))
        {
            if (_cache.Count > 512)
            {
                _cache.Clear();
            }

            formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, size, new SolidColorBrush(color));
            _cache[(text, size, color)] = formatted;
        }

        return formatted;
    }

    public void Draw(DrawingContext context, string text, Point origin, double size, Color color)
    {
        context.DrawText(Get(text, size, color), origin);
    }

    public static string FormatFrequency(double hz)
    {
        if (hz >= 1_000_000)
        {
            return (hz / 1_000_000).ToString("0.##", CultureInfo.InvariantCulture) + "M";
        }

        if (hz >= 1000)
        {
            return (hz / 1000).ToString("0.##", CultureInfo.InvariantCulture) + "k";
        }

        return hz.ToString("0", CultureInfo.InvariantCulture);
    }

    /// <summary>1-2-5 step giving roughly <paramref name="targetTicks"/> ticks over <paramref name="range"/>.</summary>
    public static double NiceStep(double range, int targetTicks)
    {
        if (range <= 0)
        {
            return 1;
        }

        double raw = range / Math.Max(1, targetTicks);
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double normalized = raw / magnitude;
        double nice = normalized < 1.5 ? 1 : normalized < 3 ? 2 : normalized < 7 ? 5 : 10;
        return nice * magnitude;
    }

    /// <summary>Frequency ticks for logarithmic axes: 1, 2 and 5 per decade.</summary>
    public static IEnumerable<double> LogTicks(double min, double max)
    {
        for (double decade = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(min, 1)))); decade <= max; decade *= 10)
        {
            foreach (double factor in (double[])[1, 2, 5])
            {
                double value = decade * factor;
                if (value >= min && value <= max)
                {
                    yield return value;
                }
            }
        }
    }
}
