using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

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

    /// <summary>Server neural restorer adapter; empty runs it on the processor.</summary>
    public string RestorerDevice { get; init; } = string.Empty;

    /// <summary>Server neural upscaler adapter; empty runs it on the processor.</summary>
    public string UpscalerDevice { get; init; } = string.Empty;

    /// <summary>Unresponsive sessions release resources after this interval.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>A portable worker exits after its one client closes, returning all native allocations to the OS.</summary>
    public bool ExitAfterSession { get; init; }

    /// <summary>Starts an isolated worker process for a new session and returns its loopback port; the listener relays to it.</summary>
    public Func<CancellationToken, Task<int>>? StartWorker { get; init; }
}

/// <summary>
/// The UDP listener of a DSP server. Sessions are opened by a player's first datagram. A listener with a worker
/// starter relays each session to an isolated worker process on a loopback port, so the one public port is all a
/// router has to forward and an idle server holds no models or devices; without one, as a worker itself or in tests,
/// it serves sessions in this process. Plain sessions are taken from private networks only; with an access key every
/// datagram is authenticated and encrypted, and public addresses may be allowed.
/// </summary>
public sealed class DspServer : IAsyncDisposable
{
    private readonly DspServerOptions _options;
    private readonly Socket _socket;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<(IPEndPoint Endpoint, Guid Id), Session> _sessions = new();
    private readonly ConcurrentDictionary<(IPEndPoint Endpoint, Guid Id), DspWorkerRelay> _relays = new();
    private readonly DspDatagramProtection? _protection;
    private Task? _running;
    private int _started;

    private sealed record Session(DspLink Link, Task Serving);

    /// <summary>Binds the listener without loading any neural model or opening a GPU context.</summary>
    public DspServer(DspServerOptions options)
    {
        if (options.Port is < 0 or > 65535 || options.MaximumClients is < 1 or > 16 || options.IdleTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (options.AllowInternet && string.IsNullOrWhiteSpace(options.AccessKey))
        {
            throw new ArgumentException("Internet DSP requires a server access key.", nameof(options));
        }

        _options = options;
        if (!string.IsNullOrWhiteSpace(options.AccessKey))
        {
            _protection = new DspDatagramProtection(options.AccessKey, server: true);
        }

        _socket = new Socket(options.ListenAddress.AddressFamily, SocketType.Dgram, ProtocolType.Udp)
        {
            ReceiveBufferSize = 8 << 20,
            SendBufferSize = 8 << 20,
        };
        DspEndpoint.IgnoreConnectionReset(_socket);
        _socket.Bind(new IPEndPoint(options.ListenAddress, options.Port));
    }

    /// <summary>The UDP endpoint actually bound, including an ephemeral test/worker port.</summary>
    public IPEndPoint Endpoint => (IPEndPoint)_socket.LocalEndPoint!;

    /// <summary>Sessions with allocated or opening DSP resources.</summary>
    public int ActiveSessions => _sessions.Count + _relays.Count;

    /// <summary>Lifecycle messages exclude audio and user credentials.</summary>
    public event Action<string>? Message;

    /// <summary>Waits on the UDP socket until stopped, or, for a worker, until its session is over.</summary>
    public Task RunAsync(CancellationToken cancellation = default)
    {
        if (_running is not null)
        {
            throw new InvalidOperationException("DSP server is already running.");
        }

        return _running = ListenAsync(cancellation);
    }

    private async Task ListenAsync(CancellationToken cancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _stop.Token);
        if (_options.ExitAfterSession)
        {
            _ = ExpireUnusedWorker();
        }

        var buffer = new byte[2048];
        try
        {
            while (!linked.IsCancellationRequested)
            {
                EndPoint any = _socket.AddressFamily == AddressFamily.InterNetwork ? new IPEndPoint(IPAddress.Any, 0) : new IPEndPoint(IPAddress.IPv6Any, 0);
                SocketReceiveFromResult received;
                try
                {
                    received = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, any, linked.Token).ConfigureAwait(false);
                }
                catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize)
                {
                    continue;
                }

                var endpoint = (IPEndPoint)received.RemoteEndPoint;
                if (!_options.AllowInternet && !DspEndpoint.IsLan(endpoint.Address))
                {
                    continue;
                }

                ReadOnlySpan<byte> wire = buffer.AsSpan(0, received.ReceivedBytes);
                byte[] plain;
                if (_protection is null)
                {
                    plain = wire.ToArray();
                }
                else if (!_protection.TryUnprotect(wire, out plain))
                {
                    continue;
                }

                if (!DspLink.Inspect(plain, out Guid id, out bool opens))
                {
                    continue;
                }

                var key = (endpoint, id);
                if (_relays.TryGetValue(key, out DspWorkerRelay? relay))
                {
                    // The worker authenticates the datagrams itself; the listener has checked them before passing
                    // them on, so a forged close cannot release a live worker.
                    relay.Accept(wire, DspLink.IsClose(plain));
                    continue;
                }

                if (_sessions.TryGetValue(key, out Session? session))
                {
                    session.Link.Accept(plain);
                    continue;
                }

                if (!opens || ActiveSessions >= _options.MaximumClients)
                {
                    continue;
                }

                if (_options.StartWorker is not null)
                {
                    relay = new DspWorkerRelay(_options.StartWorker, packet => _socket.SendTo(packet, endpoint), _options.IdleTimeout);
                    if (!_relays.TryAdd(key, relay))
                    {
                        relay.Dispose();
                        continue;
                    }

                    relay.Accept(wire);
                    DspWorkerRelay started = relay;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await started.RunAsync(linked.Token).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is not (OutOfMemoryException or AccessViolationException))
                        {
                            if (!linked.IsCancellationRequested)
                            {
                                Message?.Invoke($"DSP relay ended: {ex.Message}");
                            }
                        }
                        finally
                        {
                            _relays.TryRemove(key, out _);
                            started.Dispose();
                        }
                    }, CancellationToken.None);
                    continue;
                }

                DspDatagramProtection? protection = _protection;
                var link = new DspLink(id, datagram =>
                {
                    byte[] packet = protection?.Protect(datagram) ?? datagram;
                    _socket.SendTo(packet, endpoint);
                }, protectedTransport: protection is not null)
                {
                    Timeout = _options.IdleTimeout,
                };
                Interlocked.Increment(ref _started);
                CancellationToken token = linked.Token;
                var serving = new Task(() => Serve(key, link, token), TaskCreationOptions.LongRunning);
                if (!_sessions.TryAdd(key, new Session(link, serving)))
                {
                    link.Dispose();
                    continue;
                }

                link.Accept(plain);
                serving.Start();
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (linked.IsCancellationRequested)
        {
        }
        finally
        {
            Session[] remaining = _sessions.Values.ToArray();
            foreach (Session session in remaining)
            {
                session.Link.Dispose();
            }

            DspWorkerRelay[] relays = _relays.Values.ToArray();
            foreach (DspWorkerRelay relay in relays)
            {
                relay.Dispose();
            }

            await Task.WhenAll(relays.Select(relay => relay.Finished)).ConfigureAwait(false);

            // A session finishes the block it is processing before it releases its models and devices. Waiting for
            // that, rather than leaving the process to exit under it, is what keeps a network from being torn down
            // mid-call. A long filter design that will not notice the closed link gets half a minute.
            await Task.WhenAny(Task.WhenAll(remaining.Select(s => s.Serving)), Task.Delay(TimeSpan.FromSeconds(30))).ConfigureAwait(false);
        }
    }

    private void Serve((IPEndPoint Endpoint, Guid Id) key, DspLink link, CancellationToken cancellation)
    {
        try
        {
            Message?.Invoke($"DSP session from {key.Endpoint}.");
            DspSession.Serve(link, _options, Message, cancellation);
        }
        finally
        {
            link.Dispose();
            _sessions.TryRemove(key, out _);
            if (_options.ExitAfterSession)
            {
                _stop.Cancel();
            }
        }
    }

    private async Task ExpireUnusedWorker()
    {
        try
        {
            long launched = Stopwatch.GetTimestamp();
            while (!_stop.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), _stop.Token).ConfigureAwait(false);
                if (Volatile.Read(ref _started) == 0 && Stopwatch.GetElapsedTime(launched) > _options.IdleTimeout)
                {
                    _stop.Cancel();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_running is not null)
        {
            await _running.ConfigureAwait(false);
        }

        _socket.Dispose();
        _protection?.Dispose();
        _stop.Dispose();
    }
}
