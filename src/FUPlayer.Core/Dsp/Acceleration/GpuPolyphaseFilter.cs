using System.Numerics;
using System.Runtime.InteropServices;
using FUPlayer.Core.Dsp.Resampling;

namespace FUPlayer.Core.Dsp.Acceleration;

/// <summary>
/// The device-side half of one <see cref="FftPolyphaseStage"/>: the partition spectra and the twiddle table,
/// uploaded once and shared by every channel's <see cref="GpuConvolver"/>.
/// </summary>
internal sealed class GpuPolyphaseFilter : IGpuFilter
{
    private nint _filterRe;
    private nint _filterIm;
    private nint _twiddleRe;
    private nint _twiddleIm;

    private GpuPolyphaseFilter(FftPolyphaseStage stage, GpuProgram program)
    {
        Stage = stage;
        Program = program;
        Passes = BitOperations.Log2((uint)stage.FftSize);

        int n = stage.FftSize;
        long spectrumBytes = (long)stage.Up * stage.Partitions * n * program.ElementSize;
        if (spectrumBytes > program.Device.MaxAllocationBytes)
        {
            throw new OpenClException(
                $"{stage.Taps:N0} taps need a {spectrumBytes / (1024 * 1024)} MB buffer, more than the " +
                $"{program.Device.MaxAllocationBytes / (1024 * 1024)} MB this device allows in one allocation.");
        }

        nint queue = OpenClApi.CreateQueue(program.Context, program.Device.Handle);
        try
        {
            _filterRe = OpenClApi.CreateBuffer(program.Context, OpenClApi.MemReadOnly, (nuint)spectrumBytes);
            _filterIm = OpenClApi.CreateBuffer(program.Context, OpenClApi.MemReadOnly, (nuint)spectrumBytes);
            _twiddleRe = OpenClApi.CreateBuffer(program.Context, OpenClApi.MemReadOnly, (nuint)(n / 2 * program.ElementSize));
            _twiddleIm = OpenClApi.CreateBuffer(program.Context, OpenClApi.MemReadOnly, (nuint)(n / 2 * program.ElementSize));

            var scratch = new byte[n * program.ElementSize];
            for (int phase = 0; phase < stage.Up; phase++)
            {
                for (int part = 0; part < stage.Partitions; part++)
                {
                    (double[] re, double[] im) = stage.PartitionSpectrum(phase, part);
                    nuint offset = (nuint)(((long)phase * stage.Partitions + part) * n * program.ElementSize);
                    OpenClApi.Write(queue, _filterRe, offset, Pack(re, scratch, program.UsesDouble));
                    OpenClApi.Write(queue, _filterIm, offset, Pack(im, scratch, program.UsesDouble));
                }
            }

            // The same convention as FftPlan: forward multiplies by e^(−j2πk/n), the inverse by its conjugate.
            var twiddleRe = new double[n / 2];
            var twiddleIm = new double[n / 2];
            for (int k = 0; k < n / 2; k++)
            {
                double angle = 2.0 * Math.PI * k / n;
                twiddleRe[k] = Math.Cos(angle);
                twiddleIm[k] = -Math.Sin(angle);
            }

            var twiddleScratch = new byte[n / 2 * program.ElementSize];
            OpenClApi.Write(queue, _twiddleRe, 0, Pack(twiddleRe, twiddleScratch, program.UsesDouble));
            OpenClApi.Write(queue, _twiddleIm, 0, Pack(twiddleIm, twiddleScratch, program.UsesDouble));
            OpenClApi.Finish(queue);
        }
        catch
        {
            Release();
            OpenClApi.ReleaseQueue(queue);
            throw;
        }

        OpenClApi.ReleaseQueue(queue);
    }

    public FftPolyphaseStage Stage { get; }

    public GpuProgram Program { get; }

    /// <summary>log2 of the transform size: the number of Stockham passes each transform takes.</summary>
    public int Passes { get; }

    public nint FilterRe => _filterRe;

    public nint FilterIm => _filterIm;

    public nint TwiddleRe => _twiddleRe;

    public nint TwiddleIm => _twiddleIm;

    /// <summary>
    /// Uploads the stage and proves the device reproduces the processor's own output before anything is played
    /// through it. A device that answers differently is not used at all.
    /// </summary>
    /// <param name="allowUnchecked">
    /// Keep the filter even where the comparison could not be made at all, which is what forcing the device
    /// asks for. A disagreement still sends the stage back to the processor either way.
    /// </param>
    public static GpuPolyphaseFilter CreateVerified(
        FftPolyphaseStage stage, GpuProgram program, bool allowUnchecked = false)
    {
        var filter = new GpuPolyphaseFilter(stage, program);
        try
        {
            string? why = filter.Verify();
            if (why is not null && !allowUnchecked)
            {
                throw new OpenClException(why);
            }

            filter.Unchecked = why;
            return filter;
        }
        catch
        {
            filter.Dispose();
            throw;
        }
    }

    public void Dispose() => Release();

    /// <summary>
    /// Runs noise through both paths and compares them sample by sample, over as much of the filter as it can
    /// afford to reach: every partition where there are few, and the leading ones where there are hundreds.
    /// </summary>
    /// <returns>Why the two could not be compared, or null when they were and agreed.</returns>
    /// <remarks>
    /// The signal is scaled by the gain of the partitions it reaches. Those hold the tail of the filter, the
    /// part a block of input meets first, and on a filter of a million taps they are two hundred decibels down,
    /// so a full-scale input produced an answer around 1e-10 that said nothing about the device's arithmetic and
    /// was reported as a filter that could not be checked. Scaling puts the comparison back around full scale,
    /// where both precisions are at their most accurate and the tolerance means what it says.
    /// </remarks>
    private string? Verify()
    {
        int block = Stage.BlockSamples;
        int blocks = Math.Min(Stage.Partitions + 3, 96);

        // Kept well inside what a 32-bit device can hold, since the same signal is uploaded to it.
        double gain = Stage.PartitionPeak(blocks);
        double amplitude = gain > 0.0 ? Math.Clamp(1.0 / gain, 1.0, 1e18) : 1.0;
        var random = new Random(0x5EED);
        var input = new double[blocks * block];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = ((random.NextDouble() * 2.0) - 1.0) * amplitude;
        }

        var expected = new double[Stage.MaxOutput(input.Length)];
        var actual = new double[expected.Length];
        int expectedCount = Stage.CreateState(1).Process(input, expected);

        // Swept rather than fixed: the check then covers every division of the phases between the device and
        // the processor, which is what plays, instead of only the one the balance happens to start on.
        using var convolver = new SplitConvolver(this, 1, PhaseBalance.Sweeping(Stage.Up), channel: 0);
        int actualCount = convolver.Process(input, actual);
        if (actualCount != expectedCount)
        {
            throw new OpenClException($"the device produced {actualCount} samples where the processor produced {expectedCount}");
        }

        double peak = 0.0;
        double worst = 0.0;
        for (int i = 0; i < actualCount; i++)
        {
            peak = Math.Max(peak, Math.Abs(expected[i]));
            worst = Math.Max(worst, Math.Abs(actual[i] - expected[i]));
        }

        if (peak < 1e-6)
        {
            return $"the {blocks:N0} of {Stage.Partitions:N0} partitions the check can reach are " +
                $"{(gain > 0.0 ? 20.0 * Math.Log10(gain) : -999.0):0} dB down, too far for any answer to be compared";
        }

        // Relative to the loudest sample either path produced, so the check means the same at any signal level.
        double relative = worst / peak;
        double tolerance = Program.UsesDouble ? 1e-10 : 1e-5;
        if (!(relative <= tolerance))
        {
            throw new OpenClException(
                $"the device and the processor disagree by {20.0 * Math.Log10(relative):0} dB, " +
                $"more than the {20.0 * Math.Log10(tolerance):0} dB allowed at this precision");
        }

        WorstDeviation = relative;
        return null;
    }

    /// <summary>Largest disagreement with the processor during verification, relative to the loudest sample.</summary>
    public double WorstDeviation { get; private set; }

    /// <inheritdoc />
    public string? Unchecked { get; private set; }

    private static ReadOnlySpan<byte> Pack(double[] values, byte[] scratch, bool useDouble)
    {
        if (useDouble)
        {
            return MemoryMarshal.AsBytes(values.AsSpan());
        }

        Span<float> target = MemoryMarshal.Cast<byte, float>(scratch.AsSpan(0, values.Length * sizeof(float)));
        for (int i = 0; i < values.Length; i++)
        {
            target[i] = (float)values[i];
        }

        return scratch.AsSpan(0, values.Length * sizeof(float));
    }

    private void Release()
    {
        OpenClApi.ReleaseMemory(_filterRe);
        OpenClApi.ReleaseMemory(_filterIm);
        OpenClApi.ReleaseMemory(_twiddleRe);
        OpenClApi.ReleaseMemory(_twiddleIm);
        _filterRe = _filterIm = _twiddleRe = _twiddleIm = 0;
    }
}
