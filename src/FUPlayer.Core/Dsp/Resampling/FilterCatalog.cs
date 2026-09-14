using FUPlayer.Core.Dsp.Design;

namespace FUPlayer.Core.Dsp.Resampling;

/// <summary>Filter family (determines the design method).</summary>
public enum FilterFamily
{
    None,
    Sinc,
    GaussianSinc,
    Halfband,
    LowRinging,
    Polynomial,
    Iir,
}

public enum PolynomialKind
{
    Linear,
    CubicHermite,
}

/// <summary>A user-selectable oversampling / resampling filter.</summary>
public sealed record FilterPreset
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>UI grouping.</summary>
    public required string Group { get; init; }

    public required string Description { get; init; }

    public FilterFamily Family { get; init; }

    public PhaseResponse Phase { get; init; } = PhaseResponse.Linear;

    /// <summary>End of the passband as a fraction of the (narrower) Nyquist frequency.</summary>
    public double PassbandFraction { get; init; }

    /// <summary>Start of full attenuation as a fraction of the (narrower) Nyquist frequency.</summary>
    public double StopbandFraction { get; init; }

    public double AttenuationDb { get; init; }

    public double PassbandRippleDb { get; init; }

    /// <summary>Reaches full attenuation below Nyquist, suppressing ringing/aliasing from the source ADC.</summary>
    public bool Apodizing { get; init; }

    public bool IntegerRatioOnly { get; init; }

    /// <summary>Rolls off far below the source Nyquist frequency; only meaningful for sources above 48 kHz.</summary>
    public bool EarlyRollOff { get; init; }

    public PolynomialKind Polynomial { get; init; }

    public WindowKind Window => Family == FilterFamily.GaussianSinc ? WindowKind.Gaussian : WindowKind.Kaiser;

    /// <summary>
    /// Whether an explicit filter length applies. A windowed sinc can be made as long as one likes, but a filter
    /// whose character is its short impulse, an interpolating polynomial and an IIR cascade all keep their own length.
    /// </summary>
    public bool SupportsCustomLength => Family is FilterFamily.Sinc or FilterFamily.GaussianSinc or FilterFamily.Halfband;

    public string PhaseLabel => Phase switch
    {
        PhaseResponse.Minimum => "minimum phase",
        PhaseResponse.Intermediate => "intermediate phase",
        _ => "linear phase",
    };
}

/// <summary>Built-in filter presets.</summary>
public static class FilterCatalog
{
    public const string NoneId = "none";
    public const string DefaultId = "gauss-steep";
    public const string FallbackId = "kaiser-balanced";

    /// <summary>Used instead of an early roll-off filter for sources at 48 kHz and below.</summary>
    public const string EarlyRollOffSubstituteId = "gauss-balanced";

    /// <summary>Lowest source rate for early roll-off filters.</summary>
    public const int EarlyRollOffMinimumRate = 50_000;

    /// <summary>Filter that brings converted DSD files up to the modulator rate.</summary>
    public const string DsdRemodulationId = "halfband-steep";

    public static IReadOnlyList<FilterPreset> All { get; } = Build();

    public static FilterPreset Get(string? id) =>
        (id is null ? null : All.FirstOrDefault(p => p.Id == id)) ?? All.First(p => p.Id == FallbackId);

    public static bool TryGet(string? id, out FilterPreset preset)
    {
        preset = All.FirstOrDefault(p => p.Id == id)!;
        return preset is not null;
    }

    private static List<FilterPreset> Build()
    {
        var list = new List<FilterPreset>
        {
            new()
            {
                Id = NoneId, Name = "None (bit-perfect)", Group = "Bypass", Family = FilterFamily.None,
                Description = "No sample-rate conversion: the output runs at the source rate and only the word length is adapted.",
            },
        };

        AddKaiser(list, "kaiser-compact", "Kaiser sinc · compact", 0.84, 1.0, 110,
            "Short Kaiser-windowed sinc. Little ringing; the response starts to fall inside the top octave.",
            PhaseResponse.Linear, PhaseResponse.Minimum);
        AddKaiser(list, "kaiser-balanced", "Kaiser sinc · balanced", 0.905, 1.0, 150,
            "General-purpose Kaiser sinc with 150 dB image rejection and a flat audio band.",
            PhaseResponse.Linear, PhaseResponse.Minimum);
        AddKaiser(list, "kaiser-steep", "Kaiser sinc · steep", 0.95, 1.0, 190,
            "Steep Kaiser sinc that is completely closed at the source's Nyquist frequency.",
            PhaseResponse.Linear, PhaseResponse.Intermediate, PhaseResponse.Minimum);
        AddKaiser(list, "kaiser-very-steep", "Kaiser sinc · very steep", 0.98, 1.0, 230,
            "Very narrow transition band and the flattest passband of the Kaiser set; rings longest near Nyquist.",
            PhaseResponse.Linear);
        AddKaiser(list, "kaiser-extreme", "Kaiser sinc · extreme", 0.995, 1.0, 250,
            "The sharpest transition in the catalogue. Very long, so use multi-stage conversion with it.",
            PhaseResponse.Linear);

        AddGauss(list, "gauss-compact", "Gaussian sinc · compact", 0.80, 1.0, 140, apodizing: false, early: false,
            "Gaussian window: no ripple in the transition band and a compact impulse.",
            PhaseResponse.Linear);
        AddGauss(list, "gauss-balanced", "Gaussian sinc · balanced", 0.87, 1.0, 200, apodizing: false, early: false,
            "Gaussian sinc with an even trade between time and frequency behaviour and 200 dB rejection.",
            PhaseResponse.Linear, PhaseResponse.Minimum);
        AddGauss(list, "gauss-steep", "Gaussian sinc · steep", 0.92, 1.0, 240, apodizing: false, early: false,
            "Gaussian sinc with 240 dB rejection and a smooth, steep transition. The default.",
            PhaseResponse.Linear, PhaseResponse.Intermediate, PhaseResponse.Minimum);
        AddGauss(list, "gauss-very-steep", "Gaussian sinc · very steep", 0.96, 1.0, 260, apodizing: false, early: false,
            "The widest passband of the Gaussian set.",
            PhaseResponse.Linear);
        AddGauss(list, "gauss-apodizing", "Gaussian sinc · apodizing", 0.83, 0.97, 240, apodizing: true, early: false,
            "Closes before the source Nyquist frequency, so ringing and aliasing already present in the recording are not passed on.",
            PhaseResponse.Linear, PhaseResponse.Minimum);
        AddGauss(list, "gauss-early", "Gaussian sinc · early roll-off", 0.42, 1.0, 240, apodizing: false, early: true,
            "For files above 48 kHz: starts rolling off far below the source Nyquist frequency, with a very short impulse. Removes recorded high-frequency noise. 44.1/48 kHz files use the balanced Gaussian filter instead.",
            PhaseResponse.Linear, PhaseResponse.Minimum);

        list.Add(new FilterPreset
        {
            Id = "halfband-compact", Name = "Half-band · compact", Group = "Half-band", Family = FilterFamily.Halfband,
            PassbandFraction = 0.78, StopbandFraction = 1.22, AttenuationDb = 120,
            Description = "Symmetric transition centred on Nyquist (−6 dB there): an extremely short impulse, with slight leakage above Nyquist.",
        });
        list.Add(new FilterPreset
        {
            Id = "halfband-steep", Name = "Half-band · steep", Group = "Half-band", Family = FilterFamily.Halfband,
            PassbandFraction = 0.93, StopbandFraction = 1.07, AttenuationDb = 200,
            Description = "Half-band design with a narrow transition: wide passband and very little leakage.",
        });

        list.Add(new FilterPreset
        {
            Id = "short-fir", Name = "Short FIR · low ringing", Group = "Low ringing", Family = FilterFamily.LowRinging,
            PassbandFraction = 0.55, StopbandFraction = 1.45, AttenuationDb = 80,
            Description = "A few dozen taps: far less ringing than sinc designs, with better image rejection than interpolation.",
        });
        list.Add(new FilterPreset
        {
            Id = "short-fir-mp", Name = "Short FIR · low ringing (minimum phase)", Group = "Low ringing", Family = FilterFamily.LowRinging,
            Phase = PhaseResponse.Minimum, PassbandFraction = 0.55, StopbandFraction = 1.45, AttenuationDb = 80,
            Description = "The short FIR in minimum phase: nothing rings before a transient.",
        });
        list.Add(new FilterPreset
        {
            Id = "interp-linear", Name = "Linear interpolation", Group = "Low ringing", Family = FilterFamily.Polynomial,
            Polynomial = PolynomialKind.Linear,
            Description = "Straight lines between samples. Never rings, but lets images through and softens the top octave.",
        });
        list.Add(new FilterPreset
        {
            Id = "interp-cubic", Name = "Cubic interpolation (Catmull-Rom)", Group = "Low ringing", Family = FilterFamily.Polynomial,
            Polynomial = PolynomialKind.CubicHermite,
            Description = "Spline between samples: one cycle of ringing and a flatter passband than linear interpolation; images still leak.",
        });

        list.Add(new FilterPreset
        {
            Id = "elliptic-steep", Name = "Elliptic IIR · steep", Group = "Recursive (elliptic)", Family = FilterFamily.Iir,
            PassbandFraction = 0.91, StopbandFraction = 1.0, AttenuationDb = 110, PassbandRippleDb = 0.01,
            IntegerRatioOnly = true,
            Description = "Recursive elliptic filter: no pre-echo, a long decay and tiny passband ripple. Integer rate ratios only.",
        });
        list.Add(new FilterPreset
        {
            Id = "elliptic-gentle", Name = "Elliptic IIR · gentle", Group = "Recursive (elliptic)", Family = FilterFamily.Iir,
            PassbandFraction = 0.80, StopbandFraction = 1.10, AttenuationDb = 90, PassbandRippleDb = 0.002,
            IntegerRatioOnly = true,
            Description = "Lower-order elliptic filter with a shorter decay and negligible ripple. Integer rate ratios only.",
        });

        return list;
    }

    private static void AddKaiser(List<FilterPreset> list, string id, string name, double pass, double stop, double attenuation, string description, params PhaseResponse[] phases)
    {
        foreach (PhaseResponse phase in phases)
        {
            list.Add(new FilterPreset
            {
                Id = id + Suffix(phase), Name = name + NameSuffix(phase), Group = "Kaiser-windowed sinc", Family = FilterFamily.Sinc, Phase = phase,
                PassbandFraction = pass, StopbandFraction = stop, AttenuationDb = attenuation,
                Description = description + PhaseNote(phase),
            });
        }
    }

    private static void AddGauss(List<FilterPreset> list, string id, string name, double pass, double stop, double attenuation, bool apodizing, bool early, string description, params PhaseResponse[] phases)
    {
        foreach (PhaseResponse phase in phases)
        {
            list.Add(new FilterPreset
            {
                Id = id + Suffix(phase), Name = name + NameSuffix(phase), Group = "Gaussian-windowed sinc", Family = FilterFamily.GaussianSinc, Phase = phase,
                PassbandFraction = pass, StopbandFraction = stop, AttenuationDb = attenuation, Apodizing = apodizing, EarlyRollOff = early,
                Description = description + PhaseNote(phase),
            });
        }
    }

    private static string Suffix(PhaseResponse phase) => phase switch
    {
        PhaseResponse.Minimum => "-mp",
        PhaseResponse.Intermediate => "-ip",
        _ => string.Empty,
    };

    private static string NameSuffix(PhaseResponse phase) => phase switch
    {
        PhaseResponse.Minimum => " (minimum phase)",
        PhaseResponse.Intermediate => " (intermediate phase)",
        _ => string.Empty,
    };

    private static string PhaseNote(PhaseResponse phase) => phase switch
    {
        PhaseResponse.Minimum => " Minimum phase: all ringing follows the transient.",
        PhaseResponse.Intermediate => " Intermediate phase: a little ringing before the transient, moderate ringing after it.",
        _ => string.Empty,
    };
}
