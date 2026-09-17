using FUPlayer.Core.Dsp.Numerics;

namespace FUPlayer.Core.Dsp.Analysis;

/// <summary>
/// Brings a stream down to the lowest rate that still holds the band a display shows, for analysis only.
///
/// A spectrum is only as fine as its sample rate divided by its length. Taken straight from a 705.6 kHz stream, a
/// 16,384-point transform has bins 43 Hz wide and spans 23 ms, so the octaves below a few hundred hertz collapse
/// into a handful of bins, drawn as blocks, and successive frames look at unrelated slices of music. The same
/// stream shown to 48 kHz holds nothing a 176.4 kHz stream cannot, and there the same bins are 11 Hz and the same
/// window 93 ms.
///
/// A cascade of half-band FIR stages, each halving the rate. Every stage keeps the band from 0 Hz up to 40 % of
/// the final rate, which is the most any displayed band is allowed to reach (<see cref="Headroom"/>), and holds
/// whatever would alias into it at least <see cref="AliasRejectionDb"/> down; the early stages have wide
/// transitions and cost little, the last one does the work.
/// </summary>
public sealed class AnalysisDecimator
{
    /// <summary>The analysis rate is at least this many times the highest frequency displayed.</summary>
    public const double Headroom = 2.5;

    public const double AliasRejectionDb = 140.0;

    public const int MaxFactor = 64;

    private readonly Stage[] _stages;

    private AnalysisDecimator(Design design)
    {
        Filters = design;
        _stages = design.Coefficients.Select(h => new Stage(h)).ToArray();
    }

    /// <summary>The filters this decimator runs, shared by every channel decimated the same way.</summary>
    public Design Filters { get; }

    /// <summary>
    /// The largest power of two by which <paramref name="inputRate"/> can be divided while the result stays at least
    /// <see cref="Headroom"/> times <paramref name="bandHz"/>; 1 when no band is chosen.
    /// </summary>
    public static int FactorFor(int inputRate, double bandHz)
    {
        if (bandHz <= 0.0 || inputRate <= 0)
        {
            return 1;
        }

        int factor = 1;
        while (factor < MaxFactor && inputRate % (factor * 2) == 0 && inputRate / (factor * 2.0) >= Headroom * bandHz)
        {
            factor *= 2;
        }

        return factor;
    }

    public static AnalysisDecimator Create(Design design) => new(design);

    /// <summary>
    /// Filters and decimates one block. The returned span is valid until the next call; the first samples out carry
    /// the filters' start-up, which no display window of a useful length notices.
    /// </summary>
    public ReadOnlySpan<double> Process(ReadOnlySpan<double> input)
    {
        ReadOnlySpan<double> signal = input;
        foreach (Stage stage in _stages)
        {
            signal = stage.Process(signal);
        }

        return signal;
    }

    /// <summary>The coefficients of every stage for one input rate and factor.</summary>
    public sealed class Design
    {
        private Design(int inputRate, int factor, double[][] coefficients)
        {
            InputRate = inputRate;
            Factor = factor;
            Coefficients = coefficients;
        }

        public int InputRate { get; }

        public int Factor { get; }

        public int OutputRate => InputRate / Factor;

        internal double[][] Coefficients { get; }

        public static Design For(int inputRate, int factor)
        {
            if (factor <= 1)
            {
                return new Design(inputRate, 1, []);
            }

            int outputRate = inputRate / factor;
            double keep = outputRate / Headroom;
            double beta = 0.1102 * (AliasRejectionDb - 8.7);
            var stages = new List<double[]>();
            for (int rate = inputRate; rate > outputRate; rate /= 2)
            {
                // The band to keep, and its mirror about a quarter of this stage's input rate, where content would
                // fold into it once the rate is halved.
                double transition = 0.5 - (2.0 * keep / rate);
                int length = (int)Math.Ceiling((AliasRejectionDb - 7.95) / (14.36 * transition)) + 1;

                // 4k + 3 taps: odd, and the outermost taps land on the half-band's non-zero coefficients.
                length = Math.Max(7, (length / 4 * 4) + 3);
                stages.Add(HalfBand(length, beta));
            }

            return new Design(inputRate, factor, [.. stages]);
        }

        private static double[] HalfBand(int length, double beta)
        {
            var h = new double[length];
            int centre = length / 2;
            double norm = SpecialFunctions.BesselI0(beta);
            double sum = 0.0;
            for (int n = 0; n < length; n++)
            {
                int m = n - centre;
                double r = (double)m / centre;
                double window = SpecialFunctions.BesselI0(beta * Math.Sqrt(Math.Max(0.0, 1.0 - (r * r)))) / norm;
                h[n] = m % 2 == 0 && m != 0 ? 0.0 : 0.5 * SpecialFunctions.Sinc(0.5 * m) * window;
                sum += h[n];
            }

            for (int n = 0; n < length; n++)
            {
                h[n] /= sum;
            }

            return h;
        }
    }

    private sealed class Stage(double[] coefficients)
    {
        private readonly double[] _h = coefficients;
        private readonly double[] _history = new double[coefficients.Length - 1];
        private double[] _work = [];
        private double[] _output = [];
        private int _parity;

        public ReadOnlySpan<double> Process(ReadOnlySpan<double> input)
        {
            int taps = _h.Length;
            int total = _history.Length + input.Length;
            if (_work.Length < total)
            {
                _work = new double[Math.Max(total, _work.Length * 2)];
            }

            _history.CopyTo(_work, 0);
            input.CopyTo(_work.AsSpan(_history.Length));

            int produced = (input.Length + 1 - _parity) / 2;
            if (_output.Length < produced)
            {
                _output = new double[Math.Max(produced, _output.Length * 2)];
            }

            // Input sample j (counting from this block) completes a window that starts at work index j, and every
            // second one of them, continuing the count from the previous block, is kept.
            int o = 0;
            for (int j = _parity; j < input.Length; j += 2)
            {
                _output[o++] = SimdMath.Dot(_h, _work.AsSpan(j, taps));
            }

            _parity = (_parity + input.Length) % 2;
            _work.AsSpan(total - _history.Length, _history.Length).CopyTo(_history);
            return _output.AsSpan(0, o);
        }
    }
}
