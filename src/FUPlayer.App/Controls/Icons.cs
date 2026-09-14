using Avalonia.Media;

namespace FUPlayer.App.Controls;

/// <summary>Original line icons drawn on a 24 × 24 grid (rendered by <see cref="LineIcon"/>).</summary>
public static class Icons
{
    // Filled transport glyphs.
    public static readonly Geometry Play = Parse("M8,5.5 L18.5,12 L8,18.5 Z");
    public static readonly Geometry Pause = Parse("M7,5 H10.5 V19 H7 Z M13.5,5 H17 V19 H13.5 Z");
    public static readonly Geometry Stop = Parse("M6.5,6.5 H17.5 V17.5 H6.5 Z");
    public static readonly Geometry Next = Parse("M5.5,6 L14,12 L5.5,18 Z M15.5,6 H18.5 V18 H15.5 Z");
    public static readonly Geometry Previous = Parse("M18.5,6 L10,12 L18.5,18 Z M5.5,6 H8.5 V18 H5.5 Z");

    // Stroked glyphs.
    public static readonly Geometry Shuffle = Parse("M3,7 H7 C11,7 13,17 17,17 H21 M18,14 L21,17 L18,20 M3,17 H7 C8.6,17 9.7,15.6 10.7,13.8 M13.3,10.2 C14.3,8.4 15.4,7 17,7 H21 M18,4 L21,7 L18,10");
    public static readonly Geometry Repeat = Parse("M4,12 V10 C4,7.8 5.8,6 8,6 H19 M16,3 L19,6 L16,9 M20,12 V14 C20,16.2 18.2,18 16,18 H5 M8,21 L5,18 L8,15");
    public static readonly Geometry RepeatOne = Parse("M4,12 V10 C4,7.8 5.8,6 8,6 H19 M16,3 L19,6 L16,9 M20,12 V14 C20,16.2 18.2,18 16,18 H5 M8,21 L5,18 L8,15 M11,10.8 L12.6,9.8 V14.4");
    public static readonly Geometry NowPlaying = Parse("M3,12 A9,9 0 1 0 21,12 A9,9 0 1 0 3,12 Z M9.5,12 A2.5,2.5 0 1 0 14.5,12 A2.5,2.5 0 1 0 9.5,12 Z");
    public static readonly Geometry Queue = Parse("M4,6 H20 M4,11 H20 M4,16 H12 M15.5,14 L20.5,17 L15.5,20 Z");
    public static readonly Geometry Library = Parse("M4,4 H10 V10 H4 Z M14,4 H20 V10 H14 Z M4,14 H10 V20 H4 Z M14,14 H20 V20 H14 Z");
    public static readonly Geometry Dsp = Parse("M2,12 H5 L7.5,5 L11,19 L14,8 L16.5,15 L18,12 H22");
    public static readonly Geometry Output = Parse("M4,9 H8 L13,5 V19 L8,15 H4 Z M16.5,8.8 C18.2,10.5 18.2,13.5 16.5,15.2 M19,6.3 C22.1,9.4 22.1,14.6 19,17.7");
    public static readonly Geometry Calibration = Parse("M12,2.5 V6 M12,18 V21.5 M2.5,12 H6 M18,12 H21.5 M5.5,12 A6.5,6.5 0 1 0 18.5,12 A6.5,6.5 0 1 0 5.5,12 Z M10.5,12 A1.5,1.5 0 1 0 13.5,12 A1.5,1.5 0 1 0 10.5,12 Z");
    public static readonly Geometry Settings = Parse("M4,6 H13 M17,6 H20 M15,4 V8 M4,12 H7 M11,12 H20 M9,10 V14 M4,18 H11 M15,18 H20 M13,16 V20");
    public static readonly Geometry Add = Parse("M12,5 V19 M5,12 H19");
    public static readonly Geometry Folder = Parse("M3,7 C3,5.9 3.9,5 5,5 H9 L11,7 H19 C20.1,7 21,7.9 21,9 V17 C21,18.1 20.1,19 19,19 H5 C3.9,19 3,18.1 3,17 Z");
    public static readonly Geometry Music = Parse("M9,17.5 V6 L20,4 V15.5 M4,17.5 A2.5,2.5 0 1 0 9,17.5 A2.5,2.5 0 1 0 4,17.5 Z M15,15.5 A2.5,2.5 0 1 0 20,15.5 A2.5,2.5 0 1 0 15,15.5 Z");
    public static readonly Geometry Save = Parse("M5,4 H16 L20,8 V19 C20,19.6 19.6,20 19,20 H5 C4.4,20 4,19.6 4,19 V5 C4,4.4 4.4,4 5,4 Z M8,4 V9 H15 V4 M8,20 V14 H16 V20");
    public static readonly Geometry Trash = Parse("M4,7 H20 M9,7 V4 H15 V7 M6,7 L7,20 H17 L18,7 M10,11 V16 M14,11 V16");
    public static readonly Geometry Search = Parse("M4,10.5 A6.5,6.5 0 1 0 17,10.5 A6.5,6.5 0 1 0 4,10.5 Z M15.3,15.3 L20,20");
    public static readonly Geometry Refresh = Parse("M19.5,12 A7.5,7.5 0 1 1 17.3,6.7 M19.5,3.5 V8.5 H14.5");
    public static readonly Geometry Close = Parse("M6,6 L18,18 M18,6 L6,18");
    public static readonly Geometry ArrowUp = Parse("M12,19 V5 M6,11 L12,5 L18,11");
    public static readonly Geometry ArrowDown = Parse("M12,5 V19 M6,13 L12,19 L18,13");
    public static readonly Geometry PlayNext = Parse("M4,6 H14 M4,11 H14 M4,16 H10 M18,13 V21 M14,17 H22");
    public static readonly Geometry Warning = Parse("M12,3.5 L21.5,20 H2.5 Z M12,9.5 V14 M12,16.8 V17.2");
    public static readonly Geometry Wave = Parse("M3,12 C5,4 7,4 9,12 C11,20 13,20 15,12 C17,4 19,4 21,12");
    public static readonly Geometry Chip = Parse("M7,7 H17 V17 H7 Z M10,10 H14 V14 H10 Z M9,3 V7 M15,3 V7 M9,17 V21 M15,17 V21 M3,9 H7 M3,15 H7 M17,9 H21 M17,15 H21");
    public static readonly Geometry Check = Parse("M5,12.5 L10,17.5 L19,7");

    private static Geometry Parse(string data) => StreamGeometry.Parse(data);
}
