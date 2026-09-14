namespace FUPlayer.Core.Dsp.Restoration;

/// <summary>
/// Copies the octave below a cutoff up over the band above it, without deciding how loud the result
/// should be.
///
/// Levelling is a separate question with two answers: a fixed slope, or a trained model. Keeping the
/// copy itself in one place means the model is trained on exactly the spectrum the player will hand
/// it, which it would not be if the two drifted apart.
/// </summary>
public static class SpectralPatch
{
    /// <summary>Lowest bin the copy can start from and still have an octave underneath it.</summary>
    public const int MinimumSource = 4;

    /// <summary>Smallest patch worth making.</summary>
    public const int MinimumWidth = 8;

    /// <summary>True when there is both something to copy and somewhere to put it.</summary>
    public static bool CanPatch(int edge, int ceiling) =>
        edge / 2 >= MinimumSource && edge - (edge / 2) >= MinimumWidth && edge < ceiling;

    /// <summary>
    /// Fills <paramref name="edge"/> to <paramref name="ceiling"/> from the octave below the edge,
    /// leaving everything below untouched. Amplitudes are carried over as they are.
    /// </summary>
    /// <param name="frame">Which frame this is, so the phase can be corrected.</param>
    public static void Apply(Span<double> re, Span<double> im, int edge, int ceiling, long frame)
    {
        if (!CanPatch(edge, ceiling))
        {
            return;
        }

        int source = edge / 2;
        int width = edge - source;

        for (int bin = edge; bin <= ceiling && bin < re.Length; bin++)
        {
            int from = source + ((bin - edge) % width);

            // A bin copied from an octave down keeps the phase advance of where it came from, which
            // over a hop is the wrong advance for where it is going. The hop is a quarter of the
            // transform, so the correction is always a whole number of quarter turns.
            (double cos, double sin) = QuarterTurn((int)(((bin - from) * frame) & 3));
            double sourceRe = re[from];
            double sourceIm = im[from];

            re[bin] = (sourceRe * cos) - (sourceIm * sin);
            im[bin] = (sourceRe * sin) + (sourceIm * cos);
        }
    }

    private static (double Cos, double Sin) QuarterTurn(int quarters) => quarters switch
    {
        1 => (0.0, 1.0),
        2 => (-1.0, 0.0),
        3 => (0.0, -1.0),
        _ => (1.0, 0.0),
    };
}

/// <summary>
/// Log-spaced bands, and the level in each one.
///
/// Everything the model sees and everything it says is in these bands, so the layout travels inside
/// the model file. A model fitted with one layout cannot be read with another.
/// </summary>
public sealed class BandLayout
{
    private const double Floor = 1e-14;

    private readonly double[] _edges;

    public BandLayout(double lowHz, double highHz, int bands)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bands, 4);
        if (highHz <= lowHz * 2.0)
        {
            throw new ArgumentException("The band range has to span at least an octave.", nameof(highHz));
        }

        LowHz = lowHz;
        HighHz = highHz;
        Count = bands;

        _edges = new double[bands + 1];
        double ratio = Math.Log(highHz / lowHz) / bands;
        for (int i = 0; i <= bands; i++)
        {
            _edges[i] = lowHz * Math.Exp(ratio * i);
        }
    }

    public double LowHz { get; }

    public double HighHz { get; }

    public int Count { get; }

    public double EdgeHz(int band) => _edges[Math.Clamp(band, 0, Count)];

    /// <summary>Geometric centre of a band, which is its middle on a log axis.</summary>
    public double CentreHz(int band)
    {
        int at = Math.Clamp(band, 0, Count - 1);
        return Math.Sqrt(_edges[at] * _edges[at + 1]);
    }

    /// <summary>The band a frequency falls in, clamped to the ends.</summary>
    public int BandOf(double hz)
    {
        if (hz <= _edges[0])
        {
            return 0;
        }

        for (int band = 0; band < Count; band++)
        {
            if (hz < _edges[band + 1])
            {
                return band;
            }
        }

        return Count - 1;
    }

    /// <summary>
    /// Mean power per band in decibels. Returns the overall level, which is subtracted from the
    /// bands so that the same model serves a quiet passage and a loud one.
    /// </summary>
    public double Levels(ReadOnlySpan<double> power, double binHz, Span<float> levels)
    {
        int bins = power.Length;
        double total = 0.0;

        for (int band = 0; band < Count; band++)
        {
            int low = Math.Clamp((int)(_edges[band] / binHz), 0, bins - 1);
            int high = Math.Clamp((int)Math.Ceiling(_edges[band + 1] / binHz), low + 1, bins);

            double sum = 0.0;
            for (int bin = low; bin < high; bin++)
            {
                sum += power[bin];
            }

            double db = 10.0 * Math.Log10((sum / (high - low)) + Floor);
            levels[band] = (float)db;
            total += db;
        }

        double level = total / Count;
        for (int band = 0; band < Count; band++)
        {
            levels[band] -= (float)level;
        }

        return level;
    }
}
