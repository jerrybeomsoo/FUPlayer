using FUPlayer.Core.Audio;
using FUPlayer.Core.Dsp.Dsd;
using FUPlayer.Core.Dsp.Modulation;
using FUPlayer.Core.Dsp.Processing;
using FUPlayer.Core.Dsp.Quantization;
using FUPlayer.Core.Dsp.Resampling;
using FUPlayer.Core.Output;
using FUPlayer.Core.Settings;

namespace FUPlayer.Core.Engine;

/// <summary>Negotiates output format, rates, filters and shaping for a source against device capabilities.</summary>
public static class OutputPlanner
{
    public static PlaybackPlan Plan(StreamFormat source, PlayerSettings settings, IAudioBackend backend, DeviceCapabilities capabilities)
    {
        var notes = new List<string>();
        int channels = Math.Clamp(settings.Output.Channels, 1, Math.Max(1, capabilities.MaxChannels));
        if (channels != settings.Output.Channels)
        {
            notes.Add($"The device offers {capabilities.MaxChannels} channels; using {channels}.");
        }

        if (source.Channels > channels)
        {
            notes.Add($"The source has {source.Channels} channels; only the first {channels} are played.");
        }

        OutputMode mode = settings.Output.Mode == OutputMode.FollowSource
            ? source.IsDsd ? OutputMode.Dsd : OutputMode.Pcm
            : settings.Output.Mode;

        if (mode == OutputMode.Dsd)
        {
            if (!backend.IsBitPerfect)
            {
                notes.Add($"{backend.DisplayName} mixes the signal, which would destroy DSD; playing PCM instead.");
            }
            else if (PlanDsd(source, settings, backend, capabilities, channels, notes) is { } dsd)
            {
                return WithUpscaling(dsd, settings.Restoration, notes);
            }
            else
            {
                notes.Add("The device accepts none of the configured DSD rates; playing PCM instead.");
            }
        }

        return WithUpscaling(PlanPcm(source, settings, capabilities, channels, notes), settings.Restoration, notes);
    }

    /// <summary>
    /// Adds the neural upscaler for a PCM source at 44.1 or 48 kHz, the rates it was trained from. It runs at
    /// twice the source rate, and the chosen filter converts from there to the output rate, up or down, so
    /// the filter has to accept that conversion.
    ///
    /// The limiter runs whenever the upscaler does, whatever its own switch says. The band the network
    /// writes adds energy, and on loud masters its peaks cross full scale: measured on a held-out song
    /// played from a lossless file, 1.014 at the output. Those overs are the network's, not the
    /// recording's, and left alone they would clip or drive a DSD modulator the way a lossy decode's did.
    /// </summary>
    internal static PlaybackPlan WithUpscaling(PlaybackPlan plan, RestorationSettings restore, List<string> notes)
    {
        if (!restore.NeuralUpscaler || plan.PassThrough || plan.Source.IsDsd || plan.Filter is null)
        {
            return plan;
        }

        if (plan.ConversionRate is not (44_100 or 48_000))
        {
            return plan;
        }

        // Twice the source rate whatever the output: an output below that keeps the passband correction and
        // the filter takes the synthesised band back out. Whether a lossless source is worth running at such
        // an output is decided once its codec is known (PlaybackEngine.ApplySource).
        int upscale = plan.ConversionRate * 2;

        if (!ResamplerFactory.IsSupported(plan.Filter, upscale, plan.ProcessingRate))
        {
            notes.Add($"{plan.Filter.Name} cannot convert {AudioRates.Format(upscale)} to {AudioRates.Format(plan.ProcessingRate)}; the neural upscaler is not running.");
            return plan;
        }

        return plan with { UpscaleRate = upscale, Limiter = true, Notes = [.. plan.Notes, .. notes.Except(plan.Notes)] };
    }

    /// <summary>The configured filter, or a substitute when an early roll-off filter meets a 44.1/48 kHz source.</summary>
    private static FilterPreset ChooseFilter(string id, int sourceRate, List<string> notes)
    {
        FilterPreset filter = FilterCatalog.Get(id);
        if (filter.EarlyRollOff && sourceRate < FilterCatalog.EarlyRollOffMinimumRate)
        {
            FilterPreset substitute = FilterCatalog.Get(FilterCatalog.EarlyRollOffSubstituteId);
            notes.Add($"{filter.Name} is meant for sources above 48 kHz; {AudioRates.Format(sourceRate)} material uses {substitute.Name}.");
            return substitute;
        }

        return filter;
    }

    private static PlaybackPlan PlanPcm(StreamFormat source, PlayerSettings settings, DeviceCapabilities capabilities, int channels, List<string> notes)
    {
        PcmSettings pcm = settings.Pcm;
        int conversionRate = source.IsDsd ? source.SampleRate / 16 : source.SampleRate;
        IReadOnlyList<int> deviceRates = capabilities.PcmRates.Count > 0 ? capabilities.PcmRates : AudioRates.StandardPcmRates;
        FilterPreset filter = ChooseFilter(pcm.Filter, conversionRate, notes);

        int rate;
        if (filter.Family == FilterFamily.None)
        {
            if (deviceRates.Contains(conversionRate))
            {
                rate = conversionRate;
            }
            else
            {
                filter = FilterCatalog.Get(FilterCatalog.FallbackId);
                rate = ChooseRate(conversionRate, deviceRates, int.MaxValue, RateSelection.Automatic, 0, filter, notes);
                notes.Add($"The device does not accept {AudioRates.Format(conversionRate)}; resampling with {filter.Name}.");
            }
        }
        else
        {
            int limit = pcm.RateLimit > 0 ? pcm.RateLimit : int.MaxValue;
            rate = ChooseRate(conversionRate, deviceRates, limit, pcm.RateSelection, pcm.FixedRate, filter, notes);
            if (!ResamplerFactory.IsSupported(filter, conversionRate, rate))
            {
                FilterPreset fallback = FilterCatalog.Get(FilterCatalog.FallbackId);
                notes.Add($"{filter.Name} cannot convert {AudioRates.Format(conversionRate)} to {AudioRates.Format(rate)}; using {fallback.Name}.");
                filter = fallback;
            }
        }

        (int validBits, int containerBits) = ChooseBits(pcm.DacBits, capabilities.ContainerBits);
        if (validBits < pcm.DacBits)
        {
            notes.Add($"The device accepts at most {containerBits}-bit samples.");
        }

        return new PlaybackPlan
        {
            Source = source,
            Output = OutputFormat.Pcm(rate, channels, validBits, containerBits),
            ProcessingRate = rate,
            ConversionRate = conversionRate,
            Filter = filter,
            Staging = pcm.Staging,
            FilterTaps = pcm.FilterTaps,
            Dither = DitherCatalog.Resolve(pcm.DitherId, rate),
            DsdFilter = source.IsDsd ? DsdFilterCatalog.Get(settings.DsdToPcm.FilterId) : null,
            DsdConversionGain = source.IsDsd && settings.DsdToPcm.RestoreLevel ? 2.0 : 1.0,
            RemoveUltrasonics = settings.Processing.RemoveUltrasonics && !source.IsDsd && conversionRate >= ProcessingFilters.UltrasonicFilterMinimumRate,
            Limiter = settings.Processing.Limiter,
            Convolution = settings.Processing.Convolution,
            ConvolutionOptions = new ConvolutionOptions
            {
                ThresholdTapsPerPhase = settings.Processing.ConvolutionThresholdTaps,
                MaxBlockMilliseconds = settings.Processing.ConvolutionMaxBlockMs,
                LayeredBlocks = !settings.Processing.ConvolutionUniformBlocks,
            },
            PcmLevelOffsetDb = settings.Volume.PcmLevelOffsetDb,
            Notes = notes,
        };
    }

    private static PlaybackPlan? PlanDsd(StreamFormat source, PlayerSettings settings, IAudioBackend backend, DeviceCapabilities capabilities, int channels, List<string> notes)
    {
        DsdOutputSettings dsd = settings.Dsd;
        bool family48k = settings.Output.Use48kDsdRates && !AudioRates.Is44k1Family(source.SampleRate);
        bool native = settings.Output.DsdTransport == DsdTransport.Native && backend.SupportsNativeDsd && capabilities.NativeDsdRates.Count > 0;
        if (settings.Output.DsdTransport == DsdTransport.Native && !native)
        {
            notes.Add("This output cannot take native DSD, so DSD travels inside PCM frames (DoP).");
        }

        int limit = Math.Clamp(dsd.HighestMultiplier, 64, 1024);
        var candidates = new List<OutputFormat>();
        foreach (int multiplier in AudioRates.DsdMultipliers.OrderDescending())
        {
            if (multiplier > limit)
            {
                continue;
            }

            int dsdRate = AudioRates.DsdRate(multiplier, family48k);
            if (native)
            {
                if (capabilities.NativeDsdRates.Contains(dsdRate))
                {
                    candidates.Add(OutputFormat.NativeDsd(dsdRate, channels));
                }
            }
            else if (capabilities.SupportsDop(dsdRate))
            {
                candidates.Add(OutputFormat.Dop(dsdRate, channels, capabilities.ContainerBits.Contains(24) ? 24 : 32));
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        OutputFormat output = candidates[0];
        if (dsd.RateSelection == RateSelection.Fixed)
        {
            int wanted = AudioRates.DsdRate(limit, family48k);
            OutputFormat? exact = candidates.FirstOrDefault(c => c.DsdRate == wanted);
            if (exact is not null)
            {
                output = exact;
            }
            else
            {
                notes.Add($"DSD{limit} is not available; using DSD{AudioRates.DsdMultiplier(output.DsdRate)}.");
            }
        }

        if (source.IsDsd && dsd.PassThrough)
        {
            OutputFormat? direct = candidates.FirstOrDefault(c => c.DsdRate == source.SampleRate);
            if (direct is not null)
            {
                return new PlaybackPlan { Source = source, Output = direct, PassThrough = true, Notes = notes };
            }

            notes.Add("Pass-through needs the file's own DSD rate at the output; converting it instead.");
        }

        int processingRate = Math.Min(AudioRates.FamilyBase(output.DsdRate) * 16, output.DsdRate / 4);
        FilterPreset filter;
        int conversionRate;
        DsdFilterPreset? dsdFilter = null;
        if (source.IsDsd)
        {
            conversionRate = source.SampleRate / 8;
            while (conversionRate > processingRate && conversionRate % 2 == 0)
            {
                conversionRate /= 2;
            }

            filter = FilterCatalog.Get(FilterCatalog.DsdRemodulationId);
            dsdFilter = DsdFilterCatalog.Get(dsd.InputFilterId);
        }
        else
        {
            conversionRate = source.SampleRate;
            filter = ChooseFilter(dsd.Filter, conversionRate, notes);
            if (filter.Family == FilterFamily.None || !ResamplerFactory.IsSupported(filter, conversionRate, processingRate))
            {
                FilterPreset fallback = FilterCatalog.Get(FilterCatalog.FallbackId);
                notes.Add($"{filter.Name} cannot feed the modulator from {AudioRates.Format(conversionRate)}; using {fallback.Name}.");
                filter = fallback;
            }
        }

        ModulatorPreset modulator = ModulatorCatalog.Get(dsd.ModulatorId);
        if (AudioRates.DsdMultiplier(output.DsdRate) < modulator.MinimumDsdMultiplier)
        {
            notes.Add($"{modulator.Name} is intended for DSD{modulator.MinimumDsdMultiplier} and above.");
        }

        return new PlaybackPlan
        {
            Source = source,
            Output = output,
            ProcessingRate = processingRate,
            ConversionRate = conversionRate,
            Filter = filter,
            Staging = dsd.Staging,
            // A DSD file is brought back to the modulator with a fixed half-band filter, so a tap count does not apply.
            FilterTaps = source.IsDsd ? 0 : dsd.FilterTaps,
            Modulator = modulator,
            DsdFilter = dsdFilter,
            DsdConversionGain = source.IsDsd ? 2.0 : 1.0,
            RemoveUltrasonics = settings.Processing.RemoveUltrasonics && !source.IsDsd && conversionRate >= ProcessingFilters.UltrasonicFilterMinimumRate,
            Limiter = settings.Processing.Limiter,
            Convolution = settings.Processing.Convolution,
            ConvolutionOptions = new ConvolutionOptions
            {
                ThresholdTapsPerPhase = settings.Processing.ConvolutionThresholdTaps,
                MaxBlockMilliseconds = settings.Processing.ConvolutionMaxBlockMs,
                LayeredBlocks = !settings.Processing.ConvolutionUniformBlocks,
            },
            Notes = notes,
        };
    }

    private static int ChooseRate(int sourceRate, IReadOnlyList<int> deviceRates, int limit, RateSelection selection, int fixedRate, FilterPreset filter, List<string> notes)
    {
        List<int> usable = deviceRates.Where(r => r <= limit && ResamplerFactory.IsSupported(filter, sourceRate, r)).OrderDescending().ToList();
        if (usable.Count == 0)
        {
            usable = deviceRates.Where(r => r <= limit).OrderDescending().ToList();
            if (usable.Count == 0)
            {
                usable = [deviceRates.Min()];
                notes.Add("The rate limit is below every device rate; using the lowest rate the device accepts.");
            }
        }

        if (selection == RateSelection.Fixed)
        {
            if (usable.Contains(fixedRate))
            {
                return fixedRate;
            }

            notes.Add($"The fixed rate {AudioRates.Format(fixedRate)} is not available; choosing automatically.");
        }

        int sameFamily = usable.FirstOrDefault(r => AudioRates.IsSameFamily(r, sourceRate));
        if (selection == RateSelection.SameFamily)
        {
            if (sameFamily > 0)
            {
                return sameFamily;
            }

            notes.Add("No rate of the source's family is available; converting across rate families.");
            return usable[0];
        }

        // Stay in the source family unless the other family offers more than twice the rate.
        return sameFamily > 0 && sameFamily * 2 > usable[0] ? sameFamily : usable[0];
    }

    private static (int ValidBits, int ContainerBits) ChooseBits(int dacBits, IReadOnlyList<int> containers)
    {
        dacBits = Math.Clamp(dacBits, 16, 32);
        List<int> sorted = (containers.Count > 0 ? containers : [16, 24, 32]).Order().ToList();
        int container = sorted.FirstOrDefault(b => b >= dacBits);
        if (container == 0)
        {
            container = sorted[^1];
        }

        return (Math.Min(dacBits, container), container);
    }
}
