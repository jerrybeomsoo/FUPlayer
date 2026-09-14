using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FUPlayer.App.Services;
using FUPlayer.Core.Engine;
using FUPlayer.Core.Settings;

namespace FUPlayer.App.ViewModels;

/// <summary>Level and distance of one loudspeaker.</summary>
public sealed partial class ChannelTrimViewModel : ObservableObject
{
    private readonly CalibrationViewModel _owner;
    private readonly SpeakerTrim _trim;
    private readonly bool _ready;

    [ObservableProperty]
    private double _levelDb;

    [ObservableProperty]
    private decimal _distanceCm;

    [ObservableProperty]
    private string _levelText = string.Empty;

    [ObservableProperty]
    private string _delayText = string.Empty;

    public ChannelTrimViewModel(CalibrationViewModel owner, SpeakerTrim trim, int index)
    {
        _owner = owner;
        _trim = trim;
        Index = index;
        LevelDb = trim.LevelDb;
        DistanceCm = (decimal)trim.DistanceCm;
        LevelText = Formatting.Db(trim.LevelDb, "+0.0;-0.0;0.0");
        _ready = true;
    }

    public int Index { get; }

    public string Name => _trim.Name;

    public string Label => MeterChannelViewModel.LabelFor(Index, _owner.ChannelCount);

    partial void OnLevelDbChanged(double value)
    {
        double rounded = Math.Round(value * 10) / 10;
        LevelText = Formatting.Db(rounded, "+0.0;-0.0;0.0");
        if (_ready && _trim.LevelDb != rounded)
        {
            _trim.LevelDb = rounded;
            _owner.OnTrimChanged();
        }
    }

    partial void OnDistanceCmChanged(decimal value)
    {
        double distance = (double)Math.Clamp(value, 0m, 5000m);
        if (_ready && _trim.DistanceCm != distance)
        {
            _trim.DistanceCm = distance;
            _owner.OnTrimChanged();
        }
    }

    [RelayCommand]
    private void ResetLevel() => LevelDb = 0;
}

/// <summary>The Calibration page: speaker levels, speaker distances and the pink-noise test signal.</summary>
public sealed partial class CalibrationViewModel : ObservableObject
{
    private const double SpeedOfSoundCmPerSecond = 34_300.0;

    private readonly PlayerServices _services;

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private bool _isTonePlaying;

    public CalibrationViewModel(PlayerServices services)
    {
        _services = services;
        _isEnabled = services.Settings.Speakers.Enabled;
        SyncChannels();
        services.SettingsChanged += (_, _) => SyncChannels();
    }

    public ObservableCollection<ChannelTrimViewModel> Channels { get; } = [];

    public int ChannelCount => Math.Clamp(_services.Settings.Output.Channels, 1, 8);

    private SpeakerSettings Speakers => _services.Settings.Speakers;

    partial void OnIsEnabledChanged(bool value)
    {
        if (Speakers.Enabled != value)
        {
            Speakers.Enabled = value;
            _services.NotifySettingsChanged();
        }
    }

    internal void OnTrimChanged()
    {
        UpdateDelays();
        _services.NotifySettingsChanged(applyToEngine: IsEnabled);
    }

    [RelayCommand]
    private void PlayAllChannels() => _services.Engine.PlayTestTone(TestToneMode.AllChannels);

    [RelayCommand]
    private void PlayRotating() => _services.Engine.PlayTestTone(TestToneMode.Rotating);

    [RelayCommand]
    private void StopTone() => _services.Engine.Stop();

    [RelayCommand]
    private void ResetAll()
    {
        foreach (ChannelTrimViewModel channel in Channels)
        {
            channel.LevelDb = 0;
            channel.DistanceCm = 200;
        }
    }

    private void SyncChannels()
    {
        int count = ChannelCount;
        while (Speakers.Channels.Count < SpeakerSettings.ChannelNames.Length)
        {
            Speakers.Channels.Add(new SpeakerTrim { Name = SpeakerSettings.ChannelNames[Speakers.Channels.Count] });
        }

        if (Channels.Count == count)
        {
            return;
        }

        Channels.Clear();
        for (int i = 0; i < count; i++)
        {
            Channels.Add(new ChannelTrimViewModel(this, Speakers.Channels[i], i));
        }

        OnPropertyChanged(nameof(ChannelCount));
        UpdateDelays();
    }

    private void UpdateDelays()
    {
        if (Channels.Count == 0)
        {
            return;
        }

        double farthest = Channels.Max(c => (double)c.DistanceCm);
        foreach (ChannelTrimViewModel channel in Channels)
        {
            double delayMs = (farthest - (double)channel.DistanceCm) / SpeedOfSoundCmPerSecond * 1000.0;
            channel.DelayText = delayMs.ToString("0.00", CultureInfo.CurrentCulture) + " ms delay";
        }
    }
}
