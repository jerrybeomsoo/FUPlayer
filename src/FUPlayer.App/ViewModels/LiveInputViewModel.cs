using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FUPlayer.App.Services;
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

    public string CapturingLabel => Applications.FirstOrDefault(a => a.ProcessId == CapturedProcessId)?.Name
        ?? $"pid {CapturedProcessId}";

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

    /// <summary>Reflects what the engine reports, so the page still shows the truth after a device change.</summary>
    public void Update(PlaybackStatus status)
    {
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
    }
}
