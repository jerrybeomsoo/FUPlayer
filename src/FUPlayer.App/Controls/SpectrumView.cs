using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using FUPlayer.Core.Settings;

namespace FUPlayer.App.Controls;

/// <summary>Spectrum display: one line per analyzer trace, in the trace colour, over a logarithmic or linear axis.</summary>
public sealed class SpectrumView : Control
{
    private const double LeftMargin = 40;
    private const double RightMargin = 8;
    private const double TopMargin = 8;
    private const double BottomMargin = 18;

    /// <summary>A peak is held this long before it starts to fall.</summary>
    private const double PeakHoldSeconds = 1.5;

    /// <summary>How fast a held peak falls once it starts, in decibels per second.</summary>
    private const double PeakFallDbPerSecond = 14.0;

    private readonly PlotText _text = new();
    private AnalyzerFrame? _frame;
    private double[][] _series = [];
    private double[][] _peaks = [];
    private double[][] _peakAge = [];
    private long _lastUpdateTicks;
    private bool _hasPeaks;

    /// <summary>Shows a new update; the magnitudes are copied.</summary>
    public void Update(AnalyzerFrame frame)
    {
        int traces = frame.Traces.Count;
        if (_series.Length != traces || (traces > 0 && _series[0].Length != frame.Bins))
        {
            _series = new double[traces][];
            _peaks = new double[traces][];
            _peakAge = new double[traces][];
            for (int i = 0; i < traces; i++)
            {
                _series[i] = new double[frame.Bins];
                _peaks[i] = new double[frame.Bins];
                _peakAge[i] = new double[frame.Bins];
            }

            _hasPeaks = false;
        }

        for (int i = 0; i < traces; i++)
        {
            frame.Traces[i].MagnitudeDb.AsSpan(0, frame.Bins).CopyTo(_series[i]);
        }

        UpdatePeaks(frame);
        _frame = frame;
        InvalidateVisual();
    }

    public void Clear()
    {
        _frame = null;
        _hasPeaks = false;
        InvalidateVisual();
    }

    /// <summary>
    /// Raises each bin's held peak to whatever the new frame reached. In falling mode a peak that has not been
    /// touched for <see cref="PeakHoldSeconds"/> then slides down at a steady rate, so the line trails the music
    /// instead of freezing at the loudest moment of the track.
    /// </summary>
    private void UpdatePeaks(AnalyzerFrame frame)
    {
        long now = Environment.TickCount64;
        double elapsed = _hasPeaks ? Math.Clamp((now - _lastUpdateTicks) / 1000.0, 0.0, 1.0) : 0.0;
        _lastUpdateTicks = now;

        if (frame.PeakHold == AnalyzerPeakHold.None)
        {
            _hasPeaks = false;
            return;
        }

        bool falls = frame.PeakHold == AnalyzerPeakHold.Falling;
        for (int i = 0; i < _series.Length; i++)
        {
            double[] current = _series[i];
            double[] peak = _peaks[i];
            double[] age = _peakAge[i];
            for (int k = 0; k < current.Length; k++)
            {
                if (!_hasPeaks || current[k] >= peak[k])
                {
                    peak[k] = current[k];
                    age[k] = 0.0;
                }
                else if (falls)
                {
                    age[k] += elapsed;
                    if (age[k] > PeakHoldSeconds)
                    {
                        peak[k] = Math.Max(current[k], peak[k] - (PeakFallDbPerSecond * elapsed));
                    }
                }
            }
        }

        _hasPeaks = true;
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width < LeftMargin + 80 || bounds.Height < TopMargin + BottomMargin + 30)
        {
            return;
        }

        var plot = new Rect(LeftMargin, TopMargin, bounds.Width - LeftMargin - RightMargin, bounds.Height - TopMargin - BottomMargin);
        AnalyzerFrame? frame = _frame;
        double floor = frame?.FloorDb ?? -140.0;
        const double top = AnalyzerFrame.TopDb;
        FrequencyAxis axis = frame?.Axis ?? new FrequencyAxis(FrequencyScale.Logarithmic, 22_050);
        double X(double hz) => plot.Left + plot.Width * Math.Clamp(axis.ToFraction(hz), 0, 1);
        double Y(double db) => plot.Top + plot.Height * (top - Math.Clamp(db, floor, top)) / (top - floor);

        var gridPen = new Pen(Palette.GridBrush, 1);
        double dbStep = top - floor > 100 ? 20 : 10;
        for (double db = top; db >= floor - 1e-9; db -= dbStep)
        {
            double y = Y(db);
            context.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            _text.Draw(context, AnalyzerDrawing.DbLabel(db), new Point(4, y - 7), 10, Palette.TextMuted);
        }

        foreach ((double hz, bool labelled) in axis.Ticks(plot.Width))
        {
            double x = X(hz);
            context.DrawLine(gridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            if (labelled)
            {
                _text.Draw(context, PlotText.FormatFrequency(hz), new Point(x + 2, plot.Bottom + 2), 10, Palette.TextMuted);
            }
        }

        if (frame is null || _series.Length == 0)
        {
            AnalyzerDrawing.DrawHint(context, _text, plot, "The spectrum appears here during playback");
            return;
        }

        double nyquist = frame.Nyquist;
        if (frame.TopHz > nyquist * 1.0001)
        {
            double x = X(nyquist);
            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x50, 0, 0, 0)), null, new Rect(x, plot.Top, plot.Right - x, plot.Height));
            _text.Draw(context, "above fs/2", new Point(x + 6, plot.Top + 4), 10, Palette.TextMuted);
        }

        if (frame.MarkerHz > axis.MinimumHz && frame.MarkerHz < frame.TopHz)
        {
            double x = X(frame.MarkerHz);
            context.DrawLine(AnalyzerDrawing.Dashed(Palette.Warning, 0xB0), new Point(x, plot.Top), new Point(x, plot.Bottom));
            if (frame.MarkerLabel is { } label)
            {
                _text.Draw(context, label, new Point(x + 4, plot.Top + 18), 10, Palette.Warning);
            }
        }

        using (context.PushClip(plot))
        {
            int columns = Math.Max(2, (int)plot.Width);
            bool single = _series.Length == 1;

            // The held peaks sit behind the live traces, thin and dimmed so they read as a ceiling, not a second signal.
            if (_hasPeaks && frame.PeakHold != AnalyzerPeakHold.None)
            {
                for (int s = 0; s < _peaks.Length && s < frame.Traces.Count; s++)
                {
                    context.DrawGeometry(
                        null,
                        new Pen(new SolidColorBrush(Palette.WithAlpha(frame.Traces[s].Color, 0x8C)), 1),
                        Trace(_peaks[s], closed: false));
                }
            }

            for (int s = 0; s < _series.Length; s++)
            {
                double[] bins = _series[s];
                Color color = frame.Traces[s].Color;
                IBrush? fill = single
                    ? new LinearGradientBrush
                    {
                        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                        GradientStops = new GradientStops
                        {
                            new GradientStop(Palette.WithAlpha(color, 0x60), 0),
                            new GradientStop(Palette.WithAlpha(color, 0x06), 1),
                        },
                    }
                    : null;
                context.DrawGeometry(fill, new Pen(new SolidColorBrush(color), single ? 1.2 : 1.3), Trace(bins, single));
            }

            StreamGeometry Trace(double[] bins, bool closed)
            {
                var geometry = new StreamGeometry();
                using (StreamGeometryContext g = geometry.Open())
                {
                    bool started = false;
                    double lastX = plot.Left;
                    double binWidth = nyquist / (bins.Length - 1);
                    for (int c = 0; c < columns; c++)
                    {
                        double f0 = axis.ToHz((double)c / columns);
                        double f1 = axis.ToHz((c + 1.0) / columns);
                        if (f0 > nyquist)
                        {
                            break;
                        }

                        double level;
                        if (f1 - f0 < binWidth)
                        {
                            // Narrower than one bin (low frequencies on a logarithmic axis): interpolate between bin
                            // centres instead of drawing stair steps.
                            double position = Math.Min((f0 + f1) / 2, nyquist) / binWidth;
                            int index = Math.Min((int)position, bins.Length - 2);
                            level = bins[index] + (bins[index + 1] - bins[index]) * (position - index);
                        }
                        else
                        {
                            level = FrequencyAxis.Peak(bins, FrequencyAxis.BinRange(bins.Length, nyquist, f0, f1));
                        }

                        var point = new Point(plot.Left + plot.Width * c / columns, Y(level));
                        if (!started)
                        {
                            g.BeginFigure(closed ? new Point(point.X, plot.Bottom) : point, closed);
                            started = true;
                        }

                        g.LineTo(point);
                        lastX = point.X;
                    }

                    if (started)
                    {
                        if (closed)
                        {
                            g.LineTo(new Point(lastX, plot.Bottom));
                        }

                        g.EndFigure(closed);
                    }
                }

                return geometry;
            }
        }
    }
}
