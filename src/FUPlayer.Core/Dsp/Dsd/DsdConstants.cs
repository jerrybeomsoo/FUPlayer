namespace FUPlayer.Core.Dsp.Dsd;

/// <summary>DSD stream constants and bit-order helpers.</summary>
public static class DsdConstants
{
    /// <summary>Idle pattern (0x69) producing silence with 50 % density.</summary>
    public const byte SilenceByte = 0x69;

    /// <summary>DoP marker used on even frames.</summary>
    public const byte DopMarkerA = 0x05;

    /// <summary>DoP marker used on odd frames.</summary>
    public const byte DopMarkerB = 0xFA;

    private static readonly byte[] ReverseTable = BuildReverseTable();

    /// <summary>Byte with its bit order reversed (LSB-first ⇄ MSB-first).</summary>
    public static byte Reverse(byte value) => ReverseTable[value];

    /// <summary>Reverses the bit order of every byte in place.</summary>
    public static void ReverseBits(Span<byte> data)
    {
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = ReverseTable[data[i]];
        }
    }

    private static byte[] BuildReverseTable()
    {
        var table = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            int r = 0;
            for (int b = 0; b < 8; b++)
            {
                if ((i & (1 << b)) != 0)
                {
                    r |= 1 << (7 - b);
                }
            }

            table[i] = (byte)r;
        }

        return table;
    }
}
