using System.Numerics;
using FUPlayer.Core.Audio;
using FUPlayer.Core.Dsp.Design;
using FUPlayer.Core.Dsp.Numerics;

namespace FUPlayer.Core.Dsp.Resampling;

/// <summary>
/// Integer-ratio polyphase interpolator that convolves in the frequency domain with blocks of several lengths:
/// short ones for the taps whose answer is due immediately, longer ones for taps far enough back that they can
/// afford to wait. It produces exactly the samples <see cref="PolyphaseStage"/> and <see cref="FftPolyphaseStage"/>
/// produce, and holds input for only as long as its shortest block.
/// </summary>
/// <remarks>
/// <para>
/// A uniform frequency-domain stage has one block length and it does two jobs at once: it is how long the stage
/// waits before it answers, and it is how many taps one partition multiply covers. The cheapest block for the
/// second job is thousands of samples, and that becomes tens of milliseconds of waiting for nothing. The block
/// changes no output sample, only when it appears.
/// </para>
/// <para>
/// Splitting the two jobs is what this stage is for. <see cref="PartitionPlan"/> works out the bands; each fires
/// on its own schedule, writes its contribution into a shared ring of pending output, and the stage hands back
/// the head block's worth that is complete. What it gives up is the graphics device, which takes uniform stages
/// only, and an even amount of work per block: a long band firing costs a large transform all at once.
/// </para>
/// </remarks>
public sealed class LayeredFftPolyphaseStage : IRateStage
{
    private readonly PartitionLevel[] _levels;
    private readonly RealFftPlan[] _transforms;

    /// <summary>Spectra of the filter partitions, indexed [band][phase * Count + partition][bin].</summary>
    private readonly double[][][] _partitionRe;
    private readonly double[][][] _partitionIm;

    /// <param name="name">Stage name; the taps and the layout are appended to it.</param>
    /// <param name="plan">Band layout, from <see cref="PartitionPlan.Choose"/>.</param>
    public LayeredFftPolyphaseStage(int inputRate, int outputRate, double[] prototype, string name, PartitionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(prototype);
        ArgumentNullException.ThrowIfNull(plan);
        if (inputRate <= 0 || outputRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(inputRate), "Rates must be positive.");
        }

        (int up, int down) = AudioRates.Ratio(inputRate, outputRate);
        if (down != 1)
        {
            throw new ArgumentException("Frequency-domain conversion needs an integer rate ratio.", nameof(outputRate));
        }

        // The input history and the pending output are addressed by masking, which every band's block has to
        // divide exactly; the plan's own blocks are powers of two of the head, so only the head needs checking.
        if (plan.HeadBlock < 1 || (plan.HeadBlock & (plan.HeadBlock - 1)) != 0)
        {
            throw new ArgumentException("The head block must be a power of two.", nameof(plan));
        }

        InputRate = inputRate;
        OutputRate = outputRate;
        Up = up;
        PrototypeLength = prototype.Length;
        TapsPerPhase = Math.Max(1, (prototype.Length + up - 1) / up);
        Plan = plan;
        _levels = [.. plan.Levels];

        _transforms = new RealFftPlan[_levels.Length];
        _partitionRe = new double[_levels.Length][][];
        _partitionIm = new double[_levels.Length][][];
        for (int level = 0; level < _levels.Length; level++)
        {
            PartitionLevel band = _levels[level];
            _transforms[level] = new RealFftPlan(band.FftSize);
            _partitionRe[level] = new double[up * band.Count][];
            _partitionIm[level] = new double[up * band.Count][];
        }

        // Transforming the partitions is the slow part of designing a long filter and the phases do not depend on
        // one another, so they are shared out, exactly as the uniform stage does it.
        if ((long)up * TapsPerPhase > 1 << 20)
        {
            Parallel.For(0, up, BuildPhase);
        }
        else
        {
            for (int phase = 0; phase < up; phase++)
            {
                BuildPhase(phase);
            }
        }

        void BuildPhase(int phase)
        {
            for (int level = 0; level < _levels.Length; level++)
            {
                PartitionLevel band = _levels[level];
                var taps = new double[band.FftSize];
                for (int part = 0; part < band.Count; part++)
                {
                    Array.Clear(taps);

                    // The taps go in at the front of the transform, not at their own lag: the lag is carried by
                    // which spectrum the partition is multiplied with and by where the result is added.
                    for (int k = 0; k < band.Block; k++)
                    {
                        int tap = band.Offset + (part * band.Block) + k;
                        int index = phase + (tap * up);
                        taps[k] = tap < TapsPerPhase && index < prototype.Length ? prototype[index] : 0.0;
                    }

                    var re = new double[band.Block + 1];
                    var im = new double[band.Block + 1];
                    _transforms[level].Forward(taps, re, im);
                    _partitionRe[level][(phase * band.Count) + part] = re;
                    _partitionIm[level][(phase * band.Count) + part] = im;
                }
            }
        }

        DelayOutputSamples = FirDesign.PeakIndex(prototype);
        Description = $"{name} ({PrototypeLength:N0} taps, {TapsPerPhase:N0} per phase, {plan})";
        CostPerOutputSample = plan.CostPerOutputSample;
    }

    public int InputRate { get; }

    public int OutputRate { get; }

    /// <summary>Interpolation factor.</summary>
    public int Up { get; }

    public int TapsPerPhase { get; }

    public int PrototypeLength { get; }

    /// <summary>How the taps are divided into bands.</summary>
    public PartitionPlan Plan { get; }

    /// <summary>Input samples per processed block: the shortest block, which is what sets the wait.</summary>
    public int BlockSamples => Plan.HeadBlock;

    public double DelayOutputSamples { get; }

    public double CostPerOutputSample { get; }

    /// <summary>Tap-at-a-time cost of the same filter, for comparison.</summary>
    public double DirectCostPerOutputSample => TapsPerPhase;

    public int Taps => PrototypeLength;

    public int FlushInputSamples => Plan.HeadBlock;

    public string Description { get; }

    public int MaxOutput(int inputSamples) => ((inputSamples / Plan.HeadBlock) + 1) * Plan.HeadBlock * Up;

    public IRateStageState CreateState() => CreateState(1);

    /// <summary>Creates state that may spread the phases of a firing band over <paramref name="parallelism"/> threads.</summary>
    public IRateStageState CreateState(int parallelism) => new State(this, Math.Clamp(parallelism, 1, 16));

    private sealed class State : BlockStageState
    {
        private readonly LayeredFftPolyphaseStage _stage;
        private readonly ParallelOptions? _parallel;

        /// <summary>Input history, long enough for the oldest window any band still has to transform.</summary>
        private readonly double[] _ring;
        private readonly int _ringMask;

        /// <summary>Output owed but not yet due, per phase; a band writes ahead of the block being emitted.</summary>
        private readonly double[][] _pending;
        private readonly int _pendingMask;

        private readonly double[][][] _historyRe;
        private readonly double[][][] _historyIm;
        private readonly int[] _head;
        private readonly double[] _window;
        private readonly double[][] _accumulatorRe;
        private readonly double[][] _accumulatorIm;
        private readonly double[][] _samples;
        private long _consumed;
        private int _firing;

        public State(LayeredFftPolyphaseStage stage, int parallelism)
            : base(stage.Plan.HeadBlock, stage.Plan.HeadBlock * stage.Up)
        {
            _stage = stage;
            _parallel = parallelism > 1 && stage.Up > 1
                ? new ParallelOptions { MaxDegreeOfParallelism = Math.Min(parallelism, stage.Up) }
                : null;

            PartitionLevel[] levels = stage._levels;
            int widest = levels[^1].Block;

            // A band firing at the newest tick transforms the window that ends offset + head samples ago, and that
            // window is two of its own blocks long. The oldest sample any of them still wants sets the history.
            long reach = 0;
            foreach (PartitionLevel band in levels)
            {
                reach = Math.Max(reach, (long)band.Offset + band.Block + stage.Plan.HeadBlock);
            }

            _ring = new double[FftPlan.NextPowerOfTwo(reach)];
            _ringMask = _ring.Length - 1;

            // Nothing reaches further ahead than the longest block, which starts at the block being emitted.
            _pending = new double[stage.Up][];
            int pendingSize = FftPlan.NextPowerOfTwo(widest);
            for (int phase = 0; phase < stage.Up; phase++)
            {
                _pending[phase] = new double[pendingSize];
            }

            _pendingMask = pendingSize - 1;

            _historyRe = new double[levels.Length][][];
            _historyIm = new double[levels.Length][][];
            _head = new int[levels.Length];
            for (int level = 0; level < levels.Length; level++)
            {
                PartitionLevel band = levels[level];
                _historyRe[level] = new double[band.Count][];
                _historyIm[level] = new double[band.Count][];
                for (int slot = 0; slot < band.Count; slot++)
                {
                    _historyRe[level][slot] = new double[band.Block + 1];
                    _historyIm[level][slot] = new double[band.Block + 1];
                }
            }

            _window = new double[2 * widest];
            _accumulatorRe = new double[stage.Up][];
            _accumulatorIm = new double[stage.Up][];
            _samples = new double[stage.Up][];
            for (int phase = 0; phase < stage.Up; phase++)
            {
                _accumulatorRe[phase] = new double[widest + 1];
                _accumulatorIm[phase] = new double[widest + 1];
                _samples[phase] = new double[2 * widest];
            }

            Reset();
        }

        protected override void ResetState()
        {
            Array.Clear(_ring);
            foreach (double[] phase in _pending)
            {
                Array.Clear(phase);
            }

            for (int level = 0; level < _historyRe.Length; level++)
            {
                for (int slot = 0; slot < _historyRe[level].Length; slot++)
                {
                    Array.Clear(_historyRe[level][slot]);
                    Array.Clear(_historyIm[level][slot]);
                }

                _head[level] = 0;
            }

            _consumed = 0;
        }

        protected override void ProcessBlock()
        {
            int head = _stage.Plan.HeadBlock;
            int up = _stage.Up;
            PartitionLevel[] levels = _stage._levels;

            // The ring is a whole number of head blocks long, so the new block never straddles its end.
            Current.CopyTo(_ring.AsSpan((int)(_consumed & _ringMask)));
            long tick = _consumed / head;

            for (int level = 0; level < levels.Length; level++)
            {
                PartitionLevel band = levels[level];
                long blocks = band.Block / head;
                long start = band.Offset / head;

                // A band fires once per block of its own, timed so that what it computes is the output starting
                // exactly here. Before its first tap has any input behind it there is nothing for it to add.
                if (tick < start || (tick - start) % blocks != 0)
                {
                    continue;
                }

                _firing = level;
                long index = (tick - start) / blocks;
                Gather((index - 1) * band.Block, band.FftSize);
                _head[level] = (_head[level] + 1) % band.Count;
                _stage._transforms[level].Forward(
                    _window.AsSpan(0, band.FftSize), _historyRe[level][_head[level]], _historyIm[level][_head[level]]);

                if (_parallel is null)
                {
                    for (int phase = 0; phase < up; phase++)
                    {
                        Filter(phase);
                    }
                }
                else
                {
                    Parallel.For(0, up, _parallel, Filter);
                }
            }

            // Hand back the block that is now complete and leave its slots clear for the bands reaching past it.
            for (int n = 0; n < head; n++)
            {
                int slot = (int)((_consumed + n) & _pendingMask);
                for (int phase = 0; phase < up; phase++)
                {
                    Produced[(n * up) + phase] = _pending[phase][slot];
                    _pending[phase][slot] = 0.0;
                }
            }

            _consumed += head;
        }

        /// <summary>Copies <paramref name="size"/> input samples from <paramref name="from"/> into the window, zeroing anything before the start of the stream.</summary>
        private void Gather(long from, int size)
        {
            int at = 0;
            if (from < 0)
            {
                int lead = (int)Math.Min(-from, size);
                Array.Clear(_window, 0, lead);
                at = lead;
                from += lead;
            }

            while (at < size)
            {
                int position = (int)(from & _ringMask);
                int take = Math.Min(size - at, _ring.Length - position);
                Array.Copy(_ring, position, _window, at, take);
                at += take;
                from += take;
            }
        }

        /// <summary>Multiplies the firing band's partitions for one phase and adds the result where it is due.</summary>
        private void Filter(int phase)
        {
            int level = _firing;
            PartitionLevel band = _stage._levels[level];
            int bins = band.Block + 1;
            int count = band.Count;
            double[] accRe = _accumulatorRe[phase];
            double[] accIm = _accumulatorIm[phase];
            Array.Clear(accRe, 0, bins);
            Array.Clear(accIm, 0, bins);

            for (int part = 0; part < count; part++)
            {
                double[] hRe = _stage._partitionRe[level][(phase * count) + part];
                double[] hIm = _stage._partitionIm[level][(phase * count) + part];
                int slot = (_head[level] - part + count) % count;
                double[] xRe = _historyRe[level][slot];
                double[] xIm = _historyIm[level][slot];

                int i = 0;
                int width = Vector<double>.Count;
                for (; i <= bins - width; i += width)
                {
                    var ar = new Vector<double>(xRe, i);
                    var ai = new Vector<double>(xIm, i);
                    var br = new Vector<double>(hRe, i);
                    var bi = new Vector<double>(hIm, i);
                    (new Vector<double>(accRe, i) + ((ar * br) - (ai * bi))).CopyTo(accRe, i);
                    (new Vector<double>(accIm, i) + ((ar * bi) + (ai * br))).CopyTo(accIm, i);
                }

                for (; i < bins; i++)
                {
                    double ar = xRe[i];
                    double ai = xIm[i];
                    accRe[i] += (ar * hRe[i]) - (ai * hIm[i]);
                    accIm[i] += (ar * hIm[i]) + (ai * hRe[i]);
                }
            }

            double[] samples = _samples[phase];
            _stage._transforms[level].Inverse(accRe, accIm, samples);

            // The second half of a circular convolution of two half-filled blocks is the linear result, and it
            // belongs to the output starting at this block. For a long band, most of it is still in the future.
            double[] pending = _pending[phase];
            for (int n = 0; n < band.Block; n++)
            {
                pending[(int)((_consumed + n) & _pendingMask)] += samples[band.Block + n];
            }
        }
    }
}
