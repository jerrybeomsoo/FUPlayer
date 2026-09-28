namespace FUPlayer.Core.Upnp;

/// <summary>What the player is doing with the stream a renderer asked it to play.</summary>
public enum RendererPlaybackState
{
    /// <summary>Not playing it: never started, stopped, ended, or something else is playing.</summary>
    Stopped,

    Playing,

    Paused,
}

/// <summary>The player's side of a stream, as the renderer sees it.</summary>
/// <param name="State">Whether the stream is playing.</param>
/// <param name="Position">How far into the stream playback is.</param>
/// <param name="Error">Why the stream stopped, when it stopped on an error since it was last asked to play.</param>
public readonly record struct RendererPlayback(RendererPlaybackState State, TimeSpan Position, string? Error);

/// <summary>The player's volume, in the terms a controller's volume slider needs.</summary>
/// <param name="Db">The volume now.</param>
/// <param name="MinimumDb">The bottom of the volume range.</param>
/// <param name="MaximumDb">The top of the volume range.</param>
/// <param name="Muted">Whether a controller muted the player.</param>
/// <param name="Fixed">The volume control is bypassed, so the player always plays at full level.</param>
public readonly record struct RendererVolume(double Db, double MinimumDb, double MaximumDb, bool Muted, bool Fixed);

/// <summary>
/// The player, seen from the UPnP renderer: what it does when a controller says play, pause or stop, and what it
/// reports back. The renderer handles the network and the protocol; the host owns the engine and the volume knob.
/// </summary>
public interface IUpnpRendererHost
{
    /// <summary>Starts playing a stream. Returns at once; the engine opens it on its own thread.</summary>
    /// <param name="uri">The stream's http address.</param>
    /// <param name="metadata">What the controller said about it, if anything.</param>
    /// <param name="controller">Who asked, for the player's pages: a name and an address.</param>
    void Play(string uri, StreamMetadata? metadata, string controller);

    void Pause();

    void Resume();

    void Stop();

    /// <summary>
    /// New metadata for the stream at <paramref name="uri"/>, which a controller streaming continuously sends as it
    /// moves on to the next track. Ignored unless that stream is the one playing.
    /// </summary>
    void UpdateMetadata(string uri, StreamMetadata? metadata);

    /// <summary>Whether the stream at <paramref name="uri"/> is what the player is playing, and how far it has got.</summary>
    RendererPlayback GetPlayback(string uri);

    RendererVolume GetVolume();

    /// <summary>Sets the volume, clamped to the player's range. Ignored while the volume control is bypassed.</summary>
    void SetVolumeDb(double db);

    void SetMute(bool mute);
}
