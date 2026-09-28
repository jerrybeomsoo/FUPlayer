using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FUPlayer.App.Services;
using FUPlayer.Core.Decoding.FFmpeg;
using FUPlayer.Core.Dsp.Acceleration;
using FUPlayer.Core.Settings;

namespace FUPlayer.App.ViewModels;

/// <summary>The Settings page: playback behaviour, processing resources and information about the build.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly PlayerServices _services;
    private readonly bool _ready;

    [ObservableProperty]
    private bool _gapless;

    [ObservableProperty]
    private Choice? _selectedReplayGain;

    [ObservableProperty]
    private bool _preventReplayGainClipping;

    [ObservableProperty]
    private bool _invertPolarity;

    [ObservableProperty]
    private Choice? _selectedTimeDisplay;

    [ObservableProperty]
    private Choice? _selectedDspThreads;

    [ObservableProperty]
    private Choice? _selectedFifo;

    [ObservableProperty]
    private bool _meterAdjustedSource;

    [ObservableProperty]
    private bool _gpuAcceleration;

    [ObservableProperty]
    private bool _gpuForce;

    [ObservableProperty]
    private Choice? _selectedGpuDevice;

    [ObservableProperty]
    private Choice? _selectedGpuPrecision;

    [ObservableProperty]
    private Choice? _selectedGpuDivision;

    [ObservableProperty]
    private string _ffmpegStatus = "Checking…";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanBuildFfmpeg), nameof(FfmpegBadge))]
    private bool _isFfmpegAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanBuildFfmpeg), nameof(FfmpegBadge))]
    private bool _isFfmpegBuilding;

    [ObservableProperty]
    private string _ffmpegStage = string.Empty;

    [ObservableProperty]
    private double _ffmpegProgress;

    [ObservableProperty]
    private bool _isFfmpegProgressKnown;

    [ObservableProperty]
    private string _ffmpegLine = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFfmpegError))]
    private string? _ffmpegError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFfmpegDone))]
    private string? _ffmpegDone;

    [ObservableProperty]
    private string _ffmpegBuildDescription = string.Empty;

    private CancellationTokenSource? _ffmpegBuild;
    private string? _ffmpegLogPath;
    private bool _ffmpegChecked;

    public SettingsViewModel(PlayerServices services)
    {
        _services = services;
        PlayerSettings settings = services.Settings;
        Gapless = settings.Playback.Gapless;
        SelectedReplayGain = Choice.Find(ReplayGainChoices, settings.Playback.ReplayGain);
        PreventReplayGainClipping = settings.Playback.PreventReplayGainClipping;
        InvertPolarity = settings.Playback.InvertPolarity;
        SelectedTimeDisplay = Choice.Find(TimeDisplayChoices, settings.Playback.TimeDisplay);

        int threads = Math.Min(16, Environment.ProcessorCount);
        DspThreadChoices =
        [
            new("Automatic", -1, $"One thread per channel; a partitioned filter spreads its phases over all {threads}."),
            new("Single thread", 0, "Every channel and every stage on the engine thread."),
            .. Enumerable.Range(1, threads).Select(n => new Choice(
                $"{n} extra {(n == 1 ? "thread" : "threads")}",
                n,
                $"{n + 1} threads total, shared between channels and filter phases.")),
        ];
        SelectedDspThreads = Choice.Find(DspThreadChoices, settings.Processing.DspThreads) ?? DspThreadChoices[0];
        SelectedFifo = Choice.Find(FifoChoices, settings.Processing.FifoMilliseconds) ?? FifoChoices[1];
        MeterAdjustedSource = settings.Processing.MeterAdjustedSource;

        GpuDeviceChoices =
        [
            new("Automatic", string.Empty, "The most capable device found."),
            .. GpuRuntime.Devices.Select(d => new Choice(d.Name, d.Id, d.Summary)),
        ];
        SelectedGpuDevice = Choice.Find(GpuDeviceChoices, settings.Processing.GpuDeviceId) ?? GpuDeviceChoices[0];
        SelectedGpuPrecision = Choice.Find(GpuPrecisionChoices, settings.Processing.GpuHighPrecision) ?? GpuPrecisionChoices[0];
        SelectedGpuDivision = Choice.Find(
            GpuDivisionChoices,
            settings.Processing.GpuPhaseShare >= 0 ? settings.Processing.GpuPhaseShare
                : settings.Processing.GpuRetimeWhilePlaying ? Timed : TimedOnce) ?? GpuDivisionChoices[0];
        HasGpuDevices = GpuRuntime.IsAvailable;
        GpuAcceleration = settings.Processing.GpuAcceleration && HasGpuDevices;
        GpuForce = settings.Processing.GpuForce;
        GpuStatus = HasGpuDevices
            ? string.Join(Environment.NewLine, GpuRuntime.Devices.Select(d => $"{d.Name}: {d.Summary}"))
            : GpuRuntime.Unavailable ?? "No OpenCL device was found.";

        string? version = typeof(SettingsViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        Version = version is null ? "0.1" : version.Split('+')[0];
        _ready = true;
        _ = CheckFfmpegAsync();
    }

    public IReadOnlyList<Choice> ReplayGainChoices { get; } =
    [
        new("Off", ReplayGainMode.Off),
        new("Track gain", ReplayGainMode.Track),
        new("Album gain", ReplayGainMode.Album),
    ];

    public IReadOnlyList<Choice> TimeDisplayChoices { get; } =
    [
        new("Elapsed and total", TimeDisplayMode.Elapsed),
        new("Remaining in track", TimeDisplayMode.Remaining),
        new("Remaining in queue", TimeDisplayMode.QueueRemaining),
    ];

    public IReadOnlyList<Choice> DspThreadChoices { get; }

    /// <summary>
    /// How much audio is kept ahead of the device, and what that costs in memory. A PCM frame in the FIFO is four
    /// bytes a channel whatever the word length, and a native DSD frame is one byte per eight bits a channel,
    /// which comes to the same 5.4 MiB a second for 705.6 kHz stereo as for DSD512 stereo, since they carry the
    /// same number of bits. The ring is a power of two bytes, so every figure below is the allocation after
    /// rounding up, and the audio it holds is correspondingly longer than the setting asks for.
    /// </summary>
    public IReadOnlyList<Choice> FifoChoices { get; } =
    [
        new("100 ms", 100, "64 KiB at 44.1 kHz stereo, 1 MiB at 705.6 kHz or DSD512. Minimum, highest dropout risk."),
        new("250 ms", 250, "128 KiB at 44.1 kHz stereo, 2 MiB at 705.6 kHz or DSD512. Default."),
        new("500 ms", 500, "256 KiB at 44.1 kHz stereo, 4 MiB at 705.6 kHz or DSD512."),
        new("1 s", 1_000, "512 KiB at 44.1 kHz stereo, 8 MiB at 705.6 kHz or DSD512."),
        new("2 s", 2_000, "1 MiB at 44.1 kHz stereo, 16 MiB at 705.6 kHz or DSD512."),
        new("4 s", 4_000, "2 MiB at 44.1 kHz stereo, 32 MiB at 705.6 kHz or DSD512."),
        new("8 s", 8_000, "4 MiB at 44.1 kHz stereo, 64 MiB at 705.6 kHz or DSD512."),
        new("15 s", 15_000, "8 MiB at 44.1 kHz stereo, 128 MiB at 705.6 kHz or DSD512. Seeks refill slowly."),
        new("30 s", 30_000, "16 MiB at 44.1 kHz stereo. Hits the 256 MiB cap at 705.6 kHz, giving 47 s there."),
        new("60 s", 60_000, "32 MiB at 44.1 kHz stereo. Same 256 MiB cap and 47 s at 705.6 kHz as 30 s."),
    ];

    /// <summary>What the FIFO is actually holding, once something is playing and the real format is known.</summary>
    [ObservableProperty]
    private string _fifoSize = "No stream, no FIFO allocated.";

    /// <summary>
    /// Called from the frame timer while this page is shown. The engine's own ring is the honest figure: the
    /// setting is a floor, and the device buffer, a block-based filter or the 256 MB ceiling can all move it.
    /// </summary>
    public void RefreshFifo(int bytes, double seconds)
    {
        FifoSize = bytes <= 0
            ? "No stream, no FIFO allocated."
            : string.Create(
                System.Globalization.CultureInfo.CurrentCulture,
                $"Allocated {bytes / (1024.0 * 1024.0):0.###} MiB, {seconds * 1000.0:N0} ms at the current " +
                $"format. Rounded up to a power of two, and raised by the device buffer or a block-based filter.");
    }

    public IReadOnlyList<Choice> GpuDeviceChoices { get; }

    public IReadOnlyList<Choice> GpuPrecisionChoices { get; } =
    [
        new("64-bit (double)", true,
            "Bit-identical to the CPU. Consumer GPUs run FP64 at a fraction of their FP32 rate, often 1/32."),
        new("32-bit (single)", false,
            "2x to 3x faster on most cards. Deviation from the CPU is measured and shown in Now playing, typically -130 dB."),
    ];

    /// <summary>Time every division of the phases and keep the quickest, revisiting it as the stream plays.</summary>
    private const int Timed = -1;

    /// <summary>Time every division once, then hold the answer for the rest of the stream.</summary>
    private const int TimedOnce = -2;

    /// <summary>
    /// How the phases of a shared filter stage are divided between the device and the processor. A stereo
    /// conversion at sixteen phases has thirty-two of them in a block, and both convolve their share at once.
    /// </summary>
    public IReadOnlyList<Choice> GpuDivisionChoices { get; } =
    [
        new("Measured, and re-measured", Timed,
            "Times every split from 0 to all phases during playback, uses the fastest, and re-measures periodically. Tracks a machine whose clock speed varies."),
        new("Measured once", TimedOnce,
            "Times every split once, then holds the result for the rest of the stream. Ignores later thermal throttling."),
        new("Every phase", 100, "100 % of phases on the device, regardless of measurement."),
        new("Three quarters", 75, "75 % of phases on the device, filling channels in order."),
        new("Half", 50, "50 % of phases on the device."),
        new("A quarter", 25,
            "25 % of phases on the device. Small splits are inefficient because kernel launch cost is nearly fixed."),
    ];

    /// <summary>Whether there is any OpenCL device to offer.</summary>
    public bool HasGpuDevices { get; }

    /// <summary>What was found, or why nothing was.</summary>
    public string GpuStatus { get; }

    public string Version { get; }

    public string SettingsFolder => _services.Store.SettingsDirectory;

    /// <summary>The build is offered once the check has found no FFmpeg, and not while one runs.</summary>
    public bool CanBuildFfmpeg => _ffmpegChecked && !IsFfmpegAvailable && !IsFfmpegBuilding;

    public string FfmpegBadge => IsFfmpegAvailable ? "LOADED" : IsFfmpegBuilding ? "BUILDING" : "NOT INSTALLED";

    public bool HasFfmpegError => FfmpegError is not null;

    public bool HasFfmpegDone => FfmpegDone is not null;

    /// <summary>Stops a build in progress, for when the player closes.</summary>
    public void CancelFfmpegBuildOnExit() => _ffmpegBuild?.Cancel();

    public string RuntimeDescription => $".NET {Environment.Version}  ·  {RuntimeInformation.OSDescription}  ·  {RuntimeInformation.ProcessArchitecture}";

    private PlayerSettings Settings => _services.Settings;

    partial void OnGaplessChanged(bool value) => Update(() => Settings.Playback.Gapless = value);

    partial void OnSelectedReplayGainChanged(Choice? value)
    {
        if (value is { Value: ReplayGainMode mode })
        {
            Update(() => Settings.Playback.ReplayGain = mode);
        }
    }

    partial void OnPreventReplayGainClippingChanged(bool value) => Update(() => Settings.Playback.PreventReplayGainClipping = value);

    partial void OnInvertPolarityChanged(bool value)
    {
        if (!_ready)
        {
            return;
        }

        _services.Engine.InvertPolarity = value;
        Settings.Playback.InvertPolarity = value;
        _services.NotifySettingsChanged(applyToEngine: false);
    }

    partial void OnSelectedTimeDisplayChanged(Choice? value)
    {
        if (_ready && value is { Value: TimeDisplayMode mode })
        {
            Settings.Playback.TimeDisplay = mode;
            _services.NotifySettingsChanged(applyToEngine: false);
        }
    }

    partial void OnSelectedDspThreadsChanged(Choice? value)
    {
        if (value is { Value: int threads })
        {
            Update(() => Settings.Processing.DspThreads = threads);
        }
    }

    partial void OnSelectedFifoChanged(Choice? value)
    {
        if (value is { Value: int milliseconds })
        {
            Update(() => Settings.Processing.FifoMilliseconds = milliseconds);
        }
    }

    partial void OnMeterAdjustedSourceChanged(bool value) => Update(() => Settings.Processing.MeterAdjustedSource = value);

    partial void OnGpuAccelerationChanged(bool value) => Update(() => Settings.Processing.GpuAcceleration = value);

    partial void OnGpuForceChanged(bool value) => Update(() => Settings.Processing.GpuForce = value);

    partial void OnSelectedGpuDeviceChanged(Choice? value)
    {
        if (value is { Value: string id })
        {
            Update(() => Settings.Processing.GpuDeviceId = id);
        }
    }

    partial void OnSelectedGpuDivisionChanged(Choice? value)
    {
        if (value?.Value is int share)
        {
            Update(() =>
            {
                Settings.Processing.GpuPhaseShare = share < 0 ? -1 : share;
                Settings.Processing.GpuRetimeWhilePlaying = share != TimedOnce;
            });
        }
    }

    partial void OnSelectedGpuPrecisionChanged(Choice? value)
    {
        if (value is { Value: bool high })
        {
            Update(() => Settings.Processing.GpuHighPrecision = high);
        }
    }

    [RelayCommand]
    private void OpenSettingsFolder()
    {
        try
        {
            Directory.CreateDirectory(SettingsFolder);
            Process.Start(new ProcessStartInfo(SettingsFolder) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // Nothing sensible to do; the path is shown on the page.
        }
    }

    private void Update(Action apply)
    {
        if (_ready)
        {
            apply();
            _services.NotifySettingsChanged();
        }
    }

    private async Task CheckFfmpegAsync()
    {
        bool available = await Task.Run(() => FFmpegLibrary.IsAvailable);
        _ffmpegChecked = true;
        IsFfmpegAvailable = available;
        FfmpegStatus = available
            ? $"{FFmpegLibrary.VersionDescription ?? "Loaded"}{(FFmpegLibrary.LoadedFrom is { } folder ? $", from {folder}" : string.Empty)}"
            : "Not installed. FLAC, WAV, AIFF, DSF and DFF play without it; MP3, AAC, ALAC, Ogg Vorbis, Opus, WavPack and "
              + "the rest need it.";
        FfmpegBuildDescription = DescribeBuild();
        OnPropertyChanged(nameof(CanBuildFfmpeg));
    }

    /// <summary>What pressing the button will do on this computer, before it is pressed.</summary>
    private static string DescribeBuild()
    {
        string source = $"Downloads FFmpeg {FFmpegInstaller.FFmpegVersion}'s source from ffmpeg.org (12 MB) and builds its LGPL libraries on this computer";
        if (!OperatingSystem.IsWindows())
        {
            return $"{source} with the system's own compiler: gcc or clang, make and nasm need to be installed.";
        }

        return FFmpegInstaller.FindInstalledMsys2() is { } msys
            ? $"{source} with the MSYS2 installed at {msys}, adding its compiler if it lacks one. It takes a few minutes, and the player keeps playing meanwhile."
            : $"{source}. The compiler comes from MSYS2, which it sets up for itself in {SafeToolsRoot()}: about 50 MB to download "
              + "and 1 GB on disk once the compiler is in. The first build takes 10 minutes or more; the player keeps playing meanwhile.";
    }

    private static string SafeToolsRoot()
    {
        try
        {
            return FFmpegInstaller.DefaultToolsRoot();
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }
    }

    [RelayCommand]
    private async Task BuildFfmpegAsync()
    {
        if (IsFfmpegBuilding)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _ffmpegBuild = cancellation;
        IsFfmpegBuilding = true;
        FfmpegError = null;
        FfmpegDone = null;
        FfmpegStage = "Starting";
        FfmpegLine = string.Empty;
        IsFfmpegProgressKnown = false;

        // Reported on the UI thread, since the Progress was made here.
        var progress = new Progress<FFmpegInstallProgress>(p =>
        {
            if (p.Stage != FfmpegStage)
            {
                // A line from the step before says nothing about this one.
                FfmpegLine = string.Empty;
            }

            FfmpegStage = p.Stage;
            IsFfmpegProgressKnown = p.Fraction is not null;
            FfmpegProgress = p.Fraction ?? 0;
            if (p.Line is { Length: > 0 } line)
            {
                FfmpegLine = line;
            }
        });

        try
        {
            var installer = new FFmpegInstaller(new FFmpegInstallOptions(), progress);
            _ffmpegLogPath = installer.LogPath;
            await Task.Run(() => installer.RunAsync(cancellation.Token));
            FfmpegDone = "FFmpeg is built and loaded. The files that need it play now, and the library is looking for the ones it passed over.";
            _services.NotifyFFmpegInstalled();
        }
        catch (OperationCanceledException)
        {
            FfmpegError = "Stopped. Pressing the button again carries on from the download.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or InvalidDataException or HttpRequestException)
        {
            FfmpegError = ex.Message;
        }
        finally
        {
            _ffmpegBuild = null;
            IsFfmpegBuilding = false;
            await CheckFfmpegAsync();
        }
    }

    [RelayCommand]
    private void CancelFfmpegBuild() => _ffmpegBuild?.Cancel();

    [RelayCommand]
    private void OpenFfmpegLog()
    {
        if (_ffmpegLogPath is { } path && File.Exists(path))
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // Nothing sensible to do; the error above says what went wrong.
            }
        }
    }
}
