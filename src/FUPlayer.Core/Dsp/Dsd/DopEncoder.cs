namespace FUPlayer.Core.Dsp.Dsd;

/// <summary>
/// DSD over PCM (DoP v1.1) packer. Each PCM frame carries 16 DSD bits per channel in the lower 16 bits of a
/// 24-bit sample; the top byte alternates 0x05 / 0xFA every frame. Output is left-justified in 32-bit integers.
/// </summary>
/// <remarks>
/// There is deliberately no way to restart the marker sequence. A DAC recognises DoP by the markers alternating
/// without interruption, so the sequence has to run on across seeks and silence; only a new stream starts it over.
/// </remarks>
public sealed class DopEncoder
{
    private bool _useMarkerA = true;

    /// <summary>Packs <paramref name="byteCount"/> DSD bytes per channel (must be even) into interleaved DoP frames.</summary>
    /// <returns>Number of frames written.</returns>
    public int Encode(IReadOnlyList<byte[]> channels, int channelCount, int byteCount, Span<int> interleaved)
    {
        int frames = byteCount / 2;
        if (interleaved.Length < frames * channelCount)
        {
            throw new ArgumentException("Output buffer is too small.", nameof(interleaved));
        }

        for (int f = 0; f < frames; f++)
        {
            uint marker = _useMarkerA ? DsdConstants.DopMarkerA : DsdConstants.DopMarkerB;
            _useMarkerA = !_useMarkerA;
            int offset = f * channelCount;
            for (int c = 0; c < channelCount; c++)
            {
                byte[] data = channels[c];
                interleaved[offset + c] = (int)((marker << 24) | ((uint)data[2 * f] << 16) | ((uint)data[2 * f + 1] << 8));
            }
        }

        return frames;
    }

    /// <summary>Writes DoP silence frames, continuing the marker sequence.</summary>
    public void EncodeSilence(int channelCount, int frames, Span<int> interleaved)
    {
        uint pattern = ((uint)DsdConstants.SilenceByte << 16) | ((uint)DsdConstants.SilenceByte << 8);
        for (int f = 0; f < frames; f++)
        {
            uint marker = _useMarkerA ? DsdConstants.DopMarkerA : DsdConstants.DopMarkerB;
            _useMarkerA = !_useMarkerA;
            int value = (int)((marker << 24) | pattern);
            interleaved.Slice(f * channelCount, channelCount).Fill(value);
        }
    }
}
