namespace FUPlayer.Core.Library;

/// <summary>
/// Splits a folder's tracks into albums by their album tag, so a flat folder that holds many albums
/// becomes many albums instead of one. Tracks without an album tag stay together as the folder's album.
/// </summary>
/// <remarks>
/// The key is the album title and, when the tracks carry one, the album artist, both compared without case.
/// The track artist is deliberately not part of it: a compilation has one album title and many artists.
/// </remarks>
public static class AlbumGrouping
{
    /// <summary>Builds one album per album tag in <paramref name="tracks"/>, all of them from <paramref name="folder"/>.</summary>
    /// <param name="build">The library's own album builder (sorting, title, artist, cover).</param>
    public static IEnumerable<LibraryAlbum> Build(string folder, List<LibraryTrack> tracks, Func<string, List<LibraryTrack>, LibraryAlbum> build)
    {
        List<List<LibraryTrack>> parts = Split(tracks);
        foreach (List<LibraryTrack> part in parts)
        {
            LibraryAlbum album = build(folder, part);

            // A cover file in the folder belongs to the folder; when the folder holds several albums it is no
            // album's own, and each falls back to its embedded picture.
            yield return parts.Count > 1 ? album with { CoverFile = null } : album;
        }
    }

    /// <summary>
    /// Re-groups albums loaded from a cache written with folder grouping: their tracks are pooled per folder and
    /// split again, so the change of grouping needs no rescan.
    /// </summary>
    public static List<LibraryAlbum> Regroup(IReadOnlyList<LibraryAlbum> albums, Func<string, List<LibraryTrack>, LibraryAlbum> build)
    {
        var result = new List<LibraryAlbum>(albums.Count);
        foreach (IGrouping<string, LibraryAlbum> folder in albums.GroupBy(a => a.Folder, StringComparer.OrdinalIgnoreCase))
        {
            result.AddRange(Build(folder.Key, folder.SelectMany(a => a.Tracks).ToList(), build));
        }

        result.Sort(CompareAlbums);
        return result;
    }

    /// <summary>The library's album order: artist, then year, then title.</summary>
    public static int CompareAlbums(LibraryAlbum a, LibraryAlbum b)
    {
        int artist = string.Compare(a.Artist, b.Artist, StringComparison.CurrentCultureIgnoreCase);
        return artist != 0 ? artist : a.Year != b.Year ? a.Year.CompareTo(b.Year) : string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase);
    }

    private static List<List<LibraryTrack>> Split(List<LibraryTrack> tracks)
    {
        var parts = new Dictionary<(string Album, string AlbumArtist), List<LibraryTrack>>(KeyComparer.Instance);
        var order = new List<List<LibraryTrack>>();
        foreach (LibraryTrack track in tracks)
        {
            (string, string) key = (track.Album.Trim(), track.Album.Trim().Length == 0 ? string.Empty : track.AlbumArtist.Trim());
            if (!parts.TryGetValue(key, out List<LibraryTrack>? part))
            {
                part = [];
                parts.Add(key, part);
                order.Add(part);
            }

            part.Add(track);
        }

        return order;
    }

    private sealed class KeyComparer : IEqualityComparer<(string Album, string AlbumArtist)>
    {
        public static readonly KeyComparer Instance = new();

        public bool Equals((string Album, string AlbumArtist) x, (string Album, string AlbumArtist) y) =>
            string.Equals(x.Album, y.Album, StringComparison.CurrentCultureIgnoreCase)
            && string.Equals(x.AlbumArtist, y.AlbumArtist, StringComparison.CurrentCultureIgnoreCase);

        public int GetHashCode((string Album, string AlbumArtist) key) =>
            HashCode.Combine(
                StringComparer.CurrentCultureIgnoreCase.GetHashCode(key.Album),
                StringComparer.CurrentCultureIgnoreCase.GetHashCode(key.AlbumArtist));
    }
}
