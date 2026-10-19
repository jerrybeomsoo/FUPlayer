using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FUPlayer.Core.Engine;
using FUPlayer.Core.Localization;
using FUPlayer.Core.Settings;

namespace FUPlayer.Core.NetworkDsp;

/// <summary>
/// The player's end of a DSP session: a run of core stages that a server builds and runs. Blocks are sent as the
/// player makes them and their results come back in the same order some time later; as many are kept in flight as
/// cover a few round trips, so the server always has the next block waiting.
/// </summary>
internal sealed class DspRemoteSegment : IDisposable
{
    private readonly Socket _socket;
    private readonly DspLink _link;
    private readonly DspDatagramProtection? _protection;
    private readonly CancellationTokenSource _receiveStop = new();
    private readonly Task _receiving;
    private readonly int _maximumChannels;
    private readonly int _maximumFrames;
    private readonly int _entryRate;
    private readonly Queue<double> _inFlight = new();
    private readonly IPAddress _address;
    private IPEndPoint _destination;
    private uint _epoch;
    private long _index;
    private double _inFlightSeconds;
    private double _computeSeconds;
    private double _audioSeconds;
    private double _load;
    private int _disposed;

    /// <param name="first">The first stage the server runs.</param>
    /// <param name="last">The last stage it runs.</param>
    /// <param name="maximumFrames">The most frames a block entering the server's stages can have.</param>
    public DspRemoteSegment(PlaybackPlan plan, PlayerSettings settings, CoreLayout layout, CoreStage first, CoreStage last,
        int maximumFrames, CancellationToken cancellation)
    {
        DspEndpoint target = DspEndpoint.Parse(settings.Processing.NetworkDspHost, settings.Processing.NetworkDspPort);
        Host = target.Host;
        First = first;
        Last = last;
        _maximumChannels = Math.Max(layout.SourceChannels, layout.OutputChannels);
        _maximumFrames = (maximumFrames * 16) + 4096;
        _entryRate = layout.RateBefore(first);
        if (!string.IsNullOrWhiteSpace(settings.Processing.NetworkDspKey))
        {
            _protection = new DspDatagramProtection(settings.Processing.NetworkDspKey, server: false);
        }

        IPAddress address;
        try
        {
            address = DspEndpoint.SelectAddress(
                Dns.GetHostAddressesAsync(Host).WaitAsync(TimeSpan.FromSeconds(5), cancellation).GetAwaiter().GetResult(),
                _protection is not null);
        }
        catch
        {
            _protection?.Dispose();
            throw;
        }

        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            ReceiveBufferSize = 8 << 20,
            SendBufferSize = 8 << 20,
        };
        DspEndpoint.IgnoreConnectionReset(_socket);
        _socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        _address = address;
        _destination = new IPEndPoint(address, target.Port);
        Socket socket = _socket;
        DspDatagramProtection? protection = _protection;
        _link = new DspLink(Guid.NewGuid(), datagram =>
        {
            byte[] packet = protection?.Protect(datagram) ?? datagram;
            socket.SendTo(packet, Volatile.Read(ref _destination));
        }, protectedTransport: protection is not null);
        _link.NominalRate = (double)_entryRate * layout.ChannelsBefore(first) * 8 * (layout.DryBefore(first) ? 2 : 1);
        _receiving = Task.Factory.StartNew(Receive, TaskCreationOptions.LongRunning);
        try
        {
            using CancellationTokenRegistration registration = cancellation.Register(Abort);
            var hello = new DspHello(DspProtocol.Version, plan, settings.Restoration, layout, first, last, maximumFrames, 0);
            _link.Send((byte)DspMessage.Hello, DspProtocol.Encode(hello), cancellation);

            // Designing a long filter and loading the networks can take a while; the link's pings show the server is alive.
            if (!_link.TryReceive(out DspLinkMessage reply, TimeSpan.FromSeconds(180), cancellation))
            {
                throw new IOException(Loc.T("No answer. Check the address, the access key and that the UDP port is forwarded."));
            }
            if (reply.Type == (byte)DspMessage.Error)
            {
                throw new IOException(Encoding.UTF8.GetString(reply.Body));
            }

            if (reply.Type != (byte)DspMessage.Ready)
            {
                throw new InvalidDataException("The DSP server answered with something else than its stages.");
            }

            Metadata = DspProtocol.Decode<DspReady>(reply.Body);
            _link.PingSoon();
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or AccessViolationException))
        {
            Dispose();
            if (ex is OperationCanceledException)
            {
                throw;
            }

            string reason = _link.TimedOut && Metadata is null
                ? Loc.T("No answer. Check the address, the access key and that the UDP port is forwarded.")
                : ex.Message;
            throw new IOException(Loc.F("DSP server {0} could not be opened: {1}", Host, reason), ex);
        }
    }

    public string Host { get; }

    public CoreStage First { get; }

    public CoreStage Last { get; }

    public DspReady Metadata { get; } = null!;

    /// <summary>What Now playing says the processing is on.</summary>
    public string Summary => Loc.F("DSP server {0} over UDP", Host);

    public double RoundTripMilliseconds => _link.RoundTripMilliseconds;

    public long RetransmittedDatagrams => _link.RetransmittedDatagrams;

    /// <summary>Seconds of audio sent and not yet back.</summary>
    public double InFlightSeconds => Volatile.Read(ref _inFlightSeconds);

    /// <summary>Blocks sent and not yet back.</summary>
    public int InFlight
    {
        get
        {
            lock (_inFlight)
            {
                return _inFlight.Count;
            }
        }
    }

    /// <summary>
    /// The server's processing time over the audio it processed, over the last few seconds: above one, it cannot keep
    /// up, whatever the network does. Zero until the first results.
    /// </summary>
    public double ServerLoad => Volatile.Read(ref _load);

    /// <summary>The exciter's state on the server, from the latest result.</summary>
    public ExciterReport? Exciter { get; private set; }

    /// <summary>Sends a block as it stands in front of <see cref="First"/>.</summary>
    public void Submit(CoreBlock block, CancellationToken cancellation = default)
    {
        double seconds = block.Counts[0] / (double)_entryRate;
        _link.Send((byte)DspMessage.Audio, DspProtocol.EncodeAudio(_epoch, _index++, block), cancellation);
        lock (_inFlight)
        {
            _inFlight.Enqueue(seconds);
            _inFlightSeconds += seconds;
        }
    }

    /// <summary>
    /// The next result, as it stands after <see cref="Last"/>, waiting up to <paramref name="timeout"/>; false when
    /// none came in that time. Results from before a reset are skipped.
    /// </summary>
    public bool TryTake(CoreBlock into, TimeSpan timeout)
    {
        long until = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (true)
        {
            TimeSpan left = TimeSpan.FromSeconds(Math.Max(0, (until - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency));
            if (!_link.TryReceive(out DspLinkMessage message, left))
            {
                return false;
            }

            if (message.Type == (byte)DspMessage.Error)
            {
                throw new IOException(Loc.F("DSP server {0}: {1}", Host, Encoding.UTF8.GetString(message.Body)));
            }

            if (message.Type != (byte)DspMessage.Result)
            {
                throw new InvalidDataException("The DSP server sent something else than a result.");
            }

            (uint epoch, _, double compute, ExciterReport? exciter) = DspProtocol.DecodeResult(message.Body, into, _maximumChannels, _maximumFrames);
            if (epoch != _epoch)
            {
                continue;
            }

            double seconds;
            lock (_inFlight)
            {
                seconds = _inFlight.Count > 0 ? _inFlight.Dequeue() : 0;
                _inFlightSeconds = Math.Max(0, _inFlightSeconds - seconds);
            }

            Account(seconds, compute);
            Exciter = exciter ?? Exciter;
            return true;
        }
    }

    /// <summary>Starts the server's stages again from silence; results of blocks sent before are dropped as they arrive.</summary>
    public void Reset()
    {
        _epoch++;
        _index = 0;
        lock (_inFlight)
        {
            _inFlight.Clear();
            _inFlightSeconds = 0;
        }

        _link.Send((byte)DspMessage.Reset, DspProtocol.EncodeEpoch(_epoch));
    }

    /// <summary>Unblocks anything waiting on the session; used when playback stops while the server is busy.</summary>
    public void Abort()
    {
        _link.Dispose();
        try
        {
            _receiveStop.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (!_link.IsClosed)
            {
                _link.Send((byte)DspMessage.Bye, []);
                _link.Drain(TimeSpan.FromMilliseconds(300));
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
        }

        Abort();
        _socket.Dispose();
        try
        {
            _receiving.Wait(1000);
        }
        catch (AggregateException)
        {
        }

        _receiveStop.Dispose();
        _protection?.Dispose();
    }

    /// <summary>The server's load: its processing seconds over the audio seconds, over about the last three seconds of audio.</summary>
    private void Account(double audio, double compute)
    {
        _audioSeconds += audio;
        _computeSeconds += compute;
        if (_audioSeconds >= 0.25)
        {
            double load = _computeSeconds / _audioSeconds;
            double previous = Volatile.Read(ref _load);
            Volatile.Write(ref _load, previous == 0 ? load : (0.9 * previous) + (0.1 * load));
            _audioSeconds = 0;
            _computeSeconds = 0;
        }
    }

    private void Receive()
    {
        Socket socket = _socket;
        var buffer = new byte[2048];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        CancellationToken stop = _receiveStop.Token;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                int received = socket.ReceiveFrom(buffer, ref any);
                var source = (IPEndPoint)any;
                ReadOnlySpan<byte> wire = buffer.AsSpan(0, received);
                if (_protection is null)
                {
                    if (source.Address.Equals(_address))
                    {
                        _link.Accept(wire);
                    }
                }
                else if (_protection.TryUnprotect(wire, out byte[] plain)
                    && DspLink.Inspect(plain, out Guid session, out _) && session == _link.Session)
                {
                    // Some routers hand a reply to a public address back into the LAN from another source address.
                    // Only an authenticated reply for this unguessable session may move where the player sends.
                    Volatile.Write(ref _destination, source);
                    _link.Accept(plain);
                }
            }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
        }
    }
}
