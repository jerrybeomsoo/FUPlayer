using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FUPlayer.Core.Audio;
using FUPlayer.Core.Dsp.Acceleration;
using FUPlayer.Core.Dsp.Design;
using FUPlayer.Core.Dsp.Numerics;

namespace FUPlayer.Core.Dsp.Resampling;

/// <summary>
/// Rational L/M polyphase FIR rate converter. The prototype lowpass runs at L·inputRate with DC gain L;
/// each output sample evaluates one phase of T = ⌈length / L⌉ taps.
/// </summary>
public sealed class PolyphaseStage : IRateStage
{
    private readonly double[][] _phases;

    public PolyphaseStage(int inputRate, int outputRate, double[] prototype, string description)
    {
        ArgumentNullException.ThrowIfNull(prototype);
        if (inputRate <= 0 || outputRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(inputRate), "Rates must be positive.");
        }

        InputRate = inputRate;
        OutputRate = outputRate;
        (Up, Down) = AudioRates.Ratio(inputRate, outputRate);
        TapsPerPhase = Math.Max(1, (prototype.Length + Up - 1) / Up);
        PrototypeLength = prototype.Length;
        Description = description;

        _phases = new double[Up][];
        for (int p = 0; p < Up; p++)
        {
            var coefficients = new double[TapsPerPhase];
            for (int k = 0; k < TapsPerPhase; k++)
            {
                int index = p + k * Up;
                coefficients[TapsPerPhase - 1 - k] = index < prototype.Length ? prototype[index] : 0.0;
            }

            _phases[p] = coefficients;
        }

        DelayOutputSamples = FirDesign.PeakIndex(prototype) / (double)Down;
    }

    public int InputRate { get; }

    public int OutputRate { get; }

    /// <summary>Interpolation factor L.</summary>
    public int Up { get; }

    /// <summary>Decimation factor M.</summary>
    public int Down { get; }

    public int TapsPerPhase { get; }

    public int PrototypeLength { get; }

    public double DelayOutputSamples { get; }

    public double CostPerOutputSample => TapsPerPhase;

    public int Taps => PrototypeLength;

    public string Description { get; }

    public int MaxOutput(int inputSamples) => (int)Math.Min(int.MaxValue, (long)inputSamples * Up / Down + 2);

    /// <summary>One phase's coefficients, oldest tap first, so an accelerator can upload the same filter.</summary>
    internal double[] Phase(int index) => _phases[index];

    public IRateStageState CreateState() => new State(this, 1);

    /// <summary>Creates state that may spread its phases over <paramref name="parallelism"/> threads.</summary>
    public IRateStageState CreateState(int parallelism) => new State(this, Math.Clamp(parallelism, 1, 16));

    /// <summary>Creates state, on an OpenCL device where one will take the filter and here where none will.</summary>
    /// <param name="parallelism">Threads the stage may use for its own phases when it stays on the processor.</param>
    /// <param name="accelerator">Optional OpenCL device.</param>
    public IRateStageState CreateState(int parallelism, GpuAccelerator? accelerator) =>
        accelerator?.TryAttach(this, parallelism) ?? CreateState(parallelism);

    /// <summary>Creates the processor-side machinery for one channel, which can be asked for any run of phases.</summary>
    /// <remarks>Only whole-number ratios divide this way; a rational one moves its phase as it goes.</remarks>
    internal DirectPhaseBank CreateBank(int parallelism)
    {
        if (Down != 1)
        {
            throw new InvalidOperationException("Only an integer ratio has a fixed phase per output sample.");
        }

        return new DirectPhaseBank(this, Math.Clamp(parallelism, 1, 16));
    }

    /// <summary>
    /// One channel's worth of tap-by-tap convolution, offered a run of phases at a time rather than all of them,
    /// so a graphics device can be doing the rest of the same samples at the same time.
    /// </summary>
    internal sealed class DirectPhaseBank
    {
        private readonly PolyphaseStage _stage;
        private readonly ParallelOptions? _parallel;
        private readonly Action<int> _filter;
        private double[] _window;
        private double[] _produced = [];
        private int _count;
        private int _first;

        public DirectPhaseBank(PolyphaseStage stage, int parallelism)
        {
            _stage = stage;
            _parallel = parallelism > 1 && stage.Up > 1
                ? new ParallelOptions { MaxDegreeOfParallelism = Math.Min(parallelism, stage.Up) }
                : null;
            _filter = Filter;
            _window = new double[stage.TapsPerPhase];
        }

        /// <summary>
        /// Narrows how many threads a block's phases may be spread over. A channel whose phases are partly on a
        /// graphics device is given the whole thread budget rather than its own share of it, because the
        /// channels the device has taken whole leave their threads idle; when the device is let go that is no
        /// longer true and every channel asking for the whole budget just oversubscribes the cores.
        /// </summary>
        /// <remarks>Called between blocks by the thread that produces them, which is the only one that reads it.</remarks>
        public void Narrow(int parallelism)
        {
            if (_parallel is not null)
            {
                _parallel.MaxDegreeOfParallelism = Math.Max(1, Math.Min(parallelism, _stage.Up));
            }
        }

        /// <summary>History the bank carries between blocks: everything an output at the block boundary still needs.</summary>
        public int History => _stage.TapsPerPhase - 1;

        public void Reset() => Array.Clear(_window);

        /// <summary>Takes the block, keeping the tail of the last one in front of it as the history.</summary>
        public void Accept(ReadOnlySpan<double> block)
        {
            int history = History;
            int needed = history + block.Length;
            if (_window.Length < needed)
            {
                Array.Resize(ref _window, needed);
            }

            block.CopyTo(_window.AsSpan(history));
            _count = block.Length;
        }

        /// <summary>Convolves <paramref name="count"/> phases from <paramref name="firstPhase"/> into the interleaved output.</summary>
        public void Produce(int firstPhase, int count, double[] produced)
        {
            if (count > 0)
            {
                _first = firstPhase;
                _produced = produced;
                if (_parallel is null || count == 1)
                {
                    for (int i = 0; i < count; i++)
                    {
                        Filter(i);
                    }
                }
                else
                {
                    Parallel.For(0, count, _parallel, _filter);
                }
            }

            // The tail of this window is the history of the next one, whoever convolved which phases of it.
            _window.AsSpan(_count, History).CopyTo(_window);
        }

        private void Filter(int index)
        {
            int phase = _first + index;
            FirKernel.Convolve(
                ref MemoryMarshal.GetArrayDataReference(_stage._phases[phase]),
                (nuint)_stage.TapsPerPhase,
                ref MemoryMarshal.GetArrayDataReference(_window),
                1,
                ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_produced), (nint)phase),
                (nuint)_stage.Up,
                _count);
        }
    }

    private sealed class State : IRateStageState
    {
        /// <summary>Multiply-adds in a block below which spreading it over threads costs more than it saves.</summary>
        private const long ParallelThreshold = 1 << 16;

        /// <summary>Output samples below which a piece of a phase is too small to be worth handing to a thread.</summary>
        private const int MinimumPiece = 16;

        private readonly PolyphaseStage _stage;
        private readonly ParallelOptions? _parallel;
        private readonly Action<int> _runTask;
        private double[] _buffer;
        private double[] _results = [];
        private int _count;
        private int _index;
        private int _phase;
        private long _skip;
        private int _written;
        private int _pieces = 1;

        public State(PolyphaseStage stage, int parallelism)
        {
            _stage = stage;
            _parallel = parallelism > 1 ? new ParallelOptions { MaxDegreeOfParallelism = parallelism } : null;
            _runTask = task => RunTask(task, _results);
            _buffer = new double[Math.Max(8192, stage.TapsPerPhase * 2)];
            Reset();
        }

        public void Reset()
        {
            int history = _stage.TapsPerPhase - 1;
            Array.Clear(_buffer, 0, history);
            _count = history;
            _index = history;
            _phase = 0;
            _skip = 0;
        }

        public int Process(ReadOnlySpan<double> input, Span<double> output)
        {
            if (_skip > 0)
            {
                int drop = (int)Math.Min(_skip, input.Length);
                input = input[drop..];
                _skip -= drop;
            }

            int taps = _stage.TapsPerPhase;
            int up = _stage.Up;
            int down = _stage.Down;
            EnsureCapacity(_count + input.Length);
            input.CopyTo(_buffer.AsSpan(_count));
            _count += input.Length;

            // Output sample n uses phase (phase0 + n·down) mod up and ends at input sample
            // index0 + (phase0 + n·down) div up, so the whole block can be counted without walking it: samples
            // come out while that end still lies within what has arrived.
            long room = ((long)(_count - _index) * up) - _phase;
            int written = room <= 0 ? 0 : (int)((room + down - 1) / down);
            if (written > output.Length)
            {
                throw new ArgumentException(
                    $"{_stage.Description} produces {written} samples from {input.Length}, more than the {output.Length} the caller left room for.",
                    nameof(output));
            }

            if (written > 0)
            {
                Convolve(written, output);
            }

            long advance = (long)_phase + (long)written * down;
            int index = _index + (int)(advance / up);
            int phase = (int)(advance % up);

            int keepStart = index - (taps - 1);
            if (keepStart >= _count)
            {
                // The next window lies entirely in the future: discard the samples in between.
                _skip = keepStart - _count;
                _count = 0;
                _index = taps - 1;
            }
            else
            {
                int keep = _count - keepStart;
                Array.Copy(_buffer, keepStart, _buffer, 0, keep);
                _count = keep;
                _index = index - keepStart;
            }

            _phase = phase;
            return written;
        }

        /// <summary>
        /// Convolves the block one phase at a time. Output samples that share a phase are every up-th one and
        /// their windows sit exactly down input samples apart, so a phase is one sweep of its coefficients over
        /// evenly spaced windows, which is what keeps a long filter's coefficients in cache. Phases are also
        /// independent of one another, so they are what the threads divide between them.
        /// </summary>
        private void Convolve(int written, Span<double> output)
        {
            int slots = Math.Min(_stage.Up, written);
            _written = written;

            // A short filter is not worth handing round: waking the threads would cost more than the arithmetic.
            ParallelOptions? spread = (long)written * _stage.TapsPerPhase >= ParallelThreshold ? _parallel : null;
            int threads = spread?.MaxDegreeOfParallelism ?? 1;

            // Usually there are more phases than threads and a thread simply takes a phase. Where a conversion has
            // fewer phases than that, each phase's run is cut further, down to pieces still worth handing over.
            int perSlot = (written + slots - 1) / slots;
            _pieces = Math.Clamp(Math.Min(threads / slots, perSlot / MinimumPiece), 1, threads);

            int tasks = slots * _pieces;
            if (spread is null || tasks == 1)
            {
                for (int task = 0; task < tasks; task++)
                {
                    RunTask(task, output);
                }

                return;
            }

            // Threads cannot be handed the caller's span, so they fill an array of our own and it is copied over.
            if (_results.Length < written)
            {
                _results = new double[(int)BitOperations.RoundUpToPowerOf2((uint)written)];
            }

            Parallel.For(0, tasks, spread, _runTask);
            _results.AsSpan(0, written).CopyTo(output);
        }

        /// <summary>One phase's coefficients over its share of the output samples that use them.</summary>
        private void RunTask(int task, Span<double> destination)
        {
            int taps = _stage.TapsPerPhase;
            int up = _stage.Up;
            int down = _stage.Down;

            int slot = task / _pieces;
            long position = (long)_phase + (long)slot * down;
            int phase = (int)(position % up);
            int window = _index + (int)(position / up) - taps + 1;

            int total = (_written - slot + up - 1) / up;
            int length = (total + _pieces - 1) / _pieces;
            int first = (task % _pieces) * length;
            int count = Math.Min(length, total - first);
            if (count <= 0)
            {
                return;
            }

            FirKernel.Convolve(
                ref MemoryMarshal.GetArrayDataReference(_stage._phases[phase]),
                (nuint)taps,
                ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_buffer), (nint)window + (nint)first * down),
                (nuint)down,
                ref Unsafe.Add(ref MemoryMarshal.GetReference(destination), (nint)slot + (nint)first * up),
                (nuint)up,
                count);
        }

        private void EnsureCapacity(int required)
        {
            if (required > _buffer.Length)
            {
                Array.Resize(ref _buffer, (int)Math.Min(int.MaxValue, BitOperations.RoundUpToPowerOf2((uint)required)));
            }
        }
    }
}
