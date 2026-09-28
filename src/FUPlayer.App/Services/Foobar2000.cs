using System.Diagnostics;
using System.Runtime.Versioning;
using FUPlayer.Core.Upnp;
using Microsoft.Win32;

namespace FUPlayer.App.Services;

/// <summary>What the UPnP input page knows of foobar2000 on this computer.</summary>
/// <param name="Executable">foobar2000.exe, or null when it is not installed where it can be found.</param>
/// <param name="ConfigPath">Its UPnP output's list of renderers, or null when that file has not been written yet.</param>
/// <param name="IsConfigured">Whether the list describes this player.</param>
/// <param name="IsRunning">Whether foobar2000 is running, which decides whether a change to the list needs a restart.</param>
public sealed record Foobar2000Status(string? Executable, string? ConfigPath, bool IsConfigured, bool IsRunning)
{
    public bool IsInstalled => Executable is not null;
}

/// <summary>
/// Finds foobar2000 and its UPnP output's list of renderers, and adds this player to it: see
/// <see cref="Foobar2000Config"/> for what the entry changes.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Foobar2000
{
    private const string ProcessName = "foobar2000";

    public static Foobar2000Status Find()
    {
        string? executable = FindExecutable(out bool running);
        string? config = FindConfig(executable);
        bool configured = false;
        if (config is not null)
        {
            try
            {
                configured = Foobar2000Config.HasEntry(File.ReadAllText(config));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                config = null;
            }
        }

        return new Foobar2000Status(executable, config, configured, running);
    }

    /// <summary>Adds this player to the list. Throws when the file cannot be written.</summary>
    public static void AddEntry(string configPath)
    {
        string text = File.ReadAllText(configPath);
        string updated = Foobar2000Config.WithEntry(text);
        if (!ReferenceEquals(updated, text))
        {
            // Beside it first, then over it, so foobar2000 never finds half a file.
            string temporary = configPath + ".fuplayer";
            File.WriteAllText(temporary, updated);
            File.Move(temporary, configPath, overwrite: true);
        }
    }

    /// <summary>Starts foobar2000, or brings the running one forward.</summary>
    public static void Open(string executable) =>
        Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(executable) });

    /// <summary>The running copy first, since that is the one in use, then where installers put it.</summary>
    private static string? FindExecutable(out bool running)
    {
        running = false;
        foreach (Process process in Process.GetProcessesByName(ProcessName))
        {
            using (process)
            {
                running = true;
                if (AppIdentity.ExecutablePath(process.Id) is { } path)
                {
                    return path;
                }
            }
        }

        var candidates = new List<string?>
        {
            Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\foobar2000.exe", string.Empty, null) as string,
            Registry.GetValue(@"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\foobar2000.exe", string.Empty, null) as string,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "foobar2000", "foobar2000.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "foobar2000", "foobar2000.exe"),
        };

        return candidates.Select(c => c?.Trim('"')).FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// The list lives in the profile folder: beside the program in a portable installation, and otherwise in the
    /// roaming application data, foobar2000-v2 for version 2 and foobar2000 before it.
    /// </summary>
    private static string? FindConfig(string? executable)
    {
        var folders = new List<string>();
        if (executable is not null && Path.GetDirectoryName(executable) is { } installation
            && File.Exists(Path.Combine(installation, "portable_mode_enabled")))
        {
            folders.Add(Path.Combine(installation, "profile"));
            folders.Add(installation);
        }

        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        folders.Add(Path.Combine(roaming, "foobar2000-v2"));
        folders.Add(Path.Combine(roaming, "foobar2000"));
        return folders.Select(f => Path.Combine(f, Foobar2000Config.FileName)).FirstOrDefault(File.Exists);
    }
}
