using Avalonia.Threading;
using FUPlayer.Audio.Windows;
using FUPlayer.Core.Engine;
using FUPlayer.Core.Library;
using FUPlayer.Core.Metadata;
using FUPlayer.Core.Output;
using FUPlayer.Core.Playlists;
using FUPlayer.Core.Settings;

namespace FUPlayer.App.Services;

/// <summary>Composition root: settings, back-ends, the playback engine, the library and caches.</summary>
public sealed class PlayerServices : IDisposable
{
    private readonly DispatcherTimer _commitTimer;
    private readonly object _capabilitiesGate = new();
    private readonly Dictionary<string, DeviceCapabilities> _capabilities = new(StringComparer.Ordinal);
    private bool _applyPending;
    private bool _savePending;
    private bool _disposed;

    /// <param name="settingsDirectory">Alternative folder for settings, library cache and queue (default: the user profile).</param>
    public PlayerServices(string? settingsDirectory = null)
    {
        Store = new SettingsStore(settingsDirectory);
        Settings = Store.Load();

        var backends = new List<IAudioBackend>();
        if (OperatingSystem.IsWindows())
        {
            backends.AddRange(WindowsAudioBackends.Create());
        }

        backends.Add(new FileAudioBackend(() => RenderDirectory));
        backends.Add(new NullAudioBackend());
        Backends = new AudioBackendRegistry(backends);

        Engine = new PlaybackEngine(Settings, Backends);
        Library = new MusicLibrary(Path.Combine(Store.SettingsDirectory, "library.json"));
        Covers = new CoverArtCache(Path.Combine(Store.SettingsDirectory, "thumbnails"));

        _commitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _commitTimer.Tick += (_, _) =>
        {
            _commitTimer.Stop();
            Commit();
        };
    }

    /// <summary>Raised on the UI thread whenever a page changed <see cref="Settings"/>.</summary>
    public event EventHandler? SettingsChanged;

    /// <summary>Raised after <see cref="SettingsChanged"/> for changes that affect how the engine plays (not volume or display options).</summary>
    public event EventHandler? EngineSettingsChanged;

    public SettingsStore Store { get; }

    /// <summary>The UI's working copy of the settings (UI thread only). The engine receives clones.</summary>
    public PlayerSettings Settings { get; }

    public AudioBackendRegistry Backends { get; }

    public PlaybackEngine Engine { get; }

    public MusicLibrary Library { get; }

    public CoverArtCache Covers { get; }

    public static string DefaultRenderDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "FUPlayer renders");

    public string RenderDirectory =>
        string.IsNullOrWhiteSpace(Settings.Output.FileOutputDirectory) ? DefaultRenderDirectory : Settings.Output.FileOutputDirectory;

    private string QueueFile => Path.Combine(Store.SettingsDirectory, "queue.m3u8");

    /// <summary>Records a settings change: saved shortly afterwards and, if requested, applied to the engine.</summary>
    /// <param name="applyToEngine">False for settings the UI applies live itself (volume, polarity, repeat) or that only concern the UI.</param>
    public void NotifySettingsChanged(bool applyToEngine = true)
    {
        _applyPending |= applyToEngine;
        _savePending = true;
        _commitTimer.Stop();
        _commitTimer.Start();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
        if (applyToEngine)
        {
            EngineSettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Commit()
    {
        if (_applyPending)
        {
            _applyPending = false;
            Engine.ApplySettings(Settings);
        }

        if (_savePending)
        {
            _savePending = false;
            try
            {
                Store.Save(Settings);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Settings stay in memory; the next change retries.
            }
        }
    }

    /// <summary>
    /// Device capabilities, cached per back-end, device and channel count. Probing opens the device (an ASIO driver
    /// even takes it over), so it happens only when the Output page asks for it or the user presses refresh; other
    /// callers pass <paramref name="allowProbe"/> = false and get the last probe, or a permissive guess.
    /// </summary>
    public DeviceCapabilities GetCapabilities(string? backendId, string? deviceId, int channels, bool refresh = false, bool allowProbe = true)
    {
        IAudioBackend backend = Backends.Resolve(backendId);
        string key = $"{backend.Id}|{deviceId}|{channels}";
        lock (_capabilitiesGate)
        {
            if (!refresh && _capabilities.TryGetValue(key, out DeviceCapabilities? cached))
            {
                return cached;
            }

            if (!allowProbe || Engine.State != EngineState.Stopped)
            {
                // Not allowed to probe, or the engine holds the device: the last probe, else the usual formats.
                return _capabilities.TryGetValue(key, out DeviceCapabilities? known)
                    ? known
                    : DeviceCapabilities.Unrestricted(Math.Max(2, channels));
            }
        }

        DeviceCapabilities capabilities;
        try
        {
            capabilities = backend.GetCapabilities(deviceId, channels);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new DeviceCapabilities { MaxChannels = channels, Notes = "The device could not be queried: " + ex.Message };
        }

        lock (_capabilitiesGate)
        {
            _capabilities[key] = capabilities;
        }

        return capabilities;
    }

    public void RestoreQueue()
    {
        try
        {
            if (File.Exists(QueueFile))
            {
                Engine.Queue.Add(PlaylistFile.Load(QueueFile).Where(File.Exists));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            // A damaged queue file is ignored.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _commitTimer.Stop();
        _savePending = true;
        _applyPending = false;
        Commit();
        try
        {
            Directory.CreateDirectory(Store.SettingsDirectory);
            PlaylistFile.Save(QueueFile, Engine.Queue.Items);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing the saved queue is not fatal.
        }

        Engine.Dispose();
    }
}

/// <summary>Metadata reading that never throws into the UI.</summary>
public static class MetadataLoader
{
    public static TrackMetadata ReadSafe(string path, bool probeFormat)
    {
        try
        {
            return MetadataReader.Read(path, probeFormat);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new TrackMetadata { FilePath = path };
        }
    }
}

/// <summary>File and folder pickers provided by the main window.</summary>
public interface IDialogService
{
    Task<IReadOnlyList<string>> PickAudioFilesAsync();

    Task<IReadOnlyList<string>> PickFoldersAsync(string title);

    Task<string?> PickPlaylistAsync();

    Task<string?> PickSavePlaylistAsync(string suggestedName);
}
