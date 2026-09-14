using System.Buffers.Binary;
using System.Text;
using FUPlayer.Core.Audio;
using FUPlayer.Core.Dsp.Dsd;

namespace FUPlayer.Core.Decoding;

/// <summary>Sony DSF (DSD Stream File) decoder. Data is stored planar in fixed blocks per channel.</summary>
internal sealed class DsfDecoder : IAudioDecoder
{
    private readonly Stream _stream;
    private readonly long _dataOffset;
    private readonly int _blockSize;
    private readonly bool _lsbFirst;
    private readonly long _totalBytes;
    private readonly byte[] _block;
    private long _byteIndex;
    private long _loadedBlock = -1;

    private DsfDecoder(Stream stream, long dataOffset, int channels, int rate, long samples, int blockSize, bool lsbFirst, long metadataOffset)
    {
        _stream = stream;
        _dataOffset = dataOffset;
        _blockSize = blockSize;
        _lsbFirst = lsbFirst;
        _totalBytes = (samples + 7) / 8;
        _block = new byte[blockSize * channels];
        Format = StreamFormat.Dsd(rate, channels);
        Length = samples;
        MetadataOffset = metadataOffset;
    }

    public StreamFormat Format { get; }

    public long Length { get; }

    public long Position => _byteIndex * 8;

    public bool CanSeek => true;

    public string CodecName => "DSF";

    /// <summary>File offset of the trailing ID3v2 tag, or 0.</summary>
    public long MetadataOffset { get; }

    public static DsfDecoder Open(string path)
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

    internal static DsfDecoder Create(Stream stream)
    {
        Span<byte> header = stackalloc byte[28];
        stream.ReadExactly(header);
        if (Encoding.ASCII.GetString(header[..4]) != "DSD ")
        {
            throw new InvalidDataException("Not a DSF file.");
        }

        long metadataOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(header[20..]);

        Span<byte> fmt = stackalloc byte[52];
        stream.ReadExactly(fmt);
        if (Encoding.ASCII.GetString(fmt[..4]) != "fmt ")
        {
            throw new InvalidDataException("DSF format chunk missing.");
        }

        long fmtSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(fmt[4..]);
        int formatId = BinaryPrimitives.ReadInt32LittleEndian(fmt[16..]);
        int channels = BinaryPrimitives.ReadInt32LittleEndian(fmt[24..]);
        int rate = BinaryPrimitives.ReadInt32LittleEndian(fmt[28..]);
        int bitsPerSample = BinaryPrimitives.ReadInt32LittleEndian(fmt[32..]);
        long samples = (long)BinaryPrimitives.ReadUInt64LittleEndian(fmt[36..]);
        int blockSize = BinaryPrimitives.ReadInt32LittleEndian(fmt[44..]);
        if (formatId != 0 || channels is < 1 or > 16 || rate <= 0 || blockSize <= 0 || bitsPerSample is not (1 or 8))
        {
            throw new InvalidDataException("Unsupported DSF format parameters.");
        }

        stream.Position = 28 + fmtSize;
        Span<byte> data = stackalloc byte[12];
        stream.ReadExactly(data);
        if (Encoding.ASCII.GetString(data[..4]) != "data")
        {
            throw new InvalidDataException("DSF data chunk missing.");
        }

        return new DsfDecoder(stream, stream.Position, channels, rate, samples, blockSize, bitsPerSample == 1, metadataOffset);
    }

    public int ReadPcm(double[][] destination, int offset, int maxFrames) =>
        throw new NotSupportedException("This stream contains DSD audio.");

    public int ReadDsd(byte[][] destination, int offset, int maxBytes)
    {
        int channels = Format.Channels;
        int written = 0;
        while (written < maxBytes && _byteIndex < _totalBytes)
        {
            long blockIndex = _byteIndex / _blockSize;
            if (blockIndex != _loadedBlock && !LoadBlock(blockIndex))
            {
                break;
            }

            int inBlock = (int)(_byteIndex - blockIndex * _blockSize);
            int count = (int)Math.Min(Math.Min(maxBytes - written, _blockSize - inBlock), _totalBytes - _byteIndex);
            for (int c = 0; c < channels; c++)
            {
                Buffer.BlockCopy(_block, c * _blockSize + inBlock, destination[c], offset + written, count);
            }

            written += count;
            _byteIndex += count;
        }

        return written;
    }

    public void Seek(long position)
    {
        _byteIndex = Math.Clamp(position / 8, 0, _totalBytes);
    }

    public void Dispose() => _stream.Dispose();

    private bool LoadBlock(long blockIndex)
    {
        _stream.Position = _dataOffset + blockIndex * _block.Length;
        int read = _stream.ReadAtLeast(_block, _block.Length, throwOnEndOfStream: false);
        if (read <= 0)
        {
            return false;
        }

        if (read < _block.Length)
        {
            _block.AsSpan(read).Clear();
        }

        if (_lsbFirst)
        {
            DsdConstants.ReverseBits(_block);
        }

        _loadedBlock = blockIndex;
        return true;
    }
}
