using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;
using FUPlayer.Core.Library;
using FUPlayer.Core.Metadata;

namespace FUPlayer.App.Services;

/// <summary>
/// Decoded cover art, kept in memory for the pictures on screen and on disk as thumbnails for the rest.
/// </summary>
/// <remarks>
/// <para>
/// Album art in a library is stored at whatever size the release came with, often 1500 x 1500 or larger, and a
/// grid of a hundred albums decodes a hundred of them. The memory cache holds the most recent 400 at the size
/// they are drawn; the disk cache holds the same scaled pictures as small PNGs, so the second visit to a folder
/// decodes thumbnails of a few kilobytes instead of the originals.
/// </para>
/// <para>
/// A thumbnail is keyed by its source, the width it was decoded to, and the source file's size and modification
/// time, so replacing a cover invalidates it without any explicit purge. Writes go to a temporary file and are
/// then moved into place, so an interrupted write leaves no half-written PNG behind.
/// </para>
/// </remarks>
public sealed class CoverArtCache
{
    private const int Capacity = 400;

    /// <summary>Thumbnails kept on disk before the oldest are removed.</summary>
    private const int DiskCapacity = 4_000;

    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<KeyValuePair<string, Task<Bitmap?>>>> _map = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<KeyValuePair<string, Task<Bitmap?>>> _order = new();
    private readonly SemaphoreSlim _slots = new(Math.Clamp(Environment.ProcessorCount / 2, 2, 6));
    private readonly string? _thumbnails;
    private int _written;

    /// <param name="thumbnailDirectory">
    /// Where scaled pictures are kept between runs, or null to decode from the originals every time.
    /// </param>
    public CoverArtCache(string? thumbnailDirectory = null) => _thumbnails = thumbnailDirectory;

    /// <summary>Embedded picture of a track, falling back to a cover image in its folder.</summary>
    public Task<Bitmap?> GetTrackCoverAsync(string trackPath, int width) =>
        Get($"track|{width}|{trackPath}", width, trackPath, () =>
        {
            if (MetadataReader.ReadEmbeddedCover(trackPath) is { Length: > 0 } bytes)
            {
                return new MemoryStream(bytes, writable: false);
            }

            string? folder = Path.GetDirectoryName(trackPath);
            return folder is not null && MetadataReader.FindFolderCover(folder) is { } file ? File.OpenRead(file) : null;
        });

    /// <summary>Folder cover of an album, falling back to the first embedded picture.</summary>
    /// <remarks>
    /// The key carries where the picture would come from, not just the folder, so an album that had no cover
    /// when it was first shown picks one up as soon as a scan finds one. Otherwise the cached "nothing here"
    /// would outlive the file appearing.
    /// </remarks>
    public Task<Bitmap?> GetAlbumCoverAsync(LibraryAlbum album, int width)
    {
        ArgumentNullException.ThrowIfNull(album);
        return Get(
            $"album|{width}|{album.Folder}|{album.CoverFile}|{album.EmbeddedCoverSource}",
            width,
            album.CoverFile ?? album.EmbeddedCoverSource,
            () =>
            {
                if (album.CoverFile is { } file && File.Exists(file))
                {
                    return File.OpenRead(file);
                }

                return album.EmbeddedCoverSource is { } source && MetadataReader.ReadEmbeddedCover(source) is { Length: > 0 } bytes
                    ? new MemoryStream(bytes, writable: false)
                    : null;
            });
    }

    private Task<Bitmap?> Get(string key, int width, string? source, Func<Stream?> open)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out LinkedListNode<KeyValuePair<string, Task<Bitmap?>>>? node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                return node.Value.Value;
            }

            Task<Bitmap?> task = LoadAsync(key, width, source, open);
            _map[key] = _order.AddFirst(new KeyValuePair<string, Task<Bitmap?>>(key, task));
            while (_order.Count > Capacity && _order.Last is { } last)
            {
                _map.Remove(last.Value.Key);
                _order.RemoveLast();
            }

            return task;
        }
    }

    private async Task<Bitmap?> LoadAsync(string key, int width, string? source, Func<Stream?> open)
    {
        await _slots.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                string? thumbnail = ThumbnailPath(key, source);
                if (thumbnail is not null && File.Exists(thumbnail))
                {
                    try
                    {
                        return new Bitmap(thumbnail);
                    }
                    catch (Exception)
                    {
                        // A truncated thumbnail is worth nothing; fall through and decode the original again.
                    }
                }

                Bitmap? decoded;
                try
                {
                    using Stream? stream = open();
                    decoded = stream is null ? null : Bitmap.DecodeToWidth(stream, width, BitmapInterpolationMode.HighQuality);
                }
                catch (Exception)
                {
                    // Unreadable or corrupt pictures simply show the placeholder.
                    return null;
                }

                if (decoded is not null && thumbnail is not null)
                {
                    Store(thumbnail, decoded);
                }

                return decoded;
            }).ConfigureAwait(false);
        }
        finally
        {
            _slots.Release();
        }
    }

    /// <summary>
    /// Where this picture's thumbnail belongs, or null when there is no cache directory or no file to stamp it
    /// with. The stamp is the source's length and modification time, which is what makes a replaced cover miss.
    /// </summary>
    private string? ThumbnailPath(string key, string? source)
    {
        if (_thumbnails is null || source is null)
        {
            return null;
        }

        try
        {
            var file = new FileInfo(source);
            if (!file.Exists)
            {
                return null;
            }

            byte[] digest = SHA256.HashData(
                Encoding.UTF8.GetBytes($"{key}|{file.Length}|{file.LastWriteTimeUtc.Ticks}"));
            return Path.Combine(_thumbnails, Convert.ToHexString(digest, 0, 12) + ".png");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private void Store(string path, Bitmap bitmap)
    {
        try
        {
            Directory.CreateDirectory(_thumbnails!);
            string temporary = path + ".tmp";
            using (FileStream file = File.Create(temporary))
            {
                bitmap.Save(file, new PngBitmapEncoderOptions());
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        // Cheap enough to do now and then rather than on every write, and it only has to keep the folder from
        // growing without limit.
        if (Interlocked.Increment(ref _written) % 512 == 0)
        {
            Prune();
        }
    }

    private void Prune()
    {
        try
        {
            var files = new DirectoryInfo(_thumbnails!).GetFiles("*.png");
            if (files.Length <= DiskCapacity)
            {
                return;
            }

            foreach (FileInfo file in files.OrderBy(f => f.LastWriteTimeUtc).Take(files.Length - DiskCapacity))
            {
                file.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
