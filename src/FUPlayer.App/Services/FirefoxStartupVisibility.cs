using FUPlayer.Core.Streaming;

namespace FUPlayer.App.Services;

/// <summary>Keeps owned bootstrap windows composed but invisible until the service has a host.</summary>
internal sealed class FirefoxStartupVisibility(string marker, Func<nint, bool, bool> setCloaked)
{
    private readonly Lock _gate = new();
    private readonly HashSet<nint> _concealed = [];
    private bool _completed;

    internal bool Observe(nint window, bool owned, bool topLevel, string className, string title)
    {
        lock (_gate)
        {
            if (_completed || !owned || !topLevel || window == 0) return false;
            // Real dialogs remain accessible. The add-on marks its service popup explicitly.
            if (className != "MozillaWindowClass"
                && !(className == "MozillaDialogClass" && FirefoxLaunch.WindowTitleMatches(title, marker))) return false;
            if (!setCloaked(window, true)) return false;
            _concealed.Add(window);
            return true;
        }
    }

    internal void Present(nint service)
    {
        lock (_gate)
        {
            _completed = true;
            if (_concealed.Remove(service)) setCloaked(service, false);
        }
    }

    /// <summary>On timeout or closing, leave no inaccessible setup/prompt behind.</summary>
    internal void RevealAll()
    {
        lock (_gate)
        {
            _completed = true;
            foreach (nint window in _concealed) setCloaked(window, false);
            _concealed.Clear();
        }
    }
}
