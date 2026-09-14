using System.Numerics;
using FUPlayer.Core.Audio;
using FUPlayer.Core.Dsp.Acceleration;
using FUPlayer.Core.Dsp.Design;
using FUPlayer.Core.Dsp.Numerics;

namespace FUPlayer.Core.Dsp.Resampling;

/// <summary>
/// Integer-ratio polyphase interpolator that convolves in the frequency domain (uniformly partitioned overlap-save).
/// It produces the same samples as <see cref="PolyphaseStage"/> within rounding, but its cost per output sample grows
/// with the logarithm of the block size instead of with the number of taps, which is what makes filters of tens or
/// hundreds of thousands of taps run in real time.
/// </summary>
public sealed class FftPolyphaseStage : IRateStage
{
    /// <summary>Longest block the stage will buffer, whatever the filter's length.</summary>
    public const int MaxBlockSamples = 1 << 16;

    /// <summary>Shortest block the stage will use; below this the transforms cost more than they save.</summary>
    public const int MinBlockSamples = 64;

    /// <summary>Taps per phase from which the frequency domain takes over when nothing else is asked for.</summary>
    public const int DefaultThresholdTapsPerPhase = 1024;

    /// <summary>
    /// A block is allowed to grow to this fraction of the filter's own span, so the delay it adds stays a few
    /// per cent of the delay the filter already has.
    /// </summary>
    private const int BlockShareOfFilter = 32;

    /// <summary>
    /// A block is always allowed to be at least this long, however short the filter. Below it the transforms,
    /// which cost one forward and one per phase however little audio the block carries, start to dominate.
    /// </summary>
    private const double MinimumBlockMilliseconds = 50.0;

    private readonly RealFftPlan _plan;

    /// <summary>Spectra of the filter partitions, indexed [phase * Partitions + partition][bin].</summary>
    private readonly double[][] _partitionRe;
    private readonly double[][] _partitionIm;

    /// <param name="name">Stage name; the taps and transform size are appended to it.</param>
    public FftPolyphaseStage(int inputRate, int outputRate, double[] prototype, string name)
        : this(inputRate, outputRate, prototype, name, default)
    {
    }

    /// <param name="name">Stage name; the taps and transform size are appended to it.</param>
    /// <param name="options">Crossover and block-length tuning; the defaults let the stage choose.</param>
    public FftPolyphaseStage(int inputRate, int outputRate, double[] prototype, string name, ConvolutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(prototype);
        if (inputRate <= 0 || outputRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(inputRate), "Rates must be positive.");
        }

        (int up, int down) = AudioRates.Ratio(inputRate, outputRate);
        if (down != 1)
        {
            throw new ArgumentException("Frequency-domain conversion needs an integer rate ratio.", nameof(outputRate));
        }

        InputRate = inputRate;
        OutputRate = outputRate;
        Up = up;
        PrototypeLength = prototype.Length;
        TapsPerPhase = Math.Max(1, (prototype.Length + up - 1) / up);

        BlockSamples = ChooseBlock(TapsPerPhase, up, inputRate, options.MaxBlockMilliseconds);
        Partitions = (TapsPerPhase + BlockSamples - 1) / BlockSamples;
        _plan = new RealFftPlan(2 * BlockSamples);

        // Only the lower half of each spectrum is kept. Everything transformed here comes from real samples, so
        // the upper half is the conjugate of the lower one and carries no information: storing and multiplying it
        // would double both the memory and the work that grows with the filter's length for nothing.
        _partitionRe = new double[up * Partitions][];
        _partitionIm = new double[up * Partitions][];

        // Transforming the partitions is the slow part of designing a long filter, seconds of it, and the
        // phases do not depend on one another, so they are shared out. The plan itself is read-only once built.
        if ((long)up * Partitions * FftSize > 1 << 20)
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
            var taps = new double[FftSize];
            for (int part = 0; part < Partitions; part++)
            {
                Array.Clear(taps);
                for (int k = 0; k < BlockSamples; k++)
                {
                    int tap = (part * BlockSamples) + k;
                    int index = phase + (tap * up);
                    taps[k] = tap < TapsPerPhase && index < prototype.Length ? prototype[index] : 0.0;
                }

                var re = new double[Bins];
                var im = new double[Bins];
                _plan.Forward(taps, re, im);
                _partitionRe[(phase * Partitions) + part] = re;
                _partitionIm[(phase * Partitions) + part] = im;
            }
        }

        DelayOutputSamples = FirDesign.PeakIndex(prototype);
        Description = $"{name} ({PrototypeLength:N0} taps, {TapsPerPhase:N0} per phase, " +
            $"{Partitions:N0} × {FftSize:N0}-point FFT)";

        // One forward transform per block, one inverse per phase, plus the partition multiply-accumulates.
        CostPerOutputSample = PartitionPlan.LevelCost(BlockSamples, Partitions, up);
    }

    public int InputRate { get; }

    public int OutputRate { get; }

    /// <summary>Interpolation factor.</summary>
    public int Up { get; }

    public int TapsPerPhase { get; }

    public int PrototypeLength { get; }

    /// <summary>Input samples per processed block.</summary>
    public int BlockSamples { get; }

    /// <summary>Filter partitions per phase.</summary>
    public int Partitions { get; }

    public int FftSize => _plan.Size;

    /// <summary>
    /// Spectrum bins actually stored and multiplied. A real signal's transform is conjugate-symmetric, so bins
    /// above the halfway point repeat the ones below it and are reconstructed rather than carried around.
    /// </summary>
    public int Bins => _plan.Bins;

    public double DelayOutputSamples { get; }

    public double CostPerOutputSample { get; }

    /// <summary>Tap-at-a-time cost of the same filter: one multiply-add per tap of the phase, per output sample.</summary>
    public double DirectCostPerOutputSample => TapsPerPhase;

    public int Taps => PrototypeLength;

    public int FlushInputSamples => BlockSamples;

    public string Description { get; }

    /// <summary>Whether frequency-domain convolution applies, and is worth its overhead, for this conversion.</summary>
    public static bool Suits(int inputRate, int outputRate, int prototypeLength) =>
        Suits(inputRate, outputRate, prototypeLength, default);

    /// <summary>Whether frequency-domain convolution applies, and is worth its overhead, for this conversion.</summary>
    /// <param name="options">Crossover tuning; the default uses <see cref="DefaultThresholdTapsPerPhase"/>.</param>
    public static bool Suits(int inputRate, int outputRate, int prototypeLength, ConvolutionOptions options)
    {
        if (inputRate <= 0 || outputRate <= inputRate)
        {
            return false;
        }

        (int up, int down) = AudioRates.Ratio(inputRate, outputRate);
        int tapsPerPhase = (prototypeLength + up - 1) / up;

        // The crossover is a judgement, not a limit. Below about a thousand taps per phase tap by tap is as fast
        // and has no block delay at all; above it the frequency domain wins by more the longer the filter gets.
        // Asking for a different crossover moves where that judgement is made, in either direction.
        int threshold = options.ThresholdTapsPerPhase > 0 ? options.ThresholdTapsPerPhase : DefaultThresholdTapsPerPhase;
        return down == 1 && tapsPerPhase >= threshold;
    }

    /// <summary>
    /// Multiply-adds per output sample a uniform block of <paramref name="block"/> input samples would cost:
    /// one forward transform and one per phase, plus the partition multiplies.
    /// </summary>
    /// <remarks>
    /// The two terms pull opposite ways. The partition multiplies are proportional to taps/block, so they fall as
    /// the block grows; the transforms are proportional to log2(2·block) per output sample, so they rise with it.
    /// There is a genuine minimum in between, and it sits far above the block length a low delay wants.
    /// </remarks>
    public static double CostPerOutputSampleOf(int tapsPerPhase, int up, int block) =>
        PartitionPlan.LevelCost(block, (tapsPerPhase + block - 1) / block, up);

    /// <summary>
    /// The block to convolve in: the cheapest one that still holds input back for no longer than allowed.
    /// </summary>
    /// <param name="maxBlockMilliseconds">
    /// Longest the stage may hold input; 0 allows a block a sixteenth of the filter's own delay, and never less
    /// than <see cref="MinimumBlockMilliseconds"/>, which is what a filter that is not asked for anything gets.
    /// </param>
    public static int ChooseBlock(int tapsPerPhase, int up, int inputRate, double maxBlockMilliseconds)
    {
        double allowed = maxBlockMilliseconds > 0
            ? maxBlockMilliseconds
            : Math.Max(MinimumBlockMilliseconds, tapsPerPhase * 1000.0 / (BlockShareOfFilter * (double)inputRate));
        int cap = (int)Math.Clamp(allowed * inputRate / 1000.0, MinBlockSamples, MaxBlockSamples);

        int best = MinBlockSamples;
        for (int block = MinBlockSamples; block <= cap; block <<= 1)
        {
            if (CostPerOutputSampleOf(tapsPerPhase, up, block) < CostPerOutputSampleOf(tapsPerPhase, up, best))
            {
                best = block;
            }
        }

        return best;
    }

    public int MaxOutput(int inputSamples) => (inputSamples / BlockSamples + 1) * BlockSamples * Up;

    /// <summary>
    /// The largest gain any of the first <paramref name="partitions"/> partitions has at any frequency, which is
    /// how loud a full-scale input to them can come back.
    /// </summary>
    /// <remarks>
    /// It exists for the accelerator's verification, which can only afford to feed the head of a long filter.
    /// The head of a long windowed sinc is its deep tail in tap order, and a Gaussian two million taps long is
    /// two hundred decibels down there, so a full-scale input produces an answer too small to compare and the
    /// signal has to be scaled up by roughly the reciprocal of this before it means anything.
    /// </remarks>
    internal double PartitionPeak(int partitions)
    {
        int count = Math.Clamp(partitions, 1, Partitions);
        double peak = 0.0;
        for (int phase = 0; phase < Up; phase++)
        {
            for (int part = 0; part < count; part++)
            {
                double[] re = _partitionRe[(phase * Partitions) + part];
                double[] im = _partitionIm[(phase * Partitions) + part];
                for (int k = 0; k < re.Length; k++)
                {
                    peak = Math.Max(peak, Math.Abs(re[k]) + Math.Abs(im[k]));
                }
            }
        }

        return peak;
    }

    /// <summary>
    /// Full spectrum of one filter partition, so an accelerator can upload the same coefficients. The upper half
    /// is rebuilt from the lower one here rather than kept, since only this path still wants it.
    /// </summary>
    internal (double[] Re, double[] Im) PartitionSpectrum(int phase, int partition)
    {
        int index = phase * Partitions + partition;
        var re = new double[FftSize];
        var im = new double[FftSize];
        _partitionRe[index].CopyTo(re, 0);
        _partitionIm[index].CopyTo(im, 0);
        Mirror(re, im);
        return (re, im);
    }

    /// <summary>Rebuilds bins above the halfway point from their conjugates below it.</summary>
    private static void Mirror(double[] re, double[] im)
    {
        int size = re.Length;
        for (int k = 1; k < size / 2; k++)
        {
            re[size - k] = re[k];
            im[size - k] = -im[k];
        }
    }

    public IRateStageState CreateState() => CreateState(1);

    /// <summary>Creates state that may spread its phases over <paramref name="parallelism"/> threads.</summary>
    public IRateStageState CreateState(int parallelism) => new State(this, Math.Clamp(parallelism, 1, 16));

    /// <summary>Creates state, on an OpenCL device where one will take the filter and here where none will.</summary>
    /// <param name="parallelism">Threads the stage may use for its own phases when it stays on the processor.</param>
    /// <param name="accelerator">Optional OpenCL device; it falls back to the threads when it cannot take the stage.</param>
    public IRateStageState CreateState(int parallelism, GpuAccelerator? accelerator) =>
        accelerator?.TryAttach(this, parallelism) ?? CreateState(parallelism);

    /// <summary>Creates the processor-side machinery for one channel, which can be asked for any run of phases.</summary>
    internal PhaseBank CreateBank(int parallelism) => new(this, Math.Clamp(parallelism, 1, 16));

    /// <summary>
    /// One channel's worth of this stage's arithmetic, offered a run of phases at a time rather than all of them.
    /// </summary>
    /// <remarks>
    /// Everything before the phases, meaning buffering the block, transforming it and keeping the history, is
    /// the same work whoever convolves the phases afterwards, so <see cref="Accept"/> is separate from
    /// <see cref="Produce"/>. That is what lets a graphics device take some of the phases of a block while this
    /// takes the rest of the same block at the same time; asked for the full range, it is the whole stage.
    /// </remarks>
    internal sealed class PhaseBank
    {
        private readonly FftPolyphaseStage _stage;
        private readonly ParallelOptions? _parallel;
        private readonly double[] _previous;
        private readonly double[] _window;
        private readonly double[][] _samples;
        private readonly double[][] _historyRe;
        private readonly double[][] _historyIm;
        private readonly double[][] _accumulatorRe;
        private readonly double[][] _accumulatorIm;
        private readonly Action<int> _filter;
        private double[] _produced = [];
        private int _first;
        private int _head;

        public PhaseBank(FftPolyphaseStage stage, int parallelism)
        {
            _stage = stage;
            _parallel = parallelism > 1 && stage.Up > 1
                ? new ParallelOptions { MaxDegreeOfParallelism = Math.Min(parallelism, stage.Up) }
                : null;
            _filter = Filter;
            _previous = new double[stage.BlockSamples];
            _window = new double[stage.FftSize];
            _historyRe = new double[stage.Partitions][];
            _historyIm = new double[stage.Partitions][];
            for (int i = 0; i < stage.Partitions; i++)
            {
                _historyRe[i] = new double[stage.Bins];
                _historyIm[i] = new double[stage.Bins];
            }

            // The accumulator holds bins, not a whole circle: the transform back to samples takes the half a
            // real signal has and needs no mirror of it.
            _accumulatorRe = new double[stage.Up][];
            _accumulatorIm = new double[stage.Up][];
            _samples = new double[stage.Up][];
            for (int p = 0; p < stage.Up; p++)
            {
                _accumulatorRe[p] = new double[stage.Bins];
                _accumulatorIm[p] = new double[stage.Bins];
                _samples[p] = new double[stage.FftSize];
            }

            Reset();
        }

        public void Reset()
        {
            Array.Clear(_previous);
            foreach (double[] buffer in _historyRe)
            {
                Array.Clear(buffer);
            }

            foreach (double[] buffer in _historyIm)
            {
                Array.Clear(buffer);
            }

            _head = 0;
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

        /// <summary>
        /// Real half of the spectrum of the block last accepted. A device convolving some of the phases of the
        /// same block needs exactly this and would otherwise transform the block again to get it.
        /// </summary>
        public ReadOnlySpan<double> SpectrumRe => _historyRe[_head];

        /// <summary>Imaginary half of the spectrum of the block last accepted.</summary>
        public ReadOnlySpan<double> SpectrumIm => _historyIm[_head];

        /// <summary>Takes the block and leaves its spectrum in the history every phase is convolved against.</summary>
        public void Accept(ReadOnlySpan<double> block)
        {
            int size = _stage.BlockSamples;

            // Overlap-save: transform the previous block followed by the new one, straight into the history.
            _previous.CopyTo(_window, 0);
            block.CopyTo(_window.AsSpan(size));
            _head = (_head + 1) % _stage.Partitions;
            _stage._plan.Forward(_window, _historyRe[_head], _historyIm[_head]);
            block.CopyTo(_previous);
        }

        /// <summary>Convolves <paramref name="count"/> phases from <paramref name="firstPhase"/> into the interleaved output.</summary>
        public void Produce(int firstPhase, int count, double[] produced)
        {
            if (count <= 0)
            {
                return;
            }

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

        /// <summary>Multiplies the filter partitions of one phase with the stored input spectra and scatters the result.</summary>
        private void Filter(int index)
        {
            int phase = _first + index;
            int bins = _stage.Bins;
            int block = _stage.BlockSamples;
            int partitions = _stage.Partitions;
            int up = _stage.Up;
            double[] accRe = _accumulatorRe[phase];
            double[] accIm = _accumulatorIm[phase];
            Array.Clear(accRe, 0, bins);
            Array.Clear(accIm, 0, bins);

            for (int part = 0; part < partitions; part++)
            {
                double[] hRe = _stage._partitionRe[phase * partitions + part];
                double[] hIm = _stage._partitionIm[phase * partitions + part];
                int slot = (_head - part + partitions) % partitions;
                double[] xRe = _historyRe[slot];
                double[] xIm = _historyIm[slot];

                int i = 0;
                int width = Vector<double>.Count;
                for (; i <= bins - width; i += width)
                {
                    var ar = new Vector<double>(xRe, i);
                    var ai = new Vector<double>(xIm, i);
                    var br = new Vector<double>(hRe, i);
                    var bi = new Vector<double>(hIm, i);
                    (new Vector<double>(accRe, i) + (ar * br - ai * bi)).CopyTo(accRe, i);
                    (new Vector<double>(accIm, i) + (ar * bi + ai * br)).CopyTo(accIm, i);
                }

                for (; i < bins; i++)
                {
                    double ar = xRe[i];
                    double ai = xIm[i];
                    accRe[i] += ar * hRe[i] - ai * hIm[i];
                    accIm[i] += ar * hIm[i] + ai * hRe[i];
                }
            }

            double[] samples = _samples[phase];
            _stage._plan.Inverse(accRe, accIm, samples);

            // The second half of a circular convolution of two half-filled blocks is the linear result.
            double[] produced = _produced;
            for (int n = 0; n < block; n++)
            {
                produced[n * up + phase] = samples[block + n];
            }
        }
    }

    private sealed class State : BlockStageState
    {
        private readonly PhaseBank _bank;
        private readonly int _up;

        public State(FftPolyphaseStage stage, int parallelism)
            : base(stage.BlockSamples, stage.BlockSamples * stage.Up)
        {
            _bank = new PhaseBank(stage, parallelism);
            _up = stage.Up;
            Reset();
        }

        protected override void ResetState() => _bank.Reset();

        protected override void ProcessBlock()
        {
            _bank.Accept(Current);
            _bank.Produce(0, _up, Produced);
        }
    }
}
