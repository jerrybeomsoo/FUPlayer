using FUPlayer.Core.Dsp.Design;
using FUPlayer.Core.Dsp.Resampling;

namespace FUPlayer.Core.Dsp.Processing;

/// <summary>Fixed-rate helper filters used in pre-processing.</summary>
public static class ProcessingFilters
{
    /// <summary>Lowest source rate for which the 20 kHz filter is meaningful.</summary>
    public const int UltrasonicFilterMinimumRate = 88_200;

    /// <summary>
    /// Linear-phase Gaussian lowpass flat to 20 kHz and fully attenuating (150 dB) by 23 kHz. Cleans ultrasonic
    /// noise and distortion above the audio band, including the images left by material upsampled from a lower
    /// rate. Returns null below 88.2 kHz.
    /// </summary>
    public static ResamplerChain? CreateUltrasonicFilter(int sampleRate)
    {
        if (sampleRate < UltrasonicFilterMinimumRate)
        {
            return null;
        }

        var spec = new LowpassSpec(20_000.0 / sampleRate, 23_000.0 / sampleRate, 150.0, WindowKind.Gaussian);
        double[] h = FirDesign.Lowpass(spec);
        var stage = new PolyphaseStage(sampleRate, sampleRate, h, $"20 kHz filter ({h.Length} taps)");
        return new ResamplerChain(sampleRate, sampleRate, [stage], stage.Description);
    }
}
