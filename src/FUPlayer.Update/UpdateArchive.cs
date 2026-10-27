using System.IO.Compression;
using System.Security.Cryptography;

namespace FUPlayer.Update;

public sealed record UpdateFile(string RelativePath, string Sha256);

/// <summary>Validates the portable package before it can write files to an installation.</summary>
public static class UpdateArchive
{
    private const long MaximumExtracted = 2L * 1024 * 1024 * 1024;
    private static readonly string[] Required = ["FUPlayer.exe", "FUPlayer.dll", "FUPlayer.runtimeconfig.json", "FUPlayer.Updater.exe"];

    public static void Inspect(string archive)
    {
        using ZipArchive zip = ZipFile.OpenRead(archive);
        Entries(zip);
    }

    public static IReadOnlyList<UpdateFile> Extract(string archive, string destination, CancellationToken cancellation = default)
    {
        if (Directory.Exists(destination)) throw new IOException("The update extraction folder already exists.");
        using ZipArchive zip = ZipFile.OpenRead(archive);
        var entries = Entries(zip);
        Directory.CreateDirectory(destination);
        var files = new List<UpdateFile>();
        byte[] buffer = new byte[128 * 1024];
        foreach ((ZipArchiveEntry entry, string relative) in entries)
        {
            cancellation.ThrowIfCancellationRequested();
            string path = Inside(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using Stream input = entry.Open();
            using var output = new FileStream(path, FileMode.CreateNew);
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            int read;
            while ((read = input.Read(buffer)) > 0)
            {
                cancellation.ThrowIfCancellationRequested();
                total += read;
                if (total > entry.Length) throw new InvalidDataException("An extracted file exceeded its declared size.");
                hash.AppendData(buffer, 0, read);
                output.Write(buffer, 0, read);
            }
            if (total != entry.Length) throw new InvalidDataException("A package file was truncated.");
            files.Add(new(relative, Convert.ToHexString(hash.GetHashAndReset())));
        }
        return files;
    }

    private static List<(ZipArchiveEntry, string)> Entries(ZipArchive zip)
    {
        if (zip.Entries.Count > 10000) throw new InvalidDataException("Too many package entries.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<(ZipArchiveEntry, string)>();
        long total = 0;
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            string name = entry.FullName.Replace('\\', '/');
            bool folder = name.EndsWith('/');
            string[] parts = name.TrimEnd('/').Split('/');
            if (parts.Length < 1 || parts[0] != "FUPlayer" || parts.Any(InvalidComponent)
                || ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000
                || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Unsafe package path.");
            if (!names.Add(name.TrimEnd('/'))) throw new InvalidDataException("Duplicate package path.");
            if (folder) continue;
            if (parts.Length < 2) throw new InvalidDataException("Invalid package root.");
            string relative = string.Join('/', parts.Skip(1));
            if (parts.Skip(1).Any(p => p.Equals("browser", StringComparison.OrdinalIgnoreCase) || p.Equals(".git", StringComparison.OrdinalIgnoreCase))
                || parts[^1].Equals("settings.json", StringComparison.OrdinalIgnoreCase)
                || parts[^1].Equals("controller.json", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The package contains user configuration.");
            if (entry.Length < 0 || entry.Length > MaximumExtracted - total) throw new InvalidDataException("The package is too large.");
            total += entry.Length;
            files.Add((entry, relative));
        }
        foreach (string required in Required)
            if (!files.Any(f => f.Item2.Equals(required, StringComparison.OrdinalIgnoreCase) && f.Item1.Length > 0))
                throw new InvalidDataException("The portable update package is incomplete.");
        return files;
    }

    private static bool InvalidComponent(string value)
    {
        if (value.Length == 0 || value is "." or ".." || value.EndsWith(' ') || value.EndsWith('.')
            || value.Any(c => c < 32 || "<>:\"|?*".Contains(c))) return true;
        string stem = value.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '0' and <= '9');
    }

    public static string Inside(string directory, string relative)
    {
        string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Path leaves the update directory.");
        return path;
    }

    public static void RejectLinks(string root, string path)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        string? current = path;
        while (current is not null && (current.Equals(root, StringComparison.OrdinalIgnoreCase) || current.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The installation contains a linked update destination.");
            current = Path.GetDirectoryName(current);
        }
    }
}
