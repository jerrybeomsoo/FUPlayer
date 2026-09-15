using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using FUPlayer.App.Services;
using FUPlayer.App.ViewModels;
using FUPlayer.Core.Decoding;
using FUPlayer.Core.Playlists;

namespace FUPlayer.App;

public partial class MainWindow : Window, IDialogService
{
    public MainWindow()
    {
        InitializeComponent();
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    public async Task<IReadOnlyList<string>> PickAudioFilesAsync()
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add music files",
            AllowMultiple = true,
            FileTypeFilter =
            [
                // Every format FUPlayer knows, not only the ones playable right now: an Ogg file that the
                // picker will not even show is a puzzle, whereas one that reports a missing library is not.
                new FilePickerFileType("Audio files") { Patterns = DecoderFactory.KnownExtensions.Select(Pattern).ToArray() },
                new FilePickerFileType("Playlists") { Patterns = PlaylistFile.Extensions.Select(Pattern).ToArray() },
                FilePickerFileTypes.All,
            ],
        });
        return LocalPaths(files);
    }

    public async Task<IReadOnlyList<string>> PickFoldersAsync(string title)
    {
        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = true,
        });
        return LocalPaths(folders);
    }

    public async Task<string?> PickPlaylistAsync()
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open playlist",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Playlists") { Patterns = PlaylistFile.Extensions.Select(Pattern).ToArray() }],
        });
        return LocalPaths(files).FirstOrDefault();
    }

    public async Task<string?> PickSavePlaylistAsync(string suggestedName)
    {
        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save queue as playlist",
            SuggestedFileName = suggestedName,
            DefaultExtension = "m3u8",
            FileTypeChoices = [new FilePickerFileType("M3U8 playlist") { Patterns = ["*.m3u8"] }],
        });
        return file?.TryGetLocalPath();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (ViewModel is { } viewModel && TryGetPlatformHandle() is { } handle)
        {
            viewModel.SetWindowHandle(handle.Handle);
        }
    }

    private static string Pattern(string extension) => extension.StartsWith('.') ? "*" + extension : "*." + extension;

    private static string[] LocalPaths(IEnumerable<IStorageItem> items) =>
        items.Select(item => item.TryGetLocalPath()).OfType<string>().ToArray();

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Formats.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var items = e.DataTransfer.TryGetFiles();
        if (items is null || ViewModel is not { } viewModel)
        {
            return;
        }

        string[] paths = LocalPaths(items);
        if (paths.Length > 0)
        {
            viewModel.OpenPaths(paths);
            e.Handled = true;
        }
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        bool control = (e.KeyModifiers & KeyModifiers.Control) != 0;
        switch (e.Key)
        {
            case Key.MediaPlayPause:
            case Key.Space when !control && e.Source is not TextBox:
                viewModel.PlayPauseCommand.Execute(null);
                break;
            case Key.MediaStop:
            case Key.OemPeriod when control:
                viewModel.StopCommand.Execute(null);
                break;
            case Key.MediaNextTrack:
            case Key.Right when control:
                viewModel.NextCommand.Execute(null);
                break;
            case Key.MediaPreviousTrack:
            case Key.Left when control:
                viewModel.PreviousCommand.Execute(null);
                break;
            case Key.Up when control:
                viewModel.StepVolume(1);
                break;
            case Key.Down when control:
                viewModel.StepVolume(-1);
                break;
            case >= Key.D1 and <= Key.D7 when control:
                viewModel.NavigateCommand.Execute(viewModel.Navigation[e.Key - Key.D1].Key);
                break;
            default:
                return;
        }

        e.Handled = true;
    }
}
