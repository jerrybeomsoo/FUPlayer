using System.Numerics;
using System.Runtime.CompilerServices;

namespace FUPlayer.Core.Decoding.Flac;

/// <summary>
/// MSB-first bit reader over a stream with a 64-bit cache. Bits below the valid part of the cache are always
/// zero, which lets unary codes be counted with a single leading-zero count.
/// </summary>
internal sealed class FlacBitReader
{
    private const int KeepBehind = 16;

    private readonly Stream _stream;
    private readonly byte[] _buffer = new byte[1 << 18];
    private long _bufferFileOffset;
    private int _bufferPosition;
    private int _bufferLength;
    private ulong _cache;
    private int _cacheBits;

    public FlacBitReader(Stream stream)
    {
        _stream = stream;
        _bufferFileOffset = stream.Position;
    }

    /// <summary>File offset of the next unread whole byte.</summary>
    public long BytePosition => _bufferFileOffset + _bufferPosition - _cacheBits / 8;

    public void Reset(long fileOffset)
    {
        _stream.Position = fileOffset;
        _bufferFileOffset = fileOffset;
        _bufferPosition = 0;
        _bufferLength = 0;
        _cache = 0;
        _cacheBits = 0;
    }

    public void AlignToByte()
    {
        int drop = _cacheBits & 7;
        _cache <<= drop;
        _cacheBits -= drop;
    }

    /// <summary>Returns up to <paramref name="count"/> upcoming bytes without consuming them (must be byte aligned).</summary>
    public ReadOnlySpan<byte> Peek(int count)
    {
        ReturnCacheToBuffer();
        if (_bufferLength - _bufferPosition < count)
        {
            Fill(count);
        }

        return _buffer.AsSpan(_bufferPosition, Math.Min(count, _bufferLength - _bufferPosition));
    }

    public void SkipBytes(int count)
    {
        ReturnCacheToBuffer();
        while (count > 0)
        {
            if (_bufferPosition >= _bufferLength && !Fill(1))
            {
                throw new EndOfStreamException();
            }

            int step = Math.Min(count, _bufferLength - _bufferPosition);
            _bufferPosition += step;
            count -= step;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadBits(int count)
    {
        if (count == 0)
        {
            return 0;
        }

        if (_cacheBits < count)
        {
            RefillCache();
            if (_cacheBits < count)
            {
                throw new EndOfStreamException();
            }
        }

        uint value = (uint)(_cache >> (64 - count));
        _cache <<= count;
        _cacheBits -= count;
        return value;
    }

    /// <summary>Reads a two's-complement value of up to 64 bits.</summary>
    public long ReadSigned(int count)
    {
        if (count == 0)
        {
            return 0;
        }

        ulong raw = count <= 32
            ? ReadBits(count)
            : ((ulong)ReadBits(count - 32) << 32) | ReadBits(32);
        int shift = 64 - count;
        return (long)(raw << shift) >> shift;
    }

    /// <summary>Counts zero bits up to and including the terminating one bit.</summary>
    public int ReadUnary()
    {
        int count = 0;
        while (true)
        {
            if (_cacheBits == 0)
            {
                RefillCache();
            }

            if (_cache != 0)
            {
                int zeros = BitOperations.LeadingZeroCount(_cache);
                if (zeros < _cacheBits)
                {
                    count += zeros;
                    int consumed = zeros + 1;
                    _cache = consumed >= 64 ? 0 : _cache << consumed;
                    _cacheBits -= consumed;
                    return count;
                }
            }

            count += _cacheBits;
            _cache = 0;
            _cacheBits = 0;
        }
    }

    /// <summary>Reads one zig-zag Rice-coded residual.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long ReadRice(int parameter)
    {
        ulong quotient = (ulong)ReadUnary();
        ulong value = (quotient << parameter) | ReadBits(parameter);
        return (long)(value >> 1) ^ -(long)(value & 1);
    }

    private void RefillCache()
    {
        while (_cacheBits <= 56)
        {
            if (_bufferPosition >= _bufferLength && !Fill(1))
            {
                if (_cacheBits == 0)
                {
                    throw new EndOfStreamException();
                }

                return;
            }

            _cache |= (ulong)_buffer[_bufferPosition++] << (56 - _cacheBits);
            _cacheBits += 8;
        }
    }

    private void ReturnCacheToBuffer()
    {
        AlignToByte();
        _bufferPosition -= _cacheBits / 8;
        _cache = 0;
        _cacheBits = 0;
    }

    private bool Fill(int minimumAvailable)
    {
        int keep = Math.Min(_bufferPosition, KeepBehind);
        int start = _bufferPosition - keep;
        if (start > 0)
        {
            int remaining = _bufferLength - start;
            Buffer.BlockCopy(_buffer, start, _buffer, 0, remaining);
            _bufferFileOffset += start;
            _bufferPosition = keep;
            _bufferLength = remaining;
        }

        while (_bufferLength - _bufferPosition < minimumAvailable)
        {
            if (_bufferLength == _buffer.Length)
            {
                break;
            }

            int read = _stream.Read(_buffer, _bufferLength, _buffer.Length - _bufferLength);
            if (read <= 0)
            {
                break;
            }

            _bufferLength += read;
        }

        return _bufferLength > _bufferPosition;
    }
}
