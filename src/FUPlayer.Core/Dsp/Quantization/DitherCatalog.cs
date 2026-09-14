namespace FUPlayer.Core.Dsp.Quantization;

/// <summary>Word-length reduction method.</summary>
public enum DitherKind
{
    /// <summary>Rounding only.</summary>
    None,

    /// <summary>Rectangular (uniform) dither, ±½ LSB.</summary>
    Rectangular,

    /// <summary>Triangular dither, ±1 LSB.</summary>
    Triangular,

    /// <summary>Gaussian dither, σ = ½ LSB.</summary>
    Gaussian,

    /// <summary>High-pass triangular dither (difference of successive uniform values).</summary>
    HighPassTriangular,

    /// <summary>Error-feedback noise shaping with a synthesised NTF and in-loop triangular dither.</summary>
    NoiseShaped,

    /// <summary>Fixed psychoacoustic error-feedback filter for 44.1/48 kHz output.</summary>
    Psychoacoustic,
}

/// <summary>A selectable dither / noise-shaping algorithm.</summary>
public sealed record DitherPreset
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    public DitherKind Kind { get; init; }

    public int ShaperOrder { get; init; }

    /// <summary>Peak noise gain (H∞) of the synthesised NTF.</summary>
    public double ShaperMaxGain { get; init; }

    public bool OptimizeZeros { get; init; } = true;

    /// <summary>Output rates the preset is designed for.</summary>
    public string RecommendedFor { get; init; } = string.Empty;
}

/// <summary>Built-in dither and noise-shaping presets.</summary>
public static class DitherCatalog
{
    public const string AutoId = "auto";

    /// <summary>Band kept free of shaped noise by the synthesised shapers.</summary>
    public const double ShapingBandwidthHz = 20_000.0;

    /// <summary>
    /// 5-tap E-weighted error filter for 44.1 kHz from Lipshitz, Vanderkooy and Wannamaker,
    /// "Minimally Audible Noise Shaping" (JAES, 1991). NTF(z) = 1 − Σ cₖ·z⁻ᵏ.
    /// </summary>
    internal static readonly double[] PsychoacousticCoefficients = [2.033, -2.165, 1.959, -1.590, 0.6149];

    public static IReadOnlyList<DitherPreset> All { get; } =
    [
        new()
        {
            Id = AutoId, Name = "Automatic", Kind = DitherKind.Triangular,
            Description = "Picks a method from the output rate: triangular dither up to 48 kHz, high-pass triangular up to 96 kHz, noise shaping above.",
            RecommendedFor = "Any rate",
        },
        new()
        {
            Id = "none", Name = "Truncate (no dither)", Kind = DitherKind.None,
            Description = "Plain rounding to the output word length. Meant for bit-exact checks; leaves quantization distortion in normal listening.",
            RecommendedFor = "Tests",
        },
        new()
        {
            Id = "rectangular", Name = "Rectangular dither", Kind = DitherKind.Rectangular,
            Description = "Uniform noise of ±½ LSB. The lightest dither; the remaining noise modulation only matters at short word lengths.",
            RecommendedFor = "Deep word lengths",
        },
        new()
        {
            Id = "triangular", Name = "Triangular dither", Kind = DitherKind.Triangular,
            Description = "Sum of two uniform sources: removes quantization distortion and noise modulation completely at a small noise cost.",
            RecommendedFor = "Up to 48 kHz",
        },
        new()
        {
            Id = "gaussian", Name = "Gaussian dither", Kind = DitherKind.Gaussian,
            Description = "Normally distributed, spectrally flat dither. Behaves like triangular dither with a softer amplitude distribution.",
            RecommendedFor = "Up to 96 kHz",
        },
        new()
        {
            Id = "triangular-hp", Name = "High-pass triangular dither", Kind = DitherKind.HighPassTriangular,
            Description = "Triangular dither built from successive differences of one noise source, so its energy rises toward high frequencies.",
            RecommendedFor = "88.2-96 kHz",
        },
        new()
        {
            Id = "psychoacoustic", Name = "Perceptual noise shaping", Kind = DitherKind.Psychoacoustic,
            Description = "Five-tap error filter weighted by hearing sensitivity (Lipshitz et al.), lowering noise where the ear is most sensitive.",
            RecommendedFor = "44.1-48 kHz",
        },
        new()
        {
            Id = "shaped-1", Name = "Noise shaping · order 1", Kind = DitherKind.NoiseShaped,
            ShaperOrder = 1, ShaperMaxGain = 1.99, OptimizeZeros = false,
            Description = "First-order slope: a modest in-band improvement that moves quantization noise upward in frequency.",
            RecommendedFor = "176.4 kHz and up",
        },
        new()
        {
            Id = "shaped-5", Name = "Noise shaping · order 5", Kind = DitherKind.NoiseShaped,
            ShaperOrder = 5, ShaperMaxGain = 6.0,
            Description = "Five zeros spread across 0-20 kHz with a gentle noise rise above the band. Needs a lot of room above 20 kHz.",
            RecommendedFor = "352.8 kHz and up",
        },
        new()
        {
            Id = "shaped-9", Name = "Noise shaping · order 9", Kind = DitherKind.NoiseShaped,
            ShaperOrder = 9, ShaperMaxGain = 16.0,
            Description = "Deep in-band noise reduction with a steep noise rise just above 20 kHz, which lets it work at moderate rates.",
            RecommendedFor = "176.4 kHz and up",
        },
        new()
        {
            Id = "shaped-15", Name = "Noise shaping · order 15", Kind = DitherKind.NoiseShaped,
            ShaperOrder = 15, ShaperMaxGain = 32.0,
            Description = "The deepest in-band noise reduction, spread over a wide ultrasonic region. Also helps DACs fed with fewer bits than they accept.",
            RecommendedFor = "705.6 kHz and up",
        },
    ];

    public static DitherPreset Get(string? id) =>
        All.FirstOrDefault(p => p.Id == id) ?? All.First(p => p.Id == "triangular");

    /// <summary>Resolves <see cref="AutoId"/> into a concrete preset for the output rate.</summary>
    public static DitherPreset Resolve(string? id, int outputRate)
    {
        if (id != AutoId)
        {
            return Get(id);
        }

        string chosen = outputRate switch
        {
            < 88_200 => "triangular",
            < 176_400 => "triangular-hp",
            < 352_800 => "shaped-9",
            < 705_600 => "shaped-5",
            _ => "shaped-15",
        };
        return Get(chosen);
    }
}
