namespace FUPlayer.Core.Upnp;

/// <summary>
/// foobar2000's UPnP output keeps what it knows of each make of renderer in a text file in its profile folder,
/// <c>foo_out_upnp-config-v2.txt</c>. Of a renderer it does not know it assumes the worst: that it cannot pause and
/// cannot play an endless FLAC stream, so it sends 16-bit WAV and stops the stream when paused. The entry here tells it
/// what this player takes: FLAC at up to 24 bits, endless and chunked, and pausing like a local output.
/// </summary>
/// <remarks>
/// The file is a list of <c>key=value</c> lines. A <c>manufacturer=</c> line, with a <c>model=</c> line after it,
/// starts the settings for the renderers that match both; lines before the first of them apply to every renderer.
/// </remarks>
public static class Foobar2000Config
{
    public const string FileName = "foo_out_upnp-config-v2.txt";

    private const string ManufacturerLine = "manufacturer=" + UpnpDescriptions.Manufacturer;

    /// <summary>The lines that describe this player, with the comment that says where they came from.</summary>
    public static readonly string Entry = string.Join(
        "\n",
        "# FUPlayer: added from its UPnP input page. It pauses, and plays an endless FLAC stream at up to 24 bits.",
        ManufacturerLine,
        "model=" + UpnpDescriptions.ModelName,
        "supports-pause=true",
        "supports-FLAC=true",
        "supports-infinite-length=true",
        "supports-chunked=true",
        "bitdepth-max=24",
        "preferred-format=FLAC");

    /// <summary>Whether the file's text already describes this player.</summary>
    public static bool HasEntry(string config)
    {
        ArgumentNullException.ThrowIfNull(config);
        foreach (string line in config.Split('\n'))
        {
            if (line.Trim().Equals(ManufacturerLine, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The file's text with this player's entry at the end, or the text as it was when it has one.</summary>
    public static string WithEntry(string config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (HasEntry(config))
        {
            return config;
        }

        // Written in the file's own line endings, after a blank line like the entries foobar2000 ships with.
        string newline = config.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string text = config.TrimEnd('\r', '\n', ' ', '\t');
        string separator = text.Length == 0 ? string.Empty : newline + newline;
        return text + separator + Entry.Replace("\n", newline, StringComparison.Ordinal) + newline;
    }
}
