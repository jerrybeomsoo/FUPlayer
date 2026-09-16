using System.Runtime.InteropServices;
using FUPlayer.Audio.Windows.Interop;

namespace FUPlayer.Audio.Windows;

/// <summary>
/// Codes audio with the AAC encoder Windows ships and reads it straight back.
///
/// This is how the training pairs are made. A model that is supposed to repair coded audio has to be
/// shown coded audio, and the only way to be sure what a codec did to a recording is to hand it the
/// recording and compare. Nothing is downloaded and nothing is installed: the encoder and the decoder
/// are both part of Windows.
///
/// The decoder returns more samples than went in, because the encoder pads the front. The padding is
/// found by cross-correlation rather than assumed, since it differs with rate and channel count, and
/// a pair that is out of step by even a few samples teaches the model nothing but nonsense.
/// </summary>
public static class AacRoundTrip
{
    /// <summary>
    /// Bit rates the Windows encoder accepts, in bytes per second: 128, 192 and 256 kbit/s.
    ///
    /// 256 is here because it is what a streaming service delivers, and because this encoder does
    /// something at that rate that a better one does not. Measured against the masters over six
    /// tracks it keeps the spectrum running to the end of the band on average, and still empties the
    /// band above 17 kHz in a quarter to three quarters of individual frames. The long-term average
    /// hides that; the frames are where a model looks.
    /// </summary>
    public static readonly int[] BytesPerSecond = [16_000, 24_000, 32_000];

    private static readonly Lock Gate = new();
    private static bool _started;

    /// <summary>Rates and channel counts the Windows AAC encoder accepts.</summary>
    public static bool Supports(int sampleRate, int channels) =>
        (sampleRate is 44_100 or 48_000) && channels is 1 or 2;

    /// <summary>
    /// Codes and decodes one signal. Returns planar samples the same length as the input, lined up
    /// with it, or null when the encoder refused the format.
    /// </summary>
    public static double[][]? Process(double[][] planar, int sampleRate, int channels, int bytesPerSecond)
    {
        ArgumentNullException.ThrowIfNull(planar);
        if (!Supports(sampleRate, channels))
        {
            return null;
        }

        Startup();

        int frames = planar[0].Length;
        string path = Path.Combine(Path.GetTempPath(), $"fuplayer-aac-{Guid.NewGuid():N}.m4a");

        try
        {
            Encode(path, planar, frames, sampleRate, channels, bytesPerSecond);
            float[] decoded = Decode(path, out int decodedChannels);
            if (decodedChannels != channels || decoded.Length == 0)
            {
                return null;
            }

            int offset = FindOffset(planar, decoded, channels, frames);
            return Deinterleave(decoded, offset, frames, channels);
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // A file the system is still closing is not worth failing a training run over.
            }
        }
    }

    private static void Startup()
    {
        lock (Gate)
        {
            if (_started)
            {
                return;
            }

            Marshal.ThrowExceptionForHR(MediaFoundationNative.MFStartup(MediaFoundationConstants.Version, MediaFoundationConstants.StartupLite));
            _started = true;
        }
    }

    private static void Encode(string path, double[][] planar, int frames, int sampleRate, int channels, int bytesPerSecond)
    {
        Marshal.ThrowExceptionForHR(MediaFoundationNative.MFCreateMediaType(out IMFMediaType output));
        Guid key = MediaFoundationConstants.MajorType;
        Guid value = MediaFoundationConstants.MajorTypeAudio;
        Marshal.ThrowExceptionForHR(output.SetGUID(ref key, ref value));
        key = MediaFoundationConstants.Subtype;
        value = MediaFoundationConstants.AudioFormatAac;
        Marshal.ThrowExceptionForHR(output.SetGUID(ref key, ref value));
        Set(output, MediaFoundationConstants.BitsPerSample, 16);
        Set(output, MediaFoundationConstants.SamplesPerSecond, sampleRate);
        Set(output, MediaFoundationConstants.Channels, channels);
        Set(output, MediaFoundationConstants.AverageBytesPerSecond, bytesPerSecond);
        Set(output, MediaFoundationConstants.AacPayloadType, 0);

        Marshal.ThrowExceptionForHR(MediaFoundationNative.MFCreateMediaType(out IMFMediaType input));
        key = MediaFoundationConstants.MajorType;
        value = MediaFoundationConstants.MajorTypeAudio;
        Marshal.ThrowExceptionForHR(input.SetGUID(ref key, ref value));
        key = MediaFoundationConstants.Subtype;
        value = MediaFoundationConstants.AudioFormatPcm;
        Marshal.ThrowExceptionForHR(input.SetGUID(ref key, ref value));
        Set(input, MediaFoundationConstants.BitsPerSample, 16);
        Set(input, MediaFoundationConstants.SamplesPerSecond, sampleRate);
        Set(input, MediaFoundationConstants.Channels, channels);
        Set(input, MediaFoundationConstants.BlockAlignment, channels * 2);
        Set(input, MediaFoundationConstants.AverageBytesPerSecond, sampleRate * channels * 2);

        Marshal.ThrowExceptionForHR(MediaFoundationNative.MFCreateSinkWriterFromURL(path, IntPtr.Zero, null, out IMFSinkWriter writer));
        try
        {
            Marshal.ThrowExceptionForHR(writer.AddStream(output, out int stream));
            Marshal.ThrowExceptionForHR(writer.SetInputMediaType(stream, input, null));
            Marshal.ThrowExceptionForHR(writer.BeginWriting());

            const int chunk = 4096;
            byte[] bytes = new byte[chunk * channels * 2];
            long time = 0;

            for (int start = 0; start < frames; start += chunk)
            {
                int count = Math.Min(chunk, frames - start);
                int at = 0;
                for (int i = 0; i < count; i++)
                {
                    for (int c = 0; c < channels; c++)
                    {
                        short sample = (short)Math.Clamp(Math.Round(planar[c][start + i] * 32767.0), -32768.0, 32767.0);
                        bytes[at++] = (byte)(sample & 0xFF);
                        bytes[at++] = (byte)((sample >> 8) & 0xFF);
                    }
                }

                WriteChunk(writer, stream, bytes, at, ref time, count, sampleRate);
            }

            Marshal.ThrowExceptionForHR(writer.Finalize_());
        }
        finally
        {
            Marshal.ReleaseComObject(writer);
            Marshal.ReleaseComObject(input);
            Marshal.ReleaseComObject(output);
        }
    }

    private static void WriteChunk(IMFSinkWriter writer, int stream, byte[] bytes, int length, ref long time, int frames, int sampleRate)
    {
        Marshal.ThrowExceptionForHR(MediaFoundationNative.MFCreateMemoryBuffer(length, out IMFMediaBuffer buffer));
        try
        {
            Marshal.ThrowExceptionForHR(buffer.Lock(out IntPtr target, out _, out _));
            Marshal.Copy(bytes, 0, target, length);
            Marshal.ThrowExceptionForHR(buffer.Unlock());
            Marshal.ThrowExceptionForHR(buffer.SetCurrentLength(length));

            Marshal.ThrowExceptionForHR(MediaFoundationNative.MFCreateSample(out IMFSample sample));
            try
            {
                Marshal.ThrowExceptionForHR(sample.AddBuffer(buffer));
                Marshal.ThrowExceptionForHR(sample.SetSampleTime(time));

                long duration = frames * 10_000_000L / sampleRate;
                Marshal.ThrowExceptionForHR(sample.SetSampleDuration(duration));
                time += duration;

                Marshal.ThrowExceptionForHR(writer.WriteSample(stream, sample));
            }
            finally
            {
                Marshal.ReleaseComObject(sample);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(buffer);
        }
    }

    private static float[] Decode(string path, out int channels)
    {
        channels = 0;
        Marshal.ThrowExceptionForHR(MediaFoundationNative.MFCreateSourceReaderFromURL(path, null, out IMFSourceReader reader));
        try
        {
            Marshal.ThrowExceptionForHR(MediaFoundationNative.MFCreateMediaType(out IMFMediaType wanted));
            try
            {
                Guid key = MediaFoundationConstants.MajorType;
                Guid value = MediaFoundationConstants.MajorTypeAudio;
                Marshal.ThrowExceptionForHR(wanted.SetGUID(ref key, ref value));
                key = MediaFoundationConstants.Subtype;
                value = MediaFoundationConstants.AudioFormatFloat;
                Marshal.ThrowExceptionForHR(wanted.SetGUID(ref key, ref value));

                Marshal.ThrowExceptionForHR(reader.SetCurrentMediaType(
                    MediaFoundationConstants.SourceReaderFirstAudioStream, IntPtr.Zero, wanted));
            }
            finally
            {
                Marshal.ReleaseComObject(wanted);
            }

            Marshal.ThrowExceptionForHR(reader.GetCurrentMediaType(
                MediaFoundationConstants.SourceReaderFirstAudioStream, out IMFMediaType actual));
            try
            {
                Guid key = MediaFoundationConstants.Channels;
                Marshal.ThrowExceptionForHR(actual.GetUINT32(ref key, out channels));
            }
            finally
            {
                Marshal.ReleaseComObject(actual);
            }

            var samples = new List<float>(1 << 20);
            byte[] scratch = new byte[1 << 16];

            while (true)
            {
                int hr = reader.ReadSample(
                    MediaFoundationConstants.SourceReaderFirstAudioStream, 0, out _, out int flags, out _, out IMFSample? sample);
                Marshal.ThrowExceptionForHR(hr);

                if (sample is null)
                {
                    if ((flags & MediaFoundationConstants.BufferFlagEndOfStream) != 0)
                    {
                        break;
                    }

                    continue;
                }

                try
                {
                    Marshal.ThrowExceptionForHR(sample.ConvertToContiguousBuffer(out IMFMediaBuffer buffer));
                    try
                    {
                        Marshal.ThrowExceptionForHR(buffer.Lock(out IntPtr data, out _, out int length));
                        if (length > scratch.Length)
                        {
                            scratch = new byte[length];
                        }

                        Marshal.Copy(data, scratch, 0, length);
                        Marshal.ThrowExceptionForHR(buffer.Unlock());

                        ReadOnlySpan<float> block = MemoryMarshal.Cast<byte, float>(scratch.AsSpan(0, length));
                        samples.AddRange(block);
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(buffer);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(sample);
                }
            }

            return [.. samples];
        }
        finally
        {
            Marshal.ReleaseComObject(reader);
        }
    }

    /// <summary>
    /// Finds the encoder's padding by correlating the first loud second of the two signals. The
    /// answer is usually a couple of thousand samples, but it is measured rather than assumed.
    /// </summary>
    private static int FindOffset(double[][] planar, float[] decoded, int channels, int frames)
    {
        const int search = 5_000;
        int window = Math.Min(48_000, frames);
        int available = (decoded.Length / channels) - window;
        if (available <= 0)
        {
            return 0;
        }

        int limit = Math.Min(search, available);
        double best = double.NegativeInfinity;
        int bestOffset = 0;

        for (int offset = 0; offset <= limit; offset++)
        {
            double sum = 0.0;
            for (int i = 0; i < window; i += 8)
            {
                sum += planar[0][i] * decoded[((offset + i) * channels)];
            }

            if (sum > best)
            {
                best = sum;
                bestOffset = offset;
            }
        }

        return bestOffset;
    }

    private static double[][] Deinterleave(float[] decoded, int offset, int frames, int channels)
    {
        double[][] planar = new double[channels][];
        for (int c = 0; c < channels; c++)
        {
            planar[c] = new double[frames];
        }

        int total = decoded.Length / channels;
        for (int i = 0; i < frames; i++)
        {
            int at = offset + i;
            if (at >= total)
            {
                break;
            }

            for (int c = 0; c < channels; c++)
            {
                planar[c][i] = decoded[(at * channels) + c];
            }
        }

        return planar;
    }

    private static void Set(IMFMediaType type, Guid key, int value)
    {
        Guid local = key;
        Marshal.ThrowExceptionForHR(type.SetUINT32(ref local, value));
    }
}
