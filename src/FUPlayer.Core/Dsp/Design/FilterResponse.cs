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
        if (binWidth * 2 > resolution)
        {
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

        // Many bins to a point, as for any filter of more than about a hundred thousand taps: each point shows the
        // quietest and the loudest bin around it, one after the other at the same frequency, and the plot draws the span
        // between them. One bin read at each point showed whichever stopband sidelobe it landed on, often tens of
        // decibels under the worst one, and a narrow dip or peak in the passband could fall between two points unseen.
        var spanFrequencies = new double[2 * points];
        var spanMagnitude = new double[2 * points];
        var spanPhase = new double[2 * points];
        for (int i = 0; i < points; i++)
        {
            int from = Math.Clamp((int)Math.Ceiling((frequencies[i] - (resolution / 2)) / binWidth), 0, size / 2);
            int to = Math.Clamp((int)Math.Floor((frequencies[i] + (resolution / 2)) / binWidth), from, size / 2);
            int quietest = from;
            int loudest = from;
            double low = double.PositiveInfinity;
            double high = double.NegativeInfinity;
            for (int bin = from; bin <= to; bin++)
            {
                double power = (re[bin] * re[bin]) + (im[bin] * im[bin]);
                if (power < low)
                {
                    low = power;
                    quietest = bin;
                }

                if (power > high)
                {
                    high = power;
                    loudest = bin;
                }
            }

            spanFrequencies[2 * i] = spanFrequencies[(2 * i) + 1] = frequencies[i];
            spanMagnitude[2 * i] = ToDb(Math.Sqrt(low));
            spanMagnitude[(2 * i) + 1] = ToDb(Math.Sqrt(high));
            spanPhase[2 * i] = Math.Atan2(im[quietest], re[quietest]);
            spanPhase[(2 * i) + 1] = Math.Atan2(im[loudest], re[loudest]);
        }

        Unwrap(spanPhase);
        return new ResponseCurve(spanFrequencies, spanMagnitude, spanPhase);
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
