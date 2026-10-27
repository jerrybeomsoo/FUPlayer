using System.Text.Json;

namespace FUPlayer.Update;

public sealed record UpdateRequest(int Format, string TargetDirectory, string ArchiveHash, int ParentId,
    long ParentStartTicks, string[] Arguments, string Language)
{
    public static string CacheDirectory
    {
        get
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.GetFullPath(Path.Combine(string.IsNullOrEmpty(local) ? Path.GetTempPath() : local, "FUPlayer", "Updates"));
        }
    }
    public const string HelperName = "FUPlayer.Updater.exe";

    public static UpdateRequest Read(string ticket)
    {
        if (new FileInfo(ticket).Length > 16384) throw new InvalidDataException("Update request is too large.");
        var request = JsonSerializer.Deserialize<UpdateRequest>(File.ReadAllText(ticket)) ?? throw new InvalidDataException("Invalid update request.");
        string work = Path.GetDirectoryName(Path.GetFullPath(ticket))!;
        if (request.Format != 1 || request.ParentId <= 0 || request.ParentStartTicks <= 0 || request.TargetDirectory is null
            || !Path.IsPathFullyQualified(request.TargetDirectory) || request.Arguments is null || request.Arguments.Length > 16
            || request.Arguments.Any(a => a is null || a.Length > 4096 || a.Contains('\0'))
            || request.ArchiveHash is not { Length: 64 } || request.ArchiveHash.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("Invalid update request.");
        string target = Path.GetFullPath(request.TargetDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (work.Equals(target, StringComparison.OrdinalIgnoreCase) || work.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || target.StartsWith(work + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Update staging and installation must be separate.");
        return request;
    }

    public static void CleanupCompleted()
    {
        if (!Directory.Exists(CacheDirectory)) return;
        foreach (string directory in Directory.EnumerateDirectories(CacheDirectory).Take(32))
        {
            try
            {
                if (!File.Exists(Path.Combine(directory, "complete"))) continue;
                string busy = Path.Combine(directory, "busy");
                if (File.Exists(busy)) { using var check = new FileStream(busy, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
                UpdateArchive.RejectLinks(CacheDirectory, directory);
                // Only our marked working folders; never traverse a link during cache cleanup.
                if (HasLink(directory)) continue;
                Directory.Delete(directory, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public static void CleanupPayload(string work)
    {
        foreach (string name in new[] { "payload", "backup" })
        {
            string directory = UpdateArchive.Inside(work, name);
            if (Directory.Exists(directory) && !HasLink(directory)) Directory.Delete(directory, true);
        }
        string archive = UpdateArchive.Inside(work, "package.zip");
        if (File.Exists(archive)) File.Delete(archive);
        File.WriteAllText(Path.Combine(work, "complete"), "FUPlayer update completed");
    }

    private static bool HasLink(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return true;
        foreach (string path in Directory.EnumerateFileSystemEntries(directory))
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) return true;
            if ((attributes & FileAttributes.Directory) != 0 && HasLink(path)) return true;
        }
        return false;
    }
}
