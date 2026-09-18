using System.Globalization;
using FUPlayer.Core.Decoding;

namespace FUPlayer.Core.Capture;

/// <summary>An application whose render stream can be captured and re-processed.</summary>
/// <param name="ProcessId">Operating system process identifier.</param>
/// <param name="ProcessName">Executable name without its extension, for example "firefox".</param>
/// <param name="DisplayName">Name the application gave its audio session, or the executable name.</param>
/// <param name="IsPlaying">True while the session is rendering rather than idle.</param>
public sealed record CaptureTarget(int ProcessId, string ProcessName, string DisplayName, bool IsPlaying)
{
    public string Label => string.IsNullOrWhiteSpace(DisplayName) || DisplayName == ProcessName
        ? ProcessName
        : $"{DisplayName} ({ProcessName})";
}

/// <summary>
/// Platform hook for capturing one application's audio. Implemented by the Windows layer; null on a
/// platform that has no equivalent, in which case the feature is hidden rather than failing.
/// </summary>
public interface ICaptureProvider
{
    bool IsSupported { get; }

    /// <summary>Why capture is unavailable, when it is.</summary>
    string? UnsupportedReason { get; }

    /// <summary>Applications that currently hold an audio session, playing or idle.</summary>
    IReadOnlyList<CaptureTarget> List();

    /// <summary>Opens a live decoder over one application's output. The decoder never ends.</summary>
    IAudioDecoder Open(int processId, int channels, CaptureOptions? options = null);
}

/// <summary>How a capture treats the application's own playback, and where the player's output goes.</summary>
/// <param name="SilenceDirectOutput">
/// Mute the devices the application plays to while it is captured. Without it the application is heard twice
/// wherever its device and the player's are the same hardware, which a driver that mixes Windows and ASIO
/// playback makes an echo, the player's copy arriving a moment after the application's.
/// </param>
/// <param name="OutputBackendId">The player's output back-end, so its own device is never the one muted.</param>
/// <param name="OutputDeviceId">The player's output device on that back-end, or null for the default.</param>
public sealed record CaptureOptions(bool SilenceDirectOutput, string? OutputBackendId, string? OutputDeviceId);

/// <summary>
/// The pseudo-path that puts a live application into the queue, alongside the test-tone path. It is
/// not a file, so nothing in the library or the playlist writers should ever see one.
/// </summary>
public static class CaptureUri
{
    public const string Prefix = "fuplayer:capture/";

    public static string For(int processId) =>
        Prefix + processId.ToString(CultureInfo.InvariantCulture);

    public static bool TryParse(string? path, out int processId)
    {
        processId = 0;
        if (path is null || !path.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        return int.TryParse(path.AsSpan(Prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out processId)
            && processId > 0;
    }
}

/// <summary>Counters a live capture can report, for working out where audio stopped arriving.</summary>
public interface IDiagnosticCapture
{
    /// <summary>Frames the operating system actually delivered.</summary>
    long CapturedFrames { get; }

    /// <summary>Frames the reader invented because nothing arrived in time.</summary>
    long SilentFrames { get; }

    /// <summary>What was done about the application's own playback, or null when nothing was needed.</summary>
    string? DirectOutputNote { get; }
}
