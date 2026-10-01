using FUPlayer.Core.Audio;
using FUPlayer.Core.Dsp.Design;
using FUPlayer.Core.Dsp.Filters;
using FUPlayer.Core.Localization;

namespace FUPlayer.Core.Dsp.Resampling;

/// <summary>How an upsampling ratio is split into stages.</summary>
public enum StagingMode
{
    /// <summary>The selected filter converts to 2x; efficient half-band stages do the rest.</summary>
    MultiStage,

    /// <summary>The selected filter converts directly to the final integer multiple.</summary>
    SingleStage,
}

/// <summary>
/// How a filter's arithmetic is carried out. Both settings compute the same convolution of the same
/// coefficients; they differ in speed, in added delay, and in how rounding accumulates.
/// </summary>
public enum ConvolutionMode
{
    /// <summary>Long filters convolve in the frequency domain; shorter ones go tap by tap, where that is faster.</summary>
    Automatic,

    /// <summary>Always one multiply-add per tap per output sample, in the order the samples arrive.</summary>
    TapByTap,
}

/// <summary>
/// Tuning for frequency-domain convolution, beyond the choice of method: where it takes over from tap by tap,
/// and how long a block it may hold input in.
/// </summary>
/// <remarks>
/// Both numbers are trades, not preferences. A lower crossover moves shorter filters onto the frequency domain,
/// which is cheaper per tap but adds a block of delay that tap by tap does not have. A shorter block gives that
/// delay back and costs arithmetic: the partition multiplies grow as the block shrinks, steeply once the block
/// is far below the filter's own length.
/// </remarks>
public readonly record struct ConvolutionOptions
{
    /// <summary>Taps per phase from which the frequency domain takes over; 0 uses the built-in crossover.</summary>
    public int ThresholdTapsPerPhase { get; init; }

    /// <summary>Longest a stage may hold input back, in milliseconds; 0 lets the stage choose.</summary>
    public double MaxBlockMilliseconds { get; init; }

    /// <summary>
    /// Allow the taps to be divided between several block lengths, short blocks for the early taps and longer
    /// ones behind them, which costs far less arithmetic for the same short wait. Off by default, and therefore the
    /// default of this whole record, because a graphics device takes stages of one block length only.
    /// </summary>
    public bool LayeredBlocks { get; init; }

    public override string ToString() =>
        $"{ThresholdTapsPerPhase}/{MaxBlockMilliseconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}" +
        $"/{(LayeredBlocks ? "layered" : "uniform")}";
}

/// <summary>Plans and builds <see cref="ResamplerChain"/> instances (with a small design cache).</summary>
public static class ResamplerFactory
{
    /// <summary>
    /// Longest prototype that will be designed. Held in doubles it is a quarter of a gigabyte, and the
    /// frequency-domain stage keeps about twice that in partition spectra, so this is where a filter stops being
    /// something a machine can hold rather than something the arithmetic forbids.
    /// </summary>
    public const int MaxPrototypeLength = 1 << 25;

    private const int MaxPhaseTransformLength = 1 << 21;
    private const double MinimumCascadeAttenuation = 150.0;
    private const int CacheCapacity = 12;

    /// <summary>
    /// What the design cache may hold beyond the chain asked for last. A chain of tens of millions of taps keeps half a
    /// gigabyte of partition spectra, and twelve were kept whatever their size: looking at a few filters in the DSP
    /// studio with the longest length chosen held several gigabytes, next to the one the player was using.
    /// </summary>
    private const long CacheBudgetBytes = 1L << 30;

    private static readonly object CacheLock = new();
    private static readonly LinkedList<KeyValuePair<string, ResamplerChain>> Cache = new();

    /// <summary>Returns a (possibly cached) chain for the conversion.</summary>
    /// <param name="taps">Requested filter length in taps at <paramref name="outputRate"/>; 0 designs from the specification.</param>
    /// <param name="convolution">How the filter's arithmetic is carried out.</param>
    public static ResamplerChain Create(
        FilterPreset preset,
        int inputRate,
        int outputRate,
        StagingMode staging = StagingMode.MultiStage,
        int taps = 0,
        ConvolutionMode convolution = ConvolutionMode.Automatic,
        ConvolutionOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(preset);
        string key = $"{preset.Id}|{inputRate}|{outputRate}|{staging}|{taps}|{convolution}|{options}";
        lock (CacheLock)
        {
            for (LinkedListNode<KeyValuePair<string, ResamplerChain>>? node = Cache.First; node is not null; node = node.Next)
            {
                if (node.Value.Key == key)
                {
                    Cache.Remove(node);
                    Cache.AddFirst(node);
                    return node.Value.Value;
                }
            }
        }

        ResamplerChain chain = Build(preset, inputRate, outputRate, staging, taps, convolution, options);
        lock (CacheLock)
        {
            Cache.AddFirst(new KeyValuePair<string, ResamplerChain>(key, chain));
            long held = Cache.Sum(entry => entry.Value.MemoryBytes);
            while (Cache.Count > 1 && (Cache.Count > CacheCapacity || held > CacheBudgetBytes))
            {
                held -= Cache.Last!.Value.Value.MemoryBytes;
                Cache.RemoveLast();
            }
        }

        return chain;
    }

    /// <summary>Whether the preset can perform the conversion without substituting another filter.</summary>
    public static bool IsSupported(FilterPreset preset, int inputRate, int outputRate)
    {
        if (inputRate == outputRate)
        {
            return true;
        }

        if (preset.Family == FilterFamily.None)
        {
            return false;
        }

        if (preset.IntegerRatioOnly)
        {
            return outputRate >= inputRate * 2;
        }

        return true;
    }

    private static ResamplerChain Build(
        FilterPreset preset, int inputRate, int outputRate, StagingMode staging, int taps, ConvolutionMode convolution, ConvolutionOptions options)
    {
        if (inputRate <= 0 || outputRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(inputRate), "Rates must be positive.");
        }

        if (inputRate == outputRate)
        {
            return new ResamplerChain(inputRate, outputRate, [], Loc.T("No rate conversion"));
        }

        if (preset.Family == FilterFamily.None)
        {
            throw new NotSupportedException("The bit-perfect filter cannot change the sample rate.");
        }

        if (preset.Family == FilterFamily.TransientAligned)
        {
            return TransientAligned(preset, inputRate, outputRate, taps, convolution, options);
        }

        var notes = new List<string>();
        if (preset.EarlyRollOff && inputRate < FilterCatalog.EarlyRollOffMinimumRate)
        {
            string chosen = preset.Name;
            preset = FilterCatalog.Get(FilterCatalog.EarlyRollOffSubstituteId);
            notes.Add(Loc.F("{0} is meant for files above 48 kHz; {1} used", chosen, preset.Name));
        }

        var stages = new List<IRateStage>();
        if (outputRate < inputRate)
        {
            FilterPreset p = SubstituteIfNeeded(preset, notes, allowPolynomial: false, allowIir: false);
            stages.Add(PolyphaseCharacter(p, inputRate, outputRate, notes, convolution, options, taps, outputRate));
        }
        else
        {
            int multiple = outputRate / inputRate;
            int integerTarget = inputRate * multiple;
            bool needsBridge = integerTarget != outputRate;
            double bandHz = inputRate / 2.0 * Math.Max(1.0, preset.StopbandFraction);
            double cascadeAttenuation = Math.Max(preset.AttenuationDb, MinimumCascadeAttenuation);

            if (multiple == 1)
            {
                FilterPreset p = SubstituteIfNeeded(preset, notes, allowPolynomial: true, allowIir: false);
                stages.Add(PolyphaseCharacter(p, inputRate, outputRate, notes, convolution, options, taps, outputRate));
                needsBridge = false;
            }
            else
            {
                bool powerOfTwo = (multiple & (multiple - 1)) == 0;
                bool cascade = staging == StagingMode.MultiStage && powerOfTwo && multiple > 2
                    && preset.Family is not (FilterFamily.Polynomial or FilterFamily.LowRinging);
                int firstTarget = cascade ? inputRate * 2 : integerTarget;

                stages.Add(preset.Family == FilterFamily.Iir
                    ? IirCharacter(preset, inputRate, firstTarget / inputRate)
                    : PolyphaseCharacter(preset, inputRate, firstTarget, notes, convolution, options, taps, outputRate));

                for (int rate = firstTarget; rate < integerTarget; rate *= 2)
                {
                    stages.Add(new HalfbandInterpolatorStage(rate, bandHz, cascadeAttenuation));
                }
            }

            if (needsBridge)
            {
                stages.Add(FamilyBridge(integerTarget, outputRate, bandHz, cascadeAttenuation));
            }
        }

        string summary = $"{preset.Name}: " + string.Join(" → ", stages.Select(s => s.Description));
        if (notes.Count > 0)
        {
            summary += $" ({string.Join("; ", notes)})";
        }

        return new ResamplerChain(inputRate, outputRate, stages, summary);
    }

    /// <summary>
    /// Multiple of the source rate the transient-aligned filter's long first stage runs to; a second, short stage of the
    /// same kind takes it on from there, up to <see cref="TransientAlignedSecondLimit"/> times the source rate.
    /// </summary>
    internal const int TransientAlignedFirstMultiple = 16;

    /// <summary>Highest multiple of the source rate the transient-aligned stages reach; half-band stages go on from there.</summary>
    internal const int TransientAlignedSecondLimit = 256;

    /// <summary>
    /// Input samples the transient-aligned second stage spans. It interpolates a signal that holds nothing above the
    /// source's Nyquist frequency, so its response may take everything from there to the first image to close, and its
    /// tapers are a few samples long; this span keeps about two thirds of it the sinc's own.
    /// </summary>
    internal const int TransientAlignedSecondSpan = 64;

    /// <summary>How much shorter than for interpolation the automatic length is when the output rate is the lower one.</summary>
    private const int TransientAlignedDecimationShortening = 8;

    /// <summary>
    /// The transient-aligned filter's own stages, whatever the staging setting says, since its stages are what it is: a long
    /// one from the source rate to 16 times it, or to the largest whole multiple of it under the output rate if that is
    /// lower, and a short one from 16 times up to 256 times, each cut off at its own input's Nyquist frequency, so that each
    /// passes the samples it is given through unchanged and computes only those in between. Half-band stages go on above
    /// 256 times. An output rate that is not a whole multiple of where those stop is reached by the family bridge, from
    /// twice the source rate at least: 44.1 to 48 kHz is the long stage to 88.2 kHz and the bridge from there, rather than
    /// one stage of 160 phases convolved tap by tap, and 44.1 to 768 kHz the long stage to 705.6 kHz and the bridge. A
    /// lower output rate is one stage cut off at its Nyquist frequency, an eighth as long by default: there it removes what
    /// would fold over, and none of the samples it keeps were there before.
    /// </summary>
    private static ResamplerChain TransientAligned(
        FilterPreset preset, int inputRate, int outputRate, int taps, ConvolutionMode convolution, ConvolutionOptions options)
    {
        var notes = new List<string>();
        var stages = new List<IRateStage>();
        double bandHz = inputRate / 2.0 * (1.0 + (2.0 * TransientAlignedDesign.TransitionFraction));

        // What the stages after the long one are designed to, a little past the filter's own figure, so that the chain as a
        // whole keeps it: Kaiser's estimate of the length a stopband needs is a few decibels short at these depths.
        double attenuation = TransientAlignedDesign.AttenuationDb + 10.0;
        if (outputRate < inputRate)
        {
            stages.Add(TransientAlignedStage(preset, inputRate, outputRate, taps, outputRate, inputRate, second: false, notes, convolution, options));
        }
        else
        {
            int firstMultiple = Math.Clamp(outputRate / inputRate, 2, TransientAlignedFirstMultiple);
            int rate = inputRate * firstMultiple;
            stages.Add(TransientAlignedStage(preset, inputRate, rate, taps, outputRate, inputRate, second: false, notes, convolution, options));
            if (firstMultiple == TransientAlignedFirstMultiple && outputRate / rate >= 2)
            {
                int secondMultiple = Math.Min(outputRate / rate, TransientAlignedSecondLimit / TransientAlignedFirstMultiple);
                stages.Add(TransientAlignedStage(preset, rate, rate * secondMultiple, 0, outputRate, inputRate, second: true, notes, convolution, options));
                rate *= secondMultiple;
                while ((long)rate * 2 <= outputRate)
                {
                    stages.Add(new HalfbandInterpolatorStage(rate, bandHz, attenuation));
                    rate *= 2;
                }
            }

            if (rate != outputRate)
            {
                stages.Add(FamilyBridge(rate, outputRate, bandHz, attenuation));
            }
        }

        string summary = $"{preset.Name}: " + string.Join(" → ", stages.Select(s => s.Description));
        if (notes.Count > 0)
        {
            summary += $" ({string.Join("; ", notes)})";
        }

        return new ResamplerChain(inputRate, outputRate, stages, summary);
    }

    /// <summary>
    /// One transient-aligned stage. The first is as long as asked for, or <see cref="TransientAlignedDesign.DefaultLength"/>,
    /// and closes within a thousandth of its cut-off; the second spans <see cref="TransientAlignedSecondSpan"/> input
    /// samples and closes anywhere between the source's band and the first image of it.
    /// </summary>
    private static IRateStage TransientAlignedStage(
        FilterPreset preset, int inputRate, int outputRate, int taps, int finalRate, int sourceRate, bool second,
        List<string> notes, ConvolutionMode convolution, ConvolutionOptions options)
    {
        (int up, int down) = AudioRates.Ratio(inputRate, outputRate);
        int period = Math.Max(up, down);
        double prototypeRate = (double)up * inputRate;
        double halfWidth;
        int length;
        if (second)
        {
            // Flat to just past the source's band, closed from the image of it: half the gap between the two either side
            // of this stage's own Nyquist frequency, less a margin.
            double gapHz = (inputRate / 2.0) - (sourceRate / 2.0 * (1.0 + (2.0 * TransientAlignedDesign.TransitionFraction)));
            halfWidth = 0.9 * gapHz / prototypeRate;
            length = (TransientAlignedSecondSpan * up) + 1;
        }
        else
        {
            halfWidth = TransientAlignedDesign.TransitionFraction / (2.0 * period);
            length = taps > 0
                ? ScaleTaps(taps, outputRate, finalRate, notes)
                : outputRate < inputRate
                    ? TransientAlignedDesign.DefaultLength(period) / TransientAlignedDecimationShortening | 1
                    : TransientAlignedDesign.DefaultLength(period);
            if (length > MaxPrototypeLength)
            {
                notes.Add(Loc.F("{0:N0} taps is longer than this build will design, so {1:N0} were used", length, MaxPrototypeLength));
                length = MaxPrototypeLength;
            }
        }

        double[] prototype = TransientAlignedDesign.Lowpass(length, period, up, halfWidth, out TransientAlignedShape shape);
        double cutoffHz = prototypeRate / (2.0 * period);
        string name = Loc.F(
            "{0} {1}→{2}, {3:0.#} % exact sinc, ±{4} at {5}",
            preset.Name, AudioRates.FormatShort(inputRate), AudioRates.FormatShort(outputRate), 100.0 * shape.ExactShare,
            FormatHz(shape.HalfWidth * prototypeRate), FormatHz(cutoffHz));
        return convolution == ConvolutionMode.Automatic && FftPolyphaseStage.Suits(inputRate, outputRate, prototype.Length, options)
            ? FrequencyDomain(inputRate, outputRate, prototype, name, options)
            : new PolyphaseStage(inputRate, outputRate, prototype,
                Loc.F("{0} ({1:N0} taps, {2:N0} per phase, tap by tap)", name, prototype.Length, (prototype.Length + up - 1) / up));
    }

    /// <summary>A frequency in hertz or kilohertz: "22 Hz", "22.05 kHz", "297 kHz".</summary>
    private static string FormatHz(double hz) =>
        hz >= 1000.0
            ? (hz / 1000.0).ToString(hz >= 100_000.0 ? "0" : "0.##", System.Globalization.CultureInfo.CurrentCulture) + " kHz"
            : hz.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture) + " Hz";

    private static FilterPreset SubstituteIfNeeded(FilterPreset preset, List<string> notes, bool allowPolynomial, bool allowIir)
    {
        bool unsupported = (preset.Family == FilterFamily.Iir && !allowIir)
            || (preset.Family == FilterFamily.Polynomial && !allowPolynomial);
        if (!unsupported)
        {
            return preset;
        }

        notes.Add(Loc.F("{0} cannot perform this ratio; {1} used", preset.Name, FilterCatalog.Get(FilterCatalog.FallbackId).Name));
        return FilterCatalog.Get(FilterCatalog.FallbackId);
    }

    private static IRateStage PolyphaseCharacter(
        FilterPreset preset, int inputRate, int outputRate, List<string> notes, ConvolutionMode convolution,
        ConvolutionOptions options, int taps = 0, int finalRate = 0)
    {
        (int up, _) = AudioRates.Ratio(inputRate, outputRate);
        double prototypeRate = (double)up * inputRate;
        double[] prototype;

        if (taps > 0 && !preset.SupportsCustomLength)
        {
            notes.Add(Loc.F("{0} is defined by its own impulse, so the filter length setting does not apply to it", preset.Name));
            taps = 0;
        }

        if (preset.Family == FilterFamily.Polynomial)
        {
            prototype = PolynomialPrototype(preset.Polynomial, up);
        }
        else
        {
            double nyquist = Math.Min(inputRate, outputRate) / 2.0;
            var spec = new LowpassSpec(
                preset.PassbandFraction * nyquist / prototypeRate,
                preset.StopbandFraction * nyquist / prototypeRate,
                preset.AttenuationDb,
                preset.Window);

            int estimated = FirDesign.EstimateLength(spec);
            int wanted = taps > 0 ? ScaleTaps(taps, outputRate, finalRate, notes) : 0;
            int length = wanted > 0 ? wanted : estimated;
            if (length > MaxPrototypeLength)
            {
                throw new NotSupportedException(
                    Loc.F(
                        "{0} would need {1:N0} prototype taps for {2} → {3}. Choose a shorter filter or multi-stage processing.",
                        preset.Name, length, AudioRates.Format(inputRate), AudioRates.Format(outputRate)));
            }

            if (wanted > 0 && wanted < estimated)
            {
                notes.Add(Loc.F("{0:N0} taps is shorter than this filter needs ({1:N0}), so its stopband will not reach {2:0} dB", wanted, estimated, preset.AttenuationDb));
            }

            PhaseResponse phase = preset.Phase;
            if (phase != PhaseResponse.Linear && length > MaxPhaseTransformLength)
            {
                phase = PhaseResponse.Linear;
                notes.Add(Loc.T("linear phase used because the prototype is too long for a phase transform"));
            }

            prototype = FirDesign.DesignLowpass(spec, up, phase, length: wanted);
        }

        string name = $"{preset.Name} {AudioRates.FormatShort(inputRate)}→{AudioRates.FormatShort(outputRate)}";
        return convolution == ConvolutionMode.Automatic && FftPolyphaseStage.Suits(inputRate, outputRate, prototype.Length, options)
            ? FrequencyDomain(inputRate, outputRate, prototype, name, options)
            : new PolyphaseStage(inputRate, outputRate, prototype,
                Loc.F("{0} ({1:N0} taps, {2:N0} per phase, tap by tap)", name, prototype.Length, (prototype.Length + up - 1) / up));
    }

    /// <summary>
    /// Builds the frequency-domain stage: one block length unless several were allowed and are cheaper for the
    /// same wait. Asking for a shorter block is what usually makes them so: a short uniform block leaves every
    /// tap paying for it, where a layered one charges only the early taps.
    /// </summary>
    /// <remarks>
    /// One block length is the default, because it is the only layout a graphics device takes; layering is
    /// therefore something asked for rather than something arrived at, and where it has been asked for the
    /// cheapest plan wins outright with no margin to clear.
    /// </remarks>
    private static IRateStage FrequencyDomain(
        int inputRate, int outputRate, double[] prototype, string name, ConvolutionOptions options)
    {
        (int up, _) = AudioRates.Ratio(inputRate, outputRate);
        int tapsPerPhase = Math.Max(1, (prototype.Length + up - 1) / up);
        int head = FftPolyphaseStage.ChooseBlock(tapsPerPhase, up, inputRate, options.MaxBlockMilliseconds);

        if (options.LayeredBlocks)
        {
            PartitionPlan plan = PartitionPlan.Choose(tapsPerPhase, up, head, FftPolyphaseStage.MaxBlockSamples);
            double uniform = FftPolyphaseStage.CostPerOutputSampleOf(tapsPerPhase, up, head);
            if (!plan.IsUniform && plan.CostPerOutputSample < uniform)
            {
                return new LayeredFftPolyphaseStage(inputRate, outputRate, prototype, name, plan);
            }
        }

        return new FftPolyphaseStage(inputRate, outputRate, prototype, name, options);
    }

    /// <summary>
    /// The requested length counts taps at the final output rate, so a stage that runs at a lower rate covers the
    /// same span of time with proportionally fewer taps. A request longer than can be held is trimmed and said so.
    /// </summary>
    /// <summary>
    /// The filter length to design for one stage. The setting counts taps at the output rate, which is not this
    /// stage's rate whenever something else finishes the conversion, such as the half-band cascade of a
    /// multi-stage chain or the bridge stage that carries 48 kHz to 705.6 kHz over its last, awkward step.
    /// </summary>
    /// <remarks>
    /// Scaling is what keeps the setting meaning the same thing in both places: transition width is a fraction of
    /// the rate, so the same width costs proportionally fewer taps at a lower rate, and designing the full count
    /// there would buy a transition far narrower than was asked for at several times the arithmetic. It is also
    /// the one place the taps a stage reports can differ from the taps that were chosen, so it says when it has.
    /// </remarks>
    private static int ScaleTaps(int taps, int stageOutputRate, int finalRate, List<string> notes)
    {
        long scaled = finalRate > 0 && stageOutputRate < finalRate
            ? (long)taps * stageOutputRate / finalRate
            : taps;
        if (scaled != taps)
        {
            notes.Add(
                Loc.F(
                    "{0:N0} taps at {1} is {2:N0} at {3}, where this filter runs",
                    taps, AudioRates.FormatShort(finalRate), scaled, AudioRates.FormatShort(stageOutputRate)));
        }

        if (scaled > MaxPrototypeLength)
        {
            notes.Add(Loc.F("{0:N0} taps is longer than this build will design, so {1:N0} were used", scaled, MaxPrototypeLength));
            scaled = MaxPrototypeLength;
        }

        return (int)Math.Max(scaled, 31);
    }

    private static IRateStage IirCharacter(FilterPreset preset, int inputRate, int factor)
    {
        int outputRate = inputRate * factor;
        double nyquist = inputRate / 2.0;
        double passband = preset.PassbandFraction * nyquist / outputRate;
        double stopband = Math.Min(preset.StopbandFraction * nyquist / outputRate, 0.49);
        Biquad[] sections = EllipticDesign.Lowpass(passband, stopband, Math.Max(preset.PassbandRippleDb, 1e-4), preset.AttenuationDb, out int order);
        return new IirInterpolatorStage(inputRate, factor, sections,
            Loc.F("{0} ×{1} {2}→{3} (order {4})", preset.Name, factor, AudioRates.FormatShort(inputRate), AudioRates.FormatShort(outputRate), order));
    }

    /// <summary>Short rational stage moving an already oversampled signal between the 44.1 k and 48 k families.</summary>
    private static IRateStage FamilyBridge(int inputRate, int outputRate, double bandHz, double attenuation)
    {
        (int up, _) = AudioRates.Ratio(inputRate, outputRate);
        double prototypeRate = (double)up * inputRate;
        double stopHz = Math.Min(inputRate, outputRate) - bandHz;
        var spec = new LowpassSpec(bandHz / prototypeRate, stopHz / prototypeRate, attenuation);
        double[] prototype = FirDesign.Lowpass(spec, up);
        return new PolyphaseStage(inputRate, outputRate, prototype,
            Loc.F(
                "Family bridge {0}→{1} ({2:N0} taps, {3:N0} per phase)",
                AudioRates.FormatShort(inputRate), AudioRates.FormatShort(outputRate), prototype.Length, (prototype.Length + up - 1) / up));
    }

    private static double[] PolynomialPrototype(PolynomialKind kind, int up)
    {
        int support = kind == PolynomialKind.Linear ? 1 : 2;
        int length = 2 * support * up + 1;
        int center = support * up;
        var h = new double[length];
        for (int i = 0; i < length; i++)
        {
            double t = Math.Abs((i - center) / (double)up);
            h[i] = kind == PolynomialKind.Linear
                ? Math.Max(0.0, 1.0 - t)
                : t < 1.0 ? 1.5 * t * t * t - 2.5 * t * t + 1.0
                : t < 2.0 ? -0.5 * t * t * t + 2.5 * t * t - 4.0 * t + 2.0
                : 0.0;
        }

        return h;
    }
}
