namespace FUPlayer.Core.Dsp.Restoration;

/// <summary>A stereo restorer keeps its streaming state where its selected processor runs.</summary>
public interface INeuralRestorer
{
    /// <summary>Fixed processing delay, in source samples.</summary>
    int Latency { get; }
    /// <summary>Restores both channels in place without changing their length.</summary>
    void Process(Span<double> left, Span<double> right);
    /// <summary>Discards history after a seek.</summary>
    void Reset();
}
/// <summary>An upscaler operates on the interpolated channel, locally or on the selected LAN server.</summary>
public interface INeuralUpscaler
{
    /// <summary>Fixed processing delay at the interpolated rate.</summary>
    int Latency { get; }
    /// <summary>Writes restored/upscaled content in place.</summary>
    void Process(Span<double> samples);
    /// <summary>Discards history after a seek.</summary>
    void Reset();
}
