using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace FUPlayer.Core.Upnp;

/// <summary>
/// What a controller says about the stream it sends, from the DIDL-Lite document that comes with it: the track, the
/// artist, the album, where its cover is, and the format the <c>res</c> element describes. Every field is optional,
/// because controllers fill in as much or as little as they like.
/// </summary>
public sealed record StreamMetadata
{
    public string? Title { get; init; }

    public string? Artist { get; init; }

    public string? Album { get; init; }

    public string? Genre { get; init; }

    /// <summary>Where the controller serves the cover, when it sends one.</summary>
    public string? AlbumArtUri { get; init; }

    /// <summary>Length of the track the stream carries, when the controller knows it.</summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>The <c>res</c> element's protocolInfo, such as <c>http-get:*:audio/flac:*</c>.</summary>
    public string? ProtocolInfo { get; init; }

    public int? SampleRate { get; init; }

    public int? BitsPerSample { get; init; }

    public int? Channels { get; init; }

    /// <summary>The metadata document as it arrived, so it can be handed back to a controller that asks for it.</summary>
    public string Didl { get; init; } = string.Empty;

    /// <summary>Whether there is anything worth showing beyond the stream itself.</summary>
    public bool HasTrack => !string.IsNullOrWhiteSpace(Title) || !string.IsNullOrWhiteSpace(Artist) || !string.IsNullOrWhiteSpace(Album);

    /// <summary>Reads a DIDL-Lite document, or returns null when there is none or it cannot be read.</summary>
    public static StreamMetadata? Parse(string? didl)
    {
        if (string.IsNullOrWhiteSpace(didl) || didl.Trim() == "NOT_IMPLEMENTED")
        {
            return null;
        }

        XDocument document;
        try
        {
            // Some controllers escape the document twice, or send it with a stray byte-order mark.
            string text = didl.Trim().TrimStart('﻿');
            if (text.StartsWith("&lt;", StringComparison.Ordinal))
            {
                text = System.Net.WebUtility.HtmlDecode(text);
            }

            document = XDocument.Parse(text, LoadOptions.None);
        }
        catch (XmlException)
        {
            return null;
        }

        XElement? item = document.Descendants().FirstOrDefault(e => e.Name.LocalName is "item" or "container");
        if (item is null)
        {
            return null;
        }

        XElement? res = Child(item, "res");
        return new StreamMetadata
        {
            Title = Text(item, "title"),
            Artist = Text(item, "artist") ?? Text(item, "creator") ?? Text(item, "albumArtist"),
            Album = Text(item, "album"),
            Genre = Text(item, "genre"),
            AlbumArtUri = Text(item, "albumArtURI"),
            Duration = ParseDuration(res?.Attribute("duration")?.Value),
            ProtocolInfo = res?.Attribute("protocolInfo")?.Value,
            SampleRate = Number(res?.Attribute("sampleFrequency")?.Value),
            BitsPerSample = Number(res?.Attribute("bitsPerSample")?.Value),
            Channels = Number(res?.Attribute("nrAudioChannels")?.Value),
            Didl = didl,
        };
    }

    /// <summary>Reads a UPnP time, <c>H+:MM:SS[.F+]</c>, or null when it is not one.</summary>
    public static TimeSpan? ParseDuration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string[] parts = value.Trim().Split(':');
        if (parts.Length != 3
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int hours)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes)
            || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
        {
            return null;
        }

        TimeSpan duration = new TimeSpan(hours, minutes, 0) + TimeSpan.FromSeconds(seconds);
        return duration > TimeSpan.Zero ? duration : null;
    }

    /// <summary>Writes a UPnP time, <c>H:MM:SS</c>.</summary>
    public static string FormatDuration(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}");
    }

    private static XElement? Child(XElement item, string name) =>
        item.Elements().FirstOrDefault(e => e.Name.LocalName == name);

    private static string? Text(XElement item, string name)
    {
        string? value = Child(item, name)?.Value?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static int? Number(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) && number > 0 ? number : null;
}
