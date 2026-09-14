using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FUPlayer.Core.Playlists;

/// <summary>M3U / M3U8 / PLS playlist reading and M3U8 writing.</summary>
public static partial class PlaylistFile
{
    public static readonly string[] Extensions = [".m3u8", ".m3u", ".pls"];

    public static bool IsPlaylist(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns the local file paths referenced by a playlist (network URLs are skipped).</summary>
    public static IReadOnlyList<string> Load(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath) ?? Environment.CurrentDirectory;
        string[] lines = ReadLines(fullPath);
        var result = new List<string>();

        if (Path.GetExtension(fullPath).Equals(".pls", StringComparison.OrdinalIgnoreCase))
        {
            var entries = new SortedDictionary<int, string>();
            foreach (string line in lines)
            {
                Match match = PlsEntry().Match(line.Trim());
                if (match.Success)
                {
                    entries[int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)] = match.Groups[2].Value.Trim();
                }
            }

            foreach (string entry in entries.Values)
            {
                AddResolved(result, entry, directory);
            }
        }
        else
        {
            foreach (string line in lines)
            {
                string trimmed = line.Trim();
                if (trimmed.Length > 0 && trimmed[0] != '#')
                {
                    AddResolved(result, trimmed, directory);
                }
            }
        }

        return result;
    }

    /// <summary>Writes an extended M3U8 playlist, using paths relative to the playlist where possible.</summary>
    public static void Save(string path, IEnumerable<QueueItem> items)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? Environment.CurrentDirectory;
        var builder = new StringBuilder("#EXTM3U\n");
        foreach (QueueItem item in items)
        {
            int seconds = item.Metadata is { Duration.TotalSeconds: > 0 } metadata ? (int)Math.Round(metadata.Duration.TotalSeconds) : -1;
            string label = string.IsNullOrEmpty(item.DisplayArtist) ? item.DisplayTitle : $"{item.DisplayArtist} - {item.DisplayTitle}";
            builder.Append(CultureInfo.InvariantCulture, $"#EXTINF:{seconds},{label}\n");
            builder.Append(RelativeIfPossible(directory, item.Path)).Append('\n');
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void AddResolved(List<string> result, string entry, string directory)
    {
        if (entry.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(entry, UriKind.Absolute, out Uri? fileUri))
        {
            result.Add(fileUri.LocalPath);
            return;
        }

        if (entry.Contains("://", StringComparison.Ordinal))
        {
            return;
        }

        string normalized = Path.DirectorySeparatorChar == '/' ? entry.Replace('\\', '/') : entry;
        try
        {
            result.Add(Path.IsPathRooted(normalized) ? Path.GetFullPath(normalized) : Path.GetFullPath(Path.Combine(directory, normalized)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Skip malformed entries.
        }
    }

    private static string RelativeIfPossible(string directory, string file)
    {
        string relative = Path.GetRelativePath(directory, file);
        return Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal) && relative.Count(c => c == Path.DirectorySeparatorChar) > 4
            ? file
            : relative;
    }

    private static string[] ReadLines(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        string text;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            text = Encoding.Latin1.GetString(bytes);
        }

        return text.TrimStart('﻿').Split(["\r\n", "\n", "\r"], StringSplitOptions.None);
    }

    [GeneratedRegex(@"^File(\d+)\s*=\s*(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex PlsEntry();
}
