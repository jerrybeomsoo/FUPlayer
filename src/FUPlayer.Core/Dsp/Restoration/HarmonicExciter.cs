using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FUPlayer.Core.Dsp.Analysis;
using FUPlayer.Core.Dsp.Design;
using FUPlayer.Core.Dsp.Numerics;

namespace FUPlayer.Core.Dsp.Restoration;

/// <summary>
/// Writes the music's own harmonics into its top octave and above the top of its spectrum. It runs at the source rate,
/// or at twice it when the output has room above the source's Nyquist frequency, and then reaches up to 44.1 kHz for a
/// 44.1 kHz source and 48 for 48.
///
/// The band from 7 kHz up to the top of the music (a codec's edge, or 0.45 of the source rate for a full-band source)
/// goes through a curve of the second and third degree at twice the rate the exciter runs at. The sum of two partials
/// of one note is another partial of it, so what comes out lies on the notes' own series, and at the doubled rate
/// nothing folds back into the band that is used. The result is analysed at that rate, and only its bins below the
/// exciter's own Nyquist frequency are used.
///
/// Where they are written, measured from the music frame by frame:
/// <list type="bullet">
/// <item>From <see cref="StartHz"/>, three quarters of the source's edge (12 to 18 kHz), they fade in under the music,
/// evenly in decibels from <see cref="UnderAtStartDb"/> to <see cref="UnderAtEdgeDb"/> under it at the edge, and stay
/// that far under whatever lies above: the restorer's band, the upscaler's, or the music's own.</item>
/// <item>Above the top of the music they fill what is missing under a line: the drive band's level continued along its
/// own slope made 12 dB an octave steeper, which is where hi-res masters were found to lie. The line rises in across
/// a codec's roll-off, from where its fall begins to its wall, and a bin the music half fills gets the other half.</item>
/// </list>
/// The edges the exciter is given glide rather than jump, so a new estimate moves the band over a fraction of a second.
/// Until it has an edge the stage is a plain delay.
/// </summary>
public sealed class HarmonicExciter
{
    /// <summary>The lowest frequency that drives the harmonics, or half where they start if that is lower.</summary>
    public const double DriveFromHz = 7_000.0;

    /// <summary>
    /// The top after the neural restorer, as a fraction of the source rate: just under Nyquist, which the restorer
    /// fills to. Starting at 0.45, as for a full-band source, filled the restorer's own roll-off in its last kilohertz
    /// and put the band from the codec's edge to 0.47 of the rate 1.2 dB over the masters, where the restorer alone
    /// was 0.9 dB under; from here that band is the restorer's, with the harmonics only under it.
    /// </summary>
    public const double AfterRestorerTop = 0.49;

    /// <summary>Where the harmonics start, as a fraction of the source's edge; held between 12 and 18 kHz.</summary>
    public const double StartFraction = 0.75;

    public const double LowestStartHz = 12_000.0;

    public const double HighestStartHz = 18_000.0;

    /// <summary>
    /// How far under the music the harmonics are written where they start, and from the source's edge up. The fade
    /// between is even in decibels, so on a spectrogram it rises out of the floor rather than stepping in: a fade even
    /// in power reached 26 dB under the music within a twelfth of an octave and read as a wall. At the edge they add
    /// 1 dB to the music's level, 0.2 dB a fifth of the fade under it, and nothing to speak of lower; the held-out
    /// coded stretches are within 0.1 dB of their masters under the edge, so what is written there is an effect rather
    /// than a repair, and kept small. Above the edge, where the music is missing, the line decides instead.
    /// </summary>
    public const double UnderAtStartDb = -50.0;

    public const double UnderAtEdgeDb = -6.0;

    /// <summary>The harmonics are made at twice the rate the exciter runs at.</summary>
    private const int Oversampling = 2;

    /// <summary>The drive stops this far under the top, clear of a codec's own roll-off.</summary>
    private const double DriveGuardHz = 300.0;

    /// <summary>
    /// Width of the drive band-pass's edges, and how far it stops what lies outside: bass let through 60 dB down
    /// only meets the drive in sums and differences that stay inside the drive band or under it.
    /// </summary>
    private const double DriveTransitionHz = 1_000.0;

    private const double DriveStopbandDb = 60.0;

    /// <summary>
    /// At the source rate the drive is held under this fraction of it, so the interpolator in front of the curve has a
    /// transition band to work in: its image then starts at 0.55.
    /// </summary>
    private const double HighestDriveAtSourceRate = 0.45;

    /// <summary>The first part of the fade, as a fraction of it, over which it also rises out of nothing.</summary>
    private const double FadeTaper = 0.2;

    /// <summary>
    /// Above the top the music's own slope falls this much faster an octave. Measured against 40 stretches of hi-res
    /// masters from different albums, brought to CD rate and excited: continuing the drive band's slope as it was put
    /// the band above the source's Nyquist frequency 8 to 15 dB over the masters', and 12 dB an octave steeper puts it
    /// 0.5, 0.6 and 1.5 dB over them in the three bands up to 0.95 of the source rate, the spread from song to song 10
    /// to 15 dB. Against their long-term spectra it is 0.4 dB over them in the first quarter of an octave above
    /// Nyquist, and no line bent to meet the music at the edge came closer. Between a codec's edge and Nyquist, on 27
    /// held-out coded stretches, it comes out 1.4 dB over the masters.
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

    /// <summary>How long a new edge takes to be most of the way there (1 − 1/e of it).</summary>
    private const double GlideSeconds = 0.4;

    /// <summary>The band is laid out again once the gliding edge or top has moved this much, as a ratio.</summary>
    private const double RedesignRatio = 0.005;

    private const int MaxBands = 64;

    private readonly int _rate;
    private readonly int _sourceRate;
    private readonly bool _twice;
    private readonly int _n;
    private readonly int _h;
    private readonly int _bins;
    private readonly int _n2;
    private readonly int _h2;
    private readonly double _binHz;
    private readonly double _glide;
    private readonly RealFftPlan _plan;
    private readonly RealFftPlan _plan2;
    private readonly double[] _window;
    private readonly double[] _window2;

    /// <summary>
    /// Blackman-Harris, for measuring levels. Through the Hann window a loud band leaks a dozen bins into a quiet one
    /// beside it and passes for level there; this one's sidelobes are 92 dB down.
    /// </summary>
    private readonly double[] _lowLeak;

    /// <summary>Noise's power through the Hann window over its power through <see cref="_lowLeak"/>.</summary>
    private readonly double _lowLeakScale;

    /// <summary>The interpolator's two phases, each reversed so an input window runs forward against it.</summary>
    private readonly double[][] _phases;

    private readonly int _interpolatorTaps;
    private readonly int _interpolatorHistory;
    private readonly int _bandPassTaps;

    private double[] _bandPass = [];
    private readonly int[] _bandStart = new int[MaxBands + 1];
    private readonly double[] _bandCentre = new double[MaxBands];
    private int _bands;
    private readonly double[] _fillRamp;
    private readonly double[] _underMusic;
    private readonly double[] _density;
    private readonly double[] _existing;
    private readonly double[] _want;
    private readonly double[] _weight;
    private int _writeFrom;
    private int _writeTo;
    private int _lowerFrom;
    private int _lowerTo;
    private int _upperTo;
    private double _lowerCentreHz;
    private double _upperCentreHz;

    // Where the band is, and where it is heading.
    private double _edgeTarget;
    private double _topTarget;
    private double _wallTarget;
    private double _designedEdge;
    private double _designedTop;
    private double _designedWall;

    // Scratch for one frame.
    private readonly double[] _frame;
    private readonly double[] _frameLowLeak;
    private readonly double[] _frame2;
    private readonly double[] _xRe;
    private readonly double[] _xIm;
    private readonly double[] _lRe;
    private readonly double[] _lIm;
    private readonly double[] _eRe;
    private readonly double[] _eIm;
    private readonly double[] _gain;
    private readonly double[] _bandTarget = new double[MaxBands];
    private readonly double[] _bandHarmonic = new double[MaxBands];
    private readonly double[] _logGain = new double[MaxBands];

    // The stream.
    private readonly double[] _input;
    private readonly double[] _drive;
    private readonly double[] _driveHop;
    private readonly double[] _oversampledHop;
    private readonly double[] _harmonics;
    private readonly double[] _ola;
    private readonly double[] _ready;
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

    /// <param name="rate">
    /// The rate the exciter runs at: the source's, or twice it (88.2 or 96 kHz for 44.1 or 48) so the harmonics can
    /// reach past the source's Nyquist frequency.
    /// </param>
    /// <param name="sourceRate">The source's rate, 16 to 48 kHz.</param>
    public HarmonicExciter(int rate, int sourceRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceRate, 16_000);
        if (rate != sourceRate && rate != 2 * sourceRate)
        {
            throw new ArgumentOutOfRangeException(nameof(rate), rate, "The exciter runs at the source rate or twice it.");
        }

        _rate = rate;
        _sourceRate = sourceRate;
        _twice = rate == 2 * sourceRate;

        // The transform spans the same time either way, about 23 ms, and its bins are as wide.
        _n = _twice ? 2048 : 1024;
        _h = _n / 4;
        _bins = (_n / 2) + 1;
        _n2 = _n * Oversampling;
        _h2 = _h * Oversampling;
        _binHz = (double)rate / _n;
        _glide = 1.0 - Math.Exp(-_h / (GlideSeconds * rate));
        _plan = new RealFftPlan(_n);
        _plan2 = new RealFftPlan(_n2);

        _window = new double[_n];
        _window2 = new double[_n2];
        _lowLeak = new double[_n];
        double hannPower = 0.0;
        double lowLeakPower = 0.0;
        for (int i = 0; i < _n; i++)
        {
            double t = 2.0 * Math.PI * i / _n;
            _window[i] = 0.5 - (0.5 * Math.Cos(t));
            _lowLeak[i] = 0.35875 - (0.48829 * Math.Cos(t)) + (0.14128 * Math.Cos(2.0 * t)) - (0.01168 * Math.Cos(3.0 * t));
            hannPower += _window[i] * _window[i];
            lowLeakPower += _lowLeak[i] * _lowLeak[i];
        }

        _lowLeakScale = hannPower / lowLeakPower;
        for (int i = 0; i < _n2; i++)
        {
            _window2[i] = 0.5 - (0.5 * Math.Cos(2.0 * Math.PI * i / _n2));
        }

        // Twofold. At twice the source rate the drive lies below a quarter of the rate, so its image starts at three
        // quarters and the transition is wide; at the source rate it reaches 0.45 of it and the image starts at 0.55.
        // Given in fractions of the doubled rate, 4k + 1 taps so the delay is a whole input sample.
        LowpassSpec interpolator = _twice
            ? new LowpassSpec(0.125, 0.375, 90.0)
            : new LowpassSpec(HighestDriveAtSourceRate / 2.0, (1.0 - HighestDriveAtSourceRate) / 2.0, 90.0);
        _interpolatorTaps = _twice ? 41 : (((FirDesign.EstimateLength(interpolator) + 2) / 4 * 4) + 1);
        double[] prototype = FirDesign.Lowpass(interpolator, Oversampling, _interpolatorTaps);
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

        // The band-pass is redesigned as the edge moves but keeps its length, so the delay never does.
        _bandPassTaps = FirDesign.EstimateLength(new LowpassSpec(0.25, 0.25 + (DriveTransitionHz / rate), DriveStopbandDb)) | 1;

        // The drive and the harmonics made from it trail the signal by the band-pass's delay and the interpolator's;
        // the signal is held back as much, so the two meet in one frame.
        Delay = ((_bandPassTaps - 1) / 2) + ((_interpolatorTaps - 1) / 2 / Oversampling);
        Latency = Delay + _n - 1;

        _fillRamp = new double[_bins];
        _underMusic = new double[_bins];
        _density = new double[_bins];
        _existing = new double[_bins];
        _want = new double[_bins];
        _weight = new double[_bins];
        _gain = new double[_bins];
        _frame = new double[_n];
        _frameLowLeak = new double[_n];
        _frame2 = new double[_n2];
        _xRe = new double[_n];
        _xIm = new double[_n];
        _lRe = new double[_n];
        _lIm = new double[_n];
        _eRe = new double[_n2];
        _eIm = new double[_n2];
        _driveHop = new double[_h];
        _oversampledHop = new double[_h2];
        _harmonics = new double[_n2];
        _ola = new double[_n];
        _ready = new double[_n];
        _input = new double[Math.Max(_n + Delay, _h + _bandPassTaps - 1)];
        _drive = new double[_interpolatorHistory + _h];
        WarmUp();
    }

    /// <summary>The rate the exciter runs at.</summary>
    public int Rate => _rate;

    /// <summary>The source's rate.</summary>
    public int SourceRate => _sourceRate;

    /// <summary>Points of the mixing transform, at <see cref="Rate"/>: 1,024 at the source rate, 2,048 at twice it.</summary>
    public int Size => _n;

    public int Hop => _h;

    /// <summary>Samples from a sample going in to it coming out, at <see cref="Rate"/>.</summary>
    public int Latency { get; }

    /// <summary>Where the source's own music ends, as the exciter has it just now; 0 while it has not been told.</summary>
    public double EdgeHz { get; private set; }

    /// <summary>
    /// Where the signal reaching the exciter begins to end: the source's edge, or just under Nyquist after the
    /// restorer. The line the harmonics fill up to rises in from here.
    /// </summary>
    public double TopHz { get; private set; }

    /// <summary>
    /// Where the line has risen all the way: a codec's wall, where its band is gone, or the source's Nyquist
    /// frequency. On the held-out coded stretches the band between where a codec's fall begins and its wall was
    /// within 0.2 dB of the masters, and a line filling it from where the fall begins put it 4 dB over.
    /// </summary>
    public double WallHz { get; private set; }

    /// <summary>Where the harmonics start: <see cref="StartFor"/> the edge.</summary>
    public double StartHz { get; private set; }

    /// <summary>The lowest frequency driving the harmonics: 7 kHz, or half where they start when that is lower.</summary>
    public double DriveLowHz { get; private set; }

    /// <summary>
    /// How loud the harmonics written just now are against the band that drives them, in decibels; minus infinity
    /// while nothing is written.
    /// </summary>
    public double AddedDb => _addedDb;

    /// <summary>Samples the signal is held back so the harmonics made from it arrive with it.</summary>
    internal int Delay { get; }

    /// <summary>
    /// The source's edge and wall from the codec detector: for a band-limited source where its spectrum begins to fall
    /// and its cutoff, and for one that runs to the top of its band, or while the detector does not know yet, 0.45 of
    /// the source rate and its Nyquist frequency.
    /// </summary>
    /// <remarks>
    /// The fill starts where the fall begins and reaches the line at the cutoff, where the level has fallen 6 dB.
    /// Vorbis leaves a residue 16 dB down for several hundred hertz above its band, and averaged over a minute of one
    /// song its cutoff drifted from 17.1 to 18.1 kHz while the band itself ended at 17.2: a fill that only started at
    /// the cutoff left a dark stripe between the music and the harmonics.
    /// </remarks>
    public static (double Edge, double Wall) EdgesFor(BandwidthEstimate estimate, int sourceRate)
    {
        if (estimate.Verdict != BandwidthVerdict.BandLimited)
        {
            return (0.45 * sourceRate, 0.5 * sourceRate);
        }

        double edge = Math.Min(estimate.TransitionHz > 0.0 ? estimate.TransitionHz : estimate.CutoffHz, 0.45 * sourceRate);
        return (edge, Math.Clamp(estimate.CutoffHz, edge, 0.5 * sourceRate));
    }

    /// <summary>
    /// Where the harmonics start for an edge: three quarters of it, which is 14.4 kHz for AAC at 256 kbit/s and 15 for a
    /// full-band CD, held between 12 and 18 kHz and a twentieth of the edge under it.
    /// </summary>
    public static double StartFor(double edgeHz) =>
        Math.Min(Math.Clamp(StartFraction * edgeHz, LowestStartHz, HighestStartHz), 0.95 * edgeHz);

    /// <summary>
    /// Where the source's music begins to end; where the signal reaching the exciter does, the same or higher after the
    /// restorer; and where it has ended, a codec's wall or the source's Nyquist frequency. New edges are glided to
    /// over a fraction of a second; the first are taken at once. An edge of 0, or one below 5 kHz or above the source's
    /// Nyquist frequency, leaves the signal alone.
    /// </summary>
    public void SetEdges(double edgeHz, double topHz, double wallHz)
    {
        double nyquist = 0.5 * _sourceRate;
        bool valid = edgeHz >= 5_000.0 && edgeHz <= nyquist;
        double edge = valid ? edgeHz : 0.0;
        double top = valid ? Math.Clamp(topHz, edge, nyquist) : 0.0;
        double wall = valid ? Math.Clamp(wallHz, top, nyquist) : 0.0;
        if (edge == _edgeTarget && top == _topTarget && wall == _wallTarget)
        {
            return;
        }

        _edgeTarget = edge;
        _topTarget = top;
        _wallTarget = wall;
        if (edge == 0.0)
        {
            EdgeHz = TopHz = WallHz = StartHz = 0.0;
            _bandPass = [];
            _addedDb = double.NegativeInfinity;
        }
        else if (EdgeHz == 0.0)
        {
            EdgeHz = edge;
            TopHz = top;
            WallHz = wall;
            Design();
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
    /// Every path once, on two tones under an edge, before there is a stream to keep up with; then back to where a new
    /// exciter starts. The first frames that write harmonics are the ones that compile what they call.
    /// </summary>
    private void WarmUp()
    {
        Reset();
        SetEdges(0.4 * _sourceRate, 0.4 * _sourceRate, 0.42 * _sourceRate);
        var tone = new double[8 * _h];
        double step = (double)_sourceRate / _rate;
        for (int i = 0; i < tone.Length; i++)
        {
            tone[i] = (0.1 * Math.Sin(2.0 * Math.PI * 0.25 * step * i)) + (0.05 * Math.Sin(2.0 * Math.PI * 0.35 * step * i));
        }

        Process(tone);
        SetEdges(0.0, 0.0, 0.0);
        Reset();
    }

    /// <summary>
    /// Processes one channel in place at <see cref="Rate"/>: each sample written is the input from
    /// <see cref="Latency"/> samples earlier, with the harmonics added.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Process(Span<double> signal)
    {
        int done = 0;
        while (done < signal.Length)
        {
            int take = Math.Min(signal.Length - done, _h - _hopFill);
            Span<double> part = signal.Slice(done, take);
            Append(part);
            if (_hopFill == _h)
            {
                _hopFill = 0;
                Frame();
            }

            // The first N - 1 samples out come from before the start: silence. Every later one is finished by now,
            // since a sample is complete once the frame ending N - 1 samples after it has been added.
            int silent = (int)Math.Clamp((_n - 1) - _samplesIn, 0, take);
            part[..silent].Clear();
            Take(part[silent..]);
            _samplesIn += take;
            done += take;
        }
    }

    /// <summary>Moves the edges a hop's worth towards where they are heading, and lays the band out again once they have gone far enough.</summary>
    private void Glide()
    {
        if (_edgeTarget == 0.0 || (EdgeHz == _edgeTarget && TopHz == _topTarget && WallHz == _wallTarget))
        {
            return;
        }

        EdgeHz = Towards(EdgeHz, _edgeTarget);
        TopHz = Math.Max(EdgeHz, Towards(TopHz, _topTarget));
        WallHz = Math.Max(TopHz, Towards(WallHz, _wallTarget));
        bool arrived = EdgeHz == _edgeTarget && TopHz == _topTarget && WallHz == _wallTarget;
        if (arrived || Moved(EdgeHz, _designedEdge) || Moved(TopHz, _designedTop) || Moved(WallHz, _designedWall))
        {
            Design();
        }

        static bool Moved(double now, double then) => Math.Abs(Math.Log(now / then)) > RedesignRatio;

        double Towards(double from, double to)
        {
            double step = _glide * Math.Log(to / from);
            return Math.Abs(Math.Log(to / from)) < 2e-3 ? to : from * Math.Exp(step);
        }
    }

    private void Design()
    {
        double edge = EdgeHz;
        double top = TopHz;
        double wall = WallHz;
        _designedEdge = edge;
        _designedTop = top;
        _designedWall = wall;
        double start = StartFor(edge);
        StartHz = start;

        // The drive: from 7 kHz, or from half where the harmonics start so that their second degree reaches down to
        // it, up to the guard under the top.
        double cap = _twice ? (0.5 * _sourceRate) - DriveGuardHz : HighestDriveAtSourceRate * _rate;
        double high = Math.Min(top - DriveGuardHz, cap);
        double low = Math.Min(DriveFromHz, Math.Min(start, top) / 2.0);
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

        // Written from where the harmonics start to just under Nyquist: under the music in a fade even in decibels on a
        // logarithmic scale of frequency, rising out of nothing over its first part and reaching its full depth at the
        // edge; and where the music is missing above the top, up to the line, which rises in as a raised cosine on the
        // same scale until the wall.
        _writeFrom = Math.Max(1, (int)Math.Ceiling(start / _binHz));
        _writeTo = _bins - 2;
        for (int b = 0; b < _bins; b++)
        {
            double hz = b * _binHz;
            double x = Math.Log(hz / start) / Math.Log(edge / start);
            _underMusic[b] = b < _writeFrom || b > _writeTo ? 0.0
                : hz >= edge ? Math.Pow(10.0, UnderAtEdgeDb / 10.0)
                : Math.Pow(10.0, (UnderAtStartDb + (x * (UnderAtEdgeDb - UnderAtStartDb))) / 10.0) * RaisedCosine(x / FadeTaper);
            _fillRamp[b] = b < _writeFrom || b > _writeTo || hz < top ? 0.0
                : hz >= wall ? 1.0
                : RaisedCosine(Math.Log(hz / top) / Math.Log(wall / top));
        }

        // Sixths of an octave from the start, the last one short.
        double nyquist = _writeTo * _binHz;
        int bands = 0;
        for (double from = _writeFrom * _binHz; from < nyquist - _binHz && bands < MaxBands - 1; from *= Math.Pow(2.0, 1.0 / 6.0))
        {
            double to = Math.Min(nyquist, from * Math.Pow(2.0, 1.0 / 6.0));
            _bandStart[bands] = Math.Max(_writeFrom, (int)Math.Round(from / _binHz));
            _bandCentre[bands] = Math.Sqrt(from * to) / _binHz;
            bands++;
        }

        _bandStart[bands] = _writeTo + 1;
        _bands = bands;

        LowpassSpec Spec(double pass, double stop) => new(pass / _rate, stop / _rate, DriveStopbandDb);
    }

    private static double RaisedCosine(double x) => x <= 0.0 ? 0.0 : x >= 1.0 ? 1.0 : 0.5 - (0.5 * Math.Cos(Math.PI * x));

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Frame()
    {
        Glide();

        // The signal's frame: the last N samples of it, held back by the drive's delay.
        _input.AsSpan(_fill - Delay - _n, _n).CopyTo(_frame);
        bool exciting = EdgeHz > 0.0 && _bandPass.Length == _bandPassTaps;
        if (exciting)
        {
            for (int i = 0; i < _n; i++)
            {
                _frameLowLeak[i] = _frame[i] * _lowLeak[i];
            }

            _plan.Forward(_frameLowLeak, _lRe, _lIm);
        }

        for (int i = 0; i < _n; i++)
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
        ReadOnlySpan<double> history = _input.AsSpan(_fill - (_h + _bandPassTaps - 1), _h + _bandPassTaps - 1);
        FirKernel.Convolve(ref MemoryMarshal.GetArrayDataReference(_bandPass), (nuint)_bandPass.Length,
            ref MemoryMarshal.GetReference(history), 1, ref MemoryMarshal.GetArrayDataReference(_driveHop), 1, _h);

        // Twofold: output 2n + p is phase p against the drive up to n.
        _drive.AsSpan(_h, _interpolatorHistory).CopyTo(_drive);
        _driveHop.CopyTo(_drive.AsSpan(_interpolatorHistory));
        for (int p = 0; p < Oversampling; p++)
        {
            FirKernel.Convolve(ref MemoryMarshal.GetArrayDataReference(_phases[p]), (nuint)_phases[p].Length,
                ref MemoryMarshal.GetArrayDataReference(_drive), 1, ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_oversampledHop), p),
                Oversampling, _h);
        }

        // Second and third degree. The cube is weighed against a slow measure of the drive's level so the mixture does
        // not change with how loud the music is.
        double power = SimdMath.SumOfSquares(_oversampledHop) / _h2;
        _driveLevel = _driveLevel <= 0.0 ? Math.Sqrt(power) : (0.9 * _driveLevel) + (0.1 * Math.Sqrt(power));
        double cube = _driveLevel > 1e-12 ? 0.5 / _driveLevel : 0.0;
        for (int i = 0; i < _h2; i++)
        {
            double u = _oversampledHop[i];
            _oversampledHop[i] = u * u * (1.0 + (cube * u));
        }

        _harmonics.AsSpan(_h2).CopyTo(_harmonics);
        _oversampledHop.CopyTo(_harmonics.AsSpan(_n2 - _h2));
        for (int i = 0; i < _n2; i++)
        {
            _frame2[i] = _harmonics[i] * _window2[i];
        }

        _plan2.Forward(_frame2, _eRe, _eIm);
    }

    /// <summary>Adds the harmonics: under the music from where they start, and up to the line where it is missing.</summary>
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
        for (int b = _writeFrom; b <= _writeTo; b++)
        {
            _density[b] = _upperPower * Math.Pow(b * _binHz / _upperCentreHz, perOctave);
            _existing[b] = _lowLeakScale * ((_lRe[b] * _lRe[b]) + (_lIm[b] * _lIm[b]));
        }

        // What each bin should gain, judged on the music five bins at a time so that a bin's own fluctuation does not
        // decide: the fade under the music, or what the music falls short of the line above the top, whichever is more.
        // The weight is that against the larger of the two levels, and spreads the harmonics' own fine structure over the
        // bins in that proportion.
        for (int b = _writeFrom; b <= _writeTo; b++)
        {
            int from = Math.Max(_writeFrom, b - 2);
            int to = Math.Min(_writeTo, b + 2);
            double sum = 0.0;
            for (int k = from; k <= to; k++)
            {
                sum += _existing[k];
            }

            double there = sum / (to - from + 1);
            double line = _density[b];
            double want = Math.Max(_underMusic[b] * there, Math.Max(0.0, (_fillRamp[b] * line) - there));
            _want[b] = want;
            _weight[b] = want > 0.0 ? Math.Min(1.0, want / Math.Max(line, there)) : 0.0;
        }

        // Everything the curve made, at every frequency the oversampled signal holds: what a band of harmonics is
        // weighed against before it is trusted.
        double harmonicAll = 0.0;
        for (int b = 0; b <= _n2 / 2; b++)
        {
            harmonicAll += (_eRe[b] * _eRe[b]) + (_eIm[b] * _eIm[b]);
        }

        // Each sixth of an octave: what it should gain, and what the harmonics have there, each bin weighed. A band the
        // harmonics reach only 80 dB under everything the curve made is left alone: the drive's image in the
        // interpolator, 90 dB down, meets the drive there and nothing else does. A floor at 60 dB cut the top of the band
        // off in some frames and not in others.
        int bands = _bands;
        double floor = harmonicAll * 1e-8;
        double added = 0.0;
        for (int band = 0; band < bands; band++)
        {
            // The curve's third degree holds a copy of the drive itself, in phase with it, and some of its sums land on
            // the music's own partials. Written under the music, that part raised its level as a gain would, 1.3 dB where
            // a tenth of its power was meant; so what of the harmonics the music in the band predicts in this frame, as a
            // complex least-squares fit, is taken out first. Above the edge, where the music is missing, it is nothing.
            double hx = 0.0;
            double hy = 0.0;
            double xx = 0.0;
            for (int b = _bandStart[band]; b < _bandStart[band + 1]; b++)
            {
                hx += (_eRe[b] * _xRe[b]) + (_eIm[b] * _xIm[b]);
                hy += (_eIm[b] * _xRe[b]) - (_eRe[b] * _xIm[b]);
                xx += (_xRe[b] * _xRe[b]) + (_xIm[b] * _xIm[b]);
            }

            if (xx > 1e-30)
            {
                double cr = hx / xx;
                double ci = hy / xx;
                for (int b = _bandStart[band]; b < _bandStart[band + 1]; b++)
                {
                    double re = _eRe[b] - ((cr * _xRe[b]) - (ci * _xIm[b]));
                    _eIm[b] -= (cr * _xIm[b]) + (ci * _xRe[b]);
                    _eRe[b] = re;
                }
            }

            double target = 0.0;
            double harmonic = 0.0;
            double all = 0.0;
            for (int b = _bandStart[band]; b < _bandStart[band + 1]; b++)
            {
                double e = (_eRe[b] * _eRe[b]) + (_eIm[b] * _eIm[b]);
                all += e;
                target += _want[b];
                harmonic += _weight[b] * e;
            }

            _bandTarget[band] = all < floor ? 0.0 : target;
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
        // last centre; then each band brought to exactly what it should gain.
        for (int b = _writeFrom; b <= _writeTo; b++)
        {
            _gain[b] = Math.Exp(Interpolate(b));
        }

        for (int band = 0; band < bands; band++)
        {
            int start = _bandStart[band];
            int end = _bandStart[band + 1];
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

        for (int b = _writeFrom; b <= _writeTo; b++)
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
        int bands = _bands;
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
            _input.AsSpan(_h).CopyTo(_input);
            _fill -= _h;
        }

        samples.CopyTo(_input.AsSpan(_fill));
        _fill += samples.Length;
        _hopFill += samples.Length;
    }

    /// <summary>Adds the synthesised frame; the first H samples of the sum are then final.</summary>
    private void OverlapAdd()
    {
        // Hann analysis and synthesis at a quarter overlap sum to 1.5 everywhere.
        for (int i = 0; i < _n; i++)
        {
            _ola[i] += _frame[i] * _window[i] * (1.0 / 1.5);
        }

        // The first N / H − 1 frames finish only time before the start.
        if (++_frames >= _n / _h)
        {
            int at = (_readyRead + _readyCount) % _n;
            int first = Math.Min(_h, _n - at);
            _ola.AsSpan(0, first).CopyTo(_ready.AsSpan(at));
            _ola.AsSpan(first, _h - first).CopyTo(_ready);
            _readyCount += _h;
        }

        _ola.AsSpan(_h).CopyTo(_ola);
        Array.Clear(_ola, _n - _h, _h);
    }

    /// <summary>Hands out the next finished samples, in order; what is not finished yet comes out as silence.</summary>
    private void Take(Span<double> output)
    {
        int count = Math.Min(output.Length, _readyCount);
        int first = Math.Min(count, _n - _readyRead);
        _ready.AsSpan(_readyRead, first).CopyTo(output);
        _ready.AsSpan(0, count - first).CopyTo(output[first..]);
        output[count..].Clear();
        _readyRead = (_readyRead + count) % _n;
        _readyCount -= count;
    }
}
