using System.Buffers.Binary;
using System.Text;

namespace FUPlayer.Core.Decoding;

/// <summary>AIFF and AIFF-C decoder (uncompressed, little-endian "sowt" and floating-point variants).</summary>
internal sealed class AiffDecoder : UncompressedPcmDecoder
{
    private AiffDecoder(Stream stream, long dataOffset, long dataBytes, int channels, int rate, int bits, PcmSampleEncoding encoding)
        : base(stream, dataOffset, dataBytes, channels, rate, bits, encoding, "AIFF")
    {
    }

    public static AiffDecoder Open(string path)
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

    internal static AiffDecoder Create(Stream stream)
    {
        Span<byte> header = stackalloc byte[12];
        stream.ReadExactly(header);
        string form = Encoding.ASCII.GetString(header[..4]);
        string kind = Encoding.ASCII.GetString(header[8..12]);
        if (form != "FORM" || kind is not ("AIFF" or "AIFC"))
        {
            throw new InvalidDataException("Not an AIFF file.");
        }

        bool isAifc = kind == "AIFC";
        int channels = 0, bits = 0;
        double rate = 0;
        string compression = "NONE";
        bool haveCommon = false;
        Span<byte> chunk = stackalloc byte[8];

        while (stream.Position + 8 <= stream.Length)
        {
            stream.ReadExactly(chunk);
            string id = Encoding.ASCII.GetString(chunk[..4]);
            uint size = BinaryPrimitives.ReadUInt32BigEndian(chunk[4..]);
            long start = stream.Position;

            if (id == "COMM")
            {
                // Channels, frames, bits and the 80-bit rate take 18 bytes; a shorter chunk is damage, and reading
                // past it would throw something no caller expects of a file that is merely broken.
                if (size < 18)
                {
                    throw new InvalidDataException("AIFF common chunk is too short.");
                }

                var common = new byte[Math.Min(size, 64u)];
                stream.ReadExactly(common);
                channels = BinaryPrimitives.ReadInt16BigEndian(common);
                bits = BinaryPrimitives.ReadInt16BigEndian(common.AsSpan(6));
                rate = ReadExtended(common.AsSpan(8, 10));
                if (isAifc && common.Length >= 22)
                {
                    compression = Encoding.ASCII.GetString(common, 18, 4);
                }

                haveCommon = true;
            }
            else if (id == "SSND")
            {
                if (!haveCommon)
                {
                    throw new InvalidDataException("AIFF sound data appears before the common chunk.");
                }

                Span<byte> ssnd = stackalloc byte[8];
                stream.ReadExactly(ssnd);
                uint offset = BinaryPrimitives.ReadUInt32BigEndian(ssnd);
                long dataOffset = start + 8 + offset;
                long dataBytes = Math.Min((long)size - 8 - offset, stream.Length - dataOffset);

                PcmSampleEncoding encoding = (compression, bits) switch
                {
                    ("NONE" or "twos", 8) => PcmSampleEncoding.Int8,
                    ("NONE" or "twos", 16) => PcmSampleEncoding.Int16Be,
                    ("NONE" or "twos" or "in24", 24) => PcmSampleEncoding.Int24Be,
                    ("NONE" or "twos" or "in32", 32) => PcmSampleEncoding.Int32Be,
                    ("raw ", 8) => PcmSampleEncoding.UInt8,
                    ("sowt", 16) => PcmSampleEncoding.Int16Le,
                    ("sowt", 24) => PcmSampleEncoding.Int24Le,
                    ("sowt", 32) => PcmSampleEncoding.Int32Le,
                    ("fl32" or "FL32", _) => PcmSampleEncoding.Float32Be,
                    ("fl64" or "FL64", _) => PcmSampleEncoding.Float64Be,
                    _ => throw new NotSupportedException($"AIFF-C compression '{compression}' is not decoded natively."),
                };

                int reportedBits = PcmConversion.IsFloat(encoding) ? PcmConversion.BytesPerSample(encoding) * 8 : bits;
                return new AiffDecoder(stream, dataOffset, dataBytes, channels, (int)Math.Round(rate), reportedBits, encoding);
            }

            stream.Position = start + size + (size & 1);
        }

        throw new InvalidDataException("AIFF file has no sound data.");
    }

    /// <summary>Reads an 80-bit IEEE 754 extended-precision big-endian number.</summary>
    internal static double ReadExtended(ReadOnlySpan<byte> bytes)
    {
        int exponent = ((bytes[0] & 0x7F) << 8) | bytes[1];
        ulong mantissa = BinaryPrimitives.ReadUInt64BigEndian(bytes[2..]);
        if (exponent == 0 && mantissa == 0)
        {
            return 0.0;
        }

        double value = Math.ScaleB((double)mantissa, exponent - 16383 - 63);
        return (bytes[0] & 0x80) != 0 ? -value : value;
    }
}
