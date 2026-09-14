using System.Diagnostics;

namespace FUPlayer.App.Services;

/// <summary>Hands a path to the system file manager.</summary>
public static class FileManager
{
    /// <summary>Opens the containing folder, with the file selected where the platform can do that.</summary>
    public static void Reveal(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows() && File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
                return;
            }

            string? folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            if (folder is not null && Directory.Exists(folder))
            {
                Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // Nothing sensible to do; the file may have been moved.
        }
    }
}
