using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace FUPlayer.App.Controls;

public enum PlotAxisScale
{
    Linear,
    Logarithmic,
}

public enum PlotAxisFormat
{
    Plain,
    Frequency,
    Milliseconds,
    Decibels,
}

/// <summary>One curve of a <see cref="ResponsePlot"/>.</summary>
public sealed record PlotSeries(string Label, double[] X, double[] Y, Color Color, double Thickness = 1.6);

/// <summary>Vertical marker line with a label.</summary>
public sealed record PlotMarker(double X, string Label, Color Color);

/// <summary>General X/Y plot for filter magnitude, impulse and step responses (dense data is min/max decimated per pixel).</summary>
public sealed class ResponsePlot : Control
{
    public static readonly StyledProperty<IReadOnlyList<PlotSeries>?> SeriesProperty =
        AvaloniaProperty.Register<ResponsePlot, IReadOnlyList<PlotSeries>?>(nameof(Series));

    public static readonly StyledProperty<IReadOnlyList<PlotMarker>?> MarkersProperty =
        AvaloniaProperty.Register<ResponsePlot, IReadOnlyList<PlotMarker>?>(nameof(Markers));

    public static readonly StyledProperty<PlotAxisScale> XScaleProperty =
        AvaloniaProperty.Register<ResponsePlot, PlotAxisScale>(nameof(XScale));

    public static readonly StyledProperty<PlotAxisFormat> XFormatProperty =
        AvaloniaProperty.Register<ResponsePlot, PlotAxisFormat>(nameof(XFormat));

    public static readonly StyledProperty<PlotAxisFormat> YFormatProperty =
        AvaloniaProperty.Register<ResponsePlot, PlotAxisFormat>(nameof(YFormat));

    public static readonly StyledProperty<double> XMinimumProperty =
        AvaloniaProperty.Register<ResponsePlot, double>(nameof(XMinimum), double.NaN);

    public static readonly StyledProperty<double> XMaximumProperty =
        AvaloniaProperty.Register<ResponsePlot, double>(nameof(XMaximum), double.NaN);

    public static readonly StyledProperty<double> YMinimumProperty =
        AvaloniaProperty.Register<ResponsePlot, double>(nameof(YMinimum), double.NaN);

    public static readonly StyledProperty<double> YMaximumProperty =
        AvaloniaProperty.Register<ResponsePlot, double>(nameof(YMaximum), double.NaN);

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<ResponsePlot, string?>(nameof(Title));

    private readonly PlotText _text = new();

    static ResponsePlot()
    {
        AffectsRender<ResponsePlot>(SeriesProperty, MarkersProperty, XScaleProperty, XFormatProperty, YFormatProperty,
            XMinimumProperty, XMaximumProperty, YMinimumProperty, YMaximumProperty, TitleProperty);
    }

    public IReadOnlyList<PlotSeries>? Series
    {
        get => GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public IReadOnlyList<PlotMarker>? Markers
    {
        get => GetValue(MarkersProperty);
        set => SetValue(MarkersProperty, value);
    }

    public PlotAxisScale XScale
    {
        get => GetValue(XScaleProperty);
        set => SetValue(XScaleProperty, value);
    }

    public PlotAxisFormat XFormat
    {
        get => GetValue(XFormatProperty);
        set => SetValue(XFormatProperty, value);
    }

    public PlotAxisFormat YFormat
    {
        get => GetValue(YFormatProperty);
        set => SetValue(YFormatProperty, value);
    }

    public double XMinimum
    {
        get => GetValue(XMinimumProperty);
        set => SetValue(XMinimumProperty, value);
    }

    public double XMaximum
    {
        get => GetValue(XMaximumProperty);
        set => SetValue(XMaximumProperty, value);
    }

    public double YMinimum
    {
        get => GetValue(YMinimumProperty);
        set => SetValue(YMinimumProperty, value);
    }

    public double YMaximum
    {
        get => GetValue(YMaximumProperty);
        set => SetValue(YMaximumProperty, value);
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width < 80 || bounds.Height < 60)
        {
            return;
        }

        IReadOnlyList<PlotSeries> series = Series ?? [];
        (double xMin, double xMax) = Range(series.SelectMany(s => s.X), XMinimum, XMaximum, XScale == PlotAxisScale.Logarithmic);
        (double yMin, double yMax) = Range(series.SelectMany(s => s.Y), YMinimum, YMaximum, logarithmic: false);
        if (yMax - yMin < 1e-12)
        {
            yMax = yMin + 1;
        }

        var plot = new Rect(52, string.IsNullOrEmpty(Title) ? 8 : 24, bounds.Width - 64, bounds.Height - (string.IsNullOrEmpty(Title) ? 8 : 24) - 22);
        bool log = XScale == PlotAxisScale.Logarithmic && xMin > 0;
        double X(double x) => log
            ? plot.Left + plot.Width * (Math.Log10(Math.Max(x, xMin)) - Math.Log10(xMin)) / (Math.Log10(xMax) - Math.Log10(xMin))
            : plot.Left + plot.Width * (x - xMin) / (xMax - xMin);
        double Y(double y) => plot.Top + plot.Height * (1 - (Math.Clamp(y, yMin - (yMax - yMin), yMax + (yMax - yMin)) - yMin) / (yMax - yMin));

        context.DrawRectangle(new SolidColorBrush(Palette.WithAlpha(Palette.Background, 0x80)), null, plot, 4, 4);
        var gridPen = new Pen(Palette.GridBrush, 1);

        double yStep = PlotText.NiceStep(yMax - yMin, 6);
        for (double y = Math.Ceiling(yMin / yStep) * yStep; y <= yMax + yStep * 1e-6; y += yStep)
        {
            double py = Y(y);
            context.DrawLine(gridPen, new Point(plot.Left, py), new Point(plot.Right, py));
            string label = Format(y, YFormat);
            FormattedText text = _text.Get(label, 10, Palette.TextMuted);
            context.DrawText(text, new Point(plot.Left - text.Width - 6, py - text.Height / 2));
        }

        IEnumerable<double> xTicks = log
            ? PlotText.LogTicks(xMin, xMax)
            : Enumerable.Range(0, 64).Select(i => Math.Ceiling(xMin / PlotText.NiceStep(xMax - xMin, 7)) * PlotText.NiceStep(xMax - xMin, 7) + i * PlotText.NiceStep(xMax - xMin, 7)).TakeWhile(v => v <= xMax + 1e-9);
        double lastLabelRight = double.NegativeInfinity;
        foreach (double x in xTicks)
        {
            double px = X(x);
            context.DrawLine(gridPen, new Point(px, plot.Top), new Point(px, plot.Bottom));
            FormattedText text = _text.Get(Format(x, XFormat), 10, Palette.TextMuted);
            double labelLeft = px - text.Width / 2;
            if (labelLeft > lastLabelRight + 6)
            {
                context.DrawText(text, new Point(labelLeft, plot.Bottom + 4));
                lastLabelRight = labelLeft + text.Width;
            }
        }

        using (context.PushClip(plot))
        {
            foreach (PlotMarker marker in Markers ?? [])
            {
                if (marker.X < xMin || marker.X > xMax)
                {
                    continue;
                }

                double px = X(marker.X);
                context.DrawLine(new Pen(new SolidColorBrush(Palette.WithAlpha(marker.Color, 0xC0)), 1) { DashStyle = new DashStyle([4, 3], 0) }, new Point(px, plot.Top), new Point(px, plot.Bottom));
                _text.Draw(context, marker.Label, new Point(px + 4, plot.Top + 2), 10, marker.Color);
            }

            foreach (PlotSeries curve in series)
            {
                DrawSeries(context, curve, plot, X, Y);
            }
        }

        if (!string.IsNullOrEmpty(Title))
        {
            _text.Draw(context, Title, new Point(plot.Left, 4), 12, Palette.TextSecondary);
        }

        double legendX = plot.Right;
        foreach (PlotSeries curve in series.Reverse())
        {
            FormattedText label = _text.Get(curve.Label, 10, curve.Color);
            legendX -= label.Width + 12;
            context.DrawText(label, new Point(legendX, string.IsNullOrEmpty(Title) ? plot.Top + 2 : 6));
        }
    }

    private static void DrawSeries(DrawingContext context, PlotSeries series, Rect plot, Func<double, double> x, Func<double, double> y)
    {
        int count = Math.Min(series.X.Length, series.Y.Length);
        if (count < 2)
        {
            return;
        }

        var geometry = new StreamGeometry();
        using (StreamGeometryContext g = geometry.Open())
        {
            int column = int.MinValue;
            double columnMin = 0;
            double columnMax = 0;
            double columnLast = 0;
            bool started = false;

            void Flush(int col)
            {
                if (!started)
                {
                    g.BeginFigure(new Point(col, columnMin), false);
                    started = true;
                }

                g.LineTo(new Point(col, columnMin), true);
                g.LineTo(new Point(col, columnMax), true);
                g.LineTo(new Point(col, columnLast), true);
            }

            for (int i = 0; i < count; i++)
            {
                double px = x(series.X[i]);
                double py = y(series.Y[i]);
                if (double.IsNaN(px) || double.IsNaN(py))
                {
                    continue;
                }

                int col = (int)Math.Round(px);
                if (col != column)
                {
                    if (column != int.MinValue)
                    {
                        Flush(column);
                    }

                    column = col;
                    columnMin = py;
                    columnMax = py;
                }
                else
                {
                    columnMin = Math.Min(columnMin, py);
                    columnMax = Math.Max(columnMax, py);
                }

                columnLast = py;
            }

            if (column != int.MinValue)
            {
                Flush(column);
            }

            if (started)
            {
                g.EndFigure(false);
            }
        }

        context.DrawGeometry(null, new Pen(new SolidColorBrush(series.Color), series.Thickness) { LineJoin = PenLineJoin.Round }, geometry);
    }

    private static (double Min, double Max) Range(IEnumerable<double> values, double requestedMin, double requestedMax, bool logarithmic)
    {
        double min = requestedMin;
        double max = requestedMax;
        if (double.IsNaN(min) || double.IsNaN(max))
        {
            double dataMin = double.PositiveInfinity;
            double dataMax = double.NegativeInfinity;
            foreach (double v in values)
            {
                if (double.IsFinite(v) && (!logarithmic || v > 0))
                {
                    dataMin = Math.Min(dataMin, v);
                    dataMax = Math.Max(dataMax, v);
                }
            }

            if (double.IsInfinity(dataMin))
            {
                dataMin = logarithmic ? 1 : 0;
                dataMax = logarithmic ? 10 : 1;
            }

            min = double.IsNaN(min) ? dataMin : min;
            max = double.IsNaN(max) ? dataMax : max;
        }

        if (max <= min)
        {
            max = min + (logarithmic ? min * 9 : 1);
        }

        return (min, max);
    }

    private static string Format(double value, PlotAxisFormat format) => format switch
    {
        PlotAxisFormat.Frequency => PlotText.FormatFrequency(value),
        PlotAxisFormat.Milliseconds => value.ToString(Math.Abs(value) < 1 ? "0.###" : "0.#", CultureInfo.CurrentCulture) + " ms",
        PlotAxisFormat.Decibels => value.ToString("0", CultureInfo.CurrentCulture),
        _ => value.ToString("0.###", CultureInfo.CurrentCulture),
    };
}
