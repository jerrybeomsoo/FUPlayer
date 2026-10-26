using System.Numerics;

namespace FUPlayer.Core.Engine;

/// <summary>Lock-free single-producer / single-consumer byte FIFO between the DSP thread and the device thread.</summary>
public sealed class SpscByteRing
{
    private readonly byte[] _buffer;
    private readonly int _mask;
    private readonly int _capacity;
    private long _writeTotal;
    private long _readTotal;
    private long _clearPosition;
    private int _clearRequested;

    /// <summary>Can limit usable bytes exactly while retaining power-of-two storage for cheap wraparound.</summary>
    public SpscByteRing(int minimumCapacity, bool exactCapacity = false)
    {
        int capacity = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(4096, minimumCapacity));
        _buffer = new byte[capacity];
        _mask = capacity - 1;
        _capacity = exactCapacity ? Math.Max(1, minimumCapacity) : capacity;
    }

    public int Capacity => _capacity;

    /// <summary>Bytes ever written by the producer.</summary>
    public long WriteTotal => Volatile.Read(ref _writeTotal);

    /// <summary>Bytes ever consumed (or discarded by a clear) by the consumer.</summary>
    public long ReadTotal => Volatile.Read(ref _readTotal);

    public long Count
    {
        get
        {
            // Read first: an observer must not subtract a newer consumer position from an older write.
            // Both can move during a UI snapshot, whose result still has to fit the physical capacity.
            long read = ReadTotal;
            return Math.Clamp(WriteTotal - read, 0, Capacity);
        }
    }

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
        int first = Math.Min(count, _buffer.Length - start);
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
            // Discard only the audio present when reset was requested. A delayed device callback must
            // retain new seek/restart audio written after that boundary, and must never move backwards.
            long boundary = Volatile.Read(ref _clearPosition);
            write = Volatile.Read(ref _writeTotal);
            read = Math.Max(read, Math.Min(boundary, write));
            Volatile.Write(ref _readTotal, read);
        }

        int count = Math.Min((int)(write - read), destination.Length);
        if (count <= 0)
        {
            return 0;
        }

        int start = (int)(read & _mask);
        int first = Math.Min(count, _buffer.Length - start);
        _buffer.AsSpan(start, first).CopyTo(destination);
        if (count > first)
        {
            _buffer.AsSpan(0, count - first).CopyTo(destination[first..]);
        }

        Volatile.Write(ref _readTotal, read + count);
        return count;
    }

    /// <summary>Asks the consumer to discard the audio buffered now, retaining later writes.</summary>
    public void RequestClear()
    {
        Volatile.Write(ref _clearPosition, Volatile.Read(ref _writeTotal));
        Volatile.Write(ref _clearRequested, 1);
    }

    /// <summary>Discards everything immediately. Only valid while no consumer is running.</summary>
    public void ClearNow()
    {
        Volatile.Write(ref _readTotal, Volatile.Read(ref _writeTotal));
        Volatile.Write(ref _clearRequested, 0);
    }
}
