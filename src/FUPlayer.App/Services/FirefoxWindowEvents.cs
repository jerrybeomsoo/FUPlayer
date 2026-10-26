using System.Runtime.InteropServices;

namespace FUPlayer.App.Services;

/// <summary>Receives native window creation/show events without loading code into Firefox.</summary>
internal sealed class FirefoxWindowEvents : IDisposable
{
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _started = new();
    private readonly Action<nint> _observe;
    private readonly Callback _callback;
    private uint _threadId;
    private int _disposed;

    internal FirefoxWindowEvents(Action<nint> observe)
    {
        _observe = observe;
        _callback = OnEvent; // Root the unmanaged callback until the hook thread exits.
        _thread = new Thread(Run) { IsBackground = true, Name = "Firefox startup windows" };
        _thread.Start();
        _started.Wait();
    }

    private void OnEvent(nint hook, uint kind, nint window, int objectId, int childId, uint eventThread, uint time)
    {
        if (Volatile.Read(ref _disposed) != 0 || window == 0 || objectId != 0 || childId != 0
            || kind is not (0x8000 or 0x8002 or 0x800c or 0x8018)) return;
        // Never unwind an exception through a native callback. Timer discovery remains a fallback.
        try { _observe(window); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    private void Run()
    {
        nint hook = 0;
        try
        {
            _threadId = GetCurrentThreadId();
            PeekMessage(out _, 0, 0, 0, 0); // Create the queue before Dispose can post WM_QUIT.
            hook = SetWinEventHook(0x8000, 0x8018, 0, _callback, 0, 0, 2); // OUTOFCONTEXT | SKIPOWNPROCESS
            _started.Set();
            if (hook == 0) return;
            while (GetMessage(out Message message, 0, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        finally
        {
            _started.Set();
            if (hook != 0) UnhookWinEvent(hook);
            GC.KeepAlive(_callback);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_threadId != 0) PostThreadMessage(_threadId, 0x12, 0, 0);
        // The callback never invokes the UI dispatcher or sends a synchronous message to Firefox.
        _thread.Join(TimeSpan.FromSeconds(1));
    }

    private delegate void Callback(nint hook, uint kind, nint window, int objectId, int childId, uint thread, uint time);
    [StructLayout(LayoutKind.Sequential)]
    private struct Message { public nint Window; public uint Kind; public nuint WParam; public nint LParam; public uint Time; public int X, Y; public uint Private; }
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint first, uint last, nint module, Callback callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll", EntryPoint = "GetMessageW")] private static extern int GetMessage(out Message message, nint window, uint first, uint last);
    [DllImport("user32.dll", EntryPoint = "PeekMessageW")] private static extern bool PeekMessage(out Message message, nint window, uint first, uint last, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] private static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW")] private static extern bool PostThreadMessage(uint thread, uint message, nuint wParam, nint lParam);
}
