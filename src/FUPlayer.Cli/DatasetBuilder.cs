using System.Diagnostics;
using System.Runtime.Versioning;
using FUPlayer.Audio.Windows;
using FUPlayer.Core.Decoding;
using FUPlayer.Core.Dsp.Analysis;
using FUPlayer.Core.Dsp.Restoration;

namespace FUPlayer.Cli;

/// <summary>
/// Builds the training set: real music, coded with a real encoder, frame by frame against the
/// original it came from.
///
/// Every file is screened first. Material that is already band-limited teaches the model that high
/// bands are empty, which is the opposite of what it needs to learn, so anything the detector does
/// not call full band is thrown out before a single frame is written.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class DatasetBuilder
{
    /// <summary>Audio is coded a minute at a time, so a long album does not have to fit in memory.</summary>
    private const int ChunkSeconds = 60;

    /// <summary>Dropped from each end of a chunk, where the encoder is still settling.</summary>
    private const double GuardSeconds = 0.5;

    private readonly BandLayout _layout;
    private readonly int _context;
    private readonly int[] _rates;
    private readonly double _ceilingHz;
    private readonly int _stride;
    private readonly IReadOnlyList<(string Name, string Encoder, string Extension, int[] Kbps)> _external;

    public DatasetBuilder(BandLayout layout, int context, int[] rates, double ceilingHz, int stride)
    {
        _external = ExternalCodec.Available();
        _layout = layout;
        _context = context;
        _rates = rates;
        _ceilingHz = ceilingHz;
        _stride = Math.Max(1, stride);
    }

    public long Frames { get; private set; }

    public int FilesUsed { get; private set; }

    public int FilesSkipped { get; private set; }

    /// <summary>Files whose rate or channel count the encoder will not take.</summary>
    public int FilesUnsupported { get; private set; }

    /// <summary>Files that were already band-limited, which would teach the wrong lesson.</summary>
    public int FilesNotFullBand { get; private set; }

    public Dictionary<string, int> CutoffByRate { get; } = [];

    public void Add(string path, RepairDataset.Writer writer, double maxSeconds)
    {
        IAudioDecoder decoder;
        try
        {
            decoder = DecoderFactory.Open(path);
        }
        catch (Exception ex) when (ex is AudioDecoderException or IOException or NotSupportedException)
        {
            FilesSkipped++;
            return;
        }

        using (decoder)
        {
            int rate = decoder.Format.SampleRate;
            int channels = decoder.Format.Channels;
            if (decoder.Format.IsDsd || !AacRoundTrip.Supports(rate, channels))
            {
                // The system encoder takes 44.1 and 48 kHz, mono or stereo, and nothing else.
                FilesUnsupported++;
                FilesSkipped++;
                return;
            }

            int chunk = ChunkSeconds * rate;
            double[][] original = new double[channels][];
            for (int c = 0; c < channels; c++)
            {
                original[c] = new double[chunk];
            }

            var screen = new CodecBandwidthDetector(rate);
            bool screened = false;
            double done = 0.0;

            while (done < maxSeconds)
            {
                int frames = decoder.ReadPcm(original, 0, chunk);
                if (frames < rate * 4)
                {
                    break;
                }

                if (!screened)
                {
                    // The first chunk decides whether this file is worth anything.
                    double[] mid = new double[frames];
                    for (int i = 0; i < frames; i++)
                    {
                        double sum = 0.0;
                        for (int c = 0; c < channels; c++)
                        {
                            sum += original[c][i];
                        }

                        mid[i] = sum / channels;
                    }

                    screen.Push(mid);
                    if (screen.Estimate().Verdict != BandwidthVerdict.FullBand)
                    {
                        FilesNotFullBand++;
                        FilesSkipped++;
                        return;
                    }

                    screened = true;
                    FilesUsed++;

                    // Marks where this file's records begin, so the held-out set can be chosen by
                    // file rather than by whatever happens to sit at the end of the set.
                    writer.BeginGroup();
                }

                foreach (int bytesPerSecond in _rates)
                {
                    double[][]? coded = AacRoundTrip.Process(original, rate, channels, bytesPerSecond);
                    if (coded is null)
                    {
                        continue;
                    }

                    AddChunk(original, coded, frames, rate, channels, $"aac {bytesPerSecond * 8 / 1000}k", writer);
                }

                // Whatever else this machine can encode, which is nothing unless ffmpeg is installed.
                foreach ((string name, string encoder, string extension, int[] kbps) in _external)
                {
                    foreach (int bitrate in kbps)
                    {
                        double[][]? coded = ExternalCodec.Code(original, rate, channels, encoder, extension, bitrate);
                        if (coded is null)
                        {
                            continue;
                        }

                        AddChunk(original, coded, frames, rate, channels, $"{name} {bitrate}k", writer);
                    }
                }

                done += (double)frames / rate;
            }
        }
    }

    private void AddChunk(
        double[][] original, double[][] coded, int frames, int rate, int channels, string label, RepairDataset.Writer writer)
    {
        // Where this encoder put the wall for this material, measured rather than looked up.
        var detector = new CodecBandwidthDetector(rate);
        double[] mid = new double[frames];
        for (int i = 0; i < frames; i++)
        {
            double sum = 0.0;
            for (int c = 0; c < channels; c++)
            {
                sum += coded[c][i];
            }

            mid[i] = sum / channels;
        }

        detector.Push(mid);
        BandwidthEstimate estimate = detector.Estimate();
        if (estimate.Verdict != BandwidthVerdict.BandLimited)
        {
            // The encoder left the band alone at this rate, so there is nothing to learn from it.
            return;
        }

        CutoffByRate[label] = (int)Math.Round(estimate.CutoffHz / 1000.0);

        double nyquist = rate / 2.0;
        double cutoffFraction = estimate.CutoffHz / nyquist;
        double ceiling = Math.Min(_ceilingHz, nyquist * 0.98);

        var builder = new RepairFrameBuilder(_layout, _context, rate);
        int bands = _layout.Count;
        int guard = (int)(GuardSeconds * rate);
        int first = guard;
        int last = frames - RepairFrameBuilder.FftSize - guard;
        if (last <= first)
        {
            return;
        }

        int count = ((last - first) / RepairFrameBuilder.Hop) + 1;
        float[] levels = new float[count * bands];
        float[] gains = new float[count * bands];
        double[] frameLevels = new double[count];
        bool[] loud = new bool[count];

        for (int c = 0; c < channels; c++)
        {
            for (int index = 0; index < count; index++)
            {
                int start = first + (index * RepairFrameBuilder.Hop);
                frameLevels[index] = builder.Describe(
                    coded[c], original[c], start, estimate.CutoffHz, ceiling, index,
                    levels.AsSpan(index * bands, bands), gains.AsSpan(index * bands, bands));

                double energy = 0.0;
                for (int i = 0; i < RepairFrameBuilder.FftSize; i += 4)
                {
                    double sample = coded[c][start + i];
                    energy += sample * sample;
                }

                // A silent frame teaches only what the noise floor looks like.
                loud[index] = Math.Sqrt(energy / (RepairFrameBuilder.FftSize / 4)) > 1e-4;
            }

            float[] input = new float[builder.InputSize];
            float[] window = new float[bands * ((2 * _context) + 1)];

            // Frames overlap by three quarters, so neighbours say almost the same thing. Taking
            // every other one halves the file for almost no loss of information.
            for (int index = _context; index < count - _context; index += _stride)
            {
                if (!loud[index])
                {
                    continue;
                }

                for (int offset = -_context, at = 0; offset <= _context; offset++, at += bands)
                {
                    levels.AsSpan((index + offset) * bands, bands).CopyTo(window.AsSpan(at, bands));
                }

                RepairFrameBuilder.Compose(window, bands, _context, frameLevels[index], cutoffFraction, input);
                writer.Add(input, gains.AsSpan(index * bands, bands));
                Frames++;
            }
        }
    }

    public static int Run(
        IReadOnlyList<string> files, string output, int[] rates, double seconds, int bands, int context, int stride,
        double lowHz, double highHz)
    {
        var layout = new BandLayout(lowHz, highHz, bands);
        var builder = new DatasetBuilder(layout, context, rates, highHz, stride);

        IReadOnlyList<(string Name, string Encoder, string Extension, int[] Kbps)> external = ExternalCodec.Available();
        Console.WriteLine(external.Count == 0
            ? "Only the system AAC encoder is available. Install ffmpeg to add MP3, Opus and Vorbis."
            : $"Also coding with ffmpeg: {string.Join(", ", external.Select(c => c.Name))}.");
        var clock = Stopwatch.StartNew();

        using (var writer = new RepairDataset.Writer(output, layout, context, 44_100))
        {
            foreach (string file in files)
            {
                builder.Add(file, writer, seconds);
                Console.Write($"\r{builder.FilesUsed} files, {builder.Frames:N0} frames, {clock.Elapsed.TotalMinutes:0.0} min   ");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Wrote {output}");
        Console.WriteLine($"  {builder.Frames:N0} frames from {builder.FilesUsed} files, {builder.FilesSkipped} skipped "
            + $"({builder.FilesUnsupported} the encoder would not take, {builder.FilesNotFullBand} already band-limited)");
        Console.WriteLine($"  {bands} bands from {lowHz / 1000.0:0.###} to {highHz / 1000.0:0.###} kHz, "
            + $"{context} frames of context, {NeuralRepairModel.ExpectedInputs(bands, context)} inputs");

        // How many bands land above a typical codec cutoff, which is the only place the rebuild
        // writes. Thirty bands below 10 kHz and three above 15 kHz is a layout that spends its
        // resolution where nothing happens.
        int working = 0;
        for (int band = 0; band < bands; band++)
        {
            if (layout.CentreHz(band) >= 15_000.0)
            {
                working++;
            }
        }

        Console.WriteLine($"  {working} of them above 15 kHz, which is where the rebuilt band goes");

        foreach ((string label, int kHz) in builder.CutoffByRate.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"  {label,-12} coded to about {kHz} kHz");
        }

        return 0;
    }
}
