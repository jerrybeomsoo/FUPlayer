using System.Collections.Concurrent;
using FUPlayer.Core.Dsp.Design;

namespace FUPlayer.Core.Dsp.Modulation;

/// <summary>A selectable 1-bit delta-sigma modulator configuration.</summary>
public sealed record ModulatorPreset
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    public int Order { get; init; }

    /// <summary>Peak NTF gain; lower is more stable with less ultrasonic noise, higher gives lower in-band noise.</summary>
    public double MaxGain { get; init; }

    /// <summary>Audio band kept free of shaped noise (at DSD64; scales with the DSD rate when <see cref="ScaleBandwidth"/> is set).</summary>
    public double BandwidthHz { get; init; }

    public bool ScaleBandwidth { get; init; }

    /// <summary>Lowest DSD multiple (64, 128 …) the preset is intended for.</summary>
    public int MinimumDsdMultiplier { get; init; } = 64;
}

/// <summary>Built-in modulator presets and NTF design cache.</summary>
/// <remarks>
/// H∞ values were chosen from a stability sweep (1 kHz, 20 Hz and DC inputs, DSD64-DSD256): every preset runs
/// without a single loop reset at 55 % modulation, leaving margin above the 50 % level the engine feeds it.
/// </remarks>
public static class ModulatorCatalog
{
    public const string DefaultId = "order7";

    private static readonly ConcurrentDictionary<(string, int), NoiseTransferFunction> NtfCache = new();

    public static IReadOnlyList<ModulatorPreset> All { get; } =
    [
        new()
        {
            Id = "order5-gentle", Name = "5th order · gentle", Order = 5, MaxGain = 1.35, BandwidthHz = 20_000,
            Description = "Lowest noise gain and the widest stability margin. Leaves the least high-frequency noise.",
        },
        new()
        {
            Id = "order5", Name = "5th order", Order = 5, MaxGain = 1.45, BandwidthHz = 20_000,
            Description = "Moderate shaping with a low noise floor above the audio band. A good first choice when the DAC's analogue stage is simple.",
        },
        new()
        {
            Id = "order5-wide", Name = "5th order · 40 kHz band", Order = 5, MaxGain = 1.45, BandwidthHz = 40_000, MinimumDsdMultiplier = 128,
            Description = "Keeps the noise floor low up to 40 kHz. For DSD128 and higher.",
        },
        new()
        {
            Id = "order7", Name = "7th order", Order = 7, MaxGain = 1.4, BandwidthHz = 20_000,
            Description = "Stronger noise suppression inside the audio band. The default.",
        },
        new()
        {
            Id = "order7-wide", Name = "7th order · widening band", Order = 7, MaxGain = 1.4, BandwidthHz = 20_000, ScaleBandwidth = true, MinimumDsdMultiplier = 128,
            Description = "The low-noise band grows with the rate: 40 kHz at DSD128 and 80 kHz at DSD256.",
        },
        new()
        {
            Id = "order9", Name = "9th order", Order = 9, MaxGain = 1.35, BandwidthHz = 25_000, MinimumDsdMultiplier = 256,
            Description = "Very deep noise suppression for DSD256 and higher. Relies on a good analogue low-pass filter in the DAC.",
        },
    ];

    public static ModulatorPreset Get(string? id) =>
        All.FirstOrDefault(p => p.Id == id) ?? All.First(p => p.Id == DefaultId);

    /// <summary>Returns the (cached) NTF of a preset at a DSD rate.</summary>
    public static NoiseTransferFunction DesignNtf(ModulatorPreset preset, int dsdRate)
    {
        return NtfCache.GetOrAdd((preset.Id, dsdRate), _ =>
        {
            double bandwidth = preset.BandwidthHz;
            if (preset.ScaleBandwidth)
            {
                bandwidth *= Math.Max(1.0, Audio.AudioRates.DsdMultiplier(dsdRate) / 64.0);
            }

            double osr = dsdRate / (2.0 * bandwidth);
            return NoiseTransferFunction.Synthesize(preset.Order, osr, preset.MaxGain);
        });
    }
}
