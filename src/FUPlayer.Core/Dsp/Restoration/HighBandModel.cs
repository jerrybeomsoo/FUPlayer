using System.Text.Json;
using System.Text.Json.Serialization;

namespace FUPlayer.Core.Dsp.Restoration;

/// <summary>
/// A model that predicts the shape of the band a codec discarded from the band it kept.
///
/// It is ridge regression: the log level of a handful of bands below the cutoff, with the overall
/// loudness taken out, against the log level of the bands above it. Linear, a few hundred numbers,
/// solved in closed form, and trained from ordinary lossless music where the answer is known because
/// the high band is still there.
///
/// A linear model cannot invent detail. What it can do is learn that a recording whose top octave
/// below the cutoff looks a certain way tends to have a high band of a certain shape, which is a
/// better guess than the fixed slope the rebuilder falls back on. The output is still synthesis.
/// </summary>
public sealed class HighBandModel
{
    /// <summary>Format marker, so a file from an older layout is refused rather than misread.</summary>
    public const int CurrentVersion = 1;

    private const double Floor = 1e-12;

    [JsonConstructor]
    public HighBandModel(
        int version,
        int sampleRate,
        double cutoffHz,
        double lowHz,
        double topHz,
        int inputBands,
        int outputBands,
        double[] weights,
        string? trainedOn,
        long framesSeen)
    {
        Version = version;
        SampleRate = sampleRate;
        CutoffHz = cutoffHz;
        LowHz = lowHz;
        TopHz = topHz;
        InputBands = inputBands;
        OutputBands = outputBands;
        Weights = weights;
        TrainedOn = trainedOn;
        FramesSeen = framesSeen;
    }

    public int Version { get; }

    /// <summary>Rate the model was trained at. Bands are in hertz, so another rate still works.</summary>
    public int SampleRate { get; }

    public double CutoffHz { get; }

    /// <summary>Bottom of the band the features are taken from.</summary>
    public double LowHz { get; }

    /// <summary>Top of the band the model predicts.</summary>
    public double TopHz { get; }

    public int InputBands { get; }

    public int OutputBands { get; }

    /// <summary>Row-major, (InputBands + 1) by OutputBands. The extra input row is the constant term.</summary>
    public double[] Weights { get; }

    public string? TrainedOn { get; }

    public long FramesSeen { get; }

    public static HighBandModel Load(string path)
    {
        using FileStream stream = File.OpenRead(path);
        HighBandModel? model = JsonSerializer.Deserialize<HighBandModel>(stream)
            ?? throw new InvalidDataException("The model file is empty.");

        if (model.Version != CurrentVersion)
        {
            throw new InvalidDataException($"The model is version {model.Version}; this build reads version {CurrentVersion}.");
        }

        int expected = (model.InputBands + 1) * model.OutputBands;
        if (model.InputBands < 2 || model.OutputBands < 1 || model.Weights.Length != expected)
        {
            throw new InvalidDataException($"The model should carry {expected} weights but carries {model.Weights.Length}.");
        }

        return model;
    }

    public void Save(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using FileStream stream = File.Create(path);
        JsonSerializer.Serialize(stream, this, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Edges in hertz of the bands the features are read from.</summary>
    public double[] InputEdges() => Edges(LowHz, CutoffHz, InputBands);

    /// <summary>Edges in hertz of the bands the model predicts.</summary>
    public double[] OutputEdges() => Edges(CutoffHz, TopHz, OutputBands);

    /// <summary>Log-spaced band edges, because hearing and spectra both work that way.</summary>
    public static double[] Edges(double low, double high, int bands)
    {
        double[] edges = new double[bands + 1];
        double ratio = Math.Log(high / low) / bands;
        for (int i = 0; i <= bands; i++)
        {
            edges[i] = low * Math.Exp(ratio * i);
        }

        return edges;
    }

    /// <summary>
    /// Mean power per band in decibels, with the overall level removed. Taking the level out is what
    /// lets one model serve quiet and loud passages alike.
    /// </summary>
    public static double Features(ReadOnlySpan<double> power, double binHz, double[] edges, Span<double> features)
    {
        int bins = power.Length;
        double total = 0.0;
        for (int band = 0; band < features.Length; band++)
        {
            int low = Math.Clamp((int)(edges[band] / binHz), 0, bins - 1);
            int high = Math.Clamp((int)Math.Ceiling(edges[band + 1] / binHz), low + 1, bins);

            double sum = 0.0;
            for (int bin = low; bin < high; bin++)
            {
                sum += power[bin];
            }

            features[band] = 10.0 * Math.Log10((sum / (high - low)) + Floor);
            total += features[band];
        }

        double level = total / features.Length;
        for (int band = 0; band < features.Length; band++)
        {
            features[band] -= level;
        }

        return level;
    }

    /// <summary>Predicts the band levels above the cutoff, in decibels relative to the input level.</summary>
    public void Predict(ReadOnlySpan<double> features, Span<double> prediction)
    {
        for (int output = 0; output < OutputBands; output++)
        {
            double sum = Weights[(InputBands * OutputBands) + output];
            for (int input = 0; input < InputBands; input++)
            {
                sum += Weights[(input * OutputBands) + output] * features[input];
            }

            prediction[output] = sum;
        }
    }
}

/// <summary>
/// Accumulates the normal equations for <see cref="HighBandModel"/> and solves them.
///
/// Training needs music that still has its high band, so it runs on lossless files: the features come
/// from below the cutoff and the answer is read straight off the part a codec would have thrown away.
/// </summary>
public sealed class HighBandTrainer
{
    private readonly double[,] _xx;
    private readonly double[,] _xy;
    private readonly double[] _features;
    private readonly double[] _targets;
    private readonly double[] _inputEdges;
    private readonly double[] _outputEdges;

    private long _frames;

    public HighBandTrainer(int sampleRate, double cutoffHz, double lowHz = 500.0, double topHz = 0.0, int inputBands = 16, int outputBands = 8)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        if (cutoffHz <= lowHz * 2.0)
        {
            throw new ArgumentException("The cutoff has to sit well above the bottom of the feature band.", nameof(cutoffHz));
        }

        SampleRate = sampleRate;
        CutoffHz = cutoffHz;
        LowHz = lowHz;
        TopHz = topHz > cutoffHz ? topHz : Math.Min(sampleRate / 2.0 * 0.95, cutoffHz * 1.5);
        InputBands = inputBands;
        OutputBands = outputBands;

        _xx = new double[inputBands + 1, inputBands + 1];
        _xy = new double[inputBands + 1, outputBands];
        _features = new double[inputBands];
        _targets = new double[outputBands];
        _inputEdges = HighBandModel.Edges(LowHz, CutoffHz, inputBands);
        _outputEdges = HighBandModel.Edges(CutoffHz, TopHz, outputBands);
    }

    public int SampleRate { get; }

    public double CutoffHz { get; }

    public double LowHz { get; }

    public double TopHz { get; }

    public int InputBands { get; }

    public int OutputBands { get; }

    public long Frames => _frames;

    /// <summary>Adds one frame's power spectrum, taken from full-band material.</summary>
    public void Add(ReadOnlySpan<double> power, double binHz)
    {
        double lowLevel = HighBandModel.Features(power, binHz, _inputEdges, _features);
        double highLevel = HighBandModel.Features(power, binHz, _outputEdges, _targets);

        // Features returns the shape with the level taken out. What is wanted as the answer is the
        // high band's level measured against the low band's, so the difference goes back in.
        for (int band = 0; band < OutputBands; band++)
        {
            _targets[band] += highLevel - lowLevel;
        }

        for (int i = 0; i <= InputBands; i++)
        {
            double xi = i == InputBands ? 1.0 : _features[i];
            for (int j = 0; j <= InputBands; j++)
            {
                _xx[i, j] += xi * (j == InputBands ? 1.0 : _features[j]);
            }

            for (int j = 0; j < OutputBands; j++)
            {
                _xy[i, j] += xi * _targets[j];
            }
        }

        _frames++;
    }

    /// <summary>Solves for the weights. Ridge because neighbouring bands move together.</summary>
    public HighBandModel Solve(double ridge = 1e-3, string? trainedOn = null)
    {
        if (_frames == 0)
        {
            throw new InvalidOperationException("No frames were added.");
        }

        int n = InputBands + 1;
        double[,] a = new double[n, n];
        double[,] b = new double[n, OutputBands];
        double scale = 1.0 / _frames;

        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                a[i, j] = _xx[i, j] * scale;
            }

            a[i, i] += ridge;

            for (int j = 0; j < OutputBands; j++)
            {
                b[i, j] = _xy[i, j] * scale;
            }
        }

        Solve(a, b, n, OutputBands);

        double[] weights = new double[n * OutputBands];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < OutputBands; j++)
            {
                weights[(i * OutputBands) + j] = b[i, j];
            }
        }

        return new HighBandModel(
            HighBandModel.CurrentVersion, SampleRate, CutoffHz, LowHz, TopHz,
            InputBands, OutputBands, weights, trainedOn, _frames);
    }

    /// <summary>Gauss-Jordan with partial pivoting. The matrix is seventeen by seventeen.</summary>
    private static void Solve(double[,] a, double[,] b, int n, int columns)
    {
        for (int column = 0; column < n; column++)
        {
            int pivot = column;
            for (int row = column + 1; row < n; row++)
            {
                if (Math.Abs(a[row, column]) > Math.Abs(a[pivot, column]))
                {
                    pivot = row;
                }
            }

            if (Math.Abs(a[pivot, column]) < 1e-18)
            {
                throw new InvalidOperationException("The training data was too uniform to fit a model to.");
            }

            if (pivot != column)
            {
                for (int k = 0; k < n; k++)
                {
                    (a[column, k], a[pivot, k]) = (a[pivot, k], a[column, k]);
                }

                for (int k = 0; k < columns; k++)
                {
                    (b[column, k], b[pivot, k]) = (b[pivot, k], b[column, k]);
                }
            }

            double diagonal = a[column, column];
            for (int k = 0; k < n; k++)
            {
                a[column, k] /= diagonal;
            }

            for (int k = 0; k < columns; k++)
            {
                b[column, k] /= diagonal;
            }

            for (int row = 0; row < n; row++)
            {
                if (row == column)
                {
                    continue;
                }

                double factor = a[row, column];
                if (factor == 0.0)
                {
                    continue;
                }

                for (int k = 0; k < n; k++)
                {
                    a[row, k] -= factor * a[column, k];
                }

                for (int k = 0; k < columns; k++)
                {
                    b[row, k] -= factor * b[column, k];
                }
            }
        }
    }
}
