using FUPlayer.Core.Engine;
using FUPlayer.Core.Settings;

namespace FUPlayer.Core.Upnp;

/// <summary>
/// The renderer's host over a <see cref="PlaybackEngine"/>: a controller's play, pause and stop go to the engine, and
/// the engine's state and position go back.
/// </summary>
/// <remarks>
/// A controller only ever moves the stream it sent. Pausing or stopping when the player has since moved on to a file
/// from the queue would take the music away from somebody who chose it here, so those requests are ignored then.
/// </remarks>
public sealed class EngineRendererHost : IUpnpRendererHost, IDisposable
{
    private readonly PlaybackEngine _engine;
    private readonly Func<VolumeSettings> _volume;
    private readonly Action<double> _setVolume;
    private readonly Lock _gate = new();
    private string? _requestedUri;
    private string? _error;

    /// <param name="engine">The engine that plays the streams.</param>
    /// <param name="volume">The volume range and bypass in force.</param>
    /// <param name="setVolume">
    /// Sets the player's volume in decibels, already clamped to the range. An interface routes this through its volume
    /// knob so the knob moves and the setting is saved; without one, it can set the engine's volume directly.
    /// </param>
    public EngineRendererHost(PlaybackEngine engine, Func<VolumeSettings> volume, Action<double> setVolume)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        _volume = volume;
        _setVolume = setVolume;
        engine.ErrorOccurred += OnEngineError;
    }

    /// <summary>Raised when a controller mutes or unmutes the player.</summary>
    public event EventHandler? MuteChanged;

    public void Dispose() => _engine.ErrorOccurred -= OnEngineError;

    public void Play(string uri, StreamMetadata? metadata, string controller)
    {
        lock (_gate)
        {
            _requestedUri = uri;
            _error = null;
        }

        _engine.PlayNetworkStream(new NetworkStreamRequest(uri, metadata, controller));
    }

    public void Pause()
    {
        if (PlayingOurs())
        {
            _engine.Pause();
        }
    }

    public void Resume()
    {
        if (PlayingOurs())
        {
            _engine.Resume();
        }
    }

    public void Stop()
    {
        if (PlayingOurs())
        {
            _engine.Stop();
        }
    }

    public void UpdateMetadata(string uri, StreamMetadata? metadata) => _engine.UpdateNetworkMetadata(uri, metadata);

    public RendererPlayback GetPlayback(string uri)
    {
        PlaybackStatus status = _engine.GetStatus();
        string? error;
        lock (_gate)
        {
            error = uri == _requestedUri ? _error : null;
        }

        if (uri.Length == 0 || !status.IsStream || status.Stream?.Uri != uri)
        {
            return new RendererPlayback(RendererPlaybackState.Stopped, TimeSpan.Zero, error);
        }

        RendererPlaybackState state = status.State switch
        {
            EngineState.Playing => RendererPlaybackState.Playing,
            EngineState.Paused => RendererPlaybackState.Paused,
            _ => RendererPlaybackState.Stopped,
        };
        return new RendererPlayback(state, status.Position, error);
    }

    public RendererVolume GetVolume()
    {
        VolumeSettings volume = _volume();
        return new RendererVolume(_engine.VolumeDb, volume.MinimumDb, volume.MaximumDb, _engine.Muted, volume.IsBypassed);
    }

    public void SetVolumeDb(double db)
    {
        VolumeSettings volume = _volume();
        if (!volume.IsBypassed)
        {
            _setVolume(Math.Clamp(db, volume.MinimumDb, volume.MaximumDb));
        }
    }

    public void SetMute(bool mute)
    {
        _engine.Muted = mute;
        MuteChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool PlayingOurs()
    {
        PlaybackStatus status = _engine.GetStatus();
        string? requested;
        lock (_gate)
        {
            requested = _requestedUri;
        }

        return status.IsStream && requested is not null && status.Stream?.Uri == requested;
    }

    /// <summary>A stream that could not be opened is reported by the engine; the renderer passes it on as an error.</summary>
    private void OnEngineError(object? sender, string message)
    {
        lock (_gate)
        {
            if (_requestedUri is not null && message.StartsWith(PlaybackEngine.NetworkStreamErrorPrefix, StringComparison.Ordinal))
            {
                _error = message;
            }
        }
    }
}
