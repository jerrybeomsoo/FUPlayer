using System.Text;

namespace FUPlayer.Core.Decoding;

/// <summary>RIFF WAVE, RF64 and BW64 decoder for integer and floating-point PCM.</summary>
internal sealed class WavDecoder : UncompressedPcmDecoder
{
    private WavDecoder(Stream stream, long dataOffset, long dataBytes, int channels, int rate, int bits, PcmSampleEncoding encoding, string codec)
        : base(stream, dataOffset, dataBytes, channels, rate, bits, encoding, codec)
    {
    }

    public static WavDecoder Open(string path)
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

    internal static WavDecoder Create(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        string riff = ReadId(reader);
        reader.ReadUInt32();
        string wave = ReadId(reader);
        if (riff is not ("RIFF" or "RF64" or "BW64") || wave != "WAVE")
        {
            throw new InvalidDataException("Not a WAVE file.");
        }

        long ds64DataSize = -1;
        int channels = 0, rate = 0, bits = 0, validBits = 0;
        int formatTag = 0;
        bool haveFormat = false;

        while (stream.Position + 8 <= stream.Length)
        {
            string id = ReadId(reader);
            uint size = reader.ReadUInt32();
            long start = stream.Position;

            if (id == "ds64" && size >= 16)
            {
                reader.ReadUInt64();
                ds64DataSize = (long)reader.ReadUInt64();
            }
            else if (id == "fmt " && size >= 16)
            {
                formatTag = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                rate = (int)reader.ReadUInt32();
                reader.ReadUInt32();
                reader.ReadUInt16();
                bits = reader.ReadUInt16();
                validBits = bits;
                if (formatTag == 0xFFFE && size >= 40)
                {
                    reader.ReadUInt16();
                    validBits = reader.ReadUInt16();
                    reader.ReadUInt32();
                    byte[] guid = reader.ReadBytes(16);
                    formatTag = guid[0] | (guid[1] << 8);
                }

                haveFormat = true;
            }
            else if (id == "data")
            {
                if (!haveFormat)
                {
                    throw new InvalidDataException("WAVE data chunk appears before the format chunk.");
                }

                long dataBytes = size == 0xFFFFFFFF && ds64DataSize >= 0 ? ds64DataSize : size;
                if (size == 0 || dataBytes > stream.Length - start)
                {
                    dataBytes = stream.Length - start;
                }

                PcmSampleEncoding encoding = (formatTag, bits) switch
                {
                    (1, 8) => PcmSampleEncoding.UInt8,
                    (1, 16) => PcmSampleEncoding.Int16Le,
                    (1, 24) => PcmSampleEncoding.Int24Le,
                    (1, 32) => PcmSampleEncoding.Int32Le,
                    (3, 32) => PcmSampleEncoding.Float32Le,
                    (3, 64) => PcmSampleEncoding.Float64Le,
                    _ => throw new NotSupportedException($"WAVE format tag {formatTag} with {bits} bits is not decoded natively."),
                };

                if (channels <= 0 || rate <= 0)
                {
                    throw new InvalidDataException("Invalid WAVE format chunk.");
                }

                int reportedBits = PcmConversion.IsFloat(encoding) ? bits : Math.Clamp(validBits, 1, bits);
                string codec = riff == "RIFF" ? "WAV" : riff;
                return new WavDecoder(stream, start, dataBytes, channels, rate, reportedBits, encoding, codec);
            }

            stream.Position = start + size + (size & 1);
        }

        throw new InvalidDataException("WAVE file has no data chunk.");
    }

    private static string ReadId(BinaryReader reader) => Encoding.ASCII.GetString(reader.ReadBytes(4));
}
