using FUPlayer.Core.Audio;
using FUPlayer.Core.Dsp.Dsd;
using FUPlayer.Core.Dsp.Modulation;
using FUPlayer.Core.Dsp.Quantization;
using FUPlayer.Core.Dsp.Resampling;
using FUPlayer.Core.Output;

namespace FUPlayer.Core.Engine;

/// <summary>The complete processing decision for one source format.</summary>
public sealed record PlaybackPlan
{
    public required StreamFormat Source { get; init; }

    public required OutputFormat Output { get; init; }

    /// <summary>PCM rate at which volume, limiter and speaker trims run (0 for DSD pass-through).</summary>
    public int ProcessingRate { get; init; }

    /// <summary>PCM rate entering the resampler: the source rate, or the DSD→PCM conversion rate.</summary>
    public int ConversionRate { get; init; }

    public FilterPreset? Filter { get; init; }

    public StagingMode Staging { get; init; }

    /// <summary>Requested filter length in taps at the output rate; 0 designs it from the filter's specification.</summary>
    public int FilterTaps { get; init; }

    /// <summary>How the filter's arithmetic is carried out.</summary>
    public ConvolutionMode Convolution { get; init; }

    /// <summary>Where frequency-domain convolution takes over, and how long a block it may hold input in.</summary>
    public ConvolutionOptions ConvolutionOptions { get; init; }

    public DitherPreset? Dither { get; init; }

    public ModulatorPreset? Modulator { get; init; }

    public DsdFilterPreset? DsdFilter { get; init; }

    public double DsdConversionGain { get; init; } = 1.0;

    /// <summary>A DSD source sent to a DSD output unchanged.</summary>
    public bool PassThrough { get; init; }

    public bool RemoveUltrasonics { get; init; }

    /// <summary>Run the look-ahead peak limiter after volume.</summary>
    public bool Limiter { get; init; } = true;

    public double PcmLevelOffsetDb { get; init; }

    /// <summary>Human-readable explanations of substitutions and limits.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    public bool IsDsdOutput => Output.IsDsd;

    public string FilterName => PassThrough ? "None (DSD pass-through)" : Filter?.Name ?? "-";

    public string QuantizerName => PassThrough
        ? "Unchanged DSD"
        : IsDsdOutput ? Modulator?.Name ?? "-" : Dither?.Name ?? "-";

    /// <summary>True when both plans process audio identically, so tracks can be joined gaplessly.</summary>
    public bool HasSameProcessing(PlaybackPlan other) =>
        Source == other.Source
        && Output == other.Output
        && ProcessingRate == other.ProcessingRate
        && ConversionRate == other.ConversionRate
        && Filter?.Id == other.Filter?.Id
        && Staging == other.Staging
        && FilterTaps == other.FilterTaps
        && Convolution == other.Convolution
        && ConvolutionOptions == other.ConvolutionOptions
        && Dither?.Id == other.Dither?.Id
        && Modulator?.Id == other.Modulator?.Id
        && DsdFilter?.Id == other.DsdFilter?.Id
        && DsdConversionGain == other.DsdConversionGain
        && PassThrough == other.PassThrough
        && RemoveUltrasonics == other.RemoveUltrasonics
        && Limiter == other.Limiter
        && PcmLevelOffsetDb == other.PcmLevelOffsetDb;
}
