using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FUPlayer.Core.Audio;
using FUPlayer.Core.Dsp.Design;
using FUPlayer.Core.Dsp.Numerics;

namespace FUPlayer.Core.Dsp.Resampling;

/// <summary>
/// Efficient 2x interpolator built on a linear-phase half-band FIR (length 4K+1).
/// Even output samples are plain delayed copies; odd samples need a single 2K-tap dot product.
/// Used for the cascade stages after the user-selected filter, where the signal band is already
/// far below the stage Nyquist frequency.
/// </summary>
public sealed class HalfbandInterpolatorStage : IRateStage
{
    private readonly double[] _odd;

    /// <param name="inputRate">Stage input rate.</param>
    /// <param name="passbandHz">Highest frequency that must pass untouched (the signal band).</param>
    /// <param name="attenuationDb">Image rejection.</param>
    public HalfbandInterpolatorStage(int inputRate, double passbandHz, double attenuationDb)
    {
        InputRate = inputRate;
        OutputRate = checked(inputRate * 2);
        double passband = Math.Min(passbandHz / OutputRate, 0.24);
        var spec = new LowpassSpec(passband, 0.5 - passband, attenuationDb);
        int estimated = FirDesign.EstimateLength(spec);
        HalfLength = Math.Max(1, (estimated - 1 + 3) / 4);
        int length = 4 * HalfLength + 1;
        double[] h = FirDesign.Lowpass(spec, 2.0, length);

        // Enforce exact half-band structure: centre tap 1, other even offsets 0, odd taps summing to 1.
        int center = 2 * HalfLength;
        _odd = new double[2 * HalfLength];
        double oddSum = 0.0;
        for (int m = 0; m < 2 * HalfLength; m++)
        {
            oddSum += h[1 + 2 * m];
        }

        for (int m = 0; m < 2 * HalfLength; m++)
        {
            _odd[2 * HalfLength - 1 - m] = h[1 + 2 * m] / oddSum;
        }

        Taps = length;
        Description = $"Half-band ×2 {AudioRates.FormatShort(inputRate)}→{AudioRates.FormatShort(OutputRate)} ({length} taps)";
        DelayOutputSamples = center;
    }

    public int InputRate { get; }

    public int OutputRate { get; }

    /// <summary>K: the filter has 4K+1 taps; even outputs are delayed by K input samples.</summary>
    public int HalfLength { get; }

    public double DelayOutputSamples { get; }

    public double CostPerOutputSample => HalfLength + 0.5;

    public int Taps { get; }

    public string Description { get; }

    public int MaxOutput(int inputSamples) => checked(inputSamples * 2);

    public IRateStageState CreateState() => new State(this);

    /// <remarks>
    /// The stages run at up to 11 MHz, so the odd outputs are computed tap by tap (one vectorised pass per tap)
    /// instead of one dot product per sample, over chunks small enough to stay in the CPU cache.
    /// </remarks>
    private sealed class State : IRateStageState
    {
        private const int ChunkSize = 4096;

        private readonly HalfbandInterpolatorStage _stage;
        private readonly int _history;
        private readonly double[] _oddOutput = new double[ChunkSize];
        private double[] _buffer;

        public State(HalfbandInterpolatorStage stage)
        {
            _stage = stage;
            _history = 2 * stage.HalfLength - 1;
            _buffer = new double[Math.Max(8192, 8 * stage.HalfLength)];
            Reset();
        }

        public void Reset() => Array.Clear(_buffer, 0, _history);

        public int Process(ReadOnlySpan<double> input, Span<double> output)
        {
            int n = input.Length;
            if (output.Length < 2 * n)
            {
                throw new ArgumentException("Output buffer is too small.", nameof(output));
            }

            int k = _stage.HalfLength;
            int taps = 2 * k;
            int required = _history + n;
            if (required > _buffer.Length)
            {
                Array.Resize(ref _buffer, (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)required));
            }

            // _buffer = [history (taps − 1) | input]; odd output j = Σ_t odd[t] · buffer[j + t],
            // even output j = buffer[j + history − K] (the input delayed by K samples).
            input.CopyTo(_buffer.AsSpan(_history));
            double[] coefficients = _stage._odd;
            ref double source = ref MemoryMarshal.GetArrayDataReference(_buffer);
            ref double oddSource = ref MemoryMarshal.GetArrayDataReference(_oddOutput);
            ref double destination = ref MemoryMarshal.GetReference(output);
            int evenOffset = _history - k;
            for (int start = 0; start < n; start += ChunkSize)
            {
                int count = Math.Min(ChunkSize, n - start);
                Span<double> odd = _oddOutput.AsSpan(0, count);
                odd.Clear();
                for (int t = 0; t < taps; t++)
                {
                    SimdMath.AddScaled(odd, _buffer.AsSpan(start + t, count), coefficients[t]);
                }

                for (int j = 0; j < count; j++)
                {
                    int index = start + j;
                    Unsafe.Add(ref destination, 2 * index) = Unsafe.Add(ref source, evenOffset + index);
                    Unsafe.Add(ref destination, 2 * index + 1) = Unsafe.Add(ref oddSource, j);
                }
            }

            Array.Copy(_buffer, n, _buffer, 0, _history);
            return 2 * n;
        }
    }
}
