using System.Globalization;
using Avalonia;
using Avalonia.Media;
using FUPlayer.Core.Settings;

namespace FUPlayer.App.Controls;

/// <summary>One trace of an analyzer update: magnitudes in dBFS for FFT bins 0 … N/2.</summary>
public sealed record AnalyzerTrace(string Label, Color Color, double[] MagnitudeDb);

/// <summary>
/// Everything an analyzer display needs for one update. The magnitude arrays are reused by the producer, so displays
/// copy whatever they keep.
/// </summary>
public sealed record AnalyzerFrame(
    IReadOnlyList<AnalyzerTrace> Traces,
    int SampleRate,
    double BandHz,
    FrequencyScale Scale,
    double FloorDb,
    double MarkerHz,
    string? MarkerLabel,
    AnalyzerPeakHold PeakHold = AnalyzerPeakHold.None)
{
    public const double TopDb = 0.0;

    public double Nyquist => SampleRate / 2.0;

    /// <summary>Upper edge of the frequency axis: the chosen band, or the Nyquist frequency when none is chosen.</summary>
    public double TopHz => BandHz > 0 ? BandHz : Nyquist;

    public int Bins => Traces.Count == 0 ? 0 : Traces[0].MagnitudeDb.Length;

    internal FrequencyAxis Axis => new(Scale, TopHz);
}

/// <summary>Maps frequencies to 0 … 1 along a logarithmic (from 20 Hz) or linear (from 0 Hz) axis.</summary>
internal readonly record struct FrequencyAxis(FrequencyScale Scale, double MaximumHz)
{
    public const double LogMinimumHz = 20.0;

    public double MinimumHz => Scale == FrequencyScale.Logarithmic ? LogMinimumHz : 0.0;

    private double Top => Math.Max(MaximumHz, LogMinimumHz * 2);

    public double ToFraction(double hz) => Scale == FrequencyScale.Logarithmic
        ? Math.Log(Math.Max(hz, LogMinimumHz) / LogMinimumHz) / Math.Log(Top / LogMinimumHz)
        : hz / Top;

    public double ToHz(double fraction) => Scale == FrequencyScale.Logarithmic
        ? LogMinimumHz * Math.Pow(Top / LogMinimumHz, fraction)
        : fraction * Top;

    /// <summary>Grid frequencies for an axis <paramref name="pixels"/> long, each flagged when it should carry a label.</summary>
    public IEnumerable<(double Hz, bool Labelled)> Ticks(double pixels)
    {
        if (Scale == FrequencyScale.Logarithmic)
        {
            bool dense = pixels / Math.Log10(Top / LogMinimumHz) > 200;
            foreach (double hz in PlotText.LogTicks(LogMinimumHz, Top))
            {
                double mantissa = hz / Math.Pow(10, Math.Floor(Math.Log10(hz) + 1e-9));
                bool decade = Math.Abs(mantissa - 1) < 1e-6;
                yield return (hz, decade || dense || hz == LogMinimumHz);
            }

            yield break;
        }

        double step = PlotText.NiceStep(Top, Math.Clamp((int)(pixels / 80), 2, 12));
        for (int i = 0; i * step <= Top * (1 + 1e-9); i++)
        {
            yield return (i * step, true);
        }
    }

    /// <summary>FFT bins covering [<paramref name="f0"/>, <paramref name="f1"/>]; (−1, −1) when the range starts above Nyquist.</summary>
    public static (int Start, int End) BinRange(int bins, double nyquist, double f0, double f1)
    {
        if (bins < 2 || f0 > nyquist)
        {
            return (-1, -1);
        }

        double binWidth = nyquist / (bins - 1);
        int start = Math.Clamp((int)Math.Round(f0 / binWidth), 0, bins - 1);
        int end = Math.Clamp((int)Math.Round(Math.Min(f1, nyquist) / binWidth), start, bins - 1);
        return (start, end);
    }

    /// <summary>Highest value within a bin range; NaN for an empty range.</summary>
    public static double Peak(ReadOnlySpan<double> bins, (int Start, int End) range)
    {
        if (range.Start < 0)
        {
            return double.NaN;
        }

        double peak = bins[range.Start];
        for (int b = range.Start + 1; b <= range.End; b++)
        {
            peak = Math.Max(peak, bins[b]);
        }

        return peak;
    }
}

/// <summary>Drawing helpers shared by the analyzer displays.</summary>
internal static class AnalyzerDrawing
{
    public static string DbLabel(double db) => db.ToString("0", CultureInfo.InvariantCulture).Replace('-', '−');

    public static Pen Dashed(Color color, byte alpha) =>
        new(new SolidColorBrush(Palette.WithAlpha(color, alpha)), 1) { DashStyle = new DashStyle([4, 3], 0) };

    public static void DrawHint(DrawingContext context, PlotText text, Rect area, string message)
    {
        FormattedText hint = text.Get(message, 12, Palette.TextMuted);
        context.DrawText(hint, new Point(area.X + (area.Width - hint.Width) / 2, area.Y + (area.Height - hint.Height) / 2));
    }

    /// <summary>A small label with a colour swatch naming the trace drawn in that colour.</summary>
    public static void DrawChip(DrawingContext context, PlotText text, string label, Color color, Point anchor, bool alignRight)
    {
        FormattedText formatted = text.Get(label, 11, Palette.TextPrimary);
        double width = formatted.Width + 27;
        double x = alignRight ? anchor.X - width : anchor.X;
        context.DrawRectangle(new SolidColorBrush(Color.FromArgb(0xC8, 0x0C, 0x0F, 0x14)), null, new Rect(x, anchor.Y, width, 18), 5, 5);
        context.DrawRectangle(new SolidColorBrush(color), null, new Rect(x + 7, anchor.Y + 7.5, 11, 3), 1.5, 1.5);
        context.DrawText(formatted, new Point(x + 22, anchor.Y + (18 - formatted.Height) / 2));
    }
}
