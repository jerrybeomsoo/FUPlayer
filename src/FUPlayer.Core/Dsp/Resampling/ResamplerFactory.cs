using FUPlayer.Core.Audio;
using FUPlayer.Core.Dsp.Design;
using FUPlayer.Core.Dsp.Filters;

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
            while (Cache.Count > CacheCapacity)
            {
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
            return new ResamplerChain(inputRate, outputRate, [], "No rate conversion");
        }

        if (preset.Family == FilterFamily.None)
        {
            throw new NotSupportedException("The bit-perfect filter cannot change the sample rate.");
        }

        var notes = new List<string>();
        if (preset.EarlyRollOff && inputRate < FilterCatalog.EarlyRollOffMinimumRate)
        {
            string chosen = preset.Name;
            preset = FilterCatalog.Get(FilterCatalog.EarlyRollOffSubstituteId);
            notes.Add($"{chosen} is meant for files above 48 kHz; {preset.Name} used");
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

    private static FilterPreset SubstituteIfNeeded(FilterPreset preset, List<string> notes, bool allowPolynomial, bool allowIir)
    {
        bool unsupported = (preset.Family == FilterFamily.Iir && !allowIir)
            || (preset.Family == FilterFamily.Polynomial && !allowPolynomial);
        if (!unsupported)
        {
            return preset;
        }

        notes.Add($"{preset.Name} cannot perform this ratio; {FilterCatalog.Get(FilterCatalog.FallbackId).Name} used");
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
            notes.Add($"{preset.Name} is defined by its own impulse, so the filter length setting does not apply to it");
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
                    $"{preset.Name} would need {length:N0} prototype taps for {AudioRates.Format(inputRate)} → {AudioRates.Format(outputRate)}. " +
                    "Choose a shorter filter or multi-stage processing.");
            }

            if (wanted > 0 && wanted < estimated)
            {
                notes.Add($"{wanted:N0} taps is shorter than this filter needs ({estimated:N0}), so its stopband will not reach {preset.AttenuationDb:0} dB");
            }

            PhaseResponse phase = preset.Phase;
            if (phase != PhaseResponse.Linear && length > MaxPhaseTransformLength)
            {
                phase = PhaseResponse.Linear;
                notes.Add("linear phase used because the prototype is too long for a phase transform");
            }

            prototype = FirDesign.DesignLowpass(spec, up, phase, length: wanted);
        }

        string name = $"{preset.Name} {AudioRates.FormatShort(inputRate)}→{AudioRates.FormatShort(outputRate)}";
        return convolution == ConvolutionMode.Automatic && FftPolyphaseStage.Suits(inputRate, outputRate, prototype.Length, options)
            ? FrequencyDomain(inputRate, outputRate, prototype, name, options)
            : new PolyphaseStage(inputRate, outputRate, prototype,
                $"{name} ({prototype.Length:N0} taps, {(prototype.Length + up - 1) / up:N0} per phase, tap by tap)");
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
                $"{taps:N0} taps at {AudioRates.FormatShort(finalRate)} is {scaled:N0} at " +
                $"{AudioRates.FormatShort(stageOutputRate)}, where this filter runs");
        }

        if (scaled > MaxPrototypeLength)
        {
            notes.Add($"{scaled:N0} taps is longer than this build will design, so {MaxPrototypeLength:N0} were used");
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
            $"{preset.Name} ×{factor} {AudioRates.FormatShort(inputRate)}→{AudioRates.FormatShort(outputRate)} (order {order})");
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
            $"Family bridge {AudioRates.FormatShort(inputRate)}→{AudioRates.FormatShort(outputRate)} " +
            $"({prototype.Length:N0} taps, {(prototype.Length + up - 1) / up:N0} per phase)");
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
