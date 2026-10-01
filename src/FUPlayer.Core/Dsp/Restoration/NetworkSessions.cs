using Microsoft.ML.OnnxRuntime;

namespace FUPlayer.Core.Dsp.Restoration;

/// <summary>
/// The ONNX Runtime sessions one network runs in.
///
/// On the processor that is one session, for calls of any number of frames, and the channels call it side by side.
/// On a graphics adapter each number of frames the network is called with gets a session of its own, opened the first
/// time that number is asked for with the frame axis fixed at it, and the calls take turns: DirectML takes one call
/// at a time on a session. Measured on a Quadro M2200: a session that had been called with 6 frames and then with 4 ran
/// every later call five times slower, 20.8 ms instead of 4.3; and fixing the size when the session opens, so that
/// DirectML compiles the graph as one piece, took the restorer's call from 4.1 to 1.9 ms and the upscaler's from 8.4 to
/// 5.9 ms. A size whose own session will not open is run in the first session instead.
/// </summary>
internal sealed class NetworkSessions : IDisposable
{
    private readonly string _path;
    private readonly int _threads;
    private readonly Lock? _gate;
    private readonly Dictionary<int, InferenceSession> _sized = [];

    public NetworkSessions(string path, GraphicsAdapter? adapter, int threads)
    {
        _path = path;
        _threads = threads;
        Main = InferenceDevices.Open(path, adapter, threads, out GraphicsAdapter? used, out string? failure);
        Adapter = used;
        AdapterFailure = failure;
        _gate = used is null ? null : new Lock();
    }

    /// <summary>The session the network was opened in: where its inputs and outputs are read, and every call on the processor.</summary>
    public InferenceSession Main { get; }

    /// <summary>The adapter the network runs on, or null for the processor.</summary>
    public GraphicsAdapter? Adapter { get; }

    /// <summary>Why the adapter asked for could not open the network, which then runs on the processor; null otherwise.</summary>
    public string? AdapterFailure { get; }

    /// <summary>Runs a call of <paramref name="frames"/> frames in the session for that size.</summary>
    public void Run(int frames, RunOptions options, string[] inputNames, OrtValue[] inputs, string[] outputNames, OrtValue[] outputs)
    {
        if (_gate is null)
        {
            Main.Run(options, inputNames, inputs, outputNames, outputs);
            return;
        }

        lock (_gate)
        {
            For(frames).Run(options, inputNames, inputs, outputNames, outputs);
        }
    }

    private InferenceSession For(int frames)
    {
        if (_sized.TryGetValue(frames, out InferenceSession? session))
        {
            return session;
        }

        try
        {
            session = InferenceDevices.OpenOn(_path, Adapter!, _threads, frames);
        }
        catch (OnnxRuntimeException)
        {
            session = Main;
        }

        _sized[frames] = session;
        return session;
    }

    public void Dispose()
    {
        foreach (InferenceSession session in _sized.Values.Distinct())
        {
            if (!ReferenceEquals(session, Main))
            {
                session.Dispose();
            }
        }

        Main.Dispose();
    }
}
