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
    private readonly Action<byte[]> _send;
    private readonly Channel<byte[]> _incoming = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(16384)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly bool _server;
    private readonly object _sendGate = new();
    private Sent? _sent;
    private long _lastResend, _receivedSequence, _pingSent, _retransmits, _lastPacket = Stopwatch.GetTimestamp();
    private long _acknowledgedSequence;
    private double _roundTrip;
    private int _disposed;
    internal Guid Session { get; }
    internal double RoundTripMilliseconds => Volatile.Read(ref _roundTrip);
    internal long RetransmittedDatagrams => Interlocked.Read(ref _retransmits);
    internal TimeSpan IdleFor => Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastPacket));
    private sealed record Sent(DspMessage Type, long Sequence, byte[] Bytes)
    {
        internal int SendingAll;
        internal long CompletedAt = Stopwatch.GetTimestamp();
    }

    internal UdpDspPeer(Guid session, Action<byte[]> send, bool server)
    { Session = session; _send = send; _server = server; }

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
        if (kind == Ping)
        {
            if (_server) _send(packet);
            else if (BinaryPrimitives.ReadInt64LittleEndian(packet.AsSpan(20)) == Volatile.Read(ref _pingSent))
                Volatile.Write(ref _roundTrip, Stopwatch.GetElapsedTime(_pingSent).TotalMilliseconds);
            return;
        }
        if (kind == Close) { _incoming.Writer.TryComplete(); return; }
        if (kind == Ack) { Interlocked.Exchange(ref _acknowledgedSequence, BinaryPrimitives.ReadInt64LittleEndian(packet.AsSpan(20))); return; }
        if (kind == Nack)
        {
            Sent? sent = Volatile.Read(ref _sent);
            if (sent is null || sent.Sequence != BinaryPrimitives.ReadInt64LittleEndian(packet.AsSpan(20))) return;
            if (packet.Length == Header)
            {
                // A response probe can arrive while its original burst is still leaving the socket.
                // Never start a second whole burst beside the first, or repeat it on every short feedback tick.
                if (Volatile.Read(ref sent.SendingAll) == 0 && Stopwatch.GetElapsedTime(Volatile.Read(ref sent.CompletedAt)).TotalMilliseconds >= 12)
                    SendAll(sent, true);
                return;
            }
            for (int at = Header; at + 4 <= packet.Length; at += 4)
            {
                int index = BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(at));
                if (index >= 0 && (long)index * Payload < Math.Max(1, sent.Bytes.Length)) SendFragment(sent, index, true);
            }
            return;
        }
        long sequence = BinaryPrimitives.ReadInt64LittleEndian(packet.AsSpan(20));
        if (sequence <= Interlocked.Read(ref _receivedSequence))
        {
            _send(Packet(Ack, 0, sequence, 0, 0, []));
            Sent? sent = Volatile.Read(ref _sent);
            if (_server && sent?.Sequence == sequence && Stopwatch.GetElapsedTime(_lastResend).TotalMilliseconds > 12)
            { _lastResend = Stopwatch.GetTimestamp(); SendAll(sent, true); }
            return;
        }
        _incoming.Writer.TryWrite(packet);
    }
    internal void Probe()
    {
        if (_server || Stopwatch.GetElapsedTime(Volatile.Read(ref _pingSent)).TotalSeconds < 1) return;
        long stamp = Stopwatch.GetTimestamp(); Volatile.Write(ref _pingSent, stamp);
        _send(Packet(Ping, 0, stamp, 0, 0, []));
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
        try
        {
            int count = Math.Max(1, (sent.Bytes.Length + Payload - 1) / Payload);
            for (int index = 0; index < count && !_stop.IsCancellationRequested; index++)
            { SendFragment(sent, index, retransmit); if ((index & 63) == 63) Thread.Yield(); }
        }
        finally { Volatile.Write(ref sent.CompletedAt, Stopwatch.GetTimestamp()); Volatile.Write(ref sent.SendingAll, 0); }
    }
    private void SendFragment(Sent sent, int index, bool retransmit)
    {
        int offset = index * Payload, count = Math.Min(Payload, sent.Bytes.Length - offset);
        if (count < 0) return;
        lock (_sendGate) _send(Packet(Data, sent.Type, sent.Sequence, sent.Bytes.Length, index, sent.Bytes.AsSpan(offset, count)));
        if (retransmit) Interlocked.Increment(ref _retransmits);
    }
    private byte[] Packet(byte kind, DspMessage type, long sequence, int length, int index, ReadOnlySpan<byte> payload)
    {
        var packet = new byte[Header + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, Magic); Session.TryWriteBytes(packet.AsSpan(4));
        BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(20), sequence);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(28), length);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(32), index);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(36), Math.Max(1, (length + Payload - 1) / Payload));
        packet[40] = kind; packet[41] = (byte)type; payload.CopyTo(packet.AsSpan(Header)); return packet;
    }
    internal (DspMessage Type, long Sequence, byte[] Bytes) Receive(TimeSpan timeout, CancellationToken cancellation = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, cancellation);
        long started = Stopwatch.GetTimestamp(), lastFeedback = started;
        byte[]? result = null; bool[]? arrived = null;
        int remaining = 0; long sequence = 0; DspMessage type = default;
        while (Stopwatch.GetElapsedTime(_server && result is null ? Interlocked.Read(ref _lastPacket) : started) < timeout)
        {
            linked.Token.ThrowIfCancellationRequested();
            if (_incoming.Reader.TryRead(out byte[]? packet))
            {
                int length = BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(28));
                int index = BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(32));
                int count = BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(36));
                long incomingSequence = BinaryPrimitives.ReadInt64LittleEndian(packet.AsSpan(20));
                // Duplicates can already be queued when the preceding message completes.
                if (incomingSequence <= Interlocked.Read(ref _receivedSequence)) continue;
                int maximum = _receivedSequence == 0 ? 1 << 20 : DspProtocol.MaximumMessage;
                if (length < 0 || length > maximum || count != Math.Max(1, (length + Payload - 1) / Payload)
                    || index < 0 || index >= count || packet.Length - Header != Math.Min(Payload, length - index * Payload)) continue;
                if (result is null)
                { sequence = incomingSequence; type = (DspMessage)packet[41]; result = new byte[length]; arrived = new bool[count]; remaining = count; }
                if (incomingSequence != sequence || type != (DspMessage)packet[41] || length != result.Length || count != arrived!.Length) continue;
                if (!arrived[index])
                {
                    packet.AsSpan(Header).CopyTo(result.AsSpan(index * Payload)); arrived[index] = true; remaining--;
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
                continue;
            }
            double feedback = Math.Clamp(RoundTripMilliseconds > 0 ? 4 * RoundTripMilliseconds : 8, 4, 16);
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
        _stop.Cancel(); _incoming.Writer.TryComplete(); Volatile.Write(ref _sent, null);
    }
}
