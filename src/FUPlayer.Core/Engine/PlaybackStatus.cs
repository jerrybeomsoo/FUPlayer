using FUPlayer.Core.Playlists;

namespace FUPlayer.Core.Engine;

public enum EngineState
{
    Stopped,
    Playing,
    Paused,
}

/// <summary>Snapshot of the engine for the user interface.</summary>
public sealed record PlaybackStatus
{
    public static readonly PlaybackStatus Idle = new();

    public EngineState State { get; init; } = EngineState.Stopped;

    /// <summary>Index of the audible queue item, or −1 (stopped or test tone).</summary>
    public int CurrentIndex { get; init; } = -1;

    public QueueItem? CurrentItem { get; init; }

    public bool IsTestTone { get; init; }

    /// <summary>True while the source is another application rather than a file.</summary>
    public bool IsCapture { get; init; }

    public TimeSpan Position { get; init; }

    public TimeSpan Duration { get; init; }

    public PlaybackPlan? Plan { get; init; }

    public string? ResamplerSummary { get; init; }

    /// <summary>Where the filtering runs: the processor, or the OpenCL device that took it over.</summary>
    public string? Acceleration { get; init; }

    /// <summary>What the neural upscaler is doing, or null when it was not asked for.</summary>
    public string? Upscaler { get; init; }

    /// <summary>True while the neural upscaler's network is running.</summary>
    public bool IsUpscaling { get; init; }

    public string? BackendName { get; init; }

    public string? DeviceName { get; init; }

    /// <summary>DSP time relative to real time (1.0 = just keeping up).</summary>
    public double DspLoad { get; init; }

    /// <summary>What the source's spectrum says about how it was coded, once enough has played.</summary>
    public string? Bandwidth { get; init; }

    /// <summary>FIFO fill level 0..1.</summary>
    public double BufferFill { get; init; }

    /// <summary>
    /// Bytes the engine FIFO holds: what the setting asked for, unless the device buffer or a block-based filter
    /// needed more, and never more than the 256 MB ceiling. This is memory actually allocated.
    /// </summary>
    public int FifoBytes { get; init; }

    /// <summary>Seconds of audio the FIFO holds when full, at the format now playing.</summary>
    public double FifoSeconds { get; init; }

    /// <summary>Delay the filters themselves impose: their group delay, which is part of what they do.</summary>
    public double PipelineLatencySeconds { get; init; }

    /// <summary>
    /// Delay added purely by convolving in blocks, on top of the filters' own. The samples that come out do not
    /// depend on it, so it is the part of the wait that a shorter block gives back.
    /// </summary>
    public double FilterBlockSeconds { get; init; }

    public long LimiterEvents { get; init; }

    public long ApodizationEvents { get; init; }

    public long ClippedSamples { get; init; }

    public long ModulatorResets { get; init; }

    public long UnderrunFrames { get; init; }

    public double VolumeDb { get; init; }

    public string? LastError { get; init; }
}
