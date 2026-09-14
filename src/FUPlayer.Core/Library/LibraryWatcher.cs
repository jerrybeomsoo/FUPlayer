using FUPlayer.Core.Decoding;

namespace FUPlayer.Core.Library;

/// <summary>
/// Watches the library folders and reports, once things have settled, that a rescan is worth running.
/// </summary>
/// <remarks>
/// Copying an album in produces a burst of events, one per file and often several per file, and a rescan while
/// the copy is still running would find half an album. Every event therefore restarts a quiet timer, and only
/// silence for <see cref="SettleDelay"/> raises <see cref="ChangesSettled"/>. A watcher that overflows its
/// buffer reports that it lost events, which is also a reason to rescan, so that counts as a change too.
/// </remarks>
public sealed class LibraryWatcher : IDisposable
{
    /// <summary>How long the folders must be quiet before a rescan is suggested.</summary>
    public static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(3);

    private readonly object _gate = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Timer _settle;
    private bool _disposed;

    public LibraryWatcher()
    {
        _settle = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Raised on a thread-pool thread once the watched folders have been quiet for <see cref="SettleDelay"/>.</summary>
    public event EventHandler? ChangesSettled;

    /// <summary>Folders being watched right now (empty when watching is off).</summary>
    public IReadOnlyList<string> Folders
    {
        get
        {
            lock (_gate)
            {
                return _watchers.Select(w => w.Path).ToArray();
            }
        }
    }

    /// <summary>Replaces the watched set. Folders that do not exist, or cannot be watched, are skipped.</summary>
    public void Watch(IEnumerable<string> folders)
    {
        string[] wanted = folders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_watchers.Select(w => w.Path).SequenceEqual(wanted, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            StopAll();
            foreach (string folder in wanted)
            {
                try
                {
                    var watcher = new FileSystemWatcher(folder)
                    {
                        IncludeSubdirectories = true,
                        InternalBufferSize = 64 * 1024,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    };
                    watcher.Created += OnChanged;
                    watcher.Deleted += OnChanged;
                    watcher.Changed += OnChanged;
                    watcher.Renamed += OnRenamed;
                    watcher.Error += OnError;
                    watcher.EnableRaisingEvents = true;
                    _watchers.Add(watcher);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    // A folder on a volume that cannot be watched (some network shares) is simply not watched.
                }
            }
        }
    }

    /// <summary>Stops watching everything; a pending rescan suggestion is dropped with it.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            StopAll();
            _settle.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            StopAll();
        }

        _settle.Dispose();
    }

    /// <summary>Whether a changed path is worth a rescan: audio files, cover art, and folders.</summary>
    internal static bool IsInteresting(string path)
    {
        if (DecoderFactory.IsAudioFile(path))
        {
            return true;
        }

        string extension = Path.GetExtension(path);
        if (extension.Length == 0)
        {
            // No extension: most likely a folder being created, renamed or removed.
            return true;
        }

        return extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".webp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase);
    }

    private void StopAll()
    {
        foreach (FileSystemWatcher watcher in _watchers)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // Already gone.
            }
        }

        _watchers.Clear();
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (IsInteresting(e.FullPath))
        {
            Restart();
        }
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (IsInteresting(e.FullPath) || IsInteresting(e.OldFullPath))
        {
            Restart();
        }
    }

    // Losing events means the library may now disagree with the disk, which is exactly when to rescan.
    private void OnError(object sender, ErrorEventArgs e) => Restart();

    private void Restart()
    {
        lock (_gate)
        {
            if (!_disposed && _watchers.Count > 0)
            {
                _settle.Change(SettleDelay, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void Fire()
    {
        lock (_gate)
        {
            if (_disposed || _watchers.Count == 0)
            {
                return;
            }
        }

        ChangesSettled?.Invoke(this, EventArgs.Empty);
    }
}
