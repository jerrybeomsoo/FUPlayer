using FUPlayer.Core.Upnp;

namespace FUPlayer.Core.Engine;

/// <summary>A stream for the engine to play from a network address, and who sent it.</summary>
/// <param name="Uri">The stream's http address.</param>
/// <param name="Metadata">What the sender said about it, if anything.</param>
/// <param name="Controller">Who sent it, in words: "foobar2000 on this computer".</param>
public sealed record NetworkStreamRequest(string Uri, StreamMetadata? Metadata, string Controller);

/// <summary>The network stream that is playing, as the interface shows it.</summary>
public sealed record NetworkStreamStatus
{
    public required string Uri { get; init; }

    public required string Controller { get; init; }

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
