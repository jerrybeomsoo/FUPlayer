using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using FUPlayer.Core.Streaming;

namespace FUPlayer.App.Services;

/// <summary>Hosts only windows belonging to the Firefox instance started by this player.</summary>
internal static class FirefoxNative
{
    private const int Style = -16, ExtendedStyle = -20;
    private const long Child = 0x40000000, Popup = 0x80000000, Frame = 0x00cf0000;
    private delegate bool WindowCallback(nint window, nint parameter);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TrustFile
    {
        public uint Size;
        [MarshalAs(UnmanagedType.LPWStr)] public string Path;
        public nint Handle, Subject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size;
        public nint Policy, Client;
        public uint UserInterface, Revocation, Choice;
        public nint File;
        public uint StateAction;
        public nint State, Url;
        public uint Flags, Context;
    }

    internal static bool IsTrustedExecutable(string path)
    {
        var file = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = path };
        nint pointer = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        Marshal.StructureToPtr(file, pointer, false);
        try
        {
            var data = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), UserInterface = 2, Choice = 1, File = pointer, Flags = 0x1000 };
            var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
            return WinVerifyTrust(-1, ref action, ref data) == 0;
        }
        finally { Marshal.DestroyStructure<TrustFile>(pointer); Marshal.FreeHGlobal(pointer); }
    }

    internal sealed record Attachment(nint Window, nint Parent, nint Style, nint ExtendedStyle, Rectangle Bounds);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rectangle { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public nuint Heap;
        public uint ModuleId, Threads, ParentId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
    }

    internal static nint FindWindow(int owner, string? titleMarker = null) =>
        FindWindows(owner, titleMarker).FirstOrDefault();

    /// <summary>Accept native messaging only from this player's process tree, even if normal Firefox has the same add-on.</summary>
    internal static bool IsOwnedProcess(int owner, int process)
    {
        var parents = new Dictionary<uint, uint>();
        nint snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == -1) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), Name = string.Empty };
            if (Process32First(snapshot, ref entry))
                do { parents[entry.ProcessId] = entry.ParentId; } while (Process32Next(snapshot, ref entry));
        }
        finally { CloseHandle(snapshot); }
        uint current = (uint)process;
        var visited = new HashSet<uint>();
        while (visited.Add(current))
        {
            if (current == owner) return true;
            if (!parents.TryGetValue(current, out current)) break;
        }
        return false;
    }

    // Firefox's extension-created service popup is a dialog-class window, not its main-window class.
    // Keep the process-tree restriction for both classes, including windows awaiting a close prompt.
    internal static IReadOnlyList<nint> FindWindows(int owner, string? titleMarker = null, bool visibleOnly = true)
    {
        var parents = new Dictionary<uint, uint>();
        nint snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == -1) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), Name = string.Empty };
            if (Process32First(snapshot, ref entry))
                do { parents[entry.ProcessId] = entry.ParentId; } while (Process32Next(snapshot, ref entry));
        }
        finally { CloseHandle(snapshot); }

        bool Owned(uint process)
        {
            var visited = new HashSet<uint>();
            while (visited.Add(process))
            {
                if (process == owner) return true;
                if (!parents.TryGetValue(process, out process)) break;
            }
            return false;
        }

        var found = new List<nint>();
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint process);
            var name = new StringBuilder(128);
            GetClassName(window, name, name.Capacity);
            if (Owned(process) && (!visibleOnly || IsWindowVisible(window))
                && name.ToString() is "MozillaWindowClass" or "MozillaDialogClass")
            {
                if (titleMarker is not null)
                {
                    var title = new StringBuilder(1024);
                    GetWindowText(window, title, title.Capacity);
                    // Chromeless Firefox prepends its origin/extension identity before the requested title preface.
                    if (!FirefoxLaunch.WindowTitleMatches(title.ToString(), titleMarker)) return true;
                }
                found.Add(window);
            }
            return true;
        }, 0);
        return found;
    }

    /// <summary>Only the dedicated setup main window; never the service popup or an installation/authentication dialog.</summary>
    internal static void ParkSetup(int owner, string marker)
    {
        foreach (nint window in FindWindows(owner))
        {
            var name = new StringBuilder(128); GetClassName(window, name, name.Capacity);
            var title = new StringBuilder(1024); GetWindowText(window, title, title.Capacity);
            if (name.ToString() == "MozillaWindowClass" && !FirefoxLaunch.WindowTitleMatches(title.ToString(), marker))
            {
                // Keep the setup document running in a normal window, without a fullscreen flash or
                // the focus-dependent initialisation of a minimised Firefox. Never move a service/dialog.
                if (IsIconic(window)) ShowWindowAsync(window, 4);
                SetWindowPos(window, 0, -32000, -32000, 320, 240, 0x14);
            }
        }
    }

    internal static bool SetCloaked(nint window, bool cloaked)
    {
        if (!IsWindow(window)) return false;
        int value = cloaked ? 1 : 0;
        if (cloaked) DwmSetWindowAttribute(window, 3, ref value, sizeof(int)); // Disable startup/restore animation.
        return DwmSetWindowAttribute(window, 13, ref value, sizeof(int)) >= 0;
    }

    internal static void ObserveStartup(int owner, nint window, string marker, FirefoxStartupVisibility visibility)
    {
        if (owner == 0 || !IsWindow(window) || ((long)GetWindowLongPtr(window, Style) & Child) != 0) return;
        var name = new StringBuilder(128); GetClassName(window, name, name.Capacity);
        string className = name.ToString();
        if (className is not ("MozillaWindowClass" or "MozillaDialogClass")) return;
        GetWindowThreadProcessId(window, out uint process);
        if (!IsOwnedProcess(owner, (int)process)) return;
        var title = new StringBuilder(1024); GetWindowText(window, title, title.Capacity);
        if (visibility.Observe(window, true, true, className, title.ToString())
            && className == "MozillaWindowClass" && !FirefoxLaunch.WindowTitleMatches(title.ToString(), marker))
        {
            // Keep Gecko initialising in a normal composed window, without a desktop flash or focus.
            SetWindowPos(window, 0, -32000, -32000, 320, 240, 0x4014); // ASYNCWINDOWPOS, NOZORDER, NOACTIVATE
            if (!IsWindowVisible(window) || IsIconic(window)) ShowWindowAsync(window, 4);
        }
    }

    internal static void ShowService(nint window)
    {
        if (IsIconic(window) || !IsWindowVisible(window)) ShowWindowAsync(window, 4);
        SetCloaked(window, false);
    }

    internal static int ProcessId(nint window)
    { GetWindowThreadProcessId(window, out uint process); return (int)process; }

    internal static Attachment Attach(nint window, nint parent)
    {
        GetWindowRect(window, out Rectangle bounds);
        bool minimized = IsIconic(window);
        if (minimized)
        {
            var placement = new WindowPlacement { Length = (uint)Marshal.SizeOf<WindowPlacement>() };
            if (GetWindowPlacement(window, ref placement)) bounds = placement.NormalPosition;
        }
        var attachment = new Attachment(window, GetParent(window), (nint)((long)GetWindowLongPtr(window, Style) & ~0x20000000L), GetWindowLongPtr(window, ExtendedStyle), bounds);
        try
        {
            SetWindowLongPtr(window, Style, (nint)(((long)attachment.Style & ~(Popup | Frame)) | Child));
            SetWindowLongPtr(window, ExtendedStyle, (nint)((long)attachment.ExtendedStyle & ~0x40000L));
            SetParent(window, parent);
            if (GetParent(window) != parent) throw new Win32Exception(Marshal.GetLastWin32Error());
            SetWindowPos(window, 0, 0, 0, 0, 0, 0x27); // Frame changed, without activation or z-order changes.
            if (minimized) ShowWindowAsync(window, 4);
            Resize(window, parent);
            return attachment;
        }
        catch { Detach(attachment); throw; }
    }

    internal static void Resize(nint window, nint parent)
    {
        if (GetClientRect(parent, out Rectangle bounds) && bounds.Right > 1 && bounds.Bottom > 1)
        {
            GetWindowRect(window, out Rectangle current);
            GetClientRect(window, out Rectangle client);
            if (current.Right - current.Left != bounds.Right || current.Bottom - current.Top != bounds.Bottom
                || client.Right != bounds.Right || client.Bottom != bounds.Bottom)
                // Recalculate the child client area as well as its outer bounds after fullscreen reparenting.
                SetWindowPos(window, 0, 0, 0, bounds.Right, bounds.Bottom, 0x34);
        }
    }

    internal static void FocusBrowser(nint window, nint parent)
    {
        if (!IsInteractiveHost(window, parent)) return;
        // Do not steal focus from another program, a player control, or a separate Firefox dialog.
        nint root = GetAncestor(parent, 2);
        if (root == 0 || GetForegroundWindow() != root) return;
        nint focus = GetFocus();
        if (focus == window || IsChild(window, focus)) return;
        // SetParent joins the native input queues. Use Windows focus rather than forwarding text/keys,
        // so Firefox retains its normal editing, shortcuts and IME composition handling.
        SetFocus(window);
    }

    internal static bool BrowserHasFocus(nint window, nint parent)
    {
        if (!IsInteractiveHost(window, parent)) return false;
        nint root = GetAncestor(parent, 2);
        if (root == 0 || GetForegroundWindow() != root) return false;
        nint focus = GetFocus();
        return focus == window || IsChild(window, focus);
    }

    private static bool IsInteractiveHost(nint window, nint parent) => parent != 0 && IsWindow(window)
        && GetParent(window) == parent && IsWindowVisible(window) && IsWindowEnabled(window);

    internal static bool PostActivation(nint window, uint message, nint wparam, nint lparam) =>
        IsWindow(window) && PostMessage(window, message, wparam, lparam);

    internal static (int Width, int Height) Viewport(nint window, nint parent)
    {
        if (parent == 0 || !GetClientRect(parent, out Rectangle bounds)) return default;
        double scale = Math.Max(96, GetDpiForWindow(window)) / 96.0;
        return ((int)(bounds.Right / scale), (int)(bounds.Bottom / scale));
    }

    internal static void Detach(Attachment attachment)
    {
        if (!IsWindow(attachment.Window)) return;
        SetParent(attachment.Window, attachment.Parent);
        SetWindowLongPtr(attachment.Window, Style, attachment.Style);
        SetWindowLongPtr(attachment.Window, ExtendedStyle, attachment.ExtendedStyle);
        Rectangle r = attachment.Bounds;
        SetWindowPos(attachment.Window, 0, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, 0x64);
    }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint window);
    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement { public uint Length, Flags, ShowCommand; public Point MinPosition, MaxPosition; public Rectangle NormalPosition; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowPlacement(nint window, ref WindowPlacement placement);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindowAsync(nint window, int command);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);

    internal static void Close(nint window) { if (IsWindow(window)) PostMessage(window, 0x10, 0, 0); }

    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, nint parameter);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowEnabled(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder name, int count);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetParent(nint window, nint parent);
    [DllImport("user32.dll")] private static extern nint GetParent(nint window);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetFocus();
    [DllImport("user32.dll")] private static extern nint SetFocus(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsChild(nint parent, nint child);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out Rectangle rectangle);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rectangle rectangle);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nint wparam, nint lparam);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint CreateToolhelp32Snapshot(uint flags, uint process);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode)] private static extern bool Process32First(nint snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode)] private static extern bool Process32Next(nint snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern int WinVerifyTrust(nint window, ref Guid action, ref TrustData data);
}
