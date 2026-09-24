using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using FUPlayer.Core.Settings;

namespace FUPlayer.App.Controls;

/// <summary>
/// Scrolling time-frequency display with the newest column on the right. Every analyzer trace gets its own lane,
/// labelled in the trace colour; the bar on the right shows how colour maps to level.
/// </summary>
public sealed class SpectrogramView : Control
{
    private const int Columns = 512;
    private const int Rows = 256;
    private const double LaneGap = 6;
    private const double ScaleWidth = 56;
    private const uint AboveNyquistColor = 0xFF0E1218;

    private static readonly (double Position, Color Color)[] HeatStops =
    [
        (0.00, Color.Parse("#05070A")),
        (0.22, Color.Parse("#122A4A")),
        (0.45, Color.Parse("#4B2D8F")),
        (0.65, Color.Parse("#B6378C")),
        (0.82, Color.Parse("#F2784B")),
        (1.00, Color.Parse("#FFE9A3")),
    ];

    private static readonly uint[] HeatColors = BuildPalette();

    private readonly PlotText _text = new();
    private readonly List<WriteableBitmap> _lanes = [];
    private (int Traces, int Bins, int SampleRate, double TopHz, FrequencyScale Scale, double FloorDb) _layout;
    private RowSample[] _rowBins = [];
    private AnalyzerFrame? _frame;
    private int _nextColumn;

    /// <summary>Adds one column per trace. A change of layout (traces, FFT size, rate, axis or floor) starts a new picture.</summary>
    public unsafe void Push(AnalyzerFrame frame)
    {
        if (frame.Traces.Count == 0 || frame.Bins < 2)
        {
            return;
        }

        var layout = (frame.Traces.Count, frame.Bins, frame.SampleRate, frame.TopHz, frame.Scale, frame.FloorDb);
        if (layout != _layout || _lanes.Count != frame.Traces.Count)
        {
            Reset();
            _layout = layout;
            FrequencyAxis axis = frame.Axis;
            _rowBins = new RowSample[Rows];
            for (int row = 0; row < Rows; row++)
            {
                _rowBins[row] = FrequencyAxis.SampleRow(frame.Bins, frame.Nyquist, axis.ToHz((double)row / Rows), axis.ToHz((row + 1.0) / Rows));
            }

            for (int i = 0; i < frame.Traces.Count; i++)
            {
                var bitmap = new WriteableBitmap(new PixelSize(Columns, Rows), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
                using (ILockedFramebuffer clear = bitmap.Lock())
                {
                    new Span<uint>((void*)clear.Address, clear.RowBytes / 4 * Rows).Fill(HeatColors[0]);
                }

                _lanes.Add(bitmap);
            }
        }

        double floor = frame.FloorDb;
        double span = Math.Max(1, AnalyzerFrame.TopDb - floor);
        for (int i = 0; i < _lanes.Count; i++)
        {
            ReadOnlySpan<double> bins = frame.Traces[i].MagnitudeDb.AsSpan(0, frame.Bins);
            using ILockedFramebuffer target = _lanes[i].Lock();
            uint* pixels = (uint*)target.Address;
            int stride = target.RowBytes / 4;
            for (int row = 0; row < Rows; row++)
            {
                double peak = FrequencyAxis.Read(bins, _rowBins[row]);
                uint color = double.IsNaN(peak)
                    ? AboveNyquistColor
                    : HeatColors[(int)(Math.Clamp((peak - floor) / span, 0, 1) * (HeatColors.Length - 1))];
                pixels[(Rows - 1 - row) * stride + _nextColumn] = color;
            }
        }

        _nextColumn = (_nextColumn + 1) % Columns;
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
        if (frame is null || _lanes.Count == 0 || bounds.Width < ScaleWidth + 80 || bounds.Height < 40)
        {
            AnalyzerDrawing.DrawHint(context, _text, bounds, "The spectrogram appears here during playback");
            return;
        }

        int count = _lanes.Count;
        double width = bounds.Width - ScaleWidth;
        double laneHeight = (bounds.Height - LaneGap * (count - 1)) / count;
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.MediumQuality }))
        {
            for (int i = 0; i < count; i++)
            {
                DrawLane(context, _lanes[i], new Rect(0, i * (laneHeight + LaneGap), width, laneHeight));
            }
        }

        FrequencyAxis axis = frame.Axis;
        for (int i = 0; i < count; i++)
        {
            DrawOverlay(context, frame, axis, i, new Rect(0, i * (laneHeight + LaneGap), width, laneHeight));
        }

        DrawScale(context, frame, new Rect(width + 10, 8, 10, bounds.Height - 16));
    }

    private static uint[] BuildPalette()
    {
        var colors = new uint[256];
        for (int i = 0; i < colors.Length; i++)
        {
            double t = i / 255.0;
            int s = 0;
            while (s < HeatStops.Length - 2 && t > HeatStops[s + 1].Position)
            {
                s++;
            }

            double local = (t - HeatStops[s].Position) / (HeatStops[s + 1].Position - HeatStops[s].Position);
            Color c = Palette.Mix(HeatStops[s].Color, HeatStops[s + 1].Color, local);
            colors[i] = 0xFF000000u | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
        }

        return colors;
    }

    private void Reset()
    {
        foreach (WriteableBitmap lane in _lanes)
        {
            lane.Dispose();
        }

        _lanes.Clear();
        _layout = default;
        _nextColumn = 0;
    }

    private void DrawLane(DrawingContext context, WriteableBitmap bitmap, Rect lane)
    {
        double scale = lane.Width / Columns;
        double olderWidth = (Columns - _nextColumn) * scale;
        context.DrawImage(bitmap, new Rect(_nextColumn, 0, Columns - _nextColumn, Rows), new Rect(lane.X, lane.Y, olderWidth, lane.Height));
        if (_nextColumn > 0)
        {
            context.DrawImage(bitmap, new Rect(0, 0, _nextColumn, Rows), new Rect(lane.X + olderWidth, lane.Y, _nextColumn * scale, lane.Height));
        }
    }

    private void DrawOverlay(DrawingContext context, AnalyzerFrame frame, FrequencyAxis axis, int index, Rect lane)
    {
        double Y(double hz) => lane.Bottom - lane.Height * Math.Clamp(axis.ToFraction(hz), 0, 1);

        var tickPen = new Pen(new SolidColorBrush(Palette.TextSecondary), 1);
        double lastLabel = double.PositiveInfinity;
        foreach ((double hz, bool labelled) in axis.Ticks(lane.Height))
        {
            double y = Y(hz);
            if (!labelled || hz <= axis.MinimumHz || lastLabel - y < 16 || y < lane.Top + 14)
            {
                continue;
            }

            context.DrawLine(tickPen, new Point(lane.Left, y), new Point(lane.Left + 4, y));
            _text.Draw(context, PlotText.FormatFrequency(hz), new Point(lane.Left + 6, y - 7), 10, Palette.TextSecondary);
            lastLabel = y;
        }

        if (frame.TopHz > frame.Nyquist * 1.0001)
        {
            double y = Y(frame.Nyquist);
            context.DrawLine(AnalyzerDrawing.Dashed(Palette.TextSecondary, 0x90), new Point(lane.Left, y), new Point(lane.Right, y));
        }

        if (frame.MarkerHz > axis.MinimumHz && frame.MarkerHz < frame.TopHz)
        {
            double y = Y(frame.MarkerHz);
            context.DrawLine(AnalyzerDrawing.Dashed(Palette.Warning, 0xC0), new Point(lane.Left, y), new Point(lane.Right, y));
            if (index == 0 && frame.MarkerLabel is { } label)
            {
                FormattedText text = _text.Get(label, 10, Palette.Warning);
                context.DrawText(text, new Point(lane.Left + 48, y - text.Height - 1));
            }
        }

        AnalyzerTrace trace = frame.Traces[Math.Min(index, frame.Traces.Count - 1)];
        AnalyzerDrawing.DrawChip(context, _text, trace.Label, trace.Color, new Point(lane.Right - 8, lane.Top + 6), alignRight: true);
    }

    private void DrawScale(DrawingContext context, AnalyzerFrame frame, Rect bar)
    {
        var stops = new GradientStops();
        foreach ((double position, Color color) in HeatStops)
        {
            stops.Add(new GradientStop(color, position));
        }

        var gradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            GradientStops = stops,
        };
        context.DrawRectangle(gradient, new Pen(Palette.GridBrush, 1), bar, 2, 2);
        _text.Draw(context, "0 dB", new Point(bar.Right + 4, bar.Top - 2), 10, Palette.TextMuted);
        _text.Draw(context, AnalyzerDrawing.DbLabel((AnalyzerFrame.TopDb + frame.FloorDb) / 2), new Point(bar.Right + 4, bar.Center.Y - 7), 10, Palette.TextMuted);
        _text.Draw(context, AnalyzerDrawing.DbLabel(frame.FloorDb), new Point(bar.Right + 4, bar.Bottom - 12), 10, Palette.TextMuted);
    }
}
