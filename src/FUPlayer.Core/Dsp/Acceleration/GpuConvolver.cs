using System.Runtime.InteropServices;
using FUPlayer.Core.Dsp.Resampling;

namespace FUPlayer.Core.Dsp.Acceleration;

/// <summary>
/// One channel of frequency-domain polyphase interpolation running on an OpenCL device. It buffers input into
/// blocks exactly as the processor path does and produces the same samples; only the arithmetic moves.
/// Each instance owns its command queue and kernels, so channels do not serialise against each other.
/// </summary>
/// <remarks>
/// The work is offered a phase at a time rather than a channel at a time. <see cref="Accept"/> puts the block's
/// spectrum on the device; <see cref="Begin"/> starts however many phases the device has been given and returns
/// without waiting; <see cref="Collect"/> is where the caller finally blocks. Between those last two the
/// processor is free to convolve the phases the device was not given, on the same block, at the same time.
/// That is the only arrangement in which both of them are ever busy at once.
/// </para>
/// <para>
/// The spectrum is handed over rather than computed here, because the processor has already computed it: it
/// transforms every block for its own phases whether the device is given any or not. Doing it twice cost the
/// host thirteen more commands a block (a blocking write, a pack, and log2(n) transform passes), and that,
/// rather than any arithmetic, was most of what the device charged for a block. Measured at 262,145 taps with
/// the device attached and given no phases at all, handing it every block cost 30 % of the whole pipeline.
/// </remarks>
internal sealed class GpuConvolver : IDisposable
{
    private static readonly byte[] Zeros = new byte[64 * 1024];

    /// <summary>
    /// Spectra that may be on their way to the device at once. Each needs its own pinned buffer, since the
    /// driver reads it after the call returns; three is enough that the processor never waits for one in
    /// practice, and the wait when it does is the back-pressure that keeps the queue from growing.
    /// </summary>
    private const int StagingSlots = 3;

    private readonly GpuPolyphaseFilter _filter;
    private readonly FftPolyphaseStage _stage;
    private readonly int _element;
    private readonly byte[] _transfer;
    private readonly byte[][] _staging;
    private readonly nint[] _staged;
    private readonly double[] _packed;
    private int _slot;

    private nint _queue;
    private nint _fft;
    private nint _accumulate;
    private nint _scatter;
    private nint _historyRe;
    private nint _historyIm;
    private nint _accumulatorRe;
    private nint _accumulatorIm;
    private nint _accumulatorScratchRe;
    private nint _accumulatorScratchIm;
    private nint _output;
    private int _head;
    private int _lanes;
    private int _base;

    public GpuConvolver(GpuPolyphaseFilter filter)
    {
        _filter = filter;
        _stage = filter.Stage;
        GpuProgram program = filter.Program;
        _element = program.ElementSize;

        int n = _stage.FftSize;
        int block = _stage.BlockSamples;
        int up = _stage.Up;
        _transfer = new byte[block * up * _element];

        // Pinned, because the driver reads them after the enqueue returns and a moving array would be read at
        // whatever happened to be at the old address.
        _staging = new byte[StagingSlots][];
        for (int i = 0; i < StagingSlots; i++)
        {
            _staging[i] = GC.AllocateArray<byte>(2 * n * _element, pinned: true);
        }

        _staged = new nint[StagingSlots];
        _packed = new double[block * up];

        try
        {
            _queue = OpenClApi.CreateQueue(program.Context, program.Device.Handle);
            _fft = OpenClApi.CreateKernel(program.Program, "fft_pass");
            _accumulate = OpenClApi.CreateKernel(program.Program, "accumulate");
            _scatter = OpenClApi.CreateKernel(program.Program, "scatter");

            _historyRe = Buffer(program, (long)_stage.Partitions * n);
            _historyIm = Buffer(program, (long)_stage.Partitions * n);

            // Sized for every phase even when the device is given only a few of them, so its share can be
            // changed between one block and the next without anything being allocated again.
            _accumulatorRe = Buffer(program, (long)up * n);
            _accumulatorIm = Buffer(program, (long)up * n);
            _accumulatorScratchRe = Buffer(program, (long)up * n);
            _accumulatorScratchIm = Buffer(program, (long)up * n);
            _output = Buffer(program, (long)block * up);

            // Arguments that never change: the twiddle table, the transform size and the shared filter spectra.
            OpenClApi.SetArg(_fft, 4, _filter.TwiddleRe);
            OpenClApi.SetArg(_fft, 5, _filter.TwiddleIm);
            OpenClApi.SetArg(_fft, 8, n);
            OpenClApi.SetArg(_accumulate, 0, _filter.FilterRe);
            OpenClApi.SetArg(_accumulate, 1, _filter.FilterIm);
            OpenClApi.SetArg(_accumulate, 2, _historyRe);
            OpenClApi.SetArg(_accumulate, 3, _historyIm);
            OpenClApi.SetArg(_accumulate, 6, n);
            OpenClApi.SetArg(_accumulate, 7, _stage.Partitions);
            OpenClApi.SetArg(_scatter, 0, _accumulatorRe);
            OpenClApi.SetArg(_scatter, 1, _output);
            OpenClApi.SetArg(_scatter, 2, n);
            OpenClApi.SetArg(_scatter, 3, block);
            SetReal(_scatter, 5, 1.0 / n);

            Reset();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Input samples the device consumes at a time.</summary>
    public int BlockSamples => _stage.BlockSamples;

    /// <summary>Phases the stage has in all; the device may be given any number of them.</summary>
    public int Up => _stage.Up;

    public void Reset()
    {
        _head = 0;
        _lanes = 0;
        for (int i = 0; i < StagingSlots; i++)
        {
            OpenClApi.Wait(_staged[i]);
            _staged[i] = 0;
        }

        Clear(_historyRe, (long)_stage.Partitions * _stage.FftSize * _element);
        Clear(_historyIm, (long)_stage.Partitions * _stage.FftSize * _element);
        OpenClApi.Finish(_queue);
    }

    public void Dispose()
    {
        for (int i = 0; i < StagingSlots; i++)
        {
            OpenClApi.Wait(_staged[i]);
            _staged[i] = 0;
        }

        foreach (nint buffer in new[]
                 {
                     _historyRe, _historyIm,
                     _accumulatorRe, _accumulatorIm, _accumulatorScratchRe, _accumulatorScratchIm, _output,
                 })
        {
            OpenClApi.ReleaseMemory(buffer);
        }

        _historyRe = _historyIm = 0;
        _accumulatorRe = _accumulatorIm = _accumulatorScratchRe = _accumulatorScratchIm = _output = 0;

        OpenClApi.ReleaseKernel(_fft);
        OpenClApi.ReleaseKernel(_accumulate);
        OpenClApi.ReleaseKernel(_scatter);
        _fft = _accumulate = _scatter = 0;

        OpenClApi.ReleaseQueue(_queue);
        _queue = 0;
    }

    /// <summary>
    /// Puts the spectrum of a block into the device's history. This happens whether or not the device is given
    /// any phases of that block: the history is what the next blocks are convolved against, so a device that
    /// sits a block out has a hole in it and nothing to come back to.
    /// </summary>
    /// <param name="re">Real half of the block's spectrum, as the processor has just computed it.</param>
    /// <param name="im">Imaginary half of the same.</param>
    /// <remarks>
    /// Only the half is passed, because the block is real and the other half is its conjugate; it is mirrored
    /// out here because the device's own transform works over the whole circle. Two writes and nothing else:
    /// the device used to be given the samples and transform them itself, which is the same arithmetic the
    /// processor had already done and cost thirteen more commands a block.
    /// </remarks>
    public void Accept(ReadOnlySpan<double> re, ReadOnlySpan<double> im)
    {
        int n = _stage.FftSize;
        int bins = n / 2;
        int half = n * _element;
        _head = (_head + 1) % _stage.Partitions;
        nuint offset = (nuint)((long)_head * n * _element);

        // The slot coming round again is the oldest of the three; waiting for its write is the only thing that
        // ever makes the processor wait for the device outside Collect, and only if the device is that far behind.
        _slot = (_slot + 1) % StagingSlots;
        OpenClApi.Wait(_staged[_slot]);
        _staged[_slot] = 0;

        byte[] staging = _staging[_slot];
        Stage(staging.AsSpan(0, half), re, mirror: 1.0, bins, n);
        Stage(staging.AsSpan(half, half), im, mirror: -1.0, bins, n);
        // The queue runs in order, so the second write finishing means the first has: only its event is kept. The
        // first one's is released at once, where it used to be dropped, one driver event leaked every block.
        OpenClApi.ReleaseEvent(OpenClApi.WriteAsync(_queue, _historyRe, offset, staging.AsSpan(0, half)));
        _staged[_slot] = OpenClApi.WriteAsync(_queue, _historyIm, offset, staging.AsSpan(half, half));
    }

    /// <summary>
    /// Lays a half spectrum out over the whole circle in the device's element size. Bin k above the halfway
    /// point is the conjugate of bin n − k, which is the same value for the real part and the negated one for
    /// the imaginary part, which is what <paramref name="mirror"/> selects.
    /// </summary>
    private static void Stage(Span<byte> destination, ReadOnlySpan<double> half, double mirror, int bins, int n)
    {
        if (destination.Length == n * sizeof(double))
        {
            Span<double> target = MemoryMarshal.Cast<byte, double>(destination);
            for (int k = 0; k <= bins; k++)
            {
                target[k] = half[k];
            }

            for (int k = bins + 1; k < n; k++)
            {
                target[k] = mirror * half[n - k];
            }

            return;
        }

        Span<float> single = MemoryMarshal.Cast<byte, float>(destination);
        for (int k = 0; k <= bins; k++)
        {
            single[k] = (float)half[k];
        }

        for (int k = bins + 1; k < n; k++)
        {
            single[k] = (float)(mirror * half[n - k]);
        }
    }

    /// <summary>
    /// Starts the phases <paramref name="phaseBase"/> to <paramref name="phaseBase"/> + <paramref name="lanes"/>
    /// of the block last accepted, and returns without waiting for any of it.
    /// </summary>
    public void Begin(int phaseBase, int lanes)
    {
        _base = phaseBase;
        _lanes = lanes;
        if (lanes <= 0)
        {
            return;
        }

        int n = _stage.FftSize;
        int passes = _filter.Passes;

        bool inAccumulator = (passes & 1) == 0;
        OpenClApi.SetArg(_accumulate, 4, inAccumulator ? _accumulatorRe : _accumulatorScratchRe);
        OpenClApi.SetArg(_accumulate, 5, inAccumulator ? _accumulatorIm : _accumulatorScratchIm);
        OpenClApi.SetArg(_accumulate, 8, _head);
        OpenClApi.SetArg(_accumulate, 9, phaseBase);
        OpenClApi.Run(_queue, _accumulate, (nuint)n, (nuint)lanes);

        for (int pass = 0; pass < passes; pass++)
        {
            Transform(
                inAccumulator ? _accumulatorRe : _accumulatorScratchRe, inAccumulator ? _accumulatorIm : _accumulatorScratchIm, 0,
                inAccumulator ? _accumulatorScratchRe : _accumulatorRe, inAccumulator ? _accumulatorScratchIm : _accumulatorIm, 0,
                pass, forward: false, batch: lanes);
            inAccumulator = !inAccumulator;
        }

        OpenClApi.SetArg(_scatter, 4, lanes);
        OpenClApi.Run(_queue, _scatter, (nuint)_stage.BlockSamples, (nuint)lanes);

        // Hand it to the device now rather than when something waits for it, or the two never overlap.
        OpenClApi.Flush(_queue);
    }

    /// <summary>
    /// Waits for what <see cref="Begin"/> started and spreads it into the phases it was given, leaving the
    /// phases in between for whatever filled them in.
    /// </summary>
    /// <param name="produced">Interleaved output of the whole stage: <c>sample × Up + phase</c>.</param>
    public void Collect(Span<double> produced)
    {
        if (_lanes <= 0)
        {
            return;
        }

        int block = _stage.BlockSamples;
        int up = _stage.Up;
        int lanes = _lanes;
        Download(_output, _packed.AsSpan(0, block * lanes));

        if (lanes == up)
        {
            _packed.AsSpan(0, block * up).CopyTo(produced);
            return;
        }

        for (int i = 0; i < block; i++)
        {
            int from = i * lanes;
            int to = (i * up) + _base;
            for (int lane = 0; lane < lanes; lane++)
            {
                produced[to + lane] = _packed[from + lane];
            }
        }
    }

    private void Transform(nint sourceRe, nint sourceIm, int sourceOffset, nint targetRe, nint targetIm, int targetOffset, int pass, bool forward, int batch)
    {
        OpenClApi.SetArg(_fft, 0, sourceRe);
        OpenClApi.SetArg(_fft, 1, sourceIm);
        OpenClApi.SetArg(_fft, 2, targetRe);
        OpenClApi.SetArg(_fft, 3, targetIm);
        OpenClApi.SetArg(_fft, 6, sourceOffset);
        OpenClApi.SetArg(_fft, 7, targetOffset);
        OpenClApi.SetArg(_fft, 9, 1 << pass);
        OpenClApi.SetArg(_fft, 10, pass);
        SetReal(_fft, 11, forward ? 1.0 : -1.0);
        OpenClApi.Run(_queue, _fft, (nuint)(_stage.FftSize / 2), (nuint)batch);
    }

    private void Upload(nint buffer, ReadOnlySpan<double> values)
    {
        if (_element == sizeof(double))
        {
            OpenClApi.Write(_queue, buffer, 0, MemoryMarshal.AsBytes(values));
            return;
        }

        Span<float> target = MemoryMarshal.Cast<byte, float>(_transfer.AsSpan(0, values.Length * sizeof(float)));
        for (int i = 0; i < values.Length; i++)
        {
            target[i] = (float)values[i];
        }

        OpenClApi.Write(_queue, buffer, 0, _transfer.AsSpan(0, values.Length * sizeof(float)));
    }

    private void Download(nint buffer, Span<double> values)
    {
        if (_element == sizeof(double))
        {
            OpenClApi.Read(_queue, buffer, 0, MemoryMarshal.AsBytes(values));
            return;
        }

        Span<byte> bytes = _transfer.AsSpan(0, values.Length * sizeof(float));
        OpenClApi.Read(_queue, buffer, 0, bytes);
        ReadOnlySpan<float> source = MemoryMarshal.Cast<byte, float>(bytes);
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = source[i];
        }
    }

    private void SetReal(nint kernel, uint index, double value)
    {
        if (_element == sizeof(double))
        {
            OpenClApi.SetArg(kernel, index, value);
        }
        else
        {
            OpenClApi.SetArg(kernel, index, (float)value);
        }
    }

    private nint Buffer(GpuProgram program, long elements) =>
        OpenClApi.CreateBuffer(program.Context, OpenClApi.MemReadWrite, (nuint)(elements * program.ElementSize));

    private void Clear(nint buffer, long bytes)
    {
        for (long offset = 0; offset < bytes; offset += Zeros.Length)
        {
            int chunk = (int)Math.Min(Zeros.Length, bytes - offset);
            OpenClApi.Write(_queue, buffer, (nuint)offset, Zeros.AsSpan(0, chunk));
        }
    }
}
