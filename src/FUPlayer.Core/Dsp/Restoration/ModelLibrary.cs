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

    /// <summary>Networks, which are read by a different loader.</summary>
    public const string NeuralExtension = ".funet.json";

    private static string? _directory;

    /// <summary>The models folder, always an absolute path. Set it to keep models somewhere else.</summary>
    public static string Directory
    {
        get => _directory ??= Resolve();
        set => _directory = value;
    }

    /// <summary>
    /// Beside the settings file, and absolute whatever the shell says.
    ///
    /// GetFolderPath returns an empty string when the shell cannot answer, and an empty first part
    /// makes Path.Combine hand back "FUPlayer\models", a relative path. Everything then reads and
    /// writes next to whatever the working directory happens to be, which for a program started from
    /// its own folder is that folder and for one started from a shortcut is somewhere else entirely.
    /// A model written by the command line tool would sit in one place and the player would look in
    /// another, and both would be sure they had used the right path.
    /// </summary>
    private static string Resolve()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(appData))
        {
            appData = Environment.GetEnvironmentVariable("APPDATA") ?? string.Empty;
        }

        if (string.IsNullOrEmpty(appData))
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            appData = string.IsNullOrEmpty(home) ? AppContext.BaseDirectory : Path.Combine(home, ".config");
        }

        return Path.GetFullPath(Path.Combine(appData, "FUPlayer", "models"));
    }

    /// <summary>
    /// Creates the folder if it is not there, and returns it.
    ///
    /// The player calls this at start-up so that somebody told to put a model in it has somewhere to
    /// put it. Being told to copy a file into a folder that does not exist is a poor instruction.
    /// </summary>
    public static string EnsureDirectory()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Read-only or unreachable: listing it will simply find nothing.
        }

        return Directory;
    }

    /// <summary>
    /// Everywhere a model may be kept, in the order they are searched.
    ///
    /// A "models" folder beside the program comes first, so that a copy of FUPlayer can carry its own
    /// and a model can be put where the program is rather than where its settings are. That is not
    /// only tidiness. A program started inside an application container, which is how a packaged or
    /// store-installed program runs, has its %APPDATA% redirected into the container, so a model
    /// written there by one program is invisible to another that was started normally. Both can see
    /// the folder next to the executable.
    /// </summary>
    public static IEnumerable<string> SearchDirectories()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "models");

        string? fromEnvironment = Environment.GetEnvironmentVariable("FUPLAYER_MODELS_PATH");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            yield return fromEnvironment;
        }

        yield return Directory;
    }

    /// <summary>Every model file, newest first. An unreadable folder yields nothing rather than throwing.</summary>
    public static IReadOnlyList<string> List() =>
        [.. Files("*" + Extension).Where(file => !file.EndsWith(NeuralExtension, StringComparison.OrdinalIgnoreCase))];

    /// <summary>Files matching a pattern across every search folder, newest first, duplicates by name removed.</summary>
    private static IEnumerable<string> Files(string pattern)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string directory in SearchDirectories())
        {
            try
            {
                if (!System.IO.Directory.Exists(directory))
                {
                    continue;
                }

                foreach (string file in System.IO.Directory.EnumerateFiles(directory, pattern))
                {
                    if (seen.Add(Path.GetFileName(file)))
                    {
                        found.Add(file);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable folder: the others may still have something.
            }
        }

        found.Sort((a, b) => File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)));
        return found;
    }

    /// <summary>The most recently written model, or null when there is none.</summary>
    public static string? Newest() => List().FirstOrDefault();

    private static readonly Lock UpscalerGate = new();
    private static (string Path, DateTime Written, NeuralUpscalerModel Model)? _upscaler;

    /// <summary>Every neural upscaler in any of the search folders, newest first.</summary>
    public static IReadOnlyList<string> ListUpscalers() => [.. Files("*" + NeuralUpscalerModel.Extension)];

    /// <summary>
    /// Opens the neural upscaler, or returns null with the reason. The model is opened once and kept:
    /// it is 40 MB of weights, the pipeline is rebuilt on every settings change and every change of
    /// format, and an inference session is safe to share between channels and between pipelines. A file
    /// rewritten on disk is noticed by its time stamp and opened again.
    /// </summary>
    public static NeuralUpscalerModel? TryLoadUpscaler(string? path, out string? failure)
    {
        failure = null;
        path ??= ListUpscalers().FirstOrDefault();
        if (path is null)
        {
            failure = $"no {NeuralUpscalerModel.Extension} model in {string.Join(" or ", SearchDirectories())}";
            return null;
        }

        lock (UpscalerGate)
        {
            try
            {
                DateTime written = File.GetLastWriteTimeUtc(path);
                if (_upscaler is { } cached && cached.Path == path && cached.Written == written)
                {
                    return cached.Model;
                }

                NeuralUpscalerModel model = NeuralUpscalerModel.Load(path, Math.Max(1, Environment.ProcessorCount / 4));
                _upscaler = (path, written, model);
                return model;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                or Microsoft.ML.OnnxRuntime.OnnxRuntimeException or DllNotFoundException or TypeInitializationException)
            {
                failure = $"{Path.GetFileName(path)}: {ex.Message}";
                return null;
            }
        }
    }

    /// <summary>
    /// What the neural upscaler would run with, or why it has nothing, read from the folders every time
    /// it is asked for. The numbers are the ones the export writes beside the network: log-spectral
    /// distance to the high-resolution master on songs kept out of training, before and after, by band.
    /// </summary>
    public static string DescribeUpscaler()
    {
        string? path = ListUpscalers().FirstOrDefault();
        if (path is null)
        {
            return $"Nothing installed. Looked in {string.Join(" and ", SearchDirectories())} for a {NeuralUpscalerModel.Extension} file. "
                + "None ships with the player: a network is fitted to somebody's music. docs/restoration.md describes training one "
                + "from your own high-resolution files. Without one this switch interpolates to twice the rate and nothing more.";
        }

        string name = Path.GetFileName(path);
        try
        {
            using var metadata = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(path, ".json")));
            System.Text.Json.JsonElement root = metadata.RootElement;
            double parameters = root.TryGetProperty("parameters", out var p) ? p.GetDouble() : 0.0;
            long steps = root.TryGetProperty("step", out var s) ? s.GetInt64() : 0;
            string text = $"Using {name}: {parameters / 1e6:0.#} million parameters, {steps:N0} training steps.";
            if (root.TryGetProperty("scores", out var scores)
                && Score(scores, "16-22k") is { } middle && Score(scores, "22k-nyq") is { } top)
            {
                text += $" On songs it never trained on, its distance from the master goes from {middle.Before:0.0} to {middle.After:0.0} dB "
                    + $"between 16 and 22 kHz, and from {top.Before:0.0} to {top.After:0.0} dB above 22 kHz.";
            }

            return text + $" Loaded from {path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return $"Using {name}. There is no readable description beside it, so how it was trained is not known. Loaded from {path}";
        }

        static (double Before, double After)? Score(System.Text.Json.JsonElement scores, string band) =>
            scores.TryGetProperty("in_" + band, out var before) && scores.TryGetProperty("out_" + band, out var after)
                ? (before.GetDouble(), after.GetDouble())
                : null;
    }

    /// <summary>Every network in any of the search folders, newest first.</summary>
    public static IReadOnlyList<string> ListNetworks() => [.. Files("*" + NeuralExtension)];

    /// <summary>
    /// Where the two kinds of network part company, in hertz.
    ///
    /// Both are the same file format and the same arithmetic; what differs is the band they were
    /// fitted over. A codec repair stops at the source's own Nyquist rate, around 22 kHz, because
    /// that is where a coded 44.1 kHz recording ends. One fitted to invent the band above that rate
    /// runs to 48 kHz. Handing either to the other's job produces confident nonsense, so they are
    /// told apart by their layout rather than by their filename.
    /// </summary>
    public const double ExtensionHighHz = 30_000.0;

    /// <summary>True when a network describes bands above a 44.1 kHz recording's Nyquist rate.</summary>
    public static bool IsExtension(NeuralRepairModel model) => model.HighHz > ExtensionHighHz;

    /// <summary>The newest network of one kind or the other, or null when there is none.</summary>
    public static string? NewestNetwork(bool extension)
    {
        foreach (string file in ListNetworks())
        {
            try
            {
                if (IsExtension(NeuralRepairModel.Load(file)) == extension)
                {
                    return file;
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException
                or UnauthorizedAccessException or System.Text.Json.JsonException or FormatException)
            {
                // Unreadable: the next one may be fine.
            }
        }

        return null;
    }

    /// <summary>Loads a network, or returns null with the reason when it cannot be used.</summary>
    public static NeuralRepairModel? TryLoadNetwork(string? path, out string? failure, bool extension = false)
    {
        failure = null;
        path ??= NewestNetwork(extension);
        if (path is null)
        {
            failure = extension
                ? "No network fitted above 30 kHz is installed."
                : "No network is installed.";
            return null;
        }

        try
        {
            NeuralRepairModel model = NeuralRepairModel.Load(path);
            if (IsExtension(model) != extension)
            {
                failure = IsExtension(model)
                    ? $"{Path.GetFileName(path)} describes bands to {model.HighHz / 1000.0:0.#} kHz; it is a model of what sits above a recording's own band, not of what a codec removed."
                    : $"{Path.GetFileName(path)} stops at {model.HighHz / 1000.0:0.#} kHz; it has nothing to say about the band above that.";
                return null;
            }

            return model;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException
            or UnauthorizedAccessException or System.Text.Json.JsonException or FormatException)
        {
            failure = $"{Path.GetFileName(path)}: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// What the prediction has to work with, in one sentence, or why it has nothing.
    ///
    /// This lives here rather than in the player so that the command line tool and the interface
    /// cannot disagree about what is installed, and so that the answer is read from the folder every
    /// time it is asked for instead of once when a window was built.
    /// </summary>
    public static string DescribeInstalled()
    {
        // A network does both jobs and is preferred when one is installed.
        string? network = NewestNetwork(extension: false);
        string? above = NewestNetwork(extension: true);
        string extra = above is null
            ? string.Empty
            : $" A second network, {Path.GetFileName(above)}, covers the band above a recording's own Nyquist rate.";

        if (network is not null)
        {
            NeuralRepairModel? trained = TryLoadNetwork(network, out string? broken);
            if (trained is null)
            {
                return $"The installed network could not be read: {broken}";
            }

            string shape = trained.IsConstantCurve
                ? $"one fixed correction per band over {trained.Bands} bands"
                : $"a {string.Join("-", trained.Layers)} network over {trained.Bands} bands";

            return $"Using {Path.GetFileName(network)}: {shape}, fitted to {trained.FramesSeen:N0} frames of coded music"
                + (trained.TrainedOn is null ? string.Empty : $" from {trained.TrainedOn}")
                + $". It sets both the rebuilt levels and the correction below the cutoff. Loaded from {network}"
                + extra;
        }

        string? path = Newest();
        if (path is null)
        {
            return $"Nothing installed. Looked in {string.Join(" and ", SearchDirectories())}. Train one with "
                + "'fuplayer-cli dataset' and 'train-repair', or copy a .funet.json file into one of those. "
                + "Without one the rebuilt band follows a fixed slope and the artefact damping is a fixed rate limit.";
        }

        HighBandModel? model = TryLoad(path, out string? failure);
        return model is null
            ? $"The installed model could not be read: {failure}"
            : $"Using {Path.GetFileName(path)}: a linear fit, cutoff {model.CutoffHz / 1000.0:0.#} kHz, predicting "
                + $"to {model.TopHz / 1000.0:0.#} kHz, from {model.FramesSeen:N0} frames. It sets the rebuilt levels only.";
    }

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
