using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using FUPlayer.Core.Localization;

namespace FUPlayer.Core.Decoding.FFmpeg;

/// <summary>A step of <see cref="FFmpegInstaller"/>, for a progress display.</summary>
/// <param name="Stage">What is happening, in a few words.</param>
/// <param name="Fraction">How far the step has got, from 0 to 1, or null when that cannot be known.</param>
/// <param name="Line">The latest line from the tools, or null.</param>
public sealed record FFmpegInstallProgress(string Stage, double? Fraction = null, string? Line = null);

/// <summary>Where and how <see cref="FFmpegInstaller"/> works.</summary>
public sealed class FFmpegInstallOptions
{
    /// <summary>Where the finished libraries go. The loader looks in the default one.</summary>
    public string InstallDirectory { get; init; } = FFmpegLibrary.UserDirectory;

    /// <summary>The folder for the downloads, the build and a private MSYS2; null picks one.</summary>
    public string? ToolsRoot { get; init; }

    /// <summary>An MSYS2 installation to build with; null finds one, or sets one up in the tools folder.</summary>
    public string? Msys2Root { get; init; }

    /// <summary>Set up a private MSYS2 in the tools folder even when one is installed.</summary>
    public bool PrivateMsys2 { get; init; }

    /// <summary>Compilers run at once.</summary>
    public int Jobs { get; init; } = Environment.ProcessorCount;

    /// <summary>Keep the unpacked source and the objects after a build that worked.</summary>
    public bool KeepBuildFiles { get; init; }
}

/// <summary>
/// Downloads the FFmpeg source, builds the LGPL libraries the player uses, and loads them, for a player that came
/// without them. On Windows the compiler comes from MSYS2: the installed copy when there is one, or a private copy
/// set up in the tools folder. The build is the repository's own build-ffmpeg-lgpl.sh, so what the player builds for
/// itself is what a release would ship: FFmpeg 9.0.1 as published, configured LGPL v2.1 or later only.
/// </summary>
/// <remarks>
/// Both downloads are pinned by SHA-256, the source to the ffmpeg.org release and MSYS2 to its dated base archive.
/// Nothing needs administrator rights: MSYS2 unpacks into the user's own folder.
/// </remarks>
public sealed partial class FFmpegInstaller
{
    public const string FFmpegVersion = "9.0.1";

    private const string SourceFileName = "ffmpeg-" + FFmpegVersion + ".tar.xz";
    private const string SourceSha256 = "cf38e0e28c7e5605942c4a77755349b0145804a397af37eb1fb4c77cb237f635";
    private const string Msys2FileName = "msys2-base-x86_64-20260611.sfx.exe";
    private const string Msys2Sha256 = "c105946e64e08f099ac0e4647461ce762b95333ad211777666476a9a41451d65";
    private const string ReadyMarker = ".fuplayer-ready";
    private const string ScriptResource = "FUPlayer.Core.build-ffmpeg-lgpl.sh";

    /// <summary>The objects this configuration compiles, for the progress bar only.</summary>
    private const int ExpectedObjects = 431;

    private static readonly string[] SourceUrls = ["https://ffmpeg.org/releases/" + SourceFileName];

    private static readonly string[] Msys2Urls =
    [
        "https://repo.msys2.org/distrib/x86_64/" + Msys2FileName,
        "https://github.com/msys2/msys2-installer/releases/download/2026-06-11/" + Msys2FileName,
    ];

    /// <summary>What the build needs from MSYS2's UCRT64 environment.</summary>
    private static readonly string[] Packages = ["mingw-w64-ucrt-x86_64-gcc", "make", "nasm", "diffutils", "pkgconf"];

    private static readonly HttpClient Http = CreateClient();

    private readonly FFmpegInstallOptions _options;
    private readonly IProgress<FFmpegInstallProgress>? _progress;
    private readonly Queue<string> _lastLines = new();
    private readonly Lock _logGate = new();
    private StreamWriter? _log;
    private string _stage = string.Empty;

    public FFmpegInstaller(FFmpegInstallOptions? options = null, IProgress<FFmpegInstallProgress>? progress = null)
    {
        _options = options ?? new FFmpegInstallOptions();
        _progress = progress;
        Root = _options.ToolsRoot ?? DefaultToolsRoot();
        LogPath = Path.Combine(Root, "build.log");
    }

    /// <summary>The folder everything but the finished libraries goes in.</summary>
    public string Root { get; }

    /// <summary>Everything the tools said, for when something goes wrong.</summary>
    public string LogPath { get; }

    /// <summary>
    /// The default tools folder. FFmpeg's configure refuses a path with a space in it and MSYS2 wants plain ASCII, so a
    /// user folder that has either is tried in its short 8.3 form, and failing that the machine's ProgramData is used.
    /// </summary>
    public static string DefaultToolsRoot()
    {
        string preferred = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FUPlayer", "ffmpeg-build");
        if (IsBuildablePath(preferred) || !OperatingSystem.IsWindows())
        {
            return preferred;
        }

        Directory.CreateDirectory(preferred);
        if (ShortPath(preferred) is { } shortened && IsBuildablePath(shortened))
        {
            return shortened;
        }

        string shared = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FUPlayer", "ffmpeg-build");
        return IsBuildablePath(shared)
            ? shared
            : throw new InvalidOperationException(
                Loc.F("FFmpeg cannot be built under '{0}': its build tools take no paths with spaces or letters outside ASCII.", preferred));
    }

    /// <summary>Whether FFmpeg's configure and MSYS2 take the path: ASCII, and nothing a shell would split or expand.</summary>
    public static bool IsBuildablePath(string path) =>
        path.Length > 0 && path.All(c => c is > ' ' and < (char)127 and not ('"' or '\'' or '`' or '$' or '&' or ';' or '|' or '<' or '>' or '*' or '?' or '!'));

    /// <summary>"C:\Users\me\x" as MSYS2 writes it: "/c/Users/me/x".</summary>
    public static string ToMsysPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string full = path.Replace('\\', '/');
        return full.Length >= 2 && full[1] == ':' && char.IsAsciiLetter(full[0])
            ? "/" + char.ToLowerInvariant(full[0]) + full[2..]
            : full;
    }

    /// <summary>
    /// Downloads, builds and installs FFmpeg, then loads it. Throws <see cref="OperationCanceledException"/> when
    /// cancelled and <see cref="InvalidOperationException"/> or <see cref="IOException"/> when a step fails, with the
    /// tools' last lines in the message; <see cref="LogPath"/> has the rest.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Root);

        // One build at a time, whichever process started it: the player, or the command line tool.
        FileStream gate;
        try
        {
            gate = new FileStream(Path.Combine(Root, "build.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            throw new InvalidOperationException("FFmpeg is already being built by another copy of the player.");
        }

        await using (gate)
        await using (_log = new StreamWriter(LogPath, append: false, new UTF8Encoding(false)) { AutoFlush = true })
        {
            try
            {
                await BuildAndInstallAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Log("Cancelled.");
                throw;
            }
            catch (Exception ex)
            {
                Log($"Failed: {ex.Message}");
                throw;
            }
            finally
            {
                lock (_logGate)
                {
                    _log = null;
                }
            }
        }
    }

    private async Task BuildAndInstallAsync(CancellationToken token)
    {
        Log($"Building FFmpeg {FFmpegVersion} in {Root}, to install in {_options.InstallDirectory}.");
        string tarball = await DownloadAsync(SourceUrls, SourceSha256, Path.Combine(Root, SourceFileName), "Downloading the FFmpeg source", token);

        string bash;
        if (OperatingSystem.IsWindows())
        {
            string msys = await FindOrSetUpMsys2Async(token);
            bash = Path.Combine(msys, "usr", "bin", "bash.exe");
            await InstallToolchainAsync(bash, token);
        }
        else
        {
            bash = new[] { "/bin/bash", "/usr/bin/bash", "/usr/local/bin/bash" }.FirstOrDefault(File.Exists) ?? "bash";
        }

        string source = Path.Combine(Root, "ffmpeg-" + FFmpegVersion);
        string work = Path.Combine(Root, "work");
        string output = Path.Combine(Root, "out");
        string stage = Path.Combine(Root, "stage");
        foreach (string stale in new[] { source, work, output, stage })
        {
            DeleteDirectory(stale);
        }

        // GNU tar and bsdtar both recognise the compression themselves when reading from a file.
        Report("Unpacking the source");
        await RunBashAsync(bash, $"tar -xf {Quote(ToMsysPath(tarball))} -C {Quote(ToMsysPath(Root))}", "Unpacking the source", token);

        string script = Path.Combine(Root, "build-ffmpeg-lgpl.sh");
        await using (Stream resource = typeof(FFmpegInstaller).Assembly.GetManifestResourceStream(ScriptResource)
            ?? throw new InvalidOperationException("The build script is missing from the player."))
        using (var reader = new StreamReader(resource))
        {
            // bash reads a script with Windows line endings as commands ending in a carriage return.
            await File.WriteAllTextAsync(script, (await reader.ReadToEndAsync(token)).Replace("\r\n", "\n"), new UTF8Encoding(false), token);
        }

        // configure says nothing until it has run all its tests, a few minutes on Windows where starting a process is slow.
        Report("Configuring FFmpeg", null, Loc.T("Testing what the compiler can do; this prints nothing until it is done."));
        int compiled = 0;
        string command = string.Join(
            ' ',
            $"FFMPEG_SRC={Quote(ToMsysPath(source))}",
            $"FFMPEG_WORK_DIR={Quote(ToMsysPath(work))}",
            $"FFMPEG_OUT_DIR={Quote(ToMsysPath(output))}",
            $"FFMPEG_STAGE_DIR={Quote(ToMsysPath(stage))}",
            $"JOBS={Math.Clamp(_options.Jobs, 1, 64)}",
            $"bash {Quote(ToMsysPath(script))}");
        await RunBashAsync(bash, command, "The FFmpeg build", token, line =>
        {
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("CC", StringComparison.Ordinal) || trimmed.StartsWith("X86ASM", StringComparison.Ordinal))
            {
                compiled++;
                Report("Compiling FFmpeg", Math.Min(0.99, compiled / (double)ExpectedObjects), trimmed);
            }
            else if (trimmed.StartsWith("INSTALL", StringComparison.Ordinal))
            {
                Report("Installing FFmpeg", null, trimmed);
            }
            else
            {
                Report(_stage, null, trimmed);
            }
        });

        Install(source, stage);
        if (!_options.KeepBuildFiles)
        {
            // The objects and the unpacked source are some hundreds of megabytes; the download stays, so a rebuild does
            // not fetch it again.
            foreach (string folder in new[] { source, work, output, stage })
            {
                DeleteDirectory(folder);
            }
        }

        Report("Loading FFmpeg");
        if (!FFmpegLibrary.Reload())
        {
            throw new InvalidOperationException($"FFmpeg was built, but it did not load: {FFmpegLibrary.LoadError}");
        }

        Log($"Loaded {FFmpegLibrary.VersionDescription} from {FFmpegLibrary.LoadedFrom ?? "the system"}.");
        Report("FFmpeg is installed", 1);
    }

    /// <summary>MSYS2 to build with: the one asked for, an installed one, or a private one set up here.</summary>
    private async Task<string> FindOrSetUpMsys2Async(CancellationToken token)
    {
        if (_options.Msys2Root is { } given)
        {
            return HasBash(given) ? given : throw new InvalidOperationException($"There is no MSYS2 at {given}.");
        }

        if (!_options.PrivateMsys2 && FindInstalledMsys2() is { } installed)
        {
            Log($"Building with the MSYS2 installed at {installed}.");
            return installed;
        }

        string own = Path.Combine(Root, "msys64");
        if (HasBash(own) && File.Exists(Path.Combine(own, ReadyMarker)))
        {
            Log($"Building with the MSYS2 set up earlier at {own}.");
            return own;
        }

        string archive = await DownloadAsync(Msys2Urls, Msys2Sha256, Path.Combine(Root, Msys2FileName), "Downloading MSYS2", token);
        if (!HasBash(own))
        {
            Report("Unpacking MSYS2");
            await RunAsync(archive, ["-y", "-o" + Root], "Unpacking MSYS2", environment: null, onLine: null, tolerateFailure: false, token);
            if (!HasBash(own))
            {
                throw new InvalidOperationException($"MSYS2 did not unpack to {own}.");
            }
        }

        // The first login makes the home folder and the package signing keys. The first update then brings pacman and
        // the runtime up to date, which may end the shell it runs in, so it is allowed to fail; the second one updates
        // everything else and has to work.
        string bash = Path.Combine(own, "usr", "bin", "bash.exe");
        Report("Setting up MSYS2");
        await RunBashAsync(bash, "true", "Setting up MSYS2", token);
        Report("Updating MSYS2");
        await RunBashAsync(bash, "pacman -Syuu --noconfirm --disable-download-timeout", "Updating MSYS2", token, tolerateFailure: true);
        await RunBashAsync(bash, "pacman -Syuu --noconfirm --disable-download-timeout", "Updating MSYS2", token);
        await File.WriteAllTextAsync(Path.Combine(own, ReadyMarker), DateTime.UtcNow.ToString("O"), token);
        return own;
    }

    /// <summary>The compiler and tools, when MSYS2 lacks them.</summary>
    private async Task InstallToolchainAsync(string bash, CancellationToken token)
    {
        string packages = string.Join(' ', Packages);
        if (await RunBashAsync(bash, $"pacman -Q {packages} >/dev/null 2>&1", "Checking for the compiler", token, tolerateFailure: true) == 0)
        {
            Log("The compiler is installed.");
            return;
        }

        // Added against the package lists MSYS2 already has, which changes an installation of the user's own no more
        // than the build needs; the lists are refreshed only when that fails.
        Report("Installing the compiler");
        string install = $"pacman -S --needed --noconfirm --disable-download-timeout {packages}";
        if (await RunBashAsync(bash, install, "Installing the compiler", token, tolerateFailure: true) != 0)
        {
            await RunBashAsync(bash, install.Replace("-S ", "-Sy ", StringComparison.Ordinal), "Installing the compiler", token);
        }
    }

    /// <summary>Copies the libraries, with FFmpeg's licence, to where the player loads them from.</summary>
    private void Install(string source, string stage)
    {
        Report("Installing FFmpeg");
        string[] required = OperatingSystem.IsWindows()
            ? [$"avformat-{FFmpegLibrary.AvformatMajor}.dll", $"avcodec-{FFmpegLibrary.AvcodecMajor}.dll", $"avutil-{FFmpegLibrary.AvutilMajor}.dll", "swresample-7.dll"]
            : [];
        string? missing = required.FirstOrDefault(name => !File.Exists(Path.Combine(stage, name)));
        if (missing is not null)
        {
            throw new InvalidOperationException($"The build finished without {missing}.");
        }

        string target = _options.InstallDirectory;
        Directory.CreateDirectory(target);
        foreach (string file in Directory.GetFiles(stage))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }

        string licence = Path.Combine(source, "COPYING.LGPLv2.1");
        if (File.Exists(licence))
        {
            File.Copy(licence, Path.Combine(target, "COPYING.LGPLv2.1"), overwrite: true);
        }

        File.WriteAllText(
            Path.Combine(target, "README.txt"),
            $"FFmpeg {FFmpegVersion}'s libavformat, libavcodec, libavutil and libswresample, built on this computer by FUPlayer on "
            + $"{DateTime.Now:yyyy-MM-dd} from the unmodified source published at https://ffmpeg.org/releases/{SourceFileName}, "
            + "with the LGPL v2.1 configuration in FUPlayer's build-ffmpeg-lgpl.sh (no --enable-gpl, --enable-nonfree or "
            + "--enable-version3). FFmpeg is licensed under the GNU LGPL version 2.1 or later; see COPYING.LGPLv2.1. "
            + "Delete this folder to remove them."
            + Environment.NewLine);
        Log($"Installed {string.Join(", ", Directory.GetFiles(stage).Select(Path.GetFileName))} in {target}.");
    }

    private async Task<string> DownloadAsync(string[] urls, string sha256, string destination, string stage, CancellationToken token)
    {
        if (File.Exists(destination) && await HashAsync(destination, token) == sha256)
        {
            Log($"{Path.GetFileName(destination)} was downloaded before.");
            return destination;
        }

        Report(stage, 0);
        string partial = destination + ".part";
        for (int i = 0; ; i++)
        {
            string url = urls[i];
            try
            {
                Log($"Downloading {url}");
                using HttpResponseMessage response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();
                long? length = response.Content.Headers.ContentLength;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (Stream body = await response.Content.ReadAsStreamAsync(token))
                await using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                {
                    var buffer = new byte[1 << 16];
                    long total = 0;
                    long reported = 0;
                    int read;
                    string size = length > 0 ? $"{length / 1048576.0:0.0}" : "?";
                    while ((read = await body.ReadAsync(buffer, token)) > 0)
                    {
                        await file.WriteAsync(buffer.AsMemory(0, read), token);
                        hash.AppendData(buffer, 0, read);
                        total += read;
                        if (total - reported >= 1 << 19)
                        {
                            reported = total;
                            Report(stage, length > 0 ? total / (double)length : null, Loc.F("{0:0.0} of {1} MB", total / 1048576.0, size));
                        }
                    }

                    Report(stage, 1, $"{total / 1048576.0:0.0} MB");
                }

                string actual = Convert.ToHexStringLower(hash.GetHashAndReset());
                if (actual != sha256)
                {
                    throw new InvalidDataException(Loc.F("The file from {0} is not the one expected: its SHA-256 is {1}.", url, actual));
                }

                File.Move(partial, destination, overwrite: true);
                return destination;
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException && i + 1 < urls.Length && !token.IsCancellationRequested)
            {
                Log($"{ex.Message} Trying the next address.");
            }
            catch (HttpRequestException ex)
            {
                throw new IOException(Loc.F("{0} could not be downloaded: {1}", Path.GetFileName(destination), ex.Message), ex);
            }
        }
    }

    private Task<int> RunBashAsync(
        string bash, string command, string what, CancellationToken token, Action<string>? onLine = null, bool tolerateFailure = false)
    {
        // The UCRT64 environment, whose gcc builds ordinary Windows DLLs; its login profile keeps the Windows PATH out,
        // so configure finds MSYS2's own find and sort. HOME is dropped so a Git Bash parent's does not leak in.
        var environment = new Dictionary<string, string?>
        {
            ["MSYSTEM"] = "UCRT64",
            ["CHERE_INVOKING"] = "1",
            ["HOME"] = null,
            ["MSYS2_PATH_TYPE"] = null,
        };
        Log("$ " + command);
        return RunAsync(bash, OperatingSystem.IsWindows() ? ["-lc", command] : ["-c", command], what, environment, onLine, tolerateFailure, token);
    }

    private async Task<int> RunAsync(
        string file,
        IEnumerable<string> arguments,
        string what,
        Dictionary<string, string?>? environment,
        Action<string>? onLine,
        bool tolerateFailure,
        CancellationToken token)
    {
        var start = new ProcessStartInfo(file)
        {
            WorkingDirectory = Root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach ((string name, string? value) in environment)
            {
                if (value is null)
                {
                    start.Environment.Remove(name);
                }
                else
                {
                    start.Environment[name] = value;
                }
            }
        }

        using var process = new Process { StartInfo = start };
        DataReceivedEventHandler received = (_, e) =>
        {
            // MSYS2's messages come coloured for a terminal, which reads as noise anywhere else.
            if (e.Data is { } raw && TerminalCodes().Replace(raw, string.Empty) is { } line)
            {
                Log(line);
                if (onLine is not null)
                {
                    onLine(line);
                }
                else if (line.Length > 0)
                {
                    Report(_stage, null, line);
                }
            }
        };
        process.OutputDataReceived += received;
        process.ErrorDataReceived += received;
        lock (_logGate)
        {
            _lastLines.Clear();
        }

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException(Loc.F("{0} could not start {1}: {2}", Loc.T(what), file, ex.Message), ex);
        }

        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using (token.Register(() => Kill(process)))
        {
            await process.WaitForExitAsync(CancellationToken.None);
        }

        token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0 && !tolerateFailure)
        {
            string tail;
            lock (_logGate)
            {
                tail = string.Join(Environment.NewLine, _lastLines.TakeLast(12));
            }

            throw new InvalidOperationException(Loc.F("{0} failed (exit code {1}).", Loc.T(what), process.ExitCode) + Environment.NewLine + tail);
        }

        return process.ExitCode;
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Already gone.
        }
    }

    private void Report(string stage, double? fraction = null, string? line = null)
    {
        _stage = stage;
        _progress?.Report(new FFmpegInstallProgress(Loc.T(stage), fraction, line));
    }

    private void Log(string line)
    {
        lock (_logGate)
        {
            _log?.WriteLine(line);
            _lastLines.Enqueue(line);
            while (_lastLines.Count > 40)
            {
                _lastLines.Dequeue();
            }
        }
    }

    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
    }

    /// <summary>An MSYS2 installed on this computer, in the places its installer and winget put it, or null.</summary>
    public static string? FindInstalledMsys2()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var candidates = new List<string?>
        {
            Environment.GetEnvironmentVariable("MSYS2_ROOT"),
            @"C:\msys64",
            @"C:\msys2",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "msys64"),
        };
        return candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c) && HasBash(c));
    }

    private static bool HasBash(string root) => File.Exists(Path.Combine(root, "usr", "bin", "bash.exe"));

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        // Git and some tools leave read-only files, which Directory.Delete will not remove.
        foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FUPlayer");
        return client;
    }

    private static string? ShortPath(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var buffer = new char[1024];
        uint length = GetShortPathName(path, buffer, (uint)buffer.Length);
        return length > 0 && length < buffer.Length ? new string(buffer, 0, (int)length) : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetShortPathNameW", SetLastError = true)]
    private static extern uint GetShortPathName(string longPath, char[] shortPath, uint length);

    [System.Text.RegularExpressions.GeneratedRegex(@"\x1B\[[0-9;?]*[A-Za-z]|\x1B\][^\x07]*\x07|\r")]
    private static partial System.Text.RegularExpressions.Regex TerminalCodes();
}
