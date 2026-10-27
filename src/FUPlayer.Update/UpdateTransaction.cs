using System.Security.Cryptography;
using System.Text.Json;

namespace FUPlayer.Update;

/// <summary>Backs up replaced files, then applies atomic file replacements with rollback on failure.</summary>
public sealed class UpdateTransaction
{
    private sealed record Original(UpdateFile File, bool Existed, string? Hash);
    private readonly string _payload, _target, _backup;
    private readonly List<Original> _files;
    private readonly List<Original> _changed = [];

    private UpdateTransaction(string payload, string target, string backup, List<Original> files) =>
        (_payload, _target, _backup, _files) = (payload, target, backup, files);

    public static UpdateTransaction Prepare(string payload, string target, string backup, IReadOnlyList<UpdateFile> files)
    {
        if (!Directory.Exists(target) || Path.GetPathRoot(Path.GetFullPath(target)) == Path.GetFullPath(target))
            throw new IOException("Choose a writable FUPlayer installation folder.");
        if (Directory.Exists(backup)) throw new IOException("The update backup already exists.");
        var originals = new List<Original>();
        Directory.CreateDirectory(backup);
        foreach (UpdateFile file in files.OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            string source = UpdateArchive.Inside(payload, file.RelativePath), destination = UpdateArchive.Inside(target, file.RelativePath);
            UpdateArchive.RejectLinks(payload, source);
            UpdateArchive.RejectLinks(target, destination);
            if (!Hash(source).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The prepared update changed.");
            if (Directory.Exists(destination)) throw new IOException("A folder occupies a package file's destination.");
            bool existed = File.Exists(destination);
            string? originalHash = null;
            if (existed)
            {
                if ((File.GetAttributes(destination) & FileAttributes.ReadOnly) != 0) throw new UnauthorizedAccessException("An installed file is read-only.");
                string copy = UpdateArchive.Inside(Path.Combine(backup, "files"), file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(destination, copy);
                originalHash = Hash(copy);
            }
            originals.Add(new(file, existed, originalHash));
        }
        File.WriteAllText(Path.Combine(backup, "replaced-files.json"), JsonSerializer.Serialize(originals));
        return new(payload, target, backup, originals);
    }

    public void Apply()
    {
        try
        {
            // The still-running player could have changed an installation file after the backup.
            foreach (Original item in _files)
            {
                string destination = UpdateArchive.Inside(_target, item.File.RelativePath);
                UpdateArchive.RejectLinks(_target, destination);
                if (File.Exists(destination) != item.Existed || (item.Existed && Hash(destination) != item.Hash))
                    throw new IOException("An installed file changed while preparing the update.");
            }
            foreach (Original item in _files)
            {
                string source = UpdateArchive.Inside(_payload, item.File.RelativePath);
                if (!Hash(source).Equals(item.File.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The prepared update changed.");
                CopyAtomic(source, UpdateArchive.Inside(_target, item.File.RelativePath));
                _changed.Add(item);
            }
        }
        catch (Exception failed)
        {
            try { Rollback(); }
            catch (Exception restore) { throw new AggregateException("The update failed and some files could not be restored. Keep the backup.", failed, restore); }
            throw;
        }
    }

    public void Rollback()
    {
        var failures = new List<Exception>();
        foreach (Original item in _changed.AsEnumerable().Reverse())
        {
            try
            {
                string destination = UpdateArchive.Inside(_target, item.File.RelativePath);
                UpdateArchive.RejectLinks(_target, destination);
                if (item.Existed) CopyAtomic(UpdateArchive.Inside(Path.Combine(_backup, "files"), item.File.RelativePath), destination);
                else File.Delete(destination);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failures.Add(ex); }
        }
        if (failures.Count != 0) throw new AggregateException("Some original files could not be restored.", failures);
        _changed.Clear();
    }

    private static void CopyAtomic(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + ".update-" + Guid.NewGuid().ToString("N");
        try { File.Copy(source, temporary); File.Move(temporary, destination, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static string Hash(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
