using System.Numerics;

namespace FUPlayer.Core.Dsp.Filters;

/// <summary>Second-order section with a0 = 1: H(z) = (b0 + b1·z⁻¹ + b2·z⁻²) / (1 + a1·z⁻¹ + a2·z⁻²).</summary>
public readonly record struct Biquad(double B0, double B1, double B2, double A1, double A2)
{
    public static readonly Biquad Identity = new(1.0, 0.0, 0.0, 0.0, 0.0);

    /// <param name="frequency">Normalised frequency in cycles per sample.</param>
    public Complex Response(double frequency)
    {
        Complex z1 = Complex.FromPolarCoordinates(1.0, -2.0 * Math.PI * frequency);
        Complex z2 = z1 * z1;
        return (B0 + B1 * z1 + B2 * z2) / (1.0 + A1 * z1 + A2 * z2);
    }

    public Biquad ScaleNumerator(double gain) => this with { B0 = B0 * gain, B1 = B1 * gain, B2 = B2 * gain };

    public static Complex CascadeResponse(ReadOnlySpan<Biquad> sections, double frequency)
    {
        Complex h = Complex.One;
        foreach (Biquad s in sections)
        {
            h *= s.Response(frequency);
        }

        return h;
    }
}

/// <summary>Single-channel streaming biquad cascade (transposed direct form II).</summary>
public sealed class SosCascade
{
    private readonly Biquad[] _sections;
    private readonly double[] _s1;
    private readonly double[] _s2;

    public SosCascade(IReadOnlyList<Biquad> sections)
    {
        _sections = sections.ToArray();
        _s1 = new double[_sections.Length];
        _s2 = new double[_sections.Length];
    }

    public int SectionCount => _sections.Length;

    /// <summary>Filters <paramref name="data"/> in place.</summary>
    public void Process(Span<double> data)
    {
        for (int k = 0; k < _sections.Length; k++)
        {
            Biquad q = _sections[k];
            double b0 = q.B0, b1 = q.B1, b2 = q.B2, a1 = q.A1, a2 = q.A2;
            double s1 = _s1[k];
            double s2 = _s2[k];
            for (int i = 0; i < data.Length; i++)
            {
                double x = data[i];
                double y = b0 * x + s1;
                s1 = b1 * x - a1 * y + s2;
                s2 = b2 * x - a2 * y;
                data[i] = y;
            }

            // Drop denormals so long silences do not hit slow floating-point paths.
            _s1[k] = Math.Abs(s1) < 1e-200 ? 0.0 : s1;
            _s2[k] = Math.Abs(s2) < 1e-200 ? 0.0 : s2;
        }
    }

    public void Reset()
    {
        Array.Clear(_s1);
        Array.Clear(_s2);
    }
}
