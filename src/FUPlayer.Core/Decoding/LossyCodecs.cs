namespace FUPlayer.Core.Decoding;

/// <summary>
/// Which sources came through a perceptual codec, going by what their decoder reports.
///
/// It matters for one reason: a lossy decode does not stay inside full scale. The encoder threw the
/// top of the spectrum away, and a waveform with its highest harmonics removed overshoots where the
/// original did not, so a master that peaked at -0.1 dBFS decodes from Ogg Vorbis at +0.6 dBFS and
/// from a low bit rate at +3 or more. Those overs are the codec's, not the recording's, and nothing
/// downstream wants them: a PCM output clips them into a spray of harmonics above 40 kHz, and a
/// 9th-order modulator driven past about +3 dBFS goes unstable, which measured as 11 resets at
/// +3.1 dBFS and 2,969 at +4.1 on one track.
/// </summary>
public static class LossyCodecs
{
    /// <summary>
    /// Codec names as FFmpeg reports them, upper-cased by the decoder. Deliberately a list of what is
    /// known to be lossy rather than of what is known to be lossless, so that an unrecognised codec is
    /// left alone rather than limited on a guess.
    /// </summary>
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "MP1", "MP2", "MP3", "MP3ADU", "MP3ON4", "MP3FLOAT",
        "AAC", "AAC_LATM", "AAC_FIXED",
        "VORBIS", "OPUS", "SPEEX",
        "WMAV1", "WMAV2", "WMAPRO", "WMAVOICE",
        "AC3", "AC3_FIXED", "EAC3", "DTS",
        "MUSEPACK7", "MUSEPACK8", "MPC7", "MPC8",
        "ATRAC1", "ATRAC3", "ATRAC3P", "ATRAC9",
        "COOK", "RA_144", "RA_288", "NELLYMOSER", "QDM2", "QDMC",
        "AMRNB", "AMRWB", "GSM", "GSM_MS", "G723_1", "G729",
    };

    /// <summary>True when the decoder's codec is a perceptual one, or when the source is captured.</summary>
    public static bool IsLossy(IAudioDecoder decoder)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        return IsLossy(decoder.CodecName);
    }

    /// <summary>
    /// True for a known lossy codec name, and for live capture, which is another application's output
    /// and so, in practice, a streaming service's decode: Apple Music, Spotify and YouTube all deliver a
    /// perceptual codec, and the mix a loopback capture returns is floating point and can exceed full
    /// scale on its own.
    /// </summary>
    public static bool IsLossy(string? codecName)
    {
        if (string.IsNullOrWhiteSpace(codecName))
        {
            return false;
        }

        return codecName.StartsWith("Live capture", StringComparison.OrdinalIgnoreCase) || Names.Contains(codecName.Trim());
    }
}
