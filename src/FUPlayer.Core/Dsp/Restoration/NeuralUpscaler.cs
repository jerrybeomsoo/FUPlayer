using FUPlayer.Core.Dsp.Numerics;

namespace FUPlayer.Core.Dsp.Restoration;

/// <summary>
/// Runs a <see cref="NeuralUpscalerModel"/> over one channel of a stream at 88.2 or 96 kHz.
///
/// The signal is cut into Fourier frames exactly as the network was trained on them: 2,048 points, a
/// hop of 512, a periodic Hann window, and the first frame centred on the first sample. Frames are
/// handed to the network <see cref="ChunkFrames"/> at a time with <see cref="NeuralUpscalerModel.Context"/>
/// frames of music either side, and only the frames in the middle are kept, so every frame that comes
/// out saw the whole of the 272 ms the network looks at rather than padding. They are put back together
/// by windowed overlap-add divided by the window's own envelope, which is what the training loop's
/// inverse transform does.
///
/// The cost of that margin is arithmetic: a chunk of 64 frames runs the network on 116. Smaller chunks
/// answer sooner and cost more, because the margin is paid again for every call. Measured on a
/// four-core laptop with a ten-million-parameter network, 44.1 kHz to 88.2 kHz stereo: 128 frames held
/// the sound 0.92 s and ran 7.8 times faster than real time, 64 frames 0.55 s at 7.1 times, 32 frames
/// 0.36 s at 5.1 times, 16 frames 0.27 s at 3.3 times.
///
/// Each sample comes out <see cref="Latency"/> samples after it went in, a fixed delay whatever the
/// network is doing, which is what lets a dry copy be lined up against it.
/// </summary>
public sealed class NeuralUpscaler
{
    private const int N = NeuralUpscalerModel.FftSize;
    private const int H = NeuralUpscalerModel.Hop;
    private const int Bins = NeuralUpscalerModel.Bins;
    private const int Context = NeuralUpscalerModel.Context;

    private readonly NeuralUpscalerModel _model;
    private readonly RealFftPlan _plan = new(N);
    private readonly double[] _window = new double[N];
    private readonly double _envelope;
    private readonly double[] _startEnvelope = new double[N];
    private readonly double _forwardScale;
    private readonly bool _conjugate;
    private readonly int _span;
    private readonly int _bandFirstBin;
    private readonly double _bandGain;

    private readonly double[] _pending = new double[N];
    private int _pendingFill;
    private readonly float[][] _historyRe;
    private readonly float[][] _historyIm;
    private long _framesCut;
    private long _nextEmit;

    private readonly float[] _inRe;
    private readonly float[] _inIm;
    private readonly float[] _outRe;
    private readonly float[] _outIm;
    private readonly double[] _frame = new double[N];
    private readonly double[] _re = new double[N];
    private readonly double[] _im = new double[N];

    // Overlap-add: _ola[i] holds output sample _olaStart + i.
    private readonly double[] _ola = new double[N];
    private long _olaStart;

    // Finished samples waiting to be handed out.
    private readonly double[] _ready;
    private int _readyRead;
    private int _readyCount;
    private long _samplesIn;

    /// <param name="model">The network.</param>
    /// <param name="chunkFrames">Frames kept from each network call.</param>
    /// <param name="rate">The rate the stage runs at, needed only to place <paramref name="bandFromHz"/>.</param>
    /// <param name="bandFromHz">
    /// Where the band the network writes on its own begins: the source's Nyquist rate. Nothing of the
    /// recording lies above it, so a gain there changes only what was written.
    /// </param>
    /// <param name="bandGainDb">That gain, in decibels. Zero leaves the network's answer as it is.</param>
    public NeuralUpscaler(NeuralUpscalerModel model, int chunkFrames = 64, int rate = 0, double bandFromHz = 0.0, double bandGainDb = 0.0)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkFrames, 1);
        _model = model;
        ChunkFrames = chunkFrames;
        _span = Context + chunkFrames + Context;

        // Strictly above the old Nyquist rate: the interpolator's transition band ends there, and the
        // bin that sits on it still carries a trace of the recording.
        _bandFirstBin = rate > 0 && bandGainDb != 0.0
            ? Math.Clamp((int)Math.Floor(bandFromHz * N / rate) + 1, 1, Bins)
            : Bins;
        _bandGain = Math.Pow(10.0, bandGainDb / 20.0);

        for (int i = 0; i < N; i++)
        {
            _window[i] = 0.5 - (0.5 * Math.Cos(2.0 * Math.PI * i / N));
        }

        // The window-square envelope: constant once four frames overlap, smaller at the very start where
        // the first frames reach back before the first sample.
        _envelope = 0.0;
        for (int k = 0; k < N / H; k++)
        {
            _envelope += _window[k * H] * _window[k * H];
        }

        for (int s = 0; s < N; s++)
        {
            double sum = 0.0;
            for (long k = 0; (k * H) - (N / 2) <= s; k++)
            {
                long offset = s - ((k * H) - (N / 2));
                if (offset >= 0 && offset < N)
                {
                    sum += _window[offset] * _window[offset];
                }
            }

            _startEnvelope[s] = sum;
        }

        // Which way round this transform counts phase, and how it scales, measured rather than assumed.
        // The network was trained on torch.stft: unnormalised, e^(-j w n). A mismatch in scale shifts
        // every log-magnitude it reads; a mismatch in sign conjugates every phase it writes.
        Array.Clear(_frame);
        _frame[0] = 1.0;
        _plan.Forward(_frame, _re, _im);
        _forwardScale = 1.0 / _re[0];
        Array.Clear(_frame);
        _frame[1] = 1.0;
        _plan.Forward(_frame, _re, _im);
        _conjugate = _im[1] * _forwardScale > 0.0;

        _historyRe = new float[_span][];
        _historyIm = new float[_span][];
        for (int i = 0; i < _span; i++)
        {
            _historyRe[i] = new float[Bins];
            _historyIm[i] = new float[Bins];
        }

        _inRe = new float[Bins * _span];
        _inIm = new float[Bins * _span];
        _outRe = new float[Bins * _span];
        _outIm = new float[Bins * _span];

        Latency = ((chunkFrames + Context) * H) + N;
        _ready = new double[Latency + N + (chunkFrames * H)];
        Reset();
    }

    /// <summary>Frames kept from each network call.</summary>
    public int ChunkFrames { get; }

    /// <summary>
    /// Whether the source is coded (or of unknown origin) rather than lossless. Read by a network trained
    /// with a source condition; ignored by one without.
    /// </summary>
    public bool Lossy { get; set; } = true;

    /// <summary>Samples between a sample going in and its repaired version coming out.</summary>
    public int Latency { get; }

    public void Reset()
    {
        Array.Clear(_pending);
        _pendingFill = N / 2; // torch.stft(center=True): the first frame is centred on sample 0
        foreach (float[] row in _historyRe)
        {
            Array.Clear(row);
        }

        foreach (float[] row in _historyIm)
        {
            Array.Clear(row);
        }

        _framesCut = 0;
        _nextEmit = 0;
        Array.Clear(_ola);
        _olaStart = -(N / 2);
        _readyRead = 0;
        _readyCount = 0;
        _samplesIn = 0;
    }

    /// <summary>Processes in place: each sample written is the repaired sample from <see cref="Latency"/> earlier.</summary>
    public void Process(Span<double> samples)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            _pending[_pendingFill++] = samples[i];
            if (_pendingFill == N)
            {
                Cut();
                _pending.AsSpan(H, N - H).CopyTo(_pending);
                _pendingFill = N - H;
            }

            _samplesIn++;
            samples[i] = _samplesIn > Latency && _readyCount > 0 ? Take() : 0.0;
        }
    }

    private void Cut()
    {
        for (int i = 0; i < N; i++)
        {
            _frame[i] = _pending[i] * _window[i];
        }

        _plan.Forward(_frame, _re, _im);
        int slot = Slot(_framesCut);
        float[] rowRe = _historyRe[slot];
        float[] rowIm = _historyIm[slot];
        double sign = _conjugate ? -1.0 : 1.0;
        for (int b = 0; b < Bins; b++)
        {
            rowRe[b] = (float)(_re[b] * _forwardScale);
            rowIm[b] = (float)(_im[b] * _forwardScale * sign);
        }

        _framesCut++;
        if (_framesCut >= _nextEmit + ChunkFrames + Context)
        {
            Emit();
        }
    }

    private void Emit()
    {
        long first = _nextEmit - Context;
        for (int t = 0; t < _span; t++)
        {
            long frame = first + t;
            float[] rowRe = frame < 0 ? Zero : _historyRe[Slot(frame)];
            float[] rowIm = frame < 0 ? Zero : _historyIm[Slot(frame)];
            for (int b = 0; b < Bins; b++)
            {
                _inRe[(b * _span) + t] = rowRe[b];
                _inIm[(b * _span) + t] = rowIm[b];
            }
        }

        _model.Run(_inRe, _inIm, _span, _outRe, _outIm, Lossy);

        double sign = _conjugate ? -1.0 : 1.0;
        for (int t = Context; t < Context + ChunkFrames; t++)
        {
            for (int b = 0; b < Bins; b++)
            {
                _re[b] = _outRe[(b * _span) + t] / _forwardScale;
                _im[b] = _outIm[(b * _span) + t] / _forwardScale * sign;
            }

            for (int b = _bandFirstBin; b < Bins; b++)
            {
                _re[b] *= _bandGain;
                _im[b] *= _bandGain;
            }

            _plan.Inverse(_re, _im, _frame);
            long start = ((first + t) * H) - (N / 2);
            int at = (int)(start - _olaStart);
            for (int i = 0; i < N; i++)
            {
                _ola[at + i] += _frame[i] * _window[i];
            }

            // Everything before the next frame's start is now final.
            long final = start + H;
            int done = (int)(final - _olaStart);
            for (int i = 0; i < done; i++)
            {
                long s = _olaStart + i;
                if (s >= 0)
                {
                    double envelope = s < N ? _startEnvelope[s] : _envelope;
                    Put(envelope > 1e-12 ? _ola[i] / envelope : 0.0);
                }
            }

            _ola.AsSpan(done, N - done).CopyTo(_ola);
            Array.Clear(_ola, N - done, done);
            _olaStart = final;
        }

        _nextEmit += ChunkFrames;
    }

    private static readonly float[] Zero = new float[Bins];

    private int Slot(long frame) => (int)(frame % _span);

    private void Put(double value)
    {
        _ready[(_readyRead + _readyCount) % _ready.Length] = value;
        _readyCount++;
    }

    private double Take()
    {
        double value = _ready[_readyRead];
        _readyRead = (_readyRead + 1) % _ready.Length;
        _readyCount--;
        return value;
    }
}
