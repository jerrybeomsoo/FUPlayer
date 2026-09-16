using System.Diagnostics;
using FUPlayer.Core.Dsp.Restoration;

namespace FUPlayer.Cli;

/// <summary>
/// Trains the repair network and says plainly whether it learned anything.
///
/// The number that matters is not the training loss, which only shows that a network can memorise.
/// It is the held-out loss against the do-nothing baseline: the error you would get by leaving every
/// band exactly where the codec left it. A model that cannot beat that is worse than useless, because
/// it moves the audio for nothing.
/// </summary>
internal static class RepairTrainer
{
    public static int Run(string datasetPath, string output, int[] hidden, int epochs, float learningRate, int batch, float decay, string? note)
    {
        using var reader = new RepairDataset.Reader(datasetPath);
        if (reader.Count < 10_000)
        {
            throw new InvalidOperationException(
                $"Only {reader.Count:N0} frames in that set. Build a bigger one: a few hundred thousand is the least worth training on.");
        }

        Console.WriteLine(reader.TryCache()
            ? "The whole set is in memory."
            : "The set is too large to hold in memory, so every pass reads it from disk.");

        int inputs = reader.InputSize;
        int outputs = reader.OutputSize;
        int[] layers = [inputs, .. hidden, outputs];

        // One source file in eight is kept back whole, spread across the corpus. Holding back the
        // tail of the set instead means holding back whichever releases happened to sort last, and
        // three unusual albums can then look exactly like a model that has learned nothing.
        (List<(long Start, long End)> trainingSpans, List<(long Start, long End)> heldSpans) = reader.Split();
        long training = trainingSpans.Sum(span => span.End - span.Start);
        long held = heldSpans.Sum(span => span.End - span.Start);

        Console.WriteLine($"{reader.Count:N0} frames: {training:N0} to train on, {held:N0} held back.");
        Console.WriteLine(reader.Groups.Count > 0
            ? $"Split by source file: {trainingSpans.Count} to train on, {heldSpans.Count} held back, of {reader.Groups.Count}."
            : "This set records no file boundaries, so the last tenth is held back instead. Rebuild it for a better split.");
        Console.WriteLine($"Network {string.Join(" -> ", layers)}, {NeuralRepairModel.ParameterCount(layers):N0} weights, "
            + $"decay {decay:G3}.");

        float[] batchInputs = new float[batch * inputs];
        float[] batchTargets = new float[batch * outputs];

        // Big enough to mix several files together, small enough to stay in memory.
        int shuffle = Math.Max(batch * 8, 131_072);
        float[] chunkInputs = new float[shuffle * inputs];
        float[] chunkTargets = new float[shuffle * outputs];
        int[] order = new int[shuffle];

        double baseline = Baseline(reader, heldSpans, batchInputs, batchTargets, batch, outputs);
        Console.WriteLine($"Doing nothing scores {baseline:F3} dB squared on the held-out frames. That is what to beat.");

        // What a constant answer scores: the average gain per band over the training set, applied to
        // every frame regardless of what is in it. Anything a network is worth has to be worth more
        // than this, because this is what a fixed slope already approximates. If the two are close
        // the answer simply is not in the features, and no amount of training will find it.
        double constant = Constant(reader, trainingSpans, heldSpans, batchInputs, batchTargets, batch, outputs, out float[] curve);
        Console.WriteLine($"One constant answer per band scores {constant:F3}, "
            + $"{100.0 * (1.0 - (constant / baseline)):F1}% under it. That is the real bar.");
        Console.WriteLine();

        var trainer = new NeuralTrainer(layers) { WeightDecay = decay };
        var random = new Random(20260915);
        var clock = Stopwatch.StartNew();
        double best = double.MaxValue;
        float[] bestParameters = (float[])trainer.Parameters.Clone();

        // Roughly how many batches the whole run will take, so the step can be decayed against
        // progress rather than against the epoch number. Decaying per epoch cuts the step by nearly
        // half after one pass, which on a large set is after several thousand updates and far too
        // early: the loss then sits still for the rest of the run.
        long totalSteps = Math.Max(1, epochs * (training / batch));
        long step = 0;

        for (int epoch = 1; epoch <= epochs; epoch++)
        {
            double total = 0.0;
            int steps = 0;
            float rate = learningRate;

            // Records sit in the order they were made, so a run of them is one file at one bit rate
            // and a batch drawn straight from the file would be 256 almost identical frames. A chunk
            // is read, shuffled, and handed out in batches, which mixes across the whole chunk.
            foreach ((long spanStart, long spanEnd) in trainingSpans)
            {
            for (long chunkAt = spanStart; chunkAt + batch <= spanEnd; chunkAt += shuffle)
            {
                int inChunk = reader.Read(chunkAt, (int)Math.Min(shuffle, spanEnd - chunkAt), chunkInputs, chunkTargets);
                if (inChunk < batch)
                {
                    break;
                }

                for (int i = 0; i < inChunk; i++)
                {
                    order[i] = i;
                }

                for (int i = inChunk - 1; i > 0; i--)
                {
                    int j = random.Next(i + 1);
                    (order[i], order[j]) = (order[j], order[i]);
                }

                for (int start = 0; start + batch <= inChunk; start += batch)
                {
                    for (int i = 0; i < batch; i++)
                    {
                        int from = order[start + i];
                        chunkInputs.AsSpan(from * inputs, inputs).CopyTo(batchInputs.AsSpan(i * inputs));
                        chunkTargets.AsSpan(from * outputs, outputs).CopyTo(batchTargets.AsSpan(i * outputs));
                    }

                    // Cosine from the starting step down to a twentieth of it across the whole run.
                    double progress = Math.Min(1.0, (double)step / totalSteps);
                    rate = (float)(learningRate * (0.05 + (0.95 * 0.5 * (1.0 + Math.Cos(Math.PI * progress)))));

                    total += trainer.Train(batchInputs, batchTargets, batch, rate);
                    steps++;
                    step++;
                }
            }
            }

            double heldLoss = Held(trainer, reader, heldSpans, batchInputs, batchTargets, batch);
            string mark = string.Empty;
            if (heldLoss < best)
            {
                best = heldLoss;
                trainer.Parameters.CopyTo(bestParameters, 0);
                mark = "  <- best";
            }

            Console.WriteLine($"epoch {epoch,2}  step {rate:F6}  train {total / Math.Max(1, steps):F3}  "
                + $"held {heldLoss:F3}  ({100.0 * (1.0 - (heldLoss / baseline)):F1}% under the baseline){mark}");
        }

        Console.WriteLine();

        bool useCurve = best >= constant;
        if (useCurve)
        {
            // A constant curve is a model too: zero every weight and put the curve in the output
            // biases, and the same network arithmetic returns it for every frame. Shipping that is
            // honest, and shipping a network that is beaten by it is not.
            Console.WriteLine($"The network scored {best:F3}; one constant answer per band scores {constant:F3}.");
            Console.WriteLine("Whatever it learned about individual frames does not survive a change of record,");
            Console.WriteLine("so the constant curve is what gets saved.");
            bestParameters = Flatten([inputs, .. hidden, outputs], curve);
            best = constant;
        }

        if (best >= baseline)
        {
            Console.WriteLine("Not even the constant curve beat doing nothing. Nothing was saved.");
            return 1;
        }

        NeuralRepairModel model = NeuralRepairModel.Create(
            reader.LowHz, reader.HighHz, reader.Bands, reader.Context, hidden, bestParameters,
            note ?? $"{reader.Groups.Count} releases",
            reader.Count, best);

        PerBand(model, reader, heldSpans, curve, batch);

        string path = Path.Combine(ModelLibrary.Directory, output + ModelLibrary.NeuralExtension);
        model.Save(path);

        Console.WriteLine($"Wrote {path}");
        Console.WriteLine($"  {(useCurve ? "constant curve" : "network")}, held-out error {best:F3} against "
            + $"{baseline:F3} for doing nothing, {100.0 * (1.0 - (best / baseline)):F1}% better");
        Console.WriteLine($"  trained in {clock.Elapsed.TotalMinutes:F1} min");
        return 0;
    }

    /// <summary>
    /// What the model is worth band by band, on the frames it was not trained on.
    ///
    /// One number for the whole spectrum is how a model gets chosen badly. Ninety-six bands are
    /// averaged and about eight of them sit where anything happens, so a model can improve the loss
    /// by a third while leaving the top of the band exactly as wrong as it found it, and nothing in
    /// the headline says so. This is the same held-out error, split by frequency.
    /// </summary>
    private static void PerBand(
        NeuralRepairModel model, RepairDataset.Reader reader, List<(long Start, long End)> spans, float[] curve, int batch)
    {
        int bands = reader.Bands;
        var layout = reader.Layout;
        float[] inputs = new float[batch * reader.InputSize];
        float[] targets = new float[batch * bands];
        float[] gains = new float[bands];

        double[] nothing = new double[bands];
        double[] constant = new double[bands];
        double[] network = new double[bands];
        long counted = 0;

        foreach ((long start, long end) in spans)
        {
            for (long at = start; at < end; at += batch)
            {
                int got = reader.Read(at, (int)Math.Min(batch, end - at), inputs, targets);
                if (got == 0)
                {
                    break;
                }

                for (int sample = 0; sample < got; sample++)
                {
                    model.Predict(inputs.AsSpan(sample * reader.InputSize, reader.InputSize), gains);
                    for (int band = 0; band < bands; band++)
                    {
                        double want = targets[(sample * bands) + band];
                        nothing[band] += want * want;
                        constant[band] += (want - curve[band]) * (want - curve[band]);
                        network[band] += (want - gains[band]) * (want - gains[band]);
                    }
                }

                counted += got;
            }
        }

        if (counted == 0)
        {
            return;
        }

        (string Label, double Low, double High)[] regions =
        [
            ("below 10 kHz", 0.0, 10_000.0),
            ("10 to 15 kHz", 10_000.0, 15_000.0),
            ("15 to 18 kHz", 15_000.0, 18_000.0),
            ("18 to 20 kHz", 18_000.0, 20_000.0),
            ("above 20 kHz", 20_000.0, double.MaxValue),
        ];

        Console.WriteLine();
        Console.WriteLine($"Held-out error by region, rms decibels:  {"bands",5} {"nothing",8} {"curve",8} {"network",8}");
        foreach ((string label, double low, double high) in regions)
        {
            double a = 0.0;
            double b = 0.0;
            double c = 0.0;
            int inRegion = 0;

            for (int band = 0; band < bands; band++)
            {
                double centre = layout.CentreHz(band);
                if (centre < low || centre >= high)
                {
                    continue;
                }

                a += nothing[band];
                b += constant[band];
                c += network[band];
                inRegion++;
            }

            if (inRegion == 0)
            {
                continue;
            }

            double scale = counted * (double)inRegion;
            Console.WriteLine($"  {label,-38} {inRegion,5} {Math.Sqrt(a / scale),8:F2} "
                + $"{Math.Sqrt(b / scale),8:F2} {Math.Sqrt(c / scale),8:F2}");
        }
    }

    /// <summary>
    /// Builds the parameters of a network that answers with the same curve whatever it is shown:
    /// every weight zero, so each hidden unit is tanh(0) and contributes nothing, and the curve in
    /// the output biases.
    /// </summary>
    private static float[] Flatten(int[] layers, float[] curve)
    {
        float[] flat = new float[NeuralRepairModel.ParameterCount(layers)];
        int at = 0;

        for (int layer = 1; layer < layers.Length; layer++)
        {
            at += layers[layer - 1] * layers[layer];
            if (layer == layers.Length - 1)
            {
                curve.CopyTo(flat, at);
            }

            at += layers[layer];
        }

        return flat;
    }

    /// <summary>
    /// The held-out error of the best possible constant answer: the mean gain per band over the
    /// training material. It is the line between knowing what music usually does and knowing what
    /// this frame is doing.
    /// </summary>
    private static double Constant(
        RepairDataset.Reader reader, List<(long Start, long End)> training, List<(long Start, long End)> held,
        float[] inputs, float[] targets, int batch, int outputSize, out float[] curve)
    {
        double[] mean = new double[outputSize];
        long counted = 0;

        foreach ((long start, long end) in training)
        {
            for (long at = start; at < end; at += batch)
            {
                int got = reader.Read(at, (int)Math.Min(batch, end - at), inputs, targets);
                if (got == 0)
                {
                    break;
                }

                for (int sample = 0; sample < got; sample++)
                {
                    for (int band = 0; band < outputSize; band++)
                    {
                        mean[band] += targets[(sample * outputSize) + band];
                    }
                }

                counted += got;
            }
        }

        curve = new float[outputSize];
        for (int band = 0; band < outputSize; band++)
        {
            mean[band] /= Math.Max(1, counted);
            curve[band] = (float)mean[band];
        }

        double total = 0.0;
        long scored = 0;

        foreach ((long start, long end) in held)
        {
            for (long at = start; at < end; at += batch)
            {
                int got = reader.Read(at, (int)Math.Min(batch, end - at), inputs, targets);
                if (got == 0)
                {
                    break;
                }

                for (int sample = 0; sample < got; sample++)
                {
                    for (int band = 0; band < outputSize; band++)
                    {
                        double error = mean[band] - targets[(sample * outputSize) + band];
                        total += error * error;
                    }
                }

                scored += got;
            }
        }

        return total / Math.Max(1, scored * outputSize);
    }

    /// <summary>The error from leaving every band alone, which is what a model has to improve on.</summary>
    private static double Baseline(
        RepairDataset.Reader reader, List<(long Start, long End)> spans, float[] inputs, float[] targets, int batch, int outputSize)
    {
        double total = 0.0;
        long counted = 0;

        foreach ((long start, long end) in spans)
        {
            for (long at = start; at < end; at += batch)
            {
                int got = reader.Read(at, (int)Math.Min(batch, end - at), inputs, targets);
                if (got == 0)
                {
                    break;
                }

                for (int sample = 0; sample < got; sample++)
                {
                    for (int band = 0; band < outputSize; band++)
                    {
                        double value = targets[(sample * outputSize) + band];
                        total += value * value;
                    }
                }

                counted += got;
            }
        }

        return total / Math.Max(1, counted * outputSize);
    }

    private static double Held(
        NeuralTrainer trainer, RepairDataset.Reader reader, List<(long Start, long End)> spans,
        float[] inputs, float[] targets, int batch)
    {
        double total = 0.0;
        int batches = 0;

        foreach ((long start, long end) in spans)
        {
            for (long at = start; at < end; at += batch)
            {
                int got = reader.Read(at, (int)Math.Min(batch, end - at), inputs, targets);
                if (got == 0)
                {
                    break;
                }

                total += trainer.Evaluate(inputs, targets, got);
                batches++;
            }
        }

        return total / Math.Max(1, batches);
    }
}
