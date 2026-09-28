using System.Runtime.Versioning;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace FUPlayer.App.Services;

/// <summary>What another application says it is playing.</summary>
/// <param name="AppId">The session's application id: an executable name such as "foobar2000.exe", or a packaged app's id.</param>
/// <param name="Title">The track's title.</param>
/// <param name="Artist">Its artist.</param>
/// <param name="Album">Its album.</param>
/// <param name="Cover">The cover as the application supplied it, an image file's bytes, or null.</param>
/// <param name="IsPlaying">Whether the application says it is playing rather than paused or stopped.</param>
public sealed record MediaSessionInfo(string AppId, string? Title, string? Artist, string? Album, byte[]? Cover, bool IsPlaying)
{
    public bool HasTrack => !string.IsNullOrWhiteSpace(Title);
}

/// <summary>
/// Reads the media sessions Windows collects from every player that publishes one, the same ones its volume flyout and
/// lock screen show: foobar2000, Spotify, the browsers and most others. Now playing takes the title, artist and cover
/// from here for sources that do not carry them, a captured application and foobar2000's UPnP stream among them.
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
public sealed class MediaSessions
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private (string Key, byte[]? Cover) _lastCover;

    /// <summary>
    /// The session of the application that matches <paramref name="matches"/>, or null when it has none or Windows has
    /// no session manager to ask.
    /// </summary>
    /// <param name="matches">Given a session's application id, whether it is the one wanted.</param>
    public async Task<MediaSessionInfo?> FindAsync(Func<string, bool> matches)
    {
        try
        {
            _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            foreach (GlobalSystemMediaTransportControlsSession session in _manager.GetSessions())
            {
                string app = session.SourceAppUserModelId ?? string.Empty;
                if (!matches(app))
                {
                    continue;
                }

                GlobalSystemMediaTransportControlsSessionMediaProperties properties = await session.TryGetMediaPropertiesAsync();
                bool playing = session.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                string key = $"{app}|{properties.Title}|{properties.Artist}|{properties.AlbumTitle}";
                byte[]? cover = key == _lastCover.Key ? _lastCover.Cover : await ReadCoverAsync(properties.Thumbnail);
                _lastCover = (key, cover);
                return new MediaSessionInfo(
                    app,
                    Clean(properties.Title),
                    Clean(properties.Artist) ?? Clean(properties.AlbumArtist),
                    Clean(properties.AlbumTitle),
                    cover,
                    playing);
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            // No session manager (a server edition, or a policy that turns it off): the sources keep their own names.
            _manager = null;
        }

        return null;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static async Task<byte[]?> ReadCoverAsync(IRandomAccessStreamReference? thumbnail)
    {
        if (thumbnail is null)
        {
            return null;
        }

        try
        {
            using IRandomAccessStreamWithContentType stream = await thumbnail.OpenReadAsync();
            if (stream.Size is 0 or > 16 * 1024 * 1024)
            {
                return null;
            }

            using Stream reader = stream.AsStreamForRead();
            using var copy = new MemoryStream();
            await reader.CopyToAsync(copy);
            return copy.ToArray();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
