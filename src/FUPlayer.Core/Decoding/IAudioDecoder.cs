using FUPlayer.Core.Audio;

namespace FUPlayer.Core.Decoding;

/// <summary>Streaming decoder producing planar PCM or planar DSD bytes.</summary>
public interface IAudioDecoder : IDisposable
{
    StreamFormat Format { get; }

    /// <summary>Total length in frames: PCM frames, or 1-bit samples per channel for DSD. −1 when unknown.</summary>
    long Length { get; }

    /// <summary>Current position in the same units as <see cref="Length"/>.</summary>
    long Position { get; }

    bool CanSeek { get; }

    /// <summary>Short codec or container name, for example "FLAC" or "DSF".</summary>
    string CodecName { get; }

    /// <summary>Reads planar PCM scaled to ±1.0. Returns frames read; 0 at the end of the stream.</summary>
    int ReadPcm(double[][] destination, int offset, int maxFrames);

    /// <summary>Reads planar DSD bytes (first sample in the most significant bit). Returns bytes per channel; 0 at the end.</summary>
    int ReadDsd(byte[][] destination, int offset, int maxBytes);

    /// <summary>Seeks to a PCM frame, or to a 1-bit sample for DSD (rounded down to a whole byte).</summary>
    void Seek(long position);
}

public static class AudioDecoderExtensions
{
    public static TimeSpan GetDuration(this IAudioDecoder decoder) =>
        decoder.Length < 0 ? TimeSpan.Zero : TimeSpan.FromSeconds((double)decoder.Length / decoder.Format.SampleRate);

    public static TimeSpan GetPositionTime(this IAudioDecoder decoder) =>
        TimeSpan.FromSeconds((double)decoder.Position / decoder.Format.SampleRate);

    /// <summary>Converts a time to decoder units (DSD positions are aligned to whole bytes).</summary>
    public static long ToPosition(this IAudioDecoder decoder, TimeSpan time)
    {
        long position = (long)Math.Round(time.TotalSeconds * decoder.Format.SampleRate);
        if (decoder.Format.IsDsd)
        {
            position -= position % 8;
        }

        return Math.Max(0, decoder.Length >= 0 ? Math.Min(position, decoder.Length) : position);
    }
}
