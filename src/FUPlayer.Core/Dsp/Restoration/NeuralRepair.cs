namespace FUPlayer.Core.Dsp.Restoration;

/// <summary>
/// Repairs coded audio with a trained network.
///
/// Every frame is patched above the cutoff the way the training data was patched, described to the
/// network in log-spaced bands together with the frames either side, and then moved band by band to
/// where the network says the original sat. Above the cutoff that is harmonic rebuilding; below it,
/// where the gains are small and mostly negative on bands the encoder left flapping, it is artefact
/// reduction. The same forward pass does both.
///
/// Material with no cutoff at all gets the second half only. That is most of what people actually
/// stream: AAC at 256 kbit/s band-limits nothing, and this stage used to hand such a signal straight
/// back. Measured against the lossless masters over six tracks, correcting the bands that survive is
/// worth 5.4 dB in the top kilohertz and about half a decibel below it, which is small and real.
///
/// Filling from where the roll-off begins rather than from where it ends was tried and measured
/// worse. The roll-off still carries the music; a patch over it is an invention replacing a
/// recording, and against the masters it cost 4 dB at 17 to 18 kHz on MP3 at 256 kbit/s and 7 dB at
/// 18 to 19 on MP3 at 320. The trough that filling from the cutoff used to leave is closed by the
/// gains instead, which is what they are for.
///
/// The network needs the frame after the one it is repairing, so this holds one hop longer than the
/// transform alone would.
///
/// What comes out is a spectrum the network believes the original had. It is a better guess than a
/// fixed slope, and it is still a guess: the samples the encoder threw away are not in the file and
/// no amount of arithmetic puts them back.
/// </summary>
public sealed class NeuralRepair : SpectralProcessor
{
    private readonly NeuralRepairModel _model;
    private readonly BandLayout _layout;
    private readonly double[] _power;
    private readonly float[] _history;
    private readonly double[] _levels;
    private readonly float[] _window;
    private readonly float[] _input;
    private readonly float[] _gains;
    private readonly double[][] _heldRe;
    private readonly double[][] _heldIm;
    private readonly int[] _bandOfBin;
    private readonly int[] _lowBand;
    private readonly int[] _highBand;
    private readonly double[] _blend;
    private readonly double[][] _keptRe;
    private readonly double[][] _keptIm;
    private readonly int[] _keptEdge;
    private readonly int _slots;
    private readonly int _lowBin;
    private readonly int _highBin;

    private long _frame;
    private int _filled;

    /// <summary>
    /// Transform size for a rate, chosen so the window spans the same stretch of time the training
    /// set was measured over: 46 ms, which is 2,048 points at 44.1 kHz.
    ///
    /// A fixed 2,048 points at 88.2 kHz is half the window and twice the width per bin, so the band
    /// levels handed to the model are not the band levels it was fitted to. Playing a file this does
    /// not arise, because the conversion rate is the file's own; capturing another application's
    /// output it always does, since that runs at whatever Windows happens to be mixing at.
    /// </summary>
    public static int FrameSizeFor(int sampleRate)
    {
        double want = RepairFrameBuilder.FftSize * (sampleRate / 44_100.0);
        int size = RepairFrameBuilder.FftSize;

        // Nearest power of two on a log scale, so 96 kHz takes 4,096 rather than 8,192.
        while (size < 1 << 15 && size * Math.Sqrt(2.0) < want)
        {
            size <<= 1;
        }

        return size;
    }

    public NeuralRepair(NeuralRepairModel model, int sampleRate)
        : base(sampleRate, FrameSizeFor(sampleRate))
    {
        ArgumentNullException.ThrowIfNull(model);
        _model = model;
        _layout = model.Layout;

        _slots = (2 * model.Context) + 1;
        _power = new double[Bins];
        _history = new float[_slots * _layout.Count];
        _levels = new double[_slots];
        _window = new float[_layout.Count * _slots];
        _input = new float[model.InputSize];
        _gains = new float[_layout.Count];

        // Which band each bin falls in, worked out once rather than a thousand times a frame, and
        // the two bands whose centres bracket it. Applying one gain across a whole band puts a step
        // in the spectrum at every band edge, which is audible as a roughness the network never
        // asked for; interpolating between band centres puts a slope there instead.
        _bandOfBin = new int[Bins];
        _lowBand = new int[Bins];
        _highBand = new int[Bins];
        _blend = new double[Bins];

        // Outside the layout there is no answer to apply. BandOf clamps to the end bands, so without
        // this the top band's gain would be applied to everything from 22 kHz to the Nyquist rate of
        // whatever the file is being converted to, which on an 88.2 kHz output is most of the
        // spectrum and none of it was ever measured.
        _lowBin = Math.Max(1, (int)Math.Ceiling(_layout.LowHz / BinHz));
        _highBin = Math.Min(Bins - 1, (int)(_layout.HighHz / BinHz));

        for (int bin = 0; bin < Bins; bin++)
        {
            double hz = bin * BinHz;
            int band = _layout.BandOf(hz);
            _bandOfBin[bin] = band;

            if (hz <= _layout.CentreHz(band))
            {
                _lowBand[bin] = Math.Max(0, band - 1);
                _highBand[bin] = band;
            }
            else
            {
                _lowBand[bin] = band;
                _highBand[bin] = Math.Min(_layout.Count - 1, band + 1);
            }

            double low = _layout.CentreHz(_lowBand[bin]);
            double high = _layout.CentreHz(_highBand[bin]);
            _blend[bin] = high > low ? Math.Clamp((Math.Log(hz + 1e-9) - Math.Log(low)) / (Math.Log(high) - Math.Log(low)), 0.0, 1.0) : 0.0;
        }

        // The spectra wait here until the frames after them have been seen, patched in _held and as
        // the codec left them in _kept, with the cutoff each one was measured at.
        _heldRe = new double[_slots][];
        _heldIm = new double[_slots][];
        _keptRe = new double[_slots][];
        _keptIm = new double[_slots][];
        _keptEdge = new int[_slots];
        for (int i = 0; i < _slots; i++)
        {
            _heldRe[i] = new double[Bins];
            _heldIm[i] = new double[Bins];
            _keptRe[i] = new double[Bins];
            _keptIm[i] = new double[Bins];
        }
    }

    /// <summary>
    /// Where the codec's band ends, or the Nyquist rate when nothing was cut. Zero means the
    /// bandwidth has not been measured yet and leaves the signal alone.
    ///
    /// Set to Nyquist there is nothing to patch, and this stage becomes the gain correction alone.
    /// That is the case that matters for AAC at 256 kbit/s and above, which throws no band away: the
    /// stage used to switch itself off entirely on such material and give back the input.
    /// </summary>
    public double CutoffHz { get; set; }

    /// <summary>Nothing is written above this.</summary>
    public double CeilingHz { get; set; } = 21_000.0;

    /// <summary>
    /// How much of the network's correction to apply. 1 applies what it was fitted to say, 0 leaves
    /// the levels alone, and up to 3 exaggerates it.
    ///
    /// Above 1 this is no longer the model's answer. The model says how far the coded spectrum is
    /// from the original, and that distance was measured, not chosen; multiplying it pushes the
    /// result past the original in the same direction. It is here because the correction at high bit
    /// rates is genuinely small and some people would rather hear it than have it be right.
    /// </summary>
    public double Amount { get; set; } = 1.0;

    /// <summary>The largest exaggeration <see cref="Amount"/> allows.</summary>
    public const double MaxAmount = 3.0;

    /// <summary>
    /// Trim on the rebuilt band, in decibels, on top of whatever the model asks for.
    ///
    /// This is the "rebuilt level" control, and it used to do nothing whenever a model was installed:
    /// the fixed-slope stage applied it and this one did not, so choosing Quiet changed the setting
    /// and not the sound. It applies above the cutoff only, since it is the invented band it trims.
    /// </summary>
    public double AmountDb { get; set; }

    /// <summary>Let the network set the level of the band above the cutoff.</summary>
    public bool Rebuild { get; set; } = true;

    /// <summary>Let the network correct the bands the codec kept.</summary>
    public bool Reduce { get; set; } = true;

    /// <summary>Extra delay, in samples, beyond the transform itself.</summary>
    public int ExtraLatency => _model.Context * Hop;

    /// <summary>
    /// Fades the rebuilt band out over the last twelfth of an octave below the ceiling.
    ///
    /// Stopping dead at the ceiling leaves a wall ninety decibels deep, which is the very shape this
    /// stage exists to remove, put back one octave higher. A raised cosine over the last few bands
    /// removes that without the fade itself becoming the error: a sixth of an octave, which is what
    /// this was first written as, costs seven decibels at 20 kHz, and measured against the lossless
    /// originals that turned a band which had been two decibels too loud into one three decibels too
    /// quiet. A twelfth of an octave is a twentieth of a decibel there and still no wall.
    /// </summary>
    private static double Taper(int bin, int ceiling)
    {
        int start = (int)(ceiling / 1.06);
        if (bin <= start || ceiling <= start)
        {
            return 1.0;
        }

        if (bin >= ceiling)
        {
            return 0.0;
        }

        double t = (double)(bin - start) / (ceiling - start);
        return 0.5 + (0.5 * Math.Cos(Math.PI * t));
    }

    public override void Reset()
    {
        base.Reset();
        Array.Clear(_history);
        Array.Clear(_levels);
        Array.Clear(_keptEdge);
        _frame = 0;
        _filled = 0;
    }

    protected override void Transform(Span<double> re, Span<double> im)
    {
        int ceiling = Math.Min(_highBin, (int)(CeilingHz / BinHz));
        int edge = Bins;

        // Full-band material has no band to invent, and the gains still have work to do: measured
        // against the lossless masters, correcting the bands AAC at 256 kbit/s keeps is worth about
        // five decibels in the top kilohertz and half a decibel elsewhere. Small, but it is the
        // difference between doing something and doing nothing. Every bin then counts as kept, which
        // is what the training set says about such a frame too.
        bool fullBand = true;
        bool measured = CutoffHz > 0.0;

        if (measured)
        {
            int wall = (int)Math.Ceiling(CutoffHz / BinHz) + 1;
            fullBand = wall > ceiling;
            if (fullBand)
            {
                edge = Bins;
            }
            else if (SpectralPatch.CanPatch(wall, ceiling))
            {
                edge = wall;
            }
            else
            {
                // A cutoff too low to copy an octave from. Leaving the band above it alone is the
                // only safe answer: treating it as kept would apply a rebuilt band's gain to the
                // encoder's noise floor.
                measured = false;
            }
        }

        // Everything below still runs when there is nothing to do, and that is deliberate. Returning
        // early here shortens the stage's delay by the frames of context it holds, so the output
        // jumped forward by half a millisecond at the moment the detector reached a verdict, a second
        // or two into every track. Passing the held frame through instead keeps the delay the same
        // whatever the stage decides, which is also what makes a difference monitor exact.
        int newest = (int)(_frame % _slots);

        // The network is always shown a patched spectrum, because that is what it was trained on, and
        // the patch overwrites whatever the codec left above the cutoff. Keep that band so it can go
        // back when the rebuild is switched off: what the encoder left up there is quiet, but it is
        // signal, and replacing it with silence is a hole in the spectrum rather than the absence of
        // an addition. It is kept per frame because the frame written out is several hops behind the
        // one being read, and the measured cutoff moves while the estimate settles.
        re.CopyTo(_keptRe[newest]);
        im.CopyTo(_keptIm[newest]);
        _keptEdge[newest] = edge;

        if (!fullBand)
        {
            SpectralPatch.Apply(re, im, edge, ceiling, _frame);
        }

        re.CopyTo(_heldRe[newest]);
        im.CopyTo(_heldIm[newest]);

        for (int bin = 0; bin < Bins; bin++)
        {
            _power[bin] = (re[bin] * re[bin]) + (im[bin] * im[bin]);
        }

        _levels[newest] = _layout.Levels(_power, BinHz, _history.AsSpan(newest * _layout.Count, _layout.Count));
        _frame++;

        if (_filled < _slots)
        {
            _filled++;
        }

        // The frame being repaired is the middle of the run, so the network sees what comes after it.
        if (_filled < _slots)
        {
            re.Clear();
            im.Clear();
            return;
        }

        int middle = (int)((_frame - 1 - _model.Context) % _slots);
        if (middle < 0)
        {
            middle += _slots;
        }

        int oldest = (int)(_frame % _slots);
        for (int slot = 0, at = 0; slot < _slots; slot++, at += _layout.Count)
        {
            int index = (oldest + slot) % _slots;
            _history.AsSpan(index * _layout.Count, _layout.Count).CopyTo(_window.AsSpan(at, _layout.Count));
        }

        // Put the held frame back, ready to be moved to where the network says it belonged.
        _heldRe[middle].CopyTo(re);
        _heldIm[middle].CopyTo(im);

        if (!measured)
        {
            // Nothing to work from yet, or a cutoff with no octave under it. The frame goes out as
            // it came in, delayed by exactly as much as a repaired one would have been.
            return;
        }

        // Against the top of the layout, not against this stream's Nyquist rate: the training set was
        // measured that way, and a 44.1 kHz recording captured at 88.2 kHz has to describe itself the
        // same either way or the model is answering about a codec that is not there.
        RepairFrameBuilder.Compose(
            _window, _layout.Count, _model.Context, _levels[middle], CutoffHz / _layout.HighHz, _input);
        _model.Predict(_input, _gains);

        // One forward pass answers for the whole spectrum, so which half of the answer is used is
        // what the two switches decide.
        double amount = Math.Clamp(Amount, 0.0, MaxAmount);
        double trim = Math.Pow(10.0, AmountDb / 20.0);
        int middleEdge = _keptEdge[middle];
        double[] keptRe = _keptRe[middle];
        double[] keptIm = _keptIm[middle];

        for (int bin = _lowBin; bin <= _highBin; bin++)
        {
            bool above = bin >= middleEdge;
            if (above && !Rebuild)
            {
                // Not rebuilding: undo the patch rather than clear the band.
                re[bin] = keptRe[bin];
                im[bin] = keptIm[bin];
                continue;
            }

            if (!above && !Reduce)
            {
                continue;
            }

            double db = _gains[_lowBand[bin]] + ((_gains[_highBand[bin]] - _gains[_lowBand[bin]]) * _blend[bin]);
            double gain = Math.Pow(10.0, db * amount / 20.0);
            if (!above)
            {
                re[bin] *= gain;
                im[bin] *= gain;
                continue;
            }

            // Above the ceiling the rebuild fades out, and what it fades into is what the codec left
            // rather than silence. Fading into silence erases the encoder's own residue: measured on
            // eight held-out tracks coded to MP3 at 128 kbit/s, the band from 21 to 22 kHz came out
            // 69 dB under the master where leaving it alone was 47 dB under. A fade is supposed to
            // stop an addition, not take something away.
            double taper = Taper(bin, ceiling);
            gain *= trim * taper;
            re[bin] = (re[bin] * gain) + (keptRe[bin] * (1.0 - taper));
            im[bin] = (im[bin] * gain) + (keptIm[bin] * (1.0 - taper));
        }
    }
}
