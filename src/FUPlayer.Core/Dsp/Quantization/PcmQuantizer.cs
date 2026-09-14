using System.Runtime.CompilerServices;
using FUPlayer.Core.Dsp.Design;
using FUPlayer.Core.Dsp.Numerics;

namespace FUPlayer.Core.Dsp.Quantization;

/// <summary>
/// Per-channel word-length reduction: converts double samples (full scale ±1.0) to integers of
/// <see cref="Bits"/> resolution, left-justified in a 32-bit container.
/// </summary>
public sealed class PcmQuantizer
{
    private readonly DitherKind _kind;
    private readonly double _scale;
    private readonly double _max;
    private readonly double _min;
    private readonly int _shift;
    private readonly double _errorLimit;

    private readonly double[] _b1 = [];
    private readonly double[] _b2 = [];
    private readonly double[] _a1 = [];
    private readonly double[] _a2 = [];
    private readonly double[] _s1 = [];
    private readonly double[] _s2 = [];

    private readonly double[] _fir = [];
    private readonly double[] _errors = [];

    private FastRandom _random;
    private double _previousUniform;
    private int _errorPosition;

    public PcmQuantizer(DitherPreset preset, int bits, int sampleRate, ulong seed)
    {
        if (bits is < 8 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(bits), "Output resolution must be between 8 and 32 bits.");
        }

        Preset = preset;
        Bits = bits;
        _kind = preset.Kind;
        _scale = Math.ScaleB(1.0, bits - 1);
        _max = _scale - 1.0;
        _min = -_scale;
        _shift = 32 - bits;
        _random = new FastRandom(seed);

        if (_kind == DitherKind.NoiseShaped)
        {
            double osr = Math.Max(1.05, sampleRate / (2.0 * DitherCatalog.ShapingBandwidthHz));
            Ntf = NoiseTransferFunction.Synthesize(preset.ShaperOrder, osr, preset.ShaperMaxGain, preset.OptimizeZeros);
            int count = Ntf.Sections.Length;
            _b1 = new double[count];
            _b2 = new double[count];
            _a1 = new double[count];
            _a2 = new double[count];
            _s1 = new double[count];
            _s2 = new double[count];
            for (int k = 0; k < count; k++)
            {
                _b1[k] = Ntf.Sections[k].B1;
                _b2[k] = Ntf.Sections[k].B2;
                _a1[k] = Ntf.Sections[k].A1;
                _a2[k] = Ntf.Sections[k].A2;
            }

            _errorLimit = 2.0 + 2.0 * Ntf.MaxGain;
        }
        else if (_kind == DitherKind.Psychoacoustic)
        {
            _fir = DitherCatalog.PsychoacousticCoefficients;
            _errors = new double[_fir.Length];
            _errorLimit = 4.0;
        }
    }

    public DitherPreset Preset { get; }

    public int Bits { get; }

    /// <summary>The synthesised NTF for noise-shaping presets.</summary>
    public NoiseTransferFunction? Ntf { get; }

    /// <summary>Samples that exceeded the integer range.</summary>
    public long ClippedSamples { get; private set; }

    public void Process(ReadOnlySpan<double> input, Span<int> output)
    {
        if (output.Length < input.Length)
        {
            throw new ArgumentException("Output buffer is too small.", nameof(output));
        }

        switch (_kind)
        {
            case DitherKind.NoiseShaped:
                ProcessShaped(input, output);
                break;
            case DitherKind.Psychoacoustic:
                ProcessPsychoacoustic(input, output);
                break;
            default:
                ProcessFlat(input, output);
                break;
        }
    }

    public void Reset()
    {
        Array.Clear(_s1);
        Array.Clear(_s2);
        Array.Clear(_errors);
        _previousUniform = 0.0;
        _errorPosition = 0;
    }

    private void ProcessFlat(ReadOnlySpan<double> input, Span<int> output)
    {
        for (int i = 0; i < input.Length; i++)
        {
            double v = input[i] * _scale;
            double dither = _kind switch
            {
                DitherKind.Rectangular => _random.NextCentered(),
                DitherKind.Triangular => _random.NextCentered() + _random.NextCentered(),
                DitherKind.Gaussian => 0.5 * _random.NextGaussian(),
                DitherKind.HighPassTriangular => NextHighPass(),
                _ => 0.0,
            };

            output[i] = Store(Clamp(Math.Floor(v + dither + 0.5)));
        }
    }

    private unsafe void ProcessShaped(ReadOnlySpan<double> input, Span<int> output)
    {
        int sections = _b1.Length;
        fixed (double* b1 = _b1, b2 = _b2, a1 = _a1, a2 = _a2, s1 = _s1, s2 = _s2)
        {
            for (int i = 0; i < input.Length; i++)
            {
                double shapedError = 0.0;
                for (int k = 0; k < sections; k++)
                {
                    shapedError += s1[k];
                }

                double v = input[i] * _scale + shapedError;
                double clamped = Clamp(Math.Floor(v + _random.NextCentered() + _random.NextCentered() + 0.5));
                output[i] = Store(clamped);

                double x = Math.Clamp(clamped - v, -_errorLimit, _errorLimit);
                for (int k = 0; k < sections; k++)
                {
                    double y = x + s1[k];
                    s1[k] = b1[k] * x - a1[k] * y + s2[k];
                    s2[k] = b2[k] * x - a2[k] * y;
                    x = y;
                }
            }
        }
    }

    private void ProcessPsychoacoustic(ReadOnlySpan<double> input, Span<int> output)
    {
        int taps = _fir.Length;
        for (int i = 0; i < input.Length; i++)
        {
            double feedback = 0.0;
            for (int k = 0; k < taps; k++)
            {
                feedback += _fir[k] * _errors[(_errorPosition + k) % taps];
            }

            double v = input[i] * _scale - feedback;
            double clamped = Clamp(Math.Floor(v + _random.NextCentered() + _random.NextCentered() + 0.5));
            output[i] = Store(clamped);

            _errorPosition = (_errorPosition + taps - 1) % taps;
            _errors[_errorPosition] = Math.Clamp(clamped - v, -_errorLimit, _errorLimit);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private double NextHighPass()
    {
        double current = _random.NextCentered();
        double value = current - _previousUniform;
        _previousUniform = current;
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private double Clamp(double q)
    {
        if (q > _max)
        {
            ClippedSamples++;
            return _max;
        }

        if (q < _min || double.IsNaN(q))
        {
            ClippedSamples++;
            return double.IsNaN(q) ? 0.0 : _min;
        }

        return q;
    }

    /// <summary>Left-justifies an already clamped integer sample in its 32-bit container.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Store(double clamped) => (int)((long)clamped << _shift);
}
