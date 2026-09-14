using System.Text;
using FUPlayer.Core.Decoding.FFmpeg;
using FUPlayer.Core.Decoding.Flac;

namespace FUPlayer.Core.Decoding;

/// <summary>Thrown when no decoder can open a file.</summary>
public sealed class AudioDecoderException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Chooses a decoder by file content: native managed decoders first, FFmpeg for everything else.</summary>
public static class DecoderFactory
{
    private static readonly string[] NativeExtensionList = [".wav", ".wave", ".rf64", ".bw64", ".aif", ".aiff", ".aifc", ".flac", ".dsf", ".dff", ".dsdiff"];

    private static readonly string[] FFmpegExtensionList =
    [
        ".mp3", ".mp2", ".m4a", ".m4b", ".mp4", ".aac", ".wv", ".ape", ".ogg", ".oga", ".opus", ".tta", ".tak",
        ".wma", ".mka", ".webm", ".ac3", ".eac3", ".dts", ".caf", ".mpc", ".w64",
    ];

    private static readonly HashSet<string> NativeExtensions = new(NativeExtensionList, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> FFmpegExtensions = new(FFmpegExtensionList, StringComparer.OrdinalIgnoreCase);

    internal enum Container
    {
        Unknown,
        Wav,
        Aiff,
        Flac,
        Dsf,
        Dff,
    }

    public static IReadOnlyList<string> NativeFileExtensions => NativeExtensionList;

    /// <summary>Extensions that can be played right now (FFmpeg formats only when the libraries load).</summary>
    public static IEnumerable<string> PlayableExtensions =>
        FFmpegLibrary.IsAvailable ? NativeExtensionList.Concat(FFmpegExtensionList) : NativeExtensionList;

    public static bool IsAudioFile(string path)
    {
        string extension = Path.GetExtension(path);
        return NativeExtensions.Contains(extension) || (FFmpegExtensions.Contains(extension) && FFmpegLibrary.IsAvailable);
    }

    public static IAudioDecoder Open(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Audio file not found.", path);
        }

        Container container = Sniff(path);
        Exception? nativeFailure = null;
        if (container != Container.Unknown)
        {
            try
            {
                return container switch
                {
                    Container.Wav => WavDecoder.Open(path),
                    Container.Aiff => AiffDecoder.Open(path),
                    Container.Flac => FlacDecoder.Open(path),
                    Container.Dsf => DsfDecoder.Open(path),
                    _ => DffDecoder.Open(path),
                };
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or EndOfStreamException)
            {
                nativeFailure = ex;
            }
        }

        if (FFmpegLibrary.IsAvailable)
        {
            try
            {
                return FFmpegDecoder.Open(path);
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidDataException)
            {
                throw new AudioDecoderException($"Cannot decode '{Path.GetFileName(path)}': {ex.Message}", ex);
            }
        }

        string reason = nativeFailure?.Message
            ?? $"'{Path.GetExtension(path)}' files need the FFmpeg libraries. {FFmpegLibrary.LoadError}";
        throw new AudioDecoderException($"Cannot decode '{Path.GetFileName(path)}': {reason}", nativeFailure);
    }

    internal static Container Sniff(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096);
        Span<byte> head = stackalloc byte[16];
        if (stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < 12)
        {
            return Container.Unknown;
        }

        string id = Encoding.ASCII.GetString(head[..4]);
        string form = Encoding.ASCII.GetString(head[8..12]);
        switch (id)
        {
            case "RIFF" or "RF64" or "BW64" when form == "WAVE":
                return Container.Wav;
            case "FORM" when form is "AIFF" or "AIFC":
                return Container.Aiff;
            case "fLaC":
                return Container.Flac;
            case "DSD ":
                return Container.Dsf;
            case "FRM8" when Encoding.ASCII.GetString(head[12..16]) == "DSD ":
                return Container.Dff;
        }

        if (head[0] == 'I' && head[1] == 'D' && head[2] == '3')
        {
            // ID3v2 in front of a FLAC stream.
            int size = (head[6] << 21) | (head[7] << 14) | (head[8] << 7) | head[9];
            stream.Position = 10 + size + ((head[5] & 0x10) != 0 ? 10 : 0);
            Span<byte> marker = stackalloc byte[4];
            if (stream.ReadAtLeast(marker, 4, throwOnEndOfStream: false) == 4 && Encoding.ASCII.GetString(marker) == "fLaC")
            {
                return Container.Flac;
            }
        }

        return Container.Unknown;
    }
}
