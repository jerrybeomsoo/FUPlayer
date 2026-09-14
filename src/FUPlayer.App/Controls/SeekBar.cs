using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using FUPlayer.App.Services;

namespace FUPlayer.App.Controls;

/// <summary>Playback position bar. Dragging previews the target and seeks once on release.</summary>
public sealed class SeekBar : Control
{
    public static readonly StyledProperty<double> PositionProperty =
        AvaloniaProperty.Register<SeekBar, double>(nameof(Position));

    public static readonly StyledProperty<double> DurationProperty =
        AvaloniaProperty.Register<SeekBar, double>(nameof(Duration));

    public static readonly StyledProperty<bool> IsSeekEnabledProperty =
        AvaloniaProperty.Register<SeekBar, bool>(nameof(IsSeekEnabled), true);

    public static readonly StyledProperty<ICommand?> SeekCommandProperty =
        AvaloniaProperty.Register<SeekBar, ICommand?>(nameof(SeekCommand));

    private readonly PlotText _text = new();
    private double? _dragFraction;
    private double? _hoverFraction;
    private double _seekFraction;
    private long _seekHoldUntil;

    static SeekBar()
    {
        AffectsRender<SeekBar>(PositionProperty, DurationProperty, IsSeekEnabledProperty);
    }

    /// <summary>Position in seconds.</summary>
    public double Position
    {
        get => GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    /// <summary>Duration in seconds (0 when unknown).</summary>
    public double Duration
    {
        get => GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    public bool IsSeekEnabled
    {
        get => GetValue(IsSeekEnabledProperty);
        set => SetValue(IsSeekEnabledProperty, value);
    }

    /// <summary>Executed with the target position in seconds (double).</summary>
    public ICommand? SeekCommand
    {
        get => GetValue(SeekCommandProperty);
        set => SetValue(SeekCommandProperty, value);
    }

    private bool CanSeek => IsSeekEnabled && Duration > 0;

    protected override Size MeasureOverride(Size availableSize) => new(0, 22);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!CanSeek || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _dragFraction = Fraction(e.GetPosition(this).X);
        e.Pointer.Capture(this);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        double fraction = Fraction(e.GetPosition(this).X);
        if (_dragFraction is not null)
        {
            _dragFraction = fraction;
        }

        _hoverFraction = CanSeek ? fraction : null;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hoverFraction = null;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragFraction is not { } fraction)
        {
            return;
        }

        _dragFraction = null;
        e.Pointer.Capture(null);
        double target = fraction * Duration;
        if (SeekCommand?.CanExecute(target) == true)
        {
            SeekCommand.Execute(target);

            // Keep showing the target until the engine reports the new position.
            _seekFraction = fraction;
            _seekHoldUntil = Environment.TickCount64 + 600;
        }

        InvalidateVisual();
        e.Handled = true;
    }

    private double Fraction(double x) => Math.Clamp((x - 6) / Math.Max(1, Bounds.Width - 12), 0, 1);

    public override void Render(DrawingContext context)
    {
        double width = Bounds.Width - 12;
        if (width <= 0)
        {
            return;
        }

        double y = Bounds.Height / 2;
        var track = new Rect(6, y - 2, width, 4);
        context.DrawRectangle(Palette.TrackBrush, null, track, 2, 2);

        double fraction = _dragFraction
            ?? (Environment.TickCount64 < _seekHoldUntil ? _seekFraction : Duration > 0 ? Math.Clamp(Position / Duration, 0, 1) : 0);
        bool active = _dragFraction is not null || _hoverFraction is not null;
        if (fraction > 0)
        {
            context.DrawRectangle(new SolidColorBrush(Palette.Accent), null, new Rect(6, y - 2, width * fraction, 4), 2, 2);
        }

        if (CanSeek && active)
        {
            context.DrawEllipse(new SolidColorBrush(Palette.TextPrimary), null, new Point(6 + width * fraction, y), 6, 6);
        }

        if (CanSeek && (_dragFraction ?? _hoverFraction) is { } preview)
        {
            FormattedText label = _text.Get(Formatting.Time(TimeSpan.FromSeconds(preview * Duration)), 10, Palette.TextSecondary);
            double x = Math.Clamp(6 + width * preview - label.Width / 2, 0, Math.Max(0, Bounds.Width - label.Width));
            context.DrawText(label, new Point(x, y - 6 - label.Height));
        }
    }
}
