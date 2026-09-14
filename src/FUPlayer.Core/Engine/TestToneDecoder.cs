using FUPlayer.Core.Audio;
using FUPlayer.Core.Decoding;
using FUPlayer.Core.Dsp.Processing;

namespace FUPlayer.Core.Engine;

public enum TestToneMode
{
    /// <summary>Pink noise on every channel at once.</summary>
    AllChannels,

    /// <summary>Pink noise moving from channel to channel every two seconds.</summary>
    Rotating,
}

/// <summary>Endless pink-noise source at −20 dBFS RMS used to calibrate speaker levels.</summary>
internal sealed class TestToneDecoder : IAudioDecoder
{
    public const string UriPrefix = "fuplayer:test-tone/";
    private const int Rate = 48_000;
    private const double Rms = 0.1;

    private readonly PinkNoiseGenerator _generator = new(20260911);
    private readonly TestToneMode _mode;
    private readonly double[] _noise = new double[4096];
    private long _position;

    public TestToneDecoder(int channels, TestToneMode mode)
    {
        _mode = mode;
        Format = StreamFormat.Pcm(Rate, Math.Max(1, channels), 24);
    }

    public StreamFormat Format { get; }

    public long Length => -1;

    public long Position => _position;

    public bool CanSeek => false;

    public string CodecName => "Pink noise";

    public static string UriFor(TestToneMode mode) => UriPrefix + (mode == TestToneMode.Rotating ? "rotate" : "all");

    public static bool TryParse(string path, out TestToneMode mode)
    {
        mode = TestToneMode.AllChannels;
        if (!path.StartsWith(UriPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        mode = path.EndsWith("rotate", StringComparison.Ordinal) ? TestToneMode.Rotating : TestToneMode.AllChannels;
        return true;
    }

    public int ReadPcm(double[][] destination, int offset, int maxFrames)
    {
        int written = 0;
        while (written < maxFrames)
        {
            int count = Math.Min(_noise.Length, maxFrames - written);
            _generator.Fill(_noise.AsSpan(0, count), Rms);
            int active = _mode == TestToneMode.Rotating ? (int)(_position / (2L * Rate) % Format.Channels) : -1;
            for (int c = 0; c < Format.Channels; c++)
            {
                Span<double> target = destination[c].AsSpan(offset + written, count);
                if (active < 0 || active == c)
                {
                    _noise.AsSpan(0, count).CopyTo(target);
                }
                else
                {
                    target.Clear();
                }
            }

            written += count;
            _position += count;
        }

        return written;
    }

    public int ReadDsd(byte[][] destination, int offset, int maxBytes) =>
        throw new NotSupportedException("The test tone is PCM.");

    public void Seek(long position) => _position = 0;

    public void Dispose()
    {
    }
}
