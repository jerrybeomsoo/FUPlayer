using FUPlayer.Core.Metadata;
using FUPlayer.Core.Settings;

namespace FUPlayer.Core.Playlists;

/// <summary>One entry of the play queue.</summary>
public sealed class QueueItem
{
    public QueueItem(string path)
    {
        Path = path;
    }

    public Guid Id { get; } = Guid.NewGuid();

    public string Path { get; }

    /// <summary>Loaded lazily; may be null until the item is displayed or played.</summary>
    public TrackMetadata? Metadata { get; set; }

    /// <summary>Last error opening the item, if any.</summary>
    public string? Error { get; set; }

    public string DisplayTitle => Metadata?.DisplayTitle ?? System.IO.Path.GetFileNameWithoutExtension(Path);

    public string DisplayArtist => Metadata?.DisplayArtist ?? string.Empty;
}

/// <summary>Thread-safe ordered play queue with repeat and shuffle navigation.</summary>
public sealed class PlayQueue
{
    private readonly object _gate = new();
    private readonly List<QueueItem> _items = [];
    private readonly Random _random = new();
    private List<Guid> _shuffleOrder = [];

    public event EventHandler? Changed;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    /// <summary>Snapshot of the items.</summary>
    public IReadOnlyList<QueueItem> Items
    {
        get
        {
            lock (_gate)
            {
                return _items.ToArray();
            }
        }
    }

    public QueueItem? Get(int index)
    {
        lock (_gate)
        {
            return (uint)index < (uint)_items.Count ? _items[index] : null;
        }
    }

    public int IndexOf(Guid id)
    {
        lock (_gate)
        {
            return _items.FindIndex(item => item.Id == id);
        }
    }

    public IReadOnlyList<QueueItem> Add(IEnumerable<string> paths) => Insert(int.MaxValue, paths);

    public IReadOnlyList<QueueItem> Insert(int index, IEnumerable<string> paths)
    {
        List<QueueItem> added = paths.Select(p => new QueueItem(p)).ToList();
        if (added.Count == 0)
        {
            return added;
        }

        lock (_gate)
        {
            _items.InsertRange(Math.Clamp(index, 0, _items.Count), added);
            RebuildShuffle();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return added;
    }

    public void Remove(IEnumerable<Guid> ids)
    {
        var set = ids.ToHashSet();
        lock (_gate)
        {
            if (_items.RemoveAll(item => set.Contains(item.Id)) == 0)
            {
                return;
            }

            RebuildShuffle();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Move(int from, int to)
    {
        lock (_gate)
        {
            if ((uint)from >= (uint)_items.Count || from == to)
            {
                return;
            }

            QueueItem item = _items[from];
            _items.RemoveAt(from);
            _items.Insert(Math.Clamp(to, 0, _items.Count), item);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Moves several items at once so that they land, keeping their queue order, in front of the item now at
    /// <paramref name="insertionIndex"/>; the item count means the end. One notification covers the whole move,
    /// which is what makes dragging a large selection cheap.
    /// </summary>
    public void Move(IReadOnlyCollection<Guid> ids, int insertionIndex)
    {
        if (ids.Count == 0)
        {
            return;
        }

        HashSet<Guid> set = ids as HashSet<Guid> ?? [.. ids];
        lock (_gate)
        {
            var moving = new List<QueueItem>(set.Count);
            int target = insertionIndex;
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                if (!set.Contains(_items[i].Id))
                {
                    continue;
                }

                moving.Add(_items[i]);
                _items.RemoveAt(i);

                // An item taken from in front of the gap moves the gap back with it.
                if (i < insertionIndex)
                {
                    target--;
                }
            }

            if (moving.Count == 0)
            {
                return;
            }

            moving.Reverse();
            _items.InsertRange(Math.Clamp(target, 0, _items.Count), moving);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        lock (_gate)
        {
            _items.Clear();
            _shuffleOrder.Clear();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Index to play after <paramref name="current"/>, or −1 at the end.</summary>
    /// <param name="current">Current index.</param>
    /// <param name="repeat">Repeat mode.</param>
    /// <param name="shuffle">Shuffle order.</param>
    /// <param name="userRequested">True for an explicit "next" (ignores repeat-one).</param>
    public int NextIndex(int current, RepeatMode repeat, bool shuffle, bool userRequested)
    {
        lock (_gate)
        {
            int count = _items.Count;
            if (count == 0)
            {
                return -1;
            }

            if (!userRequested && repeat == RepeatMode.One && (uint)current < (uint)count)
            {
                return current;
            }

            if (!shuffle)
            {
                int next = current + 1;
                return next < count ? next : repeat != RepeatMode.Off ? 0 : -1;
            }

            int position = (uint)current < (uint)count ? _shuffleOrder.IndexOf(_items[current].Id) : -1;
            if (position + 1 < _shuffleOrder.Count)
            {
                Guid id = _shuffleOrder[position + 1];
                return _items.FindIndex(item => item.Id == id);
            }

            if (repeat == RepeatMode.Off)
            {
                return -1;
            }

            RebuildShuffle();
            Guid first = _shuffleOrder[0];
            return _items.FindIndex(item => item.Id == first);
        }
    }

    public int PreviousIndex(int current, bool shuffle)
    {
        lock (_gate)
        {
            if (_items.Count == 0)
            {
                return -1;
            }

            if (!shuffle)
            {
                return Math.Max(0, current - 1);
            }

            int position = (uint)current < (uint)_items.Count ? _shuffleOrder.IndexOf(_items[current].Id) : 0;
            if (position <= 0)
            {
                return Math.Max(0, current);
            }

            Guid id = _shuffleOrder[position - 1];
            return _items.FindIndex(item => item.Id == id);
        }
    }

    private void RebuildShuffle()
    {
        _shuffleOrder = _items.Select(item => item.Id).ToList();
        for (int i = _shuffleOrder.Count - 1; i > 0; i--)
        {
            int j = _random.Next(i + 1);
            (_shuffleOrder[i], _shuffleOrder[j]) = (_shuffleOrder[j], _shuffleOrder[i]);
        }
    }
}
