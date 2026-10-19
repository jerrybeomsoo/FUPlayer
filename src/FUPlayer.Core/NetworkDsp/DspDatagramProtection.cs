using System.Buffers.Binary;
using System.Security.Cryptography;

namespace FUPlayer.Core.NetworkDsp;

/// <summary>
/// Authenticated UDP envelopes for a shared 256-bit access key. LAN-only sessions can retain their plain transport.
/// Direction, session and send time are authenticated; a bounded replay window permits packet reordering.
/// </summary>
internal sealed class DspDatagramProtection : IDisposable
{
    internal const int Overhead = 57, ProtectedPayload = 1344;
    private const uint Magic = 0x31455046;
    private const int Prefix = 29, Nonce = 12, Tag = 16;
    private readonly byte[] _key;
    private readonly Dictionary<Guid, AesGcm> _sendCiphers = new(), _receiveCiphers = new();
    private readonly object _sendGate = new(), _receiveGate = new();
    private readonly byte _direction;
    private readonly Dictionary<(Guid Session, ulong Prefix), Window> _windows = new();
    private readonly byte[] _noncePrefix = RandomNumberGenerator.GetBytes(8);
    private uint _counter;
    private int _disposed;
    private sealed class Window
    {
        internal readonly ulong[] Seen = new ulong[64];
        internal long Highest = -1;
        internal long LastTime;
        internal bool Accept(uint counter)
        {
            const int width = 4096;
            if (counter <= Highest - width) return false;
            if (counter > Highest)
            {
                if (counter - Highest >= width) Array.Clear(Seen);
                else for (long n = Highest + 1; n <= counter; n++) Seen[(int)(n % width) / 64] &= ~(1UL << (int)(n % 64));
                Highest = counter;
            }
            int slot = (int)(counter % width) / 64;
            ulong bit = 1UL << (int)(counter % 64);
            if ((Seen[slot] & bit) != 0) return false;
            Seen[slot] |= bit; return true;
        }
    }

    internal DspDatagramProtection(string key, bool server)
    {
        _key = ParseKey(key);
        _direction = server ? (byte)1 : (byte)0;
    }

    private AesGcm CreateCipher(Guid session, byte direction)
    {
        // Distinct per-session and per-direction keys keep independent workers' nonce spaces separate.
        byte[] trafficKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, _key, 32, session.ToByteArray(), [.. "FUPlayer DSP UDP v1"u8, direction]);
        try { return new AesGcm(trafficKey, Tag); }
        finally { CryptographicOperations.ZeroMemory(trafficKey); }
    }

    private static void LimitCiphers(Dictionary<Guid, AesGcm> ciphers)
    {
        if (ciphers.Count < 1024) return;
        foreach (AesGcm cipher in ciphers.Values) cipher.Dispose();
        ciphers.Clear();
    }

    internal static byte[] ParseKey(string text)
    {
        byte[] key;
        try { key = Convert.FromBase64String(text.Trim()); }
        catch (FormatException) { throw new IOException("The DSP access key must contain 32 bytes encoded as Base64."); }
        if (key.Length == 32) return key;
        CryptographicOperations.ZeroMemory(key);
        throw new IOException("The DSP access key must contain 32 bytes encoded as Base64.");
    }

    internal static bool Inspect(ReadOnlySpan<byte> wire, out Guid session)
    {
        session = default;
        if (wire.Length < Overhead + UdpDspPeer.Header || wire.Length > Overhead + UdpDspPeer.Header + ProtectedPayload
            || BinaryPrimitives.ReadUInt32LittleEndian(wire) != Magic || wire[4] > 1) return false;
        session = new Guid(wire.Slice(5, 16)); return true;
    }

    internal byte[] Protect(byte[] packet)
    {
        if (!UdpDspPeer.Inspect(packet, out Guid session, out _, out _)) throw new InvalidDataException("Invalid DSP datagram.");
        var wire = new byte[packet.Length + Overhead];
        lock (_sendGate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_counter == uint.MaxValue) { RandomNumberGenerator.Fill(_noncePrefix); _counter = 0; }
            BinaryPrimitives.WriteUInt32LittleEndian(wire, Magic); wire[4] = _direction;
            session.TryWriteBytes(wire.AsSpan(5));
            BinaryPrimitives.WriteInt64LittleEndian(wire.AsSpan(21), DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            _noncePrefix.CopyTo(wire, Prefix);
            BinaryPrimitives.WriteUInt32LittleEndian(wire.AsSpan(Prefix + 8), ++_counter);
            if (!_sendCiphers.TryGetValue(session, out AesGcm? cipher))
            { LimitCiphers(_sendCiphers); _sendCiphers[session] = cipher = CreateCipher(session, _direction); }
            cipher.Encrypt(wire.AsSpan(Prefix, Nonce), packet, wire.AsSpan(Overhead), wire.AsSpan(Prefix + Nonce, Tag), wire.AsSpan(0, Prefix));
        }
        return wire;
    }

    internal bool TryUnprotect(ReadOnlySpan<byte> wire, out byte[] packet)
    {
        packet = [];
        if (!Inspect(wire, out Guid session) || wire[4] == _direction) return false;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long time = BinaryPrimitives.ReadInt64LittleEndian(wire.Slice(21));
        if (time < now - 120 || time > now + 120) return false;
        lock (_receiveGate)
        {
            if (Volatile.Read(ref _disposed) != 0) return false;
            var decoded = new byte[wire.Length - Overhead];
            bool cached = _receiveCiphers.TryGetValue(session, out AesGcm? cipher);
            cipher ??= CreateCipher(session, (byte)(1 - _direction));
            try { cipher.Decrypt(wire.Slice(Prefix, Nonce), wire[Overhead..], wire.Slice(Prefix + Nonce, Tag), decoded, wire[..Prefix]); }
            catch (AuthenticationTagMismatchException) { if (!cached) cipher.Dispose(); return false; }
            if (!cached) { LimitCiphers(_receiveCiphers); _receiveCiphers[session] = cipher; }
            if (!UdpDspPeer.Inspect(decoded, out Guid inner, out _, out _) || inner != session) return false;
            ulong prefix = BinaryPrimitives.ReadUInt64LittleEndian(wire.Slice(Prefix));
            if (!_windows.TryGetValue((session, prefix), out Window? window))
            {
                foreach (var expired in _windows.Where(p => p.Value.LastTime < now - 240).Select(p => p.Key).ToArray()) _windows.Remove(expired);
                if (_windows.Count >= 1024) return false;
                _windows[(session, prefix)] = window = new Window();
            }
            if (!window.Accept(BinaryPrimitives.ReadUInt32LittleEndian(wire.Slice(Prefix + 8)))) return false;
            window.LastTime = now; packet = decoded; return true;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_sendGate) lock (_receiveGate)
        {
            foreach (AesGcm cipher in _sendCiphers.Values) cipher.Dispose();
            foreach (AesGcm cipher in _receiveCiphers.Values) cipher.Dispose();
            _sendCiphers.Clear(); _receiveCiphers.Clear(); _windows.Clear();
            CryptographicOperations.ZeroMemory(_key);
        }
    }
}
