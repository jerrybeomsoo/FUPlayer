using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
namespace FUPlayer.Core.NetworkDsp;

/// <summary>Resources for active DSP sessions. A waiting listener does not load models or initialise a GPU.</summary>
public sealed record DspServerOptions
{
    /// <summary>Interface to bind; Internet peers additionally require the access key and explicit opt-in.</summary>
    public IPAddress ListenAddress { get; init; } = IPAddress.Any;
    /// <summary>Accept public IPv4 peers using authenticated encrypted UDP. Requires a 256-bit access key.</summary>
    public bool AllowInternet { get; init; }
    /// <summary>Base64 256-bit access key; empty selects the private-LAN plain transport.</summary>
    public string AccessKey { get; init; } = string.Empty;
    /// <summary>UDP listener port, or zero for a test-selected ephemeral port.</summary>
    public int Port { get; init; } = 58181;
    /// <summary>Maximum simultaneous sessions accepted by this listener.</summary>
    public int MaximumClients { get; init; } = 2;
    /// <summary>Helper threads available to an active filter; the calling thread is additional.</summary>
    public int Threads { get; init; } = Math.Clamp(Environment.ProcessorCount - 1, 0, 63);
    /// <summary>Convolution API/device identifier; empty selects a native API automatically.</summary>
    public string GpuDevice { get; init; } = string.Empty;
    /// <summary>Allow numerically checked GPU convolution while a client is processing.</summary>
    public bool GpuAcceleration { get; init; } = true;
    /// <summary>Use 64-bit device arithmetic where supported.</summary>
    public bool GpuHighPrecision { get; init; } = true;
    /// <summary>Override timing decisions while retaining numerical checks.</summary>
    public bool GpuForce { get; init; }
    /// <summary>Requested GPU phase percentage, or minus one for measured sharing.</summary>
    public int GpuPhaseShare { get; init; } = -1;
    /// <summary>Revisit CPU/GPU work division during playback.</summary>
    public bool GpuRetime { get; init; } = true;
    /// <summary>Server neural restorer device; empty selects its CPU provider.</summary>
    public string RestorerDevice { get; init; } = string.Empty;
    /// <summary>Server neural upscaler device; empty selects its CPU provider.</summary>
    public string UpscalerDevice { get; init; } = string.Empty;
    /// <summary>Unresponsive sessions release resources after this interval.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>A portable worker exits after its one client closes, returning all native allocations to the OS.</summary>
    public bool ExitAfterSession { get; init; }
    /// <summary>A lightweight service broker redirects clients to isolated worker processes.</summary>
    public Func<CancellationToken, Task<int>>? StartWorker { get; init; }
    /// <summary>Relay worker datagrams through the listener, so NAT needs only one external UDP port.</summary>
    public bool RelayWorkers { get; init; }
}

/// <summary>Plain UDP DSP for trusted private networks, with ordered loss recovery and per-session stage selection.</summary>
public sealed class DspServer : IAsyncDisposable
{
    private readonly DspServerOptions _options;
    private readonly Socket _socket;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<(IPEndPoint Endpoint, Guid Id), Session> _clients = new();
    private readonly ConcurrentDictionary<(IPEndPoint Endpoint, Guid Id), DspWorkerRelay> _relays = new();
    private readonly DspDatagramProtection? _protection;
    private Task? _running;
    private int _started;
    private sealed record Session(UdpDspPeer Peer, TaskCompletionSource Finished);
    /// <summary>The UDP endpoint actually bound, including an ephemeral test/worker port.</summary>
    public IPEndPoint Endpoint => (IPEndPoint)_socket.LocalEndPoint!;
    /// <summary>Sessions with allocated or opening DSP resources.</summary>
    public int ActiveSessions => _clients.Count + _relays.Count;
    /// <summary>Lifecycle messages exclude audio and user credentials.</summary>
    public event Action<string>? Message;
    /// <summary>Binds the listener without loading any neural model or opening a GPU context.</summary>
    public DspServer(DspServerOptions options)
    {
        if (options.Port is < 0 or > 65535 || options.MaximumClients is < 1 or > 16 || options.IdleTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options));
        _options = options;
        if (options.AllowInternet && string.IsNullOrWhiteSpace(options.AccessKey))
            throw new ArgumentException("Internet DSP requires a server access key.", nameof(options));
        if (!string.IsNullOrWhiteSpace(options.AccessKey)) _protection = new(options.AccessKey, server: true);
        _socket = new Socket(options.ListenAddress.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        _socket.ReceiveBufferSize = _socket.SendBufferSize = 4 << 20;
        _socket.Bind(new IPEndPoint(options.ListenAddress, options.Port));
    }
    /// <summary>Waits on the UDP socket until stopped. Only private/link-local/loopback peers can open sessions.</summary>
    public Task RunAsync(CancellationToken cancellation = default)
    {
        if (_running is not null) throw new InvalidOperationException("DSP server is already running.");
        return _running = ListenAsync(cancellation);
    }
    private async Task ListenAsync(CancellationToken cancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _stop.Token);
        if (_options.ExitAfterSession) _ = ExpireUnusedWorker();
        var buffer = new byte[UdpDspPeer.Header + UdpDspPeer.Payload + DspDatagramProtection.Overhead + 1];
        try
        {
            while (!linked.IsCancellationRequested)
            {
                EndPoint any = _socket.AddressFamily == AddressFamily.InterNetwork ? new IPEndPoint(IPAddress.Any, 0) : new IPEndPoint(IPAddress.IPv6Any, 0);
                SocketReceiveFromResult received;
                try { received = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, any, linked.Token).ConfigureAwait(false); }
                catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize) { continue; }
                var endpoint = (IPEndPoint)received.RemoteEndPoint;
                if (!_options.AllowInternet && !UdpDspPeer.IsLan(endpoint.Address)) continue;
                ReadOnlyMemory<byte> wire = buffer.AsMemory(0, received.ReceivedBytes);
                Guid route;
                if (_protection is null)
                { if (!UdpDspPeer.Inspect(wire.Span, out route, out _, out _)) continue; }
                else if (!DspDatagramProtection.Inspect(wire.Span, out route)) continue;
                ReadOnlyMemory<byte> data = wire;
                if (_protection is not null)
                { if (!_protection.TryUnprotect(wire.Span, out byte[] decoded)) continue; data = decoded; }
                if (!UdpDspPeer.Inspect(data.Span, out Guid id, out byte kind, out DspMessage type)) continue;
                var key = (endpoint, id);
                // Authenticate control packets before forwarding; a forged close must not release a live worker.
                if (_relays.TryGetValue(key, out DspWorkerRelay? relay)) { relay.Accept(wire.Span, kind == 3); continue; }
                if (_options.RelayWorkers && _options.StartWorker is not null)
                {
                    if (kind != 0 || type != DspMessage.Hello || ActiveSessions >= _options.MaximumClients) continue;
                    relay = new DspWorkerRelay(_options.StartWorker, packet => _socket.SendTo(packet, endpoint), _options.IdleTimeout);
                    if (!_relays.TryAdd(key, relay)) { relay.Dispose(); continue; }
                    relay.Accept(wire.Span);
                    DspWorkerRelay capturedRelay = relay;
                    _ = Task.Run(async () =>
                    {
                        try { await capturedRelay.RunAsync(linked.Token).ConfigureAwait(false); }
                        catch (Exception ex) when (ex is not (OutOfMemoryException or AccessViolationException))
                        { if (!linked.IsCancellationRequested) Message?.Invoke($"DSP relay ended: {ex.Message}"); }
                        finally { _relays.TryRemove(key, out _); capturedRelay.Dispose(); }
                    }, CancellationToken.None);
                    continue;
                }
                if (!_clients.TryGetValue(key, out Session? session))
                {
                    if (kind != 0 || type != DspMessage.Hello || _clients.Count >= _options.MaximumClients) continue;
                    var peer = new UdpDspPeer(id, packet => _socket.SendTo(_protection?.Protect(packet) ?? packet, endpoint), server: true, protectedTransport: _protection is not null);
                    session = new Session(peer, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                    if (!_clients.TryAdd(key, session)) { peer.Dispose(); continue; }
                    Interlocked.Increment(ref _started);
                    Session captured = session;
                    _ = Task.Run(() => Handle(key, captured, linked.Token), CancellationToken.None);
                }
                session.Peer.Accept(data.ToArray());
                if (kind == 3 && _options.ExitAfterSession) { _stop.Cancel(); break; }
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (linked.IsCancellationRequested) { }
        finally
        {
            Session[] remaining = _clients.Values.ToArray();
            foreach (Session session in remaining) session.Peer.Dispose();
            DspWorkerRelay[] relays = _relays.Values.ToArray();
            foreach (DspWorkerRelay relay in relays) relay.Dispose();
            await Task.WhenAll(relays.Select(relay => relay.Finished)).ConfigureAwait(false);
            // Isolated workers must also exit while a cancelled client was still designing a very long filter.
            if (!_options.ExitAfterSession) await Task.WhenAll(remaining.Select(s => s.Finished.Task)).ConfigureAwait(false);
        }
    }
    private async Task ExpireUnusedWorker()
    {
        try
        {
            long launched = System.Diagnostics.Stopwatch.GetTimestamp();
            while (!_stop.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), _stop.Token).ConfigureAwait(false);
                if (Volatile.Read(ref _started) == 0
                    ? System.Diagnostics.Stopwatch.GetElapsedTime(launched) > _options.IdleTimeout
                    : _clients.Values.All(session => session.Peer.IdleFor > _options.IdleTimeout))
                    _stop.Cancel();
            }
        }
        catch (OperationCanceledException) { }
    }
    private void Handle((IPEndPoint Endpoint, Guid Id) key, Session session, CancellationToken cancellation)
    {
        long sequence = 1;
        try
        {
            var hello = session.Peer.Receive(TimeSpan.FromSeconds(5), cancellation);
            if (hello.Type != DspMessage.Hello || hello.Sequence != 1) throw new InvalidDataException("Expected LAN DSP hello.");
            if (_options.StartWorker is not null)
            {
                int port = _options.StartWorker(cancellation).GetAwaiter().GetResult();
                session.Peer.Send(DspMessage.Redirect, 1, BitConverter.GetBytes(port));
                _ = session.Peer.Receive(_options.IdleTimeout, cancellation);
                return;
            }
            using var stages = new DspStageProcessor(DspProtocol.Parse<DspHello>(hello.Bytes), _options);
            session.Peer.Send(DspMessage.Hello, 1, DspProtocol.Json(stages.Metadata));
            Message?.Invoke($"DSP session opened from {key.Endpoint}.");
            while (!cancellation.IsCancellationRequested)
            {
                var frame = session.Peer.Receive(_options.IdleTimeout, cancellation);
                if (frame.Sequence != ++sequence) throw new InvalidDataException("DSP stage sequence is invalid.");
                byte[] result = stages.Process(frame.Type, frame.Bytes);
                session.Peer.Send(frame.Type, sequence, result);
            }
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or AccessViolationException))
        {
            if (ex is not OperationCanceledException)
            {
                Message?.Invoke($"DSP session ended: {ex.Message}");
                try { session.Peer.Send(DspMessage.Error, sequence, Encoding.UTF8.GetBytes(ex.Message)); }
                catch (Exception send) when (send is SocketException or ObjectDisposedException) { }
            }
        }
        finally
        {
            session.Peer.Dispose(); _clients.TryRemove(key, out _); session.Finished.TrySetResult();
            if (_options.ExitAfterSession) _stop.Cancel();
        }
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_running is not null) await _running.ConfigureAwait(false);
        _socket.Dispose(); _protection?.Dispose(); _stop.Dispose();
    }
}
