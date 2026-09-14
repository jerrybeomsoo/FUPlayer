using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;

namespace FUPlayer.App.Controls;

/// <summary>
/// Rotary volume control in dB. Drag vertically (Shift for fine steps), use the mouse wheel or the arrow keys.
/// Increases per gesture are rate-limited so a stray flick cannot jump to full level.
/// </summary>
public sealed class VolumeKnob : Control
{
    private const double StartAngle = 135.0;
    private const double SweepAngle = 270.0;
    private const double MaxIncreasePerEventDb = 6.0;

    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<VolumeKnob, double>(nameof(Value), -3.0, defaultBindingMode: BindingMode.TwoWay, coerce: CoerceValue);

    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<VolumeKnob, double>(nameof(Minimum), -60.0);

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<VolumeKnob, double>(nameof(Maximum));

    public static readonly StyledProperty<bool> IsBypassedProperty =
        AvaloniaProperty.Register<VolumeKnob, bool>(nameof(IsBypassed));

    public static readonly StyledProperty<bool> IsLimitingProperty =
        AvaloniaProperty.Register<VolumeKnob, bool>(nameof(IsLimiting));

    private readonly PlotText _text = new();
    private Point? _dragOrigin;
    private double _dragStartValue;

    static VolumeKnob()
    {
        AffectsRender<VolumeKnob>(ValueProperty, MinimumProperty, MaximumProperty, IsBypassedProperty, IsLimitingProperty);
        FocusableProperty.OverrideDefaultValue<VolumeKnob>(true);
    }

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public bool IsBypassed
    {
        get => GetValue(IsBypassedProperty);
        set => SetValue(IsBypassedProperty, value);
    }

    /// <summary>Lights the ring edge when the limiter engaged recently.</summary>
    public bool IsLimiting
    {
        get => GetValue(IsLimitingProperty);
        set => SetValue(IsLimitingProperty, value);
    }

    private static double CoerceValue(AvaloniaObject sender, double value)
    {
        var knob = (VolumeKnob)sender;
        return double.IsFinite(value) ? Math.Clamp(value, Math.Min(knob.Minimum, knob.Maximum), knob.Maximum) : knob.Minimum;
    }

    protected override Size MeasureOverride(Size availableSize) => new(Math.Min(64, availableSize.Width), Math.Min(64, availableSize.Height));

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MinimumProperty || change.Property == MaximumProperty)
        {
            CoerceValue(ValueProperty);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (IsBypassed || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _dragOrigin = e.GetPosition(this);
        _dragStartValue = Value;
        e.Pointer.Capture(this);
        Focus();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragOrigin is not { } origin)
        {
            return;
        }

        double dy = origin.Y - e.GetPosition(this).Y;
        double range = Math.Max(1.0, Maximum - Minimum);
        double perPixel = (e.KeyModifiers & KeyModifiers.Shift) != 0 ? range / 1200.0 : range / 240.0;
        double step = (e.KeyModifiers & KeyModifiers.Shift) != 0 ? 0.1 : 0.5;
        double target = Math.Round((_dragStartValue + dy * perPixel) / step) * step;
        SetLimited(target);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragOrigin is not null)
        {
            _dragOrigin = null;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (IsBypassed)
        {
            return;
        }

        double step = (e.KeyModifiers & KeyModifiers.Shift) != 0 ? 0.1 : 0.5;
        SetLimited(Value + Math.Sign(e.Delta.Y) * step);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (IsBypassed)
        {
            return;
        }

        double step = (e.KeyModifiers & KeyModifiers.Shift) != 0 ? 0.1 : 1.0;
        switch (e.Key)
        {
            case Key.Up:
            case Key.Right:
                SetLimited(Value + step);
                e.Handled = true;
                break;
            case Key.Down:
            case Key.Left:
                SetLimited(Value - step);
                e.Handled = true;
                break;
            case Key.End:
                Value = Minimum;
                e.Handled = true;
                break;
        }
    }

    private void SetLimited(double target)
    {
        double current = Value;
        if (target > current + MaxIncreasePerEventDb)
        {
            target = current + MaxIncreasePerEventDb;
        }

        Value = Math.Round(target * 10) / 10;
    }

    public override void Render(DrawingContext context)
    {
        double size = Math.Min(Bounds.Width, Bounds.Height);
        if (size < 16)
        {
            return;
        }

        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        double radius = size / 2 - 4;
        context.DrawEllipse(new SolidColorBrush(Palette.SurfaceHigh), new Pen(new SolidColorBrush(Palette.Border), 1), center, radius - 5, radius - 5);

        DrawArc(context, center, radius, StartAngle, SweepAngle, new Pen(Palette.TrackBrush, 3.5) { LineCap = PenLineCap.Round });
        if (!IsBypassed)
        {
            double fraction = Math.Clamp((Value - Minimum) / Math.Max(1e-9, Maximum - Minimum), 0, 1);
            Color color = IsLimiting ? Palette.Warning : Palette.Accent;
            if (fraction > 0.001)
            {
                DrawArc(context, center, radius, StartAngle, SweepAngle * fraction, new Pen(new SolidColorBrush(color), 3.5) { LineCap = PenLineCap.Round });
            }

            double angle = (StartAngle + SweepAngle * fraction) * Math.PI / 180;
            var tip = new Point(center.X + Math.Cos(angle) * (radius - 10), center.Y + Math.Sin(angle) * (radius - 10));
            context.DrawEllipse(new SolidColorBrush(color), null, tip, 2.2, 2.2);
        }

        string label = IsBypassed ? "FIXED" : Value.ToString(Math.Abs(Value) >= 10 ? "0" : "0.0", CultureInfo.CurrentCulture).Replace('-', '−');
        FormattedText main = _text.Get(label, IsBypassed ? 10 : 14, Palette.TextPrimary);
        context.DrawText(main, new Point(center.X - main.Width / 2, center.Y - main.Height / 2 - (IsBypassed ? 0 : 4)));
        if (!IsBypassed)
        {
            FormattedText unit = _text.Get("dB", 9, Palette.TextMuted);
            context.DrawText(unit, new Point(center.X - unit.Width / 2, center.Y + main.Height / 2 - 5));
        }
    }

    private static void DrawArc(DrawingContext context, Point center, double radius, double startDegrees, double sweepDegrees, IPen pen)
    {
        double start = startDegrees * Math.PI / 180;
        double end = (startDegrees + sweepDegrees) * Math.PI / 180;
        var geometry = new StreamGeometry();
        using (StreamGeometryContext g = geometry.Open())
        {
            g.BeginFigure(new Point(center.X + Math.Cos(start) * radius, center.Y + Math.Sin(start) * radius), false);
            g.ArcTo(
                new Point(center.X + Math.Cos(end) * radius, center.Y + Math.Sin(end) * radius),
                new Size(radius, radius),
                0,
                sweepDegrees > 180,
                SweepDirection.Clockwise,
                true);
            g.EndFigure(false);
        }

        context.DrawGeometry(null, pen, geometry);
    }
}
