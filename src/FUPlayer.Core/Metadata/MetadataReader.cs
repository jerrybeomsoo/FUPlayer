using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FUPlayer.Core.Audio;
using FUPlayer.Core.Decoding;
using FUPlayer.Core.Decoding.FFmpeg;

namespace FUPlayer.Core.Metadata;

/// <summary>Reads tags and cover art (TagLib#, DSDIFF chunks, FFmpeg) with file-name fallbacks.</summary>
public static partial class MetadataReader
{
    private static readonly string[] CoverFileNames = ["cover", "folder", "front", "albumart", "album", "artwork"];
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".webp", ".bmp"];

    /// <summary>Reads metadata. <paramref name="probeFormat"/> opens the decoder for exact technical details.</summary>
    public static TrackMetadata Read(string path, bool probeFormat = true)
    {
        TrackMetadata metadata = ReadTags(path) ?? new TrackMetadata { FilePath = path };

        if (probeFormat || metadata.Format is null)
        {
            try
            {
                using IAudioDecoder decoder = DecoderFactory.Open(path);
                metadata = metadata with
                {
                    Format = decoder.Format,
                    Codec = decoder.CodecName,
                    Duration = decoder.Length > 0 ? decoder.GetDuration() : metadata.Duration,
                };
            }
            catch (Exception ex) when (ex is AudioDecoderException or IOException or UnauthorizedAccessException)
            {
                // Keep whatever the tags provided.
            }
        }

        return ApplyFileNameFallbacks(metadata);
    }

    /// <summary>Returns embedded front-cover image bytes, if present.</summary>
    public static byte[]? ReadEmbeddedCover(string path)
    {
        try
        {
            if (IsDsdiff(path))
            {
                return ReadDsdiffId3(path)?.Pictures.FirstOrDefault()?.Data.Data;
            }

            using TagLib.File file = TagLib.File.Create(path, TagLib.ReadStyle.PictureLazy);
            TagLib.IPicture? picture = file.Tag.Pictures.FirstOrDefault(p => p.Type == TagLib.PictureType.FrontCover)
                ?? file.Tag.Pictures.FirstOrDefault();
            if (picture?.Data?.Data is { Length: > 0 } data)
            {
                return data;
            }
        }
        catch (Exception ex) when (IsTagFailure(ex))
        {
        }

        if (FFmpegLibrary.IsAvailable)
        {
            try
            {
                using var decoder = FFmpegDecoder.Open(path);
                return decoder.ReadAttachedPicture();
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidDataException)
            {
            }
        }

        return null;
    }

    /// <summary>Finds a cover image file (cover.jpg, folder.png …) in the track's folder.</summary>
    public static string? FindFolderCover(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        string[] images;
        try
        {
            images = Directory.EnumerateFiles(directory)
                .Where(f => ImageExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        foreach (string name in CoverFileNames)
        {
            string? match = images.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).StartsWith(name, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return images.Length == 1 ? images[0] : null;
    }

    private static TrackMetadata? ReadTags(string path)
    {
        if (IsDsdiff(path))
        {
            return ReadDsdiff(path);
        }

        try
        {
            using TagLib.File file = TagLib.File.Create(path, TagLib.ReadStyle.Average | TagLib.ReadStyle.PictureLazy);
            TagLib.Tag tag = file.Tag;
            TagLib.Properties? properties = file.Properties;
            StreamFormat? format = null;
            if (properties is { AudioSampleRate: > 0, AudioChannels: > 0 })
            {
                bool dsd = properties.BitsPerSample == 1 || Path.GetExtension(path).Equals(".dsf", StringComparison.OrdinalIgnoreCase);
                format = dsd
                    ? StreamFormat.Dsd(properties.AudioSampleRate, properties.AudioChannels)
                    : StreamFormat.Pcm(properties.AudioSampleRate, properties.AudioChannels, properties.BitsPerSample > 0 ? properties.BitsPerSample : 16);
            }

            return FromTag(path, tag) with
            {
                Duration = properties?.Duration ?? TimeSpan.Zero,
                BitrateKbps = properties?.AudioBitrate ?? 0,
                Codec = properties?.Description,
                Format = format,
            };
        }
        catch (Exception ex) when (IsTagFailure(ex))
        {
            return ReadFFmpegTags(path);
        }
    }

    private static TrackMetadata FromTag(string path, TagLib.Tag tag) => new()
    {
        FilePath = path,
        Title = Clean(tag.Title),
        Artist = Clean(tag.JoinedPerformers) ?? Clean(tag.JoinedAlbumArtists),
        AlbumArtist = Clean(tag.FirstAlbumArtist),
        Album = Clean(tag.Album),
        Composer = Clean(tag.JoinedComposers),
        Conductor = Clean(tag.Conductor),
        Genre = Clean(tag.FirstGenre),
        Comment = Clean(tag.Comment),
        Year = (int)tag.Year,
        TrackNumber = (int)tag.Track,
        TrackCount = (int)tag.TrackCount,
        DiscNumber = (int)tag.Disc,
        DiscCount = (int)tag.DiscCount,
        TrackGainDb = Finite(tag.ReplayGainTrackGain),
        TrackPeak = Finite(tag.ReplayGainTrackPeak),
        AlbumGainDb = Finite(tag.ReplayGainAlbumGain),
        AlbumPeak = Finite(tag.ReplayGainAlbumPeak),
        HasEmbeddedPicture = tag.Pictures.Length > 0,
    };

    private static TrackMetadata? ReadFFmpegTags(string path)
    {
        if (!FFmpegLibrary.IsAvailable)
        {
            return null;
        }

        try
        {
            using var decoder = FFmpegDecoder.Open(path);
            var tags = decoder.ReadTags()
                .GroupBy(t => t.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);
            string? Get(string key) => tags.TryGetValue(key, out string? value) ? Clean(value) : null;
            return new TrackMetadata
            {
                FilePath = path,
                Title = Get("title"),
                Artist = Get("artist"),
                AlbumArtist = Get("album_artist"),
                Album = Get("album"),
                Composer = Get("composer"),
                Genre = Get("genre"),
                Year = ParseLeadingInt(Get("date")),
                TrackNumber = ParseLeadingInt(Get("track")),
                DiscNumber = ParseLeadingInt(Get("disc")),
                TrackGainDb = ParseGain(Get("replaygain_track_gain")),
                TrackPeak = ParseDouble(Get("replaygain_track_peak")),
                AlbumGainDb = ParseGain(Get("replaygain_album_gain")),
                AlbumPeak = ParseDouble(Get("replaygain_album_peak")),
                HasEmbeddedPicture = decoder.ReadAttachedPicture() is not null,
            };
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidDataException)
        {
            return null;
        }
    }

    private static TrackMetadata ReadDsdiff(string path)
    {
        var metadata = new TrackMetadata { FilePath = path };
        try
        {
            TagLib.Id3v2.Tag? id3 = ReadDsdiffId3(path);
            if (id3 is not null)
            {
                metadata = FromTag(path, id3);
            }

            (string? artist, string? title) = ReadDsdiffEditedMasterInfo(path);
            metadata = metadata with
            {
                Title = metadata.Title ?? title,
                Artist = metadata.Artist ?? artist,
            };
        }
        catch (Exception ex) when (IsTagFailure(ex))
        {
        }

        return metadata;
    }

    private static TagLib.Id3v2.Tag? ReadDsdiffId3(string path)
    {
        byte[]? chunk = FindDsdiffChunk(path, "ID3 ", topLevelOnly: true);
        return chunk is { Length: > 10 } ? new TagLib.Id3v2.Tag(new TagLib.ByteVector(chunk)) : null;
    }

    private static (string? Artist, string? Title) ReadDsdiffEditedMasterInfo(string path)
    {
        byte[]? info = FindDsdiffChunk(path, "DIIN", topLevelOnly: true);
        if (info is null)
        {
            return (null, null);
        }

        string? artist = null;
        string? title = null;
        int position = 0;
        while (position + 12 <= info.Length)
        {
            string id = Encoding.ASCII.GetString(info, position, 4);
            long size = (long)BinaryPrimitives.ReadUInt64BigEndian(info.AsSpan(position + 4));
            int start = position + 12;
            if (size < 4 || start + size > info.Length)
            {
                break;
            }

            if (id is "DIAR" or "DITI")
            {
                int count = (int)BinaryPrimitives.ReadUInt32BigEndian(info.AsSpan(start));
                string text = Encoding.Latin1.GetString(info, start + 4, (int)Math.Min(count, size - 4)).TrimEnd('\0', ' ');
                if (id == "DIAR")
                {
                    artist = Clean(text);
                }
                else
                {
                    title = Clean(text);
                }
            }

            position = start + (int)size + (int)(size & 1);
        }

        return (artist, title);
    }

    private static byte[]? FindDsdiffChunk(string path, string chunkId, bool topLevelOnly)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096);
        Span<byte> header = stackalloc byte[12];
        stream.Position = 16;
        while (stream.Position + 12 <= stream.Length)
        {
            stream.ReadExactly(header);
            string id = Encoding.ASCII.GetString(header[..4]);
            long size = (long)BinaryPrimitives.ReadUInt64BigEndian(header[4..]);
            if (id == chunkId && size is > 0 and < 64 * 1024 * 1024)
            {
                var data = new byte[size];
                stream.ReadExactly(data);
                return data;
            }

            stream.Position += size + (size & 1);
        }

        _ = topLevelOnly;
        return null;
    }

    private static TrackMetadata ApplyFileNameFallbacks(TrackMetadata metadata)
    {
        if (!string.IsNullOrWhiteSpace(metadata.Title))
        {
            return metadata;
        }

        string name = Path.GetFileNameWithoutExtension(metadata.FilePath);
        Match match = TrackFileName().Match(name);
        int trackNumber = metadata.TrackNumber;
        string title = name;
        string? artist = metadata.Artist;
        if (match.Success)
        {
            if (trackNumber == 0 && match.Groups["track"].Success)
            {
                trackNumber = int.Parse(match.Groups["track"].Value, CultureInfo.InvariantCulture);
            }

            title = match.Groups["title"].Value.Trim();
            if (artist is null && match.Groups["artist"].Success)
            {
                artist = match.Groups["artist"].Value.Trim();
            }
        }

        // Folder structure fallback: …/Artist/Album/Track.
        string? albumDirectory = Path.GetDirectoryName(metadata.FilePath);
        string? artistDirectory = albumDirectory is null ? null : Path.GetDirectoryName(albumDirectory);
        return metadata with
        {
            Title = title,
            TrackNumber = trackNumber,
            Artist = artist ?? (artistDirectory is null ? null : Path.GetFileName(artistDirectory)),
            Album = metadata.Album ?? (albumDirectory is null ? null : Path.GetFileName(albumDirectory)),
        };
    }

    private static bool IsDsdiff(string path) =>
        Path.GetExtension(path).Equals(".dff", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".dsdiff", StringComparison.OrdinalIgnoreCase);

    private static bool IsTagFailure(Exception ex) =>
        ex is TagLib.UnsupportedFormatException or TagLib.CorruptFileException or IOException
            or UnauthorizedAccessException or NotImplementedException or ArgumentException or InvalidDataException
            or IndexOutOfRangeException or OverflowException or NullReferenceException;

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().TrimEnd('\0');
    }

    private static double? Finite(double value) => double.IsFinite(value) ? value : null;

    private static int ParseLeadingInt(string? text)
    {
        if (text is null)
        {
            return 0;
        }

        Match match = LeadingNumber().Match(text);
        return match.Success && int.TryParse(match.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : 0;
    }

    private static double? ParseGain(string? text) =>
        text is null ? null : ParseDouble(text.Replace("dB", string.Empty, StringComparison.OrdinalIgnoreCase));

    private static double? ParseDouble(string? text) =>
        double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : null;

    [GeneratedRegex(@"^\s*(?:(?<track>\d{1,3})\s*[-._ ]\s*)?(?:(?<artist>[^-]+?)\s+-\s+)?(?<title>.+)$")]
    private static partial Regex TrackFileName();

    [GeneratedRegex(@"\d+")]
    private static partial Regex LeadingNumber();
}
