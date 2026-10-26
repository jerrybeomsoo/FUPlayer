namespace FUPlayer.Core.Streaming;

/// <summary>Holds the provider's clock while DSP opens, without losing a later user Pause or Stop.</summary>
public sealed class BrowserPlaybackPreparation
{
    private int _pauseVersion;
    private bool _alreadyPaused;
    public bool Preparing { get; private set; }
    public bool AwaitingPlay { get; private set; }
    public bool WantsPlay { get; private set; }

    public void Begin(int mediaVersion, bool alreadyPaused = false, bool wantsPlay = true)
    {
        _pauseVersion = mediaVersion;
        _alreadyPaused = alreadyPaused;
        Preparing = true;
        WantsPlay = wantsPlay;
        AwaitingPlay = false;
    }

    public void Toggle() => WantsPlay = !WantsPlay;

    /// <summary>Release only after both DSP and the provider's pause acknowledgement are ready.</summary>
    public bool Update(bool pipelineReady, bool? playing, int mediaVersion)
    {
        if (Preparing && pipelineReady && (_alreadyPaused || mediaVersion > _pauseVersion) && playing == false)
        {
            Preparing = false;
            AwaitingPlay = WantsPlay;
            return WantsPlay;
        }
        if (AwaitingPlay && playing == true) AwaitingPlay = false;
        return false;
    }

    public void Cancel() => Preparing = AwaitingPlay = WantsPlay = false;
}
