using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FUPlayer.Core.Dsp.Analysis;
using FUPlayer.Core.Dsp.Design;
using FUPlayer.Core.Dsp.Numerics;

namespace FUPlayer.Core.Dsp.Restoration;

/// <summary>
/// Puts the music's own harmonics into the band a codec discarded, after the neural restorer has filled it again.
///
/// The restorer writes the band above the codec's edge at about the level the master has there, but as a texture:
/// on held-out songs its fine structure above the edge does not correlate with the master's at all (0.00), where the
/// octave below the edge, which the codec kept, correlates 0.46. A violin's partials stop at the edge. This stage
/// continues them.
///
/// The octave below the edge is taken out of the restored signal, raised to four times the rate and put through a
/// polynomial of the second and third degree. The sum of two partials of one note is another partial of that note, so
/// what comes out lies on the note's own harmonic series, above the edge, bending with its vibrato. A third-degree
/// polynomial reaches three times the highest frequency it is fed, up to 1.38 times the sample rate, which four times
/// the rate holds without folding anything back; at the sample rate itself a square law's sums above Nyquist fold down
/// into the very band being written, as tones that belong to nothing. The oversampled signal is never brought back
/// down: it is analysed at four times the rate with a transform four times as long, whose first quarter of bins are
/// the output's own bins, so no decimation filter can leak.
///
/// What it writes is no louder than what the restorer wrote. In every twelfth of an octave above the edge the
/// harmonics are scaled, frame by frame, to the restorer's level there, read through a window that does not let the
/// louder band below the edge leak into it, and they take over a share of it: twice the share of the octave below the
/// edge that stands in partials rather than in noise, held under 90 %, rising within a few milliseconds and falling
/// back over a tenth of a second. A sustained violin gives most of the band to its harmonics; cymbals, applause and a
/// dense mix leave the restorer's band much as it was. A partial the restorer's output already has above the edge is
/// left as it is. Below the edge nothing is touched, and without an edge the stage passes everything through, only
/// delayed.
///
/// Measured on held-out songs against their masters, on the stretches with harmonics above the edge to reflect: the
/// fine-structure correlation above the edge rose from 0.00 to 0.04 and the share of the master's partials matched
/// within a bin from 0.37 to 0.41, the band's level within 0.2 dB; the others were left as they were. Without the
/// oversampling the gain is about a sixth smaller.
/// </summary>
public sealed class HarmonicExciter
{
    /// <summary>Points of the mixing transform, at the sample rate.</summary>
    public const int FftSize = 1024;

    public const int Hop = FftSize / 4;

    /// <summary>How many times the sample rate the harmonics are made at.</summary>
    public const int Oversampling = 4;

    /// <summary>The share of the band given to harmonics, at most.</summary>
    public const double MaximumMix = 0.9;

    private const int N = FftSize;
    private const int H = Hop;
    private const int Bins = (N / 2) + 1;
    private const int N4 = N * Oversampling;
    private const int H4 = H * Oversampling;

    /// <summary>Taps of the fourfold interpolator: a multiple of eight plus one, so its delay is a whole input sample.</summary>
    private const int InterpolatorTaps = 257;

    /// <summary>The drive stops this far below the edge, clear of the codec's own roll-off.</summary>
    private const double DriveGuardHz = 200.0;

    /// <summary>The mix ramps in over this much above the edge.</summary>
    private const double CrossoverHz = 600.0;

    /// <summary>A bin is a partial when it stands this far above the mean of its neighbours, in dB.</summary>
    private const double PartialDb = 10.0;

    private const int NeighbourBins = 4;

    /// <summary>
    /// Bins either side of a partial the restorer's output already has that are left as they are, and left out of
    /// the band's level: the low-leakage window's main lobe is four bins either side, and the partial can sit up to
    /// half a bin off its peak.
    /// </summary>
    private const int KeepBins = 5;

    private const double Attack = 0.5;
    private const double Release = 0.95;

    private readonly int _sampleRate;
    private readonly double _binHz;
    private readonly RealFftPlan _plan = new(N);
    private readonly RealFftPlan _plan4 = new(N4);
    private readonly double[] _window = new double[N];
    private readonly double[] _window4 = new double[N4];

    /// <summary>
    /// Blackman-Harris, for measuring the restorer's level above the edge. Through the Hann window the band below
    /// the edge, often 20 to 40 dB louder, leaks across it for a dozen bins and passes for level the restorer never
    /// wrote; this one's sidelobes are 92 dB down.
    /// </summary>
    private readonly double[] _lowLeak = new double[N];

    /// <summary>Noise's power through the Hann window over its power through <see cref="_lowLeak"/>.</summary>
    private readonly double _lowLeakScale;

    /// <summary>The interpolator's phases, each reversed so an input window runs forward against it.</summary>
    private readonly double[][] _phases;

    private readonly int _interpolatorHistory;
    private readonly int _bandPassTaps;
    private readonly Channel[] _channels;

    private double[] _bandPass = [];
    private int[] _bandStart = [];
    private double[] _bandCentre = [];
    private readonly double[] _ramp = new double[Bins];
    private int _mixStart;
    private int _driveLow;
    private int _driveHigh;

    // Per call scratch, shared by the channels since they run one after the other.
    private readonly double[] _frame = new double[N];
    private readonly double[] _frameLowLeak = new double[N];
    private readonly double[] _frame4 = new double[N4];
    private readonly double[] _yRe = new double[N4];
    private readonly double[] _yIm = new double[N4];
    private readonly double[] _lRe = new double[N];
    private readonly double[] _lIm = new double[N];
    private readonly double[] _eRe = new double[N4];
    private readonly double[] _eIm = new double[N4];
    private readonly double[] _db = new double[Bins];
    private readonly double[] _gain = new double[Bins];
    private readonly double[] _sorted = new double[Bins];
    private readonly bool[] _keep = new bool[Bins];
    private readonly double[] _bandPower = new double[64];
    private readonly double[] _bandHarmonic = new double[64];
    private readonly double[] _bandHarmonicAll = new double[64];
    private readonly double[] _logGain = new double[64];

    private long _samplesIn;

    public HarmonicExciter(int sampleRate, int channels = 2)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8_000);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        _sampleRate = sampleRate;
        _binHz = (double)sampleRate / N;

        for (int i = 0; i < N; i++)
        {
            _window[i] = 0.5 - (0.5 * Math.Cos(2.0 * Math.PI * i / N));
        }

        for (int i = 0; i < N4; i++)
        {
            _window4[i] = 0.5 - (0.5 * Math.Cos(2.0 * Math.PI * i / N4));
        }

        double hannPower = 0.0;
        double lowLeakPower = 0.0;
        for (int i = 0; i < N; i++)
        {
            double t = 2.0 * Math.PI * i / N;
            _lowLeak[i] = 0.35875 - (0.48829 * Math.Cos(t)) + (0.14128 * Math.Cos(2.0 * t)) - (0.01168 * Math.Cos(3.0 * t));
            hannPower += _window[i] * _window[i];
            lowLeakPower += _lowLeak[i] * _lowLeak[i];
        }

        _lowLeakScale = hannPower / lowLeakPower;

        // Fourfold interpolation: the drive is below 0.46 of the rate, so its first image begins at 0.54 of it.
        double[] prototype = FirDesign.Lowpass(
            new LowpassSpec(0.46 / Oversampling, 0.54 / Oversampling, 90.0), Oversampling, InterpolatorTaps);
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

        // The band-pass is redesigned for each edge but keeps its length, so the delay never moves: its steeper edge,
        // the upper one, takes 600 Hz at 70 dB.
        _bandPassTaps = FirDesign.EstimateLength(new LowpassSpec(0.25, 0.25 + (600.0 / sampleRate), 70.0)) | 1;

        // The drive and the harmonics made from it trail the signal by the band-pass's delay and the interpolator's;
        // the signal is held back by the same amount so the two meet in one frame.
        Delay = ((_bandPassTaps - 1) / 2) + ((InterpolatorTaps - 1) / 2 / Oversampling);
        Latency = Delay + N - 1;

        _channels = Enumerable.Range(0, channels).Select(_ => new Channel(Delay, _bandPassTaps, _interpolatorHistory)).ToArray();
        WarmUp();
    }

    /// <summary>Samples from a sample going in to its excited self coming out.</summary>
    public int Latency { get; }

    /// <summary>The frequency the harmonics are written above, or 0 while there is no codec edge to write above.</summary>
    public double EdgeHz { get; private set; }

    /// <summary>The share of the band above the edge given to harmonics just now, over the channels.</summary>
    public double Mix => _channels.Average(c => c.Mix);

    /// <summary>Samples the signal is held back so the harmonics made from it arrive with it.</summary>
    internal int Delay { get; }

    /// <summary>
    /// The edge to write above, from the codec detector: where a band-limited source's spectrum has fallen 6 dB, or
    /// nothing for a source that runs to the top of its band.
    /// </summary>
    /// <remarks>
    /// Not where the fall begins, which the restorer starts from. An encoder that rolls off gently leaves real
    /// partials in its roll-off, lower but in place, and harmonics made in their stead are a guess where the truth
    /// was still there: on held-out songs with a roll-off 600 Hz or wider, starting at its beginning took the
    /// fine-structure correlation inside it from 0.27 down to 0.19, and starting at the cutoff left it alone and
    /// gained as much above.
    /// </remarks>
    public static double EdgeFor(BandwidthEstimate estimate) =>
        estimate.Verdict == BandwidthVerdict.BandLimited ? estimate.CutoffHz : 0.0;

    /// <summary>
    /// Where the codec's band ends. Zero, or anything below 5 kHz or within a thirteenth of the sample rate of its
    /// Nyquist frequency, leaves the signal alone. Small moves are ignored, as the detector's estimate settles.
    /// </summary>
    public void SetEdge(double hz)
    {
        double edge = hz >= 5_000.0 && hz <= 0.46 * _sampleRate ? hz : 0.0;
        if (edge == 0.0 || EdgeHz == 0.0 ? edge == EdgeHz : Math.Abs(edge - EdgeHz) < EdgeHz * 0.03)
        {
            return;
        }

        EdgeHz = edge;
        if (edge > 0.0)
        {
            Design(edge);
        }
    }

    public void Reset()
    {
        foreach (Channel channel in _channels)
        {
            channel.Reset();
        }

        _samplesIn = 0;
    }

    /// <summary>
    /// Every path once, on a tone with an edge above it, before there is a stream to keep up with; then back to where
    /// a new exciter starts. The edge usually arrives a second into a track, while the output buffer is still filling,
    /// and the first frames that write harmonics are the ones that compile what they call.
    /// </summary>
    private void WarmUp()
    {
        Reset();
        if (_channels.Length == 2)
        {
            SetEdge(0.35 * _sampleRate);
            var left = new double[8 * H];
            var right = new double[8 * H];
            for (int i = 0; i < left.Length; i++)
            {
                left[i] = 0.1 * Math.Sin(2.0 * Math.PI * 0.21 * i);
                right[i] = left[i];
            }

            Process(left, right);
            EdgeHz = 0.0;
            _bandPass = [];
        }

        Reset();
    }

    /// <summary>
    /// Processes the channels in place: each sample written is the input from <see cref="Latency"/> samples earlier,
    /// with harmonics above the edge. A mono pair is two spans over the same samples' copies; both must be as long.
    /// </summary>
    /// <remarks>
    /// Taken a hop at a time rather than a sample at a time, and compiled optimised on its first call: the exciter
    /// starts writing about a second into a track, when the detector has found the edge, and code compiled the quick
    /// way first ran it at a third of its speed for the next twenty blocks, while the output buffer was still filling.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Process(Span<double> left, Span<double> right)
    {
        if (left.Length != right.Length || _channels.Length != 2)
        {
            throw new ArgumentException("Two channels of the same length.", nameof(right));
        }

        Channel first = _channels[0];
        Channel second = _channels[1];
        int done = 0;
        while (done < left.Length)
        {
            int take = Math.Min(left.Length - done, first.HopRoom);
            Span<double> l = left.Slice(done, take);
            Span<double> r = right.Slice(done, take);
            first.Append(l);
            second.Append(r);
            if (first.HopRoom == 0)
            {
                first.EndHop();
                second.EndHop();
                Frame(first);
                Frame(second);
            }

            // The first N - 1 samples out come from before the start: silence. Every later one is finished by now,
            // since a sample is complete once the frame ending N - 1 samples after it has been added.
            int silent = (int)Math.Clamp((N - 1) - _samplesIn, 0, take);
            l[..silent].Clear();
            r[..silent].Clear();
            first.Take(l[silent..]);
            second.Take(r[silent..]);
            _samplesIn += take;
            done += take;
        }
    }

    private void Design(double edge)
    {
        double high = edge - DriveGuardHz;
        double low = edge / 2.0;

        // A band-pass as the difference of two low-passes of the same length and window: one falling to the guard below
        // the edge, one at half the edge, both in 600 Hz.
        double[] upper = FirDesign.Lowpass(Spec(high - 600.0, high), 1.0, _bandPassTaps);
        double[] lower = FirDesign.Lowpass(Spec(low - 600.0, low), 1.0, _bandPassTaps);
        var bandPass = new double[_bandPassTaps];
        for (int k = 0; k < bandPass.Length; k++)
        {
            bandPass[k] = upper[k] - lower[k];
        }

        _bandPass = bandPass;
        _driveLow = (int)Math.Ceiling(low / _binHz);
        _driveHigh = Math.Min(Bins - 1, (int)Math.Floor(high / _binHz));

        // Nothing below the edge is touched. Above it the share rises over the crossover, weighed bin by bin.
        _mixStart = (int)Math.Ceiling(edge / _binHz);
        for (int b = 0; b < Bins; b++)
        {
            _ramp[b] = Math.Clamp(((b * _binHz) - edge) / CrossoverHz, 0.0, 1.0);
        }

        // Twelfths of an octave from the edge to Nyquist, the last one short.
        var starts = new List<int>();
        var centres = new List<double>();
        double nyquist = _sampleRate / 2.0;
        for (double from = edge; from < nyquist - _binHz && starts.Count < _bandPower.Length - 1; from *= Math.Pow(2.0, 1.0 / 12.0))
        {
            double to = Math.Min(nyquist, from * Math.Pow(2.0, 1.0 / 12.0));
            starts.Add((int)Math.Round(from / _binHz));
            centres.Add(Math.Sqrt(from * to) / _binHz);
        }

        starts.Add(Bins);
        _bandStart = [.. starts];
        _bandCentre = [.. centres];

        LowpassSpec Spec(double pass, double stop) =>
            new(pass / _sampleRate, stop / _sampleRate, 70.0);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Frame(Channel c)
    {
        // The signal's frame: the last N samples of it, held back by the drive's delay.
        c.CopyFrame(_frame, N, Delay);
        bool exciting = EdgeHz > 0.0 && _bandPass.Length == _bandPassTaps;
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

        _plan.Forward(_frame, _yRe, _yIm);

        if (exciting)
        {
            MakeHarmonics(c);
            MixInto(c);
        }
        else
        {
            c.Mix = 0.0;
        }

        _plan.Inverse(_yRe, _yIm, _frame);
        c.OverlapAdd(_frame, _window);
    }

    /// <summary>The drive for the last hop, fourfold, through the polynomial, and the transform of the last 4N.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void MakeHarmonics(Channel c)
    {
        // Band-pass: the hop's H samples, each against the taps behind it.
        Span<double> drive = c.DriveHop;
        ReadOnlySpan<double> history = c.RecentInput(H + _bandPassTaps - 1);
        FirKernel.Convolve(ref MemoryMarshal.GetArrayDataReference(_bandPass), (nuint)_bandPass.Length,
            ref MemoryMarshal.GetReference(history), 1, ref MemoryMarshal.GetReference(drive), 1, H);

        // Fourfold: output 4n + p is phase p against the drive up to n.
        Span<double> driveWindow = c.AppendDrive(drive, _interpolatorHistory);
        Span<double> up = c.OversampledHop;
        for (int p = 0; p < Oversampling; p++)
        {
            FirKernel.Convolve(ref MemoryMarshal.GetArrayDataReference(_phases[p]), (nuint)_phases[p].Length,
                ref MemoryMarshal.GetReference(driveWindow), 1, ref Unsafe.Add(ref MemoryMarshal.GetReference(up), p),
                Oversampling, H);
        }

        // Second and third degree. The cube is weighed against a slow measure of the drive's level so the mixture does
        // not change with how loud the music is.
        double power = SimdMath.SumOfSquares(up) / H4;
        c.DriveLevel = c.DriveLevel <= 0.0 ? Math.Sqrt(power) : (0.9 * c.DriveLevel) + (0.1 * Math.Sqrt(power));
        double cube = c.DriveLevel > 1e-12 ? 0.5 / c.DriveLevel : 0.0;
        for (int i = 0; i < up.Length; i++)
        {
            double u = up[i];
            up[i] = u * u * (1.0 + (cube * u));
        }

        c.AppendHarmonics(up);
        c.CopyHarmonics(_frame4);
        for (int i = 0; i < N4; i++)
        {
            _frame4[i] *= _window4[i];
        }

        _plan4.Forward(_frame4, _eRe, _eIm);
    }

    /// <summary>Mixes the harmonics into the frame above the edge; false when there was nothing to mix.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private bool MixInto(Channel c)
    {
        // How much of the octave below the edge is in partials.
        double total = 0.0;
        double partial = 0.0;
        for (int b = _driveLow; b <= _driveHigh; b++)
        {
            double p = (_yRe[b] * _yRe[b]) + (_yIm[b] * _yIm[b]);
            _db[b] = 10.0 * Math.Log10(p + 1e-30);
        }

        for (int b = _driveLow; b <= _driveHigh; b++)
        {
            double sum = 0.0;
            int count = 0;
            for (int k = Math.Max(_driveLow, b - NeighbourBins); k <= Math.Min(_driveHigh, b + NeighbourBins); k++)
            {
                sum += _db[k];
                count++;
            }

            double p = (_yRe[b] * _yRe[b]) + (_yIm[b] * _yIm[b]);
            total += p;
            if (_db[b] > (sum / count) + PartialDb)
            {
                partial += p;
            }
        }

        double target = total > 0.0 ? Math.Min(MaximumMix, 2.0 * partial / total) : 0.0;
        c.Mix = target > c.Mix ? (Attack * c.Mix) + ((1.0 - Attack) * target) : (Release * c.Mix) + ((1.0 - Release) * target);
        if (c.Mix < 1e-4)
        {
            return false;
        }

        // Everything the polynomial made, at every frequency the oversampled signal holds: what a band of harmonics
        // above the edge is weighed against before it is trusted.
        double harmonicAll = 0.0;
        for (int b = 0; b <= N4 / 2; b++)
        {
            harmonicAll += (_eRe[b] * _eRe[b]) + (_eIm[b] * _eIm[b]);
        }

        // Partials the restorer's output already has above the edge are kept as they are, with the bins either side
        // that carry them. The detector's edge is where the level has fallen 6 dB: an encoder that rolls off gently
        // leaves real partials above it, and in a spectrum of sparse partials the fall can be found in the gap below
        // the last one. A partial just under the edge is looked for too, since its lobe reaches over it.
        int scanFrom = Math.Max(_driveHigh + 1, _mixStart - KeepBins - 1);
        int dbFrom = Math.Max(0, scanFrom - NeighbourBins);
        for (int b = dbFrom; b < Bins; b++)
        {
            double p = (_yRe[b] * _yRe[b]) + (_yIm[b] * _yIm[b]);
            _db[b] = 10.0 * Math.Log10(p + 1e-30);
        }

        Array.Clear(_keep);
        for (int b = Math.Max(scanFrom, dbFrom + 1); b < Bins - 1; b++)
        {
            if (_db[b] < _db[b - 1] || _db[b] < _db[b + 1])
            {
                continue;
            }

            double sum = 0.0;
            int count = 0;
            for (int k = Math.Max(dbFrom, b - NeighbourBins); k <= Math.Min(Bins - 1, b + NeighbourBins); k++)
            {
                sum += _db[k];
                count++;
            }

            if (_db[b] > (sum / count) + PartialDb)
            {
                for (int k = Math.Max(0, b - KeepBins); k <= Math.Min(Bins - 1, b + KeepBins); k++)
                {
                    _keep[k] = true;
                }
            }
        }

        // And any bin standing 10 dB over the middle of its twelfth of an octave. A step in the restorer's band, such
        // as the codec's own cliff when the detector found the fall in a gap below it, spills into the first bins
        // above it through either window, and read as level it lifts the whole band's harmonics by several decibels.
        // The restorer's texture puts a bin that high one time in a thousand.
        int bandCount = _bandCentre.Length;
        for (int band = 0; band < bandCount; band++)
        {
            int start = Math.Max(_mixStart, _bandStart[band]);
            int count = 0;
            for (int b = start; b < _bandStart[band + 1]; b++)
            {
                if (!_keep[b])
                {
                    _sorted[count++] = (_yRe[b] * _yRe[b]) + (_yIm[b] * _yIm[b]);
                }
            }

            if (count < 5)
            {
                continue;
            }

            Span<double> values = _sorted.AsSpan(0, count);
            values.Sort();
            double limit = 10.0 * values[count / 2];
            for (int b = start; b < _bandStart[band + 1]; b++)
            {
                if ((_yRe[b] * _yRe[b]) + (_yIm[b] * _yIm[b]) > limit)
                {
                    _keep[b] = true;
                }
            }
        }

        // Each twelfth of an octave: the restorer's power there, measured through the low-leakage window, and the
        // harmonics', both over the bins that will be mixed and each bin weighed by how far into the crossover it is;
        // and everything the harmonics have there, for the test below.
        int bands = _bandCentre.Length;
        double harmonicTotal = 0.0;
        for (int band = 0; band < bands; band++)
        {
            double py = 0.0;
            double pe = 0.0;
            double all = 0.0;
            for (int b = _bandStart[band]; b < _bandStart[band + 1]; b++)
            {
                double e = (_eRe[b] * _eRe[b]) + (_eIm[b] * _eIm[b]);
                all += e;
                if (!_keep[b])
                {
                    py += _ramp[b] * ((_lRe[b] * _lRe[b]) + (_lIm[b] * _lIm[b]));
                    pe += _ramp[b] * e;
                }
            }

            _bandPower[band] = py * _lowLeakScale;
            _bandHarmonic[band] = pe;
            _bandHarmonicAll[band] = all;
            harmonicTotal += all;
        }

        // A band the harmonics reach only with a trace, 50 dB under what the polynomial made in all, keeps the
        // restorer's texture: a lone tone whose square lies above Nyquist leaves nothing real there, only the product
        // of the tone with the interpolator's image 90 dB down, which scaling to the band's level would make a tone.
        double floor = harmonicAll * 1e-5;
        if (harmonicTotal < floor)
        {
            return false;
        }

        for (int band = 0; band < bands; band++)
        {
            double py = _bandPower[band];
            double pe = Math.Max(_bandHarmonic[band], (py * 1e-6) + 1e-300);
            _logGain[band] = 0.5 * Math.Log((py + 1e-300) / pe);
        }

        // Gains between band centres by their logarithms, so a band edge is not a step; held flat beyond the first
        // and last centre.
        for (int b = _mixStart; b < Bins; b++)
        {
            double at = b;
            double lg;
            if (at <= _bandCentre[0])
            {
                lg = _logGain[0];
            }
            else if (at >= _bandCentre[bands - 1])
            {
                lg = _logGain[bands - 1];
            }
            else
            {
                int band = 0;
                while (band < bands - 2 && _bandCentre[band + 1] < at)
                {
                    band++;
                }

                double t = (at - _bandCentre[band]) / (_bandCentre[band + 1] - _bandCentre[band]);
                lg = _logGain[band] + (t * (_logGain[band + 1] - _logGain[band]));
            }

            _gain[b] = Math.Exp(lg);
        }

        // A band the harmonics barely reach keeps the restorer's texture. Every other band is brought to the
        // restorer's power exactly: between centres a gain follows its neighbour's, and beside a band much louder than
        // itself a band would otherwise take on some of that level.
        for (int band = 0; band < bands; band++)
        {
            int start = Math.Max(_mixStart, _bandStart[band]);
            int end = _bandStart[band + 1];
            if (end <= start)
            {
                continue;
            }

            if (_bandHarmonicAll[band] < floor)
            {
                Array.Clear(_gain, start, end - start);
                continue;
            }

            if (_bandHarmonic[band] < _bandPower[band] * 1e-6)
            {
                continue;
            }

            double made = 0.0;
            for (int b = start; b < end; b++)
            {
                if (!_keep[b])
                {
                    made += _ramp[b] * _gain[b] * _gain[b] * ((_eRe[b] * _eRe[b]) + (_eIm[b] * _eIm[b]));
                }
            }

            if (made > 0.0)
            {
                double correction = Math.Sqrt(_bandPower[band] / made);
                for (int b = start; b < end; b++)
                {
                    _gain[b] *= correction;
                }
            }
        }

        for (int b = _mixStart; b < Bins; b++)
        {
            double w = c.Mix * _ramp[b];
            if (w <= 0.0 || _keep[b] || _gain[b] == 0.0)
            {
                continue;
            }

            double stay = Math.Sqrt(1.0 - w);
            double add = Math.Sqrt(w) * _gain[b];
            _yRe[b] = (stay * _yRe[b]) + (add * _eRe[b]);
            _yIm[b] = (stay * _yIm[b]) + (add * _eIm[b]);
        }

        return true;
    }

    /// <summary>One channel's signal history, drive, harmonics and overlap-add.</summary>
    private sealed class Channel
    {
        private readonly int _delay;
        private readonly int _history;
        private readonly double[] _input;
        private readonly double[] _drive;
        private readonly double[] _harmonics = new double[N4];
        private readonly double[] _ola = new double[N];
        private readonly double[] _ready = new double[N];
        private int _fill;
        private int _hopFill;
        private int _readyRead;
        private int _readyCount;
        private long _frames;

        public Channel(int delay, int bandPassTaps, int interpolatorHistory)
        {
            _delay = delay;
            _history = Math.Max(N + delay, H + bandPassTaps - 1);
            _input = new double[_history];
            _drive = new double[interpolatorHistory + H];
            DriveHop = new double[H];
            OversampledHop = new double[H4];
        }

        public double[] DriveHop { get; }

        public double[] OversampledHop { get; }

        public double Mix { get; set; }

        public double DriveLevel { get; set; }

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
            Mix = 0.0;
            DriveLevel = 0.0;
        }

        /// <summary>Samples the current hop still takes.</summary>
        public int HopRoom => H - _hopFill;

        /// <summary>Takes up to <see cref="HopRoom"/> samples.</summary>
        public void Append(ReadOnlySpan<double> samples)
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

        /// <summary>Marks the hop complete; a frame is due.</summary>
        public void EndHop() => _hopFill = 0;

        /// <summary>The last <paramref name="count"/> samples to arrive.</summary>
        public ReadOnlySpan<double> RecentInput(int count) => _input.AsSpan(_fill - count, count);

        /// <summary>The frame of <paramref name="size"/> samples ending <paramref name="delay"/> samples ago.</summary>
        public void CopyFrame(double[] frame, int size, int delay) =>
            _input.AsSpan(_fill - delay - size, size).CopyTo(frame);

        /// <summary>Appends the hop's drive behind the interpolator's history and returns the whole window.</summary>
        public Span<double> AppendDrive(ReadOnlySpan<double> hop, int history)
        {
            _drive.AsSpan(H, history).CopyTo(_drive);
            hop.CopyTo(_drive.AsSpan(history));
            return _drive;
        }

        public void AppendHarmonics(ReadOnlySpan<double> hop)
        {
            _harmonics.AsSpan(H4).CopyTo(_harmonics);
            hop.CopyTo(_harmonics.AsSpan(N4 - H4));
        }

        public void CopyHarmonics(double[] frame) => _harmonics.CopyTo(frame, 0);

        /// <summary>Adds a synthesised frame; the first H samples of the sum are then final.</summary>
        public void OverlapAdd(double[] frame, double[] window)
        {
            // Hann analysis and synthesis at a quarter overlap sum to 1.5 everywhere.
            for (int i = 0; i < N; i++)
            {
                _ola[i] += frame[i] * window[i] * (1.0 / 1.5);
            }

            // Frame k ends at the signal's sample kH + H − 1 and finishes samples kH + H − N to kH + 2H − N − 1;
            // the first N / H − 1 frames finish only time before the start.
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
        public void Take(Span<double> output)
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
}
