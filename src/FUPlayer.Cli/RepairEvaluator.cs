using System.Runtime.Versioning;
using FUPlayer.Audio.Windows;
using FUPlayer.Core.Decoding;
using FUPlayer.Core.Dsp.Analysis;
using FUPlayer.Core.Dsp.Numerics;
using FUPlayer.Core.Dsp.Restoration;

namespace FUPlayer.Cli;

/// <summary>
/// Measures whether repairing coded audio actually moves it closer to the original.
///
/// The distance used is the mean absolute difference of the two spectra, band by band, in decibels.
/// It is not a listening test and does not claim to be, but it answers the only question that can be
/// answered objectively: is the repaired file nearer the master than the coded file was, and by how
/// much. A stage that scores worse is doing harm, however good it sounds in a demonstration.
///
/// Run it on files the model has never been trained on, or the number means nothing.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class RepairEvaluator
{
    private const int FftSize = 2048;
    private const int Hop = FftSize / 2;

    public static int Run(IReadOnlyList<string> files, int[] rates, double seconds, string? networkPath, double amount)
    {
        NeuralRepairModel? network = ModelLibrary.TryLoadNetwork(networkPath, out string? failure);
        if (network is null)
        {
            Console.Error.WriteLine($"error: {failure}");
            return 1;
        }

        Console.WriteLine($"Network {string.Join(" -> ", network.Layers)}, fitted to {network.FramesSeen:N0} frames"
            + (network.TrainedOn is null ? string.Empty : $" from {network.TrainedOn}") + ".");
        Console.WriteLine();
        // Split at the cutoff, because filling an empty band and repairing a damaged one are not
        // the same achievement and one should not be allowed to flatter the other.
        Console.WriteLine($"{"FILE",-22} {"CODEC",-16} {"ABOVE THE CUTOFF",22}  {"BELOW THE CUTOFF",22}");
        Console.WriteLine($"{string.Empty,-22} {string.Empty,-16} {"coded",6} {"patched",7} {"network",8}  {"coded",6} {"patched",7} {"network",8}");

        double codedAbove = 0.0;
        double patchedAbove = 0.0;
        double repairedAbove = 0.0;
        double codedBelow = 0.0;
        double patchedBelow = 0.0;
        double repairedBelow = 0.0;
        int measured = 0;

        foreach (string file in files)
        {
            IAudioDecoder decoder;
            try
            {
                decoder = DecoderFactory.Open(file);
            }
            catch (Exception ex) when (ex is AudioDecoderException or IOException or NotSupportedException)
            {
                continue;
            }

            using (decoder)
            {
                int rate = decoder.Format.SampleRate;
                int channels = decoder.Format.Channels;
                if (decoder.Format.IsDsd || !AacRoundTrip.Supports(rate, channels))
                {
                    continue;
                }

                int frames = (int)(seconds * rate);
                double[][] original = new double[channels][];
                for (int c = 0; c < channels; c++)
                {
                    original[c] = new double[frames];
                }

                int read = decoder.ReadPcm(original, 0, frames);
                if (read < rate * 4)
                {
                    continue;
                }

                // The same encoders the training set is built from, so a number here answers a
                // question about the material the model was fitted to rather than about one codec.
                var passes = new List<(string Label, Func<double[][]?> Code)>();
                foreach (int bytesPerSecond in rates)
                {
                    int copy = bytesPerSecond;
                    passes.Add(($"aac-win {copy * 8 / 1000}k", () => AacRoundTrip.Process(original, rate, channels, copy)));
                }

                foreach ((string name, string encoder, string extension, int[] kbps) in ExternalCodec.Available())
                {
                    foreach (int bitrate in kbps)
                    {
                        int copy = bitrate;
                        passes.Add(($"{name} {copy}k",
                            () => ExternalCodec.Code(original, rate, channels, encoder, extension, copy)));
                    }
                }

                foreach ((string label, Func<double[][]?> code) in passes)
                {
                    double[][]? coded = code();
                    if (coded is null)
                    {
                        continue;
                    }

                    double cutoff = Cutoff(coded, rate, channels, read);
                    if (cutoff <= 0.0)
                    {
                        continue;
                    }

                    double[][] repaired = Repair(coded, network, rate, channels, read, cutoff, amount);

                    // Patching alone, with the network's answer turned off. Most of the gain above
                    // the cutoff comes from putting something plausible there at all, and without
                    // this column there is no way to see how much the network itself is worth.
                    double[][] patched = Repair(coded, network, rate, channels, read, cutoff, 0.0);

                    // The repair delays the signal, so the comparison is made on the same samples.
                    int latency = FftSize + (network.Context * (FftSize / 4));
                    (double ca, double cb) = Distance(original[0], coded[0], 0, read, rate, cutoff);
                    (double pa, double pb) = Distance(original[0], patched[0], latency, read, rate, cutoff);
                    (double ra, double rb) = Distance(original[0], repaired[0], latency, read, rate, cutoff);

                    codedAbove += ca;
                    patchedAbove += pa;
                    repairedAbove += ra;
                    codedBelow += cb;
                    patchedBelow += pb;
                    repairedBelow += rb;
                    measured++;

                    string name = Path.GetFileNameWithoutExtension(file);
                    Console.WriteLine($"{name[..Math.Min(22, name.Length)],-22} {label,-16} "
                        + $"{ca,6:F2} {pa,7:F2} {ra,8:F2}  {cb,6:F2} {pb,7:F2} {rb,8:F2}");
                }
            }
        }

        if (measured == 0)
        {
            Console.Error.WriteLine("error: nothing could be measured.");
            return 1;
        }

        double above = codedAbove / measured;
        double abovePatched = patchedAbove / measured;
        double aboveAfter = repairedAbove / measured;
        double below = codedBelow / measured;
        double belowPatched = patchedBelow / measured;
        double belowAfter = repairedBelow / measured;

        Console.WriteLine();
        Console.WriteLine($"Over {measured} measurements, mean distance from the original, in decibels per band:");
        Console.WriteLine($"  above the cutoff: {above,6:F2} coded -> {abovePatched,6:F2} patched -> {aboveAfter,6:F2} with the network");
        Console.WriteLine($"                    patching alone: {Change(above, abovePatched)}; the network then {Change(abovePatched, aboveAfter)}");
        Console.WriteLine($"  below the cutoff: {below,6:F2} coded -> {belowPatched,6:F2} patched -> {belowAfter,6:F2} with the network");
        Console.WriteLine($"                    the network: {Change(belowPatched, belowAfter)}");
        Console.WriteLine();
        Console.WriteLine("The patched column is the fixed algorithm with the network's answer turned off.");
        Console.WriteLine("Above the cutoff most of the gain is having put something plausible there at all,");
        Console.WriteLine("which patching does on its own. What the network is worth is the step after it.");

        if (belowAfter > belowPatched)
        {
            Console.WriteLine();
            Console.WriteLine("This network makes the kept band worse than leaving it alone. Do not ship it.");
        }

        return 0;
    }

    private static string Change(double before, double after) =>
        after < before
            ? $"{100.0 * (before - after) / Math.Max(1e-9, before):F0} percent closer"
            : $"{100.0 * (after - before) / Math.Max(1e-9, before):F0} percent further away";

    private static double Cutoff(double[][] coded, int rate, int channels, int frames)
    {
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

        // Nyquist, not zero, when nothing was cut: zero switches the repair off, and the point of
        // measuring a full-band copy is to see what the gains alone are worth on it.
        return estimate.Verdict switch
        {
            BandwidthVerdict.BandLimited => estimate.CutoffHz,
            BandwidthVerdict.FullBand => rate / 2.0,
            _ => 0.0,
        };
    }

    private static double[][] Repair(
        double[][] coded, NeuralRepairModel network, int rate, int channels, int frames, double cutoff, double amount)
    {
        double[][] repaired = new double[channels][];
        for (int c = 0; c < channels; c++)
        {
            repaired[c] = new double[frames];
            coded[c].AsSpan(0, frames).CopyTo(repaired[c]);

            var stage = new NeuralRepair(network, rate)
            {
                CutoffHz = cutoff,
                CeilingHz = Math.Min(21_000.0, rate / 2.0 * 0.98),
                Amount = amount,
            };

            stage.Process(repaired[c].AsSpan(0, frames));
        }

        return repaired;
    }

    /// <summary>
    /// Mean absolute difference between two spectra, band by band, in decibels. The second signal is
    /// read from <paramref name="offset"/> so a delayed copy is compared against the right samples.
    /// </summary>
    private static (double Above, double Below) Distance(
        double[] original, double[] other, int offset, int frames, int rate, double cutoffHz)
    {
        var plan = new RealFftPlan(FftSize);
        var layout = new BandLayout(200.0, Math.Min(21_000.0, rate / 2.0 * 0.98), 40);
        double[] window = new double[FftSize];
        for (int i = 0; i < FftSize; i++)
        {
            window[i] = 0.5 - (0.5 * Math.Cos(2.0 * Math.PI * i / FftSize));
        }

        double[] frame = new double[FftSize];
        double[] re = new double[FftSize];
        double[] im = new double[FftSize];
        double[] power = new double[plan.Bins];
        float[] a = new float[layout.Count];
        float[] b = new float[layout.Count];
        double binHz = (double)rate / FftSize;

        int split = layout.BandOf(cutoffHz);

        // The band the two signals are lined up on: from 300 Hz up to the cutoff, which no codec
        // touches and no repair writes into.
        int reference = layout.BandOf(300.0);
        double above = 0.0;
        double below = 0.0;
        int counted = 0;
        int guard = rate;

        for (int start = guard; start + FftSize + offset < frames; start += Hop)
        {
            double energy = 0.0;
            for (int i = 0; i < FftSize; i += 4)
            {
                energy += original[start + i] * original[start + i];
            }

            if (Math.Sqrt(energy / (FftSize / 4)) < 1e-4)
            {
                continue;
            }

            Levels(plan, window, frame, re, im, power, layout, original, start, binHz, a);
            Levels(plan, window, frame, re, im, power, layout, other, start + offset, binHz, b);

            // Line the two up on a band well below the cutoff, which both signals still have, rather
            // than on the whole spectrum. Levels() removes each signal's own mean, so a repair that
            // adds energy above the cutoff moves that mean and would otherwise appear to improve the
            // bands below it as well, which it never touched.
            double referenceA = 0.0;
            double referenceB = 0.0;
            for (int band = reference; band < split; band++)
            {
                referenceA += a[band];
                referenceB += b[band];
            }

            int referenceBands = Math.Max(1, split - reference);
            double offsetDb = (referenceA - referenceB) / referenceBands;

            for (int band = 0; band < layout.Count; band++)
            {
                double error = Math.Abs(a[band] - (b[band] + offsetDb));
                if (band >= split)
                {
                    above += error;
                }
                else
                {
                    below += error;
                }
            }

            counted++;
        }

        if (counted == 0)
        {
            return (0.0, 0.0);
        }

        int aboveBands = Math.Max(1, layout.Count - split);
        return (above / (counted * aboveBands), below / (counted * Math.Max(1, split)));
    }

    private static double Levels(
        RealFftPlan plan, double[] window, double[] frame, double[] re, double[] im, double[] power,
        BandLayout layout, double[] samples, int start, double binHz, float[] levels)
    {
        for (int i = 0; i < FftSize; i++)
        {
            frame[i] = samples[start + i] * window[i];
        }

        plan.Forward(frame, re, im);
        for (int bin = 0; bin < power.Length; bin++)
        {
            power[bin] = (re[bin] * re[bin]) + (im[bin] * im[bin]);
        }

        return layout.Levels(power, binHz, levels);
    }
}
