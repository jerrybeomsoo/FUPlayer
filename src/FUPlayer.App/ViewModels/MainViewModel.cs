using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FUPlayer.App.Controls;
using FUPlayer.App.Services;
using FUPlayer.Core.Engine;
using FUPlayer.Core.Metadata;
using FUPlayer.Core.Playlists;
using FUPlayer.Core.Settings;

namespace FUPlayer.App.ViewModels;

/// <summary>An entry of the navigation rail.</summary>
public sealed partial class NavItem(string key, string title, Geometry icon, ObservableObject page) : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    public string Key { get; } = key;

    public string Title { get; } = title;

    public Geometry Icon { get; } = icon;

    public ObservableObject Page { get; } = page;
}

/// <summary>The application shell: navigation, transport bar, signal-path summary and the status poll.</summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan LimiterHold = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan ErrorHold = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan LoadSampleLength = TimeSpan.FromSeconds(1);

    private readonly PlayerServices _services;
    private readonly DispatcherTimer _timer;
    private readonly bool _ready;
    private (Guid? Id, bool TestTone)? _current;
    private long _lastLimiterEvents;
    private DateTime _limiterUntilUtc;
    private DateTime _errorUntilUtc;
    private bool _metersIdle;
    private readonly Queue<(double Load, bool Dropout)> _loadHistory = new();
    private DateTime _loadSampleStartUtc;
    private double _loadSampleMaximum;
    private long _loadSampleUnderruns = -1;
    private bool _overloadDismissed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOverloadWarning))]
    private string? _overloadMessage;

    [ObservableProperty]
    private NavItem _selectedNav = null!;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private string _trackTitle = "Nothing playing";

    [ObservableProperty]
    private string _trackSubtitle = "Pick an album in the library or drop files anywhere";

    [ObservableProperty]
    private Bitmap? _cover;

    [ObservableProperty]
    private double _positionSeconds;

    [ObservableProperty]
    private double _durationSeconds;

    [ObservableProperty]
    private bool _canSeek;

    [ObservableProperty]
    private string _positionText = "0:00";

    [ObservableProperty]
    private string _durationText = "0:00";

    [ObservableProperty]
    private double _volumeDb;

    [ObservableProperty]
    private double _volumeMinimum;

    [ObservableProperty]
    private double _volumeMaximum;

    [ObservableProperty]
    private bool _isVolumeFixed;

    [ObservableProperty]
    private bool _isLimiting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RepeatIcon), nameof(IsRepeatOn))]
    private RepeatMode _repeat;

    [ObservableProperty]
    private bool _shuffle;

    [ObservableProperty]
    private bool _hasPlan;

    [ObservableProperty]
    private string _sourceText = "-";

    [ObservableProperty]
    private string _filterText = "-";

    [ObservableProperty]
    private string _quantizerText = "-";

    [ObservableProperty]
    private string _outputText = "-";

    [ObservableProperty]
    private bool _isDsdOutput;

    [ObservableProperty]
    private string _deviceText = "Idle";

    [ObservableProperty]
    private double _dspLoad;

    [ObservableProperty]
    private string _dspLoadText = "-";

    [ObservableProperty]
    private bool _isDspOverloaded;

    [ObservableProperty]
    private double _bufferFill;

    [ObservableProperty]
    private string _bufferText = "-";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    public MainViewModel(PlayerServices services, IDialogService dialogs)
    {
        _services = services;
        NowPlaying = new NowPlayingViewModel(services);
        Queue = new QueueViewModel(services, dialogs);
        Library = new LibraryViewModel(services, dialogs, Queue);
        Dsp = new DspStudioViewModel(services);
        Output = new OutputViewModel(services, dialogs);
        Calibration = new CalibrationViewModel(services);
        LiveInput = new LiveInputViewModel(services);
        General = new SettingsViewModel(services);

        NowPlayingNav = new NavItem("NowPlaying", "Now playing", Icons.NowPlaying, NowPlaying);
        QueueNav = new NavItem("Queue", "Queue", Icons.Queue, Queue);
        LibraryNav = new NavItem("Library", "Library", Icons.Library, Library);
        DspNav = new NavItem("Dsp", "DSP studio", Icons.Dsp, Dsp);
        OutputNav = new NavItem("Output", "Output", Icons.Output, Output);
        LiveInputNav = new NavItem("LiveInput", "Live input", Icons.LiveInput, LiveInput);
        CalibrationNav = new NavItem("Calibration", "Calibration", Icons.Calibration, Calibration);
        SettingsNav = new NavItem("Settings", "Settings", Icons.Settings, General);
        Navigation = [NowPlayingNav, QueueNav, LibraryNav, LiveInputNav, DspNav, OutputNav, CalibrationNav, SettingsNav];
        SelectedNav = Navigation.FirstOrDefault(n => n.Key == services.Settings.Ui.LastPage) ?? NowPlayingNav;

        PlaybackSettings playback = services.Settings.Playback;
        Repeat = playback.Repeat;
        Shuffle = playback.Shuffle;
        services.Engine.SetQueueOptions(Repeat, Shuffle);
        RefreshVolumeRange();
        VolumeDb = services.Engine.VolumeDb;

        services.SettingsChanged += (_, _) => RefreshVolumeRange();
        services.Engine.ErrorOccurred += (_, message) => Dispatcher.UIThread.Post(() => ShowError(message));

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        _ready = true;
    }

    public NowPlayingViewModel NowPlaying { get; }

    public QueueViewModel Queue { get; }

    public LibraryViewModel Library { get; }

    public DspStudioViewModel Dsp { get; }

    public OutputViewModel Output { get; }

    public CalibrationViewModel Calibration { get; }

    public LiveInputViewModel LiveInput { get; }

    public SettingsViewModel General { get; }

    public NavItem NowPlayingNav { get; }

    public NavItem QueueNav { get; }

    public NavItem LibraryNav { get; }

    public NavItem DspNav { get; }

    public NavItem OutputNav { get; }

    public NavItem LiveInputNav { get; }

    public NavItem CalibrationNav { get; }

    public NavItem SettingsNav { get; }

    public IReadOnlyList<NavItem> Navigation { get; }

    public Geometry RepeatIcon => Repeat == RepeatMode.One ? Icons.RepeatOne : Icons.Repeat;

    public bool IsRepeatOn => Repeat != RepeatMode.Off;

    public bool HasError => ErrorMessage is not null;

    public bool HasOverloadWarning => OverloadMessage is not null;

    public void SetWindowHandle(IntPtr handle) => _services.Engine.WindowHandle = handle;

    public void OpenPaths(IReadOnlyList<string> paths) => _ = Queue.AddPathsAsync(paths, playWhenIdle: true);

    public void StepVolume(double deltaDb)
    {
        if (!IsVolumeFixed)
        {
            VolumeDb = Math.Clamp(Math.Round((VolumeDb + deltaDb) * 2) / 2, VolumeMinimum, VolumeMaximum);
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        Library.Dispose();
    }

    partial void OnSelectedNavChanged(NavItem? oldValue, NavItem newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }

        newValue.IsSelected = true;
        if (newValue == OutputNav)
        {
            // Probing a device opens it, so it waits until the page is on screen.
            Output.EnsureCapabilities();
        }

        // Enumerating audio sessions is cheap but not free, so it only runs while the page is shown.
        LiveInput.SetActive(newValue == LiveInputNav);

        if (newValue == DspNav)
        {
            // A model can be installed while the player is open, so the folder is read again on the
            // way in rather than only when a switch is touched.
            Dsp.RefreshModelStatus();
        }

        if (_ready)
        {
            _services.Settings.Ui.LastPage = newValue.Key;
            _services.NotifySettingsChanged(applyToEngine: false);
        }
    }

    partial void OnVolumeDbChanged(double value)
    {
        if (!_ready)
        {
            return;
        }

        _services.Engine.VolumeDb = value;
        _services.Settings.Volume.VolumeDb = value;
        _services.NotifySettingsChanged(applyToEngine: false);
    }

    [RelayCommand]
    private void PlayPause()
    {
        if (_services.Engine.State == EngineState.Stopped && _services.Engine.Queue.Count == 0)
        {
            SelectedNav = LibraryNav;
            return;
        }

        _services.Engine.PlayPause();
    }

    [RelayCommand]
    private void Stop() => _services.Engine.Stop();

    [RelayCommand]
    private void Next() => _services.Engine.Next();

    [RelayCommand]
    private void Previous()
    {
        if (CanSeek && PositionSeconds > 3)
        {
            _services.Engine.Seek(TimeSpan.Zero);
        }
        else
        {
            _services.Engine.Previous();
        }
    }

    [RelayCommand]
    private void Seek(double seconds) => _services.Engine.Seek(TimeSpan.FromSeconds(Math.Max(0, seconds)));

    [RelayCommand]
    private void CycleRepeat()
    {
        Repeat = Repeat switch
        {
            RepeatMode.Off => RepeatMode.All,
            RepeatMode.All => RepeatMode.One,
            _ => RepeatMode.Off,
        };
        ApplyQueueOptions();
    }

    [RelayCommand]
    private void ToggleShuffle()
    {
        Shuffle = !Shuffle;
        ApplyQueueOptions();
    }

    [RelayCommand]
    private void ToggleTimeDisplay()
    {
        PlaybackSettings playback = _services.Settings.Playback;
        playback.TimeDisplay = playback.TimeDisplay switch
        {
            TimeDisplayMode.Elapsed => TimeDisplayMode.Remaining,
            TimeDisplayMode.Remaining => TimeDisplayMode.QueueRemaining,
            _ => TimeDisplayMode.Elapsed,
        };
        _services.NotifySettingsChanged(applyToEngine: false);
    }

    [RelayCommand]
    private void DismissError() => ErrorMessage = null;

    [RelayCommand]
    private void DismissOverload()
    {
        _overloadDismissed = true;
        OverloadMessage = null;
    }

    [RelayCommand]
    private void OpenDspStudio() => SelectedNav = DspNav;

    [RelayCommand]
    private void Navigate(string? key)
    {
        if (Navigation.FirstOrDefault(n => n.Key == key) is { } item)
        {
            SelectedNav = item;
        }
    }

    private void ApplyQueueOptions()
    {
        _services.Engine.SetQueueOptions(Repeat, Shuffle);
        _services.Settings.Playback.Repeat = Repeat;
        _services.Settings.Playback.Shuffle = Shuffle;
        _services.NotifySettingsChanged(applyToEngine: false);
    }

    private void ShowError(string message)
    {
        ErrorMessage = message;
        _errorUntilUtc = DateTime.UtcNow + ErrorHold;
    }

    private void RefreshVolumeRange()
    {
        VolumeSettings volume = _services.Settings.Volume;
        VolumeMinimum = volume.MinimumDb;
        VolumeMaximum = volume.MaximumDb;
        IsVolumeFixed = volume.IsBypassed;
        if (VolumeDb < volume.MinimumDb || VolumeDb > volume.MaximumDb)
        {
            VolumeDb = Math.Clamp(VolumeDb, volume.MinimumDb, volume.MaximumDb);
        }
    }

    private void Tick()
    {
        PlaybackEngine engine = _services.Engine;
        PlaybackStatus status = engine.GetStatus();
        bool active = status.State != EngineState.Stopped;
        IsActive = active;
        IsPlaying = status.State == EngineState.Playing;

        UpdateCurrent(status);
        UpdateTimes(status, active);
        UpdateSignalPath(status, active);

        DateTime now = DateTime.UtcNow;
        UpdateOverload(status, now);
        if (status.LimiterEvents > _lastLimiterEvents)
        {
            _limiterUntilUtc = now + LimiterHold;
        }

        _lastLimiterEvents = status.LimiterEvents;
        IsLimiting = now < _limiterUntilUtc;
        if (ErrorMessage is not null && now > _errorUntilUtc)
        {
            ErrorMessage = null;
        }

        if (SelectedNav == OutputNav)
        {
            Output.RefreshDriverStatus();
        }
        else if (SelectedNav == SettingsNav)
        {
            General.RefreshFifo(status.FifoBytes, status.FifoSeconds);
        }

        Queue.SetCurrent(status.CurrentItem?.Id);
        Calibration.IsTonePlaying = active && status.IsTestTone;
        LiveInput.Update(status);
        NowPlaying.UpdateStatus(status);
        UpdateMeters(engine, status);
    }

    private void UpdateCurrent(PlaybackStatus status)
    {
        (Guid? Id, bool TestTone) key = (status.CurrentItem?.Id, status.IsTestTone);
        if (_current == key)
        {
            return;
        }

        _current = key;
        Cover = null;
        NowPlaying.Cover = null;
        if (status.IsTestTone)
        {
            TrackTitle = "Pink noise test signal";
            TrackSubtitle = "Calibration  ·  −20 dBFS RMS";
            NowPlaying.SetPlaceholder(TrackTitle, TrackSubtitle);
        }
        else if (status.CurrentItem is { } item)
        {
            TrackTitle = item.DisplayTitle;
            TrackSubtitle = item.DisplayArtist;
            NowPlaying.SetPlaceholder(item.DisplayTitle, item.DisplayArtist);
            _ = LoadCurrentAsync(item);
        }
        else
        {
            TrackTitle = "Nothing playing";
            TrackSubtitle = "Pick an album in the library or drop files anywhere";
            NowPlaying.SetPlaceholder(TrackTitle, "Pick an album in the library, or drop music files anywhere in this window.");
        }
    }

    private async Task LoadCurrentAsync(QueueItem item)
    {
        TrackMetadata metadata = item.Metadata is { Format: not null } known
            ? known
            : await Task.Run(() => MetadataLoader.ReadSafe(item.Path, probeFormat: true));
        item.Metadata = metadata;
        if (_current?.Id != item.Id)
        {
            return;
        }

        TrackTitle = metadata.DisplayTitle;
        TrackSubtitle = string.IsNullOrWhiteSpace(metadata.Album) ? metadata.DisplayArtist : $"{metadata.DisplayArtist}  ·  {metadata.DisplayAlbum}";
        NowPlaying.SetTrack(metadata);

        Bitmap? cover = await _services.Covers.GetTrackCoverAsync(item.Path, 720);
        if (_current?.Id == item.Id)
        {
            Cover = cover;
            NowPlaying.Cover = cover;
        }
    }

    private void UpdateTimes(PlaybackStatus status, bool active)
    {
        TimeSpan position = status.Position;
        TimeSpan duration = status.Duration;
        PositionSeconds = position.TotalSeconds;
        DurationSeconds = duration.TotalSeconds;
        CanSeek = active && duration > TimeSpan.Zero && !status.IsTestTone;
        PositionText = Formatting.Time(position);
        DurationText = _services.Settings.Playback.TimeDisplay switch
        {
            TimeDisplayMode.Remaining when duration > TimeSpan.Zero => "−" + Formatting.Time(duration - position),
            TimeDisplayMode.QueueRemaining when duration > TimeSpan.Zero && status.CurrentItem is { } item =>
                "−" + Formatting.Time(duration - position + Queue.DurationAfter(item.Id)),
            _ => duration > TimeSpan.Zero ? Formatting.Time(duration) : status.IsTestTone && active ? "∞" : "0:00",
        };
    }

    private void UpdateSignalPath(PlaybackStatus status, bool active)
    {
        HasPlan = active && status.Plan is not null;
        if (HasPlan && status.Plan is { } plan)
        {
            SourceText = plan.Source.DescribeShort();
            FilterText = plan.FilterName;
            QuantizerText = plan.QuantizerName;
            OutputText = plan.Output.DescribeShort();
            IsDsdOutput = plan.Output.IsDsd;
        }

        DeviceText = active && status.BackendName is not null
            ? $"{status.BackendName}  ·  {status.DeviceName}"
            : "Idle";
        DspLoad = active ? Math.Clamp(status.DspLoad, 0, 1) : 0;
        DspLoadText = active ? Formatting.Percent(status.DspLoad) : "-";
        IsDspOverloaded = active && status.DspLoad > 0.9;
        BufferFill = active ? status.BufferFill : 0;
        BufferText = active ? Formatting.Percent(status.BufferFill) : "-";
    }

    /// <summary>
    /// Shows a warning when processing cannot keep up: DSP load at 95 % or more for three seconds in a row, or dropouts
    /// in three of the last five seconds. The warning goes away after three calm seconds.
    /// </summary>
    private void UpdateOverload(PlaybackStatus status, DateTime now)
    {
        if (status.State != EngineState.Playing)
        {
            _loadHistory.Clear();
            _loadSampleUnderruns = -1;
            if (status.State == EngineState.Stopped)
            {
                OverloadMessage = null;
                _overloadDismissed = false;
            }

            return;
        }

        if (_loadSampleUnderruns < 0 || status.UnderrunFrames < _loadSampleUnderruns)
        {
            _loadSampleUnderruns = status.UnderrunFrames;
            _loadSampleStartUtc = now;
            _loadSampleMaximum = 0;
        }

        _loadSampleMaximum = Math.Max(_loadSampleMaximum, status.DspLoad);
        if (now - _loadSampleStartUtc < LoadSampleLength)
        {
            return;
        }

        _loadHistory.Enqueue((_loadSampleMaximum, status.UnderrunFrames > _loadSampleUnderruns));
        while (_loadHistory.Count > 5)
        {
            _loadHistory.Dequeue();
        }

        _loadSampleUnderruns = status.UnderrunFrames;
        _loadSampleStartUtc = now;
        _loadSampleMaximum = 0;

        (double Load, bool Dropout)[] recent = _loadHistory.TakeLast(3).ToArray();
        bool saturated = recent.Length == 3 && recent.All(s => s.Load >= 0.95);
        bool dropouts = _loadHistory.Count(s => s.Dropout) >= 3;
        bool calm = recent.Length == 3 && recent.All(s => s.Load < 0.9 && !s.Dropout);
        if ((saturated || dropouts) && !_overloadDismissed)
        {
            OverloadMessage = status.Plan is { Output.IsDsd: true }
                ? "Processing cannot keep up with this output, so the sound may drop out. In the DSP studio, lower the highest DSD rate, choose a lighter modulator or filter, or use multi-stage filter staging."
                : "Processing cannot keep up with this output, so the sound may drop out. In the DSP studio, lower the highest PCM rate, choose a lighter filter, or use multi-stage filter staging.";
        }
        else if (calm)
        {
            OverloadMessage = null;
            _overloadDismissed = false;
        }
    }

    private void UpdateMeters(PlaybackEngine engine, PlaybackStatus status)
    {
        MeterHub? hub = status.State != EngineState.Stopped ? engine.Meters : null;
        NowPlaying.Analyzer.Update(hub, status.Plan, visible: SelectedNav == NowPlayingNav, playing: status.State == EngineState.Playing);
        if (hub is null)
        {
            if (!_metersIdle)
            {
                _metersIdle = true;
                NowPlaying.ResetMeters();
            }

            return;
        }

        _metersIdle = false;
        NowPlaying.UpdateMeters(hub.Source.Read(), hub.Output?.Read());
    }
}
