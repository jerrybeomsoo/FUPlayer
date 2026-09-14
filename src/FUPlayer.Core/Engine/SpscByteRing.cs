using System.Numerics;

namespace FUPlayer.Core.Engine;

/// <summary>Lock-free single-producer / single-consumer byte FIFO between the DSP thread and the device thread.</summary>
public sealed class SpscByteRing
{
    private readonly byte[] _buffer;
    private readonly int _mask;
    private long _writeTotal;
    private long _readTotal;
    private int _clearRequested;

    public SpscByteRing(int minimumCapacity)
    {
        int capacity = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(4096, minimumCapacity));
        _buffer = new byte[capacity];
        _mask = capacity - 1;
    }

    public int Capacity => _buffer.Length;

    /// <summary>Bytes ever written by the producer.</summary>
    public long WriteTotal => Volatile.Read(ref _writeTotal);

    /// <summary>Bytes ever consumed (or discarded by a clear) by the consumer.</summary>
    public long ReadTotal => Volatile.Read(ref _readTotal);

    public long Count => WriteTotal - ReadTotal;

    public long FreeSpace => Capacity - Count;

    public bool IsClearPending => Volatile.Read(ref _clearRequested) != 0;

    /// <summary>Producer side: writes as much as fits; returns bytes written.</summary>
    public int Write(ReadOnlySpan<byte> data)
    {
        long write = _writeTotal;
        long read = Volatile.Read(ref _readTotal);
        int free = Capacity - (int)(write - read);
        int count = Math.Min(free, data.Length);
        if (count <= 0)
        {
            return 0;
        }

        int start = (int)(write & _mask);
        int first = Math.Min(count, Capacity - start);
        data[..first].CopyTo(_buffer.AsSpan(start));
        if (count > first)
        {
            data[first..count].CopyTo(_buffer);
        }

        Volatile.Write(ref _writeTotal, write + count);
        return count;
    }

    /// <summary>Consumer side: reads up to the destination length; honours pending clear requests first.</summary>
    public int Read(Span<byte> destination)
    {
        long read = _readTotal;
        long write = Volatile.Read(ref _writeTotal);
        if (Interlocked.Exchange(ref _clearRequested, 0) != 0)
        {
            read = write;
            Volatile.Write(ref _readTotal, read);
        }

        int count = Math.Min((int)(write - read), destination.Length);
        if (count <= 0)
        {
            return 0;
        }

        int start = (int)(read & _mask);
        int first = Math.Min(count, Capacity - start);
        _buffer.AsSpan(start, first).CopyTo(destination);
        if (count > first)
        {
            _buffer.AsSpan(0, count - first).CopyTo(destination[first..]);
        }

        Volatile.Write(ref _readTotal, read + count);
        return count;
    }

    /// <summary>Asks the consumer to discard everything buffered on its next read.</summary>
    public void RequestClear() => Volatile.Write(ref _clearRequested, 1);

    /// <summary>Discards everything immediately. Only valid while no consumer is running.</summary>
    public void ClearNow()
    {
        Volatile.Write(ref _readTotal, Volatile.Read(ref _writeTotal));
        Volatile.Write(ref _clearRequested, 0);
    }
}
