using FUPlayer.Core.Dsp.Numerics;

namespace FUPlayer.Core.Dsp.Processing;

/// <summary>
/// Pink (−3 dB/octave) noise using Paul Kellet's refined filter bank (public domain), used as the
/// channel-balance test tone. Coefficients are tuned for 44.1 kHz.
/// </summary>
public sealed class PinkNoiseGenerator
{
    private FastRandom _random;
    private double _b0;
    private double _b1;
    private double _b2;
    private double _b3;
    private double _b4;
    private double _b5;
    private double _b6;

    public PinkNoiseGenerator(ulong seed)
    {
        _random = new FastRandom(seed);
    }

    /// <summary>Fills <paramref name="data"/> with pink noise whose RMS is roughly <paramref name="rms"/>.</summary>
    public void Fill(Span<double> data, double rms)
    {
        // The filter bank output has an RMS of about 3.3× the white input RMS (white RMS = 1/√12).
        double scale = rms / (0.29 * 3.3);
        for (int i = 0; i < data.Length; i++)
        {
            double white = _random.NextCentered();
            _b0 = 0.99886 * _b0 + white * 0.0555179;
            _b1 = 0.99332 * _b1 + white * 0.0750759;
            _b2 = 0.96900 * _b2 + white * 0.1538520;
            _b3 = 0.86650 * _b3 + white * 0.3104856;
            _b4 = 0.55000 * _b4 + white * 0.5329522;
            _b5 = -0.7616 * _b5 - white * 0.0168980;
            double pink = _b0 + _b1 + _b2 + _b3 + _b4 + _b5 + _b6 + white * 0.5362;
            _b6 = white * 0.115926;
            data[i] = pink * scale;
        }
    }
}
