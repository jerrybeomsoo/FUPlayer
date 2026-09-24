using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FUPlayer.App.Services;
using FUPlayer.Core.Audio;
using FUPlayer.Core.Capture;
using FUPlayer.Core.Engine;

namespace FUPlayer.App.ViewModels;

/// <summary>One application in the picker.</summary>
public sealed partial class CaptureTargetViewModel(CaptureTarget target) : ObservableObject
{
    [ObservableProperty]
    private bool _isPlaying = target.IsPlaying;

    [ObservableProperty]
    private bool _isCaptured;

    public int ProcessId { get; } = target.ProcessId;

    public string Name { get; } = target.Label;

    public string ProcessName { get; } = target.ProcessName;

    public string Detail => $"pid {ProcessId}";

    public string State => IsCaptured ? "CAPTURING" : IsPlaying ? "PLAYING" : "IDLE";

    partial void OnIsPlayingChanged(bool value) => OnPropertyChanged(nameof(State));

    partial void OnIsCapturedChanged(bool value) => OnPropertyChanged(nameof(State));
}

/// <summary>
/// Picks an application to take audio from. The chosen one becomes the player's source and runs
/// through the same chain as a file, out to whatever output device is selected.
/// </summary>
public sealed partial class LiveInputViewModel : ObservableObject
{
    private readonly PlayerServices _services;
    private readonly DispatcherTimer _timer;

    [ObservableProperty]
    private CaptureTargetViewModel? _selected;

    [ObservableProperty]
    private int _capturedProcessId;

    [ObservableProperty]
    private string? _captureNote;

    public LiveInputViewModel(PlayerServices services)
    {
        _services = services;
        IsSupported = services.Capture is { IsSupported: true };
        UnsupportedReason = services.Capture?.UnsupportedReason
            ?? "Capturing another application is a Windows feature.";

        Refresh();

        // The list changes as applications open and close sessions, so it is re-read while the page
        // is on screen rather than only when the user asks.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => Refresh();
    }

    public ObservableCollection<CaptureTargetViewModel> Applications { get; } = [];

    public bool IsSupported { get; }

    public string UnsupportedReason { get; }

    public bool IsUnsupported => !IsSupported;

    public bool IsEmpty => IsSupported && Applications.Count == 0;

    public bool IsCapturing => CapturedProcessId > 0;

    public bool HasCaptureNote => IsCapturing && !string.IsNullOrEmpty(CaptureNote);

    /// <summary>Mute the devices the captured application plays to, so it is heard once.</summary>
    public bool SilenceCapturedApplication
    {
        get => _services.Settings.Playback.SilenceCapturedApplication;
        set
        {
            if (_services.Settings.Playback.SilenceCapturedApplication == value)
            {
                return;
            }

            _services.Settings.Playback.SilenceCapturedApplication = value;
            _services.NotifySettingsChanged(applyToEngine: true);
            OnPropertyChanged();

            // The muting belongs to the capture, so a running one is opened again to take the change.
            if (IsCapturing)
            {
                _services.Engine.PlayCapture(CapturedProcessId);
            }
        }
    }

    public string CapturingLabel => Applications.FirstOrDefault(a => a.ProcessId == CapturedProcessId)?.Name
        ?? $"pid {CapturedProcessId}";

    /// <summary>
    /// What is arriving, and who chose it. A process loopback is taken after the session mixer, so the rate is the
    /// device's shared format rather than the application's: Windows has already converted whatever the application
    /// rendered. The player converts nothing further when its own output rate is the same.
    /// </summary>
    public string CaptureFormat => _captureFormat;

    /// <summary>True while a capture is running and its format is known.</summary>
    public bool HasCaptureFormat => IsCapturing && _captureFormat.Length > 0;

    /// <summary>Called by the shell when this page is shown or hidden, so the timer only runs when it matters.</summary>
    public void SetActive(bool active)
    {
        if (active)
        {
            Refresh();
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    /// <summary>
    /// Reflects what the engine reports, so the page still shows the truth after a device change, and shows a
    /// capture this page did not start: <c>FUPlayer.exe --capture &lt;pid&gt;</c> starts one before any page is open,
    /// and without this the page said nothing was being captured and hid the note that explains what was muted.
    /// </summary>
    public void Update(PlaybackStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        CaptureNote = status.CaptureNote;
        string format = status.IsCapture && status.Plan is { Source.IsValid: true } plan
            ? $"Arriving as {plan.Source.Describe()}: the shared format of the device Windows mixes this application to, "
                + "which it converts to before the player sees it. The player converts nothing more when its own output "
                + $"rate is the same one ({AudioRates.Format(plan.Output.SampleRate)} here). That format is the device's "
                + "Default Format in Windows' sound settings."
            : string.Empty;
        if (format != _captureFormat)
        {
            _captureFormat = format;
            OnPropertyChanged(nameof(CaptureFormat));
            OnPropertyChanged(nameof(HasCaptureFormat));
        }

        if (status.IsCapture && status.CaptureProcessId != 0)
        {
            if (CapturedProcessId != status.CaptureProcessId)
            {
                CapturedProcessId = status.CaptureProcessId;
            }

            foreach (CaptureTargetViewModel item in Applications)
            {
                item.IsCaptured = item.ProcessId == CapturedProcessId;
            }

            return;
        }

        if (!status.IsCapture && CapturedProcessId != 0)
        {
            CapturedProcessId = 0;
            foreach (CaptureTargetViewModel item in Applications)
            {
                item.IsCaptured = false;
            }
        }
    }

    [RelayCommand]
    public void Refresh()
    {
        if (!IsSupported)
        {
            return;
        }

        IReadOnlyList<CaptureTarget> targets = _services.Engine.CaptureTargets;
        var seen = new HashSet<int>();

        foreach (CaptureTarget target in targets)
        {
            seen.Add(target.ProcessId);
            CaptureTargetViewModel? existing = Applications.FirstOrDefault(a => a.ProcessId == target.ProcessId);
            if (existing is null)
            {
                Applications.Add(new CaptureTargetViewModel(target) { IsCaptured = target.ProcessId == CapturedProcessId });
            }
            else
            {
                existing.IsPlaying = target.IsPlaying;
            }
        }

        // An application that closed its session is gone, unless we are capturing it: a browser tab
        // that goes quiet drops its session and would otherwise vanish mid-listen.
        for (int i = Applications.Count - 1; i >= 0; i--)
        {
            if (!seen.Contains(Applications[i].ProcessId) && Applications[i].ProcessId != CapturedProcessId)
            {
                Applications.RemoveAt(i);
            }
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private void Start(CaptureTargetViewModel? target)
    {
        target ??= Selected;
        if (target is null || !IsSupported)
        {
            return;
        }

        _services.Engine.PlayCapture(target.ProcessId);
        CapturedProcessId = target.ProcessId;

        foreach (CaptureTargetViewModel item in Applications)
        {
            item.IsCaptured = item.ProcessId == target.ProcessId;
        }
    }

    private string _captureFormat = string.Empty;

    /// <summary>
    /// Windows' own per-application output setting. It is the way out of the one echo the player cannot mute
    /// away: a shared output on the very device the application plays to. Sending the application to another
    /// device there leaves the capture untouched, since a process loopback follows the application and not the
    /// device.
    /// </summary>
    [RelayCommand]
    private static void OpenWindowsAppVolume()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:apps-volume") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Nothing sensible to do; the page says what the setting is called.
        }
    }

    /// <summary>Windows' sound settings, where a device's shared Default Format is set.</summary>
    [RelayCommand]
    private static void OpenWindowsSoundSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Nothing sensible to do; the page says what the setting is called.
        }
    }

    [RelayCommand]
    private void Stop()
    {
        _services.Engine.Stop();
        CapturedProcessId = 0;
        foreach (CaptureTargetViewModel item in Applications)
        {
            item.IsCaptured = false;
        }
    }

    partial void OnCapturedProcessIdChanged(int value)
    {
        OnPropertyChanged(nameof(IsCapturing));
        OnPropertyChanged(nameof(CapturingLabel));
        OnPropertyChanged(nameof(HasCaptureNote));
        OnPropertyChanged(nameof(HasCaptureFormat));
    }

    partial void OnCaptureNoteChanged(string? value) => OnPropertyChanged(nameof(HasCaptureNote));
}
