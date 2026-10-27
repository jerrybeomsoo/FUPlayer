using System.Reflection;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FUPlayer.App.Services;
using FUPlayer.Core.Localization;
using FUPlayer.Update;

namespace FUPlayer.App.ViewModels;

public sealed partial class UpdateViewModel(string settingsDirectory) : ObservableObject, IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private CancellationTokenSource? _cancel;
    private string? _work, _hash;
    private bool _installing;
    private bool _disposed;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private bool _isReady;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _status = Loc.T("Check the latest stable release on GitHub. Updates are downloaded only when requested.");

    [RelayCommand]
    private async Task UpdateFuPlayer()
    {
        if (IsBusy) return;
        IsBusy = true; IsReady = false; Progress = 0;
        _cancel = new CancellationTokenSource();
        try
        {
            DiscardWork();
            Status = Loc.T("Checking GitHub releases…");
            await Task.Run(UpdateRequest.CleanupCompleted, _cancel.Token);
            var client = new ReleaseClient(_http);
            PlayerRelease release = await client.GetLatestAsync(_cancel.Token);
            string current = typeof(UpdateViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
            if (!ReleaseClient.IsNewer(release, current)) { Status = Loc.F("FUPlayer {0} is up to date.", current.Split('+')[0]); return; }
            _work = Path.Combine(UpdateRequest.CacheDirectory, Guid.NewGuid().ToString("N"));
            IsDownloading = true;
            Status = Loc.F("Downloading FUPlayer {0}…", release.Tag[1..]);
            _hash = await client.DownloadAsync(release, _work, new Progress<double>(value => Progress = value), _cancel.Token);
            IsReady = true;
            Status = Loc.F("FUPlayer {0} is verified and ready. Install and restart when you are ready to stop playback.", release.Tag[1..]);
        }
        catch (OperationCanceledException) { Status = Loc.T("Update download cancelled."); DiscardWork(); }
        catch (ObjectDisposedException) when (_disposed) { DiscardWork(); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { Status = Loc.F("Update failed: {0}", ex.Message); DiscardWork(); }
        finally { IsDownloading = false; IsBusy = false; _cancel?.Dispose(); _cancel = null; }
    }

    [RelayCommand] private void CancelUpdate() => _cancel?.Cancel();

    [RelayCommand]
    private async Task InstallUpdate()
    {
        if (IsBusy || !IsReady || _work is null || _hash is null) return;
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        IsBusy = true;
        try
        {
            Status = Loc.T("Preparing the update. FUPlayer will close and restart after its files are ready.");
            await UpdateLauncher.StartAsync(_work, _hash, settingsDirectory, Loc.Language);
            _installing = true;
            desktop.Shutdown();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        { Status = Loc.F("Update failed: {0}", ex.Message); IsReady = false; DiscardWork(); }
        finally { IsBusy = false; }
    }

    private void DiscardWork()
    {
        if (_work is not null)
            try { File.WriteAllText(Path.Combine(_work, "complete"), "Discarded update download"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        _work = null; _hash = null;
    }

    public void Dispose()
    {
        _disposed = true;
        _cancel?.Cancel(); _http.Dispose();
        if (!_installing) DiscardWork();
    }
}
