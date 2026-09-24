using System.Runtime.InteropServices;
using FUPlayer.Core.Dsp.Resampling;

namespace FUPlayer.Core.Dsp.Acceleration;

/// <summary>
/// One channel of tap-by-tap polyphase interpolation running on an OpenCL device. It keeps the same history the
/// processor path keeps and produces the same samples from the same coefficients; only the arithmetic moves, and
/// unlike the frequency-domain path it holds nothing back, so the stage adds no delay of its own.
/// Each instance owns its command queue and kernel, so channels do not serialise against each other.
/// </summary>
/// <remarks>
/// As with the frequency-domain path, the work is offered a phase at a time: <see cref="Accept"/> gets the input
/// onto the device, <see cref="Begin"/> starts the phases it has been given without waiting for them, and
/// <see cref="Collect"/> is the only step that blocks. The processor convolves the remaining phases of the same
/// samples in between.
///
/// The history stays on the device. A window is the history followed by the block, and two of them alternate: each
/// new block's history is the tail of the window before, moved there by a copy on the device, so only the block's own
/// samples cross the bus. Sending the whole window instead sends <c>TapsPerPhase − 1</c> samples of history with every
/// block, which for a filter of 8,917 taps a phase is 13,012 samples to deliver 4,096.
/// </remarks>
internal sealed class GpuDirectConvolver : IDisposable
{
    /// <summary>
    /// Input samples per launch. A block of this size is tens of thousands of output samples of tens of thousands
    /// of taps each, which is work enough to fill any device, while keeping the buffers on it modest.
    /// </summary>
    public const int MaxBlock = 4096;

    private readonly GpuDirectFilter _filter;
    private readonly PolyphaseStage _stage;
    private readonly int _element;
    private readonly int _history;
    private readonly double[] _silence;
    private readonly double[] _packed;
    private readonly byte[] _transfer;

    private nint _queue;
    private nint _kernel;

    // The two windows, history then block. Two, because a copy between overlapping ranges of one buffer is undefined.
    private readonly nint[] _windows = new nint[2];
    private int _current;
    private nint _output;
    private int _count;
    private int _lanes;
    private int _base;

    public GpuDirectConvolver(GpuDirectFilter filter)
    {
        _filter = filter;
        _stage = filter.Stage;
        GpuProgram program = filter.Program;
        _element = program.ElementSize;
        _history = _stage.TapsPerPhase - 1;
        _silence = new double[_history];
        long window = (long)_history + MaxBlock;

        int outputs = MaxBlock * _stage.Up;
        _packed = new double[outputs];
        _transfer = program.UsesDouble ? [] : new byte[Math.Max(Math.Max(_history, MaxBlock), outputs) * sizeof(float)];

        try
        {
            _queue = OpenClApi.CreateQueue(program.Context, program.Device.Handle);
            _kernel = OpenClApi.CreateKernel(program.Program, "direct");
            for (int i = 0; i < _windows.Length; i++)
            {
                _windows[i] = OpenClApi.CreateBuffer(program.Context, OpenClApi.MemReadOnly, (nuint)(window * _element));
            }

            _output = OpenClApi.CreateBuffer(
                program.Context, OpenClApi.MemWriteOnly, (nuint)((long)outputs * _element));

            OpenClApi.SetArg(_kernel, 0, filter.Coefficients);
            OpenClApi.SetArg(_kernel, 1, _windows[0]);
            OpenClApi.SetArg(_kernel, 2, _output);
            OpenClApi.SetLocalArg(_kernel, 3, (nuint)(filter.GroupSize * _element));
            OpenClApi.SetArg(_kernel, 4, _stage.TapsPerPhase);

            Reset();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Phases the stage has in all; the device may be given any number of them.</summary>
    public int Up => _stage.Up;

    /// <summary>Starts again from silence: the next block is convolved against a history of zeros.</summary>
    public void Reset()
    {
        _current = 0;
        _lanes = 0;
        _count = 0;
        if (_history > 0)
        {
            Upload(_silence, _windows[0], 0);
        }
    }

    public void Dispose()
    {
        for (int i = 0; i < _windows.Length; i++)
        {
            OpenClApi.ReleaseMemory(_windows[i]);
            _windows[i] = 0;
        }

        OpenClApi.ReleaseMemory(_output);
        _output = 0;
        OpenClApi.ReleaseKernel(_kernel);
        _kernel = 0;
        OpenClApi.ReleaseQueue(_queue);
        _queue = 0;
    }

    /// <summary>
    /// Puts the block onto the device behind the history it is convolved against. This happens whether or not the
    /// device is given any phases of it: the history is what the samples after it are convolved against.
    /// </summary>
    public void Accept(ReadOnlySpan<double> block)
    {
        // The last TapsPerPhase − 1 samples of the window before (its history and block together) are this block's
        // history. The queue is in order, so the copy follows the launch that read that window, and the launch below
        // follows the copy and the write.
        int next = 1 - _current;
        if (_history > 0)
        {
            OpenClApi.Copy(
                _queue, _windows[_current], (nuint)((long)_count * _element),
                _windows[next], 0, (nuint)((long)_history * _element));
        }

        Upload(block, _windows[next], (nuint)((long)_history * _element));
        OpenClApi.SetArg(_kernel, 1, _windows[next]);
        _current = next;
        _count = block.Length;
    }

    /// <summary>Starts the given phases of the block last accepted, and returns without waiting for them.</summary>
    public void Begin(int phaseBase, int lanes)
    {
        _base = phaseBase;
        _lanes = lanes;
        if (lanes <= 0 || _count <= 0)
        {
            _lanes = 0;
            return;
        }

        int group = _filter.GroupSize;
        OpenClApi.SetArg(_kernel, 5, lanes);
        OpenClApi.SetArg(_kernel, 6, _count);
        OpenClApi.SetArg(_kernel, 7, phaseBase);
        OpenClApi.Run(
            _queue, _kernel, (nuint)((_count + group - 1) / group * group), (nuint)lanes, (nuint)group);

        // Hand it to the device now rather than when something waits for it, or the two never overlap.
        OpenClApi.Flush(_queue);
    }

    /// <summary>Waits for what <see cref="Begin"/> started and spreads it into the phases it was given.</summary>
    /// <param name="produced">Interleaved output: <c>sample × Up + phase</c>.</param>
    public void Collect(double[] produced)
    {
        if (_lanes <= 0)
        {
            return;
        }

        int lanes = _lanes;
        int up = _stage.Up;
        Download(_packed.AsSpan(0, _count * lanes));

        if (lanes == up)
        {
            _packed.AsSpan(0, _count * up).CopyTo(produced);
            return;
        }

        for (int i = 0; i < _count; i++)
        {
            int from = i * lanes;
            int to = (i * up) + _base;
            for (int lane = 0; lane < lanes; lane++)
            {
                produced[to + lane] = _packed[from + lane];
            }
        }
    }

    private void Upload(ReadOnlySpan<double> values, nint buffer, nuint offsetBytes)
    {
        if (values.IsEmpty)
        {
            return;
        }

        if (_element == sizeof(double))
        {
            OpenClApi.Write(_queue, buffer, offsetBytes, MemoryMarshal.AsBytes(values));
            return;
        }

        Span<float> target = MemoryMarshal.Cast<byte, float>(_transfer.AsSpan(0, values.Length * sizeof(float)));
        for (int i = 0; i < values.Length; i++)
        {
            target[i] = (float)values[i];
        }

        OpenClApi.Write(_queue, buffer, offsetBytes, _transfer.AsSpan(0, values.Length * sizeof(float)));
    }

    private void Download(Span<double> values)
    {
        if (_element == sizeof(double))
        {
            OpenClApi.Read(_queue, _output, 0, MemoryMarshal.AsBytes(values));
            return;
        }

        Span<byte> bytes = _transfer.AsSpan(0, values.Length * sizeof(float));
        OpenClApi.Read(_queue, _output, 0, bytes);
        ReadOnlySpan<float> source = MemoryMarshal.Cast<byte, float>(bytes);
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = source[i];
        }
    }
}
