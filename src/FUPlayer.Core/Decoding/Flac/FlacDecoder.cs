using System.Buffers.Binary;
using System.Text;
using FUPlayer.Core.Audio;

namespace FUPlayer.Core.Decoding.Flac;

/// <summary>Native FLAC decoder (RFC 9639): fixed and LPC prediction, Rice partitions, stereo decorrelation, up to 32 bits.</summary>
internal sealed class FlacDecoder : IAudioDecoder
{
    private static readonly byte[] Crc8Table = BuildCrc8Table();

    private readonly Stream _stream;
    private readonly FlacBitReader _reader;
    private readonly long _firstFrameOffset;
    private readonly List<(long Sample, long Offset)> _seekPoints = [];
    private readonly int _fixedBlockSize;
    private readonly int _maxFrameSize;
    private readonly int _streamRate;
    private readonly int _streamBits;
    private readonly double _scale;
    private long[][] _samples;
    private int _frameSamples;
    private int _frameCursor;
    private long _frameStart;
    private long _position;

    private FlacDecoder(Stream stream)
    {
        _stream = stream;
        SkipId3v2(stream);

        Span<byte> marker = stackalloc byte[4];
        stream.ReadExactly(marker);
        if (Encoding.ASCII.GetString(marker) != "fLaC")
        {
            throw new InvalidDataException("Not a native FLAC stream.");
        }

        int channels = 0;
        long totalSamples = 0;
        int maxBlockSize = 0;
        bool haveStreamInfo = false;
        bool last = false;
        Span<byte> blockHeader = stackalloc byte[4];
        while (!last)
        {
            stream.ReadExactly(blockHeader);
            last = (blockHeader[0] & 0x80) != 0;
            int type = blockHeader[0] & 0x7F;
            int length = (blockHeader[1] << 16) | (blockHeader[2] << 8) | blockHeader[3];
            long start = stream.Position;

            if (type == 0)
            {
                var info = new byte[34];
                stream.ReadExactly(info);
                _fixedBlockSize = BinaryPrimitives.ReadUInt16BigEndian(info);
                maxBlockSize = BinaryPrimitives.ReadUInt16BigEndian(info.AsSpan(2));
                _maxFrameSize = (info[7] << 16) | (info[8] << 8) | info[9];
                _streamRate = (info[10] << 12) | (info[11] << 4) | (info[12] >> 4);
                channels = ((info[12] >> 1) & 0x7) + 1;
                _streamBits = (((info[12] & 0x1) << 4) | (info[13] >> 4)) + 1;
                totalSamples = ((long)(info[13] & 0x0F) << 32) | BinaryPrimitives.ReadUInt32BigEndian(info.AsSpan(14));
                haveStreamInfo = true;
            }
            else if (type == 3)
            {
                var table = new byte[length];
                stream.ReadExactly(table);
                for (int p = 0; p + 18 <= length; p += 18)
                {
                    ulong sample = BinaryPrimitives.ReadUInt64BigEndian(table.AsSpan(p));
                    if (sample != ulong.MaxValue)
                    {
                        _seekPoints.Add(((long)sample, (long)BinaryPrimitives.ReadUInt64BigEndian(table.AsSpan(p + 8))));
                    }
                }
            }

            stream.Position = start + length;
        }

        if (!haveStreamInfo || _streamRate <= 0 || _streamBits is < 4 or > 32)
        {
            throw new InvalidDataException("FLAC stream information is missing or invalid.");
        }

        _firstFrameOffset = stream.Position;
        _reader = new FlacBitReader(stream);
        _scale = 1.0 / (1L << (_streamBits - 1));
        _samples = new long[channels][];
        for (int c = 0; c < channels; c++)
        {
            _samples[c] = new long[Math.Max(maxBlockSize, 4096)];
        }

        Format = StreamFormat.Pcm(_streamRate, channels, _streamBits);
        Length = totalSamples > 0 ? totalSamples : -1;
        _seekPoints.Sort((a, b) => a.Sample.CompareTo(b.Sample));
    }

    public StreamFormat Format { get; }

    public long Length { get; }

    public long Position => _position;

    public bool CanSeek => _stream.CanSeek;

    public string CodecName => "FLAC";

    public static FlacDecoder Open(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        try
        {
            return new FlacDecoder(stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    internal static FlacDecoder Create(Stream stream) => new(stream);

    public int ReadPcm(double[][] destination, int offset, int maxFrames)
    {
        int channels = Format.Channels;
        int written = 0;
        while (written < maxFrames)
        {
            if (_frameCursor >= _frameSamples && !DecodeNextFrame())
            {
                break;
            }

            int count = Math.Min(maxFrames - written, _frameSamples - _frameCursor);
            double scale = _scale;
            for (int c = 0; c < channels; c++)
            {
                long[] source = _samples[c];
                double[] target = destination[c];
                for (int i = 0; i < count; i++)
                {
                    target[offset + written + i] = source[_frameCursor + i] * scale;
                }
            }

            _frameCursor += count;
            written += count;
            _position += count;
        }

        return written;
    }

    public int ReadDsd(byte[][] destination, int offset, int maxBytes) =>
        throw new NotSupportedException("This stream contains PCM audio.");

    public void Seek(long position)
    {
        long target = Math.Max(0, Length > 0 ? Math.Min(position, Length) : position);
        long offset = _firstFrameOffset;
        bool fromTable = false;
        foreach ((long sample, long pointOffset) in _seekPoints)
        {
            if (sample > target)
            {
                break;
            }

            offset = _firstFrameOffset + pointOffset;
            fromTable = true;
        }

        if (!fromTable && target > 0)
        {
            offset = BisectFrameOffset(target);
        }

        _reader.Reset(offset);
        _frameSamples = 0;
        _frameCursor = 0;
        while (DecodeNextFrame())
        {
            if (_frameStart + _frameSamples > target)
            {
                _frameCursor = (int)Math.Max(0, target - _frameStart);
                _position = _frameStart + _frameCursor;
                return;
            }
        }

        _position = Length > 0 ? Length : target;
    }

    public void Dispose() => _stream.Dispose();

    private long BisectFrameOffset(long target)
    {
        long lo = _firstFrameOffset;
        long hi = _stream.Length;
        long best = _firstFrameOffset;
        long span = Math.Max(_maxFrameSize, 1 << 16);
        for (int iteration = 0; iteration < 48 && hi - lo > span; iteration++)
        {
            long mid = lo + (hi - lo) / 2;
            _reader.Reset(mid);
            if (!TryFindFrame(out FrameHeader header, out long frameOffset, out _, 4 * span))
            {
                hi = mid;
                continue;
            }

            if (header.FirstSample <= target)
            {
                best = frameOffset;
                lo = frameOffset + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return best;
    }

    private bool TryFindFrame(out FrameHeader header, out long frameOffset, out int headerLength, long scanLimit)
    {
        header = default;
        frameOffset = 0;
        headerLength = 0;
        for (long scanned = 0; scanned < scanLimit; scanned++)
        {
            ReadOnlySpan<byte> peek = _reader.Peek(24);
            if (peek.Length < 6)
            {
                return false;
            }

            if (TryParseHeader(peek, out header, out headerLength))
            {
                frameOffset = _reader.BytePosition;
                return true;
            }

            _reader.SkipBytes(1);
        }

        return false;
    }

    private bool DecodeNextFrame()
    {
        try
        {
            _reader.AlignToByte();
            if (!TryFindFrame(out FrameHeader header, out _, out int headerLength, 64L << 20))
            {
                return false;
            }

            _reader.SkipBytes(headerLength);

            int blockSize = header.BlockSize;
            int channels = Format.Channels;
            if (_samples[0].Length < blockSize)
            {
                for (int c = 0; c < channels; c++)
                {
                    _samples[c] = new long[blockSize];
                }
            }

            for (int c = 0; c < channels; c++)
            {
                int bits = header.BitsPerSample;
                bool isSide = (header.ChannelCode == 8 && c == 1) || (header.ChannelCode == 9 && c == 0) || (header.ChannelCode == 10 && c == 1);
                DecodeSubframe(isSide ? bits + 1 : bits, blockSize, _samples[c]);
            }

            Decorrelate(header.ChannelCode, blockSize);
            _reader.AlignToByte();
            _reader.ReadBits(16);

            _frameStart = header.FirstSample;
            _frameSamples = blockSize;
            _frameCursor = 0;
            return true;
        }
        catch (EndOfStreamException)
        {
            return false;
        }
    }

    private void DecodeSubframe(int bitsPerSample, int blockSize, long[] output)
    {
        if (_reader.ReadBits(1) != 0)
        {
            throw new InvalidDataException("Invalid FLAC subframe padding.");
        }

        int type = (int)_reader.ReadBits(6);
        int wasted = 0;
        if (_reader.ReadBits(1) == 1)
        {
            wasted = _reader.ReadUnary() + 1;
        }

        int bits = bitsPerSample - wasted;
        if (type == 0)
        {
            long value = _reader.ReadSigned(bits);
            Array.Fill(output, value, 0, blockSize);
        }
        else if (type == 1)
        {
            for (int i = 0; i < blockSize; i++)
            {
                output[i] = _reader.ReadSigned(bits);
            }
        }
        else if (type is >= 8 and <= 12)
        {
            int order = type - 8;
            for (int i = 0; i < order; i++)
            {
                output[i] = _reader.ReadSigned(bits);
            }

            DecodeResidual(blockSize, order, output);
            RestoreFixed(order, blockSize, output);
        }
        else if (type >= 32)
        {
            int order = type - 31;
            for (int i = 0; i < order; i++)
            {
                output[i] = _reader.ReadSigned(bits);
            }

            int precision = (int)_reader.ReadBits(4) + 1;
            if (precision == 16)
            {
                throw new InvalidDataException("Invalid FLAC LPC precision.");
            }

            int shift = (int)_reader.ReadSigned(5);
            Span<long> coefficients = stackalloc long[order];
            for (int i = 0; i < order; i++)
            {
                coefficients[i] = _reader.ReadSigned(precision);
            }

            if (shift < 0)
            {
                throw new InvalidDataException("Negative FLAC LPC shift.");
            }

            DecodeResidual(blockSize, order, output);
            RestoreLpc(coefficients, shift, blockSize, output);
        }
        else
        {
            throw new InvalidDataException($"Reserved FLAC subframe type {type}.");
        }

        if (wasted > 0)
        {
            for (int i = 0; i < blockSize; i++)
            {
                output[i] <<= wasted;
            }
        }
    }

    private void DecodeResidual(int blockSize, int order, long[] output)
    {
        int method = (int)_reader.ReadBits(2);
        if (method > 1)
        {
            throw new InvalidDataException("Reserved FLAC residual coding method.");
        }

        int parameterBits = method == 0 ? 4 : 5;
        int escape = method == 0 ? 15 : 31;
        int partitionOrder = (int)_reader.ReadBits(4);
        int partitions = 1 << partitionOrder;
        int partitionSamples = blockSize >> partitionOrder;
        if (partitionSamples < order || (partitionOrder > 0 && blockSize % partitions != 0))
        {
            throw new InvalidDataException("Invalid FLAC residual partitioning.");
        }

        int index = order;
        for (int p = 0; p < partitions; p++)
        {
            int count = p == 0 ? partitionSamples - order : partitionSamples;
            int parameter = (int)_reader.ReadBits(parameterBits);
            if (parameter == escape)
            {
                int rawBits = (int)_reader.ReadBits(5);
                for (int i = 0; i < count; i++)
                {
                    output[index++] = _reader.ReadSigned(rawBits);
                }
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    output[index++] = _reader.ReadRice(parameter);
                }
            }
        }
    }

    private static void RestoreFixed(int order, int blockSize, long[] s)
    {
        switch (order)
        {
            case 1:
                for (int i = 1; i < blockSize; i++)
                {
                    s[i] += s[i - 1];
                }

                break;
            case 2:
                for (int i = 2; i < blockSize; i++)
                {
                    s[i] += 2 * s[i - 1] - s[i - 2];
                }

                break;
            case 3:
                for (int i = 3; i < blockSize; i++)
                {
                    s[i] += 3 * s[i - 1] - 3 * s[i - 2] + s[i - 3];
                }

                break;
            case 4:
                for (int i = 4; i < blockSize; i++)
                {
                    s[i] += 4 * s[i - 1] - 6 * s[i - 2] + 4 * s[i - 3] - s[i - 4];
                }

                break;
        }
    }

    private static void RestoreLpc(ReadOnlySpan<long> coefficients, int shift, int blockSize, long[] s)
    {
        int order = coefficients.Length;
        for (int i = order; i < blockSize; i++)
        {
            long sum = 0;
            for (int j = 0; j < order; j++)
            {
                sum += coefficients[j] * s[i - 1 - j];
            }

            s[i] += sum >> shift;
        }
    }

    private void Decorrelate(int channelCode, int blockSize)
    {
        if (channelCode < 8)
        {
            return;
        }

        long[] a = _samples[0];
        long[] b = _samples[1];
        switch (channelCode)
        {
            case 8:
                for (int i = 0; i < blockSize; i++)
                {
                    b[i] = a[i] - b[i];
                }

                break;
            case 9:
                for (int i = 0; i < blockSize; i++)
                {
                    a[i] += b[i];
                }

                break;
            case 10:
                for (int i = 0; i < blockSize; i++)
                {
                    long mid = (a[i] << 1) | (b[i] & 1);
                    long side = b[i];
                    a[i] = (mid + side) >> 1;
                    b[i] = (mid - side) >> 1;
                }

                break;
        }
    }

    private bool TryParseHeader(ReadOnlySpan<byte> b, out FrameHeader header, out int length)
    {
        header = default;
        length = 0;
        if (b.Length < 6 || b[0] != 0xFF || (b[1] & 0xFE) != 0xF8)
        {
            return false;
        }

        bool variable = (b[1] & 1) != 0;
        int blockCode = b[2] >> 4;
        int rateCode = b[2] & 0x0F;
        int channelCode = b[3] >> 4;
        int sizeCode = (b[3] >> 1) & 0x7;
        if ((b[3] & 1) != 0 || rateCode == 15 || blockCode == 0 || channelCode > 10 || sizeCode == 3)
        {
            return false;
        }

        int pos = 4;
        byte first = b[pos++];
        int extra;
        ulong number;
        if ((first & 0x80) == 0)
        {
            extra = 0;
            number = first;
        }
        else if ((first & 0xE0) == 0xC0)
        {
            extra = 1;
            number = (ulong)(first & 0x1F);
        }
        else if ((first & 0xF0) == 0xE0)
        {
            extra = 2;
            number = (ulong)(first & 0x0F);
        }
        else if ((first & 0xF8) == 0xF0)
        {
            extra = 3;
            number = (ulong)(first & 0x07);
        }
        else if ((first & 0xFC) == 0xF8)
        {
            extra = 4;
            number = (ulong)(first & 0x03);
        }
        else if ((first & 0xFE) == 0xFC)
        {
            extra = 5;
            number = (ulong)(first & 0x01);
        }
        else if (first == 0xFE)
        {
            extra = 6;
            number = 0;
        }
        else
        {
            return false;
        }

        if (b.Length < pos + extra + 5)
        {
            return false;
        }

        for (int i = 0; i < extra; i++)
        {
            byte c = b[pos++];
            if ((c & 0xC0) != 0x80)
            {
                return false;
            }

            number = (number << 6) | (byte)(c & 0x3F);
        }

        int blockSize;
        switch (blockCode)
        {
            case 1:
                blockSize = 192;
                break;
            case >= 2 and <= 5:
                blockSize = 576 << (blockCode - 2);
                break;
            case 6:
                blockSize = b[pos++] + 1;
                break;
            case 7:
                blockSize = ((b[pos] << 8) | b[pos + 1]) + 1;
                pos += 2;
                break;
            default:
                blockSize = 256 << (blockCode - 8);
                break;
        }

        int rate;
        switch (rateCode)
        {
            case 12:
                rate = b[pos++] * 1000;
                break;
            case 13:
                rate = (b[pos] << 8) | b[pos + 1];
                pos += 2;
                break;
            case 14:
                rate = ((b[pos] << 8) | b[pos + 1]) * 10;
                pos += 2;
                break;
            default:
                rate = rateCode switch
                {
                    0 => _streamRate, 1 => 88200, 2 => 176400, 3 => 192000, 4 => 8000, 5 => 16000,
                    6 => 22050, 7 => 24000, 8 => 32000, 9 => 44100, 10 => 48000, _ => 96000,
                };
                break;
        }

        int bits = sizeCode switch { 0 => _streamBits, 1 => 8, 2 => 12, 4 => 16, 5 => 20, 6 => 24, _ => 32 };
        int channels = channelCode <= 7 ? channelCode + 1 : 2;
        if (pos >= b.Length || Crc8(b[..pos]) != b[pos])
        {
            return false;
        }

        if (channels != Format.Channels || bits != _streamBits || rate != _streamRate)
        {
            return false;
        }

        length = pos + 1;
        long firstSample = variable ? (long)number : (long)number * _fixedBlockSize;
        header = new FrameHeader(blockSize, channelCode, bits, firstSample);
        return true;
    }

    private static void SkipId3v2(Stream stream)
    {
        Span<byte> id3 = stackalloc byte[10];
        long start = stream.Position;
        if (stream.Read(id3) == 10 && id3[0] == 'I' && id3[1] == 'D' && id3[2] == '3')
        {
            int size = (id3[6] << 21) | (id3[7] << 14) | (id3[8] << 7) | id3[9];
            bool footer = (id3[5] & 0x10) != 0;
            stream.Position = start + 10 + size + (footer ? 10 : 0);
        }
        else
        {
            stream.Position = start;
        }
    }

    private static byte Crc8(ReadOnlySpan<byte> data)
    {
        byte crc = 0;
        foreach (byte value in data)
        {
            crc = Crc8Table[crc ^ value];
        }

        return crc;
    }

    private static byte[] BuildCrc8Table()
    {
        var table = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            int crc = i;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x80) != 0 ? (crc << 1) ^ 0x07 : crc << 1;
            }

            table[i] = (byte)crc;
        }

        return table;
    }

    private readonly record struct FrameHeader(int BlockSize, int ChannelCode, int BitsPerSample, long FirstSample);
}
