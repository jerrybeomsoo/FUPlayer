using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FUPlayer.App.Services;
using FUPlayer.Core.Engine;
using FUPlayer.Core.Settings;
using FUPlayer.Core.Upnp;

namespace FUPlayer.App.ViewModels;

/// <summary>
/// The player as a UPnP renderer that foobar2000 and other controllers play to: whether it offers itself, under which
/// name and to whom, what a controller is sending it, and foobar2000's side of the arrangement.
/// </summary>
public sealed partial class UpnpInputViewModel : ObservableObject
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(250);

    private readonly PlayerServices _services;
    private DateTime _nextRefreshUtc;
    private bool _active;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _hasStartError;

    [ObservableProperty]
    private string _statusBadge = "OFF";

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _addressesText = string.Empty;

    [ObservableProperty]
    private bool _isReceiving;

    [ObservableProperty]
    private string _transportBadge = "IDLE";

    [ObservableProperty]
    private string _receivingText = string.Empty;

    [ObservableProperty]
    private bool _hasStream;

    [ObservableProperty]
    private string _controllerText = "-";

    [ObservableProperty]
    private string _trackText = "-";

    [ObservableProperty]
    private string _streamText = "-";

    [ObservableProperty]
    private string _formatText = "-";

    [ObservableProperty]
    private string _bufferText = "-";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReceiveError))]
    private string? _receiveError;

    [ObservableProperty]
    private string _activityText = string.Empty;

    [ObservableProperty]
    private bool _hasActivity;

    [ObservableProperty]
    private bool _isFoobarInstalled;

    [ObservableProperty]
    private bool _canConfigureFoobar;

    [ObservableProperty]
    private bool _isFoobarConfigured;

    [ObservableProperty]
    private string _foobarText = string.Empty;

    [ObservableProperty]
    private string _foobarFormatText = string.Empty;

    [ObservableProperty]
    private string _foobarBitsText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFoobarNote))]
    private string? _foobarNote;

    private Foobar2000Status? _foobar;

    public UpnpInputViewModel(PlayerServices services)
    {
        _services = services;
        services.Upnp.Changed += (_, _) =>
        {
            if (_active)
            {
                Refresh(_services.Engine.GetStatus());
            }
        };
    }

    public bool Enabled
    {
        get => Settings.Enabled;
        set
        {
            if (Settings.Enabled != value)
            {
                Settings.Enabled = value;
                Save();
            }
        }
    }

    /// <summary>The name controllers list the player under; empty for the default one.</summary>
    public string FriendlyName
    {
        get => Settings.FriendlyName;
        set
        {
            value = value?.Trim() ?? string.Empty;
            if (Settings.FriendlyName != value)
            {
                Settings.FriendlyName = value;
                Save();
            }
        }
    }

    public string DefaultName => UpnpService.DefaultFriendlyName;

    /// <summary>The name in use: the setting, or the default when it is empty.</summary>
    public string EffectiveName => string.IsNullOrWhiteSpace(Settings.FriendlyName) ? DefaultName : Settings.FriendlyName;

    public bool AllowNetworkControl
    {
        get => Settings.AllowNetworkControl;
        set
        {
            if (Settings.AllowNetworkControl != value)
            {
                Settings.AllowNetworkControl = value;
                Save();
            }
        }
    }

    public bool FollowControllerVolume
    {
        get => Settings.FollowControllerVolume;
        set
        {
            if (Settings.FollowControllerVolume != value)
            {
                Settings.FollowControllerVolume = value;
                Save();
            }
        }
    }

    public bool HasReceiveError => ReceiveError is not null;

    public bool HasFoobarNote => FoobarNote is not null;

    private UpnpSettings Settings => _services.Settings.Upnp;

    /// <summary>Called by the shell when this page is shown or hidden.</summary>
    public void SetActive(bool active)
    {
        _active = active;
        if (active)
        {
            FoobarNote = null;
            RefreshFoobar();
            Refresh(_services.Engine.GetStatus());
        }
    }

    /// <summary>Called by the shell's status poll while the page is shown; refreshes a few times a second.</summary>
    public void Update(PlaybackStatus status)
    {
        DateTime now = DateTime.UtcNow;
        if (now >= _nextRefreshUtc)
        {
            _nextRefreshUtc = now + RefreshInterval;
            Refresh(status);
        }
    }

    [RelayCommand]
    private void Stop() => _services.Engine.Stop();

    [RelayCommand]
    private void OpenLog() => OpenWithShell(_services.Upnp.LogPath);

    [RelayCommand]
    private void OpenFoobar()
    {
        if (_foobar?.Executable is { } executable && OperatingSystem.IsWindows())
        {
            try
            {
                Foobar2000.Open(executable);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                FoobarNote = $"foobar2000 could not be started: {ex.Message}";
            }
        }
    }

    [RelayCommand]
    private void ConfigureFoobar()
    {
        if (_foobar?.ConfigPath is not { } path || !OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            Foobar2000.AddEntry(path);
            RefreshFoobar();
            FoobarNote = _foobar is { IsRunning: true }
                ? "Added. foobar2000 reads the list when it starts, so close it and open it again."
                : "Added. foobar2000 takes it up the next time it starts.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FoobarNote = $"The list could not be changed: {ex.Message}";
        }
    }

    private void Save()
    {
        // The service follows the settings on this call: it starts, stops or renames the renderer.
        _services.NotifySettingsChanged(applyToEngine: false);
        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(FriendlyName));
        OnPropertyChanged(nameof(EffectiveName));
        OnPropertyChanged(nameof(AllowNetworkControl));
        OnPropertyChanged(nameof(FollowControllerVolume));
        RefreshFoobarText();
        Refresh(_services.Engine.GetStatus());
    }

    private void Refresh(PlaybackStatus status)
    {
        UpnpService upnp = _services.Upnp;
        UpnpRendererSnapshot? snapshot = upnp.Snapshot;
        IsRunning = snapshot is not null;
        HasStartError = upnp.StartError is not null;
        StatusBadge = IsRunning ? "ON" : HasStartError ? "ERROR" : "OFF";
        StatusText = snapshot is not null
            ? $"Listed as \"{snapshot.FriendlyName}\" {(Settings.AllowNetworkControl ? "on the local network" : "on this computer only")}, port {upnp.Port}."
                + (snapshot.Subscriptions > 0 ? $" {Plural(snapshot.Subscriptions, "controller")} following it." : string.Empty)
            : upnp.StartError ?? "Off: controllers do not see the player.";
        AddressesText = snapshot is null ? string.Empty : "Description at " + string.Join("  ·  ", snapshot.DescriptionUrls);

        NetworkStreamStatus? stream = status.IsStream && status.State != EngineState.Stopped ? status.Stream : null;
        string state = snapshot?.TransportState ?? "NO_MEDIA_PRESENT";
        IsReceiving = stream is not null;
        TransportBadge = state switch
        {
            "PLAYING" => "PLAYING",
            "PAUSED_PLAYBACK" => "PAUSED",
            "TRANSITIONING" => "OPENING",
            "STOPPED" => "STOPPED",
            _ => "IDLE",
        };
        HasStream = stream is not null || snapshot?.Uri is not null;
        ReceivingText = stream is not null
            ? "Playing through the processing chain to the output device."
            : snapshot?.Uri is not null
                ? "A controller has chosen a stream and not asked for it to play, or has stopped it."
                : "Nothing yet. Choose this player as an output device in a controller, then play.";

        StreamMetadata? metadata = stream?.Metadata ?? snapshot?.Metadata;
        ControllerText = stream?.Controller ?? snapshot?.Controller ?? "-";
        TrackText = metadata is { HasTrack: true }
            ? string.Join("  ·  ", new[] { metadata.Title, metadata.Artist, metadata.Album }.Where(s => !string.IsNullOrWhiteSpace(s)))
            : "-";
        StreamText = stream?.Uri ?? snapshot?.Uri ?? "-";
        FormatText = stream is not null
            ? $"{stream.Container}{(string.IsNullOrEmpty(stream.ContentType) ? string.Empty : $" ({stream.ContentType})")}"
                + (status.Plan is { Source.IsValid: true } plan ? $"  ·  {plan.Source.Describe()}" : string.Empty)
            : "-";
        BufferText = stream is not null
            ? $"{stream.BufferedSeconds:0.0} s ahead" + (stream.StarvedReads > 0 ? $"  ·  waited on the network {Plural(stream.StarvedReads, "time")}" : string.Empty)
            : "-";
        ReceiveError = snapshot?.Error;

        IReadOnlyList<string> recent = snapshot?.Recent ?? [];
        HasActivity = recent.Count > 0;
        ActivityText = string.Join(Environment.NewLine, recent.Reverse().Take(14));
    }

    private void RefreshFoobar()
    {
        _foobar = OperatingSystem.IsWindows() ? Foobar2000.Find() : null;
        IsFoobarInstalled = _foobar is { IsInstalled: true };
        IsFoobarConfigured = _foobar is { IsConfigured: true };
        CanConfigureFoobar = _foobar is { ConfigPath: not null, IsConfigured: false };
        RefreshFoobarText();
    }

    private void RefreshFoobarText()
    {
        FoobarText = $"In foobar2000, open File › Preferences › Playback › Output and choose \"{EffectiveName}\" as the device. "
            + "What foobar2000 plays then comes here, and its volume and mute follow along when the setting above allows it. "
            + "Choose a local device there again to play through foobar2000 alone.";
        FoobarBitsText = "foobar2000 sends 16 bits to a renderer until told otherwise: under Preferences › Playback › Output › Devices, "
            + $"set Bits to 24 on the \"{EffectiveName}\" row. The format it arrives in shows under Receiving above.";
        FoobarFormatText = _foobar switch
        {
            { IsConfigured: true } =>
                "foobar2000 knows this player: it sends FLAC, and pauses the stream rather than stopping it.",
            { ConfigPath: not null } =>
                "Until foobar2000 knows this player it treats it like any renderer it has never heard of: it sends WAV, "
                + "and pausing stops the stream. Adding the player to foobar2000's list of renderers lets it send FLAC and pause.",
            _ =>
                "foobar2000 writes its list of renderers the first time its UPnP output runs. Choose this player as its output "
                + "device once, then come back here to add it to that list, so foobar2000 sends FLAC rather than WAV.",
        };
    }

    private static string Plural(long count, string noun) => count == 1 ? $"1 {noun}" : $"{count:N0} {noun}s";

    private static void OpenWithShell(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Nothing sensible to do; the page shows the same lines.
        }
    }
}
