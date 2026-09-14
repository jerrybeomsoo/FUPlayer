using System.Buffers.Binary;
using System.Text;
using FUPlayer.Core.Audio;

namespace FUPlayer.Core.Decoding;

/// <summary>Philips DSDIFF (DFF) decoder for uncompressed DSD. DST-compressed files are left to FFmpeg.</summary>
internal sealed class DffDecoder : IAudioDecoder
{
    private readonly Stream _stream;
    private readonly long _dataOffset;
    private readonly long _totalBytes;
    private byte[] _scratch = [];
    private long _byteIndex;

    private DffDecoder(Stream stream, long dataOffset, long dataBytes, int channels, int rate)
    {
        _stream = stream;
        _dataOffset = dataOffset;
        _totalBytes = Math.Min(dataBytes, stream.Length - dataOffset) / channels;
        Format = StreamFormat.Dsd(rate, channels);
        Length = _totalBytes * 8;
        _stream.Position = dataOffset;
    }

    public StreamFormat Format { get; }

    public long Length { get; }

    public long Position => _byteIndex * 8;

    public bool CanSeek => true;

    public string CodecName => "DSDIFF";

    public static DffDecoder Open(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        try
        {
            return Create(stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    internal static DffDecoder Create(Stream stream)
    {
        Span<byte> header = stackalloc byte[16];
        stream.ReadExactly(header);
        if (Encoding.ASCII.GetString(header[..4]) != "FRM8" || Encoding.ASCII.GetString(header[12..16]) != "DSD ")
        {
            throw new InvalidDataException("Not a DSDIFF file.");
        }

        int rate = 0;
        int channels = 0;
        string compression = "DSD ";
        Span<byte> chunk = stackalloc byte[12];

        while (stream.Position + 12 <= stream.Length)
        {
            stream.ReadExactly(chunk);
            string id = Encoding.ASCII.GetString(chunk[..4]);
            long size = (long)BinaryPrimitives.ReadUInt64BigEndian(chunk[4..]);
            long start = stream.Position;

            switch (id)
            {
                case "PROP":
                    ParseProperties(stream, start, size, ref rate, ref channels, ref compression);
                    break;
                case "DSD ":
                    if (rate <= 0 || channels <= 0)
                    {
                        throw new InvalidDataException("DSDIFF sound data appears before its properties.");
                    }

                    return new DffDecoder(stream, start, size, channels, rate);
                case "DST ":
                    throw new NotSupportedException("DST-compressed DSDIFF is not decoded natively.");
            }

            stream.Position = start + size + (size & 1);
        }

        throw new InvalidDataException(compression == "DSD " ? "DSDIFF file has no sound data." : "Unsupported DSDIFF compression.");
    }

    public int ReadPcm(double[][] destination, int offset, int maxFrames) =>
        throw new NotSupportedException("This stream contains DSD audio.");

    public int ReadDsd(byte[][] destination, int offset, int maxBytes)
    {
        int channels = Format.Channels;
        int count = (int)Math.Min(maxBytes, _totalBytes - _byteIndex);
        if (count <= 0)
        {
            return 0;
        }

        int bytes = count * channels;
        if (_scratch.Length < bytes)
        {
            _scratch = new byte[bytes];
        }

        int read = _stream.ReadAtLeast(_scratch.AsSpan(0, bytes), bytes, throwOnEndOfStream: false);
        count = read / channels;
        for (int i = 0, p = 0; i < count; i++)
        {
            for (int c = 0; c < channels; c++)
            {
                destination[c][offset + i] = _scratch[p++];
            }
        }

        _byteIndex += count;
        return count;
    }

    public void Seek(long position)
    {
        _byteIndex = Math.Clamp(position / 8, 0, _totalBytes);
        _stream.Position = _dataOffset + _byteIndex * Format.Channels;
    }

    public void Dispose() => _stream.Dispose();

    private static void ParseProperties(Stream stream, long start, long size, ref int rate, ref int channels, ref string compression)
    {
        Span<byte> type = stackalloc byte[4];
        stream.ReadExactly(type);
        if (Encoding.ASCII.GetString(type) != "SND ")
        {
            return;
        }

        long end = start + size;
        Span<byte> chunk = stackalloc byte[12];
        Span<byte> value = stackalloc byte[4];
        while (stream.Position + 12 <= end)
        {
            stream.ReadExactly(chunk);
            string id = Encoding.ASCII.GetString(chunk[..4]);
            long chunkSize = (long)BinaryPrimitives.ReadUInt64BigEndian(chunk[4..]);
            long chunkStart = stream.Position;

            switch (id)
            {
                case "FS  ":
                    stream.ReadExactly(value);
                    rate = (int)BinaryPrimitives.ReadUInt32BigEndian(value);
                    break;
                case "CHNL":
                    stream.ReadExactly(value[..2]);
                    channels = BinaryPrimitives.ReadUInt16BigEndian(value);
                    break;
                case "CMPR":
                    stream.ReadExactly(value);
                    compression = Encoding.ASCII.GetString(value);
                    break;
            }

            stream.Position = chunkStart + chunkSize + (chunkSize & 1);
        }
    }
}
