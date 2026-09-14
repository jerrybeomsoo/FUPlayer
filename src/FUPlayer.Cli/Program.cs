using System.Diagnostics;
using System.Globalization;
using FUPlayer.Audio.Windows;
using FUPlayer.Core.Audio;
using FUPlayer.Core.Decoding;
using FUPlayer.Core.Decoding.FFmpeg;
using FUPlayer.Core.Dsp.Acceleration;
using FUPlayer.Core.Dsp.Dsd;
using FUPlayer.Core.Dsp.Modulation;
using FUPlayer.Core.Dsp.Quantization;
using FUPlayer.Core.Dsp.Resampling;
using FUPlayer.Core.Engine;
using FUPlayer.Core.Output;
using FUPlayer.Core.Playlists;
using FUPlayer.Core.Settings;

namespace FUPlayer.Cli;

/// <summary>Headless companion to FUPlayer: device listing, offline rendering, console playback and benchmarks.</summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp();
            return 0;
        }

        try
        {
            var options = Options.Parse(args.Skip(1));
            return args[0] switch
            {
                "devices" => ListDevices(options),
                "filters" => ListFilters(),
                "dithers" => ListDithers(),
                "modulators" => ListModulators(),
                "gpus" => ListGpus(),
                "info" => ShowInfo(options),
                "render" => Render(options, benchmark: false),
                "bench" => Render(options, benchmark: true),
                "play" => Play(options),
                _ => Fail($"Unknown command '{args[0]}'. Run 'fuplayer-cli help'."),
            };
        }
        catch (Exception ex) when (ex is ArgumentException or AudioDecoderException or InvalidOperationException or IOException or NotSupportedException)
        {
            return Fail(ex.Message);
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            fuplayer-cli: FUPlayer command-line tool

            Commands
              devices                      List output back-ends, devices and their capabilities
              filters | dithers | modulators  List processing presets
              gpus                         List the OpenCL devices that can convolve long filters
              info <file>                  Show format, tags and the processing plan for a file
              render <files…> --out <dir>  Process files faster than real time into WAV/DSF
              bench <file>                 Measure how many times faster than real time processing runs
              play <files…>                Play through an audio device (Ctrl+C to stop)

            Processing options
              --mode pcm|dsd|follow        Output mode: everything as PCM, everything as DSD,
                                           or follow the file's own format (default pcm)
              --rate <Hz>                  Fixed PCM output rate
              --limit <Hz>                 Highest PCM output rate
              --dsd 64|128|256|512|1024    Highest DSD multiple
              --filter <id> --dither <id> --modulator <id>
              --taps <n>                   Filter length in taps at the output rate (0 = from the filter)
              --staging single|multi       One filter to the final rate, or half-band stages after it
              --convolution auto|tap       Frequency domain for long filters, or one multiply-add per tap
              --convolution-from <taps>    Taps per phase from which the frequency domain takes over (default 1024)
              --convolution-block <ms>     Longest a frequency-domain stage may hold input (0 = choose)
              --convolution-layered        Divide the taps between short blocks for the early ones and
                                           longer blocks behind them, instead of one length for every tap.
                                           Far less arithmetic at a short block, and no graphics device
              --threads <n>                Extra DSP threads (0 = single threaded, default automatic)
              --bits <16..32>              DAC word length
              --volume <dB>                Volume (default 0 for render/bench, -3 for play)
              --dop                        Send DSD as DoP (PCM frames at 1/16 of the DSD rate)
              --pass-through               Send DSD files unchanged when the output rate matches
              --remove-ultrasonics         Low-pass files above 48 kHz at 20 kHz
              --no-limiter                 Leave peaks above full scale alone instead of limiting them
              --gpu                        Convolve long filters on an OpenCL device
              --gpu-device <id>            Which device, from 'gpus' (default: the most capable one)
              --gpu-fast                   Let the device work in 32-bit; much faster on most cards
              --gpu-force                  Use the device even where the processor measures faster
              --gpu-share <0..100>         Percentage of the polyphase phases to give the device, instead of
                                           timing every division and keeping the quickest
              --gpu-hold                   Time the divisions once and hold the answer, rather than going
                                           back over them as the stream plays

            Device options (play)
              --backend wasapi-exclusive|wasapi-shared|asio|null
              --device <id>                Device id from 'devices'
              --buffer <ms>                Device buffer length
            """);
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"error: {message}");
        return 1;
    }

    private static List<IAudioBackend> CreateBackends(string? fileDirectory)
    {
        var backends = new List<IAudioBackend>();
        if (OperatingSystem.IsWindows())
        {
            backends.AddRange(WindowsAudioBackends.Create());
        }

        backends.Add(new FileAudioBackend(() => fileDirectory ?? Environment.CurrentDirectory));
        backends.Add(new NullAudioBackend());
        return backends;
    }

    private static int ListDevices(Options options)
    {
        foreach (IAudioBackend backend in CreateBackends(null))
        {
            Console.WriteLine($"{backend.DisplayName} [{backend.Id}]{(backend.IsAvailable ? string.Empty : " (unavailable)")}");
            if (!backend.IsAvailable)
            {
                continue;
            }

            foreach (AudioDevice device in backend.GetDevices())
            {
                Console.WriteLine($"  {(device.IsDefault ? "*" : " ")} {device.Name}");
                Console.WriteLine($"      id: {device.Id}");
                if (options.Has("probe") || backend.Id is not (FileAudioBackend.BackendId or NullAudioBackend.BackendId))
                {
                    DeviceCapabilities capabilities = backend.GetCapabilities(device.Id, 2);
                    Console.WriteLine($"      channels ≤ {capabilities.MaxChannels}; PCM: {string.Join(", ", capabilities.PcmRates.Select(AudioRates.FormatShort))}");
                    Console.WriteLine($"      containers: {string.Join("/", capabilities.ContainerBits)} bit; native DSD: {(capabilities.NativeDsdRates.Count == 0 ? "none" : string.Join(", ", capabilities.NativeDsdRates.Select(r => "DSD" + AudioRates.DsdMultiplier(r))))}");
                    if (capabilities.Notes is not null)
                    {
                        Console.WriteLine($"      note: {capabilities.Notes}");
                    }
                }
            }
        }

        Console.WriteLine(FFmpegLibrary.IsAvailable ? $"FFmpeg: {FFmpegLibrary.VersionDescription}" : $"FFmpeg: not loaded ({FFmpegLibrary.LoadError})");
        return 0;
    }

    private static int ListFilters()
    {
        foreach (IGrouping<string, FilterPreset> group in FilterCatalog.All.GroupBy(p => p.Group))
        {
            Console.WriteLine(group.Key);
            foreach (FilterPreset preset in group)
            {
                Console.WriteLine($"  {preset.Id,-20} {preset.Name}");
            }
        }

        return 0;
    }

    private static int ListDithers()
    {
        foreach (DitherPreset preset in DitherCatalog.All)
        {
            Console.WriteLine($"  {preset.Id,-10} {preset.Name,-30} {preset.RecommendedFor}");
        }

        return 0;
    }

    private static int ListModulators()
    {
        foreach (ModulatorPreset preset in ModulatorCatalog.All)
        {
            Console.WriteLine($"  {preset.Id,-10} {preset.Name,-32} order {preset.Order}, H∞ {preset.MaxGain:0.00}, ≥ DSD{preset.MinimumDsdMultiplier}");
        }

        Console.WriteLine("DSD→PCM filters:");
        foreach (DsdFilterPreset preset in DsdFilterCatalog.All)
        {
            Console.WriteLine($"  {preset.Id,-16} {preset.Name}");
        }

        return 0;
    }

    private static int ListGpus()
    {
        if (!GpuRuntime.IsAvailable)
        {
            Console.WriteLine(GpuRuntime.Unavailable ?? "No OpenCL device was found.");
            return 0;
        }

        foreach (GpuDevice device in GpuRuntime.Devices)
        {
            Console.WriteLine($"  {device.Id}");
            Console.WriteLine($"      {device.Vendor}  ·  {device.Summary}");
        }

        return 0;
    }

    private static int ShowInfo(Options options)
    {
        string file = options.Positional.FirstOrDefault() ?? throw new ArgumentException("info needs a file.");
        var metadata = FUPlayer.Core.Metadata.MetadataReader.Read(file);
        Console.WriteLine($"{metadata.DisplayArtist} - {metadata.DisplayTitle}");
        Console.WriteLine($"Album: {metadata.DisplayAlbum}  Year: {metadata.Year}  Track: {metadata.TrackNumber}");
        Console.WriteLine($"Format: {metadata.Format?.Describe()}  Codec: {metadata.Codec}  Duration: {metadata.Duration:hh\\:mm\\:ss}");
        if (metadata.TrackGainDb is not null)
        {
            Console.WriteLine($"ReplayGain: track {metadata.TrackGainDb:+0.00;-0.00} dB, album {metadata.AlbumGainDb:+0.00;-0.00} dB");
        }

        if (metadata.Format is { } format)
        {
            PlayerSettings settings = options.ToSettings(defaultVolume: -3.0);
            IAudioBackend backend = CreateBackends(null).First(b => b.Id == (options.Get("backend") ?? NullAudioBackend.BackendId));
            PlaybackPlan plan = OutputPlanner.Plan(format, settings, backend, backend.GetCapabilities(options.Get("device"), settings.Output.Channels));
            Console.WriteLine($"Plan: {format.DescribeShort()} → {plan.Output.Describe()}");
            Console.WriteLine($"  filter: {plan.FilterName}; quantizer: {plan.QuantizerName}");
            foreach (string note in plan.Notes)
            {
                Console.WriteLine($"  note: {note}");
            }
        }

        return 0;
    }

    private static int Render(Options options, bool benchmark)
    {
        List<string> files = MediaScanner.Expand(options.Positional).ToList();
        if (files.Count == 0)
        {
            throw new ArgumentException("No playable input files were given.");
        }

        string directory = benchmark
            ? Path.Combine(Path.GetTempPath(), $"fuplayer-bench-{Guid.NewGuid():N}")
            : options.Get("out") ?? Environment.CurrentDirectory;

        PlayerSettings settings = options.ToSettings(defaultVolume: 0.0);
        settings.Output.BackendId = FileAudioBackend.BackendId;
        settings.Output.FileOutputDirectory = directory;
        var backend = new FileAudioBackend(() => directory);
        double audioSeconds = files.Sum(f => FUPlayer.Core.Metadata.MetadataReader.Read(f).Duration.TotalSeconds);

        var clock = Stopwatch.StartNew();
        RunToCompletion(settings, new AudioBackendRegistry([backend]), files, showProgress: !benchmark);
        clock.Stop();

        double speed = audioSeconds / Math.Max(1e-6, clock.Elapsed.TotalSeconds);
        if (benchmark)
        {
            Console.WriteLine($"Processed {audioSeconds:0.0} s of audio in {clock.Elapsed.TotalSeconds:0.00} s: {speed:0.0}× real time.");
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
        else
        {
            Console.WriteLine($"Wrote {backend.LastFilePath} ({speed:0.0}× real time).");
        }

        return 0;
    }

    private static int Play(Options options)
    {
        List<string> files = MediaScanner.Expand(options.Positional).ToList();
        if (files.Count == 0)
        {
            throw new ArgumentException("No playable input files were given.");
        }

        PlayerSettings settings = options.ToSettings(defaultVolume: -3.0);
        settings.Output.BackendId = options.Get("backend") ?? (OperatingSystem.IsWindows() ? WasapiBackend.ExclusiveId : NullAudioBackend.BackendId);
        settings.Output.DeviceId = options.Get("device");
        settings.Output.BufferMilliseconds = options.GetInt("buffer") ?? 0;
        RunToCompletion(settings, new AudioBackendRegistry(CreateBackends(null)), files, showProgress: true);
        return 0;
    }

    private static void RunToCompletion(PlayerSettings settings, AudioBackendRegistry backends, List<string> files, bool showProgress)
    {
        using var engine = new PlaybackEngine(settings, backends);
        using var finished = new ManualResetEventSlim(false);
        bool started = false;
        engine.ErrorOccurred += (_, message) => Console.Error.WriteLine($"\nerror: {message}");
        engine.StateChanged += (_, state) =>
        {
            if (state == EngineState.Playing)
            {
                started = true;
            }
            else if (state == EngineState.Stopped && started)
            {
                finished.Set();
            }
        };

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            engine.Stop();
            finished.Set();
        };

        engine.Queue.Add(files);
        engine.Play(0);
        bool announced = false;
        string? opening = null;
        string? latest = null;
        while (!finished.Wait(250))
        {
            PlaybackStatus status = engine.GetStatus();
            if (status.Plan is null)
            {
                continue;
            }

            latest = status.Acceleration;
            if (!announced || status.CurrentItem is null)
            {
                announced = true;
                Console.WriteLine($"{status.Plan.Source.DescribeShort()} → {status.Plan.Output.Describe()} | {status.Plan.FilterName} | {status.Plan.QuantizerName}");
                foreach (string note in status.Plan.Notes)
                {
                    Console.WriteLine($"  note: {note}");
                }

                if (status.Acceleration is { Length: > 0 } acceleration)
                {
                    opening ??= acceleration;
                    Console.WriteLine($"  {acceleration}");
                }
            }

            if (showProgress)
            {
                Console.Write(string.Create(CultureInfo.InvariantCulture,
                    $"\r{status.CurrentItem?.DisplayTitle,-40} {status.Position:mm\\:ss}/{status.Duration:mm\\:ss}  DSP {status.DspLoad * 100,5:0}%  FIFO {status.BufferFill * 100,3:0}%  limiter {status.LimiterEvents}  "));
            }
        }

        if (showProgress)
        {
            Console.WriteLine();
        }

        // The division of the work is measured as it plays, so where it ended up is not where it started.
        if (latest is { Length: > 0 } && latest != opening)
        {
            Console.WriteLine($"  ended: {latest}");
        }
    }

    private sealed class Options
    {
        private readonly Dictionary<string, string?> _named = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Positional { get; } = [];

        public static Options Parse(IEnumerable<string> args)
        {
            var options = new Options();
            string[] list = args.ToArray();
            for (int i = 0; i < list.Length; i++)
            {
                if (list[i].StartsWith("--", StringComparison.Ordinal))
                {
                    string name = list[i][2..];
                    bool hasValue = i + 1 < list.Length && !list[i + 1].StartsWith("--", StringComparison.Ordinal)
                        && name is not ("dop" or "pass-through" or "remove-ultrasonics" or "no-limiter"
                            or "gpu" or "gpu-fast" or "gpu-force" or "probe" or "convolution-layered"
                            or "gpu-hold");
                    options._named[name] = hasValue ? list[++i] : null;
                }
                else
                {
                    options.Positional.Add(list[i]);
                }
            }

            return options;
        }

        public bool Has(string name) => _named.ContainsKey(name);

        public string? Get(string name) => _named.TryGetValue(name, out string? value) ? value : null;

        public int? GetInt(string name) => int.TryParse(Get(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : null;

        public PlayerSettings ToSettings(double defaultVolume)
        {
            var settings = new PlayerSettings();
            settings.Output.Mode = Get("mode")?.ToLowerInvariant() switch
            {
                null or "pcm" => OutputMode.Pcm,
                "dsd" => OutputMode.Dsd,
                "follow" => OutputMode.FollowSource,
                string other => throw new ArgumentException($"Unknown output mode '{other}'. Use pcm, dsd or follow."),
            };
            if (GetInt("rate") is int rate)
            {
                settings.Pcm.RateSelection = RateSelection.Fixed;
                settings.Pcm.FixedRate = rate;
            }

            settings.Pcm.RateLimit = GetInt("limit") ?? 0;
            settings.Dsd.HighestMultiplier = GetInt("dsd") ?? settings.Dsd.HighestMultiplier;
            settings.Pcm.DacBits = GetInt("bits") ?? settings.Pcm.DacBits;
            if (Get("filter") is { } filter)
            {
                if (FilterCatalog.All.All(p => p.Id != filter))
                {
                    throw new ArgumentException($"Unknown filter '{filter}'. Run 'fuplayer-cli filters' for the list.");
                }

                settings.Pcm.Filter = filter;
                settings.Dsd.Filter = filter;
            }

            settings.Pcm.Staging = settings.Dsd.Staging = Get("staging")?.ToLowerInvariant() switch
            {
                null or "multi" => StagingMode.MultiStage,
                "single" => StagingMode.SingleStage,
                string other => throw new ArgumentException($"Unknown staging '{other}'. Use single or multi."),
            };
            if (GetInt("taps") is int taps)
            {
                settings.Pcm.FilterTaps = taps;
                settings.Dsd.FilterTaps = taps;
            }

            settings.Processing.DspThreads = GetInt("threads") ?? settings.Processing.DspThreads;

            settings.Pcm.DitherId = Get("dither") ?? settings.Pcm.DitherId;
            settings.Dsd.ModulatorId = Get("modulator") ?? settings.Dsd.ModulatorId;
            settings.Dsd.PassThrough = Has("pass-through");
            settings.Output.DsdTransport = Has("dop") ? DsdTransport.Dop : DsdTransport.Native;
            settings.Processing.RemoveUltrasonics = Has("remove-ultrasonics");
            settings.Processing.Limiter = !Has("no-limiter");

            settings.Processing.Convolution = Get("convolution")?.ToLowerInvariant() switch

            {

                null or "auto" or "automatic" => ConvolutionMode.Automatic,

                "tap" or "tap-by-tap" or "direct" => ConvolutionMode.TapByTap,

                string other => throw new ArgumentException($"Unknown convolution mode '{other}'. Use auto or tap."),

            };
            settings.Processing.ConvolutionThresholdTaps =
                int.TryParse(Get("convolution-from"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int crossover) ? crossover : 0;
            settings.Processing.ConvolutionMaxBlockMs =
                double.TryParse(Get("convolution-block"), NumberStyles.Float, CultureInfo.InvariantCulture, out double blockMs) ? blockMs : 0.0;
            settings.Processing.ConvolutionUniformBlocks = !Has("convolution-layered");
            settings.Processing.GpuAcceleration = Has("gpu") || Has("gpu-force");
            settings.Processing.GpuDeviceId = Get("gpu-device") ?? string.Empty;
            settings.Processing.GpuHighPrecision = !Has("gpu-fast");
            settings.Processing.GpuForce = Has("gpu-force");
            settings.Processing.GpuPhaseShare = GetInt("gpu-share") is int share ? Math.Clamp(share, 0, 100) : -1;
            settings.Processing.GpuRetimeWhilePlaying = !Has("gpu-hold");
            settings.Volume.VolumeDb = double.TryParse(Get("volume"), NumberStyles.Float, CultureInfo.InvariantCulture, out double volume) ? volume : defaultVolume;
            return settings;
        }
    }
}
