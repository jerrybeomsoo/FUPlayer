using FUPlayer.Core.Audio;

namespace FUPlayer.Core.Metadata;

/// <summary>Descriptive and technical information about a track.</summary>
public sealed record TrackMetadata
{
    public required string FilePath { get; init; }

    public string? Title { get; init; }

    public string? Artist { get; init; }

    public string? AlbumArtist { get; init; }

    public string? Album { get; init; }

    public string? Composer { get; init; }

    public string? Conductor { get; init; }

    public string? Genre { get; init; }

    public string? Comment { get; init; }

    public int Year { get; init; }

    public int TrackNumber { get; init; }

    public int TrackCount { get; init; }

    public int DiscNumber { get; init; }

    public int DiscCount { get; init; }

    public TimeSpan Duration { get; init; }

    public StreamFormat? Format { get; init; }

    public string? Codec { get; init; }

    public int BitrateKbps { get; init; }

    /// <summary>ReplayGain 2.0 track gain in dB.</summary>
    public double? TrackGainDb { get; init; }

    /// <summary>ReplayGain track peak (linear, 1.0 = full scale).</summary>
    public double? TrackPeak { get; init; }

    public double? AlbumGainDb { get; init; }

    public double? AlbumPeak { get; init; }

    public bool HasEmbeddedPicture { get; init; }

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? Path.GetFileNameWithoutExtension(FilePath) : Title;

    public string DisplayArtist => FirstNonEmpty(Artist, AlbumArtist) ?? "Unknown artist";

    public string DisplayAlbum => FirstNonEmpty(Album) ?? Path.GetFileName(Path.GetDirectoryName(FilePath)) ?? string.Empty;

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
