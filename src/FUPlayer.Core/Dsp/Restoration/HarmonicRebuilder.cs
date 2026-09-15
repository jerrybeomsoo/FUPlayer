namespace FUPlayer.Core.Dsp.Restoration;

/// <summary>
/// Rebuilds a band above a codec's cutoff from the band that survived below it.
///
/// The top octave below the cutoff is copied upwards in patches, which is how spectral band
/// replication has worked since MPEG-4 HE-AAC. The fine structure comes from real content an octave
/// down, so it moves with the music instead of sitting there as a static hiss, and the phase is
/// rotated to match where it is going so the patches do not beat against each other.
///
/// How loud each rebuilt band should be is decided one of two ways. With no model it follows a fixed
/// slope away from the cutoff, which suits most music and flatters none of it. Given a
/// <see cref="HighBandModel"/> the level is predicted from the shape of the surviving band, which is
/// a better guess because it was learned from music that still had its high end.
///
/// Either way what comes out is not what the encoder threw away. It is plausible material with a
/// plausible shape, and it is synthesis.
///
/// Below the cutoff nothing is written, and what leaks back down through the skirt of the squared
/// analysis window measures 66 dB under the band it came from.
/// </summary>
public sealed class HarmonicRebuilder : SpectralProcessor
{
    private double[] _power = [];
    private double[] _features = [];
    private double[] _prediction = [];
    private double[] _inputEdges = [];
    private double[] _outputEdges = [];
    private HighBandModel? _model;
    private long _frame;

    public HarmonicRebuilder(int sampleRate, int fftSize = 2048)
        : base(sampleRate, fftSize)
    {
    }

    /// <summary>Where the codec's band ends. Zero leaves the signal alone.</summary>
    public double CutoffHz { get; set; }

    /// <summary>Where the roll-off begins, which is where the rebuild starts. Zero means the cutoff.</summary>
    public double TransitionHz { get; set; }

    /// <summary>Trim on the rebuilt band, wherever its level came from.</summary>
    public double AmountDb { get; set; } = -3.0;

    /// <summary>How fast the rebuilt band falls away above the cutoff, when no model is loaded.</summary>
    public double SlopeDbPerOctave { get; set; } = 12.0;

    /// <summary>Highest frequency to rebuild up to. Nothing is written above it.</summary>
    public double CeilingHz { get; set; } = 22_000.0;

    /// <summary>A learned level curve, or null to use the fixed slope.</summary>
    public HighBandModel? Model
    {
        get => _model;
        set
        {
            _model = value;
            if (value is null)
            {
                return;
            }

            _power = new double[Bins];
            _features = new double[value.InputBands];
            _prediction = new double[value.OutputBands];
            _inputEdges = value.InputEdges();
            _outputEdges = value.OutputEdges();
        }
    }

    /// <summary>True when a model is deciding the levels rather than the fixed slope.</summary>
    public bool IsPredicting => _model is not null;

    public override void Reset()
    {
        base.Reset();
        _frame = 0;
    }

    protected override void Transform(Span<double> re, Span<double> im)
    {
        if (CutoffHz <= 0.0)
        {
            return;
        }

        // Rounded up, and then one bin of guard: truncating puts the first rebuilt bin below the
        // cutoff, which overwrites the top of the very band this stage promises not to touch.
        double startHz = TransitionHz > 0.0 ? Math.Min(TransitionHz, CutoffHz) : CutoffHz;
        int edge = (int)Math.Ceiling(startHz / BinHz) + 1;
        int ceiling = Math.Min(Bins - 1, (int)(CeilingHz / BinHz));

        // The source needs a full octave below the cutoff, and there has to be somewhere to put it.
        int source = edge / 2;
        int width = edge - source;
        if (source < 4 || width < 8 || edge >= ceiling)
        {
            return;
        }

        double reference = Rms(re, im, edge - (width / 2), edge);
        if (reference <= 0.0)
        {
            return;
        }

        double lowLevelDb = 0.0;
        if (_model is not null)
        {
            for (int bin = 0; bin < Bins; bin++)
            {
                _power[bin] = (re[bin] * re[bin]) + (im[bin] * im[bin]);
            }

            lowLevelDb = HighBandModel.Features(_power, BinHz, _inputEdges, _features);
            _model.Predict(_features, _prediction);
        }

        double trim = Math.Pow(10.0, AmountDb / 20.0);

        for (int bin = edge; bin <= ceiling; bin++)
        {
            int from = source + ((bin - edge) % width);
            double magnitude = Math.Sqrt((re[from] * re[from]) + (im[from] * im[from]));
            if (magnitude <= 0.0)
            {
                re[bin] = 0.0;
                im[bin] = 0.0;
                continue;
            }

            double wanted = _model is null
                ? reference * Math.Pow(10.0, -SlopeDbPerOctave * Math.Log2((double)bin / edge) / 20.0)
                : Predicted(bin, lowLevelDb);

            double scale = wanted * trim / reference;

            // A bin copied from lower down keeps the phase advance of where it came from, and over a
            // hop that is the wrong advance for where it is going. Rotating by the frequency
            // difference times the hop fixes it, and because the hop is a quarter of the transform
            // the rotation is always a whole number of quarter turns.
            (double cos, double sin) = QuarterTurn((int)(((bin - from) * _frame) & 3));
            double sourceRe = re[from];
            double sourceIm = im[from];

            re[bin] = ((sourceRe * cos) - (sourceIm * sin)) * scale;
            im[bin] = ((sourceRe * sin) + (sourceIm * cos)) * scale;
        }

        _frame++;
    }

    /// <summary>The level the model asks for at one bin, on the same scale as the reference.</summary>
    private double Predicted(int bin, double lowLevelDb)
    {
        double hz = bin * BinHz;
        int band = _prediction.Length - 1;
        for (int i = 0; i < _prediction.Length; i++)
        {
            if (hz < _outputEdges[i + 1])
            {
                band = i;
                break;
            }
        }

        // The features are in power decibels, so the amplitude is half of them.
        return Math.Pow(10.0, (lowLevelDb + _prediction[band]) / 20.0);
    }

    private static (double Cos, double Sin) QuarterTurn(int quarters) => quarters switch
    {
        1 => (0.0, 1.0),
        2 => (-1.0, 0.0),
        3 => (0.0, -1.0),
        _ => (1.0, 0.0),
    };

    private static double Rms(ReadOnlySpan<double> re, ReadOnlySpan<double> im, int low, int high)
    {
        double total = 0.0;
        for (int bin = low; bin < high; bin++)
        {
            total += (re[bin] * re[bin]) + (im[bin] * im[bin]);
        }

        return Math.Sqrt(total / Math.Max(1, high - low));
    }
}
