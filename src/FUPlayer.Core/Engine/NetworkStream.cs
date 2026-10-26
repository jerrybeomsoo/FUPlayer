using FUPlayer.Core.Upnp;
using FUPlayer.Core.Decoding;

namespace FUPlayer.Core.Engine;

/// <summary>A stream for the engine to play from a network address, and who sent it.</summary>
/// <param name="Uri">The stream's http address.</param>
/// <param name="Metadata">What the sender said about it, if anything.</param>
/// <param name="Controller">Who sent it, in words: "foobar2000 on this computer".</param>
public sealed record NetworkStreamRequest(string Uri, StreamMetadata? Metadata, string Controller, bool DirectBrowserAudio = false)
{
    /// <summary>Player-owned live PCM source; external controllers only supply ordinary HTTP addresses.</summary>
    public Func<IAudioDecoder>? OpenDecoder { get; init; }
    /// <summary>Reads current metadata from a trusted live source without restarting its audio decoder.</summary>
    public Func<StreamMetadata?>? ReadMetadata { get; init; }
    /// <summary>Trusted live-source notification before every DSP rebuild; true means playback should continue.</summary>
    public Action<bool>? PreparingProcessing { get; init; }
    /// <summary>Runs after the replacement DSP, device and decoder are ready, including settings changes.</summary>
    public Action? ProcessingReady { get; init; }
    /// <summary>Stops the owned live source when opening or playback ends, including device failures.</summary>
    public Action? ProcessingStopped { get; init; }
}

/// <summary>The network stream that is playing, as the interface shows it.</summary>
public sealed record NetworkStreamStatus
{
    public required string Uri { get; init; }

    public required string Controller { get; init; }

    /// <summary>True for the player's private browser PCM bridge, rather than a UPnP controller.</summary>
    public bool DirectBrowserAudio { get; init; }

    /// <summary>The latest metadata, which a continuous stream updates as each track begins.</summary>
    public StreamMetadata? Metadata { get; init; }

    /// <summary>How the stream arrives: FLAC, WAV or LPCM.</summary>
    public required string Container { get; init; }

    public string? ContentType { get; init; }

    /// <summary>Audio received and not yet played, which is how far behind the sender the player runs.</summary>
    public double BufferedSeconds { get; init; }

    /// <summary>Times the player wanted audio and none had arrived yet.</summary>
    public long StarvedReads { get; init; }
}
