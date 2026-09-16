using System.Diagnostics;
using System.Runtime.Versioning;
using FUPlayer.Audio.Windows;
using FUPlayer.Core.Decoding;
using FUPlayer.Core.Dsp.Analysis;
using FUPlayer.Core.Dsp.Numerics;
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

    public DatasetBuilder(BandLayout layout, int context, int[] rates, double ceilingHz, int stride, int narrowTo = 0)
    {
        _external = ExternalCodec.Available();
        _layout = layout;
        _context = context;
        _rates = rates;
        _ceilingHz = ceilingHz;
        _stride = Math.Max(1, stride);
        _narrowTo = narrowTo;
    }

    /// <summary>
    /// Rate to narrow the music to and back, instead of coding it. Zero builds codec pairs.
    ///
    /// This is the high-resolution set: the pair is a real 96 kHz recording and the same recording
    /// with everything above 22.05 kHz removed, which is what that music would have been had it been
    /// released at CD rate. The answer above the old Nyquist rate is then a recording rather than a
    /// guess, which is the only reason to attempt the band at all.
    /// </summary>
    private readonly int _narrowTo;

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
            bool usable = _narrowTo > 0
                ? !decoder.Format.IsDsd && channels is 1 or 2 && rate > _narrowTo
                : !decoder.Format.IsDsd && AacRoundTrip.Supports(rate, channels);

            if (!usable)
            {
                // The system encoder takes 44.1 and 48 kHz, mono or stereo, and nothing else. The
                // high-resolution set instead wants anything faster than the rate it narrows to.
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

                    if (_narrowTo > 0)
                    {
                        if (!IsRealHighResolution(mid, frames, rate))
                        {
                            FilesNotFullBand++;
                            FilesSkipped++;
                            return;
                        }
                    }
                    else
                    {
                        screen.Push(mid);
                        if (screen.Estimate().Verdict != BandwidthVerdict.FullBand)
                        {
                            FilesNotFullBand++;
                            FilesSkipped++;
                            return;
                        }
                    }

                    screened = true;
                    FilesUsed++;

                    // Marks where this file's records begin, so the held-out set can be chosen by
                    // file rather than by whatever happens to sit at the end of the set.
                    writer.BeginGroup();
                }

                if (_narrowTo > 0)
                {
                    double[][]? narrowed = ExternalCodec.Resample(original, rate, channels, _narrowTo);
                    if (narrowed is not null)
                    {
                        AddChunk(original, narrowed, frames, rate, channels, $"narrowed to {_narrowTo / 1000.0:0.#} kHz", writer);
                    }

                    done += (double)frames / rate;
                    continue;
                }

                foreach (int bytesPerSecond in _rates)
                {
                    double[][]? coded = AacRoundTrip.Process(original, rate, channels, bytesPerSecond);
                    if (coded is null)
                    {
                        continue;
                    }

                    // Named for the encoder, not just the codec: ffmpeg has an AAC encoder too and
                    // the two disagree about the same bit rate, which is the point of using both.
                    AddChunk(original, coded, frames, rate, channels, $"aac-windows {bytesPerSecond * 8 / 1000}k", writer);
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

                        AddChunk(original, coded, frames, rate, channels, $"{name}-ffmpeg {bitrate}k", writer);
                    }
                }

                done += (double)frames / rate;
            }
        }
    }

    /// <summary>
    /// Whether a file with a high sample rate actually carries music up there, rather than noise or
    /// nothing at all.
    ///
    /// Two kinds of file have to be thrown out and they fail in opposite directions. A transfer from
    /// a 1-bit source carries the modulator's noise, which rises with frequency: measured on this
    /// library, one track in three has a spectrum that is flat from 8 kHz to 40 kHz and one of them
    /// sits 1.4 dB under its own midband at 30 kHz, which is not music by any reading. Fitting a
    /// model to those teaches it to add hiss to everything. At the other end, a 44.1 kHz master sold
    /// at 96 kHz has nothing above 22 kHz but the resampler's stopband, and teaches nothing at all.
    ///
    /// So the test is the slope: the band from 24 to 32 kHz has to sit well below the band from 8 to
    /// 16 kHz, and not so far below that there is nothing there. Of twenty tracks measured by hand,
    /// this keeps the nine whose spectra fall away and rejects the eleven that do not.
    /// </summary>
    private static bool IsRealHighResolution(double[] mid, int frames, int rate)
    {
        const int Size = 4096;
        const double QuietestSlopeDb = -70.0;
        const double SteepestSlopeDb = -13.0;

        if (rate < 64_000)
        {
            return false;
        }

        var plan = new RealFftPlan(Size);
        double[] window = new double[Size];
        for (int i = 0; i < Size; i++)
        {
            window[i] = 0.5 - (0.5 * Math.Cos(2.0 * Math.PI * i / Size));
        }

        double[] frame = new double[Size];
        double[] re = new double[Size];
        double[] im = new double[Size];
        double[] sum = new double[plan.Bins];
        double binHz = (double)rate / Size;
        int counted = 0;

        for (int start = 0; start + Size <= frames; start += Size)
        {
            double energy = 0.0;
            for (int i = 0; i < Size; i += 4)
            {
                energy += mid[start + i] * mid[start + i];
            }

            if (Math.Sqrt(energy / (Size / 4)) < 1e-4)
            {
                continue;
            }

            for (int i = 0; i < Size; i++)
            {
                frame[i] = mid[start + i] * window[i];
            }

            plan.Forward(frame, re, im);
            for (int bin = 0; bin < plan.Bins; bin++)
            {
                sum[bin] += (re[bin] * re[bin]) + (im[bin] * im[bin]);
            }

            counted++;
        }

        if (counted < 4)
        {
            return false;
        }

        double Mean(double lowHz, double highHz)
        {
            int low = Math.Clamp((int)(lowHz / binHz), 0, plan.Bins - 1);
            int high = Math.Clamp((int)(highHz / binHz), low + 1, plan.Bins);
            double total = 0.0;
            for (int bin = low; bin < high; bin++)
            {
                total += sum[bin];
            }

            return (total / (high - low)) + 1e-30;
        }

        double slope = 10.0 * Math.Log10(Mean(24_000.0, 32_000.0) / Mean(8_000.0, 16_000.0));
        return slope is > QuietestSlopeDb and < SteepestSlopeDb;
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
        if (estimate.Verdict == BandwidthVerdict.Unknown)
        {
            // Not enough loud audio to say anything about this chunk.
            return;
        }

        double nyquist = rate / 2.0;

        // A rate that band-limits nothing used to be dropped here, which threw away every frame of
        // AAC above 192 kbit/s: the very material most people stream. What such a frame teaches is
        // a small correction rather than a large one, and a model that has never seen one has no
        // answer at all for the case it meets most often.
        double cutoffHz = estimate.Verdict == BandwidthVerdict.BandLimited ? estimate.CutoffHz : nyquist;
        CutoffByRate[label] = (int)Math.Round(cutoffHz / 1000.0);

        // Measured against the top of the band layout rather than against the Nyquist rate, because
        // the layout travels inside the model and the Nyquist rate does not. A stream captured at
        // 88.2 kHz carries the same 44.1 kHz music; dividing by its Nyquist rate would describe an
        // untouched spectrum as one cut in half, and the model would answer a question about a codec
        // that was not there.
        double cutoffFraction = cutoffHz / _layout.HighHz;
        double ceiling = Math.Min(_ceilingHz, nyquist * 0.98);

        var builder = new RepairFrameBuilder(_layout, _context, rate);
        int bands = _layout.Count;
        int guard = (int)(GuardSeconds * rate);
        int first = guard;
        int last = frames - builder.Size - guard;
        if (last <= first)
        {
            return;
        }

        int count = ((last - first) / builder.HopSize) + 1;
        float[] levels = new float[count * bands];
        float[] gains = new float[count * bands];
        double[] frameLevels = new double[count];
        bool[] loud = new bool[count];

        for (int c = 0; c < channels; c++)
        {
            for (int index = 0; index < count; index++)
            {
                int start = first + (index * builder.HopSize);
                frameLevels[index] = builder.Describe(
                    coded[c], original[c], start, cutoffHz, ceiling, index,
                    levels.AsSpan(index * bands, bands), gains.AsSpan(index * bands, bands));

                double energy = 0.0;
                for (int i = 0; i < builder.Size; i += 4)
                {
                    double sample = coded[c][start + i];
                    energy += sample * sample;
                }

                // A silent frame teaches only what the noise floor looks like.
                loud[index] = Math.Sqrt(energy / (builder.Size / 4)) > 1e-4;
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
        double lowHz, double highHz, int narrowTo = 0)
    {
        var layout = new BandLayout(lowHz, highHz, bands);
        var builder = new DatasetBuilder(layout, context, rates, highHz, stride, narrowTo);

        if (narrowTo > 0 && ExternalCodec.Find() is null)
        {
            throw new InvalidOperationException("Narrowing needs ffmpeg on the path.");
        }

        IReadOnlyList<(string Name, string Encoder, string Extension, int[] Kbps)> external = ExternalCodec.Available();
        Console.WriteLine(narrowTo > 0
            ? $"Narrowing each file to {narrowTo / 1000.0:0.#} kHz and back; the original is the answer."
            : external.Count == 0
                ? "Only the system AAC encoder is available. Install ffmpeg to add MP3 and a second AAC encoder."
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
            + (narrowTo > 0
                ? $"({builder.FilesUnsupported} not fast enough to narrow, {builder.FilesNotFullBand} carrying noise or nothing above 24 kHz)"
                : $"({builder.FilesUnsupported} the encoder would not take, {builder.FilesNotFullBand} already band-limited)"));
        Console.WriteLine($"  {bands} bands from {lowHz / 1000.0:0.###} to {highHz / 1000.0:0.###} kHz, "
            + $"{context} frames of context, {NeuralRepairModel.ExpectedInputs(bands, context)} inputs");

        // How many bands land above a typical codec cutoff, which is the only place the rebuild
        // writes. Thirty bands below 10 kHz and three above 15 kHz is a layout that spends its
        // resolution where nothing happens.
        double fillFrom = narrowTo > 0 ? narrowTo / 2.0 : 15_000.0;
        int working = 0;
        for (int band = 0; band < bands; band++)
        {
            if (layout.CentreHz(band) >= fillFrom)
            {
                working++;
            }
        }

        Console.WriteLine($"  {working} of them above {fillFrom / 1000.0:0.#} kHz, which is where the rebuilt band goes");

        foreach ((string label, int kHz) in builder.CutoffByRate.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"  {label,-12} coded to about {kHz} kHz");
        }

        return 0;
    }
}
