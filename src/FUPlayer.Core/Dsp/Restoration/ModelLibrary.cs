namespace FUPlayer.Core.Dsp.Restoration;

/// <summary>
/// Where the neural models are kept: upscalers, and restorers, whose file names end in "restorer.onnx".
///
/// A release carries one of each in the "models" folder beside the program. A model is weights fitted to music,
/// and which music decides what it writes, so any other can take its place: train one from your own files with the
/// scripts in training/, or put somebody else's in one of these folders. The newest file of a kind is the one used
/// unless the settings name another.
/// </summary>
public static class ModelLibrary
{
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
    /// makes Path.Combine hand back "FUPlayer\models", a relative path that then resolves against
    /// whatever the working directory happens to be.
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

    /// <summary>Creates the folder if it is not there, and returns it.</summary>
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
    /// A "models" folder beside the program comes first. A program started inside an application
    /// container has its %APPDATA% redirected into the container, so a model written there by one program
    /// is invisible to another started normally; both can see the folder next to the executable.
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

    /// <summary>Files matching a pattern across every search folder, newest first, duplicates by name removed.</summary>
    private static List<string> Files(string pattern)
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

    private static readonly Lock UpscalerGate = new();
    private static (string Path, DateTime Written, NeuralUpscalerModel Model)? _upscaler;
    private static readonly Lock RestorerGate = new();
    private static (string Path, DateTime Written, NeuralRestorerModel Model)? _restorer;

    /// <summary>Whether a model file is a restorer, by its name.</summary>
    public static bool IsRestorerFile(string path) =>
        Path.GetFileName(path).EndsWith(NeuralRestorerModel.Suffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Every neural upscaler in any of the search folders, newest first.</summary>
    public static IReadOnlyList<string> ListUpscalers() =>
        Files("*" + NeuralUpscalerModel.Extension).Where(path => !IsRestorerFile(path)).ToList();

    /// <summary>Every neural restorer in any of the search folders, newest first.</summary>
    public static IReadOnlyList<string> ListRestorers() => Files("*" + NeuralRestorerModel.Suffix);

    /// <summary>
    /// Opens the neural upscaler, or returns null with the reason. The model is opened once and kept: it is
    /// 40 MB of weights, the pipeline is rebuilt on every settings change and every change of format, and an
    /// inference session is safe to share between channels and between pipelines. A file rewritten on disk is
    /// noticed by its time stamp and opened again.
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
    /// Opens the neural restorer, or returns null with the reason. Opened once and kept, like the upscaler, and shared
    /// by every pipeline that plays with it.
    /// </summary>
    public static NeuralRestorerModel? TryLoadRestorer(string? path, out string? failure)
    {
        failure = null;
        path ??= ListRestorers().FirstOrDefault();
        if (path is null)
        {
            failure = $"no *{NeuralRestorerModel.Suffix} model in {string.Join(" or ", SearchDirectories())}";
            return null;
        }

        lock (RestorerGate)
        {
            try
            {
                DateTime written = File.GetLastWriteTimeUtc(path);
                if (_restorer is { } cached && cached.Path == path && cached.Written == written)
                {
                    return cached.Model;
                }

                // One thread: the network's layers are small enough that handing them between threads costs more
                // than it saves, and the rest of the chain wants the cores.
                NeuralRestorerModel model = NeuralRestorerModel.Load(path, threads: 1);
                _restorer = (path, written, model);
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
    /// What the neural restorer would run with, in one line, or why it has nothing: the scores the export wrote beside
    /// it, measured on songs kept out of training, before and after.
    /// </summary>
    public static string DescribeRestorer()
    {
        string? path = ListRestorers().FirstOrDefault();
        if (path is null)
        {
            return $"No model installed. Place a *{NeuralRestorerModel.Suffix} model and its .json in "
                + $"{string.Join(" or ", SearchDirectories())}; see training/neural-restorer.";
        }

        string name = Path.GetFileName(path);
        try
        {
            using var metadata = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(path, ".json")));
            System.Text.Json.JsonElement root = metadata.RootElement;
            double parameters = root.TryGetProperty("parameters", out var p) ? p.GetDouble() : 0.0;
            long steps = root.TryGetProperty("step", out var s) ? s.GetInt64() : 0;
            string text = $"{name}: {parameters / 1e6:0.#} M parameters, {steps:N0} steps.";
            if (root.TryGetProperty("scores", out var scores)
                && Pair(scores, "in_above16k_db", "out_above16k_db") is { } level
                && Pair(scores, "in_12k-nyq", "out_12k-nyq") is { } top
                && Pair(scores, "in_side_db", "out_side_db") is { } side)
            {
                text += $" Held out, coded against lossless: level above 16 kHz {level.Before:+0.0;−0.0} → {level.After:+0.0;−0.0} dB, "
                    + $"LSD above 12 kHz {top.Before:0.0} → {top.After:0.0} dB, side channel {side.Before:0.0} → {side.After:0.0} dB.";
            }

            return text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return $"{name}: no description file beside it.";
        }

        static (double Before, double After)? Pair(System.Text.Json.JsonElement scores, string before, string after) =>
            scores.TryGetProperty(before, out var b) && scores.TryGetProperty(after, out var a) ? (b.GetDouble(), a.GetDouble()) : null;
    }

    /// <summary>
    /// What the neural upscaler would run with, in one line, or why it has nothing. Read from the folders every
    /// time it is asked for. The scores come from the description the export writes beside the network:
    /// log-spectral distance to the master on songs kept out of training, before and after.
    /// </summary>
    public static string DescribeUpscaler()
    {
        string? path = ListUpscalers().FirstOrDefault();
        if (path is null)
        {
            return $"No model installed. Place a {NeuralUpscalerModel.Extension} model and its .json in "
                + $"{string.Join(" or ", SearchDirectories())}; see training/neural-upscaler.";
        }

        string name = Path.GetFileName(path);
        try
        {
            using var metadata = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(path, ".json")));
            System.Text.Json.JsonElement root = metadata.RootElement;
            double parameters = root.TryGetProperty("parameters", out var p) ? p.GetDouble() : 0.0;
            long steps = root.TryGetProperty("step", out var s) ? s.GetInt64() : 0;
            bool conditioned = root.TryGetProperty("conditioned", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.True;
            string text = $"{name}: {parameters / 1e6:0.#} M parameters, {steps:N0} steps"
                + (conditioned ? ", lossy/lossless conditioned" : string.Empty) + ".";
            if (root.TryGetProperty("scores", out var scores)
                && Score(scores, "16-22k") is { } middle && Score(scores, "22k-nyq") is { } top)
            {
                text += $" Held-out LSD: 16–22 kHz {middle.Before:0.0} → {middle.After:0.0} dB, >22 kHz {top.Before:0.0} → {top.After:0.0} dB.";
            }

            return text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return $"{name}: no description file beside it.";
        }

        static (double Before, double After)? Score(System.Text.Json.JsonElement scores, string band) =>
            scores.TryGetProperty("in_" + band, out var before) && scores.TryGetProperty("out_" + band, out var after)
                ? (before.GetDouble(), after.GetDouble())
                : null;
    }
}
