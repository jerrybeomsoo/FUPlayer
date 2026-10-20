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

    internal static int ProcessId(nint window)
    { GetWindowThreadProcessId(window, out uint process); return (int)process; }

    internal static Attachment Attach(nint window, nint parent)
    {
        GetWindowRect(window, out Rectangle bounds);
        var attachment = new Attachment(window, GetParent(window), GetWindowLongPtr(window, Style), GetWindowLongPtr(window, ExtendedStyle), bounds);
        try
        {
            SetWindowLongPtr(window, Style, (nint)(((long)attachment.Style & ~(Popup | Frame)) | Child));
            SetWindowLongPtr(window, ExtendedStyle, (nint)((long)attachment.ExtendedStyle & ~0x40000L));
            SetParent(window, parent);
            if (GetParent(window) != parent) throw new Win32Exception(Marshal.GetLastWin32Error());
            SetWindowPos(window, 0, 0, 0, 0, 0, 0x27); // Frame changed, without activation or z-order changes.
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

    internal static void Close(nint window) { if (IsWindow(window)) PostMessage(window, 0x10, 0, 0); }

    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, nint parameter);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder name, int count);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetParent(nint window, nint parent);
    [DllImport("user32.dll")] private static extern nint GetParent(nint window);
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
