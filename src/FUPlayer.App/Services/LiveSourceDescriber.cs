using System.Diagnostics;
using System.Runtime.Versioning;
using Avalonia.Media.Imaging;
using FUPlayer.Core.Engine;
using FUPlayer.Core.Upnp;

namespace FUPlayer.App.Services;

/// <summary>What Now playing shows for a source outside the queue.</summary>
/// <param name="Title">The track, or the application or stream when the track is not known.</param>
/// <param name="Subtitle">The artist, or what the source is.</param>
/// <param name="Album">The album, when known.</param>
/// <param name="Details">Where the audio comes from, in a line.</param>
/// <param name="Source">The label above the title: LIVE INPUT, or UPNP and the controller.</param>
/// <param name="Cover">The cover, or the application's icon, or null.</param>
public sealed record LiveTrack(string Title, string Subtitle, string? Album, string Details, string Source, Bitmap? Cover);

/// <summary>
/// Works out what a captured application or a UPnP stream is playing. The best answer is the media session the
/// application publishes to Windows, which foobar2000, Spotify and the browsers all do; then what the stream's own
/// metadata says; then the application's window title, which is where most players put the track anyway.
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
public sealed class LiveSourceDescriber
{
    private static readonly HttpClient ArtClient = new() { Timeout = TimeSpan.FromSeconds(5) };

    private readonly MediaSessions _sessions = new();
    private (int ProcessId, string? Executable, string Product, Bitmap? Icon) _app;
    private (string Name, Bitmap? Icon) _controllerIcon = (string.Empty, null);
    private (byte[]? Bytes, Bitmap? Bitmap) _sessionCover;
    private (string Url, Bitmap? Bitmap) _art = (string.Empty, null);

    public async Task<LiveTrack?> DescribeAsync(PlaybackStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (status.IsCapture && status.CaptureProcessId > 0)
        {
            return await DescribeCaptureAsync(status);
        }

        if (status.IsStream && status.Stream is { } stream)
        {
            return await DescribeStreamAsync(status, stream);
        }

        return null;
    }

    private async Task<LiveTrack> DescribeCaptureAsync(PlaybackStatus status)
    {
        int processId = status.CaptureProcessId;
        if (_app.ProcessId != processId)
        {
            string? executable = AppIdentity.ExecutablePath(processId);
            string name = executable is null ? $"Process {processId}" : Path.GetFileNameWithoutExtension(executable);
            _app = (processId, executable, AppIdentity.ProductName(executable, name), AppIdentity.Icon(executable));
        }

        string processName = _app.Executable is null ? string.Empty : Path.GetFileNameWithoutExtension(_app.Executable);
        string details = $"Captured from {_app.Product}{Format(status)}";
        MediaSessionInfo? session = processName.Length > 0 ? await _sessions.FindAsync(app => Matches(app, processName)) : null;
        if (session is { HasTrack: true })
        {
            return new LiveTrack(session.Title!, session.Artist ?? _app.Product, session.Album, details, "LIVE INPUT", Cover(session) ?? _app.Icon);
        }

        // No session: the window title, which most players set to the track. An application that shows only its own
        // name there has nothing more to say, and the line underneath says it is live input.
        string? title = AppIdentity.WindowTitle(processId);
        bool meaningful = title is not null && !string.Equals(title, _app.Product, StringComparison.OrdinalIgnoreCase);
        return new LiveTrack(meaningful ? title! : _app.Product, meaningful ? _app.Product : "Live input", null, details, "LIVE INPUT", _app.Icon);
    }

    private async Task<LiveTrack> DescribeStreamAsync(PlaybackStatus status, NetworkStreamStatus stream)
    {
        string controller = stream.Controller;
        string product = ControllerProduct(controller);
        bool local = controller.EndsWith("on this computer", StringComparison.Ordinal);
        string source = "UPNP · " + product.ToUpperInvariant();
        string details = $"{stream.Container} stream from {controller}{Format(status)}";

        // A controller on this computer usually publishes a media session with the track in it, which a stream of its
        // output does not carry: foobar2000's UPnP output calls every stream "foobar2000 audio stream".
        MediaSessionInfo? session = local ? await _sessions.FindAsync(app => Matches(app, product)) : null;
        StreamMetadata? metadata = stream.Metadata;
        Bitmap? icon = local ? ControllerIcon(product) : null;
        if (session is { HasTrack: true })
        {
            return new LiveTrack(session.Title!, session.Artist ?? product, session.Album, details, source, Cover(session) ?? await ArtAsync(metadata) ?? icon);
        }

        if (metadata is { HasTrack: true } && !IsGeneric(metadata))
        {
            return new LiveTrack(metadata.Title ?? product, metadata.Artist ?? product, metadata.Album, details, source, await ArtAsync(metadata) ?? icon);
        }

        return new LiveTrack($"Stream from {product}", controller, null, details, source, await ArtAsync(metadata) ?? icon);
    }

    /// <summary>A session's application id against a program: "foobar2000.exe" for "foobar2000", or a packaged app that names it.</summary>
    private static bool Matches(string app, string program) =>
        program.Length > 0
        && (app.Equals(program + ".exe", StringComparison.OrdinalIgnoreCase)
            || app.Equals(program, StringComparison.OrdinalIgnoreCase)
            || app.Contains(program, StringComparison.OrdinalIgnoreCase));

    /// <summary>"foobar2000 on this computer" → "foobar2000"; "BubbleUPnP at 192.168.1.20" → "BubbleUPnP".</summary>
    private static string ControllerProduct(string controller)
    {
        foreach (string separator in new[] { " on this computer", " at " })
        {
            int at = controller.IndexOf(separator, StringComparison.Ordinal);
            if (at > 0)
            {
                return controller[..at];
            }
        }

        return controller;
    }

    /// <summary>A stream's own name for itself rather than a track's: a title with nothing else, ending "stream".</summary>
    private static bool IsGeneric(StreamMetadata metadata) =>
        metadata.Artist is null && metadata.Album is null
        && metadata.Title?.EndsWith("stream", StringComparison.OrdinalIgnoreCase) == true;

    private static string Format(PlaybackStatus status) =>
        status.Plan is { Source.IsValid: true } plan ? $"  ·  {plan.Source.DescribeShort()}" : string.Empty;

    private Bitmap? Cover(MediaSessionInfo session)
    {
        if (session.Cover is null)
        {
            return null;
        }

        if (!ReferenceEquals(session.Cover, _sessionCover.Bytes))
        {
            _sessionCover = (session.Cover, Decode(session.Cover));
        }

        return _sessionCover.Bitmap;
    }

    private Bitmap? ControllerIcon(string product)
    {
        if (_controllerIcon.Name != product)
        {
            Bitmap? icon = null;
            try
            {
                Process? process = Process.GetProcessesByName(product).FirstOrDefault();
                icon = process is null ? null : AppIdentity.Icon(AppIdentity.ExecutablePath(process.Id));
            }
            catch (InvalidOperationException)
            {
            }

            _controllerIcon = (product, icon);
        }

        return _controllerIcon.Icon;
    }

    /// <summary>The cover the stream's metadata points at, fetched once per address.</summary>
    private async Task<Bitmap?> ArtAsync(StreamMetadata? metadata)
    {
        string? url = metadata?.AlbumArtUri;
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out Uri? address) || address.Scheme is not ("http" or "https"))
        {
            return null;
        }

        if (_art.Url == url)
        {
            return _art.Bitmap;
        }

        Bitmap? bitmap = null;
        try
        {
            bitmap = Decode(await ArtClient.GetByteArrayAsync(address));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
        }

        _art = (url, bitmap);
        return bitmap;
    }

    private static Bitmap? Decode(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            return Bitmap.DecodeToWidth(stream, 720);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or NotSupportedException)
        {
            return null;
        }
    }
}
