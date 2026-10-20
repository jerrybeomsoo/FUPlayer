using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FUPlayer.App.Services;
using FUPlayer.Core.Engine;
using FUPlayer.Core.Localization;
using FUPlayer.Core.Streaming;
using Microsoft.Win32;

namespace FUPlayer.App.ViewModels;

/// <summary>Hosts the dedicated Firefox profile and the signed direct-PCM add-on.</summary>
public sealed partial class StreamingViewModel : ObservableObject, IDisposable
{
    public sealed record Service(string Name, string Address) { public override string ToString() => Name; }
    private readonly PlayerServices _services;
    private readonly DispatcherTimer _timer;
    private BrowserAudioBridge? _audio;
    private string? _streamUri;
    private DateTime _opened;
    private int _discontinuity;
    private (int Width, int Height) _viewport;
    private bool _volumeInitialised;
    private bool _windowPresented;
    private string? _nativeManifest;
    private bool _rememberedAddon;
    private double? _pendingVolume, _lastVolume;
    private DateTime _volumeRequested;

    [ObservableProperty] private string _executable;
    [ObservableProperty] private string _address;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isReady;
    [ObservableProperty] private bool _isCapturing;
    [ObservableProperty] private bool _embedBrowser = true;
    [ObservableProperty] private string _status = Loc.T("Install the signed streaming add-on once. It will load automatically on later launches.");
    [ObservableProperty] private string? _addonManifest;
    [ObservableProperty] private string _audioReceived = string.Empty;
    [ObservableProperty] private string _audioDetails = string.Empty;
    [ObservableProperty] private string _hostDetails = string.Empty;
    [ObservableProperty] private string? _captureNote;
    [ObservableProperty] private Service? _selectedService;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PageTitle))] private bool _isAppleMusicPage;

    public StreamingViewModel(PlayerServices services)
    {
        _services = services;
        Browser = new FirefoxSession(services.Store.SettingsDirectory);
        _executable = string.IsNullOrWhiteSpace(services.Settings.Ui.FirefoxExecutable)
            ? FirefoxSession.FindExecutable() : services.Settings.Ui.FirefoxExecutable;
        _address = "https://music.apple.com/";
        _selectedService = Services[0];
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Poll();
    }

    public FirefoxSession Browser { get; }
    public string PageTitle => IsAppleMusicPage ? Loc.T("Apple Music") : Loc.T("Streaming");
    public bool IsSupported => OperatingSystem.IsWindows();
    public bool HasCaptureNote => !string.IsNullOrWhiteSpace(CaptureNote);
    public bool CanSkipTracks => _audio?.CanNext == true || _audio?.CanPrevious == true;
    public bool CanSeek => _audio?.CanSeek == true;
    public bool CanShuffle => _audio?.CanShuffle == true;
    public bool CanRepeat => _audio?.CanRepeat == true;
    public BrowserAudioBridge.BrowserTransport? Transport => _audio?.Transport;
    /// <summary>Mirrors the service's normal linear volume into the display; DSP gain remains unity.</summary>
    public event Action<double>? VolumeChanged;
    public string ProfileDirectory => Browser.ProfileDirectory;
    public IReadOnlyList<Service> Services { get; } =
    [
        new(Loc.T("Apple Music"), "https://music.apple.com/"),
    ];

    partial void OnSelectedServiceChanged(Service? value) { if (value is not null) Address = value.Address; }
    partial void OnCaptureNoteChanged(string? value) => OnPropertyChanged(nameof(HasCaptureNote));

    [RelayCommand]
    private void Open()
    {
        try
        {
            string settings = _services.Store.SettingsDirectory;
            bool installed = File.Exists(FirefoxAudioAddon.InstalledMarker(settings));
            Launch(temporary: false, installed ? null : FirefoxAudioAddon.EnsureBundledPackage(settings));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        { Status = Loc.F("The streaming add-on could not install: {0}", ex.Message); }
    }

    /// <summary>Firefox verifies the selected signed package and asks for installation once, outside the native host.</summary>
    public void InstallAddon(string path)
    {
        try
        {
            if (Browser.IsRunning) throw new InvalidOperationException(Loc.T("Close this browser before opening another service."));
            string package = FirefoxAudioAddon.StorePackage(_services.Store.SettingsDirectory, path);
            Launch(temporary: false, package);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        { Status = Loc.F("The streaming add-on could not install: {0}", ex.Message); }
    }

    [RelayCommand]
    private void OpenTemporaryAddon() => Launch(temporary: true, installAddon: null);

    private void Launch(bool temporary, string? installAddon)
    {
        try
        {
            if (Browser.IsRunning) throw new InvalidOperationException(Loc.T("Close this browser before opening another service."));
            _audio?.Dispose();
            FirefoxNativeMessaging.Revoke(_nativeManifest);
            _nativeManifest = null;
            _audio = new BrowserAudioBridge();
            _windowPresented = false;
            _rememberedAddon = false;
            if (temporary)
                AddonManifest = FirefoxAudioAddon.Prepare(_services.Store.SettingsDirectory, Address, _audio, Browser.WindowTitleMarker);
            else
            {
                AddonManifest = null;
                // Firefox consumes the XPI as an installation request and leaves about:blank, not a file-URL document.
                string startup = FirefoxLaunch.PersistentSetupAddress;
                _nativeManifest = FirefoxNativeMessaging.Prepare(_services.Store.SettingsDirectory, Environment.ProcessPath!,
                    new(_audio.SocketUri, Address, new Uri(Address).GetLeftPart(UriPartial.Authority), Browser.WindowTitleMarker, startup));
                using var key = Registry.CurrentUser.CreateSubKey(@"Software\Mozilla\NativeMessagingHosts\" + FirefoxNativeMessaging.HostName);
                key.SetValue(null, _nativeManifest, RegistryValueKind.String);
            }
            Browser.Open(Executable, Address, audioBridgeSetup: true, persistentAddon: !temporary, installAddon: installAddon);
            _services.Settings.Ui.FirefoxExecutable = Executable;
            _services.Settings.Ui.StreamingAddress = Address;
            _services.NotifySettingsChanged(applyToEngine: false);
            _opened = DateTime.UtcNow;
            IsRunning = true;
            Status = temporary ? Loc.T("Load Temporary Add-on in Firefox, then choose the manifest shown below. The service opens afterwards.")
                : installAddon is null ? Loc.T("Connecting the installed streaming add-on.")
                : Loc.T("Confirm the streaming add-on installation in Firefox. Later launches are automatic.");
            _timer.Start();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or Win32Exception or InvalidOperationException)
        { _audio?.Dispose(); _audio = null; FirefoxNativeMessaging.Revoke(_nativeManifest); Status = Loc.F("Firefox could not open: {0}", ex.Message); }
    }

    private void Poll()
    {
        // Native file pickers need Firefox's own top-level window during add-on setup.
        try { Browser.Update(EmbedBrowser && _audio?.IsConnected == true); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            EmbedBrowser = false;
            Status = Loc.F("Firefox is available in a separate window: {0}", ex.Message);
        }
        IsRunning = Browser.IsRunning;
        AudioReceived = _audio is { } bridge ? Loc.F("Direct input: {0:N0} PCM frames; queued {1:N0}; seeks {2:N0}.",
            bridge.ReceivedFrames, bridge.PendingFrames, bridge.DiscontinuityVersion) : string.Empty;
        var host = Browser.HostAvailable;
        HostDetails = Loc.F("Browser host: window {0}; container {1}; embedded {2}.",
            host.Window ? 1 : 0, host.Container ? 1 : 0, Browser.IsEmbedded ? 1 : 0);
        AudioDetails = _audio?.AudioStatus is { } audio
            ? Loc.F("Browser audio: {0}; media elements {1}, attached {2}.", audio.Context switch
            {
                "suspended" => Loc.T("Suspended. Click Play inside Firefox to enable audio"),
                "running" => Loc.T("Audio context running"),
                "closed" => Loc.T("Audio context closed"),
                "interrupted" => Loc.T("Audio context interrupted"),
                _ => Loc.T("Waiting for media playback")
            }, audio.Watched, audio.Captured)
            : Loc.T("Waiting for browser audio status.");
        bool ready = IsRunning && _audio?.IsConnected == true;
        if (ready && _audio!.IsPersistentAddon && !_rememberedAddon)
        {
            try { File.WriteAllText(FirefoxAudioAddon.InstalledMarker(_services.Store.SettingsDirectory), "0.2.0"); _rememberedAddon = true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { CaptureNote = ex.Message; }
        }
        if (ready && !IsReady)
        {
            Status = Loc.T("Direct audio bridge connected. Sign in and play in Firefox; audio enters FUPlayer before Windows mixing.");
            Listen();
        }
        IsReady = ready;
        if (ready && !_windowPresented && host.Window && (!EmbedBrowser || Browser.IsEmbedded)
            && _audio!.RequestWindowReady()) _windowPresented = true;
        if (ready && Browser.IsEmbedded && Browser.Viewport is { Width: > 0, Height: > 0 } viewport && viewport != _viewport
            && _audio!.RequestViewport(viewport.Width, viewport.Height)) _viewport = viewport;
        if (!IsRunning)
        {
            StopOwnedStream();
            _timer.Stop();
            Status = Loc.T("Firefox is closed. Its profile and sign-ins are kept for next time.");
        }
        else if (_audio?.Error is { } error) Status = Loc.F("Direct audio stopped: {0}", error);
        else if (!ready && DateTime.UtcNow - _opened > TimeSpan.FromSeconds(30))
            Status = Loc.T("Waiting for the direct audio add-on. Windows capture is not used as a fallback.");
    }

    /// <summary>Mirror the actual engine state, including Stop and switching to a file on another page.</summary>
    public void Update(PlaybackStatus status)
    {
        bool wasCapturing = IsCapturing;
        IsCapturing = status.IsStream && status.Stream?.Uri == _streamUri && _streamUri is not null;
        CaptureNote = _audio?.Error is { } error ? Loc.F("Direct audio stopped: {0}", error)
            : _audio?.ControlError is { } control ? Loc.F("Firefox control: {0}", control) : null;
        if (IsCapturing && _audio is { } bridge)
        {
            if (bridge.DiscontinuityVersion != _discontinuity)
            {
                _discontinuity = bridge.DiscontinuityVersion;
                _services.Engine.ResetBrowserStream(_streamUri!);
            }
            // Drain already-sent PCM before pausing. Otherwise its queue can block the later playing report.
            if (bridge.IsMediaPlaying == false && bridge.PendingFrames == 0 && status.State == EngineState.Playing)
                _services.Engine.Pause();
            else if (bridge.IsMediaPlaying == true && status.State == EngineState.Paused) _services.Engine.Resume();
            if (bridge.CanVolume && bridge.Transport.Volume is { } volume)
            {
                if (!wasCapturing) _lastVolume = null;
                if (!_volumeInitialised)
                {
                    _volumeInitialised = true;
                    // The page owns the only volume gain; opening mirrors its existing level.
                    VolumeChanged?.Invoke(volume);
                    _lastVolume = volume;
                }
                else if (_pendingVolume is not { } pending || Math.Abs(pending - volume) < 0.0001
                    || DateTime.UtcNow - _volumeRequested > TimeSpan.FromSeconds(2))
                {
                    _pendingVolume = null;
                    if (_lastVolume != volume) { _lastVolume = volume; VolumeChanged?.Invoke(volume); }
                }
            }
        }
    }

    /// <summary>Direct browser input owns transport actions without touching the local file queue.</summary>
    public bool Owns(PlaybackStatus status) => _streamUri is not null && status.Stream?.Uri == _streamUri;

    /// <summary>Let the provider pause before the engine stops reading, so the PCM queue cannot fill during pause.</summary>
    public bool TryPlayPause(PlaybackStatus status)
    {
        if (!Owns(status)) return false;
        _audio?.RequestControl(status.State == EngineState.Paused ? "play" : "pause");
        return true;
    }

    public bool TrySkip(PlaybackStatus status, bool next)
    {
        if (!Owns(status)) return false;
        if ((next && _audio?.CanNext == true) || (!next && _audio?.CanPrevious == true))
            _audio.RequestControl(next ? "nexttrack" : "previoustrack");
        return true;
    }

    /// <summary>Seeks the service, leaving the PCM decoder non-seekable.</summary>
    public bool TrySeek(PlaybackStatus status, double seconds)
    {
        if (!Owns(status)) return false;
        if (_audio is { CanSeek: true } bridge && double.IsFinite(seconds))
            bridge.RequestControl("seekto", Math.Clamp(seconds, 0, bridge.Transport.Duration));
        return true;
    }

    public bool TryShuffle(PlaybackStatus status)
    {
        if (!Owns(status)) return false;
        if (_audio is { CanShuffle: true } bridge) bridge.RequestControl("shuffle", bridge.Transport.Shuffle != true);
        return true;
    }

    public bool TryRepeat(PlaybackStatus status)
    {
        if (!Owns(status)) return false;
        if (_audio is { CanRepeat: true } bridge)
            bridge.RequestControl("repeat", bridge.Transport.Repeat switch { "off" => "all", "all" => "one", _ => "off" });
        return true;
    }

    /// <summary>Shares volume with this service only; browser acknowledgements do not echo back.</summary>
    public void SetVolume(double decibels)
    {
        if (IsCapturing && _audio?.CanVolume == true)
            SendVolume(decibels <= -60 ? 0 : Math.Clamp(Math.Pow(10, decibels / 20), 0, 1));
    }

    private void SendVolume(double volume)
    {
        if (_audio?.RequestControl("volume", volume) == true) { _pendingVolume = volume; _volumeRequested = DateTime.UtcNow; }
    }

    [RelayCommand]
    private void Listen()
    {
        if (_audio?.IsConnected != true || !IsSupported || _streamUri is not null) return;
        _streamUri = _audio.StreamUri;
        _volumeInitialised = false; _pendingVolume = _lastVolume = null;
        _discontinuity = _audio.DiscontinuityVersion;
        _viewport = default;
        StartEngineStream();
        Status = Loc.T("Direct audio bridge connected. Sign in and play in Firefox; audio enters FUPlayer before Windows mixing.");
    }

    private void StartEngineStream()
    {
        if (_audio is null || _streamUri is null) return;
        _services.Engine.PlayNetworkStream(new NetworkStreamRequest(_streamUri, new FUPlayer.Core.Upnp.StreamMetadata
        { Title = SelectedService?.Name ?? Loc.T("Firefox"), SampleRate = BrowserAudioBridge.SampleRate, BitsPerSample = 32, Channels = 2 }, Loc.T("Firefox"), DirectBrowserAudio: true)
        { OpenDecoder = _audio.OpenDecoder, ReadMetadata = () => _audio?.Metadata });
    }

    [RelayCommand]
    private void StopListening() => Close();

    private void StopOwnedStream()
    {
        if (_streamUri is not null) _services.Engine.StopNetworkStream(_streamUri);
        _streamUri = null;
        _audio?.Dispose(); _audio = null;
        FirefoxNativeMessaging.Revoke(_nativeManifest); _nativeManifest = null;
        IsCapturing = false;
    }

    [RelayCommand]
    private void Close()
    {
        Browser.Close();
        StopOwnedStream();
        Status = Loc.T("Closing Firefox. Complete any browser prompts to finish closing.");
    }

    [RelayCommand]
    private void OpenProfileFolder()
    {
        try
        {
            Directory.CreateDirectory(ProfileDirectory);
            Process.Start(new ProcessStartInfo(ProfileDirectory) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        { Status = Loc.F("The profile folder could not open: {0}", ex.Message); }
    }

    [RelayCommand]
    private void OpenAddonFolder()
    {
        if (AddonManifest is null) return;
        try { Process.Start(new ProcessStartInfo(Path.GetDirectoryName(AddonManifest)!) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        { Status = Loc.F("The profile folder could not open: {0}", ex.Message); }
    }

    public void Dispose()
    {
        _timer.Stop();
        Browser.Dispose();
        StopOwnedStream();
    }
}
