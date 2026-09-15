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

    private long _frame;
    private int _filled;

    public NeuralRepair(NeuralRepairModel model, int sampleRate)
        : base(sampleRate, RepairFrameBuilder.FftSize)
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

    /// <summary>Where the rebuilt band starts. Zero leaves the signal alone.</summary>
    public double CutoffHz { get; set; }

    /// <summary>Nothing is written above this.</summary>
    public double CeilingHz { get; set; } = 21_000.0;

    /// <summary>How much of the network's correction to apply, from 0 to 1.</summary>
    public double Amount { get; set; } = 1.0;

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
    /// Fades the rebuilt band out over the last sixth of an octave below the ceiling.
    ///
    /// Stopping dead at the ceiling leaves a wall ninety decibels deep, which is the very shape this
    /// stage exists to remove, put back one octave higher. A raised cosine over the last few bands
    /// costs nothing audible and leaves an edge the ear and the eye both read as an ending.
    /// </summary>
    private static double Taper(int bin, int ceiling)
    {
        int start = (int)(ceiling / 1.125);
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
        if (CutoffHz <= 0.0)
        {
            return;
        }

        int edge = (int)Math.Ceiling(CutoffHz / BinHz) + 1;
        int ceiling = Math.Min(Bins - 1, (int)(CeilingHz / BinHz));
        if (!SpectralPatch.CanPatch(edge, ceiling))
        {
            return;
        }

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

        SpectralPatch.Apply(re, im, edge, ceiling, _frame);
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

        RepairFrameBuilder.Compose(
            _window, _layout.Count, _model.Context, _levels[middle], CutoffHz / (SampleRate / 2.0), _input);
        _model.Predict(_input, _gains);

        // Put the held frame back, moved to where the network says it belonged.
        _heldRe[middle].CopyTo(re);
        _heldIm[middle].CopyTo(im);

        // One forward pass answers for the whole spectrum, so which half of the answer is used is
        // what the two switches decide.
        double amount = Math.Clamp(Amount, 0.0, 1.0);
        double trim = Math.Pow(10.0, AmountDb / 20.0);
        int middleEdge = _keptEdge[middle];
        double[] keptRe = _keptRe[middle];
        double[] keptIm = _keptIm[middle];

        for (int bin = 1; bin < Bins; bin++)
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
            if (above)
            {
                gain *= trim * Taper(bin, ceiling);
            }

            re[bin] *= gain;
            im[bin] *= gain;
        }
    }
}
