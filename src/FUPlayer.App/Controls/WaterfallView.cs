using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using FUPlayer.Core.Settings;

namespace FUPlayer.App.Controls;

/// <summary>
/// Waterfall display: recent spectra drawn as receding lines, the newest at the front. Several traces are shown in
/// separate panels, each labelled in its trace colour.
/// </summary>
public sealed class WaterfallView : Control
{
    private const int Depth = 40;
    private const int Points = 240;
    private const double PanelGap = 10;

    private readonly PlotText _text = new();
    private readonly List<double[][]> _histories = [];
    private (int Traces, int Bins, int SampleRate, double TopHz, FrequencyScale Scale) _layout;
    private RowSample[] _pointBins = [];
    private AnalyzerFrame? _frame;
    private int _newest = -1;
    private int _filled;

    /// <summary>Adds one line per trace. A change of traces, FFT size, rate or axis starts a new history.</summary>
    public void Push(AnalyzerFrame frame)
    {
        if (frame.Traces.Count == 0 || frame.Bins < 2)
        {
            return;
        }

        var layout = (frame.Traces.Count, frame.Bins, frame.SampleRate, frame.TopHz, frame.Scale);
        if (layout != _layout || _histories.Count != frame.Traces.Count)
        {
            Reset();
            _layout = layout;
            FrequencyAxis axis = frame.Axis;
            _pointBins = new RowSample[Points];
            for (int p = 0; p < Points; p++)
            {
                double f0 = axis.ToHz(Math.Max(0.0, (p - 0.5) / (Points - 1)));
                double f1 = axis.ToHz(Math.Min(1.0, (p + 0.5) / (Points - 1)));
                _pointBins[p] = FrequencyAxis.SampleRow(frame.Bins, frame.Nyquist, f0, f1);
            }

            for (int i = 0; i < frame.Traces.Count; i++)
            {
                var history = new double[Depth][];
                for (int d = 0; d < Depth; d++)
                {
                    history[d] = new double[Points];
                }

                _histories.Add(history);
            }
        }

        _newest = (_newest + 1) % Depth;
        _filled = Math.Min(Depth, _filled + 1);
        for (int i = 0; i < _histories.Count; i++)
        {
            ReadOnlySpan<double> bins = frame.Traces[i].MagnitudeDb.AsSpan(0, frame.Bins);
            double[] line = _histories[i][_newest];
            for (int p = 0; p < Points; p++)
            {
                line[p] = FrequencyAxis.Read(bins, _pointBins[p]);
            }
        }

        _frame = frame;
        InvalidateVisual();
    }

    public void Clear()
    {
        Reset();
        _frame = null;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.DrawRectangle(new SolidColorBrush(Palette.Background), null, bounds, 6, 6);
        AnalyzerFrame? frame = _frame;
        if (frame is null || _histories.Count == 0 || _filled == 0)
        {
            AnalyzerDrawing.DrawHint(context, _text, bounds, "The waterfall appears here during playback");
            return;
        }

        int count = _histories.Count;
        int columns = count <= 2 ? count : count <= 4 ? 2 : 4;
        int rows = (count + columns - 1) / columns;
        double cellWidth = (bounds.Width - PanelGap * (columns - 1)) / columns;
        double cellHeight = (bounds.Height - PanelGap * (rows - 1)) / rows;
        for (int i = 0; i < count; i++)
        {
            var cell = new Rect(i % columns * (cellWidth + PanelGap), i / columns * (cellHeight + PanelGap), cellWidth, cellHeight);
            if (cell.Width > 140 && cell.Height > 90)
            {
                DrawPanel(context, frame, i, cell);
            }
        }
    }

    private void Reset()
    {
        _histories.Clear();
        _layout = default;
        _newest = -1;
        _filled = 0;
    }

    private void DrawPanel(DrawingContext context, AnalyzerFrame frame, int index, Rect cell)
    {
        var plot = new Rect(cell.X + 38, cell.Y + 30, cell.Width - 46, cell.Height - 50);
        FrequencyAxis axis = frame.Axis;
        double frontWidth = plot.Width * 0.8;
        double shiftX = plot.Width - frontWidth;
        double amplitude = plot.Height * 0.55;
        double shiftY = plot.Height - amplitude;
        double floor = frame.FloorDb;
        double span = Math.Max(1, AnalyzerFrame.TopDb - floor);
        Color color = frame.Traces[Math.Min(index, frame.Traces.Count - 1)].Color;
        var occlusion = new SolidColorBrush(Palette.Background);
        double[][] history = _histories[index];

        for (int age = _filled - 1; age >= 0; age--)
        {
            double[] line = history[(_newest - age + Depth) % Depth];
            double depth = (double)age / (Depth - 1);
            double left = plot.Left + shiftX * depth;
            double baseline = plot.Bottom - shiftY * depth;

            var outline = new StreamGeometry();
            var area = new StreamGeometry();
            using (StreamGeometryContext stroke = outline.Open())
            using (StreamGeometryContext fill = area.Open())
            {
                fill.BeginFigure(new Point(left, baseline), true);
                double lastX = left;
                bool started = false;
                for (int p = 0; p < Points; p++)
                {
                    double value = line[p];
                    if (double.IsNaN(value))
                    {
                        break;
                    }

                    var point = new Point(left + frontWidth * p / (Points - 1), baseline - amplitude * Math.Clamp((value - floor) / span, 0, 1));
                    if (!started)
                    {
                        stroke.BeginFigure(point, false);
                        started = true;
                    }
                    else
                    {
                        stroke.LineTo(point);
                    }

                    fill.LineTo(point);
                    lastX = point.X;
                }

                if (started)
                {
                    stroke.EndFigure(false);
                }

                fill.LineTo(new Point(lastX, baseline));
                fill.EndFigure(true);
            }

            byte alpha = (byte)(255 - 205 * depth);
            context.DrawGeometry(occlusion, null, area);
            context.DrawGeometry(null, new Pen(new SolidColorBrush(Palette.WithAlpha(color, alpha)), age == 0 ? 1.5 : 1.0), outline);
        }

        var gridPen = new Pen(Palette.GridBrush, 1);
        context.DrawLine(gridPen, new Point(plot.Left, plot.Bottom), new Point(plot.Left + frontWidth, plot.Bottom));
        foreach ((double hz, bool labelled) in axis.Ticks(frontWidth))
        {
            double x = plot.Left + frontWidth * Math.Clamp(axis.ToFraction(hz), 0, 1);
            context.DrawLine(gridPen, new Point(x, plot.Bottom), new Point(x, plot.Bottom + 4));
            if (labelled)
            {
                _text.Draw(context, PlotText.FormatFrequency(hz), new Point(x + 2, plot.Bottom + 3), 10, Palette.TextMuted);
            }
        }

        foreach (double db in new[] { AnalyzerFrame.TopDb, Math.Round(floor / 2 / 10) * 10, floor })
        {
            double y = plot.Bottom - amplitude * (db - floor) / span;
            context.DrawLine(gridPen, new Point(plot.Left - 4, y), new Point(plot.Left, y));
            _text.Draw(context, AnalyzerDrawing.DbLabel(db), new Point(cell.X + 4, y - 7), 10, Palette.TextMuted);
        }

        if (frame.TopHz > frame.Nyquist * 1.0001)
        {
            double x = plot.Left + frontWidth * axis.ToFraction(frame.Nyquist);
            context.DrawLine(AnalyzerDrawing.Dashed(Palette.TextSecondary, 0x90), new Point(x, plot.Bottom), new Point(x, plot.Bottom - amplitude));
        }

        if (frame.MarkerHz > axis.MinimumHz && frame.MarkerHz < frame.TopHz)
        {
            double x = plot.Left + frontWidth * axis.ToFraction(frame.MarkerHz);
            context.DrawLine(AnalyzerDrawing.Dashed(Palette.Warning, 0xC0), new Point(x, plot.Bottom), new Point(x, plot.Bottom - amplitude));
        }

        AnalyzerDrawing.DrawChip(context, _text, frame.Traces[Math.Min(index, frame.Traces.Count - 1)].Label, color, new Point(cell.X + 6, cell.Y + 6), alignRight: false);
    }
}
