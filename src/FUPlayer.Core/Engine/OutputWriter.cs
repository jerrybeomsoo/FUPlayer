namespace FUPlayer.Core.Engine;

/// <summary>DSP-thread side of the FIFO: writes whole frames, waiting for space, abortable by engine commands.</summary>
internal sealed class OutputWriter
{
    internal delegate void AudioSink(ReadOnlySpan<byte> data);
    private readonly AudioSink? _sink;
    private readonly SpscByteRing _ring;
    private readonly AutoResetEvent _spaceAvailable;
    private readonly int _frameBytes;
    private readonly Func<bool> _shouldAbort;
    private readonly Action? _beforeWait;
    private long _discardBytes;
    private long _discarded;

    /// <summary>This writer feeds the device's FIFO, which the meters' clock reads, rather than a file or a test.</summary>
    internal bool HasRing => _sink is null;

    /// <summary>Bytes ever written to the FIFO, by any writer.</summary>
    internal long RingWritten => _ring.WriteTotal;

    /// <summary>Bytes of leading filter padding discarded so far, which the device never plays and never counts.</summary>
    internal long Discarded => Volatile.Read(ref _discarded);

    /// <summary>An engine command is waiting, and whatever is waiting on something else should give way to it.</summary>
    internal bool ShouldAbort => _shouldAbort();

    /// <summary>Leading filter padding still to be discarded before file playback reaches time zero.</summary>
    internal long RemainingDiscardBytes => Volatile.Read(ref _discardBytes);

    /// <summary>Advances seekable file output to the filter's latency-aligned start, without changing its samples.</summary>
    internal void DiscardLeadingFrames(long frames) => Volatile.Write(ref _discardBytes, checked(Math.Max(0, frames) * _frameBytes));

    /// <param name="beforeWait">Called before waiting for space (the engine starts a pre-filling device here).</param>
    public OutputWriter(SpscByteRing ring, AutoResetEvent spaceAvailable, int frameBytes, Func<bool> shouldAbort, Action? beforeWait = null)
    {
        _ring = ring;
        _spaceAvailable = spaceAvailable;
        _frameBytes = frameBytes;
        _shouldAbort = shouldAbort;
        _beforeWait = beforeWait;
    }

    /// <summary>Collects a server block without opening an audio device or a local playback FIFO.</summary>
    internal OutputWriter(AudioSink sink)
    {
        _sink = sink;
        _ring = null!;
        _spaceAvailable = null!;
        _shouldAbort = static () => false;
    }

    /// <summary>
    /// Writes <paramref name="data"/> (a whole number of frames). Returns the bytes written, which is less than the
    /// input length only when an engine command interrupted the wait; the caller keeps the remainder.
    /// </summary>
    public int Write(ReadOnlySpan<byte> data)
    {
        if (_sink is not null)
        {
            _sink(data);
            return data.Length;
        }

        int discarded = (int)Math.Min(data.Length, _discardBytes);
        discarded -= discarded % _frameBytes;
        _discardBytes -= discarded;
        Volatile.Write(ref _discarded, _discarded + discarded);
        data = data[discarded..];
        int offset = 0;
        while (offset < data.Length)
        {
            if (_shouldAbort())
            {
                break;
            }

            long free = _ring.FreeSpace;
            free -= free % _frameBytes;
            if (free <= 0)
            {
                _beforeWait?.Invoke();
                _spaceAvailable.WaitOne(20);
                continue;
            }

            offset += _ring.Write(data.Slice(offset, (int)Math.Min(free, data.Length - offset)));
        }

        return offset + discarded;
    }
}
