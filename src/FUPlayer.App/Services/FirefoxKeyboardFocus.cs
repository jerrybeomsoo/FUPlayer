using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FUPlayer.App.Services;

/// <summary>Hands native focus to the hosted browser after a user enters its HWND container.</summary>
internal sealed class FirefoxKeyboardFocus : IDisposable
{
    private const uint SetFocus = 0x7, MouseActivate = 0x21, ParentNotify = 0x210, Destroy = 0x82;
    private readonly SubclassCallback _callback;
    private readonly Action _request;
    private nint _host;

    internal FirefoxKeyboardFocus(nint host, Action request)
    {
        _request = request;
        _callback = OnMessage;
        if (!SetWindowSubclass(host, _callback, 1, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        _host = host;
    }

    internal static bool RequestsFocus(uint message, nuint wparam, nint lparam) => message switch
    {
        SetFocus => true,
        MouseActivate => IsPress((uint)((nuint)lparam >> 16) & 0xffff),
        ParentNotify => IsPress((uint)wparam & 0xffff),
        _ => false,
    };

    private static bool IsPress(uint message) => message is 0x201 or 0x204 or 0x207 or 0x20b or 0x246;

    private nint OnMessage(nint window, uint message, nuint wparam, nint lparam, nuint id, nuint data)
    {
        // Queue the handoff: Firefox can be synchronously notifying its parent during a mouse click.
        // Calling back into it here would make two UI threads wait on each other.
        if (RequestsFocus(message, wparam, lparam))
        {
            try { _request(); }
            catch (InvalidOperationException) { } // The UI dispatcher may already be shutting down.
        }
        if (message == Destroy)
        {
            RemoveWindowSubclass(window, _callback, 1);
            _host = 0;
        }
        return DefSubclassProc(window, message, wparam, lparam);
    }

    public void Dispose()
    {
        if (_host == 0) return;
        RemoveWindowSubclass(_host, _callback, 1);
        _host = 0;
        GC.KeepAlive(_callback);
    }

    private delegate nint SubclassCallback(nint window, uint message, nuint wparam, nint lparam, nuint id, nuint data);
    [DllImport("comctl32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint window, SubclassCallback callback, nuint id, nuint data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint window, SubclassCallback callback, nuint id);
    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint window, uint message, nuint wparam, nint lparam);
}
