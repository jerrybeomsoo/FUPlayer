using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace FUPlayer.App.Controls;

/// <summary>Colours shared by the custom-drawn controls (kept in sync with Styles/Theme.axaml).</summary>
public static class Palette
{
    public static readonly Color Background = Color.Parse("#0C0F14");
    public static readonly Color Surface = Color.Parse("#141920");
    public static readonly Color SurfaceHigh = Color.Parse("#1B222B");
    public static readonly Color Border = Color.Parse("#27303B");
    public static readonly Color TextPrimary = Color.Parse("#E8EDF3");
    public static readonly Color TextSecondary = Color.Parse("#98A5B3");
    public static readonly Color TextMuted = Color.Parse("#5F6B78");
    public static readonly Color Accent = Color.Parse("#3FD3C1");
    public static readonly Color AccentDsd = Color.Parse("#9B87FF");
    public static readonly Color Warning = Color.Parse("#F4C44F");
    public static readonly Color Danger = Color.Parse("#FF5A6A");
    public static readonly Color Good = Color.Parse("#62D892");

    public static readonly IImmutableSolidColorBrush TextSecondaryBrush = new ImmutableSolidColorBrush(TextSecondary);
    public static readonly IImmutableSolidColorBrush TextMutedBrush = new ImmutableSolidColorBrush(TextMuted);
    public static readonly IImmutableSolidColorBrush GridBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
    public static readonly IImmutableSolidColorBrush TrackBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));

    /// <summary>Analyzer colours by channel: left blue, right red (as on RCA plugs), then centre, LFE and surrounds.</summary>
    public static readonly Color[] ChannelColors =
    [
        Color.Parse("#5AB0FF"),
        Color.Parse("#FF6B61"),
        Color.Parse("#F4C44F"),
        Color.Parse("#9B87FF"),
        Color.Parse("#62D892"),
        Color.Parse("#FF9ED1"),
        Color.Parse("#3FD3C1"),
        Color.Parse("#D9A66B"),
    ];

    public static Color ChannelColor(int channel) => ChannelColors[(channel % ChannelColors.Length + ChannelColors.Length) % ChannelColors.Length];

    public static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    /// <summary>Linear interpolation between two colours.</summary>
    public static Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return Color.FromArgb(
            (byte)(a.A + (b.A - a.A) * t),
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }
}
