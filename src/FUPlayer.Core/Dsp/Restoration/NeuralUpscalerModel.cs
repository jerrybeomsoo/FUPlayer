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
    public static NeuralUpscalerModel Load(string path, int threads = 0, GraphicsAdapter? adapter = null)
    {
        var sessions = new NetworkSessions(path, adapter, threads);
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
        return new NeuralUpscalerModel(sessions, path, conditioned);
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
