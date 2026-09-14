using FUPlayer.Core.Decoding;

namespace FUPlayer.Core.Playlists;

/// <summary>Expands dropped files, folders and playlists into an ordered list of playable files.</summary>
public static class MediaScanner
{
    public static IComparer<string> NaturalOrder { get; } = Comparer<string>.Create(NaturalCompare);

    public static IReadOnlyList<string> Expand(IEnumerable<string> paths, bool recursive = true, CancellationToken cancellation = default)
    {
        var result = new List<string>();
        foreach (string path in paths)
        {
            cancellation.ThrowIfCancellationRequested();
            if (Directory.Exists(path))
            {
                result.AddRange(EnumerateFolder(path, recursive, cancellation));
            }
            else if (File.Exists(path))
            {
                if (PlaylistFile.IsPlaylist(path))
                {
                    result.AddRange(PlaylistFile.Load(path));
                }
                else if (DecoderFactory.IsAudioFile(path))
                {
                    result.Add(Path.GetFullPath(path));
                }
            }
        }

        return result;
    }

    /// <summary>Audio files of a folder in natural order, followed by those of its sub-folders.</summary>
    public static IEnumerable<string> EnumerateFolder(string folder, bool recursive, CancellationToken cancellation = default)
    {
        string[] files;
        string[] folders;
        try
        {
            files = Directory.GetFiles(folder);
            folders = recursive ? Directory.GetDirectories(folder) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        Array.Sort(files, NaturalOrder);
        foreach (string file in files)
        {
            if (DecoderFactory.IsAudioFile(file))
            {
                yield return file;
            }
        }

        Array.Sort(folders, NaturalOrder);
        foreach (string child in folders)
        {
            cancellation.ThrowIfCancellationRequested();
            foreach (string file in EnumerateFolder(child, recursive, cancellation))
            {
                yield return file;
            }
        }
    }

    /// <summary>Compares strings treating digit runs as numbers ("track 2" before "track 10").</summary>
    public static int NaturalCompare(string? a, string? b)
    {
        if (ReferenceEquals(a, b))
        {
            return 0;
        }

        if (a is null)
        {
            return -1;
        }

        if (b is null)
        {
            return 1;
        }

        int i = 0;
        int j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                int startA = i;
                int startB = j;
                while (i < a.Length && char.IsDigit(a[i]))
                {
                    i++;
                }

                while (j < b.Length && char.IsDigit(b[j]))
                {
                    j++;
                }

                ReadOnlySpan<char> numberA = a.AsSpan(startA, i - startA).TrimStart('0');
                ReadOnlySpan<char> numberB = b.AsSpan(startB, j - startB).TrimStart('0');
                if (numberA.Length != numberB.Length)
                {
                    return numberA.Length.CompareTo(numberB.Length);
                }

                int compare = numberA.CompareTo(numberB, StringComparison.Ordinal);
                if (compare != 0)
                {
                    return compare;
                }
            }
            else
            {
                int compare = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                if (compare != 0)
                {
                    return compare;
                }

                i++;
                j++;
            }
        }

        return (a.Length - i).CompareTo(b.Length - j);
    }
}
