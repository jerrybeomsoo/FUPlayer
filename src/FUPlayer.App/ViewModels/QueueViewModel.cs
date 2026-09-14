using System.Collections;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FUPlayer.App.Services;
using FUPlayer.Core.Audio;
using FUPlayer.Core.Engine;
using FUPlayer.Core.Metadata;
using FUPlayer.Core.Playlists;

namespace FUPlayer.App.ViewModels;

/// <summary>A row of the play queue.</summary>
public sealed partial class QueueItemViewModel : ObservableObject
{
    private readonly QueueViewModel _owner;
    private Bitmap? _thumbnail;
    private bool _thumbnailRequested;

    [ObservableProperty]
    private int _number;

    [ObservableProperty]
    private bool _isCurrent;

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private string _artist = string.Empty;

    [ObservableProperty]
    private string _album = string.Empty;

    [ObservableProperty]
    private string _format = string.Empty;

    [ObservableProperty]
    private string _duration = string.Empty;

    [ObservableProperty]
    private bool _isDsd;

    public QueueItemViewModel(QueueItem item, QueueViewModel owner)
    {
        Item = item;
        _owner = owner;
        _title = item.DisplayTitle;
        if (item.Metadata is { } metadata)
        {
            Apply(metadata);
        }
    }

    public QueueItem Item { get; }

    public Guid Id => Item.Id;

    public bool IsLoaded { get; private set; }

    public TimeSpan DurationValue { get; private set; }

    /// <summary>Row cover; decoding starts when a row first scrolls into view and binds to it.</summary>
    public Bitmap? Thumbnail
    {
        get
        {
            if (!_thumbnailRequested)
            {
                _thumbnailRequested = true;
                _ = LoadThumbnailAsync();
            }

            return _thumbnail;
        }
    }

    public bool HasThumbnail => _thumbnail is not null;

    public void Apply(TrackMetadata metadata)
    {
        IsLoaded = true;
        Title = metadata.DisplayTitle;
        Artist = metadata.DisplayArtist;
        Album = metadata.DisplayAlbum;
        DurationValue = metadata.Duration;
        Duration = metadata.Duration > TimeSpan.Zero ? Formatting.Time(metadata.Duration) : string.Empty;
        string extension = Path.GetExtension(Item.Path).TrimStart('.').ToUpperInvariant();
        if (metadata.Format is { } format)
        {
            IsDsd = format.IsDsd;
            Format = format.IsDsd
                ? $"DSD{AudioRates.DsdMultiplier(format.SampleRate)}"
                : $"{extension} {AudioRates.FormatShort(format.SampleRate)}/{format.BitsPerSample}";
        }
        else
        {
            Format = extension;
        }
    }

    [RelayCommand]
    private void Play() => _owner.PlayItem(this);

    private async Task LoadThumbnailAsync()
    {
        Bitmap? bitmap = await _owner.Covers.GetTrackCoverAsync(Item.Path, 96);
        if (bitmap is not null)
        {
            _thumbnail = bitmap;
            OnPropertyChanged(nameof(Thumbnail));
            OnPropertyChanged(nameof(HasThumbnail));
        }
    }
}

/// <summary>The Queue page.</summary>
public sealed partial class QueueViewModel : ObservableObject
{
    private readonly PlayerServices _services;
    private readonly IDialogService _dialogs;
    private readonly Dictionary<Guid, QueueItemViewModel> _byId = [];
    private bool _metadataRunning;
    private bool _metadataDirty;
    private Guid? _currentId;

    [ObservableProperty]
    private string _summary = "The queue is empty";

    [ObservableProperty]
    private bool _isEmpty = true;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private QueueItemViewModel? _selectedItem;

    public QueueViewModel(PlayerServices services, IDialogService dialogs)
    {
        _services = services;
        _dialogs = dialogs;
        services.Engine.Queue.Changed += (_, _) => Dispatcher.UIThread.Post(Sync);
        Sync();
    }

    public ObservableCollection<QueueItemViewModel> Items { get; } = [];

    /// <summary>Shared with the rows so each can fetch its own cover.</summary>
    public CoverArtCache Covers => _services.Covers;

    private PlayQueue Queue => _services.Engine.Queue;

    /// <summary>Expands folders and playlists and appends the result; optionally starts playback when idle.</summary>
    public async Task AddPathsAsync(IReadOnlyList<string> paths, bool playWhenIdle = false)
    {
        if (paths.Count == 0)
        {
            return;
        }

        IsBusy = true;
        try
        {
            IReadOnlyList<string> expanded = await Task.Run(() => Expand(paths));
            if (expanded.Count == 0)
            {
                return;
            }

            int first = Queue.Count;
            Queue.Add(expanded);
            if (playWhenIdle && _services.Engine.State == EngineState.Stopped)
            {
                _services.Engine.Play(first);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Replaces the queue with <paramref name="paths"/> and plays from <paramref name="startIndex"/>.</summary>
    public void ReplaceAndPlay(IReadOnlyList<string> paths, int startIndex)
    {
        Queue.Clear();
        Queue.Add(paths);
        _services.Engine.Play(Math.Clamp(startIndex, 0, Math.Max(0, paths.Count - 1)));
    }

    public void Enqueue(IReadOnlyList<string> paths) => Queue.Add(paths);

    /// <summary>Inserts after the item that is playing now.</summary>
    public void PlayNext(IReadOnlyList<string> paths)
    {
        int current = _currentId is { } id ? Queue.IndexOf(id) : -1;
        Queue.Insert(current + 1, paths);
    }

    public void PlayItem(QueueItemViewModel item)
    {
        int index = Queue.IndexOf(item.Id);
        if (index >= 0)
        {
            _services.Engine.Play(index);
        }
    }

    /// <summary>Moves <paramref name="items"/>, keeping their queue order, to right after the item that is playing now.</summary>
    public void MoveAfterCurrent(IEnumerable<QueueItemViewModel> items)
    {
        Guid[] ids = items
            .Where(item => item.Id != _currentId)
            .OrderBy(item => Queue.IndexOf(item.Id))
            .Select(item => item.Id)
            .ToArray();
        int anchor = _currentId is { } current ? Queue.IndexOf(current) : -1;
        foreach (Guid id in ids)
        {
            int from = Queue.IndexOf(id);
            if (from < 0)
            {
                continue;
            }

            // Removing an item that sits before the anchor shifts the anchor back by one.
            Queue.Move(from, from > anchor ? anchor + 1 : anchor);
            anchor = Queue.IndexOf(id);
        }
    }

    /// <summary>
    /// Moves the given rows, keeping their queue order, so that they land in front of the row now at
    /// <paramref name="insertionIndex"/> (the item count means the end).
    /// </summary>
    public void MoveTo(IReadOnlyList<QueueItemViewModel> items, int insertionIndex)
    {
        Guid[] ids = items.Select(item => item.Id).Where(id => Queue.IndexOf(id) >= 0).ToArray();
        if (ids.Length == 0)
        {
            return;
        }

        Queue.Move(ids, insertionIndex);
        SelectedItem = items[0];
    }

    /// <summary>Opens the file's folder in the system file manager, with the file selected where possible.</summary>
    public static void ShowInFolder(QueueItemViewModel item) => FileManager.Reveal(item.Item.Path);

    /// <summary>Called by the status poll to highlight the audible item.</summary>
    public void SetCurrent(Guid? id)
    {
        if (id == _currentId)
        {
            return;
        }

        if (_currentId is { } previous && _byId.TryGetValue(previous, out QueueItemViewModel? old))
        {
            old.IsCurrent = false;
        }

        _currentId = id;
        if (id is { } current && _byId.TryGetValue(current, out QueueItemViewModel? item))
        {
            item.IsCurrent = true;
        }
    }

    /// <summary>Total known duration of the items after <paramref name="id"/>.</summary>
    public TimeSpan DurationAfter(Guid id)
    {
        TimeSpan total = TimeSpan.Zero;
        bool after = false;
        foreach (QueueItemViewModel item in Items)
        {
            if (after)
            {
                total += item.DurationValue;
            }
            else if (item.Id == id)
            {
                after = true;
            }
        }

        return total;
    }

    [RelayCommand]
    private async Task AddFilesAsync() => await AddPathsAsync(await _dialogs.PickAudioFilesAsync());

    [RelayCommand]
    private async Task AddFolderAsync() => await AddPathsAsync(await _dialogs.PickFoldersAsync("Add folders to the queue"));

    [RelayCommand]
    private async Task OpenPlaylistAsync()
    {
        if (await _dialogs.PickPlaylistAsync() is { } path)
        {
            await AddPathsAsync([path]);
        }
    }

    [RelayCommand]
    private async Task SavePlaylistAsync()
    {
        if (Queue.Count == 0 || await _dialogs.PickSavePlaylistAsync("Queue.m3u8") is not { } path)
        {
            return;
        }

        try
        {
            PlaylistFile.Save(path, Queue.Items);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Summary = "Could not save the playlist: " + ex.Message;
        }
    }

    [RelayCommand]
    private void Clear() => Queue.Clear();

    [RelayCommand]
    private void RemoveSelected(IList? selected)
    {
        if (selected is null || selected.Count == 0)
        {
            return;
        }

        Queue.Remove(selected.OfType<QueueItemViewModel>().Select(item => item.Id).ToArray());
    }

    [RelayCommand]
    private void MoveUp(QueueItemViewModel? item) => Move(item, -1);

    [RelayCommand]
    private void MoveDown(QueueItemViewModel? item) => Move(item, 1);

    private void Move(QueueItemViewModel? item, int delta)
    {
        item ??= SelectedItem;
        if (item is null)
        {
            return;
        }

        int index = Queue.IndexOf(item.Id);
        if (index >= 0)
        {
            Queue.Move(index, index + delta);
            SelectedItem = item;
        }
    }

    private static IReadOnlyList<string> Expand(IReadOnlyList<string> paths)
    {
        var result = new List<string>();
        foreach (string path in paths)
        {
            if (PlaylistFile.IsPlaylist(path) && File.Exists(path))
            {
                try
                {
                    result.AddRange(PlaylistFile.Load(path));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
                {
                    // Unreadable playlists are skipped.
                }
            }
            else
            {
                result.AddRange(MediaScanner.Expand([path]));
            }
        }

        return result;
    }

    private void Sync()
    {
        IReadOnlyList<QueueItem> items = Queue.Items;
        int prefix = 0;
        while (prefix < items.Count && prefix < Items.Count && Items[prefix].Id == items[prefix].Id)
        {
            prefix++;
        }

        while (Items.Count > prefix)
        {
            Items.RemoveAt(Items.Count - 1);
        }

        var alive = new HashSet<Guid>(items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            QueueItem item = items[i];
            alive.Add(item.Id);
            if (!_byId.TryGetValue(item.Id, out QueueItemViewModel? vm))
            {
                vm = new QueueItemViewModel(item, this);
                _byId[item.Id] = vm;
            }

            vm.Number = i + 1;
            vm.IsCurrent = item.Id == _currentId;
            if (i >= prefix)
            {
                Items.Add(vm);
            }
        }

        foreach (Guid stale in _byId.Keys.Where(id => !alive.Contains(id)).ToArray())
        {
            _byId.Remove(stale);
        }

        UpdateSummary();
        _ = LoadMetadataAsync();
    }

    private void UpdateSummary()
    {
        IsEmpty = Items.Count == 0;
        if (IsEmpty)
        {
            Summary = "The queue is empty";
            return;
        }

        TimeSpan total = TimeSpan.FromTicks(Items.Sum(i => i.DurationValue.Ticks));
        Summary = $"{Items.Count} {(Items.Count == 1 ? "track" : "tracks")}  ·  {Formatting.Time(total)}";
    }

    private async Task LoadMetadataAsync()
    {
        if (_metadataRunning)
        {
            _metadataDirty = true;
            return;
        }

        _metadataRunning = true;
        try
        {
            do
            {
                _metadataDirty = false;
                QueueItemViewModel[] pending = Items.Where(i => !i.IsLoaded).ToArray();
                foreach (QueueItemViewModel[] chunk in pending.Chunk(24))
                {
                    TrackMetadata[] results = await Task.Run(() => chunk
                        .Select(vm => vm.Item.Metadata ?? MetadataLoader.ReadSafe(vm.Item.Path, probeFormat: false))
                        .ToArray());
                    for (int i = 0; i < chunk.Length; i++)
                    {
                        chunk[i].Item.Metadata ??= results[i];
                        chunk[i].Apply(results[i]);
                    }

                    UpdateSummary();
                }
            }
            while (_metadataDirty);
        }
        finally
        {
            _metadataRunning = false;
        }
    }
}
