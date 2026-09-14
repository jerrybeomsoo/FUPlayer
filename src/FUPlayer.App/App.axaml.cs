using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FUPlayer.App.Services;
using FUPlayer.App.ViewModels;

namespace FUPlayer.App;

public partial class App : Application
{
    private PlayerServices? _services;
    private MainViewModel? _viewModel;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            string[] args = desktop.Args ?? [];
            _services = new PlayerServices(OptionValue(args, "--settings-dir"));
            _services.RestoreQueue();

            var window = new MainWindow
            {
                Width = Math.Max(1100, _services.Settings.Ui.WindowWidth),
                Height = Math.Max(700, _services.Settings.Ui.WindowHeight),
            };
            _viewModel = new MainViewModel(_services, window);
            window.DataContext = _viewModel;
            window.Closing += (_, _) => RememberWindowSize(window);
            desktop.MainWindow = window;
            desktop.Exit += (_, _) => ShutDown();

            if (OptionValue(args, "--page") is { } page)
            {
                _viewModel.NavigateCommand.Execute(page);
            }

            string[] paths = PositionalArguments(args).Where(p => File.Exists(p) || Directory.Exists(p)).ToArray();
            if (paths.Length > 0)
            {
                _viewModel.OpenPaths(paths);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Value following a "--name value" option.</summary>
    private static string? OptionValue(string[] args, string name)
    {
        int index = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    /// <summary>Arguments that are not options (files and folders to play).</summary>
    private static IEnumerable<string> PositionalArguments(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                i++;
                continue;
            }

            yield return args[i];
        }
    }

    private void RememberWindowSize(Window window)
    {
        if (_services is not null && window.WindowState == WindowState.Normal)
        {
            _services.Settings.Ui.WindowWidth = window.ClientSize.Width;
            _services.Settings.Ui.WindowHeight = window.ClientSize.Height;
        }
    }

    private void ShutDown()
    {
        _viewModel?.Dispose();
        _services?.Dispose();
    }
}
