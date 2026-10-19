using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace FUPlayer.Core.NetworkDsp;

/// <summary>MTU-sized datagrams with selective recovery. A duplicate request never repeats stateful DSP.</summary>
internal sealed class UdpDspPeer : IDisposable
{
    internal const int Header = 44, Payload = 1400;
    private const uint Magic = 0x33505546;
    private const byte Data = 0, Nack = 1, Ping = 2, Close = 3, Ack = 4;
    private const int FlightWindow = 1024, ProgressInterval = 128;
    private readonly Action<byte[]> _send;
    private readonly Channel<byte[]> _incoming = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(16384)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly bool _server;
    private readonly int _payload;
    private readonly bool _protected;
    private readonly object _sendGate = new();
    private Sent? _sent;
    private long _lastResend, _receivedSequence, _pingSent, _retransmits, _lastPacket = Stopwatch.GetTimestamp();
    private long _acknowledgedSequence;
    private long _selectiveRecoveryAt;
    private bool _windowSupported;
    private double _roundTrip;
    private readonly TaskCompletionSource _firstProbe = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;
    internal Guid Session { get; }
    internal double RoundTripMilliseconds => Volatile.Read(ref _roundTrip);
    internal long RetransmittedDatagrams => Interlocked.Read(ref _retransmits);
    internal TimeSpan IdleFor => Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastPacket));
    private sealed record Sent(DspMessage Type, long Sequence, byte[] Bytes)
    {
        internal int SendingAll;
        internal long CompletedAt = Stopwatch.GetTimestamp();
        internal int HighestReceived = -1;
    }

    internal UdpDspPeer(Guid session, Action<byte[]> send, bool server, bool protectedTransport = false)
    { Session = session; _send = send; _server = server; _protected = protectedTransport;
      _payload = protectedTransport ? DspDatagramProtection.ProtectedPayload : Payload; }

    internal static bool Inspect(ReadOnlySpan<byte> packet, out Guid session, out byte kind, out DspMessage type)
    {
        session = default; kind = 0; type = default;
        if (packet.Length < Header || BinaryPrimitives.ReadUInt32LittleEndian(packet) != Magic) return false;
        session = new Guid(packet.Slice(4, 16)); kind = packet[40]; type = (DspMessage)packet[41];
        return kind <= Ack;
    }
    internal static bool IsLan(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal) return true;
        byte[] b = address.GetAddressBytes();
        return b.Length == 16 ? (b[0] & 0xfe) == 0xfc
            : b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] is >= 16 and <= 31
                || b[0] == 169 && b[1] == 254;
    }
    internal void Accept(byte[] packet)
    {
        if (!Inspect(packet, out Guid id, out byte kind, out _) || id != Session || _stop.IsCancellationRequested) return;
        Interlocked.Exchange(ref _lastPacket, Stopwatch.GetTimestamp());
        if (packet[42] == 1) Volatile.Write(ref _windowSupported, true);
        if (kind == Ping)
        {
            // The client measures propagation; return that measurement so the worker need not infer
            // it from a potentially retransmitted hello. Older servers echo this feedback once.
            if (packet[43] == 2)
            {
                int microseconds = BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(32));
                if (_server && packet[42] == 1 && microseconds is > 0 and <= 1_000_000)
                { Volatile.Write(ref _roundTrip, microseconds / 1000.0); _firstProbe.TrySetResult(); }
                return;
            }
            if (_server) _send(packet);
            else if (BinaryPrimitives.ReadInt64LittleEndian(packet.AsSpan(20)) == Volatile.Read(ref _pingSent))
            {
                Volatile.Write(ref _roundTrip, Stopwatch.GetElapsedTime(_pingSent).TotalMilliseconds);
                byte[] feedback = Packet(Ping, 0, _pingSent, 0, (int)Math.Clamp(RoundTripMilliseconds * 1000, 1, 1_000_000), []);
                feedback[43] = 2; _send(feedback);
                _firstProbe.TrySetResult();
            }
            return;
        }
        if (kind == Close) { _stop.Cancel(); WakeSender(); _incoming.Writer.TryComplete(); return; }
        if (kind == Ack)
        {
            long acknowledgedSequence = BinaryPrimitives.ReadInt64LittleEndian(packet.AsSpan(20));
            Sent? progress = Volatile.Read(ref _sent);
            if (progress is not null && progress.Sequence == acknowledgedSequence)
            {
                int count = Math.Max(1, (progress.Bytes.Length + _payload - 1) / _payload);
                int received = packet[43] == 1 ? BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(32)) : count - 1;
                if (received >= 0 && received < count)
                    lock (progress)
                    { progress.HighestReceived = Math.Max(progress.HighestReceived, received); Monitor.PulseAll(progress); }
            }
            // Partial acknowledgements open the receive window; they do not acknowledge the whole message.
            if (packet[43] == 1) return;
            long previous = Interlocked.Read(ref _acknowledgedSequence);
            while (acknowledgedSequence > previous)
            {
                long observed = Interlocked.CompareExchange(ref _acknowledgedSequence, acknowledgedSequence, previous);
                if (observed == previous) break;
                previous = observed;
            }
            // The small hello response's acknowledgement gives the worker its own propagation estimate.
            // Large audio responses include transmission/queue time and must not inflate that estimate.
            Sent? acknowledged = Volatile.Read(ref _sent);
            if (!_server && acknowledged is { Type: DspMessage.Hello } && acknowledged.Sequence == acknowledgedSequence)
                Probe();
            if (_server && acknowledged is { Type: DspMessage.Hello } && acknowledged.Sequence == acknowledgedSequence
                && Volatile.Read(ref acknowledged.SendingAll) == 0)
                Volatile.Write(ref _roundTrip, Stopwatch.GetElapsedTime(Volatile.Read(ref acknowledged.CompletedAt)).TotalMilliseconds);
            return;
        }
        if (kind == Nack)
        {
            Sent? sent = Volatile.Read(ref _sent);
            if (sent is null || sent.Sequence != BinaryPrimitives.ReadInt64LittleEndian(packet.AsSpan(20))) return;
            if (packet.Length == Header)
            {
                // A response probe can arrive while its original burst is still leaving the socket.
                // Never start a second whole burst beside the first, or repeat it on every short feedback tick.
                if (Volatile.Read(ref sent.SendingAll) == 0 && Stopwatch.GetElapsedTime(Volatile.Read(ref sent.CompletedAt)).TotalMilliseconds >= 12)
                    QueueSendAll(sent);
                return;
            }
            Interlocked.Exchange(ref _selectiveRecoveryAt, Stopwatch.GetTimestamp());
            for (int at = Header; at + 4 <= packet.Length; at += 4)
            {
                int index = BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(at));
                if (index >= 0 && (long)index * _payload < Math.Max(1, sent.Bytes.Length)) SendFragment(sent, index, true);
            }
            return;
        }
        long sequence = BinaryPrimitives.ReadInt64LittleEndian(packet.AsSpan(20));
        if (sequence <= Interlocked.Read(ref _receivedSequence))
        {
            // A late request burst can contain thousands of duplicate fragments. Acknowledge receipt;
            // only an explicit response NACK may replay cached audio. Otherwise each fragment can
            // trigger another multi-megabyte response while the client has already moved on.
            if (_server && Stopwatch.GetElapsedTime(_lastResend).TotalMilliseconds > 12)
            { _lastResend = Stopwatch.GetTimestamp(); _send(Packet(Ack, 0, sequence, 0, 0, [])); }
            return;
        }
        _incoming.Writer.TryWrite(packet);
    }
    internal void Probe()
    {
        if (_server || Stopwatch.GetElapsedTime(Volatile.Read(ref _pingSent)).TotalSeconds < (RoundTripMilliseconds > 0 ? 1 : .05)) return;
        long stamp = Stopwatch.GetTimestamp(); Volatile.Write(ref _pingSent, stamp);
        _send(Packet(Ping, 0, stamp, 0, 0, []));
    }
    internal void WaitForFirstProbe(CancellationToken cancellation, int timeoutMilliseconds = 500)
    {
        try { _firstProbe.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMilliseconds), cancellation).GetAwaiter().GetResult(); }
        catch (TimeoutException) { }
    }
    internal void Send(DspMessage type, long sequence, byte[] bytes)
    {
        if (bytes.Length > DspProtocol.MaximumMessage) throw new InvalidDataException("DSP message exceeds 64 MiB.");
        var sent = new Sent(type, sequence, bytes);
        Volatile.Write(ref _sent, sent); SendAll(sent, false);
    }
    private void SendAll(Sent sent, bool retransmit)
    {
        if (Interlocked.CompareExchange(ref sent.SendingAll, 1, 0) != 0) return;
        TransmitAll(sent, retransmit);
    }
    private void QueueSendAll(Sent sent)
    {
        if (Interlocked.CompareExchange(ref sent.SendingAll, 1, 0) != 0) return;
        // Keep receiving window acknowledgements while replaying a response. Blocking the socket reader
        // here would make a large replay wait for feedback that it could no longer receive.
        _ = Task.Run(() =>
        {
            try { TransmitAll(sent, true); }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { _stop.Cancel(); }
        });
    }
    private void TransmitAll(Sent sent, bool retransmit)
    {
        try
        {
            int count = Math.Max(1, (sent.Bytes.Length + _payload - 1) / _payload);
            bool window = _server && Volatile.Read(ref _windowSupported) && count > FlightWindow;
            lock (sent) sent.HighestReceived = -1;
            for (int index = 0; index < count && !_stop.IsCancellationRequested; index++)
            {
                if (window)
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        bool retry;
                        lock (sent)
                        {
                            if (index <= sent.HighestReceived + FlightWindow) break;
                            retry = !Monitor.Wait(sent, (int)Math.Clamp(2 * RoundTripMilliseconds, 50, 1000));
                        }
                        // A lost progress acknowledgement must not deadlock the window. One duplicate
                        // elicits current progress without replaying the entire multi-megabyte response.
                        if (retry && !_stop.IsCancellationRequested) SendFragment(sent, index - 1, true);
                    }
                }
                if (_stop.IsCancellationRequested) break;
                SendFragment(sent, index, retransmit); if ((index & 63) == 63) Thread.Yield();
            }
        }
        finally { Volatile.Write(ref sent.CompletedAt, Stopwatch.GetTimestamp()); Volatile.Write(ref sent.SendingAll, 0); }
    }
    private void SendFragment(Sent sent, int index, bool retransmit)
    {
        int offset = index * _payload, count = Math.Min(_payload, sent.Bytes.Length - offset);
        if (count < 0) return;
        lock (_sendGate) _send(Packet(Data, sent.Type, sent.Sequence, sent.Bytes.Length, index, sent.Bytes.AsSpan(offset, count)));
        if (retransmit) Interlocked.Increment(ref _retransmits);
    }
    private void WakeSender()
    {
        if (Volatile.Read(ref _sent) is { } sent) lock (sent) Monitor.PulseAll(sent);
    }
    private byte[] Packet(byte kind, DspMessage type, long sequence, int length, int index, ReadOnlySpan<byte> payload)
    {
        var packet = new byte[Header + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, Magic); Session.TryWriteBytes(packet.AsSpan(4));
        BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(20), sequence);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(28), length);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(32), index);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(36), Math.Max(1, (length + _payload - 1) / _payload));
        packet[40] = kind; packet[41] = (byte)type; packet[42] = 1;
        payload.CopyTo(packet.AsSpan(Header)); return packet;
    }
    internal (DspMessage Type, long Sequence, byte[] Bytes) Receive(TimeSpan timeout, CancellationToken cancellation = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, cancellation);
        long started = Stopwatch.GetTimestamp(), lastFeedback = started;
        byte[]? result = null; bool[]? arrived = null;
        int remaining = 0, highestReceived = -1, lastProgress = -1; long sequence = 0; DspMessage type = default;
        while (Stopwatch.GetElapsedTime(_server && result is null ? Interlocked.Read(ref _lastPacket) : started) < timeout)
        {
            linked.Token.ThrowIfCancellationRequested();
            if (!_server && _receivedSequence == 0 && _sent is { Type: DspMessage.Hello } opening
                && opening.Sequence != Interlocked.Read(ref _acknowledgedSequence) && Stopwatch.GetElapsedTime(started).TotalSeconds > 10)
                throw new IOException("No DSP response. Check the access key, DNS address and UDP forwarding.");
            if (_incoming.Reader.TryRead(out byte[]? packet))
            {
                int length = BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(28));
                int index = BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(32));
                int count = BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(36));
                long incomingSequence = BinaryPrimitives.ReadInt64LittleEndian(packet.AsSpan(20));
                // Duplicates can already be queued when the preceding message completes.
                if (incomingSequence <= Interlocked.Read(ref _receivedSequence)) continue;
                int maximum = _receivedSequence == 0 ? 1 << 20 : DspProtocol.MaximumMessage;
                if (length < 0 || length > maximum || count != Math.Max(1, (length + _payload - 1) / _payload)
                    || index < 0 || index >= count || packet.Length - Header != Math.Min(_payload, length - index * _payload)) continue;
                if (result is null)
                { sequence = incomingSequence; type = (DspMessage)packet[41]; result = new byte[length]; arrived = new bool[count]; remaining = count; }
                if (incomingSequence != sequence || type != (DspMessage)packet[41] || length != result.Length || count != arrived!.Length) continue;
                if (!arrived[index])
                {
                    packet.AsSpan(Header).CopyTo(result.AsSpan(index * _payload)); arrived[index] = true; remaining--;
                    // DSP may have taken much longer than a feedback interval. Give the arriving burst time
                    // to finish before asking for fragments that are still following it through the socket.
                    lastFeedback = Stopwatch.GetTimestamp();
                    if (remaining == 0)
                    {
                        Interlocked.Exchange(ref _receivedSequence, sequence);
                        _send(Packet(Ack, 0, sequence, 0, 0, []));
                        return (type, sequence, result);
                    }
                }
                highestReceived = Math.Max(highestReceived, index);
                if (!_server && packet[42] == 1 && count > FlightWindow
                    && (highestReceived >= lastProgress + ProgressInterval || index <= lastProgress))
                {
                    byte[] progress = Packet(Ack, type, sequence, 0, highestReceived, []);
                    progress[43] = 1; _send(progress); lastProgress = highestReceived;
                }
                continue;
            }
            // Once fragments have arrived, a short gap is enough to identify holes in that burst. Waiting
            // four WAN round trips after its final packet turns a single loss into another audio stall.
            // Keep the longer request/response probe interval while inference has not produced a burst.
            // A selective request retry is progress even before its acknowledgement returns. Restart
            // the whole-request deadline so it cannot race the fragment that was just retransmitted.
            if (!_server && result is null) lastFeedback = Math.Max(lastFeedback, Interlocked.Read(ref _selectiveRecoveryAt));
            double feedback = Math.Clamp(RoundTripMilliseconds > 0
                ? (arrived is not null ? 1.5 : 4) * RoundTripMilliseconds : _protected ? 100 : 8,
                _protected && arrived is not null ? 24 : 4, _protected ? 1000 : 16);
            if (Stopwatch.GetElapsedTime(lastFeedback).TotalMilliseconds >= feedback)
            {
                if (arrived is not null)
                {
                    var missing = new List<int>(320);
                    for (int index = 0; index < arrived.Length; index++)
                    {
                        if (!arrived[index]) missing.Add(index);
                        if (missing.Count == 320 || index == arrived.Length - 1)
                        {
                            if (missing.Count > 0)
                            {
                                byte[] list = new byte[missing.Count * 4];
                                for (int n = 0; n < missing.Count; n++) BinaryPrimitives.WriteInt32LittleEndian(list.AsSpan(n * 4), missing[n]);
                                _send(Packet(Nack, type, sequence, 0, 0, list)); missing.Clear();
                            }
                        }
                    }
                }
                else if (!_server && _sent is { } sent)
                {
                    // Once the request arrived, ask for a missing response without resending its audio during inference.
                    if (sent.Sequence == Interlocked.Read(ref _acknowledgedSequence)) _send(Packet(Nack, sent.Type, sent.Sequence, 0, 0, []));
                    else SendAll(sent, true);
                }
                lastFeedback = Stopwatch.GetTimestamp();
            }
            using var tick = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
            tick.CancelAfter(_server && result is null ? TimeSpan.FromMilliseconds(500) : TimeSpan.FromMilliseconds(feedback));
            try
            {
                if (!_incoming.Reader.WaitToReadAsync(tick.Token).AsTask().GetAwaiter().GetResult())
                    throw new IOException("DSP UDP peer closed the session.");
            }
            catch (OperationCanceledException) when (!linked.IsCancellationRequested) { }
        }
        throw new IOException("DSP UDP response timed out.");
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { for (int n = 0; n < 3; n++) _send(Packet(Close, DspMessage.Close, 0, 0, 0, [])); }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
        _stop.Cancel(); WakeSender(); _firstProbe.TrySetCanceled(); _incoming.Writer.TryComplete(); Volatile.Write(ref _sent, null);
    }
}
