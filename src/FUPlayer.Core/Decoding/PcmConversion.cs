using System.Buffers.Binary;

namespace FUPlayer.Core.Decoding;

/// <summary>Uncompressed sample encodings found in WAV/AIFF containers.</summary>
internal enum PcmSampleEncoding
{
    UInt8,
    Int8,
    Int16Le,
    Int24Le,
    Int32Le,
    Float32Le,
    Float64Le,
    Int16Be,
    Int24Be,
    Int32Be,
    Float32Be,
    Float64Be,
}

internal static class PcmConversion
{
    public static int BytesPerSample(PcmSampleEncoding encoding) => encoding switch
    {
        PcmSampleEncoding.UInt8 or PcmSampleEncoding.Int8 => 1,
        PcmSampleEncoding.Int16Le or PcmSampleEncoding.Int16Be => 2,
        PcmSampleEncoding.Int24Le or PcmSampleEncoding.Int24Be => 3,
        PcmSampleEncoding.Float64Le or PcmSampleEncoding.Float64Be => 8,
        _ => 4,
    };

    public static bool IsFloat(PcmSampleEncoding encoding) => encoding is
        PcmSampleEncoding.Float32Le or PcmSampleEncoding.Float32Be or PcmSampleEncoding.Float64Le or PcmSampleEncoding.Float64Be;

    /// <summary>Converts interleaved samples to planar doubles in ±1.0.</summary>
    public static void ToPlanar(ReadOnlySpan<byte> source, PcmSampleEncoding encoding, int channels, double[][] destination, int offset, int frames)
    {
        int size = BytesPerSample(encoding);
        int p = 0;
        switch (encoding)
        {
            case PcmSampleEncoding.Int16Le:
                for (int f = 0; f < frames; f++)
                {
                    for (int c = 0; c < channels; c++, p += 2)
                    {
                        destination[c][offset + f] = (short)(source[p] | (source[p + 1] << 8)) / 32768.0;
                    }
                }

                break;

            case PcmSampleEncoding.Int24Le:
                for (int f = 0; f < frames; f++)
                {
                    for (int c = 0; c < channels; c++, p += 3)
                    {
                        int v = source[p] | (source[p + 1] << 8) | ((sbyte)source[p + 2] << 16);
                        destination[c][offset + f] = v / 8388608.0;
                    }
                }

                break;

            case PcmSampleEncoding.Int32Le:
                for (int f = 0; f < frames; f++)
                {
                    for (int c = 0; c < channels; c++, p += 4)
                    {
                        destination[c][offset + f] = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(p, 4)) / 2147483648.0;
                    }
                }

                break;

            default:
                for (int f = 0; f < frames; f++)
                {
                    for (int c = 0; c < channels; c++, p += size)
                    {
                        destination[c][offset + f] = ReadSample(source.Slice(p, size), encoding);
                    }
                }

                break;
        }
    }

    private static double ReadSample(ReadOnlySpan<byte> s, PcmSampleEncoding encoding) => encoding switch
    {
        PcmSampleEncoding.UInt8 => (s[0] - 128) / 128.0,
        PcmSampleEncoding.Int8 => (sbyte)s[0] / 128.0,
        PcmSampleEncoding.Int16Be => BinaryPrimitives.ReadInt16BigEndian(s) / 32768.0,
        PcmSampleEncoding.Int24Be => (s[2] | (s[1] << 8) | ((sbyte)s[0] << 16)) / 8388608.0,
        PcmSampleEncoding.Int32Be => BinaryPrimitives.ReadInt32BigEndian(s) / 2147483648.0,
        PcmSampleEncoding.Float32Le => BinaryPrimitives.ReadSingleLittleEndian(s),
        PcmSampleEncoding.Float64Le => BinaryPrimitives.ReadDoubleLittleEndian(s),
        PcmSampleEncoding.Float32Be => BinaryPrimitives.ReadSingleBigEndian(s),
        PcmSampleEncoding.Float64Be => BinaryPrimitives.ReadDoubleBigEndian(s),
        PcmSampleEncoding.Int16Le => BinaryPrimitives.ReadInt16LittleEndian(s) / 32768.0,
        PcmSampleEncoding.Int24Le => (s[0] | (s[1] << 8) | ((sbyte)s[2] << 16)) / 8388608.0,
        PcmSampleEncoding.Int32Le => BinaryPrimitives.ReadInt32LittleEndian(s) / 2147483648.0,
        _ => 0.0,
    };
}

/// <summary>Common implementation for containers holding uncompressed, block-aligned PCM.</summary>
internal abstract class UncompressedPcmDecoder : IAudioDecoder
{
    private readonly Stream _stream;
    private readonly long _dataOffset;
    private readonly int _blockAlign;
    private readonly PcmSampleEncoding _encoding;
    private byte[] _scratch = [];
    private long _frame;

    protected UncompressedPcmDecoder(Stream stream, long dataOffset, long dataBytes, int channels, int sampleRate, int bitsPerSample, PcmSampleEncoding encoding, string codecName)
    {
        _stream = stream;
        _dataOffset = dataOffset;
        _encoding = encoding;
        _blockAlign = channels * PcmConversion.BytesPerSample(encoding);
        long available = Math.Max(0, Math.Min(dataBytes, stream.Length - dataOffset));
        Length = available / _blockAlign;
        Format = Audio.StreamFormat.Pcm(sampleRate, channels, bitsPerSample);
        CodecName = codecName;
        _stream.Position = dataOffset;
    }

    public Audio.StreamFormat Format { get; }

    public long Length { get; }

    public long Position => _frame;

    public bool CanSeek => _stream.CanSeek;

    public string CodecName { get; }

    public int ReadPcm(double[][] destination, int offset, int maxFrames)
    {
        int frames = (int)Math.Min(maxFrames, Length - _frame);
        if (frames <= 0)
        {
            return 0;
        }

        int bytes = frames * _blockAlign;
        if (_scratch.Length < bytes)
        {
            _scratch = new byte[bytes];
        }

        int read = _stream.ReadAtLeast(_scratch.AsSpan(0, bytes), bytes, throwOnEndOfStream: false);
        frames = read / _blockAlign;
        PcmConversion.ToPlanar(_scratch.AsSpan(0, frames * _blockAlign), _encoding, Format.Channels, destination, offset, frames);
        _frame += frames;
        return frames;
    }

    public int ReadDsd(byte[][] destination, int offset, int maxBytes) =>
        throw new NotSupportedException("This stream contains PCM audio.");

    public void Seek(long position)
    {
        _frame = Math.Clamp(position, 0, Length);
        _stream.Position = _dataOffset + _frame * _blockAlign;
    }

    public void Dispose() => _stream.Dispose();
}
