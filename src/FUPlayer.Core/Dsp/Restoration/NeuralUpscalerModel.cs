using Microsoft.ML.OnnxRuntime;

namespace FUPlayer.Core.Dsp.Restoration;

/// <summary>
/// A trained neural upscaler: the network that turns the spectrum of a lossy or CD-rate recording,
/// upsampled to 88.2 or 96 kHz, into the spectrum of a high-resolution one.
///
/// It reads and writes short-time Fourier frames of 2,048 points at a hop of 512, periodic Hann,
/// the real and imaginary parts as separate float tensors shaped [batch, 1025, frames]. The player does
/// the transform and the overlap-add itself; the file holds only the network, which is what keeps it
/// exact between training and playback.
///
/// Every output frame depends on <see cref="Context"/> frames either side of it. A frame nearer the
/// edge of what it was given than that sees padding instead of music, and its answer is not the one the
/// network was trained to give, so the stage that runs this only keeps frames at least that far in.
///
/// A network trained with a source condition has a third input, "lossy", shaped [batch]: 1 for a coded
/// or unknown source, 0 for a lossless one. A network without it is run as it was trained, without.
/// </summary>
public sealed class NeuralUpscalerModel : IDisposable
{
    public const int FftSize = 2048;
    public const int Hop = FftSize / 4;
    public const int Bins = (FftSize / 2) + 1;

    /// <summary>Frames either side an output frame depends on: one for the embedding, three per block.</summary>
    public const int Context = 26;

    public const string Extension = ".onnx";

    private readonly NetworkSessions _sessions;
    private readonly string[] _inputNames;
    private readonly string[] _outputNames = ["out_re", "out_im"];

    private NeuralUpscalerModel(NetworkSessions sessions, string path, bool conditioned)
    {
        _sessions = sessions;
        Path = path;
        IsConditioned = conditioned;
        _inputNames = conditioned ? ["re", "im", "lossy"] : ["re", "im"];
    }

    public string Path { get; }

    /// <summary>True when the network takes the lossy/lossless source flag.</summary>
    public bool IsConditioned { get; }

    /// <summary>The graphics adapter the network runs on, or null for the processor.</summary>
    public GraphicsAdapter? Adapter => _sessions.Adapter;

    /// <summary>Why the adapter asked for could not open the network, which then runs on the processor; null otherwise.</summary>
    public string? AdapterFailure => _sessions.AdapterFailure;

    /// <summary>
    /// Opens a model on the processor, on <paramref name="threads"/> threads (0 lets ONNX Runtime choose), or on a
    /// graphics adapter (<see cref="InferenceDevices"/>), falling back to the processor when the adapter cannot open it.
    /// </summary>
    public static NeuralUpscalerModel Load(string path, int threads = 0, GraphicsAdapter? adapter = null, bool allowSpinning = true)
    {
        var sessions = new NetworkSessions(path, adapter, threads, allowSpinning);
        InferenceSession session = sessions.Main;
        foreach (string name in new[] { "re", "im" })
        {
            if (!session.InputMetadata.TryGetValue(name, out NodeMetadata? meta)
                || meta.Dimensions.Length != 3 || meta.Dimensions[1] != Bins)
            {
                sessions.Dispose();
                throw new InvalidDataException($"{System.IO.Path.GetFileName(path)} is not a neural upscaler: it has no '{name}' input of {Bins} bins.");
            }
        }

        bool conditioned = session.InputMetadata.TryGetValue("lossy", out NodeMetadata? flag) && flag.Dimensions.Length == 1;
        var model = new NeuralUpscalerModel(sessions, path, conditioned);
        if (sessions.Adapter is null)
        {
            return model;
        }

        string? disagreement = model.CompareWithProcessor(threads);
        if (disagreement is null)
        {
            return model;
        }

        sessions.Dispose();
        return new NeuralUpscalerModel(sessions.OnProcessor(disagreement), path, conditioned);
    }

    /// <summary>
    /// One call of the size the upscaler makes, on the adapter and on the processor, from the same made-up spectrum: an
    /// adapter whose answer is more than a thousandth of the loudest output away from the processor's is not used.
    /// Measured on an RX 6400 through WebGPU, the two were 7e-8 apart on a peak of 0.036.
    /// </summary>
    private string? CompareWithProcessor(int threads)
    {
        const int frames = (2 * Context) + 64;
        var random = new Random(0x5EED);
        float[] re = new float[Bins * frames], im = new float[Bins * frames];
        for (int i = 0; i < re.Length; i++)
        {
            re[i] = (float)((random.NextDouble() - 0.5) * 0.02);
            im[i] = (float)((random.NextDouble() - 0.5) * 0.02);
        }

        float[] adapterRe = new float[re.Length], adapterIm = new float[im.Length];
        float[] processorRe = new float[re.Length], processorIm = new float[im.Length];
        try
        {
            Run(re, im, frames, adapterRe, adapterIm);
            using var processor = new NeuralUpscalerModel(new NetworkSessions(Path, null, threads), Path, IsConditioned);
            processor.Run(re, im, frames, processorRe, processorIm);
        }
        catch (OnnxRuntimeException ex)
        {
            return ex.Message;
        }

        double peak = 0, worst = 0;
        for (int i = 0; i < re.Length; i++)
        {
            peak = Math.Max(peak, Math.Max(Math.Abs(processorRe[i]), Math.Abs(processorIm[i])));
            double difference = Math.Max(Math.Abs(adapterRe[i] - processorRe[i]), Math.Abs(adapterIm[i] - processorIm[i]));
            worst = double.IsNaN(difference) ? double.PositiveInfinity : Math.Max(worst, difference);
        }

        return worst <= 1e-3 * Math.Max(peak, 1e-6)
            ? null
            : Localization.Loc.F("its answer is {0:0} dB of the output away from the processor's", 20 * Math.Log10(worst / Math.Max(peak, 1e-12)));
    }

    /// <summary>
    /// Runs the network on <paramref name="frames"/> frames of one channel. Inputs and outputs are laid
    /// out bin by bin, as the tensors are: element [bin * frames + frame].
    /// </summary>
    public void Run(float[] re, float[] im, int frames, float[] outRe, float[] outIm, bool lossy = true)
    {
        long[] shape = [1, Bins, frames];
        int count = Bins * frames;
        using var inRe = OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, re.AsMemory(0, count), shape);
        using var inIm = OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, im.AsMemory(0, count), shape);
        using var resRe = OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, outRe.AsMemory(0, count), shape);
        using var resIm = OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, outIm.AsMemory(0, count), shape);
        using var runOptions = new RunOptions();
        if (!IsConditioned)
        {
            _sessions.Run(frames, runOptions, _inputNames, [inRe, inIm], _outputNames, [resRe, resIm]);
            return;
        }

        float[] flag = [lossy ? 1.0f : 0.0f];
        using var inFlag = OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, flag.AsMemory(), [1]);
        _sessions.Run(frames, runOptions, _inputNames, [inRe, inIm, inFlag], _outputNames, [resRe, resIm]);
    }

    public void Dispose() => _sessions.Dispose();
}
