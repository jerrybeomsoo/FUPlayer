using System.Globalization;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FUPlayer.App.Services;
using FUPlayer.Core.Library;
using FUPlayer.Core.Localization;

namespace FUPlayer.App.ViewModels;

/// <summary>A sort order of the Tracks page.</summary>
public sealed record TrackSort(string Key, string Title)
{
    public override string ToString() => Title;
}

/// <summary>One track of the Tracks page.</summary>
public sealed partial class TrackRowViewModel(LibraryTrack track, TracksViewModel owner) : ObservableObject
{
    private Bitmap? _thumbnail;
    private bool _thumbnailRequested;

    public LibraryTrack Track { get; } = track;

    public string Title { get; } = string.IsNullOrWhiteSpace(track.Title) ? Path.GetFileNameWithoutExtension(track.Path) : track.Title;

    public string Artist { get; } = track.Artist;

    public string Album { get; } = track.Album;

    public string DurationText { get; } = track.Duration.TotalHours >= 1 ? track.Duration.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : track.Duration.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    /// <summary>The rating stored in the file, as stars.</summary>
    public string RatingText { get; } = track.Rating > 0 ? new string('★', Math.Min(track.Rating, 5)) : string.Empty;

    /// <summary>Row cover; decoding starts when a row first scrolls into view and binds to it, as in the queue.</summary>
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

    /// <summary>Every searchable field in one string, so a term is one search.</summary>
    internal string Haystack { get; } = string.Join('\n', track.Title, track.Artist, track.Album, track.AlbumArtist, track.Genre, Path.GetFileName(track.Path));

    [RelayCommand]
    private void PlayOnly() => owner.PlayOnly(this);

    [RelayCommand]
    private void PlayFromHere() => owner.PlayFromHere(this);

    [RelayCommand]
    private void PlayNext() => owner.Queue.PlayNext([Track.Path]);

    [RelayCommand]
    private void Enqueue() => owner.Queue.Enqueue([Track.Path]);

    [RelayCommand]
    private void ShowInFolder() => FileManager.Reveal(Track.Path);

    private async Task LoadThumbnailAsync()
    {
        // The queue's row width, so the two share cached thumbnails.
        Bitmap? bitmap = await owner.Covers.GetTrackCoverAsync(Track.Path, 96);
        if (bitmap is not null)
        {
            _thumbnail = bitmap;
            OnPropertyChanged(nameof(Thumbnail));
            OnPropertyChanged(nameof(HasThumbnail));
        }
    }
}

/// <summary>
/// Single tracks across the whole library, where the Library page shows albums. Every term must appear in the title,
/// artist, album, album artist, genre or file name; case, kana type and width are ignored.
/// </summary>
public sealed partial class TracksViewModel : ObservableObject
{
    private static readonly CompareInfo Compare = CultureInfo.CurrentCulture.CompareInfo;
    private const CompareOptions Loose = CompareOptions.IgnoreCase | CompareOptions.IgnoreKanaType | CompareOptions.IgnoreWidth;

    private readonly PlayerServices _services;
    private readonly DispatcherTimer _searchTimer;
    private List<TrackRowViewModel> _all = [];

    public TracksViewModel(PlayerServices services, QueueViewModel queue)
    {
        _services = services;
        Queue = queue;
        SortChoices =
        [
            new TrackSort("title", Loc.T("Title")),
            new TrackSort("artist", Loc.T("Artist")),
            new TrackSort("album", Loc.T("Album")),
            new TrackSort("rating", Loc.T("Rating")),
            new TrackSort("recent", Loc.T("Recently changed")),
        ];
        _selectedSort = SortChoices[0];

        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            Refilter();
        };

        services.Library.Changed += (_, _) => Dispatcher.UIThread.Post(Rebuild);
        Rebuild();
    }

    public QueueViewModel Queue { get; }

    public CoverArtCache Covers => _services.Covers;

    public IReadOnlyList<TrackSort> SortChoices { get; }

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private TrackSort _selectedSort;

    [ObservableProperty]
    private IReadOnlyList<TrackRowViewModel> _rows = [];

    [ObservableProperty]
    private TrackRowViewModel? _selectedRow;

    [ObservableProperty]
    private string _summary = string.Empty;

    partial void OnSearchTextChanged(string value)
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    partial void OnSelectedSortChanged(TrackSort value) => Refilter();

    /// <summary>Plays this one track: the queue becomes just this track.</summary>
    public void PlayOnly(TrackRowViewModel row) => Queue.ReplaceAndPlay([row.Track.Path], 0);

    /// <summary>Plays the search result from this track on.</summary>
    public void PlayFromHere(TrackRowViewModel row)
    {
        IReadOnlyList<TrackRowViewModel> rows = Rows;
        int index = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            if (ReferenceEquals(rows[i], row))
            {
                index = i;
                break;
            }
        }

        Queue.ReplaceAndPlay(rows.Select(r => r.Track.Path).ToList(), index);
    }

    private void Rebuild()
    {
        _all = _services.Library.Albums.SelectMany(a => a.Tracks).Select(t => new TrackRowViewModel(t, this)).ToList();
        Refilter();
    }

    private void Refilter()
    {
        string[] terms = SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        IEnumerable<TrackRowViewModel> rows = terms.Length == 0
            ? _all
            : _all.Where(r => terms.All(t => Compare.IndexOf(r.Haystack, t, Loose) >= 0));
        StringComparer text = StringComparer.CurrentCultureIgnoreCase;
        rows = SelectedSort.Key switch
        {
            "artist" => rows.OrderBy(r => r.Artist, text).ThenBy(r => r.Album, text).ThenBy(r => r.Track.DiscNumber).ThenBy(r => r.Track.TrackNumber),
            "album" => rows.OrderBy(r => r.Album, text).ThenBy(r => r.Track.DiscNumber).ThenBy(r => r.Track.TrackNumber),
            "rating" => rows.OrderByDescending(r => r.Track.Rating).ThenBy(r => r.Title, text),
            "recent" => rows.OrderByDescending(r => r.Track.ModifiedUtc),
            _ => rows.OrderBy(r => r.Title, text),
        };
        Rows = rows.ToList();
        Summary = terms.Length == 0
            ? Loc.F("{0} tracks", _all.Count.ToString("N0", CultureInfo.CurrentCulture))
            : Loc.F("{0} of {1} tracks", Rows.Count.ToString("N0", CultureInfo.CurrentCulture), _all.Count.ToString("N0", CultureInfo.CurrentCulture));
    }
}
