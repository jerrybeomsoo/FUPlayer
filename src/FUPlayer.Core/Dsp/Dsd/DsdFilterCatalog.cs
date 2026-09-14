using FUPlayer.Core.Dsp.Design;

namespace FUPlayer.Core.Dsp.Dsd;

/// <summary>Low-pass applied when a DSD stream is turned into PCM (removes DSD's shaped high-frequency noise).</summary>
public sealed record DsdFilterPreset
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    /// <summary>Passband edge for DSD64; scaled proportionally for higher DSD rates.</summary>
    public double PassbandHz { get; init; }

    /// <summary>Stopband edge for DSD64; scaled proportionally for higher DSD rates.</summary>
    public double StopbandHz { get; init; }

    public double AttenuationDb { get; init; }

    public PhaseResponse Phase { get; init; } = PhaseResponse.Linear;
}

public static class DsdFilterCatalog
{
    public const string DefaultId = "balanced";

    /// <summary>Widest preset; used before DSD files are modulated again.</summary>
    public const string WidebandId = "wideband";

    public static IReadOnlyList<DsdFilterPreset> All { get; } =
    [
        new()
        {
            Id = "balanced", Name = "Balanced", PassbandHz = 35_000, StopbandHz = 70_000, AttenuationDb = 140,
            Description = "Passband to 35 kHz at DSD64 (scaled with the DSD rate) and 140 dB rejection: a wide band with most of the high-frequency noise removed.",
        },
        new()
        {
            Id = "balanced-mp", Name = "Balanced · minimum phase", PassbandHz = 35_000, StopbandHz = 70_000, AttenuationDb = 140,
            Phase = PhaseResponse.Minimum,
            Description = "The balanced low-pass without ringing ahead of transients.",
        },
        new()
        {
            Id = "quiet", Name = "Quiet", PassbandHz = 26_000, StopbandHz = 48_000, AttenuationDb = 150,
            Description = "Passband to 26 kHz at DSD64 and 150 dB rejection, for systems that should receive as little high-frequency energy as possible.",
        },
        new()
        {
            Id = "wideband", Name = "Wideband", PassbandHz = 45_000, StopbandHz = 100_000, AttenuationDb = 120,
            Description = "Passband to 45 kHz at DSD64: the widest band, with more residual high-frequency noise.",
        },
        new()
        {
            Id = "sharp", Name = "Sharp", PassbandHz = 22_000, StopbandHz = 26_000, AttenuationDb = 160,
            Description = "Very steep cut just above 22 kHz at DSD64 with 160 dB rejection: practically nothing remains above the audio band.",
        },
    ];

    public static DsdFilterPreset Get(string? id) =>
        All.FirstOrDefault(p => p.Id == id) ?? All.First(p => p.Id == DefaultId);
}
