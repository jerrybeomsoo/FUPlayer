using System.Globalization;

namespace FUPlayer.Audio.Windows;

/// <summary>
/// Log of the ASIO driver's life in this process: loading, opening, starting, stopping, the resets and rate
/// changes a driver asks for, and releasing. Nothing is written per buffer.
///
/// On by default, in %APPDATA%\FUPlayer\asio.log, kept under a megabyte: a driver that takes the process down
/// leaves nothing else behind, and the last lines say what it was asked to do. FUPLAYER_ASIO_LOG=0 turns it off;
/// a path writes there instead.
/// </summary>
public static class AsioTrace
{
    private static readonly object Gate = new();
    private static readonly string? LogPath = ResolvePath();

    private static int _loadedDrivers;
    private static string? _streamingDriver;
    private static string _lastAction = "nothing yet";
    private static DateTime _lastActionUtc = DateTime.UtcNow;

    public static bool IsEnabled => LogPath is not null;

    /// <summary>Where the log is written, or null when logging is off.</summary>
    public static string? Path => LogPath;

    /// <summary>Drivers this process currently holds (should be 0 when nothing is playing).</summary>
    public static int LoadedDrivers => Volatile.Read(ref _loadedDrivers);

    /// <summary>Short status for the Output page.</summary>
    public static string Status
    {
        get
        {
            lock (Gate)
            {
                string age = $"{(DateTime.UtcNow - _lastActionUtc).TotalSeconds:0} s ago";
                return _streamingDriver is { } streaming
                    ? $"Streaming through {streaming}"
                    : LoadedDrivers > 0
                        ? $"{LoadedDrivers} driver instance(s) still loaded. Last action: {_lastAction} ({age})"
                        : $"No driver loaded. Last action: {_lastAction} ({age})";
            }
        }
    }

    public static void Write(string message)
    {
        lock (Gate)
        {
            _lastAction = message;
            _lastActionUtc = DateTime.UtcNow;
        }

        if (LogPath is null)
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                File.AppendAllText(LogPath, string.Create(CultureInfo.InvariantCulture,
                    $"{DateTime.Now:HH:mm:ss.fff} [{Environment.CurrentManagedThreadId,3}] {message}{Environment.NewLine}"));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging must never break playback.
        }
    }

    internal static void DriverLoaded(string name)
    {
        Interlocked.Increment(ref _loadedDrivers);
        Write($"load {name} (loaded now: {LoadedDrivers})");
    }

    internal static void DriverReleased(string name)
    {
        Interlocked.Decrement(ref _loadedDrivers);
        Write($"release {name} (loaded now: {LoadedDrivers})");
    }

    internal static void Streaming(string? name)
    {
        lock (Gate)
        {
            _streamingDriver = name;
        }
    }

    private const long MaxLogBytes = 1 << 20;

    private static string? ResolvePath()
    {
        string? value = Environment.GetEnvironmentVariable("FUPLAYER_ASIO_LOG");
        if (value is "0" or "false")
        {
            return null;
        }

        try
        {
            string path = string.IsNullOrWhiteSpace(value) || value is "1" or "true"
                ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FUPlayer", "asio.log")
                : value;
            if (System.IO.Path.GetDirectoryName(path) is { Length: > 0 } folder)
            {
                Directory.CreateDirectory(folder);
            }

            // Keep the newest half once the log passes its size.
            var info = new FileInfo(path);
            if (info.Exists && info.Length > MaxLogBytes)
            {
                string[] lines = File.ReadAllLines(path);
                File.WriteAllLines(path, lines[(lines.Length / 2)..]);
            }

            File.AppendAllText(path, $"{Environment.NewLine}=== FUPlayer ASIO log, {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}");
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
