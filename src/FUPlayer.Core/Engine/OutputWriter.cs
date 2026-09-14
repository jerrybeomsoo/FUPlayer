namespace FUPlayer.Core.Engine;

/// <summary>DSP-thread side of the FIFO: writes whole frames, waiting for space, abortable by engine commands.</summary>
internal sealed class OutputWriter
{
    private readonly SpscByteRing _ring;
    private readonly AutoResetEvent _spaceAvailable;
    private readonly int _frameBytes;
    private readonly Func<bool> _shouldAbort;
    private readonly Action? _beforeWait;

    /// <param name="beforeWait">Called before waiting for space (the engine starts a pre-filling device here).</param>
    public OutputWriter(SpscByteRing ring, AutoResetEvent spaceAvailable, int frameBytes, Func<bool> shouldAbort, Action? beforeWait = null)
    {
        _ring = ring;
        _spaceAvailable = spaceAvailable;
        _frameBytes = frameBytes;
        _shouldAbort = shouldAbort;
        _beforeWait = beforeWait;
    }

    /// <summary>
    /// Writes <paramref name="data"/> (a whole number of frames). Returns the bytes written, which is less than the
    /// input length only when an engine command interrupted the wait; the caller keeps the remainder.
    /// </summary>
    public int Write(ReadOnlySpan<byte> data)
    {
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

        return offset;
    }
}
