using FUPlayer.Core.Output;

namespace FUPlayer.Audio.Windows;

/// <summary>Creates the Windows output back-ends in order of preference.</summary>
public static class WindowsAudioBackends
{
    public static IEnumerable<IAudioBackend> Create() =>
    [
        new WasapiBackend(exclusive: true),
        new AsioBackend(),
        new WasapiBackend(exclusive: false),
    ];
}
