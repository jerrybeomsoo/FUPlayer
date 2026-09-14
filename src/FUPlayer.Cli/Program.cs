using System.Diagnostics;
using System.Globalization;
using FUPlayer.Audio.Windows;
using FUPlayer.Core.Capture;
using FUPlayer.Core.Audio;
using FUPlayer.Core.Decoding;
using FUPlayer.Core.Decoding.FFmpeg;
using FUPlayer.Core.Dsp.Acceleration;
using FUPlayer.Core.Dsp.Dsd;
using FUPlayer.Core.Dsp.Modulation;
using FUPlayer.Core.Dsp.Numerics;
using FUPlayer.Core.Dsp.Quantization;
using FUPlayer.Core.Dsp.Resampling;
using FUPlayer.Core.Dsp.Restoration;
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
                "apps" => ListApps(),
                "capture" => Capture(options),
                "models" => ListModels(),
                "train" => Train(options),
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
              apps                         List applications whose audio can be captured
              capture --app <name|pid>     Record one application's output to a WAV file
                      --seconds <n> --out <file>
              models                       List the installed high-band models
              train <files|folders…>       Fit a high-band model from lossless music
                      --cutoff <Hz> --out <name> [--bands <n>] [--ridge <r>]
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

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static ICaptureProvider CreateCaptureProvider() => new ProcessLoopbackProvider();

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

    private static int ListApps()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Fail("Capturing an application's audio is a Windows feature.");
        }

        return ListWindowsApps();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static int ListWindowsApps()
    {
        var provider = new ProcessLoopbackProvider();
        if (!provider.IsSupported)
        {
            return Fail(provider.UnsupportedReason ?? "Capture is not available.");
        }

        IReadOnlyList<CaptureTarget> targets = provider.List();
        if (targets.Count == 0)
        {
            Console.WriteLine("No application holds an audio session right now.");
            return 0;
        }

        (int rate, int channels) = ProcessLoopbackProvider.MixFormat();
        Console.WriteLine($"Windows mixes at {rate} Hz, {channels} channels. Capture runs at that rate.");
        Console.WriteLine();
        Console.WriteLine($"{"PID",8}  {"STATE",-8}  APPLICATION");
        foreach (CaptureTarget target in targets)
        {
            Console.WriteLine($"{target.ProcessId,8}  {(target.IsPlaying ? "playing" : "idle"),-8}  {target.Label}");
        }

        return 0;
    }

    private static int Capture(Options options)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Fail("Capturing an application's audio is a Windows feature.");
        }

        return CaptureWindows(options);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static int CaptureWindows(Options options)
    {
        var provider = new ProcessLoopbackProvider();
        if (!provider.IsSupported)
        {
            return Fail(provider.UnsupportedReason ?? "Capture is not available.");
        }

        string? wanted = options.Get("app");
        if (string.IsNullOrWhiteSpace(wanted))
        {
            return Fail("Pass --app with a process name or id. Run 'fuplayer-cli apps' to see them.");
        }

        IReadOnlyList<CaptureTarget> targets = provider.List();
        CaptureTarget? target = int.TryParse(wanted, NumberStyles.None, CultureInfo.InvariantCulture, out int pid)
            ? targets.FirstOrDefault(t => t.ProcessId == pid)
            : targets.FirstOrDefault(t => t.ProcessName.Equals(wanted, StringComparison.OrdinalIgnoreCase) && t.IsPlaying)
              ?? targets.FirstOrDefault(t => t.ProcessName.Equals(wanted, StringComparison.OrdinalIgnoreCase));

        if (target is null)
        {
            return Fail($"No audio session for '{wanted}'. Run 'fuplayer-cli apps' to see them.");
        }

        int seconds = Math.Clamp(options.GetInt("seconds") ?? 10, 1, 3600);
        string output = options.Get("out") ?? $"capture-{target.ProcessName}.wav";

        using IAudioDecoder decoder = provider.Open(target.ProcessId, channels: 0);
        int rate = decoder.Format.SampleRate;
        int channels = decoder.Format.Channels;
        long total = (long)(seconds * rate);
        int block = Math.Min(4096, (int)total);

        Console.WriteLine($"Capturing {target.Label} (pid {target.ProcessId}) at {rate} Hz, {channels} channels, for {seconds} s.");

        double[][] planar = new double[channels][];
        for (int channel = 0; channel < channels; channel++)
        {
            planar[channel] = new double[block];
        }

        using var file = new FileStream(output, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(file);
        WriteWavHeader(writer, rate, channels, total);

        double peak = 0.0;
        double sum = 0.0;
        long written = 0;
        var clock = Stopwatch.StartNew();
        while (written < total)
        {
            int frames = (int)Math.Min(block, total - written);
            decoder.ReadPcm(planar, 0, frames);

            for (int frame = 0; frame < frames; frame++)
            {
                for (int channel = 0; channel < channels; channel++)
                {
                    double sample = planar[channel][frame];
                    peak = Math.Max(peak, Math.Abs(sample));
                    sum += sample * sample;
                    writer.Write((float)sample);
                }
            }

            written += frames;

            // The capture arrives in real time, so the reader must not spin ahead of it.
            double due = (double)written / rate;
            double ahead = due - clock.Elapsed.TotalSeconds;
            if (ahead > 0.002)
            {
                Thread.Sleep(TimeSpan.FromSeconds(Math.Min(ahead, 0.25)));
            }
        }

        double rms = Math.Sqrt(sum / Math.Max(1, written * channels));
        Console.WriteLine($"Wrote {output}: {written} frames, peak {Db(peak)}, RMS {Db(rms)}.");
        if (decoder is IDiagnosticCapture diagnostics)
        {
            Console.WriteLine($"Device delivered {diagnostics.CapturedFrames} frames; {diagnostics.SilentFrames} were filled in by the reader.");
        }
        if (peak <= 0.0)
        {
            Console.WriteLine("Every sample was zero. The application was silent, or it renders through a process the tree does not cover.");
        }

        return 0;
    }

    private static string Db(double amplitude) =>
        amplitude <= 0.0 ? "silence" : $"{20.0 * Math.Log10(amplitude):0.0} dBFS";

    /// <summary>32-bit float WAV, which is what the capture delivers.</summary>
    private static void WriteWavHeader(BinaryWriter writer, int rate, int channels, long frames)
    {
        int blockAlign = channels * sizeof(float);
        long data = frames * blockAlign;
        writer.Write("RIFF"u8);
        writer.Write((uint)(36 + data));
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16u);
        writer.Write((ushort)3);
        writer.Write((ushort)channels);
        writer.Write((uint)rate);
        writer.Write((uint)(rate * blockAlign));
        writer.Write((ushort)blockAlign);
        writer.Write((ushort)32);
        writer.Write("data"u8);
        writer.Write((uint)data);
    }

    private static int ListModels()
    {
        Console.WriteLine($"Models folder: {ModelLibrary.Directory}");
        IReadOnlyList<string> files = ModelLibrary.List();
        if (files.Count == 0)
        {
            Console.WriteLine("No model is installed. Train one with 'fuplayer-cli train', or copy a");
            Console.WriteLine($"*{ModelLibrary.Extension} file into that folder.");
            return 0;
        }

        foreach (string file in files)
        {
            HighBandModel? model = ModelLibrary.TryLoad(file, out string? failure);
            if (model is null)
            {
                Console.WriteLine($"  {Path.GetFileName(file)}: unreadable ({failure})");
                continue;
            }

            Console.WriteLine($"  {Path.GetFileName(file)}");
            Console.WriteLine($"      cutoff {model.CutoffHz / 1000.0:0.#} kHz, predicts to {model.TopHz / 1000.0:0.#} kHz, "
                + $"{model.InputBands} in, {model.OutputBands} out");
            Console.WriteLine($"      trained at {model.SampleRate} Hz on {model.FramesSeen} frames"
                + (model.TrainedOn is null ? string.Empty : $" from {model.TrainedOn}"));
        }

        return 0;
    }

    private static int Train(Options options)
    {
        List<string> files = MediaScanner.Expand(options.Positional).ToList();
        if (files.Count == 0)
        {
            throw new ArgumentException("Give some lossless files or folders to learn from.");
        }

        double cutoff = options.GetInt("cutoff") ?? 16_000;
        int bands = Math.Clamp(options.GetInt("bands") ?? 16, 4, 48);
        string name = options.Get("out") ?? $"cutoff-{cutoff / 1000.0:0.#}k";
        double ridge = options.GetInt("ridge") is int r ? r / 1000.0 : 1e-3;

        const int size = 2048;
        var plan = new RealFftPlan(size);
        double[] window = new double[size];
        for (int i = 0; i < size; i++)
        {
            window[i] = 0.5 - (0.5 * Math.Cos(2.0 * Math.PI * i / size));
        }

        HighBandTrainer? trainer = null;
        int used = 0;
        int skipped = 0;
        double[] re = new double[size];
        double[] im = new double[size];
        double[] frame = new double[size];
        double[] power = new double[(size / 2) + 1];

        foreach (string file in files)
        {
            IAudioDecoder decoder;
            try
            {
                decoder = DecoderFactory.Open(file);
            }
            catch (Exception ex) when (ex is AudioDecoderException or IOException or NotSupportedException)
            {
                skipped++;
                continue;
            }

            using (decoder)
            {
                // Only material that still has the band being learned is any use as an answer.
                if (decoder.Format.IsDsd || decoder.Format.SampleRate < cutoff * 2.2)
                {
                    skipped++;
                    continue;
                }

                trainer ??= new HighBandTrainer(decoder.Format.SampleRate, cutoff, inputBands: bands);
                if (trainer.SampleRate != decoder.Format.SampleRate)
                {
                    skipped++;
                    continue;
                }

                int channels = decoder.Format.Channels;
                double[][] block = new double[channels][];
                for (int c = 0; c < channels; c++)
                {
                    block[c] = new double[size];
                }

                double binHz = (double)decoder.Format.SampleRate / size;
                while (decoder.ReadPcm(block, 0, size) == size)
                {
                    // The mid of the pair, which is where most of the music is.
                    for (int i = 0; i < size; i++)
                    {
                        double sum = 0.0;
                        for (int c = 0; c < channels; c++)
                        {
                            sum += block[c][i];
                        }

                        frame[i] = sum / channels * window[i];
                    }

                    double energy = 0.0;
                    for (int i = 0; i < size; i++)
                    {
                        energy += frame[i] * frame[i];
                    }

                    // Quiet frames teach nothing except what the noise floor looks like.
                    if (Math.Sqrt(energy / size) < 1e-3)
                    {
                        continue;
                    }

                    plan.Forward(frame, re, im);
                    for (int bin = 0; bin < power.Length; bin++)
                    {
                        power[bin] = (re[bin] * re[bin]) + (im[bin] * im[bin]);
                    }

                    trainer.Add(power, binHz);
                }

                used++;
                Console.Write($"\r{used} files, {trainer.Frames} frames   ");
            }
        }

        Console.WriteLine();
        if (trainer is null || trainer.Frames < 1000)
        {
            throw new InvalidOperationException(
                $"Only {trainer?.Frames ?? 0} usable frames were found. Point this at lossless music whose rate is at "
                + $"least {cutoff * 2.2 / 1000.0:0.#} kHz, and give it a few albums.");
        }

        HighBandModel model = trainer.Solve(ridge, trainedOn: $"{used} files");
        string path = Path.Combine(ModelLibrary.Directory, name + ModelLibrary.Extension);
        model.Save(path);

        Console.WriteLine($"Wrote {path}");
        Console.WriteLine($"  {trainer.Frames} frames from {used} files at {trainer.SampleRate} Hz"
            + (skipped > 0 ? $", {skipped} skipped" : string.Empty));
        Console.WriteLine($"  cutoff {trainer.CutoffHz / 1000.0:0.#} kHz, predicts {trainer.OutputBands} bands to {trainer.TopHz / 1000.0:0.#} kHz");
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
        // A capture is not a file, so it goes straight through rather than past the media scanner.
        List<string> files = options.Positional.Any(p => CaptureUri.TryParse(p, out _))
            ? [.. options.Positional.Where(p => CaptureUri.TryParse(p, out _))]
            : MediaScanner.Expand(options.Positional).ToList();
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
        ICaptureProvider? capture = OperatingSystem.IsWindows() ? CreateCaptureProvider() : null;
        using var engine = new PlaybackEngine(settings, backends, capture);
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
