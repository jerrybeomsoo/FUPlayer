using System.Text.Json;
using System.Text.Json.Serialization;

namespace FUPlayer.Core.Dsp.Restoration;

/// <summary>
/// A small feed-forward network that says, band by band, how far a coded spectrum is from the
/// original it came from.
///
/// It is shown the band levels of a coded frame after the high band has been patched in, together
/// with the frames either side of it, and it answers with a gain in decibels for every band. Above
/// the cutoff that gain decides how loud the invented band should be, which is harmonic rebuilding.
/// Below it the gain puts back what the encoder's quantiser took out and steadies a band it keeps
/// dropping, which is artefact reduction. One network, both jobs, because they are the same question
/// asked about different parts of the spectrum.
///
/// It is trained on real coded audio: freely licensed lossless music, run through a real encoder, so
/// that the answer is known for every frame. What it learns is a mapping between spectra. It does not
/// recover the samples the encoder discarded, and nothing can.
///
/// One measurement is worth knowing before reaching for a bigger network. Fitted to 33 releases and
/// judged on releases it had never seen, a frame-by-frame network scored 5.86 where simply applying
/// one constant gain per band scored 3.64, against 6.03 for doing nothing at all. Weight decay did
/// not close the gap. Whatever it learns about an individual frame does not survive a change of
/// record, and on unfamiliar music that costs more than it gains. So a constant curve is a first
/// class answer here, and one is expressible in exactly this format: zero every weight, put the
/// curve in the output biases, and the same arithmetic returns it for every frame.
///
/// What would change that is more releases, or features that describe a frame better than forty band
/// levels do. Not a larger network.
/// </summary>
public sealed class NeuralRepairModel
{
    public const int CurrentVersion = 1;

    /// <summary>Gains are held inside this range, in decibels, at training and at playback.</summary>
    public const float MinGainDb = -12.0f;
    public const float MaxGainDb = 30.0f;

    private readonly float[][] _weights;
    private readonly float[][] _biases;
    private readonly float[][] _activations;

    [JsonConstructor]
    public NeuralRepairModel(
        int version,
        double lowHz,
        double highHz,
        int bands,
        int context,
        int[] layers,
        string weights,
        string? trainedOn,
        long framesSeen,
        double loss)
    {
        Version = version;
        LowHz = lowHz;
        HighHz = highHz;
        Bands = bands;
        Context = context;
        Layers = layers;
        Weights = weights;
        TrainedOn = trainedOn;
        FramesSeen = framesSeen;
        Loss = loss;

        Layout = new BandLayout(lowHz, highHz, bands);
        (_weights, _biases) = Unpack(Convert.FromBase64String(weights), layers);
        _activations = [.. layers.Select(size => new float[size])];
    }

    public int Version { get; }

    public double LowHz { get; }

    public double HighHz { get; }

    public int Bands { get; }

    /// <summary>Frames either side of the one being repaired that the network also sees.</summary>
    public int Context { get; }

    /// <summary>Layer sizes, input first and output last.</summary>
    public int[] Layers { get; }

    /// <summary>Weights and biases, base64 of little-endian float32.</summary>
    public string Weights { get; }

    public string? TrainedOn { get; }

    public long FramesSeen { get; }

    /// <summary>Mean squared error on held-out frames, in decibels squared.</summary>
    public double Loss { get; }

    [JsonIgnore]
    public BandLayout Layout { get; }

    /// <summary>How many numbers the network expects.</summary>
    [JsonIgnore]
    public int InputSize => Layers[0];

    /// <summary>The number of bands it returns a gain for.</summary>
    [JsonIgnore]
    public int OutputSize => Layers[^1];

    public static int ExpectedInputs(int bands, int context) => (bands * ((2 * context) + 1)) + 2;

    public static NeuralRepairModel Load(string path)
    {
        using FileStream stream = File.OpenRead(path);
        NeuralRepairModel model = JsonSerializer.Deserialize<NeuralRepairModel>(stream)
            ?? throw new InvalidDataException("The model file is empty.");

        if (model.Version != CurrentVersion)
        {
            throw new InvalidDataException($"The model is version {model.Version}; this build reads version {CurrentVersion}.");
        }

        if (model.Layers.Length < 2 || model.InputSize != ExpectedInputs(model.Bands, model.Context))
        {
            throw new InvalidDataException(
                $"The model's first layer should take {ExpectedInputs(model.Bands, model.Context)} numbers but takes {model.InputSize}.");
        }

        if (model.OutputSize != model.Bands)
        {
            throw new InvalidDataException($"The model should answer for {model.Bands} bands but answers for {model.OutputSize}.");
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

    public static NeuralRepairModel Create(
        double lowHz, double highHz, int bands, int context, int[] hidden, float[] flat, string? trainedOn, long frames, double loss)
    {
        int[] layers = [ExpectedInputs(bands, context), .. hidden, bands];
        byte[] bytes = new byte[flat.Length * sizeof(float)];
        Buffer.BlockCopy(flat, 0, bytes, 0, bytes.Length);
        return new NeuralRepairModel(
            CurrentVersion, lowHz, highHz, bands, context, layers, Convert.ToBase64String(bytes), trainedOn, frames, loss);
    }

    /// <summary>How many weights and biases a network of these layer sizes needs.</summary>
    public static int ParameterCount(int[] layers)
    {
        int total = 0;
        for (int layer = 1; layer < layers.Length; layer++)
        {
            total += (layers[layer - 1] * layers[layer]) + layers[layer];
        }

        return total;
    }

    /// <summary>
    /// Runs the network. Hidden layers use tanh, which keeps the answer bounded without the dead
    /// units a rectifier gives on spectra that are mostly quiet. The output is linear and then
    /// clamped, because a gain is a gain and there is no sense in +200 dB.
    /// </summary>
    public void Predict(ReadOnlySpan<float> input, Span<float> gainsDb)
    {
        input[..InputSize].CopyTo(_activations[0]);

        for (int layer = 1; layer < Layers.Length; layer++)
        {
            float[] previous = _activations[layer - 1];
            float[] current = _activations[layer];
            float[] weights = _weights[layer - 1];
            float[] biases = _biases[layer - 1];
            bool last = layer == Layers.Length - 1;
            int inputs = previous.Length;

            for (int unit = 0; unit < current.Length; unit++)
            {
                float sum = biases[unit];
                int at = unit * inputs;
                for (int i = 0; i < inputs; i++)
                {
                    sum += weights[at + i] * previous[i];
                }

                current[unit] = last ? Math.Clamp(sum, MinGainDb, MaxGainDb) : MathF.Tanh(sum);
            }
        }

        _activations[^1].AsSpan().CopyTo(gainsDb);
    }

    private static (float[][] Weights, float[][] Biases) Unpack(byte[] bytes, int[] layers)
    {
        int needed = ParameterCount(layers);
        if (bytes.Length != needed * sizeof(float))
        {
            throw new InvalidDataException(
                $"The model should carry {needed} numbers but carries {bytes.Length / sizeof(float)}.");
        }

        float[] flat = new float[needed];
        Buffer.BlockCopy(bytes, 0, flat, 0, bytes.Length);

        var weights = new float[layers.Length - 1][];
        var biases = new float[layers.Length - 1][];
        int at = 0;

        for (int layer = 1; layer < layers.Length; layer++)
        {
            int count = layers[layer - 1] * layers[layer];
            weights[layer - 1] = flat[at..(at + count)];
            at += count;
            biases[layer - 1] = flat[at..(at + layers[layer])];
            at += layers[layer];
        }

        return (weights, biases);
    }
}
