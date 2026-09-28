using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using System.Xml;
using System.Xml.Linq;

namespace FUPlayer.Core.Upnp;

/// <summary>How the renderer presents itself and whom it listens to.</summary>
/// <param name="DeviceId">The device's unique id, a GUID kept from one run to the next.</param>
/// <param name="FriendlyName">The name controllers list it under.</param>
/// <param name="Port">TCP port for its HTTP requests; 0 tries the default and then any free one.</param>
/// <param name="AllowNetworkControl">Take orders from other devices on the local network as well as from this computer.</param>
/// <param name="FollowControllerVolume">Let a controller's volume set the player's.</param>
/// <param name="Version">The player's version, for the device description.</param>
public sealed record UpnpRendererOptions(
    string DeviceId, string FriendlyName, int Port, bool AllowNetworkControl, bool FollowControllerVolume, string Version)
{
    /// <summary>
    /// Listen on the loopback address alone and announce nothing, for tests: a socket open to the network makes
    /// Windows ask about its firewall, and a controller would only ever find the renderer through the announcements.
    /// </summary>
    internal bool LoopbackOnly { get; init; }
}

/// <summary>What the renderer is doing, for the page that shows it.</summary>
public sealed record UpnpRendererSnapshot
{
    public required string FriendlyName { get; init; }

    /// <summary>The addresses control points are told to fetch the description from.</summary>
    public required IReadOnlyList<string> DescriptionUrls { get; init; }

    public required string TransportState { get; init; }

    public string? Controller { get; init; }

    public string? Uri { get; init; }

    public StreamMetadata? Metadata { get; init; }

    public string? NextUri { get; init; }

    public string? Error { get; init; }

    public int Subscriptions { get; init; }

    /// <summary>The last few things controllers asked for, newest last.</summary>
    public required IReadOnlyList<string> Recent { get; init; }
}

/// <summary>
/// The player as a UPnP MediaRenderer (DLNA DMR): a device that foobar2000's UPnP output, and any other UPnP or DLNA
/// controller, can find on the network and play to. It offers the three services a renderer needs (AVTransport for
/// play, pause and stop, RenderingControl for the volume, ConnectionManager for the formats it takes), announces
/// itself over SSDP, and sends its state to subscribed controllers as it changes.
/// </summary>
/// <remarks>
/// A controller sends an address, the renderer hands it to the player, and the player fetches the stream over HTTP
/// and plays it through the whole processing chain like any other source. The formats offered are the ones the
/// player decodes natively from a stream (FLAC, WAV, and LPCM as L16 and L24). foobar2000 sends WAV to a renderer it
/// does not know, and FLAC once its list of renderers describes this one: see <see cref="Foobar2000Config"/>.
/// </remarks>
public sealed class UpnpRenderer : IDisposable
{
    /// <summary>The port tried first, so a controller that remembers the device's address still finds it next time.</summary>
    public const int DefaultPort = 58180;

    private const int HistoryLength = 40;

    /// <summary>What the renderer accepts, in ConnectionManager's words.</summary>
    public static readonly string SinkProtocolInfo = string.Join(",",
        "http-get:*:audio/flac:*",
        "http-get:*:audio/x-flac:*",
        "http-get:*:audio/wav:*",
        "http-get:*:audio/x-wav:*",
        "http-get:*:audio/wave:*",
        "http-get:*:audio/L16;rate=44100;channels=2:DLNA.ORG_PN=LPCM",
        "http-get:*:audio/L16;rate=48000;channels=2:DLNA.ORG_PN=LPCM",
        "http-get:*:audio/L16;rate=44100;channels=1:DLNA.ORG_PN=LPCM",
        "http-get:*:audio/L16;rate=48000;channels=1:DLNA.ORG_PN=LPCM",
        "http-get:*:audio/L16:*",
        "http-get:*:audio/L24:*");

    private static readonly HttpClient EventClient = new() { Timeout = TimeSpan.FromSeconds(3) };

    private readonly IUpnpRendererHost _host;
    private readonly UpnpRendererOptions _options;
    private readonly Action<string> _log;
    private readonly string _udn;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<(Subscription Subscription, string Body)> _events = Channel.CreateUnbounded<(Subscription, string)>();
    private readonly List<Subscription> _subscriptions = [];
    private readonly Queue<string> _recent = new();
    private readonly Dictionary<string, string> _evented = new(StringComparer.Ordinal);
    private TcpListener? _listener;
    private SsdpServer? _ssdp;
    private Task[] _loops = [];

    private string _transportState = "NO_MEDIA_PRESENT";
    private string _transportStatus = "OK";
    private string _uri = string.Empty;
    private string _uriMetadata = string.Empty;
    private StreamMetadata? _metadata;
    private string _nextUri = string.Empty;
    private string _nextUriMetadata = string.Empty;
    private string? _controller;
    private string? _error;
    private bool _wantPlaying;
    private long _requestedAt;
    private RendererPlayback _playback;
    private RendererVolume _volume;

    public UpnpRenderer(IUpnpRendererHost host, UpnpRendererOptions options, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(options);
        _host = host;
        _options = options;
        _log = log ?? (_ => { });
        _udn = "uuid:" + (Guid.TryParse(options.DeviceId, out Guid id) ? id : Guid.NewGuid()).ToString("D");
    }

    /// <summary>Raised on a thread of the renderer's own whenever what <see cref="Snapshot"/> reports has changed.</summary>
    public event EventHandler? Changed;

    public int Port { get; private set; }

    public string FriendlyName => _options.FriendlyName;

    public UpnpRendererSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return new UpnpRendererSnapshot
                {
                    FriendlyName = _options.FriendlyName,
                    DescriptionUrls = [.. UpnpHttp.LocalAddresses().Select(DescriptionUrl)],
                    TransportState = _transportState,
                    Controller = _controller,
                    Uri = _uri.Length > 0 ? _uri : null,
                    Metadata = _metadata,
                    NextUri = _nextUri.Length > 0 ? _nextUri : null,
                    Error = _error,
                    Subscriptions = _subscriptions.Count,
                    Recent = [.. _recent],
                };
            }
        }
    }

    /// <summary>Starts listening and announcing. Throws when the network will not let it.</summary>
    public void Start()
    {
        _listener = Listen(_options.Port, _options.LoopbackOnly ? IPAddress.Loopback : IPAddress.Any);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _volume = _host.GetVolume();
        if (!_options.LoopbackOnly)
        {
            _ssdp = new SsdpServer(
                _udn,
                UpnpDescriptions.DeviceType,
                UpnpDescriptions.Services.Select(s => s.Type),
                DescriptionUrl,
                MayControl,
                announce: _options.AllowNetworkControl,
                _log);
            _ssdp.Start();
        }

        _loops = [Task.Run(AcceptLoop), Task.Run(MonitorLoop), Task.Run(EventLoop)];
        Remember($"Listening as \"{_options.FriendlyName}\" on port {Port}");
        _log($"UPnP renderer \"{_options.FriendlyName}\" {_udn} at {string.Join(", ", UpnpHttp.LocalAddresses().Select(DescriptionUrl))}");
    }

    public void Dispose()
    {
        if (_stop.IsCancellationRequested)
        {
            return;
        }

        _stop.Cancel();
        _ssdp?.Dispose();
        _listener?.Stop();
        _events.Writer.TryComplete();
        try
        {
            Task.WaitAll(_loops, 2000);
        }
        catch (AggregateException)
        {
            // The loops end on their cancellation; some of them by throwing it.
        }

        _stop.Dispose();
    }

    private static TcpListener Listen(int port, IPAddress address)
    {
        foreach (int candidate in port > 0 ? [port, DefaultPort, 0] : new[] { DefaultPort, 0 })
        {
            var listener = new TcpListener(address, candidate);
            try
            {
                listener.Start();
                return listener;
            }
            catch (SocketException) when (candidate != 0)
            {
                // Taken by something else: try the next one.
            }
        }

        throw new InvalidOperationException("No TCP port could be opened for the UPnP renderer.");
    }

    private string DescriptionUrl(IPAddress address) => $"http://{address}:{Port}/description.xml";

    /// <summary>
    /// This computer always; other devices on the local network when that is allowed; nothing further away ever,
    /// since a renderer fetches and plays whatever address it is handed.
    /// </summary>
    private bool MayControl(IPAddress address) =>
        UpnpHttp.IsLocalMachine(address) || (_options.AllowNetworkControl && UpnpHttp.IsPrivate(address));

    private async Task AcceptLoop()
    {
        TcpListener? listener = _listener;
        while (listener is not null && !_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = Task.Run(() => Serve(client));
        }
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                NetworkStream stream = client.GetStream();
                var remote = (IPEndPoint)client.Client.RemoteEndPoint!;
                var local = (IPEndPoint)client.Client.LocalEndPoint!;
                UpnpRequest? request = await UpnpHttp.ReadAsync(stream, remote, local, timeout.Token).ConfigureAwait(false);
                if (request is null)
                {
                    return;
                }

                UpnpResponse response = MayControl(remote.Address)
                    ? Handle(request)
                    : UpnpResponse.Empty(403, "Forbidden");
                await UpnpHttp.WriteAsync(stream, response, timeout.Token).ConfigureAwait(false);
                if (response.Status == 200 && request.Method == "SUBSCRIBE" && response.Headers.TryGetValue("SID", out string? sid))
                {
                    SendInitialEvent(sid);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // The control point hung up or took too long; it will ask again.
            }
        }
    }

    private UpnpResponse Handle(UpnpRequest request)
    {
        string path = request.Path.Split('?')[0];
        if (request.Method is "GET" or "HEAD")
        {
            if (path == "/description.xml")
            {
                return UpnpResponse.Xml(UpnpDescriptions.Device(_udn, _options.FriendlyName, _options.Version));
            }

            if (path.StartsWith("/scpd/", StringComparison.Ordinal) && path.EndsWith(".xml", StringComparison.Ordinal)
                && UpnpDescriptions.Scpd(path[6..^4]) is { } scpd)
            {
                return UpnpResponse.Xml(scpd);
            }

            return UpnpResponse.Empty(404, "Not Found");
        }

        if (request.Method == "POST" && path.StartsWith("/control/", StringComparison.Ordinal))
        {
            return Control(path[9..], request);
        }

        if (request.Method is "SUBSCRIBE" or "UNSUBSCRIBE" && path.StartsWith("/event/", StringComparison.Ordinal))
        {
            string service = path[7..];
            if (!UpnpDescriptions.Services.Any(s => s.Name == service))
            {
                return UpnpResponse.Empty(404, "Not Found");
            }

            return request.Method == "SUBSCRIBE" ? Subscribe(service, request) : Unsubscribe(request);
        }

        return UpnpResponse.Empty(405, "Method Not Allowed");
    }

    // Control ------------------------------------------------------------------------------------------------------

    private UpnpResponse Control(string service, UpnpRequest request)
    {
        (string Type, string Id, string Name) description = UpnpDescriptions.Services.FirstOrDefault(s => s.Name == service);
        if (description.Type is null)
        {
            return UpnpResponse.Empty(404, "Not Found");
        }

        string action;
        Dictionary<string, string> arguments;
        try
        {
            (action, arguments) = ParseSoap(request.BodyText, request.Header("SOAPACTION"));
        }
        catch (XmlException)
        {
            return Fault(description.Type, 401, "Invalid Action");
        }

        try
        {
            IReadOnlyList<(string Name, string Value)>? result = service switch
            {
                "AVTransport" => AvTransport(action, arguments, request),
                "RenderingControl" => RenderingControl(action, arguments),
                "ConnectionManager" => ConnectionManager(action, arguments),
                _ => null,
            };

            return result is null ? Fault(description.Type, 401, "Invalid Action") : Success(description.Type, action, result);
        }
        catch (UpnpFault fault)
        {
            Remember($"{action} refused: {fault.Message}");
            return Fault(description.Type, fault.Code, fault.Message);
        }
    }

    private IReadOnlyList<(string, string)>? AvTransport(string action, Dictionary<string, string> arguments, UpnpRequest request)
    {
        switch (action)
        {
            case "SetAVTransportURI":
            {
                string uri = Argument(arguments, "CurrentURI").Trim();
                string metadata = Argument(arguments, "CurrentURIMetaData");
                if (uri.Length > 0 && !IsHttp(uri))
                {
                    throw new UpnpFault(714, "Illegal MIME-type: only http streams can be played");
                }

                StreamMetadata? parsed = StreamMetadata.Parse(metadata);
                bool restart;
                lock (_gate)
                {
                    _controller = DescribeController(request);
                    bool same = uri == _uri;
                    restart = !same && _wantPlaying && uri.Length > 0;
                    _uri = uri;
                    _uriMetadata = metadata;
                    _metadata = parsed;
                    _error = null;
                    _transportStatus = "OK";
                    if (uri.Length == 0)
                    {
                        _wantPlaying = false;
                    }
                }

                Remember($"SetAVTransportURI {Shorten(uri)}{Describe(parsed)} from {DescribeController(request)}");
                if (restart)
                {
                    // Changing the address while playing means playing the new one, which the controller will not
                    // necessarily ask for again.
                    StartPlaying();
                }
                else if (uri.Length > 0)
                {
                    // The same stream with new metadata is how a controller streaming continuously says the next
                    // track has begun; for any other address this does nothing until it is played.
                    _host.UpdateMetadata(uri, parsed);
                }

                if (uri.Length == 0)
                {
                    _host.Stop();
                }

                Refresh();
                return [];
            }

            case "SetNextAVTransportURI":
            {
                string uri = Argument(arguments, "NextURI").Trim();
                if (uri.Length > 0 && !IsHttp(uri))
                {
                    throw new UpnpFault(714, "Illegal MIME-type: only http streams can be played");
                }

                lock (_gate)
                {
                    _nextUri = uri;
                    _nextUriMetadata = Argument(arguments, "NextURIMetaData");
                }

                Remember($"SetNextAVTransportURI {Shorten(uri)}{Describe(StreamMetadata.Parse(_nextUriMetadata))}");
                Refresh();
                return [];
            }

            case "Play":
            {
                lock (_gate)
                {
                    if (_uri.Length == 0)
                    {
                        throw new UpnpFault(701, "Transition not available: no stream has been set");
                    }

                    _controller = DescribeController(request);
                }

                Remember("Play");
                if (_transportState == "PAUSED_PLAYBACK")
                {
                    lock (_gate)
                    {
                        _wantPlaying = true;
                    }

                    _host.Resume();
                }
                else if (_transportState != "PLAYING")
                {
                    StartPlaying();
                }

                Refresh();
                return [];
            }

            case "Pause":
                Remember("Pause");
                if (_transportState is "PLAYING" or "TRANSITIONING")
                {
                    _host.Pause();
                }

                Refresh();
                return [];

            case "Stop":
                Remember("Stop");
                lock (_gate)
                {
                    _wantPlaying = false;
                }

                _host.Stop();
                Refresh();
                return [];

            case "Seek":
                throw new UpnpFault(710, "Seek mode not supported: the player takes streams as they come");

            case "Next" or "Previous":
                throw new UpnpFault(711, "Illegal seek target: the renderer holds one stream at a time");

            case "SetPlayMode":
                return Argument(arguments, "NewPlayMode") == "NORMAL" ? [] : throw new UpnpFault(712, "Play mode not supported");

            case "GetTransportInfo":
                lock (_gate)
                {
                    return [("CurrentTransportState", _transportState), ("CurrentTransportStatus", _transportStatus), ("CurrentSpeed", "1")];
                }

            case "GetPositionInfo":
                lock (_gate)
                {
                    string duration = StreamMetadata.FormatDuration(_metadata?.Duration ?? TimeSpan.Zero);
                    string position = StreamMetadata.FormatDuration(_playback.State == RendererPlaybackState.Stopped ? TimeSpan.Zero : _playback.Position);
                    return
                    [
                        ("Track", _uri.Length > 0 ? "1" : "0"), ("TrackDuration", duration), ("TrackMetaData", _uriMetadata),
                        ("TrackURI", _uri), ("RelTime", position), ("AbsTime", position),
                        ("RelCount", "2147483647"), ("AbsCount", "2147483647"),
                    ];
                }

            case "GetMediaInfo":
                lock (_gate)
                {
                    return
                    [
                        ("NrTracks", _uri.Length > 0 ? "1" : "0"),
                        ("MediaDuration", StreamMetadata.FormatDuration(_metadata?.Duration ?? TimeSpan.Zero)),
                        ("CurrentURI", _uri), ("CurrentURIMetaData", _uriMetadata),
                        ("NextURI", _nextUri), ("NextURIMetaData", _nextUriMetadata),
                        ("PlayMedium", _uri.Length > 0 ? "NETWORK" : "NONE"), ("RecordMedium", "NOT_IMPLEMENTED"),
                        ("WriteStatus", "NOT_IMPLEMENTED"),
                    ];
                }

            case "GetDeviceCapabilities":
                return [("PlayMedia", "NETWORK"), ("RecMedia", "NOT_IMPLEMENTED"), ("RecQualityModes", "NOT_IMPLEMENTED")];

            case "GetTransportSettings":
                return [("PlayMode", "NORMAL"), ("RecQualityMode", "NOT_IMPLEMENTED")];

            case "GetCurrentTransportActions":
                lock (_gate)
                {
                    return [("Actions", TransportActions(_transportState))];
                }

            default:
                return null;
        }
    }

    private IReadOnlyList<(string, string)>? RenderingControl(string action, Dictionary<string, string> arguments)
    {
        RendererVolume volume = _host.GetVolume();
        switch (action)
        {
            case "GetVolume":
                return [("CurrentVolume", VolumePercent(volume).ToString(CultureInfo.InvariantCulture))];

            case "SetVolume":
            {
                if (!int.TryParse(Argument(arguments, "DesiredVolume"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int desired)
                    || desired is < 0 or > 100)
                {
                    throw new UpnpFault(601, "Argument value out of range");
                }

                Remember($"SetVolume {desired}");
                if (_options.FollowControllerVolume && !volume.Fixed)
                {
                    _host.SetVolumeDb(volume.MinimumDb + ((volume.MaximumDb - volume.MinimumDb) * desired / 100.0));
                }

                Refresh();
                return [];
            }

            case "GetVolumeDB":
                return [("CurrentVolume", Fixed256(volume.Fixed ? volume.MaximumDb : volume.Db))];

            case "SetVolumeDB":
            {
                if (!int.TryParse(Argument(arguments, "DesiredVolume"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int desired))
                {
                    throw new UpnpFault(601, "Argument value out of range");
                }

                Remember($"SetVolumeDB {desired / 256.0:0.0} dB");
                if (_options.FollowControllerVolume && !volume.Fixed)
                {
                    _host.SetVolumeDb(desired / 256.0);
                }

                Refresh();
                return [];
            }

            case "GetVolumeDBRange":
                return [("MinValue", Fixed256(volume.MinimumDb)), ("MaxValue", Fixed256(volume.MaximumDb))];

            case "GetMute":
                return [("CurrentMute", volume.Muted ? "1" : "0")];

            case "SetMute":
            {
                string desired = Argument(arguments, "DesiredMute");
                bool mute = desired is "1" or "true" or "True" or "yes";
                Remember(mute ? "Mute" : "Unmute");
                if (_options.FollowControllerVolume)
                {
                    _host.SetMute(mute);
                }

                Refresh();
                return [];
            }

            case "ListPresets":
                return [("CurrentPresetNameList", "FactoryDefaults")];

            case "SelectPreset":
                return [];

            default:
                return null;
        }
    }

    private IReadOnlyList<(string, string)>? ConnectionManager(string action, Dictionary<string, string> arguments) => action switch
    {
        "GetProtocolInfo" => [("Source", string.Empty), ("Sink", SinkProtocolInfo)],
        "GetCurrentConnectionIDs" => [("ConnectionIDs", "0")],
        "GetCurrentConnectionInfo" => Argument(arguments, "ConnectionID") == "0"
            ? [
                ("RcsID", "0"), ("AVTransportID", "0"), ("ProtocolInfo", _metadata?.ProtocolInfo ?? string.Empty),
                ("PeerConnectionManager", string.Empty), ("PeerConnectionID", "-1"), ("Direction", "Input"), ("Status", "OK"),
            ]
            : throw new UpnpFault(706, "Invalid connection reference"),
        _ => null,
    };

    private void StartPlaying()
    {
        string uri;
        StreamMetadata? metadata;
        string controller;
        lock (_gate)
        {
            uri = _uri;
            metadata = _metadata;
            controller = _controller ?? "a UPnP controller";
            _wantPlaying = true;
            _requestedAt = Environment.TickCount64;
            _error = null;
            _transportStatus = "OK";
            _transportState = "TRANSITIONING";
        }

        _host.Play(uri, metadata, controller);
    }

    // State --------------------------------------------------------------------------------------------------------

    private async Task MonitorLoop()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                Refresh();
                ExpireSubscriptions();
                await Task.Delay(250, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Brings the transport state up to date with what the player is doing, plays the next stream when the current
    /// one has ended and a controller left one queued, and tells subscribers what changed.
    /// </summary>
    private void Refresh()
    {
        RendererPlayback playback = _host.GetPlayback(_uri);
        RendererVolume volume = _host.GetVolume();
        bool advance = false;
        bool changed;
        lock (_gate)
        {
            string previous = _transportState;
            string state;
            bool waiting = _wantPlaying && Environment.TickCount64 - _requestedAt < 15_000;
            if (_uri.Length == 0)
            {
                state = "NO_MEDIA_PRESENT";
            }
            else if (playback.State == RendererPlaybackState.Playing)
            {
                state = "PLAYING";
                _requestedAt = 0;
            }
            else if (playback.State == RendererPlaybackState.Paused)
            {
                state = "PAUSED_PLAYBACK";
                _requestedAt = 0;
            }
            else if (waiting && playback.Error is null && _requestedAt != 0)
            {
                state = "TRANSITIONING";
            }
            else
            {
                state = "STOPPED";
                if (_wantPlaying && playback.Error is { } error)
                {
                    _error = error;
                    _transportStatus = "ERROR_OCCURRED";
                    _wantPlaying = false;
                }
                else if (_wantPlaying && previous == "PLAYING" && _nextUri.Length > 0)
                {
                    // The stream ran out and the controller queued the next one, which is what the next address is for.
                    _uri = _nextUri;
                    _uriMetadata = _nextUriMetadata;
                    _metadata = StreamMetadata.Parse(_nextUriMetadata);
                    _nextUri = string.Empty;
                    _nextUriMetadata = string.Empty;
                    advance = true;
                }
                else if (_wantPlaying && _requestedAt == 0)
                {
                    // It was playing and is no longer: the stream ended, or the player was stopped or given
                    // something else to play. Either way it is not coming back on its own.
                    _wantPlaying = false;
                }
                else if (_wantPlaying && !waiting)
                {
                    _error = "The stream did not start in time.";
                    _transportStatus = "ERROR_OCCURRED";
                    _wantPlaying = false;
                }
            }

            _transportState = state;
            _playback = playback;
            _volume = volume;
            changed = QueueChanges(initial: false);
        }

        if (advance)
        {
            Remember($"Next stream: {Shorten(_uri)}");
            StartPlaying();
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private static string TransportActions(string state) => state switch
    {
        "PLAYING" => "Pause,Stop",
        "PAUSED_PLAYBACK" => "Play,Stop",
        "STOPPED" => "Play",
        "TRANSITIONING" => "Stop",
        _ => string.Empty,
    };

    private static int VolumePercent(RendererVolume volume)
    {
        if (volume.Fixed || volume.MaximumDb <= volume.MinimumDb)
        {
            return 100;
        }

        return (int)Math.Round(Math.Clamp((volume.Db - volume.MinimumDb) / (volume.MaximumDb - volume.MinimumDb), 0.0, 1.0) * 100.0);
    }

    private static string Fixed256(double db) =>
        ((int)Math.Clamp(Math.Round(db * 256.0), short.MinValue, short.MaxValue)).ToString(CultureInfo.InvariantCulture);

    // Eventing -----------------------------------------------------------------------------------------------------

    private sealed class Subscription(string sid, string service, Uri[] callbacks, TimeSpan timeout)
    {
        public string Sid { get; } = sid;

        public string Service { get; } = service;

        public Uri[] Callbacks { get; } = callbacks;

        public DateTime Expires { get; set; } = DateTime.UtcNow + timeout;

        public uint Sequence { get; set; }
    }

    private UpnpResponse Subscribe(string service, UpnpRequest request)
    {
        TimeSpan timeout = ParseTimeout(request.Header("TIMEOUT"));
        string? sid = request.Header("SID");
        lock (_gate)
        {
            if (sid is not null)
            {
                Subscription? existing = _subscriptions.FirstOrDefault(s => s.Sid == sid && s.Service == service);
                if (existing is null)
                {
                    return UpnpResponse.Empty(412, "Precondition Failed");
                }

                existing.Expires = DateTime.UtcNow + timeout;
                return Subscribed(existing.Sid, timeout);
            }

            Uri[] callbacks = (request.Header("CALLBACK") ?? string.Empty)
                .Split('<', '>', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(c => Uri.TryCreate(c, UriKind.Absolute, out Uri? uri) && uri.Scheme == "http" ? uri : null)
                .OfType<Uri>()
                .ToArray();
            if (callbacks.Length == 0 || request.Header("NT") != "upnp:event")
            {
                return UpnpResponse.Empty(412, "Precondition Failed");
            }

            var subscription = new Subscription("uuid:" + Guid.NewGuid().ToString("D"), service, callbacks, timeout);
            _subscriptions.Add(subscription);
            Remember($"{DescribeController(request)} subscribed to {service}");
            return Subscribed(subscription.Sid, timeout);
        }
    }

    private static UpnpResponse Subscribed(string sid, TimeSpan timeout)
    {
        var response = new UpnpResponse(200, "OK");
        response.Headers["SID"] = sid;
        response.Headers["TIMEOUT"] = $"Second-{(int)timeout.TotalSeconds}";
        return response;
    }

    private UpnpResponse Unsubscribe(UpnpRequest request)
    {
        string? sid = request.Header("SID");
        lock (_gate)
        {
            int removed = _subscriptions.RemoveAll(s => s.Sid == sid);
            return removed > 0 ? new UpnpResponse(200, "OK") : UpnpResponse.Empty(412, "Precondition Failed");
        }
    }

    private static TimeSpan ParseTimeout(string? header)
    {
        if (header is not null && header.StartsWith("Second-", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(header.AsSpan(7), NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds) && seconds > 0)
        {
            return TimeSpan.FromSeconds(Math.Clamp(seconds, 60, 7200));
        }

        return TimeSpan.FromSeconds(1800);
    }

    private void ExpireSubscriptions()
    {
        lock (_gate)
        {
            _subscriptions.RemoveAll(s => s.Expires < DateTime.UtcNow);
        }
    }

    /// <summary>The whole state of the subscribed service, which is what a new subscriber is sent first.</summary>
    private void SendInitialEvent(string sid)
    {
        lock (_gate)
        {
            Subscription? subscription = _subscriptions.FirstOrDefault(s => s.Sid == sid);
            if (subscription is not null)
            {
                Queue(subscription, Properties(subscription.Service, changedOnly: false));
            }
        }
    }

    /// <summary>Sends each subscriber what changed in its service since the last event. Called under the gate.</summary>
    private bool QueueChanges(bool initial)
    {
        bool any = false;
        foreach ((string Type, string Id, string Name) service in UpnpDescriptions.Services)
        {
            string? body = Properties(service.Name, changedOnly: !initial);
            if (body is null)
            {
                continue;
            }

            any = true;
            foreach (Subscription subscription in _subscriptions.Where(s => s.Service == service.Name))
            {
                Queue(subscription, body);
            }
        }

        return any;
    }

    /// <summary>
    /// The event body for a service: its state variables in a propertyset, the AVTransport and RenderingControl ones
    /// wrapped in a LastChange document as those services require. With <paramref name="changedOnly"/>, only what
    /// differs from what was last sent, and null when nothing does.
    /// </summary>
    private string? Properties(string service, bool changedOnly)
    {
        var values = new List<(string Name, string Value, string? Channel)>();
        switch (service)
        {
            case "AVTransport":
                string duration = StreamMetadata.FormatDuration(_metadata?.Duration ?? TimeSpan.Zero);
                values.AddRange(new (string, string, string?)[]
                {
                    ("TransportState", _transportState, null),
                    ("TransportStatus", _transportStatus, null),
                    ("CurrentTransportActions", TransportActions(_transportState), null),
                    ("PlaybackStorageMedium", _uri.Length > 0 ? "NETWORK" : "NONE", null),
                    ("TransportPlaySpeed", "1", null),
                    ("CurrentPlayMode", "NORMAL", null),
                    ("NumberOfTracks", _uri.Length > 0 ? "1" : "0", null),
                    ("CurrentTrack", _uri.Length > 0 ? "1" : "0", null),
                    ("AVTransportURI", _uri, null),
                    ("AVTransportURIMetaData", _uriMetadata, null),
                    ("CurrentTrackURI", _uri, null),
                    ("CurrentTrackMetaData", _uriMetadata, null),
                    ("CurrentTrackDuration", duration, null),
                    ("CurrentMediaDuration", duration, null),
                    ("NextAVTransportURI", _nextUri, null),
                    ("NextAVTransportURIMetaData", _nextUriMetadata, null),
                });
                break;
            case "RenderingControl":
                values.Add(("Volume", VolumePercent(_volume).ToString(CultureInfo.InvariantCulture), "Master"));
                values.Add(("VolumeDB", Fixed256(_volume.Fixed ? _volume.MaximumDb : _volume.Db), "Master"));
                values.Add(("Mute", _volume.Muted ? "1" : "0", "Master"));
                values.Add(("PresetNameList", "FactoryDefaults", null));
                break;
            default:
                values.Add(("SourceProtocolInfo", string.Empty, null));
                values.Add(("SinkProtocolInfo", SinkProtocolInfo, null));
                values.Add(("CurrentConnectionIDs", "0", null));
                break;
        }

        if (changedOnly)
        {
            values = values.Where(v => !_evented.TryGetValue(service + "/" + v.Name, out string? sent) || sent != v.Value).ToList();
            if (values.Count == 0)
            {
                return null;
            }
        }

        foreach ((string name, string value, _) in values)
        {
            _evented[service + "/" + name] = value;
        }

        var properties = new StringBuilder();
        if (service is "AVTransport" or "RenderingControl")
        {
            string ns = service == "AVTransport" ? "urn:schemas-upnp-org:metadata-1-0/AVT/" : "urn:schemas-upnp-org:metadata-1-0/RCS/";
            var change = new StringBuilder();
            change.Append("<Event xmlns=\"").Append(ns).Append("\"><InstanceID val=\"0\">");
            foreach ((string name, string value, string? channel) in values)
            {
                change.Append('<').Append(name);
                if (channel is not null)
                {
                    change.Append(" channel=\"").Append(channel).Append('"');
                }

                change.Append(" val=\"").Append(UpnpDescriptions.Escape(value)).Append("\"/>");
            }

            change.Append("</InstanceID></Event>");
            properties.Append("<e:property><LastChange>").Append(UpnpDescriptions.Escape(change.ToString())).Append("</LastChange></e:property>");
        }
        else
        {
            foreach ((string name, string value, _) in values)
            {
                properties.Append("<e:property><").Append(name).Append('>').Append(UpnpDescriptions.Escape(value))
                    .Append("</").Append(name).Append("></e:property>");
            }
        }

        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<e:propertyset xmlns:e=\"urn:schemas-upnp-org:event-1-0\">"
            + properties + "</e:propertyset>";
    }

    private void Queue(Subscription subscription, string? body)
    {
        if (body is not null)
        {
            _events.Writer.TryWrite((subscription, body));
        }
    }

    /// <summary>
    /// Delivers events one at a time, in order, so each subscriber sees its sequence numbers count up without gaps.
    /// A subscriber that cannot be reached loses the event; it will renew or subscribe again.
    /// </summary>
    private async Task EventLoop()
    {
        try
        {
            await foreach ((Subscription subscription, string body) in _events.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                uint sequence;
                lock (_gate)
                {
                    sequence = subscription.Sequence;
                    subscription.Sequence = subscription.Sequence == uint.MaxValue ? 1 : subscription.Sequence + 1;
                }

                foreach (Uri callback in subscription.Callbacks)
                {
                    try
                    {
                        using var request = new HttpRequestMessage(new HttpMethod("NOTIFY"), callback)
                        {
                            Content = new StringContent(body, Encoding.UTF8, "text/xml"),
                        };
                        request.Headers.TryAddWithoutValidation("NT", "upnp:event");
                        request.Headers.TryAddWithoutValidation("NTS", "upnp:propchange");
                        request.Headers.TryAddWithoutValidation("SID", subscription.Sid);
                        request.Headers.TryAddWithoutValidation("SEQ", sequence.ToString(CultureInfo.InvariantCulture));
                        using HttpResponseMessage response = await EventClient.SendAsync(request, _stop.Token).ConfigureAwait(false);
                        break;
                    }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                    {
                        // Try the subscriber's next callback, if it gave one.
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // SOAP ---------------------------------------------------------------------------------------------------------

    private sealed class UpnpFault(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }

    private static (string Action, Dictionary<string, string> Arguments) ParseSoap(string body, string? soapAction)
    {
        var document = XDocument.Parse(body);
        XElement? call = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "Body")?.Elements().FirstOrDefault();
        string action = call?.Name.LocalName
            ?? soapAction?.Trim('"').Split('#').LastOrDefault()
            ?? throw new XmlException("No action.");
        var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
        if (call is not null)
        {
            foreach (XElement argument in call.Elements())
            {
                arguments[argument.Name.LocalName] = argument.Value;
            }
        }

        return (action, arguments);
    }

    private static string Argument(Dictionary<string, string> arguments, string name) =>
        arguments.TryGetValue(name, out string? value) ? value : string.Empty;

    private static UpnpResponse Success(string serviceType, string action, IReadOnlyList<(string Name, string Value)> result)
    {
        var xml = new StringBuilder();
        xml.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
        xml.Append("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">");
        xml.Append("<s:Body><u:").Append(action).Append("Response xmlns:u=\"").Append(serviceType).Append("\">");
        foreach ((string name, string value) in result)
        {
            xml.Append('<').Append(name).Append('>').Append(UpnpDescriptions.Escape(value)).Append("</").Append(name).Append('>');
        }

        xml.Append("</u:").Append(action).Append("Response></s:Body></s:Envelope>");
        return UpnpResponse.Xml(xml.ToString());
    }

    private static UpnpResponse Fault(string serviceType, int code, string description)
    {
        string xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
            + "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">"
            + "<s:Body><s:Fault><faultcode>s:Client</faultcode><faultstring>UPnPError</faultstring><detail>"
            + "<UPnPError xmlns=\"urn:schemas-upnp-org:control-1-0\"><errorCode>" + code.ToString(CultureInfo.InvariantCulture)
            + "</errorCode><errorDescription>" + UpnpDescriptions.Escape(description) + "</errorDescription></UPnPError>"
            + "</detail></s:Fault></s:Body></s:Envelope>";
        return new UpnpResponse(500, "Internal Server Error", "text/xml; charset=\"utf-8\"", Encoding.UTF8.GetBytes(xml));
    }

    // Reporting ----------------------------------------------------------------------------------------------------

    private static bool IsHttp(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed) && parsed.Scheme is "http" or "https";

    /// <summary>The program that sent a request, from its User-Agent, and where it is.</summary>
    private static string DescribeController(UpnpRequest request)
    {
        string agent = request.Header("User-Agent") ?? string.Empty;
        string product = agent.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.Split('/')[0])
            .FirstOrDefault(name => name.Length > 0 && !name.Equals("UPnP", StringComparison.OrdinalIgnoreCase)
                && !name.StartsWith("DLNA", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("Windows", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("Microsoft-Windows", StringComparison.OrdinalIgnoreCase))
            ?? "A UPnP controller";
        IPAddress address = request.Remote.Address.IsIPv4MappedToIPv6 ? request.Remote.Address.MapToIPv4() : request.Remote.Address;
        string where = UpnpHttp.IsLocalMachine(address) ? "on this computer" : $"at {address}";
        return $"{product} {where}";
    }

    private static string Describe(StreamMetadata? metadata) =>
        metadata is { HasTrack: true } ? $" ({string.Join(" · ", new[] { metadata.Title, metadata.Artist }.Where(s => !string.IsNullOrWhiteSpace(s)))})" : string.Empty;

    private static string Shorten(string uri) => uri.Length > 96 ? uri[..93] + "…" : uri;

    private void Remember(string line)
    {
        string entry = $"{DateTime.Now:HH:mm:ss}  {line}";
        lock (_gate)
        {
            _recent.Enqueue(entry);
            while (_recent.Count > HistoryLength)
            {
                _recent.Dequeue();
            }
        }

        _log("UPnP: " + line);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
