namespace FUPlayer.App.Services;

/// <summary>Keeps Gecko's focus visuals in step with the embedded window's actual native focus.</summary>
internal sealed class FirefoxKeyboardActivation(Func<uint, nint, nint, bool> post)
{
    private bool _focused;
    private bool _incomplete;

    internal void SetFocused(bool focused)
    {
        if (focused == _focused && !_incomplete) return;
        // Gecko raises/lowers its DOM window only when WM_ACTIVATE precedes WM_*FOCUS.
        // A reparented child misses top-level activation notifications. Post both in order to its
        // own queue; do not synchronously enter its UI thread or send/replay any keyboard input.
        if (!post(0x6, focused ? 1 : 0, 0)) return;
        _incomplete = true;
        if (!post(focused ? 0x7u : 0x8u, 0, 0)) return;
        _focused = focused;
        _incomplete = false;
    }
}
