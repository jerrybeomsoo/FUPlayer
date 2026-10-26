namespace FUPlayer.Audio.Windows;

/// <summary>Checks the device channel interval before any native driver buffer is requested.</summary>
internal static class AsioChannelRange
{
    internal static bool Fits(int first, int channels, int available) =>
        first >= 0 && channels > 0 && channels <= available && first <= available - channels;
}
