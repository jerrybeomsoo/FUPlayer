using System.Runtime.InteropServices;
using FUPlayer.Core.Dsp.Numerics;
using FUPlayer.Core.Dsp.Resampling;

namespace FUPlayer.Core.Dsp.Acceleration;

/// <summary>
/// The device-side half of one tap-by-tap <see cref="PolyphaseStage"/>: the phase coefficients, uploaded once and
/// shared by every channel's <see cref="GpuDirectConvolver"/>.
/// </summary>
internal sealed class GpuDirectFilter : IGpuFilter
{
    /// <summary>Output samples a work group computes together, sharing one pass over the coefficients.</summary>
    private const int PreferredGroup = 256;

    private nint _coefficients;

    private GpuDirectFilter(PolyphaseStage stage, GpuProgram program)
    {
        Stage = stage;
        Program = program;

        long bytes = (long)stage.Up * stage.TapsPerPhase * program.ElementSize;
        if (bytes > program.Device.MaxAllocationBytes)
        {
            throw new OpenClException(
                $"{stage.Taps:N0} taps need a {bytes / (1024 * 1024)} MB buffer, more than the " +
                $"{program.Device.MaxAllocationBytes / (1024 * 1024)} MB this device allows in one allocation.");
        }

        int maxGroup = (int)OpenClApi.GetDeviceValue<nuint>(program.Device.Handle, OpenClApi.DeviceMaxWorkGroupSize);
        long localBytes = OpenClApi.GetDeviceValue<ulong>(program.Device.Handle, OpenClApi.DeviceLocalMemory) is var l and > 0
            ? (long)l
            : 16 * 1024;
        int byMemory = (int)(localBytes / program.ElementSize);
        GroupSize = Math.Max(4, RoundDownToPowerOfTwo(Math.Min(Math.Min(PreferredGroup, maxGroup <= 0 ? PreferredGroup : maxGroup), byMemory)));

        nint queue = OpenClApi.CreateQueue(program.Context, program.Device.Handle);
        try
        {
            _coefficients = OpenClApi.CreateBuffer(program.Context, OpenClApi.MemReadOnly, (nuint)bytes);
            var scratch = new byte[stage.TapsPerPhase * program.ElementSize];
            for (int phase = 0; phase < stage.Up; phase++)
            {
                nuint offset = (nuint)((long)phase * stage.TapsPerPhase * program.ElementSize);
                OpenClApi.Write(queue, _coefficients, offset, Pack(stage.Phase(phase), scratch, program.UsesDouble));
            }

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

    public PolyphaseStage Stage { get; }

    public GpuProgram Program { get; }

    /// <summary>Work items per group, which is also how many coefficients are staged at a time.</summary>
    public int GroupSize { get; }

    public nint Coefficients => _coefficients;

    /// <summary>Largest disagreement with the processor during verification, relative to the loudest sample.</summary>
    public double WorstDeviation { get; private set; }

    /// <inheritdoc />
    public string? Unchecked { get; private set; }

    /// <summary>
    /// Whether tap by tap on a device applies to this stage at all. Only whole-number interpolation, where the
    /// output samples of one phase march through the input one sample at a time: the kernel cannot do a rational
    /// ratio, and no setting changes that. The length is a judgement rather than a limit: below about a thousand
    /// taps a phase the round trip costs more than the arithmetic saves, so forcing the device sets it aside.
    /// </summary>
    public static bool Suits(PolyphaseStage stage, bool force) =>
        stage.Down == 1 && (force || stage.TapsPerPhase >= 1024);

    /// <summary>
    /// Uploads the stage and proves the device reproduces the processor's own answer before anything is played
    /// through it. A device that answers differently is not used at all.
    /// </summary>
    /// <param name="allowUnchecked">
    /// Keep the filter even where the comparison could not be made at all, which is what forcing the device
    /// asks for. A disagreement still sends the stage back to the processor either way.
    /// </param>
    public static GpuDirectFilter CreateVerified(
        PolyphaseStage stage, GpuProgram program, bool allowUnchecked = false)
    {
        var filter = new GpuDirectFilter(stage, program);
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
    /// Runs noise through both paths and compares them. The signal has to be longer than the filter before any
    /// output sample carries the whole of it. A shorter run only touches the far tail, where the coefficients
    /// are 200 dB down and any two answers agree, so the check reads the samples after that point, computed
    /// here by the same kernel the processor path uses.
    /// </summary>
    /// <returns>Why the two could not be compared, or null when they were and agreed.</returns>
    private string? Verify()
    {
        int taps = Stage.TapsPerPhase;
        int up = Stage.Up;
        const int engaged = 192;

        var random = new Random(0x5EED);
        var signal = new double[taps + engaged];
        for (int i = 0; i < signal.Length; i++)
        {
            signal[i] = random.NextDouble() * 2.0 - 1.0;
        }

        var actual = new double[(long)signal.Length * up];
        int produced;
        using (var convolver = new SplitDirectConvolver(this, 1, PhaseBalance.Sweeping(Stage.Up), channel: 0))
        {
            produced = convolver.Process(signal, actual);
        }

        if (produced != actual.Length)
        {
            throw new OpenClException($"the device produced {produced} samples where {actual.Length} were expected");
        }

        // Output group g reads input samples g − taps + 1 to g, so the first group whose window lies wholly
        // inside the signal, and therefore carries the whole filter, is the one at index taps - 1.
        double peak = 0.0;
        double worst = 0.0;
        var expected = new double[engaged];
        for (int phase = 0; phase < up; phase++)
        {
            double[] coefficients = Stage.Phase(phase);
            FirKernel.Convolve(ref coefficients[0], (nuint)taps, ref signal[1], 1, ref expected[0], 1, engaged);
            for (int j = 0; j < engaged; j++)
            {
                peak = Math.Max(peak, Math.Abs(expected[j]));
                worst = Math.Max(worst, Math.Abs(actual[((taps + j) * up) + phase] - expected[j]));
            }
        }

        if (peak < 1e-6)
        {
            return "the comparison signal never reached the device; the filter cannot be checked";
        }

        // Relative to the loudest sample either path produced, so the check means the same at any signal level.
        double relative = worst / peak;
        double tolerance = Program.UsesDouble ? 1e-10 : 1e-4;
        if (!(relative <= tolerance))
        {
            throw new OpenClException(
                $"the device and the processor disagree by {20.0 * Math.Log10(relative):0} dB, " +
                $"more than the {20.0 * Math.Log10(tolerance):0} dB allowed at this precision");
        }

        WorstDeviation = relative;
        return null;
    }

    private static int RoundDownToPowerOfTwo(int value) =>
        value <= 1 ? 1 : 1 << (31 - System.Numerics.BitOperations.LeadingZeroCount((uint)value));

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
        OpenClApi.ReleaseMemory(_coefficients);
        _coefficients = 0;
    }
}
