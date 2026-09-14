using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FUPlayer.App.Services;
using FUPlayer.Core.Library;
using FUPlayer.Core.Settings;

namespace FUPlayer.App.ViewModels;

/// <summary>A track row in the album detail panel.</summary>
public sealed partial class TrackViewModel(LibraryTrack track, AlbumViewModel album, int index, string number, string subtitle) : ObservableObject
{
    public LibraryTrack Track { get; } = track;

    public int Index { get; } = index;

    public string Number { get; } = number;

    public string Title => Track.Title;

    public string Subtitle { get; } = subtitle;

    public bool HasSubtitle => Subtitle.Length > 0;

    public string Duration => Track.DurationSeconds > 0 ? Formatting.Time(Track.Duration) : string.Empty;

    public string Format => Track.Format;

    [RelayCommand]
    private void Play() => album.PlayFrom(Index);

    [RelayCommand]
    private void Enqueue() => album.Owner.Queue.Enqueue([Track.Path]);

    [RelayCommand]
    private void PlayNext() => album.Owner.Queue.PlayNext([Track.Path]);

    [RelayCommand]
    private void PlayOnly() => album.Owner.Queue.ReplaceAndPlay([Track.Path], 0);

    [RelayCommand]
    private void ShowInFolder() => FileManager.Reveal(Track.Path);
}

/// <summary>An album tile and its detail view.</summary>
public sealed partial class AlbumViewModel : ObservableObject
{
    private readonly CoverArtCache _covers;
    private Bitmap? _thumbnail;
    private Bitmap? _largeCover;
    private bool _thumbnailRequested;
    private bool _largeCoverRequested;
    private IReadOnlyList<TrackViewModel>? _tracks;

    public AlbumViewModel(LibraryAlbum album, LibraryViewModel owner, CoverArtCache covers)
    {
        Album = album;
        Owner = owner;
        _covers = covers;
        IsDsd = album.Tracks.Any(t => t.IsDsd);
        IsHiRes = !IsDsd && album.Tracks.Any(t => t.SampleRate > 48_000 || t.BitsPerSample > 16);

        var parts = new List<string>();
        if (album.Year > 0)
        {
            parts.Add(album.Year.ToString(CultureInfo.InvariantCulture));
        }

        parts.Add($"{album.Tracks.Count} {(album.Tracks.Count == 1 ? "track" : "tracks")}");
        parts.Add(Formatting.Time(album.Duration));
        Details = string.Join("  ·  ", parts);

        string[] words = album.Title.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Initials = string.Concat(words.Take(2).Select(w => char.ToUpperInvariant(w[0])));
        PlaceholderBrush = CreatePlaceholder(album.Folder);
    }

    public LibraryAlbum Album { get; }

    public LibraryViewModel Owner { get; }

    public string Title => Album.Title;

    public string Artist => Album.Artist;

    public string Format => Album.FormatSummary;

    public string Genre => Album.Genre;

    public string Folder => Album.Folder;

    public string Details { get; }

    public bool IsDsd { get; }

    public bool IsHiRes { get; }

    public string Initials { get; }

    public IBrush PlaceholderBrush { get; }

    /// <summary>Small cover; loading starts when a tile first binds to it.</summary>
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

    public Bitmap? LargeCover
    {
        get
        {
            if (!_largeCoverRequested)
            {
                _largeCoverRequested = true;
                _ = LoadLargeCoverAsync();
            }

            return _largeCover;
        }
    }

    public IReadOnlyList<TrackViewModel> Tracks => _tracks ??= BuildTracks();

    private string[] Paths => Album.Tracks.Select(t => t.Path).ToArray();

    public void PlayFrom(int index) => Owner.Queue.ReplaceAndPlay(Paths, index);

    [RelayCommand]
    private void Open() => Owner.SelectedAlbum = this;

    [RelayCommand]
    private void Play() => PlayFrom(0);

    [RelayCommand]
    private void Enqueue() => Owner.Queue.Enqueue(Paths);

    [RelayCommand]
    private void PlayNext() => Owner.Queue.PlayNext(Paths);

    [RelayCommand]
    private void ShowInFolder() => FileManager.Reveal(Album.Folder);

    [RelayCommand]
    private void ShowArtist() => Owner.SearchText = $"artist:\"{Album.Artist}\"";

    [RelayCommand]
    private void ShowGenre() => Owner.SearchText = $"genre:\"{Album.Genre}\"";

    private async Task LoadThumbnailAsync()
    {
        Bitmap? bitmap = await _covers.GetAlbumCoverAsync(Album, 240);
        if (bitmap is not null)
        {
            _thumbnail = bitmap;
            OnPropertyChanged(nameof(Thumbnail));
        }
    }

    private async Task LoadLargeCoverAsync()
    {
        Bitmap? bitmap = await _covers.GetAlbumCoverAsync(Album, 640);
        if (bitmap is not null)
        {
            _largeCover = bitmap;
            OnPropertyChanged(nameof(LargeCover));
        }
    }

    private List<TrackViewModel> BuildTracks()
    {
        bool multiDisc = Album.Tracks.Select(t => t.DiscNumber).Where(d => d > 0).Distinct().Count() > 1;
        var list = new List<TrackViewModel>(Album.Tracks.Count);
        for (int i = 0; i < Album.Tracks.Count; i++)
        {
            LibraryTrack track = Album.Tracks[i];
            string number = track.TrackNumber > 0
                ? multiDisc && track.DiscNumber > 0
                    ? string.Create(CultureInfo.InvariantCulture, $"{track.DiscNumber}-{track.TrackNumber:00}")
                    : track.TrackNumber.ToString(CultureInfo.InvariantCulture)
                : (i + 1).ToString(CultureInfo.InvariantCulture);
            bool showArtist = track.Artist.Length > 0 && !track.Artist.Equals(Album.Artist, StringComparison.CurrentCultureIgnoreCase);
            list.Add(new TrackViewModel(track, this, i, number, showArtist ? track.Artist : string.Empty));
        }

        return list;
    }

    private static LinearGradientBrush CreatePlaceholder(string key)
    {
        uint hash = 2166136261;
        foreach (char c in key.ToUpperInvariant())
        {
            hash = (hash ^ c) * 16777619;
        }

        double hue = hash % 360;
        Color top = new HslColor(1.0, hue, 0.42, 0.34).ToRgb();
        Color bottom = new HslColor(1.0, (hue + 36) % 360, 0.5, 0.16).ToRgb();
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(top, 0), new GradientStop(bottom, 1) },
        };
    }
}

/// <summary>A library root folder with its remove command.</summary>
public sealed record FolderItem(string Path, System.Windows.Input.ICommand RemoveCommand);

/// <summary>One artist or genre in the browse list beside the albums.</summary>
public sealed record LibraryGroup(string Name, int AlbumCount, int TrackCount)
{
    /// <summary>The entry that clears the grouping; its name is never matched against an album.</summary>
    public bool IsAll => AlbumCount < 0;

    public string Count => IsAll ? string.Empty : AlbumCount.ToString(CultureInfo.InvariantCulture);

    public static LibraryGroup All(int albums) => new("All", -1, 0) { Total = albums };

    public int Total { get; private init; }

    public string Summary => IsAll
        ? $"{Total} {(Total == 1 ? "album" : "albums")}"
        : $"{AlbumCount} {(AlbumCount == 1 ? "album" : "albums")}  ·  {TrackCount} {(TrackCount == 1 ? "track" : "tracks")}";
}

/// <summary>A row of album tiles (rows keep the grid virtualised).</summary>
public sealed class AlbumRowViewModel(IReadOnlyList<AlbumViewModel> albums)
{
    public IReadOnlyList<AlbumViewModel> Albums { get; } = albums;
}

/// <summary>The Library page: folder management, scanning, search and album browsing.</summary>
public sealed partial class LibraryViewModel : ObservableObject, IDisposable
{
    private readonly PlayerServices _services;
    private readonly IDialogService _dialogs;
    private readonly DispatcherTimer _searchTimer;
    private readonly Dictionary<string, AlbumViewModel> _albums = new(StringComparer.OrdinalIgnoreCase);
    private readonly LibraryWatcher _watcher = new();
    private IReadOnlyList<LibraryAlbum> _filtered = [];
    private CancellationTokenSource? _scan;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private int _columns = 5;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDetailOpen))]
    private AlbumViewModel? _selectedAlbum;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    private bool _isScanning;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private bool _hasFolders;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    private bool _isLibraryEmpty = true;

    [ObservableProperty]
    private IReadOnlyList<AlbumRowViewModel> _rows = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBrowsingGroups))]
    private Choice _selectedBrowse;

    [ObservableProperty]
    private IReadOnlyList<LibraryGroup> _groups = [];

    [ObservableProperty]
    private LibraryGroup? _selectedGroup;

    [ObservableProperty]
    private bool _watchFolders;

    [ObservableProperty]
    private bool _hasPendingChanges;

    public LibraryViewModel(PlayerServices services, IDialogService dialogs, QueueViewModel queue)
    {
        _services = services;
        _dialogs = dialogs;
        Queue = queue;
        Folders = new ObservableCollection<FolderItem>(services.Settings.Library.Folders.Select(CreateFolderItem));
        HasFolders = Folders.Count > 0;
        _selectedBrowse = Choice.Find(BrowseChoices, services.Settings.Library.Browse) ?? BrowseChoices[0];
        _watchFolders = services.Settings.Library.WatchFolders;
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            Refilter();
        };
        services.Library.Changed += (_, _) => Dispatcher.UIThread.Post(Refilter);
        _watcher.ChangesSettled += (_, _) => Dispatcher.UIThread.Post(OnFoldersChangedOnDisk);
        _ = InitializeAsync();
    }

    public QueueViewModel Queue { get; }

    public ObservableCollection<FolderItem> Folders { get; }

    public IReadOnlyList<Choice> BrowseChoices { get; } =
    [
        new("Albums", LibraryBrowse.Albums),
        new("Artists", LibraryBrowse.Artists),
        new("Genres", LibraryBrowse.Genres),
    ];

    public bool IsDetailOpen => SelectedAlbum is not null;

    public bool ShowEmptyState => IsLibraryEmpty && !IsScanning;

    /// <summary>True when the artist or genre list is shown beside the albums.</summary>
    public bool IsBrowsingGroups => SelectedBrowse.Value is not LibraryBrowse.Albums;

    public void Dispose() => _watcher.Dispose();

    private LibraryBrowse Browse => (LibraryBrowse)SelectedBrowse.Value;

    private FolderItem CreateFolderItem(string path) => new(path, RemoveFolderCommand);

    partial void OnSearchTextChanged(string value)
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    partial void OnColumnsChanged(int value) => RebuildRows();

    partial void OnSelectedBrowseChanged(Choice value)
    {
        _services.Settings.Library.Browse = Browse;
        _services.NotifySettingsChanged(applyToEngine: false);
        RebuildGroups();
        RebuildRows();
    }

    partial void OnSelectedGroupChanged(LibraryGroup? value) => RebuildRows();

    partial void OnWatchFoldersChanged(bool value)
    {
        _services.Settings.Library.WatchFolders = value;
        _services.NotifySettingsChanged(applyToEngine: false);
        ApplyWatch();
    }

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        bool added = false;
        foreach (string folder in await _dialogs.PickFoldersAsync("Add music folders"))
        {
            if (!Folders.Any(f => f.Path.Equals(folder, StringComparison.OrdinalIgnoreCase)))
            {
                Folders.Add(CreateFolderItem(folder));
                added = true;
            }
        }

        if (added)
        {
            SaveFolders();
            await ScanAsync(rescanAll: false);
        }
    }

    [RelayCommand]
    private async Task RemoveFolderAsync(FolderItem? folder)
    {
        if (folder is not null && Folders.Remove(folder))
        {
            SaveFolders();
            await ScanAsync(rescanAll: false);
        }
    }

    [RelayCommand]
    private Task Refresh()
    {
        HasPendingChanges = false;
        return ScanAsync(rescanAll: false);
    }

    [RelayCommand]
    private Task Rescan() => ScanAsync(rescanAll: true);

    [RelayCommand]
    private void CancelScan() => _scan?.Cancel();

    [RelayCommand]
    private void CloseDetail() => SelectedAlbum = null;

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    private async Task InitializeAsync()
    {
        await _services.Library.LoadAsync();
        Refilter();
        ApplyWatch();
        if (Folders.Count > 0 && _services.Library.Albums.Count == 0)
        {
            await ScanAsync(rescanAll: false);
        }
    }

    private async Task ScanAsync(bool rescanAll)
    {
        _scan?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _scan = cancellation;
        IsScanning = true;
        Status = "Scanning…";
        var progress = new Progress<LibraryScanProgress>(p =>
        {
            if (p.CurrentFolder is not null && _scan == cancellation)
            {
                Status = $"Scanning…  {p.Tracks} tracks in {p.Folders} folders";
            }
        });

        try
        {
            await _services.Library.ScanAsync(Folders.Select(f => f.Path).ToArray(), rescanAll, progress, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the user or superseded by a newer scan.
        }
        finally
        {
            if (_scan == cancellation)
            {
                _scan = null;
                IsScanning = false;
                Refilter();
            }
        }
    }

    private void SaveFolders()
    {
        HasFolders = Folders.Count > 0;
        _services.Settings.Library.Folders = Folders.Select(f => f.Path).ToList();
        _services.NotifySettingsChanged(applyToEngine: false);
        ApplyWatch();
    }

    private void ApplyWatch()
    {
        if (WatchFolders)
        {
            _watcher.Watch(Folders.Select(f => f.Path));
        }
        else
        {
            _watcher.Stop();
            HasPendingChanges = false;
        }
    }

    /// <summary>
    /// The folders went quiet after changing. A scan started while the user is in the middle of something would
    /// reshuffle the grid under them, so a scan runs by itself only when the page is idle; otherwise it offers.
    /// </summary>
    private void OnFoldersChangedOnDisk()
    {
        if (IsScanning)
        {
            return;
        }

        if (SelectedAlbum is null && SearchText.Length == 0)
        {
            HasPendingChanges = false;
            _ = ScanAsync(rescanAll: false);
        }
        else
        {
            HasPendingChanges = true;
        }
    }

    private void Refilter()
    {
        _filtered = _services.Library.Search(SearchText);
        RebuildGroups();
        RebuildRows();

        IReadOnlyList<LibraryAlbum> all = _services.Library.Albums;
        IsLibraryEmpty = all.Count == 0;
        if (SelectedAlbum is { } selected && !all.Contains(selected.Album))
        {
            SelectedAlbum = null;
        }

        if (IsScanning)
        {
            return;
        }

        int tracks = all.Sum(a => a.Tracks.Count);
        Status = all.Count == 0
            ? HasFolders ? "No music found in the library folders" : "Add a music folder to build your library"
            : string.IsNullOrWhiteSpace(SearchText)
                ? $"{all.Count} albums  ·  {tracks} tracks"
                : $"{_filtered.Count} of {all.Count} albums";
    }

    /// <summary>Artists or genres of the albums the search left, each with what it holds.</summary>
    private void RebuildGroups()
    {
        if (!IsBrowsingGroups)
        {
            Groups = [];
            SelectedGroup = null;
            return;
        }

        bool byArtist = Browse is LibraryBrowse.Artists;
        List<LibraryGroup> groups = _filtered
            .GroupBy(album => Name(album), StringComparer.CurrentCultureIgnoreCase)
            .Select(g => new LibraryGroup(g.Key, g.Count(), g.Sum(a => a.Tracks.Count)))
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        groups.Insert(0, LibraryGroup.All(_filtered.Count));

        string? keep = SelectedGroup is { IsAll: false } previous ? previous.Name : null;
        Groups = groups;
        SelectedGroup = (keep is null ? null : groups.FirstOrDefault(g => g.Name.Equals(keep, StringComparison.CurrentCultureIgnoreCase)))
            ?? groups[0];

        string Name(LibraryAlbum album)
        {
            string value = byArtist ? album.Artist : album.Genre;
            return value.Length > 0 ? value : byArtist ? "Unknown artist" : "No genre";
        }
    }

    private void RebuildRows()
    {
        IReadOnlyList<LibraryAlbum> albums = _filtered;
        if (IsBrowsingGroups && SelectedGroup is { IsAll: false } group)
        {
            bool byArtist = Browse is LibraryBrowse.Artists;
            albums = _filtered.Where(a => Matches(byArtist ? a.Artist : a.Genre)).ToList();

            bool Matches(string value) =>
                value.Length > 0
                    ? value.Equals(group.Name, StringComparison.CurrentCultureIgnoreCase)
                    : group.Name is "Unknown artist" or "No genre";
        }

        int columns = Math.Max(1, Columns);
        var rows = new List<AlbumRowViewModel>((albums.Count + columns - 1) / columns);
        for (int i = 0; i < albums.Count; i += columns)
        {
            var row = new AlbumViewModel[Math.Min(columns, albums.Count - i)];
            for (int j = 0; j < row.Length; j++)
            {
                row[j] = GetAlbum(albums[i + j]);
            }

            rows.Add(new AlbumRowViewModel(row));
        }

        Rows = rows;
    }

    private AlbumViewModel GetAlbum(LibraryAlbum album)
    {
        if (!_albums.TryGetValue(album.Folder, out AlbumViewModel? vm) || !ReferenceEquals(vm.Album, album))
        {
            vm = new AlbumViewModel(album, this, _services.Covers);
            _albums[album.Folder] = vm;
        }

        return vm;
    }
}
