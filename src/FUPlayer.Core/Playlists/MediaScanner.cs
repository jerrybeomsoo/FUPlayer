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
                else if (DecoderFactory.IsKnownAudioFile(path))
                {
                    // A file named by hand is kept even when nothing can decode it yet, so that the
                    // engine can say why. Folders below are scanned with IsAudioFile instead.
                    result.Add(Path.GetFullPath(path));
                }
            }
        }

        return result;
    }

    /// <summary>Audio files of a folder in natural order, followed by those of its sub-folders.</summary>
    public static IEnumerable<string> EnumerateFolder(string folder, bool recursive, CancellationToken cancellation = default)
    {
        foreach (string current in EnumerateFolders(folder, recursive, cancellation))
        {
            string[] files;
            try { files = Directory.GetFiles(current); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            Array.Sort(files, NaturalOrder);
            foreach (string file in files)
            {
                cancellation.ThrowIfCancellationRequested();
                if (DecoderFactory.IsAudioFile(file)) yield return file;
            }
        }
    }

    /// <summary>Natural-order depth-first traversal shared by queue expansion and library scans.</summary>
    internal static IEnumerable<string> EnumerateFolders(string root, bool recursive, CancellationToken cancellation)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out string? folder))
        {
            cancellation.ThrowIfCancellationRequested();
            yield return folder;
            if (!recursive) continue;
            string[] children;
            try { children = Directory.GetDirectories(folder); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            Array.Sort(children, NaturalOrder);
            for (int i = children.Length - 1; i >= 0; i--)
                // Directory junctions can point back to an ancestor. Explicitly chosen roots still work.
                if (!IsLinkedDirectory(children[i])) pending.Push(children[i]);
        }
    }

    private static bool IsLinkedDirectory(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
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
