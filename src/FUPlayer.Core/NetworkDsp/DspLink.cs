using System.Buffers.Binary;
using System.Diagnostics;
using System.Threading.Channels;

namespace FUPlayer.Core.NetworkDsp;

/// <summary>A message received on a <see cref="DspLink"/>.</summary>
internal readonly record struct DspLinkMessage(byte Type, byte[] Body);

/// <summary>
/// A reliable, ordered stream of messages between a player and a DSP server over UDP datagrams.
///
/// Each datagram of data carries a packet number. The receiver acknowledges the packets it holds, all of them up to
/// one number and a bitmap of those after it, every few packets and whenever one arrives out of order; the sender
/// sends a packet again once three later ones have been acknowledged without it, or once it has waited a
/// retransmission timeout for the oldest. As many packets are in flight as a window allows, so a processing server is
/// never left waiting a round trip for its next block, and they leave at a paced rate, which falls by a quarter when
/// packets go missing and climbs back while none do, so that a burst of processed audio does not overrun a router's
/// queue. Pings measure the round trip and keep an idle link alive.
///
/// A message is a type byte and a body. It starts a packet of its own and runs over as many as it needs.
/// </summary>
internal sealed class DspLink : IDisposable
{
    internal const int Header = 32;

    /// <summary>Payload of a plain datagram: 1,432 bytes in all, under a 1,492-byte PPPoE path with room to spare.</summary>
    internal const int PlainPayload = 1400;

    /// <summary>Payload when the datagram is encrypted, which adds <see cref="DspDatagramProtection.Overhead"/> bytes.</summary>
    internal const int ProtectedPayload = 1340;

    internal const uint Magic = 0x34505546;
    internal const int MaximumMessage = 64 << 20;

    private const byte Data = 0, Ack = 1, Ping = 2, Pong = 3, Close = 4;
    private const byte FirstOfMessage = 1;
    private const int AckEvery = 8;
    private const int MaximumBitmapPackets = 8192;
    private const int Batch = 64;

    private readonly Action<byte[]> _transmit;
    private readonly int _payload;
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _sender;
    private readonly Channel<DspLinkMessage> _incoming = Channel.CreateUnbounded<DspLinkMessage>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    // Sending, under _gate.
    private readonly object _gate = new();
    private readonly Queue<Packet> _ready = new();
    private readonly SortedDictionary<long, Packet> _unacked = new();
    private readonly Queue<Packet> _lost = new();
    // Only the sender thread uses this list. Empty idle polls must not allocate a fresh batch.
    private readonly List<byte[]> _sendBatch = new(Batch);
    private long _nextPacket;
    private long _unackedBytes;
    private long _queuedBytes;
    private long _highestAcked = -1;
    private double _nominalRate = 2e6;
    private double _rate = 8e6;
    private double _tokens;
    private long _lastRefill = Stopwatch.GetTimestamp();
    private long _lastIncrease = Stopwatch.GetTimestamp();
    private long _quietUntil;
    private int _timeouts;
    private double _srtt;
    private double _rttVariance;

    // Receiving, under _receiveGate: the thread that calls Accept, and the sender's delayed acknowledgements.
    private readonly object _receiveGate = new();
    private readonly Dictionary<long, Packet> _outOfOrder = new();
    private long _expected;
    private long _highestReceived = -1;
    private int _sinceAck;
    private byte[]? _assembling;
    private byte _assemblingType;
    private int _assembled;

    private long _ackDueAt = long.MaxValue;
    private long _retransmits;
    private double _lastRoundTrip;
    private long _lastPingSent;
    private long _lastReceived = Stopwatch.GetTimestamp();
    private long _lastSent = Stopwatch.GetTimestamp();
    private int _closed;
    private int _closeSent;
    private string? _closeReason;

    private sealed class Packet(long number, byte[] datagram)
    {
        public readonly long Number = number;
        public readonly byte[] Datagram = datagram;
        public long SentAt;
        public int Sends;
        public bool Lost;
    }

    /// <param name="session">The session every datagram is marked with.</param>
    /// <param name="transmit">Sends one datagram; protection, if any, is the caller's.</param>
    /// <param name="protectedTransport">Datagrams are encrypted, which leaves less room for the payload.</param>
    public DspLink(Guid session, Action<byte[]> transmit, bool protectedTransport)
    {
        Session = session;
        _transmit = transmit;
        _payload = protectedTransport ? ProtectedPayload : PlainPayload;
        _sender = new Thread(SendLoop) { IsBackground = true, Name = "DSP link", Priority = ThreadPriority.AboveNormal };
        _sender.Start();
    }

    public Guid Session { get; }

    /// <summary>
    /// The last round trip a ping measured, in milliseconds; before the first ping returns, what acknowledgements of
    /// data have measured; zero before either.
    /// </summary>
    public double RoundTripMilliseconds
    {
        get
        {
            double ping = Volatile.Read(ref _lastRoundTrip);
            if (ping > 0)
            {
                return ping;
            }

            lock (_gate)
            {
                return _srtt;
            }
        }
    }

    /// <summary>Sends a ping now rather than at the next second, for a round trip measured as soon as possible.</summary>
    public void PingSoon()
    {
        Interlocked.Exchange(ref _lastPingSent, 0);
        _wake.Set();
    }

    /// <summary>Datagrams sent a second time or more.</summary>
    public long RetransmittedDatagrams => Interlocked.Read(ref _retransmits);

    /// <summary>How long since anything arrived from the other end.</summary>
    public TimeSpan IdleFor => Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastReceived));

    /// <summary>The link was closed by either end or timed out.</summary>
    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>The link closed because nothing arrived for <see cref="Timeout"/>, rather than by either end.</summary>
    public bool TimedOut { get; private set; }

    /// <summary>Why the link closed, once it has.</summary>
    public string? CloseReason => Volatile.Read(ref _closeReason);

    /// <summary>A link that hears nothing for this long is closed.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The rate this end expects to send at on average, in bytes a second. Pacing starts at four times it and never
    /// falls below one and a fifth of it, which is what catching up after a loss needs.
    /// </summary>
    public double NominalRate
    {
        set
        {
            lock (_gate)
            {
                _nominalRate = Math.Max(64e3, value);
                _rate = 4 * _nominalRate;
            }
        }
    }

    /// <summary>
    /// Reads the plain header: whether a datagram belongs to this protocol, its session, and whether it is the first
    /// packet of the first message of a session, which is what a listener opens a session on.
    /// </summary>
    internal static bool Inspect(ReadOnlySpan<byte> datagram, out Guid session, out bool opensSession)
    {
        session = default;
        opensSession = false;
        if (datagram.Length < Header || BinaryPrimitives.ReadUInt32LittleEndian(datagram) != Magic || datagram[20] > Close)
        {
            return false;
        }

        session = new Guid(datagram.Slice(4, 16));
        opensSession = datagram[20] == Data && (datagram[21] & FirstOfMessage) != 0
            && BinaryPrimitives.ReadInt64LittleEndian(datagram[24..]) == 0;
        return true;
    }

    /// <summary>Whether a datagram is this protocol's close.</summary>
    internal static bool IsClose(ReadOnlySpan<byte> datagram) => datagram.Length >= Header && datagram[20] == Close;

    /// <summary>
    /// Queues a message. Waits while the data not yet acknowledged fills the window, which is what holds a fast
    /// producer to the pace of the link.
    /// </summary>
    public void Send(byte type, ReadOnlySpan<byte> body, CancellationToken cancellation = default)
    {
        if (body.Length > MaximumMessage)
        {
            throw new InvalidDataException("A DSP message exceeds 64 MiB.");
        }

        int total = 5 + body.Length;
        int packets = Math.Max(1, (total + _payload - 1) / _payload);
        lock (_gate)
        {
            cancellation.ThrowIfCancellationRequested();
            while (_unackedBytes + _queuedBytes > 0 && _unackedBytes + _queuedBytes >= Window())
            {
                ThrowIfClosed();
                cancellation.ThrowIfCancellationRequested();
                Monitor.Wait(_gate, 10);
            }

            ThrowIfClosed();
            int offset = 0;
            for (int p = 0; p < packets; p++)
            {
                int length = Math.Min(_payload, total - (p * _payload));
                byte[] datagram = new byte[Header + length];
                WriteHeader(datagram, Data, p == 0 ? FirstOfMessage : (byte)0, _nextPacket);
                Span<byte> payload = datagram.AsSpan(Header, length);
                if (p == 0)
                {
                    payload[0] = type;
                    BinaryPrimitives.WriteInt32LittleEndian(payload[1..], body.Length);
                    payload = payload[5..];
                }

                body.Slice(offset, payload.Length).CopyTo(payload);
                offset += payload.Length;
                _ready.Enqueue(new Packet(_nextPacket++, datagram));
                _queuedBytes += datagram.Length;
            }
        }

        _wake.Set();
    }

    /// <summary>The next message, waiting up to <paramref name="timeout"/>; false when none came in that time.</summary>
    public bool TryReceive(out DspLinkMessage message, TimeSpan timeout, CancellationToken cancellation = default)
    {
        if (_incoming.Reader.TryRead(out message))
        {
            return true;
        }

        ThrowIfClosed();
        if (timeout <= TimeSpan.Zero)
        {
            return false;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        linked.CancelAfter(timeout);
        try
        {
            if (_incoming.Reader.WaitToReadAsync(linked.Token).AsTask().GetAwaiter().GetResult()
                && _incoming.Reader.TryRead(out message))
            {
                return true;
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return false;
        }

        ThrowIfClosed();
        return false;
    }

    /// <summary>The next message, or an exception once the link closes or <paramref name="timeout"/> passes without one.</summary>
    public DspLinkMessage Receive(TimeSpan timeout, CancellationToken cancellation = default)
    {
        if (TryReceive(out DspLinkMessage message, timeout, cancellation))
        {
            return message;
        }

        throw new IOException("The DSP server did not answer in time.");
    }

    /// <summary>Takes a datagram addressed to this session, already authenticated and decrypted by the caller.</summary>
    public void Accept(ReadOnlySpan<byte> datagram)
    {
        if (!Inspect(datagram, out Guid session, out _) || session != Session || IsClosed)
        {
            return;
        }

        Interlocked.Exchange(ref _lastReceived, Stopwatch.GetTimestamp());
        switch (datagram[20])
        {
            case Data:
                AcceptData(datagram);
                break;
            case Ack:
                AcceptAck(datagram);
                break;
            case Ping:
            {
                byte[] pong = new byte[Header];
                WriteHeader(pong, Pong, 0, BinaryPrimitives.ReadInt64LittleEndian(datagram[24..]));
                Transmit(pong);
                break;
            }

            case Pong:
            {
                double rtt = Stopwatch.GetElapsedTime(BinaryPrimitives.ReadInt64LittleEndian(datagram[24..])).TotalMilliseconds;
                if (rtt is >= 0 and < 60_000)
                {
                    Volatile.Write(ref _lastRoundTrip, rtt);
                    Sample(rtt);
                }

                break;
            }

            default:
                Shut("The DSP peer closed the session.");
                break;
        }
    }

    /// <summary>Waits, up to <paramref name="timeout"/>, until everything sent has been acknowledged.</summary>
    public bool Drain(TimeSpan timeout)
    {
        long until = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        lock (_gate)
        {
            while (_unacked.Count > 0 || _ready.Count > 0)
            {
                if (IsClosed || Stopwatch.GetTimestamp() >= until)
                {
                    return false;
                }

                Monitor.Wait(_gate, 5);
            }
        }

        return true;
    }

    /// <summary>
    /// Closes the link, telling the other end, even when it was that end which closed first: a relay in between ends
    /// on the close it passes from the player. What is not yet acknowledged is dropped.
    /// </summary>
    public void Dispose()
    {
        Interlocked.Exchange(ref _closed, 1);
        if (Interlocked.Exchange(ref _closeSent, 1) != 0)
        {
            return;
        }

        Volatile.Write(ref _closeReason, _closeReason ?? "The DSP session was closed.");
        byte[] close = new byte[Header];
        WriteHeader(close, Close, 0, 0);
        try
        {
            for (int i = 0; i < 3; i++)
            {
                _transmit(close);
            }
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ObjectDisposedException)
        {
        }

        Wake();
        if (Thread.CurrentThread != _sender)
        {
            _sender.Join(1000);
        }
    }

    private void AcceptData(ReadOnlySpan<byte> datagram)
    {
        bool now;
        lock (_receiveGate)
        {
            now = Store(datagram);
        }

        if (now)
        {
            SendAck();
        }
        else if (Interlocked.CompareExchange(ref _ackDueAt, Stopwatch.GetTimestamp() + (Stopwatch.Frequency / 1000), long.MaxValue) == long.MaxValue)
        {
            _wake.Set();
        }
    }

    /// <summary>Keeps a data packet and delivers what is now in order; true when an acknowledgement should go at once.</summary>
    private bool Store(ReadOnlySpan<byte> datagram)
    {
        long number = BinaryPrimitives.ReadInt64LittleEndian(datagram[24..]);
        if (number < _expected || _outOfOrder.ContainsKey(number))
        {
            // A repeat: the acknowledgement that would have stopped it went missing. Say again what is held.
            return true;
        }

        if (number - _expected > 4 * MaximumBitmapPackets)
        {
            return false;
        }

        var packet = new Packet(number, datagram.ToArray());
        _highestReceived = Math.Max(_highestReceived, number);
        if (number != _expected)
        {
            _outOfOrder[number] = packet;
            return true;
        }

        Deliver(packet);
        while (_outOfOrder.Remove(_expected, out Packet? next))
        {
            Deliver(next);
        }

        return _outOfOrder.Count > 0 || ++_sinceAck >= AckEvery;
    }

    private void Deliver(Packet packet)
    {
        _expected = packet.Number + 1;
        ReadOnlySpan<byte> payload = packet.Datagram.AsSpan(Header);
        if ((packet.Datagram[21] & FirstOfMessage) != 0)
        {
            int length = payload.Length >= 5 ? BinaryPrimitives.ReadInt32LittleEndian(payload[1..]) : -1;
            if (length is < 0 or > MaximumMessage)
            {
                Shut("A DSP message arrived malformed.");
                return;
            }

            _assemblingType = payload[0];
            _assembling = length == 0 ? [] : GC.AllocateUninitializedArray<byte>(length);
            _assembled = 0;
            payload = payload[5..];
        }

        if (_assembling is null)
        {
            return;
        }

        int take = Math.Min(payload.Length, _assembling.Length - _assembled);
        payload[..take].CopyTo(_assembling.AsSpan(_assembled));
        _assembled += take;
        if (_assembled == _assembling.Length)
        {
            _incoming.Writer.TryWrite(new DspLinkMessage(_assemblingType, _assembling));
            _assembling = null;
        }
    }

    /// <summary>Everything below the next expected packet, and a bitmap of those after it that are held.</summary>
    private void SendAck()
    {
        byte[] ack;
        lock (_receiveGate)
        {
            int span = (int)Math.Clamp(_highestReceived - _expected, 0, MaximumBitmapPackets);
            int bytes = (span + 7) / 8;
            ack = new byte[Header + 2 + bytes];
            WriteHeader(ack, Ack, 0, _expected);
            BinaryPrimitives.WriteUInt16LittleEndian(ack.AsSpan(Header), (ushort)bytes);
            foreach (long number in _outOfOrder.Keys)
            {
                long bit = number - _expected - 1;
                if (bit >= 0 && bit < span)
                {
                    ack[Header + 2 + (int)(bit / 8)] |= (byte)(1 << (int)(bit % 8));
                }
            }

            _sinceAck = 0;
        }

        Interlocked.Exchange(ref _ackDueAt, long.MaxValue);
        Transmit(ack);
    }

    private void AcceptAck(ReadOnlySpan<byte> datagram)
    {
        if (datagram.Length < Header + 2)
        {
            return;
        }

        long cumulative = BinaryPrimitives.ReadInt64LittleEndian(datagram[24..]);
        int bytes = Math.Min(BinaryPrimitives.ReadUInt16LittleEndian(datagram[Header..]), datagram.Length - Header - 2);
        ReadOnlySpan<byte> bitmap = datagram.Slice(Header + 2, bytes);
        long now = Stopwatch.GetTimestamp();
        double sample = -1;
        lock (_gate)
        {
            while (_unacked.Count > 0)
            {
                KeyValuePair<long, Packet> first = _unacked.First();
                if (first.Key >= cumulative)
                {
                    break;
                }

                Acknowledge(first.Value, now, ref sample);
            }

            for (int bit = 0; bit < bytes * 8; bit++)
            {
                if ((bitmap[bit / 8] & (1 << (bit % 8))) != 0 && _unacked.TryGetValue(cumulative + 1 + bit, out Packet? packet))
                {
                    Acknowledge(packet, now, ref sample);
                }
            }

            // Three later packets acknowledged without one: it was lost, unless it went too recently for that to tell.
            // A packet already sent again gets a round trip for that copy to be acknowledged before it counts as lost again.
            double reorder = Math.Max(2.0, _srtt / 8);
            double again = Math.Max(5.0, 1.25 * _srtt);
            foreach (Packet packet in _unacked.Values)
            {
                if (packet.Number > _highestAcked - 3)
                {
                    break;
                }

                if (!packet.Lost && (now - packet.SentAt) * 1000.0 / Stopwatch.Frequency > (packet.Sends > 1 ? again : reorder))
                {
                    packet.Lost = true;
                    _lost.Enqueue(packet);
                    BackOff(now);
                }
            }

            if (sample >= 0)
            {
                Sample(sample);
            }

            Monitor.PulseAll(_gate);
        }

        _wake.Set();
    }

    private void Acknowledge(Packet packet, long now, ref double sample)
    {
        _unacked.Remove(packet.Number);
        _unackedBytes -= packet.Datagram.Length;
        _highestAcked = Math.Max(_highestAcked, packet.Number);
        _timeouts = 0;
        if (packet.Sends == 1)
        {
            sample = (now - packet.SentAt) * 1000.0 / Stopwatch.Frequency;
        }
    }

    /// <summary>A loss slows pacing by a quarter, once a round trip at most; the rate then climbs back while nothing is lost.</summary>
    private void BackOff(long now)
    {
        if (now < _quietUntil)
        {
            return;
        }

        _rate = Math.Max(1.2 * _nominalRate, _rate * 0.75);
        _quietUntil = now + (long)(Math.Max(_srtt, 5) * Stopwatch.Frequency / 1000);
        _lastIncrease = now;
    }

    private void Sample(double rtt)
    {
        lock (_gate)
        {
            if (_srtt == 0)
            {
                _srtt = rtt;
                _rttVariance = rtt / 2;
            }
            else
            {
                _rttVariance = (0.75 * _rttVariance) + (0.25 * Math.Abs(_srtt - rtt));
                _srtt = (0.875 * _srtt) + (0.125 * rtt);
            }
        }
    }

    private double TimeoutMilliseconds() =>
        Math.Clamp((_srtt == 0 ? 200 : _srtt + (4 * _rttVariance) + 10) * (1 << Math.Min(_timeouts, 5)), 40, 3000);

    /// <summary>Bytes in flight: three round trips at the paced rate, within half a megabyte and 24.</summary>
    private long Window() => (long)Math.Clamp(_rate * Math.Max(_srtt, 10) / 1000 * 3, 512 << 10, 24 << 20);

    private void SendLoop()
    {
        try
        {
            while (!IsClosed)
            {
                long now = Stopwatch.GetTimestamp();
                if (Interlocked.Read(ref _ackDueAt) <= now)
                {
                    SendAck();
                }

                if (Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastReceived)) > Timeout)
                {
                    TimedOut = true;
                    Shut("The DSP link timed out: nothing arrived for ten seconds.");
                    break;
                }

                // A ping a second, and four a second while nothing else goes, so a server busy designing a filter is
                // still heard from and the round trip shown is a recent one.
                long lastPing = Interlocked.Read(ref _lastPingSent);
                double sincePing = lastPing == 0 ? double.MaxValue : Stopwatch.GetElapsedTime(lastPing).TotalMilliseconds;
                if (sincePing >= 1000 || (sincePing >= 250 && Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastSent)).TotalMilliseconds >= 250))
                {
                    Interlocked.Exchange(ref _lastPingSent, now);
                    byte[] ping = new byte[Header];
                    WriteHeader(ping, Ping, 0, now);
                    Transmit(ping);
                }

                int wait = SendDue(now);
                long ackDue = Interlocked.Read(ref _ackDueAt);
                if (ackDue != long.MaxValue)
                {
                    wait = Math.Min(wait, Math.Max(0, (int)((ackDue - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency)));
                }

                if (wait > 0)
                {
                    _wake.WaitOne(wait);
                }
            }
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ObjectDisposedException)
        {
            Shut("The DSP link could not send: " + ex.Message);
        }
    }

    /// <summary>Sends what pacing allows, lost packets first, then timed-out ones, then new ones; returns the milliseconds to wait before looking again.</summary>
    private int SendDue(long now)
    {
        List<byte[]> batch = _sendBatch;
        batch.Clear();
        int wait = 50;
        lock (_gate)
        {
            double elapsed = (now - _lastRefill) / (double)Stopwatch.Frequency;
            _lastRefill = now;
            _tokens = Math.Min(Math.Max(64 << 10, _rate * 0.004), _tokens + (elapsed * _rate));
            if (now >= _quietUntil && (now - _lastIncrease) * 1000.0 / Stopwatch.Frequency >= Math.Max(_srtt, 10))
            {
                _rate = Math.Min(4 * _nominalRate, _rate + (0.05 * _nominalRate));
                _lastIncrease = now;
            }

            while (_lost.Count > 0 && _tokens > 0 && batch.Count < Batch)
            {
                Packet packet = _lost.Dequeue();
                if (packet.Lost && _unacked.ContainsKey(packet.Number))
                {
                    Resend(packet, now, batch);
                }
            }

            // Nothing acknowledged for a timeout: the oldest packets go again, and the timeout doubles until something is.
            double timeout = TimeoutMilliseconds();
            bool timedOut = false;
            foreach (Packet packet in _unacked.Values)
            {
                if (_tokens <= 0 || batch.Count >= Batch || (now - packet.SentAt) * 1000.0 / Stopwatch.Frequency < timeout)
                {
                    break;
                }

                timedOut = true;
                Resend(packet, now, batch);
            }

            if (timedOut)
            {
                _timeouts++;
                BackOff(now);
            }

            long window = Window();
            while (_ready.Count > 0 && _tokens > 0 && batch.Count < Batch && _unackedBytes < window)
            {
                Packet packet = _ready.Dequeue();
                _queuedBytes -= packet.Datagram.Length;
                packet.SentAt = now;
                packet.Sends = 1;
                _unacked[packet.Number] = packet;
                _unackedBytes += packet.Datagram.Length;
                _tokens -= packet.Datagram.Length;
                batch.Add(packet.Datagram);
            }

            if (batch.Count > 0)
            {
                Monitor.PulseAll(_gate);
            }

            if (_tokens > 0 && (_lost.Count > 0 || (_ready.Count > 0 && _unackedBytes < window)))
            {
                wait = 0;
            }
            else if (_tokens <= 0)
            {
                wait = Math.Max(1, (int)Math.Ceiling(-_tokens / _rate * 1000));
            }
            else if (_unacked.Count > 0)
            {
                Packet oldest = _unacked.Values.First();
                double due = timeout - ((now - oldest.SentAt) * 1000.0 / Stopwatch.Frequency);
                wait = Math.Clamp((int)Math.Ceiling(due), 1, 50);
            }
        }

        foreach (byte[] datagram in batch)
        {
            Transmit(datagram);
        }

        return wait;
    }

    private void Resend(Packet packet, long now, List<byte[]> batch)
    {
        packet.Lost = false;
        packet.SentAt = now;
        packet.Sends++;
        _tokens -= packet.Datagram.Length;
        batch.Add(packet.Datagram);
        Interlocked.Increment(ref _retransmits);
    }

    private void Transmit(byte[] datagram)
    {
        _transmit(datagram);
        Interlocked.Exchange(ref _lastSent, Stopwatch.GetTimestamp());
    }

    private void WriteHeader(byte[] datagram, byte kind, byte flags, long number)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(datagram, Magic);
        Session.TryWriteBytes(datagram.AsSpan(4));
        datagram[20] = kind;
        datagram[21] = flags;
        BinaryPrimitives.WriteInt64LittleEndian(datagram.AsSpan(24), number);
    }

    private void Shut(string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        Volatile.Write(ref _closeReason, reason);
        Wake();
    }

    private void Wake()
    {
        _incoming.Writer.TryComplete();
        _wake.Set();
        lock (_gate)
        {
            Monitor.PulseAll(_gate);
        }
    }

    private void ThrowIfClosed()
    {
        // Receive takes already-delivered messages before checking this. A closed sender must never
        // accept more work merely because its independent receive queue still contains audio.
        if (IsClosed)
        {
            throw new IOException(CloseReason ?? "The DSP session was closed.");
        }
    }
}
