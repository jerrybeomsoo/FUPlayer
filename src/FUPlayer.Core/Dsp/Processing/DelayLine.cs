namespace FUPlayer.Core.Dsp.Processing;

/// <summary>Integer-sample delay (speaker distance compensation, latency alignment).</summary>
public sealed class DelayLine
{
    private readonly double[] _buffer;
    private int _position;

    public DelayLine(int samples)
    {
        Delay = Math.Max(0, samples);
        _buffer = new double[Math.Max(1, Delay)];
    }

    public int Delay { get; }

    public void Process(Span<double> data)
    {
        if (Delay == 0)
        {
            return;
        }

        for (int i = 0; i < data.Length; i++)
        {
            (data[i], _buffer[_position]) = (_buffer[_position], data[i]);
            _position = (_position + 1) % Delay;
        }
    }

    public void Reset()
    {
        Array.Clear(_buffer);
        _position = 0;
    }
}

/// <summary>Whole-byte delay for 1-bit streams (distance compensation for DSD pass-through).</summary>
public sealed class ByteDelayLine
{
    private readonly byte[] _buffer;
    private readonly byte _fill;
    private int _position;

    public ByteDelayLine(int bytes, byte fill)
    {
        Delay = Math.Max(0, bytes);
        _fill = fill;
        _buffer = new byte[Math.Max(1, Delay)];
        Array.Fill(_buffer, fill);
    }

    public int Delay { get; }

    public void Process(Span<byte> data)
    {
        if (Delay == 0)
        {
            return;
        }

        for (int i = 0; i < data.Length; i++)
        {
            (data[i], _buffer[_position]) = (_buffer[_position], data[i]);
            _position = (_position + 1) % Delay;
        }
    }

    /// <summary>Refills the line with the idle pattern, so a seek does not play the bytes before it.</summary>
    public void Reset()
    {
        Array.Fill(_buffer, _fill);
        _position = 0;
    }
}
