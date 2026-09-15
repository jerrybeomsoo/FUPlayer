using System.Diagnostics;

namespace FUPlayer.Cli;

/// <summary>
/// Codes audio with ffmpeg, when ffmpeg happens to be on the path.
///
/// Windows ships an AAC encoder and that covers 96 to 192 kbit/s, which is most of what people
/// actually listen to. It does not cover MP3, Opus or Vorbis, and it stops below the 256 kbit/s AAC
/// that some services use. Where ffmpeg is already installed those gaps close, and where it is not
/// the training set is simply built from AAC alone.
///
/// Nothing is downloaded to make this work. If ffmpeg is absent the feature reports itself absent.
/// </summary>
internal static class ExternalCodec
{
    private static bool _looked;
    private static string? _path;

    /// <summary>
    /// Codecs worth training on, with the bit rates each is usually met at.
    ///
    /// One rate per distinct edge, not every rate on offer. Measured on this corpus, MP3 at 192, 256
    /// and 320 all leave the wall within a kilohertz of 19 to 20 kHz, and Opus at 96, 128 and 160 all
    /// leave it at 20; coding the same music three times to learn the same edge costs three times the
    /// hours and teaches nothing the first pass did not. What matters is that the edges span the range
    /// and that they come from different encoders, since each one rolls off in its own shape.
    ///
    /// AAC at 256 and above is left out for a different reason: it does not band-limit at all. Every
    /// band of it measures within 0.6 dB of the master, so the frames it would contribute carry a
    /// target of zero, and the player never invokes a model on material that reads as full band.
    /// </summary>
    public static readonly (string Name, string Encoder, string Extension, int[] Kbps)[] Codecs =
    [
        ("mp3", "libmp3lame", ".mp3", [128, 192]),
        ("opus", "libopus", ".opus", [128]),
        ("vorbis", "libvorbis", ".ogg", [128, 192]),
    ];

    /// <summary>The ffmpeg executable, or null when there is not one.</summary>
    public static string? Find()
    {
        if (_looked)
        {
            return _path;
        }

        _looked = true;
        try
        {
            using var probe = Process.Start(new ProcessStartInfo("ffmpeg", "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (probe is null)
            {
                return null;
            }

            probe.WaitForExit(10_000);
            _path = probe.ExitCode == 0 ? "ffmpeg" : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _path = null;
        }

        return _path;
    }

    /// <summary>Which of the codecs this ffmpeg build can actually encode.</summary>
    public static IReadOnlyList<(string Name, string Encoder, string Extension, int[] Kbps)> Available()
    {
        if (Find() is null)
        {
            return [];
        }

        string encoders = Run("-hide_banner -encoders") ?? string.Empty;
        return [.. Codecs.Where(codec => encoders.Contains(codec.Encoder, StringComparison.Ordinal))];
    }

    /// <summary>
    /// Codes and decodes one signal, returning planar samples lined up with the input, or null when
    /// it could not be done. The offset the encoder adds is found the same way as for AAC.
    ///
    /// Named Code rather than Process because a method called Process in this class would shadow
    /// System.Diagnostics.Process, which is the very thing it needs to start.
    /// </summary>
    public static double[][]? Code(
        double[][] planar, int sampleRate, int channels, string encoder, string extension, int kbps)
    {
        if (Find() is null)
        {
            return null;
        }

        int frames = planar[0].Length;
        string stem = Path.Combine(Path.GetTempPath(), $"fuplayer-{Guid.NewGuid():N}");
        string source = stem + "-in.wav";
        string coded = stem + extension;
        string back = stem + "-out.wav";

        try
        {
            WriteWav(source, planar, frames, sampleRate, channels);

            if (Run($"-hide_banner -loglevel error -y -i \"{source}\" -c:a {encoder} -b:a {kbps}k \"{coded}\"") is null
                || !File.Exists(coded))
            {
                return null;
            }

            if (Run($"-hide_banner -loglevel error -y -i \"{coded}\" -c:a pcm_f32le -ar {sampleRate} \"{back}\"") is null
                || !File.Exists(back))
            {
                return null;
            }

            float[] decoded = ReadWav(back, out int decodedChannels);
            return decodedChannels != channels || decoded.Length == 0
                ? null
                : Align(planar, decoded, channels, frames);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            foreach (string file in new[] { source, coded, back })
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // A file the system is still closing is not worth failing a training run over.
                }
            }
        }
    }

    private static string? Run(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("ffmpeg", arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                return null;
            }

            string output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            return process.WaitForExit(300_000) && process.ExitCode == 0 ? output : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private static void WriteWav(string path, double[][] planar, int frames, int rate, int channels)
    {
        using var writer = new BinaryWriter(File.Create(path));
        int blockAlign = channels * sizeof(float);
        long data = (long)frames * blockAlign;

        writer.Write("RIFF"u8);
        writer.Write((uint)(36 + data));
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16u);
        writer.Write((ushort)3);
        writer.Write((ushort)channels);
        writer.Write((uint)rate);
        writer.Write((uint)(rate * blockAlign));
        writer.Write((ushort)blockAlign);
        writer.Write((ushort)32);
        writer.Write("data"u8);
        writer.Write((uint)data);

        for (int i = 0; i < frames; i++)
        {
            for (int c = 0; c < channels; c++)
            {
                writer.Write((float)planar[c][i]);
            }
        }
    }

    /// <summary>Reads the float WAV ffmpeg wrote back, skipping to the data chunk.</summary>
    private static float[] ReadWav(string path, out int channels)
    {
        channels = 0;
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length < 44)
        {
            return [];
        }

        channels = BitConverter.ToUInt16(bytes, 22);
        int at = 12;

        while (at + 8 <= bytes.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(bytes, at, 4);
            int size = BitConverter.ToInt32(bytes, at + 4);
            if (id == "data")
            {
                int count = Math.Min(size, bytes.Length - at - 8) / sizeof(float);
                float[] samples = new float[count];
                Buffer.BlockCopy(bytes, at + 8, samples, 0, count * sizeof(float));
                return samples;
            }

            at += 8 + size + (size & 1);
        }

        return [];
    }

    private static double[][] Align(double[][] planar, float[] decoded, int channels, int frames)
    {
        const int search = 6_000;
        int window = Math.Min(48_000, frames);
        int total = decoded.Length / channels;
        int limit = Math.Min(search, Math.Max(0, total - window));

        double best = double.NegativeInfinity;
        int offset = 0;
        for (int candidate = 0; candidate <= limit; candidate++)
        {
            double sum = 0.0;
            for (int i = 0; i < window; i += 8)
            {
                sum += planar[0][i] * decoded[(candidate + i) * channels];
            }

            if (sum > best)
            {
                best = sum;
                offset = candidate;
            }
        }

        double[][] result = new double[channels][];
        for (int c = 0; c < channels; c++)
        {
            result[c] = new double[frames];
        }

        for (int i = 0; i < frames && offset + i < total; i++)
        {
            for (int c = 0; c < channels; c++)
            {
                result[c][i] = decoded[((offset + i) * channels) + c];
            }
        }

        return result;
    }
}
