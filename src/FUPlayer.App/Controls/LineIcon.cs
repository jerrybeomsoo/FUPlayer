using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace FUPlayer.App.Controls;

/// <summary>Draws a 24 × 24 icon geometry scaled to its size, stroked (line icons) or filled, in the inherited foreground.</summary>
public sealed class LineIcon : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<LineIcon, Geometry?>(nameof(Data));

    public static readonly StyledProperty<bool> IsFilledProperty =
        AvaloniaProperty.Register<LineIcon, bool>(nameof(IsFilled));

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<LineIcon, double>(nameof(StrokeThickness), 1.8);

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<LineIcon>();

    static LineIcon()
    {
        AffectsRender<LineIcon>(DataProperty, IsFilledProperty, StrokeThicknessProperty, ForegroundProperty);
        AffectsMeasure<LineIcon>(DataProperty);
    }

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public bool IsFilled
    {
        get => GetValue(IsFilledProperty);
        set => SetValue(IsFilledProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsNaN(Width) ? 20 : Width;
        double height = double.IsNaN(Height) ? 20 : Height;
        return new Size(Math.Min(width, availableSize.Width), Math.Min(height, availableSize.Height));
    }

    public override void Render(DrawingContext context)
    {
        if (Data is null || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        double scale = Math.Min(Bounds.Width, Bounds.Height) / 24.0;
        double offsetX = (Bounds.Width - 24 * scale) / 2;
        double offsetY = (Bounds.Height - 24 * scale) / 2;
        IBrush brush = Foreground ?? Brushes.White;
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offsetX, offsetY)))
        {
            if (IsFilled)
            {
                context.DrawGeometry(brush, null, Data);
            }
            else
            {
                var pen = new Pen(brush, StrokeThickness / scale * Math.Max(1.0, scale * 0.75))
                {
                    LineCap = PenLineCap.Round,
                    LineJoin = PenLineJoin.Round,
                };
                context.DrawGeometry(null, pen, Data);
            }
        }
    }
}
