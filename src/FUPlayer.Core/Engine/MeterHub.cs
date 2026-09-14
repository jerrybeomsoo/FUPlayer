using FUPlayer.Core.Dsp.Analysis;

namespace FUPlayer.Core.Engine;

/// <summary>Meters of the running pipeline: source levels and spectrum, and post-processing output levels and spectrum.</summary>
public sealed class MeterHub
{
    public MeterHub(int sourceChannels, int sourceRate, int outputChannels, int processingRate)
    {
        SourceRate = sourceRate;
        ProcessingRate = processingRate;
        Source = new LevelMeter(sourceChannels, sourceRate);
        Output = processingRate > 0 ? new LevelMeter(outputChannels, processingRate) : null;
        Spectrum = new SpectrumAnalyzer(sourceChannels, sourceRate);
        OutputSpectrum = processingRate > 0 ? new SpectrumAnalyzer(outputChannels, processingRate) { IsEnabled = false } : null;
    }

    public int SourceRate { get; }

    /// <summary>Rate of the output meters (0 when there is no PCM processing stage).</summary>
    public int ProcessingRate { get; }

    public LevelMeter Source { get; }

    /// <summary>Levels after volume, limiter and resampling (inter-sample peaks included); null for DSD pass-through.</summary>
    public LevelMeter? Output { get; }

    public SpectrumAnalyzer Spectrum { get; }

    /// <summary>
    /// Spectrum of the processed PCM just before dither or modulation; null for DSD pass-through. Starts disabled because
    /// feeding it at high processing rates is not free; a display enables it while it is shown.
    /// </summary>
    public SpectrumAnalyzer? OutputSpectrum { get; }
}
