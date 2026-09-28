namespace FUPlayer.Core.Decoding;

/// <summary>
/// A decoder fed from outside at its own pace, such as a stream a network controller sends. Its reads can come back
/// empty before the stream is over, when the next audio has simply not arrived yet; the engine waits for it then,
/// instead of taking an empty read for the end of the track, and carries on answering commands meanwhile.
/// </summary>
public interface ILiveSource
{
    /// <summary>True once the source has nothing more to give: the sender closed the stream, or it failed.</summary>
    bool HasEnded { get; }
}
