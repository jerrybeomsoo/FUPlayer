using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using FUPlayer.App.ViewModels;
using FUPlayer.Core.Localization;

namespace FUPlayer.App.Views;

public partial class StreamingView : UserControl
{
    public StreamingView() => InitializeComponent();

    private async void BrowseAddon(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not StreamingViewModel model || TopLevel.GetTopLevel(this) is not { } top) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("Choose the signed streaming add-on"), AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(Loc.T("Firefox add-on")) { Patterns = ["*.xpi"] }],
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) model.InstallAddon(path);
    }

    private async void BrowseFirefox(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not StreamingViewModel model || TopLevel.GetTopLevel(this) is not { } top) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("Choose Firefox"), AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Firefox") { Patterns = ["firefox.exe"] }],
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) model.Executable = path;
    }
}
