namespace FUPlayer.Core.Dsp.Restoration;

/// <summary>
/// Where trained models are kept.
///
/// No model ships with the player. A model is weights fitted to music, and which music decides what
/// the prediction sounds like, so the choice belongs to whoever is listening. Train one from your own
/// lossless files with the command line tool, or drop somebody else's file in this folder.
/// </summary>
public static class ModelLibrary
{
    public const string Extension = ".fumodel.json";

    private static string? _directory;

    /// <summary>The models folder. Set once at start-up to sit beside the settings file.</summary>
    public static string Directory
    {
        get => _directory ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FUPlayer", "models");
        set => _directory = value;
    }

    /// <summary>Every model file, newest first. An unreadable folder yields nothing rather than throwing.</summary>
    public static IReadOnlyList<string> List()
    {
        try
        {
            if (!System.IO.Directory.Exists(Directory))
            {
                return [];
            }

            return [.. System.IO.Directory
                .EnumerateFiles(Directory, "*" + Extension)
                .OrderByDescending(File.GetLastWriteTimeUtc)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The most recently written model, or null when there is none.</summary>
    public static string? Newest() => List().FirstOrDefault();

    /// <summary>Loads a model, or returns null with the reason when it cannot be used.</summary>
    public static HighBandModel? TryLoad(string? path, out string? failure)
    {
        failure = null;
        path ??= Newest();
        if (path is null)
        {
            failure = "No model is installed.";
            return null;
        }

        try
        {
            return HighBandModel.Load(path);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException
            or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            failure = $"{Path.GetFileName(path)}: {ex.Message}";
            return null;
        }
    }
}
