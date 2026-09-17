using System.Collections.Concurrent;
using FUPlayer.Core.Dsp.Numerics;

namespace FUPlayer.Core.Dsp.Analysis;

/// <summary>Analysis window for spectrum displays.</summary>
public enum SpectrumWindow
{
    Rectangular,
    Hann,
    Hamming,
    Blackman,
    BlackmanHarris,
    FlatTop,
    Kaiser,
}

/// <summary>
/// Keeps the most recent samples of each channel and computes windowed magnitude spectra on demand (typically from a UI
/// timer). A full-scale sine reads 0 dBFS with every window. Samples are copied under a short lock and transformed
/// outside it, so DSP threads are never held up by a large FFT.
///
/// Given the band a display shows (<see cref="SetAnalysisBand"/>), a stream at many times that band is decimated on
/// the way in (<see cref="AnalysisDecimator"/>), so its spectra are taken at <see cref="AnalysisRate"/> rather than
/// at a rate whose bins are too wide for the low octaves.
/// </summary>
public sealed class SpectrumAnalyzer
{
    public const int MinFftSize = 512;
    public const int MaxFftSize = 65536;

    /// <summary>
    /// The longest transform <see cref="MatchedFftSize"/> asks for: four times the longest one offered, so that a
    /// stream analysed at four times a file's rate can still be given the file's resolution.
    /// </summary>
    public const int MaxMatchedFftSize = 4 * MaxFftSize;

    private const int RingSize = MaxMatchedFftSize;

    private const double KaiserBeta = 9.0;
    private const double FloorDb = -300.0;

    private static readonly ConcurrentDictionary<(SpectrumWindow Window, int Size), WindowTable> WindowTables = new();

    private readonly object _gate = new();
    private readonly object _computeGate = new();
    private readonly double[][] _rings;
    private readonly int[] _positions;
    private readonly long[] _written;
    private readonly Dictionary<int, FftPlan> _plans = [];
    private readonly AnalysisDecimator?[] _decimators;
    private double[] _re = [];
    private double[] _im = [];
    private volatile bool _enabled = true;
    private volatile AnalysisDecimator.Design _design;

    public SpectrumAnalyzer(int channels, int sampleRate)
    {
        Channels = channels;
        SampleRate = sampleRate;
        _rings = new double[channels][];
        for (int c = 0; c < channels; c++)
        {
            _rings[c] = new double[RingSize];
        }

        _positions = new int[channels];
        _written = new long[channels];
        _decimators = new AnalysisDecimator?[channels];
        _design = AnalysisDecimator.Design.For(sampleRate, 1);
    }

    public int Channels { get; }

    /// <summary>Rate of the samples pushed in.</summary>
    public int SampleRate { get; }

    /// <summary>Rate the spectra are taken at: <see cref="SampleRate"/>, or less while a narrow band is shown.</summary>
    public int AnalysisRate => _design.OutputRate;

    /// <summary>
    /// While false, <see cref="Push"/> ignores its input so a hidden display costs the DSP threads nothing. Enabling
    /// again discards the old samples, so the first spectrum waits for a full window of new audio.
    /// </summary>
    public bool IsEnabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
            {
                return;
            }

            if (value)
            {
                Reset();
            }

            _enabled = value;
        }
    }

    /// <summary>Power of two between <see cref="MinFftSize"/> and <see cref="MaxFftSize"/>.</summary>
    public static int NormalizeFftSize(int fftSize) =>
        Math.Clamp(FftPlan.NextPowerOfTwo(Math.Max(1, fftSize)), MinFftSize, MaxFftSize);

    public static int BinCount(int fftSize) => NormalizeMatchedFftSize(fftSize) / 2 + 1;

    /// <summary>
    /// The transform length that gives a stream analysed at <paramref name="rate"/> the bin width and time span that
    /// <paramref name="fftSize"/> points give one analysed at <paramref name="referenceRate"/>: the processed output
    /// read with the same resolution as the file it came from.
    /// </summary>
    public static int MatchedFftSize(int fftSize, int rate, int referenceRate)
    {
        fftSize = NormalizeFftSize(fftSize);
        if (rate <= 0 || referenceRate <= 0 || rate == referenceRate)
        {
            return fftSize;
        }

        // The power of two nearest the exact length on a logarithmic scale.
        double exact = fftSize * (double)rate / referenceRate;
        int size = FftPlan.NextPowerOfTwo((long)Math.Ceiling(exact / Math.Sqrt(2.0)));
        return NormalizeMatchedFftSize(size);
    }

    /// <summary>
    /// Chooses the analysis rate for a display showing up to <paramref name="bandHz"/> (0: everything). Changing it
    /// discards the samples held, which were taken at the old rate.
    /// </summary>
    public void SetAnalysisBand(double bandHz)
    {
        int factor = AnalysisDecimator.FactorFor(SampleRate, bandHz);
        if (factor == _design.Factor)
        {
            return;
        }

        lock (_gate)
        {
            _design = AnalysisDecimator.Design.For(SampleRate, factor);
            ClearRings();
        }
    }

    public void Push(int channel, ReadOnlySpan<double> data)
    {
        if (!_enabled || (uint)channel >= (uint)Channels || data.IsEmpty)
        {
            return;
        }

        // Filtered outside the lock, by the one thread that feeds this channel. A band chosen meanwhile makes the
        // result worthless, and it is dropped below.
        AnalysisDecimator.Design design = _design;
        ReadOnlySpan<double> source = data;
        if (design.Factor > 1)
        {
            AnalysisDecimator? decimator = _decimators[channel];
            if (decimator is null || !ReferenceEquals(decimator.Filters, design))
            {
                decimator = AnalysisDecimator.Create(design);
                _decimators[channel] = decimator;
            }

            source = decimator.Process(data);
            if (source.IsEmpty)
            {
                return;
            }
        }

        int produced = source.Length;
        if (source.Length > RingSize)
        {
            source = source[^RingSize..];
        }

        lock (_gate)
        {
            if (!ReferenceEquals(design, _design))
            {
                return;
            }

            double[] ring = _rings[channel];
            int position = _positions[channel];
            int first = Math.Min(source.Length, RingSize - position);
            source[..first].CopyTo(ring.AsSpan(position));
            source[first..].CopyTo(ring);
            _positions[channel] = (position + source.Length) & (RingSize - 1);
            _written[channel] += produced;
        }
    }

    /// <summary>
    /// Computes the spectrum of the average of <paramref name="channels"/> (pass one index for a single channel) over the
    /// last <paramref name="fftSize"/> samples into <paramref name="magnitudeDb"/> (length ≥ <see cref="BinCount"/>).
    /// </summary>
    /// <returns>False until enough samples have arrived.</returns>
    public bool TryCompute(ReadOnlySpan<int> channels, int fftSize, SpectrumWindow window, Span<double> magnitudeDb)
    {
        fftSize = NormalizeMatchedFftSize(fftSize);
        int bins = fftSize / 2 + 1;
        if (channels.IsEmpty || magnitudeDb.Length < bins)
        {
            return false;
        }

        lock (_computeGate)
        {
            if (_re.Length < fftSize)
            {
                _re = new double[fftSize];
                _im = new double[fftSize];
            }

            Span<double> re = _re.AsSpan(0, fftSize);
            Span<double> im = _im.AsSpan(0, fftSize);
            re.Clear();
            double scale = 1.0 / channels.Length;
            lock (_gate)
            {
                foreach (int channel in channels)
                {
                    if ((uint)channel >= (uint)Channels || _written[channel] < fftSize)
                    {
                        return false;
                    }
                }

                foreach (int channel in channels)
                {
                    double[] ring = _rings[channel];
                    int start = (_positions[channel] - fftSize) & (RingSize - 1);
                    int first = Math.Min(fftSize, RingSize - start);
                    SimdMath.AddScaled(re[..first], ring.AsSpan(start, first), scale);
                    if (first < fftSize)
                    {
                        SimdMath.AddScaled(re[first..], ring.AsSpan(0, fftSize - first), scale);
                    }
                }
            }

            WindowTable table = WindowTables.GetOrAdd((window, fftSize), static key => WindowTable.Create(key.Window, key.Size));
            double[] coefficients = table.Coefficients;
            for (int n = 0; n < fftSize; n++)
            {
                re[n] *= coefficients[n];
            }

            im.Clear();
            if (!_plans.TryGetValue(fftSize, out FftPlan? plan))
            {
                plan = new FftPlan(fftSize);
                _plans[fftSize] = plan;
            }

            plan.Forward(re, im);

            // Two, because the input is real and each frequency's energy is split between the bin and its
            // negative-frequency twin, which is not stored. DC and Nyquist are the exceptions: they have no
            // twin, are wholly in the one bin, and doubling them reads a full-scale signal there 6 dB high.
            double normalisation = 2.0 / (fftSize * table.CoherentGain);
            double edge = 0.5 * normalisation;
            int nyquist = fftSize / 2;
            for (int k = 0; k < bins; k++)
            {
                double factor = k == 0 || k == nyquist ? edge : normalisation;
                double magnitude = Math.Sqrt((re[k] * re[k]) + (im[k] * im[k])) * factor;
                magnitudeDb[k] = magnitude > 0.0 ? Math.Max(FloorDb, 20.0 * Math.Log10(magnitude)) : FloorDb;
            }
        }

        return true;
    }

    /// <summary>Convenience overload for one channel.</summary>
    public bool TryCompute(int channel, int fftSize, SpectrumWindow window, Span<double> magnitudeDb) =>
        TryCompute([channel], fftSize, window, magnitudeDb);

    public void Reset()
    {
        lock (_gate)
        {
            ClearRings();
        }
    }

    private static int NormalizeMatchedFftSize(int fftSize) =>
        Math.Clamp(FftPlan.NextPowerOfTwo(Math.Max(1, fftSize)), MinFftSize, MaxMatchedFftSize);

    private void ClearRings()
    {
        foreach (double[] ring in _rings)
        {
            Array.Clear(ring);
        }

        Array.Clear(_positions);
        Array.Clear(_written);
    }

    private sealed record WindowTable(double[] Coefficients, double CoherentGain)
    {
        public static WindowTable Create(SpectrumWindow window, int size)
        {
            var coefficients = new double[size];
            double kaiserNorm = SpecialFunctions.BesselI0(KaiserBeta);
            for (int n = 0; n < size; n++)
            {
                double phase = 2.0 * Math.PI * n / size;
                coefficients[n] = window switch
                {
                    SpectrumWindow.Rectangular => 1.0,
                    SpectrumWindow.Hann => 0.5 - 0.5 * Math.Cos(phase),
                    SpectrumWindow.Hamming => 0.54 - 0.46 * Math.Cos(phase),
                    SpectrumWindow.Blackman => 0.42 - 0.5 * Math.Cos(phase) + 0.08 * Math.Cos(2 * phase),
                    SpectrumWindow.FlatTop => 0.21557895 - 0.41663158 * Math.Cos(phase) + 0.277263158 * Math.Cos(2 * phase)
                        - 0.083578947 * Math.Cos(3 * phase) + 0.006947368 * Math.Cos(4 * phase),
                    SpectrumWindow.Kaiser => SpecialFunctions.BesselI0(KaiserBeta * Math.Sqrt(Math.Max(0.0, 1.0 - Math.Pow(2.0 * n / size - 1.0, 2)))) / kaiserNorm,
                    _ => 0.35875 - 0.48829 * Math.Cos(phase) + 0.14128 * Math.Cos(2 * phase) - 0.01168 * Math.Cos(3 * phase),
                };
            }

            return new WindowTable(coefficients, coefficients.Sum() / size);
        }
    }
}
