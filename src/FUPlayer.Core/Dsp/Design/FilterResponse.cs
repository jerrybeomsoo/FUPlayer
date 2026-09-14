using System.Numerics;
using FUPlayer.Core.Dsp.Filters;
using FUPlayer.Core.Dsp.Numerics;

namespace FUPlayer.Core.Dsp.Design;

/// <summary>Sampled frequency response for display.</summary>
public sealed record ResponseCurve(double[] Frequencies, double[] MagnitudeDb, double[] PhaseRadians);

/// <summary>Frequency, impulse and step response evaluation.</summary>
public static class FilterResponse
{
    private const double FloorDb = -400.0;

    /// <summary>Response of an FIR sampled uniformly on [0, maxFrequency].</summary>
    public static ResponseCurve Fir(ReadOnlySpan<double> h, double sampleRate, double maxFrequency, int points)
    {
        points = Math.Max(2, points);
        maxFrequency = Math.Clamp(maxFrequency, 1.0, sampleRate / 2.0);
        double resolution = maxFrequency / (points - 1);
        var frequencies = new double[points];
        var magnitude = new double[points];
        var phase = new double[points];
        for (int i = 0; i < points; i++)
        {
            frequencies[i] = i * resolution;
        }

        // A transform as long as the impulse resolves everything the filter can do, but a filter of tens of
        // millions of taps does not get one: past the cap the impulse is folded instead. Adding h[n] into
        // n mod size samples the transform at exactly those bins. It is the same answer, not an approximation,
        // and unlike truncating the impulse it does not quietly flatter the stopband.
        long wanted = Math.Max((long)Math.Ceiling(sampleRate / resolution) * 2, h.Length);
        int size = FftPlan.NextPowerOfTwo(Math.Min(wanted, 1 << 22));
        var re = new double[size];
        var im = new double[size];
        for (int n = 0; n < h.Length; n++)
        {
            re[n % size] += h[n];
        }

        new FftPlan(size).Forward(re, im);
        double binWidth = sampleRate / size;
        for (int i = 0; i < points; i++)
        {
            int bin = Math.Min((int)Math.Round(frequencies[i] / binWidth), size / 2);
            var value = new Complex(re[bin], im[bin]);
            magnitude[i] = ToDb(value.Magnitude);
            phase[i] = value.Phase;
        }

        Unwrap(phase);
        return new ResponseCurve(frequencies, magnitude, phase);
    }

    /// <summary>Response of a biquad cascade sampled uniformly on [0, maxFrequency].</summary>
    public static ResponseCurve Sos(ReadOnlySpan<Biquad> sections, double sampleRate, double maxFrequency, int points)
    {
        points = Math.Max(2, points);
        maxFrequency = Math.Clamp(maxFrequency, 1.0, sampleRate / 2.0);
        var frequencies = new double[points];
        var magnitude = new double[points];
        var phase = new double[points];
        for (int i = 0; i < points; i++)
        {
            frequencies[i] = maxFrequency * i / (points - 1);
            Complex value = Biquad.CascadeResponse(sections, frequencies[i] / sampleRate);
            magnitude[i] = ToDb(value.Magnitude);
            phase[i] = value.Phase;
        }

        Unwrap(phase);
        return new ResponseCurve(frequencies, magnitude, phase);
    }

    /// <summary>DTFT of an FIR at one normalised frequency (cycles per sample), summed directly.</summary>
    public static Complex EvaluateFir(ReadOnlySpan<double> h, double frequency)
    {
        double omega = 2.0 * Math.PI * frequency;
        double re = 0.0;
        double im = 0.0;
        for (int n = 0; n < h.Length; n++)
        {
            re += h[n] * Math.Cos(omega * n);
            im -= h[n] * Math.Sin(omega * n);
        }

        return new Complex(re, im);
    }

    /// <summary>Impulse response of a biquad cascade.</summary>
    public static double[] SosImpulse(IReadOnlyList<Biquad> sections, int length)
    {
        var data = new double[length];
        if (length > 0)
        {
            data[0] = 1.0;
        }

        new SosCascade(sections).Process(data);
        return data;
    }

    public static double[] StepResponse(ReadOnlySpan<double> impulse)
    {
        var step = new double[impulse.Length];
        double sum = 0.0;
        for (int i = 0; i < impulse.Length; i++)
        {
            sum += impulse[i];
            step[i] = sum;
        }

        return step;
    }

    public static double ToDb(double magnitude) =>
        magnitude > 0.0 ? Math.Max(FloorDb, 20.0 * Math.Log10(magnitude)) : FloorDb;

    private static void Unwrap(double[] phase)
    {
        double offset = 0.0;
        for (int i = 1; i < phase.Length; i++)
        {
            double raw = phase[i] + offset;
            double delta = raw - phase[i - 1];
            if (delta > Math.PI)
            {
                offset -= 2.0 * Math.PI * Math.Round(delta / (2.0 * Math.PI));
            }
            else if (delta < -Math.PI)
            {
                offset += 2.0 * Math.PI * Math.Round(-delta / (2.0 * Math.PI));
            }

            phase[i] += offset;
        }
    }
}
