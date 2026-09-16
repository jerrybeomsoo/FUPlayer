namespace FUPlayer.Core.Dsp.Restoration;

/// <summary>
/// Doubles the sample rate with exactly the interpolator the neural upscaler was trained behind.
///
/// The network learned what a lossy or CD-rate recording looks like after torchaudio's windowed-sinc
/// resampler took it to 88.2 or 96 kHz: a Kaiser window with beta 14.77 over 64 zero crossings, the
/// pass band rolled off at 99 percent of the old Nyquist rate. A different interpolator leaves a
/// different transition band just below the edge, which is precisely the region the network reads to
/// decide where the music stops, so this reproduces that kernel coefficient for coefficient rather than
/// borrowing one of the player's own filters.
///
/// Output sample 2n is the interpolated value at input sample n, and 2n+1 the value half a sample later,
/// so the result is phase-aligned with the input and delayed by <see cref="Latency"/> output samples.
/// </summary>
public sealed class TrainingUpsampler
{
    private const int Width = 65;               // ceil(64 / 0.99)
    private const int Taps = (2 * Width) + 1;   // torchaudio pads by width either side, plus one
    private const double Rolloff = 0.99;
    private const double ZeroCrossings = 64.0;
    private const double Beta = 14.769656459379492;

    private static readonly double[] Even = Kernel(0);
    private static readonly double[] Odd = Kernel(1);

    private readonly double[] _history = new double[Taps];
    private int _filled;

    /// <summary>Output samples between an input sample and its interpolated copy.</summary>
    public const int Latency = 2 * Width;

    public TrainingUpsampler() => Reset();

    public void Reset()
    {
        Array.Clear(_history);
        _filled = 0;
    }

    /// <summary>Writes two output samples for every input sample. Returns how many were written.</summary>
    public int Process(ReadOnlySpan<double> input, Span<double> output)
    {
        if (output.Length < 2 * input.Length)
        {
            throw new ArgumentException("The output needs room for two samples per input sample.", nameof(output));
        }

        int written = 0;
        foreach (double sample in input)
        {
            // Shift the window along by one input sample; the newest sample sits at the end.
            Array.Copy(_history, 1, _history, 0, Taps - 1);
            _history[Taps - 1] = sample;
            if (_filled < Taps)
            {
                _filled++;
            }

            // The window now holds x[n - 65] .. x[n + 65] for the n that is 65 samples old.
            double even = 0.0;
            double odd = 0.0;
            for (int m = 0; m < Taps; m++)
            {
                even += _history[m] * Even[m];
                odd += _history[m] * Odd[m];
            }

            output[written++] = even;
            output[written++] = odd;
        }

        return written;
    }

    /// <summary>
    /// torchaudio's _get_sinc_resample_kernel for orig=1, new=2: t = (idx - j/2) * rolloff, clamped to
    /// the zero crossings, Kaiser-windowed sinc, scaled by the rolloff.
    /// </summary>
    private static double[] Kernel(int j)
    {
        double[] kernel = new double[Taps];
        double i0Beta = BesselI0(Beta);
        for (int m = 0; m < Taps; m++)
        {
            double idx = m - Width;
            double t = (idx - (j / 2.0)) * Rolloff;
            t = Math.Clamp(t, -ZeroCrossings, ZeroCrossings);
            double ratio = t / ZeroCrossings;
            double window = BesselI0(Beta * Math.Sqrt(Math.Max(0.0, 1.0 - (ratio * ratio)))) / i0Beta;
            double x = t * Math.PI;
            double sinc = x == 0.0 ? 1.0 : Math.Sin(x) / x;
            kernel[m] = sinc * window * Rolloff;
        }

        return kernel;
    }

    /// <summary>Modified Bessel function of the first kind, order zero, by its power series.</summary>
    private static double BesselI0(double x)
    {
        double sum = 1.0;
        double term = 1.0;
        double half = x / 2.0;
        for (int k = 1; k < 200; k++)
        {
            term *= (half / k) * (half / k);
            sum += term;
            if (term < sum * 1e-17)
            {
                break;
            }
        }

        return sum;
    }
}
