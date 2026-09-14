using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FUPlayer.Core.Audio;
using FUPlayer.Core.Decoding;
using FUPlayer.Core.Metadata;
using FUPlayer.Core.Playlists;

namespace FUPlayer.Core.Library;

/// <summary>A track known to the library.</summary>
public sealed record LibraryTrack
{
    public required string Path { get; init; }

    public string Title { get; init; } = string.Empty;

    public string Artist { get; init; } = string.Empty;

    public string AlbumArtist { get; init; } = string.Empty;

    public string Album { get; init; } = string.Empty;

    public string Genre { get; init; } = string.Empty;

    public string Composer { get; init; } = string.Empty;

    public int Year { get; init; }

    public int TrackNumber { get; init; }

    public int DiscNumber { get; init; }

    public double DurationSeconds { get; init; }

    /// <summary>Short format label such as "FLAC 96/24" or "DSD64".</summary>
    public string Format { get; init; } = string.Empty;

    public bool IsDsd { get; init; }

    public int SampleRate { get; init; }

    public int BitsPerSample { get; init; }

    public long FileSize { get; init; }

    public DateTime ModifiedUtc { get; init; }

    public bool HasEmbeddedCover { get; init; }

    [JsonIgnore]
    public TimeSpan Duration => TimeSpan.FromSeconds(DurationSeconds);
}

/// <summary>An album: the playable files of one folder.</summary>
public sealed record LibraryAlbum
{
    public required string Folder { get; init; }

    public required string Title { get; init; }

    public required string Artist { get; init; }

    public int Year { get; init; }

    public string Genre { get; init; } = string.Empty;

    public string? CoverFile { get; init; }

    public string FormatSummary { get; init; } = string.Empty;

    public required IReadOnlyList<LibraryTrack> Tracks { get; init; }

    [JsonIgnore]
    public TimeSpan Duration => TimeSpan.FromSeconds(Tracks.Sum(t => t.DurationSeconds));

    /// <summary>A track whose embedded picture can serve as the cover when there is no cover file.</summary>
    [JsonIgnore]
    public string? EmbeddedCoverSource => Tracks.FirstOrDefault(t => t.HasEmbeddedCover)?.Path;
}

public readonly record struct LibraryScanProgress(int Folders, int Tracks, string? CurrentFolder);

/// <summary>Folder-based music library with a JSON cache and field-scoped search syntax.</summary>
public sealed class MusicLibrary
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly string _cacheFile;
    private IReadOnlyList<LibraryAlbum> _albums = [];

    public MusicLibrary(string cacheFile)
    {
        _cacheFile = cacheFile;
    }

    public event EventHandler? Changed;

    /// <summary>Current albums (an immutable snapshot, replaced after scans).</summary>
    public IReadOnlyList<LibraryAlbum> Albums => Volatile.Read(ref _albums);

    public int TrackCount => Albums.Sum(a => a.Tracks.Count);

    public async Task LoadAsync(CancellationToken cancellation = default)
    {
        if (!File.Exists(_cacheFile))
        {
            return;
        }

        try
        {
            await using FileStream stream = File.OpenRead(_cacheFile);
            List<LibraryAlbum>? albums = await JsonSerializer.DeserializeAsync<List<LibraryAlbum>>(stream, JsonOptions, cancellation).ConfigureAwait(false);
            if (albums is not null)
            {
                Publish(albums);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A damaged cache is rebuilt by the next scan.
        }
    }

    /// <summary>Scans folder trees. Unchanged files are taken from the cache unless <paramref name="rescanAll"/> is set.</summary>
    public Task ScanAsync(IReadOnlyList<string> roots, bool rescanAll, IProgress<LibraryScanProgress>? progress, CancellationToken cancellation) =>
        Task.Run(() => Scan(roots, rescanAll, progress, cancellation), cancellation);

    /// <summary>
    /// Filters albums. Terms of three characters or fewer match the start of a word, longer terms match anywhere.
    /// Terms are OR-ed; a leading '+' makes a term required. Prefixes album:, artist:, track: and genre: restrict the field.
    /// </summary>
    public IReadOnlyList<LibraryAlbum> Search(string? query)
    {
        IReadOnlyList<LibraryAlbum> albums = Albums;
        if (string.IsNullOrWhiteSpace(query))
        {
            return albums;
        }

        List<LibrarySearch.Term> terms = LibrarySearch.Parse(query);
        return terms.Count == 0 ? albums : albums.Where(a => LibrarySearch.Matches(a, terms)).ToList();
    }

    private void Scan(IReadOnlyList<string> roots, bool rescanAll, IProgress<LibraryScanProgress>? progress, CancellationToken cancellation)
    {
        var known = new Dictionary<string, LibraryTrack>(StringComparer.OrdinalIgnoreCase);
        if (!rescanAll)
        {
            foreach (LibraryTrack track in Albums.SelectMany(a => a.Tracks))
            {
                known.TryAdd(track.Path, track);
            }
        }

        var albums = new List<LibraryAlbum>();
        int folders = 0;
        int tracks = 0;
        foreach (string root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (string folder in EnumerateFolders(root, cancellation))
            {
                cancellation.ThrowIfCancellationRequested();
                folders++;
                progress?.Report(new LibraryScanProgress(folders, tracks, folder));

                string[] files;
                try
                {
                    files = Directory.GetFiles(folder).Where(DecoderFactory.IsAudioFile).ToArray();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                if (files.Length == 0)
                {
                    continue;
                }

                Array.Sort(files, MediaScanner.NaturalOrder);
                var albumTracks = new List<LibraryTrack>(files.Length);
                foreach (string file in files)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var info = new FileInfo(file);
                    albumTracks.Add(known.TryGetValue(file, out LibraryTrack? cached) && cached.FileSize == info.Length && cached.ModifiedUtc == info.LastWriteTimeUtc
                        ? cached
                        : ReadTrack(file, info));
                    tracks++;
                }

                albums.Add(BuildAlbum(folder, albumTracks));
            }
        }

        albums.Sort((a, b) =>
        {
            int artist = string.Compare(a.Artist, b.Artist, StringComparison.CurrentCultureIgnoreCase);
            return artist != 0 ? artist : a.Year != b.Year ? a.Year.CompareTo(b.Year) : string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase);
        });

        Publish(albums);
        progress?.Report(new LibraryScanProgress(folders, tracks, null));
        Save(albums);
    }

    private void Publish(IReadOnlyList<LibraryAlbum> albums)
    {
        Volatile.Write(ref _albums, albums);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Save(IReadOnlyList<LibraryAlbum> albums)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile)!);
            string temporary = _cacheFile + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(albums, JsonOptions));
            File.Move(temporary, _cacheFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The in-memory library stays usable.
        }
    }

    private static IEnumerable<string> EnumerateFolders(string root, CancellationToken cancellation)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            string folder = pending.Pop();
            yield return folder;

            string[] children;
            try
            {
                children = Directory.GetDirectories(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            Array.Sort(children, MediaScanner.NaturalOrder);
            for (int i = children.Length - 1; i >= 0; i--)
            {
                pending.Push(children[i]);
            }
        }
    }

    private static LibraryTrack ReadTrack(string file, FileInfo info)
    {
        TrackMetadata metadata;
        try
        {
            metadata = MetadataReader.Read(file, probeFormat: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AudioDecoderException or InvalidDataException)
        {
            metadata = new TrackMetadata { FilePath = file };
        }

        StreamFormat? format = metadata.Format;
        return new LibraryTrack
        {
            Path = file,
            Title = metadata.DisplayTitle,
            Artist = metadata.Artist ?? string.Empty,
            AlbumArtist = metadata.AlbumArtist ?? string.Empty,
            Album = metadata.Album ?? string.Empty,
            Genre = metadata.Genre ?? string.Empty,
            Composer = metadata.Composer ?? string.Empty,
            Year = metadata.Year,
            TrackNumber = metadata.TrackNumber,
            DiscNumber = metadata.DiscNumber,
            DurationSeconds = metadata.Duration.TotalSeconds,
            Format = FormatLabel(file, format),
            IsDsd = format?.IsDsd ?? false,
            SampleRate = format?.SampleRate ?? 0,
            BitsPerSample = format?.BitsPerSample ?? 0,
            FileSize = info.Length,
            ModifiedUtc = info.LastWriteTimeUtc,
            HasEmbeddedCover = metadata.HasEmbeddedPicture,
        };
    }

    private static string FormatLabel(string file, StreamFormat? format)
    {
        string codec = Path.GetExtension(file).TrimStart('.').ToUpperInvariant();
        if (format is null)
        {
            return codec;
        }

        return format.Value.IsDsd
            ? $"DSD{AudioRates.DsdMultiplier(format.Value.SampleRate)}"
            : string.Create(CultureInfo.InvariantCulture, $"{codec} {format.Value.SampleRate / 1000.0:0.#}/{format.Value.BitsPerSample}");
    }

    private static LibraryAlbum BuildAlbum(string folder, List<LibraryTrack> tracks)
    {
        tracks.Sort((a, b) =>
        {
            int disc = a.DiscNumber.CompareTo(b.DiscNumber);
            if (disc != 0)
            {
                return disc;
            }

            if (a.TrackNumber > 0 && b.TrackNumber > 0 && a.TrackNumber != b.TrackNumber)
            {
                return a.TrackNumber.CompareTo(b.TrackNumber);
            }

            return MediaScanner.NaturalCompare(a.Path, b.Path);
        });

        string? albumArtist = MostCommon(tracks.Select(t => t.AlbumArtist));
        int distinctArtists = tracks.Select(t => t.Artist).Where(a => a.Length > 0).Distinct(StringComparer.CurrentCultureIgnoreCase).Count();
        string artist = albumArtist
            ?? (distinctArtists > 2 ? "Various artists" : MostCommon(tracks.Select(t => t.Artist)))
            ?? Path.GetFileName(Path.GetDirectoryName(folder))
            ?? "Unknown artist";

        return new LibraryAlbum
        {
            Folder = folder,
            Title = MostCommon(tracks.Select(t => t.Album)) ?? Path.GetFileName(folder),
            Artist = artist,
            Year = tracks.Select(t => t.Year).Where(y => y > 0).DefaultIfEmpty(0).Max(),
            Genre = MostCommon(tracks.Select(t => t.Genre)) ?? string.Empty,
            CoverFile = MetadataReader.FindFolderCover(folder),
            FormatSummary = MostCommon(tracks.Select(t => t.Format)) ?? string.Empty,
            Tracks = tracks,
        };
    }

    private static string? MostCommon(IEnumerable<string> values) =>
        values.Where(v => !string.IsNullOrWhiteSpace(v))
            .GroupBy(v => v, StringComparer.CurrentCultureIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();
}

/// <summary>Search query parsing and matching.</summary>
public static class LibrarySearch
{
    public enum Field
    {
        Any,
        Album,
        Artist,
        Track,
        Genre,
    }

    public readonly record struct Term(string Text, bool Required, Field Field);

    public static List<Term> Parse(string query)
    {
        var terms = new List<Term>();
        foreach (string raw in Tokenize(query))
        {
            string text = raw;
            bool required = false;
            if (text.StartsWith('+'))
            {
                required = true;
                text = text[1..];
            }

            Field field = Field.Any;
            int colon = text.IndexOf(':');
            if (colon > 0)
            {
                Field? prefix = text[..colon].ToLowerInvariant() switch
                {
                    "album" => Field.Album,
                    "artist" => Field.Artist,
                    "track" => Field.Track,
                    "genre" => Field.Genre,
                    _ => null,
                };

                if (prefix is not null)
                {
                    field = prefix.Value;
                    text = text[(colon + 1)..];
                }
            }

            if (text.Length > 0)
            {
                terms.Add(new Term(text, required, field));
            }
        }

        return terms;
    }

    /// <summary>
    /// Splits a query on spaces, except inside double quotes, so a name with a space in it can be asked for as
    /// one term: <c>artist:"miles davis"</c>. The quotes are removed, wherever in the term they appear.
    /// </summary>
    public static List<string> Tokenize(string query)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        bool quoted = false;
        foreach (char c in query)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (c == ' ' && !quoted)
            {
                Flush();
            }
            else
            {
                current.Append(c);
            }
        }

        Flush();
        return tokens;

        void Flush()
        {
            if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }
    }

    public static bool Matches(LibraryAlbum album, IReadOnlyList<Term> terms)
    {
        bool hasOptional = false;
        bool optionalMatched = false;
        foreach (Term term in terms)
        {
            bool hit = MatchTerm(album, term);
            if (term.Required)
            {
                if (!hit)
                {
                    return false;
                }
            }
            else
            {
                hasOptional = true;
                optionalMatched |= hit;
            }
        }

        return !hasOptional || optionalMatched;
    }

    /// <summary>Short terms (≤ 3 characters) match the beginning of any word; longer terms match anywhere.</summary>
    public static bool TextMatches(string? value, string term)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        if (term.Length > 3)
        {
            return value.Contains(term, StringComparison.CurrentCultureIgnoreCase);
        }

        if (value.StartsWith(term, StringComparison.CurrentCultureIgnoreCase))
        {
            return true;
        }

        for (int i = 1; i < value.Length; i++)
        {
            if (!char.IsLetterOrDigit(value[i - 1]) && string.Compare(value, i, term, 0, term.Length, StringComparison.CurrentCultureIgnoreCase) == 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchTerm(LibraryAlbum album, Term term) => term.Field switch
    {
        Field.Album => TextMatches(album.Title, term.Text),
        Field.Artist => TextMatches(album.Artist, term.Text) || album.Tracks.Any(t => TextMatches(t.Artist, term.Text)),
        Field.Track => album.Tracks.Any(t => TextMatches(t.Title, term.Text)),
        Field.Genre => TextMatches(album.Genre, term.Text),
        _ => TextMatches(album.Title, term.Text)
            || TextMatches(album.Artist, term.Text)
            || TextMatches(album.Genre, term.Text)
            || (album.Year > 0 && album.Year.ToString(CultureInfo.InvariantCulture) == term.Text)
            || album.Tracks.Any(t => TextMatches(t.Title, term.Text) || TextMatches(t.Artist, term.Text) || TextMatches(t.Composer, term.Text)),
    };
}
