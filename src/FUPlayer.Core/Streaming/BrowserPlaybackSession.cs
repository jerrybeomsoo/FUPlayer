namespace FUPlayer.Core.Streaming;

/// <summary>Coordinates provider transport with actual DSP rebuild boundaries, independently of UI polling.</summary>
public sealed class BrowserPlaybackSession(BrowserAudioBridge bridge)
{
    private readonly Lock _gate = new();
    private readonly BrowserPlaybackPreparation _preparation = new();
    private bool _ready;
    private bool _stopped = true;
    private bool _passiveOpening, _passiveWantsPlay;

    /// <summary>Waits for real PCM before DSP preparation can interrupt the first browser activation.</summary>
    public bool TryStartPlayback()
    {
        lock (_gate)
        {
            if (bridge.ReceivedFrames > 0) return true;
            // Playing/advancing media time does not prove the separate Web Audio graph is running.
            // This also permits the first packet after Stop was pressed before any audio arrived.
            bridge.HoldAudio(false);
            if (bridge.IsMediaPlaying != true) bridge.RequestControl("play");
            return false;
        }
    }

    /// <summary>Protects preparation acknowledgements from being mistaken for a user transport pause.</summary>
    public bool BlocksEnginePause
    {
        get { lock (_gate) return _passiveOpening || _preparation.Preparing || _preparation.AwaitingPlay; }
    }

    /// <summary>Called by the engine before opening every replacement pipeline, including live rate changes.</summary>
    public void PreparingProcessing(bool wantsPlay)
    {
        lock (_gate)
        {
            // The first PCM proves the provider is starting normally. Hold incoming audio while DSP
            // opens, without interrupting that media/licence activation with an automatic Pause/Play.
            if (_passiveOpening || (_stopped && wantsPlay && bridge.ReceivedFrames > 0 && bridge.IsMediaPlaying == true))
            {
                if (!_passiveOpening) _passiveWantsPlay = wantsPlay;
                _passiveOpening = true; _stopped = false; _ready = false;
                bridge.HoldAudio(true);
                return;
            }
            // A newer settings snapshot must retain a user Pause made during the previous preparation.
            bool intent = _preparation.Preparing || _preparation.AwaitingPlay ? _preparation.WantsPlay : wantsPlay;
            _stopped = false;
            _ready = false;
            int version = bridge.MediaStateVersion;
            // A previously sent Play can still be in flight while the last report says Paused.
            // The new rebuild needs a fresh pause transition after that Play, not this stale report.
            bool paused = bridge.IsMediaPlaying == false && !_preparation.AwaitingPlay;
            bridge.HoldAudio(true);
            if (bridge.RequestControl("pause")) _preparation.Begin(version, paused, intent);
            else { _preparation.Cancel(); bridge.HoldAudio(false); }
        }
    }

    /// <summary>Latches completion even when a whole rebuild finishes between UI updates.</summary>
    public void ProcessingReady()
    {
        lock (_gate) { _ready = true; UpdateLocked(); }
    }

    /// <summary>Completes a late provider acknowledgement without using a sampled DSP-ready state.</summary>
    public void Update()
    {
        lock (_gate) UpdateLocked();
    }

    private void UpdateLocked()
    {
        if (_stopped) return;
        if (_passiveOpening)
        {
            if (_ready) { _passiveOpening = false; bridge.HoldAudio(false); }
            return;
        }
        if (_preparation.Update(_ready, bridge.IsMediaPlaying, bridge.MediaStateVersion))
        { bridge.HoldAudio(false); bridge.RequestControl("play"); }
        else if (!_preparation.Preparing && !_preparation.AwaitingPlay && !_preparation.WantsPlay)
            bridge.HoldAudio(false);
    }

    /// <summary>Retains a later user transport choice while preparation or its Play command is pending.</summary>
    public bool TryTogglePreparation()
    {
        lock (_gate)
        {
            if (_passiveOpening)
            {
                _passiveWantsPlay = !_passiveWantsPlay;
                bridge.RequestControl(_passiveWantsPlay ? "play" : "pause");
                return true;
            }
            if (_preparation.Preparing) { _preparation.Toggle(); return true; }
            if (!_preparation.AwaitingPlay) return false;
            _preparation.Cancel(); bridge.HoldAudio(false); bridge.RequestControl("pause");
            return true;
        }
    }

    /// <summary>Cancels any pending resume and holds PCM until the next explicit preparation.</summary>
    public void Stop()
    {
        lock (_gate) { _ready = false; _stopped = true; _passiveOpening = false; _preparation.Cancel(); bridge.HoldAudio(true); }
    }

    /// <summary>Prevents a failed output opening from leaving the provider playing into an unread queue.</summary>
    public void ProcessingStopped()
    {
        lock (_gate) { Stop(); bridge.RequestControl("pause"); }
    }
}
