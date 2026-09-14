using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace FUPlayer.App.Controls;

/// <summary>Vertical level meter: RMS bar, translucent peak bar and a peak-hold line.</summary>
public sealed class LevelMeterBar : Control
{
    public static readonly StyledProperty<double> PeakDbProperty =
        AvaloniaProperty.Register<LevelMeterBar, double>(nameof(PeakDb), -120.0);

    public static readonly StyledProperty<double> RmsDbProperty =
        AvaloniaProperty.Register<LevelMeterBar, double>(nameof(RmsDb), -120.0);

    public static readonly StyledProperty<double> HoldDbProperty =
        AvaloniaProperty.Register<LevelMeterBar, double>(nameof(HoldDb), -120.0);

    public static readonly StyledProperty<double> RangeDbProperty =
        AvaloniaProperty.Register<LevelMeterBar, double>(nameof(RangeDb), 60.0);

    public static readonly StyledProperty<bool> IsOverProperty =
        AvaloniaProperty.Register<LevelMeterBar, bool>(nameof(IsOver));

    public static readonly StyledProperty<Color> AccentColorProperty =
        AvaloniaProperty.Register<LevelMeterBar, Color>(nameof(AccentColor), Palette.Accent);

    private readonly LinearGradientBrush _fill = new()
    {
        StartPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
    };

    private Color _fillAccent;

    static LevelMeterBar()
    {
        AffectsRender<LevelMeterBar>(PeakDbProperty, RmsDbProperty, HoldDbProperty, RangeDbProperty, IsOverProperty, AccentColorProperty);
    }

    public double PeakDb
    {
        get => GetValue(PeakDbProperty);
        set => SetValue(PeakDbProperty, value);
    }

    public double RmsDb
    {
        get => GetValue(RmsDbProperty);
        set => SetValue(RmsDbProperty, value);
    }

    public double HoldDb
    {
        get => GetValue(HoldDbProperty);
        set => SetValue(HoldDbProperty, value);
    }

    public double RangeDb
    {
        get => GetValue(RangeDbProperty);
        set => SetValue(RangeDbProperty, value);
    }

    public bool IsOver
    {
        get => GetValue(IsOverProperty);
        set => SetValue(IsOverProperty, value);
    }

    public Color AccentColor
    {
        get => GetValue(AccentColorProperty);
        set => SetValue(AccentColorProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        context.DrawRectangle(Palette.TrackBrush, null, bounds, 2, 2);
        if (_fillAccent != AccentColor || _fill.GradientStops.Count == 0)
        {
            _fillAccent = AccentColor;
            _fill.GradientStops = new GradientStops
            {
                new GradientStop(AccentColor, 0.0),
                new GradientStop(AccentColor, 0.62),
                new GradientStop(Palette.Warning, 0.85),
                new GradientStop(Palette.Danger, 1.0),
            };
        }

        double range = Math.Max(1.0, RangeDb);
        double Height(double db) => bounds.Height * Math.Clamp(1.0 + db / range, 0.0, 1.0);

        double peak = Height(PeakDb);
        using (context.PushClip(new Rect(0, bounds.Height - peak, bounds.Width, peak)))
        using (context.PushOpacity(0.35))
        {
            context.DrawRectangle(_fill, null, bounds, 2, 2);
        }

        double rms = Height(RmsDb);
        using (context.PushClip(new Rect(0, bounds.Height - rms, bounds.Width, rms)))
        {
            context.DrawRectangle(_fill, null, bounds, 2, 2);
        }

        double holdY = bounds.Height - Height(HoldDb);
        var hold = new Rect(0, Math.Clamp(holdY - 1, 0, Math.Max(0, bounds.Height - 2)), bounds.Width, 2);
        context.DrawRectangle(new SolidColorBrush(IsOver ? Palette.Danger : Palette.TextPrimary), null, hold);
    }
}
