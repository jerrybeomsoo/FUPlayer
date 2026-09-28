using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace FUPlayer.App.Services;

/// <summary>
/// Who a process is, for Now playing: its product name, its main window's title, and its icon. The window title is
/// where most players put the track when they publish nothing else, and the icon stands in for a cover.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class AppIdentity
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>The executable behind a process, or null when the process is gone or will not say.</summary>
    public static string? ExecutablePath(int processId)
    {
        IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new char[1024];
            int size = buffer.Length;
            return QueryFullProcessImageName(process, 0, buffer, ref size) ? new string(buffer, 0, size) : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    /// <summary>The name the product gives itself ("Spotify", "Firefox"), falling back on the executable's name.</summary>
    public static string ProductName(string? executable, string fallback)
    {
        if (executable is null || !File.Exists(executable))
        {
            return fallback;
        }

        try
        {
            FileVersionInfo version = FileVersionInfo.GetVersionInfo(executable);
            string? name = !string.IsNullOrWhiteSpace(version.FileDescription) ? version.FileDescription : version.ProductName;
            return string.IsNullOrWhiteSpace(name) ? fallback : name.Trim();
        }
        catch (FileNotFoundException)
        {
            return fallback;
        }
    }

    /// <summary>The main window's title, or null when the process has none that shows.</summary>
    public static string? WindowTitle(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            string title = process.MainWindowTitle;
            return string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>The executable's own icon at up to <paramref name="size"/> pixels, or null.</summary>
    public static Bitmap? Icon(string? executable, int size = 256)
    {
        if (executable is null || !File.Exists(executable))
        {
            return null;
        }

        if (SHDefExtractIcon(executable, 0, 0, out IntPtr large, out IntPtr small, (uint)((16 << 16) | size)) != 0 || large == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return ToBitmap(large);
        }
        finally
        {
            DestroyIcon(large);
            if (small != IntPtr.Zero)
            {
                DestroyIcon(small);
            }
        }
    }

    private static unsafe Bitmap? ToBitmap(IntPtr icon)
    {
        if (!GetIconInfo(icon, out IconInfo info))
        {
            return null;
        }

        try
        {
            if (info.Color == IntPtr.Zero || GetObject(info.Color, Marshal.SizeOf<BitmapHeader>(), out BitmapHeader header) == 0)
            {
                return null;
            }

            int width = header.Width;
            int height = header.Height;
            var request = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = width,
                Height = -height,
                Planes = 1,
                BitCount = 32,
            };

            var pixels = new byte[width * height * 4];
            IntPtr screen = GetDC(IntPtr.Zero);
            try
            {
                if (GetDIBits(screen, info.Color, 0, (uint)height, pixels, ref request, 0) == 0)
                {
                    return null;
                }
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screen);
            }

            // Icons older than Windows XP carry no alpha and rely on the mask; shown opaque, they still read as the app.
            bool hasAlpha = false;
            for (int i = 3; i < pixels.Length; i += 4)
            {
                if (pixels[i] != 0)
                {
                    hasAlpha = true;
                    break;
                }
            }

            if (!hasAlpha)
            {
                for (int i = 3; i < pixels.Length; i += 4)
                {
                    pixels[i] = 255;
                }
            }

            var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
            using (ILockedFramebuffer target = bitmap.Lock())
            {
                for (int row = 0; row < height; row++)
                {
                    Marshal.Copy(pixels, row * width * 4, target.Address + (row * target.RowBytes), width * 4);
                }
            }

            return bitmap;
        }
        finally
        {
            if (info.Color != IntPtr.Zero)
            {
                DeleteObject(info.Color);
            }

            if (info.Mask != IntPtr.Zero)
            {
                DeleteObject(info.Mask);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int IsIcon;
        public int HotspotX;
        public int HotspotY;
        public IntPtr Mask;
        public IntPtr Color;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapHeader
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public IntPtr Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
        public uint ColorTable;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, char[] name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHDefExtractIconW")]
    private static extern int SHDefExtractIcon(string file, int index, uint flags, out IntPtr large, out IntPtr small, uint size);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr icon, out IconInfo info);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")]
    private static extern int GetObject(IntPtr handle, int size, out BitmapHeader header);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BitmapInfoHeader info, uint usage);
}
