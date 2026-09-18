using Microsoft.ML.OnnxRuntime;

namespace FUPlayer.Core.Dsp.Restoration;

/// <summary>
/// A trained neural restorer: the network that turns a stereo stream a codec delivered at 96 to 160 kbit/s back
/// into something close to the lossless recording at the same rate, 44.1 or 48 kHz.
///
/// The file holds the network between its features and its heads, run a few frames at a time. In: the log-magnitude
/// features of mid and side, [1, 2050, frames]; the rate, [1], 1 at 48 kHz and 0 at 44.1; and the state the previous
/// call left, one tensor for the look-ahead layer and one for each causal block. Out: the heads for mid and side,
/// [2, 5125, frames], five values per bin (mask gain, mask rotation, generated magnitude, generated phase, and the
/// crossover between the two), each answering for the frame <see cref="Lookahead"/> frames before the one that went in
/// with it; and the state for the next call. The features, the frames held back, the masks, the transform and the
/// overlap-add are the player's (<see cref="NeuralRestorer"/>): a graph of small operators costs more to dispatch than
/// that arithmetic does to run in a loop.
///
/// The blocks' state is cleared once the first <see cref="Lookahead"/> frames have gone through, since those frames only
/// answer for time before the stream began; that is what the padding in training amounts to.
/// </summary>
public sealed class NeuralRestorerModel : IDisposable
{
    public const int FftSize = 2048;
    public const int Hop = FftSize / 8;
    public const int Bins = (FftSize / 2) + 1;

    /// <summary>Frames a head lags the input it came with.</summary>
    public const int Lookahead = 6;

    /// <summary>Values each head writes per bin, in order: g, theta, m, phi, u.</summary>
    public const int HeadValues = 5;

    /// <summary>Restorer files end in this, which is how they are told from upscalers in the models folders.</summary>
    public const string Suffix = "restorer.onnx";

    private readonly InferenceSession _session;
    private readonly string[] _inputNames;
    private readonly string[] _outputNames;

    private NeuralRestorerModel(InferenceSession session, string path, (int Channels, int Frames)[] states)
    {
        _session = session;
        Path = path;
        StateShapes = states;
        _inputNames = ["features", "rate48", .. Enumerable.Range(0, states.Length).Select(i => $"state{i}")];
        _outputNames = ["heads", .. Enumerable.Range(0, states.Length).Select(i => $"new_state{i}")];
    }

    public string Path { get; }

    /// <summary>Channels and frames of each state tensor, the batch dimension of one left out: the look-ahead layer's first.</summary>
    public IReadOnlyList<(int Channels, int Frames)> StateShapes { get; }

    /// <summary>Opens a restorer. Inference uses the processor, on as many threads as asked for.</summary>
    public static NeuralRestorerModel Load(string path, int threads = 1)
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            IntraOpNumThreads = Math.Max(1, threads),
        };

        var session = new InferenceSession(path, options);
        try
        {
            string name = System.IO.Path.GetFileName(path);
            if (!session.InputMetadata.TryGetValue("features", out NodeMetadata? features)
                || features.Dimensions.Length != 3 || features.Dimensions[1] != 2 * Bins)
            {
                throw new InvalidDataException($"{name} is not a neural restorer: it has no features input of {2 * Bins} values.");
            }

            if (!session.InputMetadata.TryGetValue("rate48", out NodeMetadata? rate) || rate.Dimensions.Length != 1
                || !session.OutputMetadata.TryGetValue("heads", out NodeMetadata? heads) || heads.Dimensions.Length != 3
                || heads.Dimensions[1] != HeadValues * Bins)
            {
                throw new InvalidDataException($"{name} is not a neural restorer: it has no rate input or no heads.");
            }

            var states = new List<(int, int)>();
            while (session.InputMetadata.TryGetValue($"state{states.Count}", out NodeMetadata? state))
            {
                if (state.Dimensions.Length != 3 || state.Dimensions[1] <= 0 || state.Dimensions[2] <= 0)
                {
                    throw new InvalidDataException($"{name}: state{states.Count} has no fixed size.");
                }

                states.Add((state.Dimensions[1], state.Dimensions[2]));
            }

            // The look-ahead layer sees as many frames behind as ahead, and there is at least one block.
            if (states.Count < 2 || states[0].Item2 != 2 * Lookahead)
            {
                throw new InvalidDataException($"{name}: its state is not laid out as a restorer's with a look-ahead of {Lookahead} frames.");
            }

            return new NeuralRestorerModel(session, path, [.. states]);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Runs <paramref name="frames"/> frames. <paramref name="features"/> holds mid then side, bin by bin, element
    /// [(channel * 1025 + bin) * frames + frame]; <paramref name="heads"/> receives
    /// [((channel * 5 + value) * 1025 + bin) * frames + frame]. <paramref name="state"/> is read and
    /// <paramref name="nextState"/> written, one array per state tensor laid out the same way.
    /// </summary>
    public void Run(float[] features, int frames, bool rate48, float[][] state, float[] heads, float[][] nextState)
    {
        var values = new List<OrtValue>(4 + (2 * state.Length));
        try
        {
            var inputs = new OrtValue[2 + state.Length];
            var outputs = new OrtValue[1 + state.Length];
            inputs[0] = Track(values, OrtValue.CreateTensorValueFromMemory(
                OrtMemoryInfo.DefaultInstance, features.AsMemory(0, 2 * Bins * frames), [1, 2 * Bins, frames]));
            float[] flag = [rate48 ? 1.0f : 0.0f];
            inputs[1] = Track(values, OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, flag.AsMemory(), [1]));
            outputs[0] = Track(values, OrtValue.CreateTensorValueFromMemory(
                OrtMemoryInfo.DefaultInstance, heads.AsMemory(0, 2 * HeadValues * Bins * frames), [2, HeadValues * Bins, frames]));
            for (int s = 0; s < state.Length; s++)
            {
                long[] shape = [1, StateShapes[s].Channels, StateShapes[s].Frames];
                inputs[2 + s] = Track(values, OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, state[s].AsMemory(), shape));
                outputs[1 + s] = Track(values, OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, nextState[s].AsMemory(), shape));
            }

            using var runOptions = new RunOptions();
            _session.Run(runOptions, _inputNames, inputs, _outputNames, outputs);
        }
        finally
        {
            foreach (OrtValue value in values)
            {
                value.Dispose();
            }
        }

        static OrtValue Track(List<OrtValue> values, OrtValue value)
        {
            values.Add(value);
            return value;
        }
    }

    public void Dispose() => _session.Dispose();
}
