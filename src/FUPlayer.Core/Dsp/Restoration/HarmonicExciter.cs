using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FUPlayer.Core.Dsp.Analysis;
using FUPlayer.Core.Dsp.Design;
using FUPlayer.Core.Dsp.Numerics;

namespace FUPlayer.Core.Dsp.Restoration;

/// <summary>
/// Writes the music's own harmonics above the top of its spectrum, running at twice the source rate so they reach
/// past the source's Nyquist frequency: up to 44.1 kHz for a 44.1 kHz source, 48 for 48.
///
/// The band from 7 kHz up to the top of the music (a codec's edge, or 0.45 of the source rate for a full-band source)
/// goes through a curve of the second and third degree at four times the source rate. The sum of two partials of one
/// note is another partial of it, so what comes out lies on the notes' own series, and at four times the rate the
/// third degree has room: nothing folds back below Nyquist. The result is analysed at that rate, and only its bins
/// below the output's Nyquist frequency are used.
///
/// How loud is measured from the music, frame by frame: the drive band's level is continued above the top along its own
/// slope made 12 dB an octave steeper, which is where hi-res masters were found to lie. In every sixth of an octave the
/// harmonics fill what is missing under that line: all of it where the band is empty, nothing where something already
/// reaches it, such as the neural restorer's or the upscaler's band. Below the top nothing changes, and until the top
/// is known the stage is a plain delay.
/// </summary>
public sealed class HarmonicExciter
{
    /// <summary>Points of the mixing transform, at the rate the exciter runs at.</summary>
    public const int FftSize = 2048;

    public const int Hop = FftSize / 4;

    /// <summary>The lowest frequency that drives the harmonics.</summary>
    public const double DriveFromHz = 7_000.0;

    /// <summary>
    /// The top after the neural restorer, as a fraction of the source rate: just under Nyquist, which the restorer
    /// fills to. Starting at 0.45, as for a full-band source, filled the restorer's own roll-off in its last kilohertz
    /// and put the band from the codec's edge to 0.47 of the rate 1.2 dB over the masters, where the restorer alone
    /// was 0.9 dB under; from here that band is the restorer's alone.
    /// </summary>
    public const double AfterRestorerTop = 0.49;

    private const int N = FftSize;
    private const int H = Hop;
    private const int Bins = (N / 2) + 1;

    /// <summary>The harmonics are made at twice the rate the exciter runs at: four times the source rate.</summary>
    private const int Oversampling = 2;

    private const int N2 = N * Oversampling;
    private const int H2 = H * Oversampling;

    /// <summary>
    /// Taps of the twofold interpolator for the drive: 4k + 1, so its delay is a whole input sample. The drive lies
    /// below a quarter of the rate, so its image starts at three quarters and the transition is wide.
    /// </summary>
    private const int InterpolatorTaps = 41;

    /// <summary>The drive stops this far under the top, clear of a codec's own roll-off.</summary>
    private const double DriveGuardHz = 300.0;

    /// <summary>
    /// Width of the drive band-pass's edges, and how far it stops what lies outside: bass let through 60 dB down
    /// only meets the drive in sums and differences that stay inside the drive band or under it, and nothing below
    /// the top is written.
    /// </summary>
    private const double DriveTransitionHz = 1_000.0;

    private const double DriveStopbandDb = 60.0;

    /// <summary>
    /// The harmonics ramp in over this much, centred on the top: a codec's roll-off leaves the last few hundred hertz
    /// under its cutoff half empty, and a ramp that only began there left a dark line between the music and them.
    /// Where the music still reaches the line nothing is added anyway.
    /// </summary>
    private const double RampHz = 300.0;

    /// <summary>
    /// Above the top the music's own slope falls this much faster an octave. Measured against 40 stretches of hi-res
    /// masters from different albums, brought to CD rate and excited: continuing the drive band's slope as it was put
    /// the band above the source's Nyquist frequency 8 to 15 dB over the masters', and 12 dB an octave steeper puts it
    /// 0.5, 0.6 and 1.5 dB over them in the three bands up to 0.95 of the source rate, the spread from song to song 10
    /// to 15 dB. Between a codec's edge and Nyquist, on 27 held-out coded stretches, it comes out 1.4 dB over the
    /// masters.
    /// </summary>
    private const double SlopeSteepeningDb = 12.0;

    /// <summary>
    /// The continued slope falls at least this much an octave, and at most <see cref="SteepestSlopeDb"/>. A drive band
    /// that rises towards the top, bright cymbals over a restored band, would otherwise be continued almost level.
    /// </summary>
    private const double ShallowestSlopeDb = -12.0;

    private const double SteepestSlopeDb = -24.0;

    /// <summary>
    /// The most the harmonics add in a frame, against the power of the band that drives them: a quarter. On one of 27
    /// held-out stretches the band above Nyquist came out 6 dB louder than the drive band before this was set.
    /// </summary>
    private const double MostAdded = 0.25;

    /// <summary>
    /// Per hop, how fast the drive band's level follows the music, the same up and down: one that rose faster than it
    /// fell read a fluctuating band 1 to 3 dB louder than it was.
    /// </summary>
    private const double LevelSmoothing = 0.6;

    /// <summary>
    /// Per hop, how slowly the drive band's slope is measured, the same both ways: a level that rises faster than it
    /// falls reads a fluctuating band as louder than it is, and the upper half fluctuates more, which read the slope
    /// 2 to 3 dB an octave shallower than the music's.
    /// </summary>
    private const double SlopeSmoothing = 0.95;

    private readonly int _rate;
    private readonly double _binHz;
    private readonly RealFftPlan _plan = new(N);
    private readonly RealFftPlan _plan2 = new(N2);
    private readonly double[] _window = new double[N];
    private readonly double[] _window2 = new double[N2];

    /// <summary>
    /// Blackman-Harris, for measuring levels. Through the Hann window a loud band leaks a dozen bins into a quiet one
    /// beside it and passes for level there; this one's sidelobes are 92 dB down.
    /// </summary>
    private readonly double[] _lowLeak = new double[N];

    /// <summary>Noise's power through the Hann window over its power through <see cref="_lowLeak"/>.</summary>
    private readonly double _lowLeakScale;

    /// <summary>The interpolator's two phases, each reversed so an input window runs forward against it.</summary>
    private readonly double[][] _phases;

    private readonly int _interpolatorHistory;
    private readonly int _bandPassTaps;

    private double[] _bandPass = [];
    private int[] _bandStart = [];
    private double[] _bandCentre = [];
    private readonly double[] _ramp = new double[Bins];
    private readonly double[] _density = new double[Bins];
    private readonly double[] _existing = new double[Bins];
    private readonly double[] _weight = new double[Bins];
    private int _fillStart;
    private int _fillEnd;
    private int _lowerFrom;
    private int _lowerTo;
    private int _upperTo;
    private double _lowerCentreHz;
    private double _upperCentreHz;

    // Scratch for one frame.
    private readonly double[] _frame = new double[N];
    private readonly double[] _frameLowLeak = new double[N];
    private readonly double[] _frame2 = new double[N2];
    private readonly double[] _xRe = new double[N];
    private readonly double[] _xIm = new double[N];
    private readonly double[] _lRe = new double[N];
    private readonly double[] _lIm = new double[N];
    private readonly double[] _eRe = new double[N2];
    private readonly double[] _eIm = new double[N2];
    private readonly double[] _gain = new double[Bins];
    private readonly double[] _bandTarget = new double[64];
    private readonly double[] _bandHarmonic = new double[64];
    private readonly double[] _logGain = new double[64];

    // The stream.
    private readonly double[] _input;
    private readonly double[] _drive;
    private readonly double[] _driveHop = new double[H];
    private readonly double[] _oversampledHop = new double[H2];
    private readonly double[] _harmonics = new double[N2];
    private readonly double[] _ola = new double[N];
    private readonly double[] _ready = new double[N];
    private int _fill;
    private int _hopFill;
    private int _readyRead;
    private int _readyCount;
    private long _frames;
    private long _samplesIn;
    private double _driveLevel;
    private double _lowerPower;
    private double _upperPower;
    private double _lowerSlow;
    private double _upperSlow;
    private double _addedDb = double.NegativeInfinity;

    /// <param name="rate">The rate the exciter runs at: twice the source's, 88.2 or 96 kHz for 44.1 or 48.</param>
    public HarmonicExciter(int rate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rate, 32_000);
        _rate = rate;
        _binHz = (double)rate / N;

        double hannPower = 0.0;
        double lowLeakPower = 0.0;
        for (int i = 0; i < N; i++)
        {
            double t = 2.0 * Math.PI * i / N;
            _window[i] = 0.5 - (0.5 * Math.Cos(t));
            _lowLeak[i] = 0.35875 - (0.48829 * Math.Cos(t)) + (0.14128 * Math.Cos(2.0 * t)) - (0.01168 * Math.Cos(3.0 * t));
            hannPower += _window[i] * _window[i];
            lowLeakPower += _lowLeak[i] * _lowLeak[i];
        }

        _lowLeakScale = hannPower / lowLeakPower;
        for (int i = 0; i < N2; i++)
        {
            _window2[i] = 0.5 - (0.5 * Math.Cos(2.0 * Math.PI * i / N2));
        }

        // Twofold, for a drive below a quarter of the rate: pass to an eighth of the doubled rate, stop from three
        // eighths, where the drive's image begins.
        double[] prototype = FirDesign.Lowpass(new LowpassSpec(0.125, 0.375, 90.0), Oversampling, InterpolatorTaps);
        int perPhase = (prototype.Length + Oversampling - 1) / Oversampling;
        _phases = new double[Oversampling][];
        for (int p = 0; p < Oversampling; p++)
        {
            _phases[p] = new double[perPhase];
            for (int k = 0; k < perPhase; k++)
            {
                int index = p + (k * Oversampling);
                _phases[p][perPhase - 1 - k] = index < prototype.Length ? prototype[index] : 0.0;
            }
        }

        _interpolatorHistory = perPhase - 1;

        // The band-pass is redesigned for each top but keeps its length, so the delay never moves.
        _bandPassTaps = FirDesign.EstimateLength(new LowpassSpec(0.25, 0.25 + (DriveTransitionHz / rate), DriveStopbandDb)) | 1;

        // The drive and the harmonics made from it trail the signal by the band-pass's delay and the interpolator's;
        // the signal is held back as much, so the two meet in one frame.
        Delay = ((_bandPassTaps - 1) / 2) + ((InterpolatorTaps - 1) / 2 / Oversampling);
        Latency = Delay + N - 1;

        _input = new double[Math.Max(N + Delay, H + _bandPassTaps - 1)];
        _drive = new double[_interpolatorHistory + H];
        WarmUp();
    }

    /// <summary>The rate the exciter runs at.</summary>
    public int Rate => _rate;

    /// <summary>Samples from a sample going in to it coming out, at <see cref="Rate"/>.</summary>
    public int Latency { get; }

    /// <summary>Where the music's spectrum ends and the harmonics begin, or 0 while that is not known.</summary>
    public double TopHz { get; private set; }

    /// <summary>The lowest frequency driving the harmonics: 7 kHz, or half the top when the top is under 14 kHz.</summary>
    public double DriveLowHz { get; private set; }

    /// <summary>
    /// How loud the harmonics written just now are against the band that drives them, in decibels; minus infinity
    /// while nothing is written.
    /// </summary>
    public double AddedDb => _addedDb;

    /// <summary>Samples the signal is held back so the harmonics made from it arrive with it.</summary>
    internal int Delay { get; }

    /// <summary>
    /// The top of the music from the codec detector: where a band-limited source's spectrum begins to fall, or 0.45
    /// of the source rate for one that runs to the top of its band; 0 until the detector knows.
    /// </summary>
    /// <remarks>
    /// Where the fall begins rather than the cutoff, where it has fallen 6 dB. Vorbis leaves a residue 16 dB down for
    /// several hundred hertz above its band, and averaged over a minute of one song its cutoff drifted from 17.1 to
    /// 18.1 kHz while the band itself ended at 17.2: starting there left a dark stripe between the music and the
    /// harmonics. Starting lower costs nothing, since the harmonics only fill what falls short of the line.
    /// </remarks>
    public static double TopFor(BandwidthEstimate estimate, int sourceRate) => estimate.Verdict switch
    {
        BandwidthVerdict.BandLimited => Math.Min(estimate.TransitionHz > 0.0 ? estimate.TransitionHz : estimate.CutoffHz, 0.45 * sourceRate),
        BandwidthVerdict.FullBand => 0.45 * sourceRate,
        _ => 0.0,
    };

    /// <summary>
    /// Where the music ends. Zero, or anything below 5 kHz or above a quarter of <see cref="Rate"/>, leaves the
    /// signal alone. Small moves are ignored, as the detector's estimate settles.
    /// </summary>
    public void SetTop(double hz)
    {
        double top = hz >= 5_000.0 && hz <= 0.25 * _rate ? hz : 0.0;
        if (top == 0.0 || TopHz == 0.0 ? top == TopHz : Math.Abs(top - TopHz) < TopHz * 0.03)
        {
            return;
        }

        TopHz = top;
        if (top > 0.0)
        {
            Design(top);
        }
        else
        {
            _bandPass = [];
            _addedDb = double.NegativeInfinity;
        }
    }

    public void Reset()
    {
        Array.Clear(_input);
        Array.Clear(_drive);
        Array.Clear(_harmonics);
        Array.Clear(_ola);
        _fill = _input.Length;
        _hopFill = 0;
        _readyRead = 0;
        _readyCount = 0;
        _frames = 0;
        _samplesIn = 0;
        _driveLevel = 0.0;
        _lowerPower = 0.0;
        _upperPower = 0.0;
        _lowerSlow = 0.0;
        _upperSlow = 0.0;
        _addedDb = double.NegativeInfinity;
    }

    /// <summary>
    /// Every path once, on a tone under a top, before there is a stream to keep up with; then back to where a new
    /// exciter starts. The top usually arrives a second into a track, while the output buffer is still filling, and
    /// the first frames that write harmonics are the ones that compile what they call.
    /// </summary>
    private void WarmUp()
    {
        Reset();
        SetTop(0.2 * _rate);
        var tone = new double[8 * H];
        for (int i = 0; i < tone.Length; i++)
        {
            tone[i] = (0.1 * Math.Sin(2.0 * Math.PI * 0.12 * i)) + (0.05 * Math.Sin(2.0 * Math.PI * 0.17 * i));
        }

        Process(tone);
        TopHz = 0.0;
        _bandPass = [];
        Reset();
    }

    /// <summary>
    /// Processes one channel in place at <see cref="Rate"/>: each sample written is the input from
    /// <see cref="Latency"/> samples earlier, with harmonics above the top.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Process(Span<double> signal)
    {
        int done = 0;
        while (done < signal.Length)
        {
            int take = Math.Min(signal.Length - done, H - _hopFill);
            Span<double> part = signal.Slice(done, take);
            Append(part);
            if (_hopFill == H)
            {
                _hopFill = 0;
                Frame();
            }

            // The first N - 1 samples out come from before the start: silence. Every later one is finished by now,
            // since a sample is complete once the frame ending N - 1 samples after it has been added.
            int silent = (int)Math.Clamp((N - 1) - _samplesIn, 0, take);
            part[..silent].Clear();
            Take(part[silent..]);
            _samplesIn += take;
            done += take;
        }
    }

    private void Design(double top)
    {
        double high = top - DriveGuardHz;
        double low = Math.Min(DriveFromHz, top / 2.0);
        DriveLowHz = low;

        // A band-pass as the difference of two low-passes of the same length and window.
        double[] upper = FirDesign.Lowpass(Spec(high - DriveTransitionHz, high), 1.0, _bandPassTaps);
        double[] lower = FirDesign.Lowpass(Spec(Math.Max(100.0, low - DriveTransitionHz), low), 1.0, _bandPassTaps);
        var bandPass = new double[_bandPassTaps];
        for (int k = 0; k < bandPass.Length; k++)
        {
            bandPass[k] = upper[k] - lower[k];
        }

        _bandPass = bandPass;

        // The drive band's lower and upper halves, on a logarithmic scale, for its level and slope.
        double middle = Math.Sqrt(low * high);
        _lowerFrom = (int)Math.Ceiling(low / _binHz);
        _lowerTo = (int)Math.Floor(middle / _binHz);
        _upperTo = (int)Math.Floor(high / _binHz);
        _lowerCentreHz = Math.Sqrt(low * middle);
        _upperCentreHz = Math.Sqrt(middle * high);

        // Written from the top to just under Nyquist, rising over the ramp.
        double rampFrom = top - (RampHz / 2.0);
        _fillStart = (int)Math.Ceiling(rampFrom / _binHz);
        _fillEnd = Bins - 2;
        for (int b = 0; b < Bins; b++)
        {
            _ramp[b] = b < _fillStart || b > _fillEnd ? 0.0 : Math.Clamp(((b * _binHz) - rampFrom) / RampHz, 0.0, 1.0);
        }

        // Sixths of an octave from the start of the ramp, the last one short.
        var starts = new List<int>();
        var centres = new List<double>();
        double nyquist = _fillEnd * _binHz;
        for (double from = rampFrom; from < nyquist - _binHz && starts.Count < _bandTarget.Length - 1; from *= Math.Pow(2.0, 1.0 / 6.0))
        {
            double to = Math.Min(nyquist, from * Math.Pow(2.0, 1.0 / 6.0));
            starts.Add((int)Math.Round(from / _binHz));
            centres.Add(Math.Sqrt(from * to) / _binHz);
        }

        starts.Add(_fillEnd + 1);
        _bandStart = [.. starts];
        _bandCentre = [.. centres];

        LowpassSpec Spec(double pass, double stop) => new(pass / _rate, stop / _rate, DriveStopbandDb);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Frame()
    {
        // The signal's frame: the last N samples of it, held back by the drive's delay.
        _input.AsSpan(_fill - Delay - N, N).CopyTo(_frame);
        bool exciting = TopHz > 0.0 && _bandPass.Length == _bandPassTaps;
        if (exciting)
        {
            for (int i = 0; i < N; i++)
            {
                _frameLowLeak[i] = _frame[i] * _lowLeak[i];
            }

            _plan.Forward(_frameLowLeak, _lRe, _lIm);
        }

        for (int i = 0; i < N; i++)
        {
            _frame[i] *= _window[i];
        }

        _plan.Forward(_frame, _xRe, _xIm);

        if (exciting)
        {
            MakeHarmonics();
            AddHarmonics();
        }

        _plan.Inverse(_xRe, _xIm, _frame);
        OverlapAdd();
    }

    /// <summary>The drive for the last hop, twofold, through the curve, and the transform of the last N2 samples.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void MakeHarmonics()
    {
        // Band-pass: the hop's H samples, each against the taps behind it.
        ReadOnlySpan<double> history = _input.AsSpan(_fill - (H + _bandPassTaps - 1), H + _bandPassTaps - 1);
        FirKernel.Convolve(ref MemoryMarshal.GetArrayDataReference(_bandPass), (nuint)_bandPass.Length,
            ref MemoryMarshal.GetReference(history), 1, ref MemoryMarshal.GetArrayDataReference(_driveHop), 1, H);

        // Twofold: output 2n + p is phase p against the drive up to n.
        _drive.AsSpan(H, _interpolatorHistory).CopyTo(_drive);
        _driveHop.CopyTo(_drive.AsSpan(_interpolatorHistory));
        for (int p = 0; p < Oversampling; p++)
        {
            FirKernel.Convolve(ref MemoryMarshal.GetArrayDataReference(_phases[p]), (nuint)_phases[p].Length,
                ref MemoryMarshal.GetArrayDataReference(_drive), 1, ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_oversampledHop), p),
                Oversampling, H);
        }

        // Second and third degree. The cube is weighed against a slow measure of the drive's level so the mixture does
        // not change with how loud the music is.
        double power = SimdMath.SumOfSquares(_oversampledHop) / H2;
        _driveLevel = _driveLevel <= 0.0 ? Math.Sqrt(power) : (0.9 * _driveLevel) + (0.1 * Math.Sqrt(power));
        double cube = _driveLevel > 1e-12 ? 0.5 / _driveLevel : 0.0;
        for (int i = 0; i < H2; i++)
        {
            double u = _oversampledHop[i];
            _oversampledHop[i] = u * u * (1.0 + (cube * u));
        }

        _harmonics.AsSpan(H2).CopyTo(_harmonics);
        _oversampledHop.CopyTo(_harmonics.AsSpan(N2 - H2));
        for (int i = 0; i < N2; i++)
        {
            _frame2[i] = _harmonics[i] * _window2[i];
        }

        _plan2.Forward(_frame2, _eRe, _eIm);
    }

    /// <summary>Adds the harmonics above the top, where the continued slope says the band falls short.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void AddHarmonics()
    {
        // The drive band's two halves, through the low-leakage window, following the music up quickly and back down
        // over a few hundred milliseconds.
        double lower = LowLeakPower(_lowerFrom, _lowerTo) / Math.Max(1, _lowerTo - _lowerFrom + 1);
        double upper = LowLeakPower(_lowerTo + 1, _upperTo) / Math.Max(1, _upperTo - _lowerTo);
        _lowerPower = Follow(_lowerPower, lower);
        _upperPower = Follow(_upperPower, upper);
        _lowerSlow = _lowerSlow <= 0.0 ? lower : (SlopeSmoothing * _lowerSlow) + ((1.0 - SlopeSmoothing) * lower);
        _upperSlow = _upperSlow <= 0.0 ? upper : (SlopeSmoothing * _upperSlow) + ((1.0 - SlopeSmoothing) * upper);
        if (_upperPower <= 1e-24 || _lowerPower <= 1e-24 || _upperSlow <= 1e-24 || _lowerSlow <= 1e-24)
        {
            _addedDb = double.NegativeInfinity;
            return;
        }

        // Its level and slope, continued above the top: the power a bin there should have.
        double slope = Math.Clamp(
            (10.0 * Math.Log10(_upperSlow / _lowerSlow) / Math.Log2(_upperCentreHz / _lowerCentreHz)) - SlopeSteepeningDb,
            SteepestSlopeDb,
            ShallowestSlopeDb);
        double perOctave = slope / (10.0 * Math.Log10(2.0));
        for (int b = _fillStart; b <= _fillEnd; b++)
        {
            _density[b] = _upperPower * Math.Pow(b * _binHz / _upperCentreHz, perOctave);
            _existing[b] = _lowLeakScale * ((_lRe[b] * _lRe[b]) + (_lIm[b] * _lIm[b]));
        }

        // A bin where the music already comes within 6 dB of the line is left as it is, judged five bins at a time so
        // that a bin's own fluctuation does not decide. The harmonics are for what is missing: the line can run a few
        // decibels over the music's own band just under a codec's edge, which filled to it came out 1 to 3 dB louder.
        for (int b = _fillStart; b <= _fillEnd; b++)
        {
            int from = Math.Max(_fillStart, b - 2);
            int to = Math.Min(_fillEnd, b + 2);
            double sum = 0.0;
            for (int k = from; k <= to; k++)
            {
                sum += _existing[k];
            }

            _weight[b] = sum / (to - from + 1) >= 0.25 * _density[b] ? 0.0 : _ramp[b];
        }

        // Everything the curve made, at every frequency the oversampled signal holds: what a band of harmonics is
        // weighed against before it is trusted.
        double harmonicAll = 0.0;
        for (int b = 0; b <= N2 / 2; b++)
        {
            harmonicAll += (_eRe[b] * _eRe[b]) + (_eIm[b] * _eIm[b]);
        }

        // Each sixth of an octave: what is missing under the line, and what the harmonics have there, each bin weighed
        // by how far into the ramp it is. A band the harmonics reach only 80 dB under everything the curve made is left
        // alone: the drive's image in the interpolator, 90 dB down, meets the drive there and nothing else does. A floor
        // at 60 dB cut the top of the band off in some frames and not in others.
        int bands = _bandCentre.Length;
        double floor = harmonicAll * 1e-8;
        double added = 0.0;
        for (int band = 0; band < bands; band++)
        {
            double target = 0.0;
            double there = 0.0;
            double harmonic = 0.0;
            double all = 0.0;
            for (int b = _bandStart[band]; b < _bandStart[band + 1]; b++)
            {
                double e = (_eRe[b] * _eRe[b]) + (_eIm[b] * _eIm[b]);
                all += e;
                target += _weight[b] * _density[b];
                there += _weight[b] * _existing[b];
                harmonic += _weight[b] * e;
            }

            double missing = Math.Max(0.0, target - there);
            _bandTarget[band] = all < floor ? 0.0 : missing;
            _bandHarmonic[band] = harmonic;
            _logGain[band] = _bandTarget[band] > 0.0 && harmonic > 0.0 ? 0.5 * Math.Log(_bandTarget[band] / harmonic) : double.NegativeInfinity;
            added += _bandTarget[band];
        }

        if (added <= 0.0)
        {
            _addedDb = double.NegativeInfinity;
            return;
        }

        double drive = (_lowerPower * (_lowerTo - _lowerFrom + 1)) + (_upperPower * (_upperTo - _lowerTo));
        if (added > MostAdded * drive)
        {
            double scale = MostAdded * drive / added;
            for (int band = 0; band < bands; band++)
            {
                _bandTarget[band] *= scale;
                if (_bandTarget[band] > 0.0 && _bandHarmonic[band] > 0.0)
                {
                    _logGain[band] = 0.5 * Math.Log(_bandTarget[band] / _bandHarmonic[band]);
                }
            }

            added = MostAdded * drive;
        }

        // Gains between band centres by their logarithms, so a band edge is not a step, held flat beyond the first and
        // last centre; then each band brought to exactly what it is missing.
        for (int b = _fillStart; b <= _fillEnd; b++)
        {
            _gain[b] = Math.Exp(Interpolate(b));
        }

        for (int band = 0; band < bands; band++)
        {
            int start = Math.Max(_fillStart, _bandStart[band]);
            int end = Math.Min(_fillEnd + 1, _bandStart[band + 1]);
            if (end <= start)
            {
                continue;
            }

            if (_bandTarget[band] <= 0.0)
            {
                Array.Clear(_gain, start, end - start);
                continue;
            }

            double made = 0.0;
            for (int b = start; b < end; b++)
            {
                made += _weight[b] * _gain[b] * _gain[b] * ((_eRe[b] * _eRe[b]) + (_eIm[b] * _eIm[b]));
            }

            if (made > 0.0)
            {
                double correction = Math.Sqrt(_bandTarget[band] / made);
                for (int b = start; b < end; b++)
                {
                    _gain[b] *= correction;
                }
            }
        }

        for (int b = _fillStart; b <= _fillEnd; b++)
        {
            double g = Math.Sqrt(_weight[b]) * _gain[b];
            if (g > 0.0)
            {
                _xRe[b] += g * _eRe[b];
                _xIm[b] += g * _eIm[b];
            }
        }

        _addedDb = 10.0 * Math.Log10(added / drive);
    }

    private double Interpolate(int b)
    {
        int bands = _bandCentre.Length;
        double at = b;
        int from;
        int to;
        if (at <= _bandCentre[0])
        {
            from = to = 0;
        }
        else if (at >= _bandCentre[bands - 1])
        {
            from = to = bands - 1;
        }
        else
        {
            from = 0;
            while (from < bands - 2 && _bandCentre[from + 1] < at)
            {
                from++;
            }

            to = from + 1;
        }

        double a = _logGain[from];
        double c = _logGain[to];
        if (double.IsNegativeInfinity(a) || double.IsNegativeInfinity(c))
        {
            // A band with nothing to add keeps its neighbour from reaching into it; the other side is used alone.
            return double.IsNegativeInfinity(a) ? c : a;
        }

        double t = from == to ? 0.0 : (at - _bandCentre[from]) / (_bandCentre[to] - _bandCentre[from]);
        return a + (t * (c - a));
    }

    private double LowLeakPower(int from, int to)
    {
        double sum = 0.0;
        for (int b = from; b <= to; b++)
        {
            sum += (_lRe[b] * _lRe[b]) + (_lIm[b] * _lIm[b]);
        }

        return sum * _lowLeakScale;
    }

    private static double Follow(double level, double now) =>
        level <= 0.0 ? now : (LevelSmoothing * level) + ((1.0 - LevelSmoothing) * now);

    /// <summary>Takes up to the rest of the current hop.</summary>
    private void Append(ReadOnlySpan<double> samples)
    {
        if (_hopFill == 0 && _fill == _input.Length)
        {
            // A hop's room at the end for the hop about to arrive.
            _input.AsSpan(H).CopyTo(_input);
            _fill -= H;
        }

        samples.CopyTo(_input.AsSpan(_fill));
        _fill += samples.Length;
        _hopFill += samples.Length;
    }

    /// <summary>Adds the synthesised frame; the first H samples of the sum are then final.</summary>
    private void OverlapAdd()
    {
        // Hann analysis and synthesis at a quarter overlap sum to 1.5 everywhere.
        for (int i = 0; i < N; i++)
        {
            _ola[i] += _frame[i] * _window[i] * (1.0 / 1.5);
        }

        // The first N / H − 1 frames finish only time before the start.
        if (++_frames >= N / H)
        {
            int at = (_readyRead + _readyCount) % N;
            int first = Math.Min(H, N - at);
            _ola.AsSpan(0, first).CopyTo(_ready.AsSpan(at));
            _ola.AsSpan(first, H - first).CopyTo(_ready);
            _readyCount += H;
        }

        _ola.AsSpan(H).CopyTo(_ola);
        Array.Clear(_ola, N - H, H);
    }

    /// <summary>Hands out the next finished samples, in order; what is not finished yet comes out as silence.</summary>
    private void Take(Span<double> output)
    {
        int count = Math.Min(output.Length, _readyCount);
        int first = Math.Min(count, N - _readyRead);
        _ready.AsSpan(_readyRead, first).CopyTo(output);
        _ready.AsSpan(0, count - first).CopyTo(output[first..]);
        output[count..].Clear();
        _readyRead = (_readyRead + count) % N;
        _readyCount -= count;
    }
}
