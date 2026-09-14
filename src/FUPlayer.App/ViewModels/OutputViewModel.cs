using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FUPlayer.App.Services;
using FUPlayer.Audio.Windows;
using FUPlayer.Core.Audio;
using FUPlayer.Core.Output;
using FUPlayer.Core.Settings;

namespace FUPlayer.App.ViewModels;

/// <summary>An audio back-end as shown in the picker.</summary>
public sealed class BackendChoice
{
    public BackendChoice(IAudioBackend backend)
    {
        Backend = backend;
        IsAvailable = backend.IsAvailable;
        var traits = new List<string>();
        if (backend.IsBitPerfect)
        {
            traits.Add("Bit-perfect");
        }

        if (backend.SupportsNativeDsd)
        {
            traits.Add("Native DSD");
        }

        if (!IsAvailable)
        {
            traits.Add("Not available");
        }

        Traits = string.Join("  ·  ", traits);
    }

    public IAudioBackend Backend { get; }

    public string Id => Backend.Id;

    public string Name => Backend.DisplayName;

    public string Description => Backend.Description;

    public bool IsAvailable { get; }

    public string Traits { get; }
}

/// <summary>The Output page: back-end, device, capabilities, buffering and volume range.</summary>
public sealed partial class OutputViewModel : ObservableObject
{
    /// <summary>Highest volume setting; above 0 dB the soft limiter protects against overs.</summary>
    public const double MaximumVolumeDb = 12.0;

    private readonly PlayerServices _services;
    private readonly IDialogService _dialogs;
    private bool _loading;
    private int _probeGeneration;

    [ObservableProperty]
    private BackendChoice? _selectedBackend;

    [ObservableProperty]
    private AudioDevice? _selectedDevice;

    [ObservableProperty]
    private bool _isProbing;

    [ObservableProperty]
    private string _capabilityNotes = string.Empty;

    [ObservableProperty]
    private bool _hasCapabilityNotes;

    [ObservableProperty]
    private string _maxChannelsText = "-";

    [ObservableProperty]
    private string _dopText = "-";

    [ObservableProperty]
    private bool _hasNativeDsd;

    [ObservableProperty]
    private decimal _channels;

    [ObservableProperty]
    private decimal _firstChannel;

    [ObservableProperty]
    private Choice? _selectedBuffer;

    [ObservableProperty]
    private bool _lowLatencyFifo;

    [ObservableProperty]
    private bool _instantPause;

    [ObservableProperty]
    private bool _hasControlPanel;

    [ObservableProperty]
    private bool _isFileBackend;

    [ObservableProperty]
    private string _renderDirectory = string.Empty;

    [ObservableProperty]
    private decimal _minimumDb;

    [ObservableProperty]
    private decimal _maximumDb;

    [ObservableProperty]
    private decimal _pcmLevelOffsetDb;

    [ObservableProperty]
    private string _volumeSummary = string.Empty;

    [ObservableProperty]
    private string _message = string.Empty;

    [ObservableProperty]
    private bool _isAsio;

    [ObservableProperty]
    private string _driverStatus = string.Empty;

    [ObservableProperty]
    private bool _hasCapabilities;

    public OutputViewModel(PlayerServices services, IDialogService dialogs)
    {
        _services = services;
        _dialogs = dialogs;
        _loading = true;
        Backends = services.Backends.Backends.Select(b => new BackendChoice(b)).ToArray();

        OutputSettings output = services.Settings.Output;
        SelectedBackend = Backends.FirstOrDefault(b => b.Id == output.BackendId) ?? Backends.FirstOrDefault(b => b.IsAvailable);
        Channels = output.Channels;
        FirstChannel = output.FirstChannel;
        SelectedBuffer = Choice.Find(BufferChoices, output.BufferMilliseconds) ?? BufferChoices[0];
        LowLatencyFifo = output.LowLatencyFifo;
        InstantPause = output.InstantPause;
        RenderDirectory = services.RenderDirectory;

        VolumeSettings volume = services.Settings.Volume;
        MinimumDb = (decimal)volume.MinimumDb;
        MaximumDb = (decimal)volume.MaximumDb;
        PcmLevelOffsetDb = (decimal)volume.PcmLevelOffsetDb;
        UpdateVolumeSummary();
        _loading = false;

        // Only list devices at start-up. Probing opens the device (ASIO drivers take it over), so it waits until
        // this page is actually shown.
        _ = RefreshDevicesAsync(refreshCapabilities: false, allowProbe: false);
    }

    /// <summary>Called when the page becomes visible: probes the device once if it has not been probed yet.</summary>
    public void EnsureCapabilities()
    {
        if (!HasCapabilities)
        {
            _ = RefreshCapabilitiesAsync(refresh: false, allowProbe: true);
        }
    }

    /// <summary>Refreshes the ASIO driver status shown on the page (called by the shell's status timer).</summary>
    public void RefreshDriverStatus()
    {
        if (IsAsio && OperatingSystem.IsWindows())
        {
            DriverStatus = AsioTrace.Status + (AsioTrace.IsEnabled ? $"  ·  log: {AsioTrace.Path}" : string.Empty);
        }
    }

    public IReadOnlyList<BackendChoice> Backends { get; }

    public ObservableCollection<AudioDevice> Devices { get; } = [];

    public ObservableCollection<string> PcmRates { get; } = [];

    public ObservableCollection<string> ContainerBits { get; } = [];

    public ObservableCollection<string> DsdRates { get; } = [];

    public IReadOnlyList<Choice> BufferChoices { get; } =
    [
        new("Driver default", 0),
        new("5 ms", 5),
        new("10 ms", 10),
        new("20 ms", 20),
        new("50 ms", 50),
        new("100 ms", 100),
        new("200 ms", 200),
    ];

    private OutputSettings Settings => _services.Settings.Output;

    partial void OnSelectedBackendChanged(BackendChoice? value)
    {
        if (value is null)
        {
            return;
        }

        HasControlPanel = value.Backend.HasControlPanel;
        IsFileBackend = value.Id == FileAudioBackend.BackendId;
        IsAsio = value.Id == AsioBackend.BackendId;
        RefreshDriverStatus();
        if (_loading)
        {
            return;
        }

        if (Settings.BackendId != value.Id)
        {
            Settings.BackendId = value.Id;
            Settings.DeviceId = null;
            _services.NotifySettingsChanged();
        }

        _ = RefreshDevicesAsync(refreshCapabilities: false);
    }

    partial void OnSelectedDeviceChanged(AudioDevice? value)
    {
        if (_loading || value is null)
        {
            return;
        }

        if (Settings.DeviceId != value.Id)
        {
            Settings.DeviceId = value.Id;
            HasCapabilities = false;
            _services.NotifySettingsChanged();
        }

        _ = RefreshCapabilitiesAsync(refresh: false);
    }

    partial void OnChannelsChanged(decimal value)
    {
        int channels = (int)Math.Clamp(value, 1, 8);
        if (_loading || Settings.Channels == channels)
        {
            return;
        }

        Settings.Channels = channels;
        _services.NotifySettingsChanged();
        _ = RefreshCapabilitiesAsync(refresh: false);
    }

    partial void OnFirstChannelChanged(decimal value)
    {
        int first = (int)Math.Clamp(value, 0, 64);
        if (!_loading && Settings.FirstChannel != first)
        {
            Settings.FirstChannel = first;
            _services.NotifySettingsChanged();
        }
    }

    partial void OnSelectedBufferChanged(Choice? value)
    {
        if (!_loading && value is { Value: int milliseconds } && Settings.BufferMilliseconds != milliseconds)
        {
            Settings.BufferMilliseconds = milliseconds;
            _services.NotifySettingsChanged();
        }
    }

    partial void OnLowLatencyFifoChanged(bool value)
    {
        if (!_loading && Settings.LowLatencyFifo != value)
        {
            Settings.LowLatencyFifo = value;
            _services.NotifySettingsChanged();
        }
    }

    partial void OnInstantPauseChanged(bool value)
    {
        if (!_loading && Settings.InstantPause != value)
        {
            Settings.InstantPause = value;
            _services.NotifySettingsChanged();
        }
    }

    partial void OnMinimumDbChanged(decimal value) => UpdateVolume();

    partial void OnMaximumDbChanged(decimal value) => UpdateVolume();

    partial void OnPcmLevelOffsetDbChanged(decimal value) => UpdateVolume();

    [RelayCommand]
    private Task RefreshDevices() => RefreshDevicesAsync(refreshCapabilities: true);

    [RelayCommand]
    private void ShowControlPanel()
    {
        if (SelectedBackend?.Backend is not { HasControlPanel: true } backend)
        {
            return;
        }

        string? device = SelectedDevice?.Id;
        _ = Task.Run(() =>
        {
            try
            {
                backend.ShowControlPanel(device);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => Message = "The driver control panel could not be opened: " + ex.Message);
            }
        });
    }

    [RelayCommand]
    private async Task BrowseRenderDirectoryAsync()
    {
        IReadOnlyList<string> folders = await _dialogs.PickFoldersAsync("Folder for rendered files");
        if (folders.Count > 0)
        {
            Settings.FileOutputDirectory = folders[0];
            RenderDirectory = folders[0];
            _services.NotifySettingsChanged(applyToEngine: false);
        }
    }

    [RelayCommand]
    private void OpenRenderDirectory()
    {
        try
        {
            Directory.CreateDirectory(_services.RenderDirectory);
            Process.Start(new ProcessStartInfo(_services.RenderDirectory) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Message = "The folder could not be opened: " + ex.Message;
        }
    }

    private void UpdateVolume()
    {
        if (_loading)
        {
            return;
        }

        VolumeSettings volume = _services.Settings.Volume;
        volume.MaximumDb = (double)Math.Clamp(MaximumDb, -40m, (decimal)MaximumVolumeDb);
        volume.MinimumDb = (double)Math.Clamp(MinimumDb, -120m, (decimal)volume.MaximumDb);
        volume.PcmLevelOffsetDb = (double)Math.Clamp(PcmLevelOffsetDb, -12m, 12m);
        volume.VolumeDb = Math.Clamp(volume.VolumeDb, volume.MinimumDb, volume.MaximumDb);

        // Show what was actually stored when a value had to be clamped (e.g. a minimum above the maximum).
        _loading = true;
        try
        {
            MaximumDb = (decimal)volume.MaximumDb;
            MinimumDb = (decimal)volume.MinimumDb;
            PcmLevelOffsetDb = (decimal)volume.PcmLevelOffsetDb;
        }
        finally
        {
            _loading = false;
        }

        UpdateVolumeSummary();
        _services.NotifySettingsChanged();
    }

    private void UpdateVolumeSummary()
    {
        VolumeSettings volume = _services.Settings.Volume;
        VolumeSummary = volume.IsBypassed
            ? "Volume control is bypassed: the signal leaves at 0 dB, so use the amplifier or DAC volume."
            : $"The knob covers {Formatting.Db(volume.MinimumDb)} to {Formatting.Db(volume.MaximumDb)}. Set both to 0 dB to bypass digital volume.";
    }

    private async Task RefreshDevicesAsync(bool refreshCapabilities, bool allowProbe = true)
    {
        BackendChoice? backend = SelectedBackend;
        if (backend is null)
        {
            return;
        }

        IReadOnlyList<AudioDevice> devices = await Task.Run(() =>
        {
            try
            {
                return backend.Backend.GetDevices();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return (IReadOnlyList<AudioDevice>)[];
            }
        });

        if (backend != SelectedBackend)
        {
            return;
        }

        _loading = true;
        try
        {
            Devices.Clear();
            foreach (AudioDevice device in devices)
            {
                Devices.Add(device);
            }

            string? wanted = Settings.DeviceId;
            SelectedDevice = devices.FirstOrDefault(d => d.Id == wanted) ?? devices.FirstOrDefault(d => d.IsDefault) ?? devices.FirstOrDefault();
        }
        finally
        {
            _loading = false;
        }

        Message = devices.Count == 0 ? "No devices were found for this output type." : string.Empty;
        await RefreshCapabilitiesAsync(refreshCapabilities, allowProbe);
    }

    private async Task RefreshCapabilitiesAsync(bool refresh, bool allowProbe = true)
    {
        if (SelectedBackend is not { } backend)
        {
            return;
        }

        string? device = SelectedDevice?.Id;
        int channels = Settings.Channels;
        int generation = ++_probeGeneration;
        IsProbing = allowProbe;
        DeviceCapabilities capabilities = await Task.Run(() => _services.GetCapabilities(backend.Id, device, channels, refresh, allowProbe));
        if (generation != _probeGeneration)
        {
            return;
        }

        IsProbing = false;
        HasCapabilities |= allowProbe;
        RefreshDriverStatus();
        PcmRates.Clear();
        foreach (int rate in capabilities.PcmRates)
        {
            PcmRates.Add(AudioRates.FormatShort(rate));
        }

        ContainerBits.Clear();
        foreach (int bits in capabilities.ContainerBits)
        {
            ContainerBits.Add(bits.ToString(CultureInfo.InvariantCulture) + "-bit");
        }

        DsdRates.Clear();
        foreach (int rate in capabilities.NativeDsdRates.Order())
        {
            string label = "DSD" + AudioRates.DsdMultiplier(rate).ToString(CultureInfo.InvariantCulture);
            DsdRates.Add(AudioRates.Is44k1Family(rate) ? label : label + " · 48k");
        }

        HasNativeDsd = DsdRates.Count > 0;
        MaxChannelsText = capabilities.MaxChannels.ToString(CultureInfo.InvariantCulture);
        int[] dop = AudioRates.DsdMultipliers.Where(m => capabilities.SupportsDop(AudioRates.DsdRate(m, family48k: false))).ToArray();
        DopText = dop.Length == 0
            ? "Not possible at the supported PCM rates"
            : string.Join(", ", dop.Select(m => $"DSD{m.ToString(CultureInfo.InvariantCulture)} ({AudioRates.Format(AudioRates.DsdRate(m, family48k: false) / 16)})"));
        CapabilityNotes = capabilities.Notes ?? string.Empty;
        HasCapabilityNotes = !string.IsNullOrWhiteSpace(capabilities.Notes);
    }
}
