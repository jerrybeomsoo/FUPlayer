using System.Reflection;
using Avalonia.Threading;
using FUPlayer.Core.Localization;
using FUPlayer.Core.Settings;
using FUPlayer.Core.Upnp;

namespace FUPlayer.App.Services;

/// <summary>
/// Runs the UPnP renderer while the settings ask for it. A controller's play, pause and stop go to the engine through
/// <see cref="EngineRendererHost"/>; its volume goes to the volume knob, so the knob moves and the setting is kept.
/// </summary>
public sealed class UpnpService : IDisposable
{
    private readonly PlayerServices _services;
    private readonly EngineRendererHost _host;
    private readonly string _logPath;
    private UpnpRenderer? _renderer;
    private (bool Enabled, string Name, int Port, bool Network, bool Volume) _running;

    public UpnpService(PlayerServices services)
    {
        _services = services;
        _logPath = Path.Combine(services.Store.SettingsDirectory, "upnp.log");
        _host = new EngineRendererHost(
            services.Engine,
            () => services.Settings.Volume,
            db => Dispatcher.UIThread.Post(() => VolumeRequested?.Invoke(this, db)));
        _host.MuteChanged += (_, _) => Dispatcher.UIThread.Post(() => Changed?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>Raised on the UI thread when the renderer starts, stops, or reports a change.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised on the UI thread with the volume a controller asked for, which the volume knob takes up.</summary>
    public event EventHandler<double>? VolumeRequested;

    public bool IsRunning => _renderer is not null;

    /// <summary>Why the renderer is not running though it was asked to, or null.</summary>
    public string? StartError { get; private set; }

    public UpnpRendererSnapshot? Snapshot => _renderer?.Snapshot;

    /// <summary>The name used when the setting leaves it empty.</summary>
    public static string DefaultFriendlyName => $"FUPlayer ({Environment.MachineName})";

    public int Port => _renderer?.Port ?? 0;

    /// <summary>Where the renderer writes what controllers asked of it.</summary>
    public string LogPath => _logPath;

    /// <summary>Starts, stops or restarts the renderer to match the settings. Call on the UI thread after they change.</summary>
    public void Apply()
    {
        UpnpSettings settings = _services.Settings.Upnp;
        string name = string.IsNullOrWhiteSpace(settings.FriendlyName) ? DefaultFriendlyName : settings.FriendlyName.Trim();
        var wanted = (settings.Enabled, name, settings.Port, settings.AllowNetworkControl, settings.FollowControllerVolume);
        if (wanted == _running && (_renderer is not null || StartError is not null) == settings.Enabled)
        {
            return;
        }

        StopRenderer();
        _running = wanted;
        StartError = null;
        if (!settings.Enabled)
        {
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        // Made once and saved once the renderer runs, so the device keeps its identity even if nothing else is ever
        // changed. Saving comes last because it calls back in here, which by then finds nothing to do.
        bool newIdentity = string.IsNullOrEmpty(settings.DeviceId);
        if (newIdentity)
        {
            settings.DeviceId = Guid.NewGuid().ToString("D");
        }

        string version = typeof(UpnpService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "1.0";
        var renderer = new UpnpRenderer(
            _host,
            new UpnpRendererOptions(settings.DeviceId, name, settings.Port, settings.AllowNetworkControl, settings.FollowControllerVolume, version),
            Log);
        try
        {
            renderer.Start();
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or InvalidOperationException or IOException)
        {
            renderer.Dispose();
            StartError = Loc.F("The renderer could not start: {0}", ex.Message);
            Log(StartError);
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        renderer.Changed += (_, _) => Dispatcher.UIThread.Post(() => Changed?.Invoke(this, EventArgs.Empty));
        _renderer = renderer;
        Changed?.Invoke(this, EventArgs.Empty);
        if (newIdentity)
        {
            _services.NotifySettingsChanged(applyToEngine: false);
        }
    }

    /// <summary>Undoes a controller's mute from the player's side; the renderer tells the controller.</summary>
    public void Unmute() => _host.SetMute(false);

    public void Dispose()
    {
        StopRenderer();
        _host.Dispose();
    }

    private void StopRenderer()
    {
        UpnpRenderer? renderer = _renderer;
        _renderer = null;
        renderer?.Dispose();
    }

    private static readonly Lock LogGate = new();

    private void Log(string message)
    {
        lock (LogGate)
        {
            try
            {
                var info = new FileInfo(_logPath);
                if (info.Exists && info.Length > 1 << 20)
                {
                    string[] lines = File.ReadAllLines(_logPath);
                    File.WriteAllLines(_logPath, lines[(lines.Length / 2)..]);
                }

                File.AppendAllText(_logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The log is for diagnosis; the renderer works without it.
            }
        }
    }
}
