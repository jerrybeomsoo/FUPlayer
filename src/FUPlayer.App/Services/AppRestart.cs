using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace FUPlayer.App.Services;

/// <summary>Starts the player again, for a change that only takes effect when the interface is built.</summary>
internal static class AppRestart
{
    /// <summary>
    /// Starts a new player that waits until this one is gone, then closes this one. The new one keeps the settings
    /// folder this one was given and opens on the Settings page; files given on the command line are already in
    /// the queue it restores, and a language given for one run gives way to the setting.
    /// </summary>
    public static bool Restart()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || Environment.ProcessPath is not { } program)
        {
            return false;
        }

        var start = new ProcessStartInfo(program) { UseShellExecute = false, WorkingDirectory = Environment.CurrentDirectory };

        // Started as "dotnet FUPlayer.dll", the player is an argument of the runtime.
        if (Path.GetFileNameWithoutExtension(program).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            && Assembly.GetEntryAssembly()?.Location is { Length: > 0 } entry)
        {
            start.ArgumentList.Add(entry);
        }

        string[] args = desktop.Args ?? [];
        int settings = Array.FindIndex(args, a => a.Equals("--settings-dir", StringComparison.OrdinalIgnoreCase));
        if (settings >= 0 && settings + 1 < args.Length)
        {
            start.ArgumentList.Add("--settings-dir");
            start.ArgumentList.Add(args[settings + 1]);
        }

        start.ArgumentList.Add("--page");
        start.ArgumentList.Add("Settings");
        start.ArgumentList.Add("--wait-for");
        start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

        try
        {
            Process.Start(start)?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }

        desktop.Shutdown();
        return true;
    }

    /// <summary>
    /// For the player a restart started: waits for the one that started it to finish closing, since that one holds
    /// the settings, the output device and the renderer's port until it has.
    /// </summary>
    public static void WaitForPredecessor(string[] args)
    {
        int index = Array.FindIndex(args, a => a.Equals("--wait-for", StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length || !int.TryParse(args[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int id))
        {
            return;
        }

        try
        {
            using Process predecessor = Process.GetProcessById(id);
            predecessor.WaitForExit(TimeSpan.FromSeconds(20));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }
}
