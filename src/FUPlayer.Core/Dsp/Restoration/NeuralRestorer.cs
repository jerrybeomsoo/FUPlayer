using System.Numerics.Tensors;
using FUPlayer.Core.Dsp.Numerics;

namespace FUPlayer.Core.Dsp.Restoration;

/// <summary>
/// Runs a <see cref="NeuralRestorerModel"/> over a stereo stream at 44.1 or 48 kHz, at the rate it arrives.
///
/// Both channels are cut into Fourier frames exactly as the network was trained on them: 2,048 points, a hop of 256,
/// a periodic Hann window, the first frame centred on the first sample. Each frame is turned into mid and side, and
/// the network is given their log magnitudes, floored together 85 dB under the frame's loudest bin, <see cref="FramesPerCall"/>
/// frames at a time with the state the last call left. What comes back for each bin of mid and of side is a mask on
/// the spectrum that went in <see cref="NeuralRestorerModel.Lookahead"/> frames earlier and a component of its own,
/// with a crossover between them:
///
///     out = w * e^g * e^(i*theta) * X + (1 - w) * e^m * e^(i*phi),  w = sigmoid(u),  g in [-8, 4],  m in [-20, 8]
///
/// Mid and side go back to left and right, and the frames are put together by windowed overlap-add divided by the
/// window's own envelope, which is what the training loop's inverse transform does.
///
/// The delay is fixed: a sample is finished once every frame over it has been through the network, the last of those
/// is answered <see cref="NeuralRestorerModel.Lookahead"/> frames after it went in, and frames wait for their call. Four
/// frames per call is 4,351 samples, 99 ms at 44.1 kHz and 91 ms at 48 kHz; two is 3,839; one is 3,583. Fewer frames
/// per call cost more processor for the same audio: on a four-core laptop, one thread, the network ran 8.7 times faster
/// than real time four frames at a time and 4.7 times two at a time.
/// </summary>
public sealed class NeuralRestorer
{
    private const int N = NeuralRestorerModel.FftSize;
    private const int H = NeuralRestorerModel.Hop;
    private const int Bins = NeuralRestorerModel.Bins;
    private const int Lookahead = NeuralRestorerModel.Lookahead;
    private const int Values = NeuralRestorerModel.HeadValues;
    private const double SqrtHalf = 0.7071067811865476;
    private const double FloorBelowPeak = 5.623413251903491e-05; // 10^(-85/20)

    private readonly NeuralRestorerModel _model;
    private readonly RealFftPlan _plan = new(N);
    private readonly double[] _window = new double[N];
    private readonly double _envelope;
    private readonly double[] _startEnvelope = new double[N];
    private readonly double _forwardScale;
    private readonly double _sign;
    private readonly bool _rate48;

    private readonly double[][] _pending = [new double[N], new double[N]];
    private int _pendingFill;

    // What goes to the network and what comes back, laid out as NeuralRestorerModel.Run describes.
    private readonly float[] _features;
    private readonly float[] _heads;
    private float[][] _state;
    private float[][] _nextState;
    private int _queued;
    private bool _warm;
    private long _framesCut;
    private long _framesSent;

    // Mid and side spectra of recent frames, re and im, held until their heads come back: [slot][4 * Bins].
    private readonly double[][] _spectra;

    private readonly double[] _frame = new double[N];
    private readonly double[] _re = new double[N];
    private readonly double[] _im = new double[N];
    private readonly double[] _leftRe = new double[N];
    private readonly double[] _leftIm = new double[N];
    private readonly double[] _rightRe = new double[N];
    private readonly double[] _rightIm = new double[N];

    // One head's values for one frame, gathered into rows the vector primitives can run along.
    private readonly float[][] _row = Enumerable.Range(0, 12).Select(_ => new float[Bins]).ToArray();

    // Overlap-add: _ola[channel][i] holds output sample _olaStart + i.
    private readonly double[][] _ola = [new double[N], new double[N]];
    private long _olaStart;

    // Finished samples waiting to be handed out.
    private readonly double[][] _ready;
    private int _readyRead;
    private int _readyCount;
    private long _samplesIn;

    /// <param name="model">The network.</param>
    /// <param name="sampleRate">44,100 or 48,000: the network is told which.</param>
    /// <param name="framesPerCall">Frames handed to the network at a time, after the first call.</param>
    public NeuralRestorer(NeuralRestorerModel model, int sampleRate, int framesPerCall = 4)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentOutOfRangeException.ThrowIfLessThan(framesPerCall, 1);
        _model = model;
        _rate48 = sampleRate >= 46_050;
        FramesPerCall = framesPerCall;

        for (int i = 0; i < N; i++)
        {
            _window[i] = 0.5 - (0.5 * Math.Cos(2.0 * Math.PI * i / N));
        }

        // The window-square envelope: constant once eight frames overlap, smaller at the very start where the first
        // frames reach back before the first sample.
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

        // Which way round this transform counts phase, and how it scales, measured rather than assumed. The network
        // was trained on torch.stft: unnormalised, e^(-j w n).
        Array.Clear(_frame);
        _frame[0] = 1.0;
        _plan.Forward(_frame, _re, _im);
        _forwardScale = 1.0 / _re[0];
        Array.Clear(_frame);
        _frame[1] = 1.0;
        _plan.Forward(_frame, _re, _im);
        _sign = _im[1] * _forwardScale > 0.0 ? -1.0 : 1.0;

        int callFrames = Math.Max(Lookahead, framesPerCall);
        _features = new float[2 * Bins * callFrames];
        _heads = new float[2 * Values * Bins * callFrames];
        _state = model.StateShapes.Select(s => new float[s.Channels * s.Frames]).ToArray();
        _nextState = model.StateShapes.Select(s => new float[s.Channels * s.Frames]).ToArray();
        _spectra = Enumerable.Range(0, Lookahead + callFrames).Select(_ => new double[4 * Bins]).ToArray();

        Latency = N + ((Lookahead - 1 + framesPerCall) * H) - 1;
        int readySize = Latency + N + (framesPerCall * H);
        _ready = [new double[readySize], new double[readySize]];
        Reset();
    }

    /// <summary>Frames handed to the network at a time.</summary>
    public int FramesPerCall { get; }

    /// <summary>Samples between a sample going in and its restored version coming out.</summary>
    public int Latency { get; }

    public void Reset()
    {
        Array.Clear(_pending[0]);
        Array.Clear(_pending[1]);
        _pendingFill = N / 2; // torch.stft(center=True): the first frame is centred on sample 0
        foreach (float[] state in _state)
        {
            Array.Clear(state);
        }

        _queued = 0;
        _warm = false;
        _framesCut = 0;
        _framesSent = 0;
        Array.Clear(_ola[0]);
        Array.Clear(_ola[1]);
        _olaStart = -(N / 2);
        _readyRead = 0;
        _readyCount = 0;
        _samplesIn = 0;
    }

    /// <summary>
    /// Processes both channels in place: each sample written is the restored sample from <see cref="Latency"/>
    /// earlier. The two spans must be the same length.
    /// </summary>
    public void Process(Span<double> left, Span<double> right)
    {
        if (left.Length != right.Length)
        {
            throw new ArgumentException("Both channels must be the same length.", nameof(right));
        }

        for (int i = 0; i < left.Length; i++)
        {
            _pending[0][_pendingFill] = left[i];
            _pending[1][_pendingFill] = right[i];
            _pendingFill++;
            if (_pendingFill == N)
            {
                Cut();
                _pending[0].AsSpan(H, N - H).CopyTo(_pending[0]);
                _pending[1].AsSpan(H, N - H).CopyTo(_pending[1]);
                _pendingFill = N - H;
            }

            _samplesIn++;
            if (_samplesIn > Latency && _readyCount > 0)
            {
                left[i] = _ready[0][_readyRead];
                right[i] = _ready[1][_readyRead];
                _readyRead = (_readyRead + 1) % _ready[0].Length;
                _readyCount--;
            }
            else
            {
                left[i] = 0.0;
                right[i] = 0.0;
            }
        }
    }

    /// <summary>The first call takes exactly the frames that answer for time before the start; the rest take the same number.</summary>
    private int CallSize => _warm ? FramesPerCall : Lookahead;

    private double[] Spectrum(long frame) => _spectra[(int)(frame % _spectra.Length)];

    private void Cut()
    {
        Transform(_pending[0], _leftRe, _leftIm);
        Transform(_pending[1], _rightRe, _rightIm);

        double[] spectrum = Spectrum(_framesCut);
        int size = CallSize;
        double peak = 0.0;
        for (int b = 0; b < Bins; b++)
        {
            double midRe = (_leftRe[b] + _rightRe[b]) * SqrtHalf;
            double midIm = (_leftIm[b] + _rightIm[b]) * SqrtHalf;
            double sideRe = (_leftRe[b] - _rightRe[b]) * SqrtHalf;
            double sideIm = (_leftIm[b] - _rightIm[b]) * SqrtHalf;
            spectrum[b] = midRe;
            spectrum[Bins + b] = midIm;
            spectrum[(2 * Bins) + b] = sideRe;
            spectrum[(3 * Bins) + b] = sideIm;

            // Magnitudes kept where the features go, until the peak is known.
            _re[b] = Math.Sqrt((midRe * midRe) + (midIm * midIm));
            _im[b] = Math.Sqrt((sideRe * sideRe) + (sideIm * sideIm));
            peak = Math.Max(peak, Math.Max(_re[b], _im[b]));
        }

        double floor = Math.Max(peak * FloorBelowPeak, 1e-7);
        for (int b = 0; b < Bins; b++)
        {
            _features[(b * size) + _queued] = (float)Math.Log(Math.Max(_re[b], floor));
            _features[((Bins + b) * size) + _queued] = (float)Math.Log(Math.Max(_im[b], floor));
        }

        _framesCut++;
        _queued++;
        if (_queued == size)
        {
            Emit(size);
        }
    }

    private void Transform(double[] pending, double[] re, double[] im)
    {
        for (int i = 0; i < N; i++)
        {
            _frame[i] = pending[i] * _window[i];
        }

        _plan.Forward(_frame, re, im);
        for (int b = 0; b < Bins; b++)
        {
            re[b] *= _forwardScale;
            im[b] *= _forwardScale * _sign;
        }
    }

    private void Emit(int size)
    {
        _model.Run(_features, size, _rate48, _state, _heads, _nextState);
        (_state, _nextState) = (_nextState, _state);
        _queued = 0;

        if (!_warm)
        {
            // These frames answer for time before the stream began. The causal blocks forget them, as the padding the
            // network was trained with would have them; the look-ahead layer keeps the real frames it has seen.
            for (int s = 1; s < _state.Length; s++)
            {
                Array.Clear(_state[s]);
            }

            _warm = true;
            _framesSent += size;
            return;
        }

        for (int t = 0; t < size; t++)
        {
            long frame = _framesSent + t - Lookahead;
            double[] spectrum = Spectrum(frame);
            Restore(spectrum, 0, size, t, _leftRe, _leftIm);
            Restore(spectrum, 2 * Bins, size, t, _rightRe, _rightIm);

            // _leftRe/_leftIm now hold mid, _rightRe/_rightIm side; back to left and right, and out of torch's convention.
            for (int b = 0; b < Bins; b++)
            {
                double midRe = _leftRe[b];
                double midIm = _leftIm[b];
                _leftRe[b] = (midRe + _rightRe[b]) * SqrtHalf / _forwardScale;
                _leftIm[b] = (midIm + _rightIm[b]) * SqrtHalf / _forwardScale * _sign;
                _rightRe[b] = (midRe - _rightRe[b]) * SqrtHalf / _forwardScale;
                _rightIm[b] = (midIm - _rightIm[b]) * SqrtHalf / _forwardScale * _sign;
            }

            long start = (frame * H) - (N / 2);
            int at = (int)(start - _olaStart);
            Overlap(_leftRe, _leftIm, _ola[0], at);
            Overlap(_rightRe, _rightIm, _ola[1], at);

            // Everything before the next frame's start is now final.
            long final = start + H;
            int done = (int)(final - _olaStart);
            for (int i = 0; i < done; i++)
            {
                long s = _olaStart + i;
                if (s < 0)
                {
                    continue;
                }

                double envelope = s < N ? _startEnvelope[s] : _envelope;
                int slot = (_readyRead + _readyCount) % _ready[0].Length;
                _ready[0][slot] = envelope > 1e-12 ? _ola[0][i] / envelope : 0.0;
                _ready[1][slot] = envelope > 1e-12 ? _ola[1][i] / envelope : 0.0;
                _readyCount++;
            }

            for (int channel = 0; channel < 2; channel++)
            {
                _ola[channel].AsSpan(done, N - done).CopyTo(_ola[channel]);
                Array.Clear(_ola[channel], N - done, done);
            }

            _olaStart = final;
        }

        _framesSent += size;
    }

    /// <summary>
    /// Applies a head to the spectrum held back for it: mid when <paramref name="offset"/> is 0, side when it is
    /// 2 * 1025. The head is frame <paramref name="t"/> of a call of <paramref name="size"/> frames.
    /// </summary>
    private void Restore(double[] spectrum, int offset, int size, int t, double[] outRe, double[] outIm)
    {
        int first = (offset == 0 ? 0 : 1) * Values * Bins;
        Span<float> g = _row[0], theta = _row[1], m = _row[2], phi = _row[3], w = _row[4];
        Span<float> re = _row[5], im = _row[6], cos = _row[7], sin = _row[8], gain = _row[9], made = _row[10], scratch = _row[11];
        for (int b = 0; b < Bins; b++)
        {
            g[b] = _heads[((first + b) * size) + t];
            theta[b] = _heads[((first + Bins + b) * size) + t];
            m[b] = _heads[((first + (2 * Bins) + b) * size) + t];
            phi[b] = _heads[((first + (3 * Bins) + b) * size) + t];
            w[b] = _heads[((first + (4 * Bins) + b) * size) + t];
            re[b] = (float)spectrum[offset + b];
            im[b] = (float)spectrum[offset + Bins + b];
        }

        // gain = w e^g, made = (1 - w) e^m, with g and m clamped as the network was trained.
        TensorPrimitives.Sigmoid(w, w);
        TensorPrimitives.Max(g, -8.0f, g);
        TensorPrimitives.Min(g, 4.0f, g);
        TensorPrimitives.Exp(g, gain);
        TensorPrimitives.Multiply(gain, w, gain);
        TensorPrimitives.Max(m, -20.0f, m);
        TensorPrimitives.Min(m, 8.0f, m);
        TensorPrimitives.Exp(m, made);
        TensorPrimitives.Subtract(1.0f, w, scratch);
        TensorPrimitives.Multiply(made, scratch, made);

        // The kept part: X rotated by theta and scaled.
        TensorPrimitives.Cos(theta, cos);
        TensorPrimitives.Sin(theta, sin);
        for (int b = 0; b < Bins; b++)
        {
            outRe[b] = gain[b] * ((re[b] * cos[b]) - (im[b] * sin[b]));
            outIm[b] = gain[b] * ((re[b] * sin[b]) + (im[b] * cos[b]));
        }

        // The made part, at its own phase.
        TensorPrimitives.Cos(phi, cos);
        TensorPrimitives.Sin(phi, sin);
        for (int b = 0; b < Bins; b++)
        {
            outRe[b] += made[b] * cos[b];
            outIm[b] += made[b] * sin[b];
        }
    }

    private void Overlap(double[] re, double[] im, double[] ola, int at)
    {
        _plan.Inverse(re, im, _frame);
        for (int i = 0; i < N; i++)
        {
            ola[at + i] += _frame[i] * _window[i];
        }
    }
}
