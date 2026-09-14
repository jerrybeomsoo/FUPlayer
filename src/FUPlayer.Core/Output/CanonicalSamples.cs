using System.Buffers.Binary;
using FUPlayer.Core.Dsp.Dsd;

namespace FUPlayer.Core.Output;

/// <summary>Conversions from the canonical render stream to device sample layouts.</summary>
public static class CanonicalSamples
{
    /// <summary>Copies canonical int32 samples into a little-endian integer container of 16, 24 or 32 bits.</summary>
    public static void ToInteger(ReadOnlySpan<byte> canonical, Span<byte> destination, int containerBits)
    {
        int samples = canonical.Length / 4;
        switch (containerBits)
        {
            case 32:
                canonical[..(samples * 4)].CopyTo(destination);
                break;
            case 24:
                for (int i = 0, s = 0, d = 0; i < samples; i++, s += 4, d += 3)
                {
                    destination[d] = canonical[s + 1];
                    destination[d + 1] = canonical[s + 2];
                    destination[d + 2] = canonical[s + 3];
                }

                break;
            case 16:
                for (int i = 0, s = 0, d = 0; i < samples; i++, s += 4, d += 2)
                {
                    destination[d] = canonical[s + 2];
                    destination[d + 1] = canonical[s + 3];
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(containerBits));
        }
    }

    /// <summary>Converts canonical int32 samples to little-endian 32-bit float.</summary>
    public static void ToFloat32(ReadOnlySpan<byte> canonical, Span<byte> destination)
    {
        int samples = canonical.Length / 4;
        for (int i = 0, p = 0; i < samples; i++, p += 4)
        {
            float value = BinaryPrimitives.ReadInt32LittleEndian(canonical.Slice(p, 4)) / 2147483648f;
            BinaryPrimitives.WriteSingleLittleEndian(destination.Slice(p, 4), value);
        }
    }

    /// <summary>Writes silence for a canonical format.</summary>
    public static void FillSilence(Span<byte> destination, OutputSampleKind kind)
    {
        if (kind == OutputSampleKind.NativeDsd)
        {
            destination.Fill(DsdConstants.SilenceByte);
        }
        else
        {
            destination.Clear();
        }
    }
}
