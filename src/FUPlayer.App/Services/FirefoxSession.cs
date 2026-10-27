using System.ComponentModel;
using System.Diagnostics;
using FUPlayer.Core.Localization;
using FUPlayer.Core.Streaming;
using Microsoft.Win32;

namespace FUPlayer.App.Services;

/// <summary>Owns a single dedicated Firefox instance; its profile and logins survive closing the player.</summary>
public sealed class FirefoxSession(string settingsDirectory) : IDisposable
{
    private Process? _process;
    private FileStream? _profileLease;
    private FirefoxNative.Attachment? _attachment;
    private FirefoxKeyboardActivation? _keyboardActivation;
    private nint _window, _host;
    private bool _servicePopup, _minimizeSetup;
    private FirefoxStartupVisibility? _startupVisibility;
    private FirefoxWindowEvents? _startupEvents;
    private int _startupOwner;
    private long _startupAt;

    /// <summary>The browser's process, whose children supply the audio; zero before its window is found.</summary>
    public int CaptureProcessId { get; private set; }
    public bool IsRunning => _process is { HasExited: false };
    public bool IsEmbedded => _attachment is not null;
    /// <summary>A unique session label distinguishes this service window from other owned dialogs and page titles.</summary>
    public string WindowTitleMarker { get; } = $"FUPlayer Streaming {Guid.NewGuid():N} | ";
    /// <summary>Whether the service window and native player container are available for attachment.</summary>
    public (bool Window, bool Container) HostAvailable => (_window != 0 && FirefoxNative.IsWindow(_window), _host != 0);
    /// <summary>Available browser area in CSS pixels, independent of the host's screen position.</summary>
    public (int Width, int Height) Viewport => _window == 0 ? default : FirefoxNative.Viewport(_window, _host);
    public string ProfileDirectory => FirefoxLaunch.ProfileDirectory(settingsDirectory);

    /// <summary>Find Firefox without launching it or looking inside the user's Firefox profiles.</summary>
    public static string FindExecutable()
    {
        if (!OperatingSystem.IsWindows()) return string.Empty;
        foreach (RegistryKey hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using RegistryKey? key = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\firefox.exe");
            if (key?.GetValue(null) is string path && File.Exists(path)) return path;
        }
        foreach (string folder in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            string path = Path.Combine(folder, "Mozilla Firefox", "firefox.exe");
            if (File.Exists(path)) return path;
        }
        return string.Empty;
    }

    /// <summary>Starts a browser only on explicit request. Firefox itself manages DRM and authentication.</summary>
    public void Open(string executable, string address, bool audioBridgeSetup = false, bool persistentAddon = false, string? installAddon = null)
    {
        if (!OperatingSystem.IsWindows()) throw new NotSupportedException(Loc.T("Firefox hosting is available on Windows."));
        if (IsRunning) throw new InvalidOperationException(Loc.T("Close this browser before opening another service."));
        if (!File.Exists(executable) || !Path.GetFileName(executable).Equals("firefox.exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException(Loc.T("Choose an installed Firefox executable."));
        var info = FileVersionInfo.GetVersionInfo(executable);
        if (info.ProductName?.Contains("Firefox", StringComparison.OrdinalIgnoreCase) != true)
            throw new IOException(Loc.T("Choose an installed Firefox executable."));
        if (!FirefoxNative.IsTrustedExecutable(executable))
            throw new IOException(Loc.T("Firefox's executable signature could not be verified. Choose an official Firefox installation."));
        ProcessStartInfo start;
        try { start = FirefoxLaunch.StartInfo(executable, settingsDirectory, address, audioBridgeSetup, persistentAddon, installAddon); }
        catch (ArgumentException) { throw new IOException(Loc.T("Enter an http or https web address without embedded credentials.")); }
        ReleaseExited();
        Directory.CreateDirectory(ProfileDirectory);
        try
        {
            _profileLease = new FileStream(Path.Combine(ProfileDirectory, ".fuplayer-owner"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (audioBridgeSetup)
            {
                // Only this managed profile: suppress native speaker output after Web Audio's PCM branch.
                // The standard preference changes volume, not sandbox, DRM or add-on security.
                string preferences = Path.Combine(ProfileDirectory, "user.js");
                string text = File.Exists(preferences) ? File.ReadAllText(preferences) : string.Empty;
                const string setting = "user_pref(\"media.volume_scale\", \"0.0\");";
                if (!text.EndsWith(setting + "\n", StringComparison.Ordinal))
                    File.AppendAllText(preferences, "\n// FUPlayer direct audio: keep native browser output silent.\n" + setting + "\n");
            }
            _servicePopup = audioBridgeSetup;
            _minimizeSetup = persistentAddon && installAddon is null;
            if (_minimizeSetup)
            {
                _startupVisibility = new FirefoxStartupVisibility(WindowTitleMarker, FirefoxNative.SetCloaked);
                FirefoxStartupVisibility visibility = _startupVisibility;
                _startupAt = Stopwatch.GetTimestamp();
                _startupEvents = new FirefoxWindowEvents(window =>
                    FirefoxNative.ObserveStartup(Volatile.Read(ref _startupOwner), window, WindowTitleMarker, visibility));
            }
            _process = Process.Start(start) ?? throw new IOException(Loc.T("Firefox could not be started."));
            Volatile.Write(ref _startupOwner, _process.Id);
        }
        catch { EndStartup(reveal: true); _profileLease?.Dispose(); _profileLease = null; throw; }
    }

    /// <summary>Called on the UI thread. The native container follows Avalonia's visibility and DPI-aware layout.</summary>
    internal void SetHost(nint handle)
    {
        if (_host == handle) return;
        // Native host recreation must never return the browser to a visible desktop top-level window.
        if (_attachment is not null) FirefoxNative.SetCloaked(_window, true);
        Detach();
        _host = handle;
    }

    /// <summary>Discovers the owned window and keeps it fitted without touching unrelated Firefox windows.</summary>
    public void Update(bool embed)
    {
        if (!IsRunning) { ReleaseExited(); return; }
        if (_startupVisibility is not null && Stopwatch.GetElapsedTime(_startupAt).TotalSeconds > 30)
            EndStartup(reveal: true); // Expose unexpected first-run/permission prompts instead of trapping them.
        if (_window == 0 || !FirefoxNative.IsWindow(_window))
        {
            if (_startupVisibility is { } visibility)
                foreach (nint window in FirefoxNative.FindWindows(_process!.Id, visibleOnly: false))
                    FirefoxNative.ObserveStartup(_process.Id, window, WindowTitleMarker, visibility);
            if (_minimizeSetup) FirefoxNative.ParkSetup(_process!.Id, WindowTitleMarker);
            _attachment = null;
            _keyboardActivation = null;
            _window = FirefoxNative.FindWindows(_process!.Id, _servicePopup ? WindowTitleMarker : null, visibleOnly: !_servicePopup).FirstOrDefault();
            CaptureProcessId = _window == 0 ? 0 : FirefoxNative.ProcessId(_window);
        }
        if (_window == 0) return;
        if (!embed) { Detach(); PresentService(); }
        else if (_host != 0 && _attachment is null)
        {
            _attachment = FirefoxNative.Attach(_window, _host);
            nint attached = _window;
            _keyboardActivation = new((message, wparam, lparam) => FirefoxNative.PostActivation(attached, message, wparam, lparam));
        }
        if (_attachment is not null) { FirefoxNative.Resize(_window, _host); PresentService(); UpdateKeyboardActivation(); }
    }

    private void PresentService()
    {
        _startupEvents?.Dispose(); _startupEvents = null;
        _startupVisibility?.Present(_window);
        _startupVisibility = null;
        FirefoxNative.ShowService(_window);
    }

    /// <summary>User-initiated focus only; never called by the playback or browser status timer.</summary>
    internal void FocusBrowser()
    {
        if (_attachment is not null && _host != 0) FirefoxNative.FocusBrowser(_window, _host);
        UpdateKeyboardActivation();
    }

    private void UpdateKeyboardActivation()
    {
        // Observation only: status updates never call SetFocus or change the foreground window.
        _keyboardActivation?.SetFocused(_attachment is not null && FirefoxNative.BrowserHasFocus(_window, _host));
    }

    private void EndStartup(bool reveal)
    {
        Volatile.Write(ref _startupOwner, 0);
        _startupEvents?.Dispose(); _startupEvents = null;
        if (reveal) _startupVisibility?.RevealAll();
        _startupVisibility = null;
    }

    /// <summary>Close normally, so Firefox can save the profile and show its own pending-dialog prompts.</summary>
    public void Close()
    {
        // The popup can exist before the first hosting tick, or beside a setup/prompt window.
        // Close all owned browser windows, rather than relying on the last successfully hosted one.
        if (IsRunning)
            foreach (nint window in FirefoxNative.FindWindows(_process!.Id, visibleOnly: false))
                FirefoxNative.Close(window);
        if (_window != 0) FirefoxNative.Close(_window);
        EndStartup(reveal: true);
    }

    private void Detach()
    {
        if (_attachment is not null) { FirefoxNative.Detach(_attachment); _attachment = null; }
        _keyboardActivation = null; // A standalone Firefox window owns its normal activation again.
    }

    private void ReleaseExited()
    {
        EndStartup(reveal: true);
        Detach();
        _window = 0; CaptureProcessId = 0;
        _process?.Dispose(); _process = null;
        _profileLease?.Dispose(); _profileLease = null;
    }

    public void Dispose()
    {
        // Detach first: destroying the native host must not destroy a cross-process browser window under a pending dialog.
        Detach();
        Close();
        _process?.Dispose(); _process = null;
        _profileLease?.Dispose(); _profileLease = null;
    }
}
