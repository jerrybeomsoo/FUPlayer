using System.Diagnostics;
using System.Globalization;
using FUPlayer.Audio.Windows;
using FUPlayer.Core.Capture;
using FUPlayer.Core.Audio;
using FUPlayer.Core.Decoding;
using FUPlayer.Core.Decoding.FFmpeg;
using FUPlayer.Core.Dsp.Acceleration;
using FUPlayer.Core.Dsp.Analysis;
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

            // Where models are kept, for a run that wants them somewhere other than the settings
            // folder. Worth having because the settings folder is not the same folder for every
            // process: one running inside an application container has %APPDATA% redirected into the
            // container, so a model written there cannot be seen by a player started normally.
            if (options.Get("models") is string modelsFolder && modelsFolder.Length > 0)
            {
                ModelLibrary.Directory = Path.GetFullPath(modelsFolder);
            }

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
                "bandwidth" => Bandwidth(options),
                "roundtrip" => RoundTrip(options),
                "dataset" => Dataset(options),
                "train-repair" => TrainRepair(options),
                "evaluate" => Evaluate(options),
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
              bandwidth <files…>           Report where each file's spectrum ends
              roundtrip <file> --rate <B/s>  Code a file with the system AAC encoder and measure it
                      [--out <wav>]          Keep the coded copy, to play or render through the repair
              dataset <files…> --out <file>  Build training pairs by coding lossless music
                      [--bands <n>] [--low <Hz>] [--high <Hz>] [--stride <n>] [--seconds <n>]
              dataset --narrow-to <Hz>       Build high-resolution pairs instead: each file with
                                           everything above half that rate removed, against itself
              dataset --merge <sets…> --out <file>  Join sets built the same way, for parallel runs
              evaluate <files…>              Measure how much closer the repair gets to the original
              train-repair <dataset>         Fit the repair network to a training set
                      --out <name> [--hidden 96,64] [--epochs 8] [--decay <n>]
              models                       List the installed high-band models
              --models <folder>            Keep models somewhere other than the settings folder
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
              --repair-artifacts           Damp the warbling a low bit rate leaves behind
              --repair-rebuild             Synthesise a band above the codec's cutoff
              --repair-predict             Let a trained network set the levels for both
              --repair-difference          Output what the repair added instead of the repaired signal
              --rebuild-ultrasonics        Synthesise the band above the source's own Nyquist rate
              --network <file>             Which model to use, when more than one is installed
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

    private static int Evaluate(Options options)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Fail("Measuring the repair needs the system AAC encoder, which is a Windows feature.");
        }

        return EvaluateWindows(options);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static int EvaluateWindows(Options options)
    {
        List<string> files = MediaScanner.Expand(options.Positional).ToList();
        if (files.Count == 0)
        {
            throw new ArgumentException("Give lossless files the model was not trained on.");
        }

        int[] rates = options.Get("rates") is string list
            ? [.. list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out int value) ? value : 0)
                .Where(value => value > 0)]
            : AacRoundTrip.BytesPerSecond;

        double seconds = Math.Clamp(options.GetInt("seconds") ?? 60, 5, 600);
        double amount = Math.Clamp((options.GetInt("amount") ?? 100) / 100.0, 0.0, 1.0);

        return RepairEvaluator.Run(files, rates, seconds, options.Get("network"), amount);
    }

    private static int TrainRepair(Options options)
    {
        string? dataset = options.Positional.FirstOrDefault();
        if (dataset is null)
        {
            throw new ArgumentException("Give the training set built by 'dataset'.");
        }

        int[] hidden = options.Get("hidden") is string list
            ? [.. list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out int value) ? value : 0)
                .Where(value => value > 0)]
            : [96, 64];

        if (hidden.Length == 0)
        {
            throw new ArgumentException("No usable hidden layer sizes were given.");
        }

        int epochs = Math.Clamp(options.GetInt("epochs") ?? 8, 1, 200);
        int batch = Math.Clamp(options.GetInt("batch") ?? 256, 8, 4096);
        float rate = (options.GetInt("rate") ?? 300) / 100_000f;
        float decay = (options.GetInt("decay") ?? 100) / 100_000f;

        return RepairTrainer.Run(dataset, options.Get("out") ?? "repair", hidden, epochs, rate, batch, decay, options.Get("note"));
    }

    private static int Dataset(Options options)
    {
        if (options.Has("merge"))
        {
            // Joining sets reads and writes files; it needs no encoder and no particular platform.
            string merged = options.Get("out") ?? "repair-dataset.fudata";
            long frames = RepairDataset.Merge(options.Positional, merged);
            Console.WriteLine($"Joined {options.Positional.Count} sets into {merged}: {frames:N0} frames.");
            return 0;
        }

        if (!OperatingSystem.IsWindows())
        {
            return Fail("Building a training set needs the system AAC encoder, which is a Windows feature.");
        }

        return DatasetWindows(options);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static int DatasetWindows(Options options)
    {
        List<string> files = MediaScanner.Expand(options.Positional).ToList();
        if (files.Count == 0)
        {
            throw new ArgumentException("Give some lossless files or folders to code.");
        }

        string output = options.Get("out") ?? "repair-dataset.fudata";
        double seconds = Math.Clamp(options.GetInt("seconds") ?? 240, 10, 36_000);
        int bands = Math.Clamp(options.GetInt("bands") ?? 64, 8, 160);
        int context = Math.Clamp(options.GetInt("context") ?? 1, 0, 4);
        // With one codec and thirty files every other frame was worth keeping. With four codecs at
        // fourteen bit rates and two hundred files, a hundred and fifty times as many frames arrive
        // and most of them are the neighbours of frames already in the set. A wide stride keeps the
        // variety and drops the repetition.
        int stride = Math.Clamp(options.GetInt("stride") ?? 2, 1, 256);

        // The layout decides where the model has resolution. Log spacing from 200 Hz spends most of
        // its bands below 10 kHz, where a codec changes nothing, and leaves three above 15 kHz, where
        // the whole of the rebuild happens. Raising the bottom moves them to where the work is.
        double lowHz = Math.Clamp(options.GetDouble("low") ?? 200.0, 20.0, 5_000.0);
        // Up to 96 kHz, because the high-resolution set describes bands above the source's Nyquist
        // rate and a 192 kHz recording has something to say as far as 96.
        double highHz = Math.Clamp(options.GetDouble("high") ?? 22_050.0, lowHz * 4.0, 96_000.0);

        // Narrowing instead of coding: the high-resolution set, where the pair is a fast recording
        // and the same recording with everything above half this rate removed.
        int narrowTo = Math.Clamp(options.GetInt("narrow-to") ?? 0, 0, 96_000);

        int[] rates = options.Get("rates") is string list
            ? [.. list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out int value) ? value : 0)
                .Where(value => value > 0)]
            : AacRoundTrip.BytesPerSecond;

        if (rates.Length == 0)
        {
            throw new ArgumentException("No usable bit rates were given.");
        }

        Console.WriteLine(narrowTo > 0
            ? $"Narrowing {files.Count} files to {narrowTo / 1000.0:0.#} kHz, up to {seconds / 60.0:0.#} min each."
            : $"Coding {files.Count} files at {string.Join(", ", rates.Select(r => $"{r * 8 / 1000} kbit/s"))}, "
                + $"up to {seconds / 60.0:0.#} min each.");

        return DatasetBuilder.Run(files, output, rates, seconds, bands, context, stride, lowHz, highHz, narrowTo);
    }

    /// <summary>
    /// Codes a file with the system encoder and reports what that did to it. This is the check that
    /// the training pairs are worth anything: the coded copy has to be band-limited where a codec
    /// would band-limit it, and it has to line up with the original.
    /// </summary>
    private static int RoundTrip(Options options)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Fail("The system AAC encoder is a Windows feature.");
        }

        return RoundTripWindows(options);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static int RoundTripWindows(Options options)
    {
        string? path = options.Positional.FirstOrDefault();
        if (path is null)
        {
            throw new ArgumentException("Give a lossless file to code.");
        }

        int bytesPerSecond = options.GetInt("rate") ?? 16_000;
        int seconds = Math.Clamp(options.GetInt("seconds") ?? 30, 2, 600);

        using IAudioDecoder decoder = DecoderFactory.Open(path);
        int rate = decoder.Format.SampleRate;
        int channels = decoder.Format.Channels;
        if (!AacRoundTrip.Supports(rate, channels))
        {
            return Fail($"The system encoder does not take {rate} Hz with {channels} channels.");
        }

        int frames = seconds * rate;
        double[][] original = new double[channels][];
        for (int c = 0; c < channels; c++)
        {
            original[c] = new double[frames];
        }

        int read = decoder.ReadPcm(original, 0, frames);
        if (read < rate)
        {
            return Fail("The file is too short to measure.");
        }

        var clock = Stopwatch.StartNew();
        double[][]? coded = AacRoundTrip.Process(original, rate, channels, bytesPerSecond);
        if (coded is null)
        {
            return Fail("The system encoder refused the format.");
        }

        Console.WriteLine($"{path}: {rate} Hz, {channels} ch, {read / (double)rate:0.#} s, "
            + $"coded at {bytesPerSecond * 8 / 1000} kbit/s in {clock.Elapsed.TotalSeconds:0.0} s");

        Console.WriteLine($"  original: {Verdict(original, rate, read)}");
        Console.WriteLine($"  coded   : {Verdict(coded, rate, read)}");

        // How far apart the two are, which is what the model is being asked to shrink.
        double error = 0.0;
        double signal = 0.0;
        for (int c = 0; c < channels; c++)
        {
            for (int i = 0; i < read; i++)
            {
                double difference = original[c][i] - coded[c][i];
                error += difference * difference;
                signal += original[c][i] * original[c][i];
            }
        }

        Console.WriteLine($"  difference: {10.0 * Math.Log10(error / Math.Max(1e-30, signal)):0.0} dB relative to the original");

        // A coded copy on disk, so the repair can be run over it the way a listener would.
        if (options.Get("out") is string codedPath)
        {
            using var file = new FileStream(codedPath, FileMode.Create, FileAccess.Write);
            using var writer = new BinaryWriter(file);
            WriteWavHeader(writer, rate, channels, read);
            for (int i = 0; i < read; i++)
            {
                for (int c = 0; c < channels; c++)
                {
                    writer.Write((float)coded[c][i]);
                }
            }

            Console.WriteLine($"  wrote {codedPath}");
        }

        // Where the codec actually spent its bits, which the verdict alone does not show.
        Console.WriteLine($"  {"BAND",-14} {"ORIGINAL",9} {"CODED",9} {"CHANGE",8}");
        double[] edges = [1_000, 4_000, 8_000, 11_000, 13_000, 15_000, 16_500, 18_000, 20_000, 22_050];
        for (int band = 0; band < edges.Length - 1; band++)
        {
            double a = BandDb(original, rate, read, edges[band], edges[band + 1]);
            double b = BandDb(coded, rate, read, edges[band], edges[band + 1]);
            Console.WriteLine($"  {edges[band] / 1000.0,5:0.#}-{edges[band + 1] / 1000.0,-8:0.#} {a,9:0.0} {b,9:0.0} {b - a,8:+0.0;-0.0}");
        }

        return 0;
    }

    /// <summary>Mean power in one band, in decibels, across the whole signal.</summary>
    private static double BandDb(double[][] planar, int rate, int frames, double lowHz, double highHz)
    {
        const int size = 8192;
        var plan = new RealFftPlan(size);
        double[] re = new double[size];
        double[] im = new double[size];
        double[] frame = new double[size];
        double binHz = (double)rate / size;
        int low = (int)(lowHz / binHz);
        int high = Math.Min(plan.Bins, (int)Math.Ceiling(highHz / binHz));

        double total = 0.0;
        int blocks = 0;
        for (int start = 0; start + size <= frames; start += size)
        {
            for (int i = 0; i < size; i++)
            {
                double sum = 0.0;
                for (int c = 0; c < planar.Length; c++)
                {
                    sum += planar[c][start + i];
                }

                frame[i] = sum / planar.Length * (0.5 - (0.5 * Math.Cos(2.0 * Math.PI * i / size)));
            }

            plan.Forward(frame, re, im);
            for (int bin = low; bin < high; bin++)
            {
                total += (re[bin] * re[bin]) + (im[bin] * im[bin]);
            }

            blocks++;
        }

        double mean = total / Math.Max(1, blocks * Math.Max(1, high - low));
        return mean <= 0.0 ? -400.0 : 10.0 * Math.Log10(mean);
    }

    private static string Verdict(double[][] planar, int rate, int frames)
    {
        var detector = new CodecBandwidthDetector(rate);
        double[] mid = new double[frames];
        for (int i = 0; i < frames; i++)
        {
            double sum = 0.0;
            for (int c = 0; c < planar.Length; c++)
            {
                sum += planar[c][i];
            }

            mid[i] = sum / planar.Length;
        }

        detector.Push(mid);
        return detector.Estimate().Describe();
    }

    /// <summary>Reports what the long-term spectrum says about how each file was coded.</summary>
    private static int Bandwidth(Options options)
    {
        List<string> files = MediaScanner.Expand(options.Positional).ToList();
        if (files.Count == 0)
        {
            throw new ArgumentException("Give some audio files or folders.");
        }

        double seconds = Math.Clamp(options.GetInt("seconds") ?? 60, 2, 3600);

        // "ROLL-OFF" is where the fall begins and "CUTOFF" is where it ends. A rebuild starts at the
        // cutoff: filling from the start of the roll-off overwrites music that is still there, and
        // measured against the lossless masters that costs several decibels. On a brick wall the two
        // are the same frequency anyway.
        Console.WriteLine($"{"VERDICT",-14} {"CUTOFF",9} {"ROLL-OFF",11} {"EDGE",7}  FILE");
        foreach (string file in files)
        {
            IAudioDecoder decoder;
            try
            {
                decoder = DecoderFactory.Open(file);
            }
            catch (Exception ex) when (ex is AudioDecoderException or IOException or NotSupportedException)
            {
                Console.WriteLine($"{"unreadable",-14} {string.Empty,9} {string.Empty,11} {string.Empty,7}  {Path.GetFileName(file)}: {ex.Message}");
                continue;
            }

            using (decoder)
            {
                BandwidthEstimate estimate = Measure(decoder, seconds);
                string cutoff = estimate.Verdict == BandwidthVerdict.Unknown
                    ? string.Empty
                    : $"{estimate.CutoffHz / 1000.0:0.0} kHz";
                string edge = estimate.Verdict == BandwidthVerdict.BandLimited ? $"{estimate.EdgeDropDb:0} dB" : string.Empty;
                string from = estimate.Verdict == BandwidthVerdict.BandLimited
                    ? $"{estimate.TransitionHz / 1000.0:0.0} kHz"
                    : string.Empty;
                Console.WriteLine($"{estimate.Verdict,-14} {cutoff,9} {from,11} {edge,7}  {Path.GetFileName(file)}");
            }
        }

        return 0;
    }

    /// <summary>Pushes the mid of the first few seconds through the detector.</summary>
    private static BandwidthEstimate Measure(IAudioDecoder decoder, double seconds)
    {
        if (decoder.Format.IsDsd)
        {
            return default;
        }

        int rate = decoder.Format.SampleRate;
        int channels = decoder.Format.Channels;
        var detector = new CodecBandwidthDetector(rate);

        const int block = 4096;
        double[][] planar = new double[channels][];
        for (int c = 0; c < channels; c++)
        {
            planar[c] = new double[block];
        }

        double[] mid = new double[block];
        long limit = (long)(seconds * rate);
        long done = 0;

        while (done < limit)
        {
            int frames = decoder.ReadPcm(planar, 0, block);
            if (frames <= 0)
            {
                break;
            }

            for (int i = 0; i < frames; i++)
            {
                double sum = 0.0;
                for (int c = 0; c < channels; c++)
                {
                    sum += planar[c][i];
                }

                mid[i] = sum / channels;
            }

            detector.Push(mid.AsSpan(0, frames));
            done += frames;
        }

        return detector.Estimate();
    }

    private static int ListModels()
    {
        Console.WriteLine($"Models folder: {ModelLibrary.Directory}");

        // The same sentence the player's DSP studio shows, from the same code, so that a
        // disagreement between the two is not possible.
        Console.WriteLine(ModelLibrary.DescribeInstalled());
        Console.WriteLine();

        IReadOnlyList<string> networks = ModelLibrary.ListNetworks();
        IReadOnlyList<string> files = ModelLibrary.List();

        if (networks.Count == 0 && files.Count == 0)
        {
            Console.WriteLine("Nothing is installed. Build a training set with 'dataset' and fit one with");
            Console.WriteLine($"'train-repair', or copy a *{ModelLibrary.NeuralExtension} file into that folder.");
            return 0;
        }

        foreach (string network in networks)
        {
            NeuralRepairModel? model = ModelLibrary.TryLoadNetwork(network, out string? broken);
            if (model is null)
            {
                Console.WriteLine($"  {Path.GetFileName(network)}: unreadable ({broken})");
                continue;
            }

            // A network whose weights are all zero answers with its output biases and nothing else,
            // which is a constant curve however it is stored.
            Console.WriteLine($"  {Path.GetFileName(network)}   {(model.IsConstantCurve ? "constant curve" : "network")}");
            Console.WriteLine($"      {string.Join("-", model.Layers)} over {model.Bands} bands, "
                + $"{model.Context} frames of context, {model.LowHz / 1000.0:0.#} to {model.HighHz / 1000.0:0.#} kHz");
            Console.WriteLine($"      fitted to {model.FramesSeen:N0} frames"
                + (model.TrainedOn is null ? string.Empty : $" from {model.TrainedOn}")
                + $", held-out error {model.Loss:F3}");
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

        /// <summary>
        /// Options that take no value. Anything not named here swallows the next word, so a switch
        /// left off this list quietly eats the first of its own arguments.
        /// </summary>
        private static readonly HashSet<string> Switches = new(StringComparer.Ordinal)
        {
            "dop", "pass-through", "remove-ultrasonics", "no-limiter",
            "gpu", "gpu-fast", "gpu-force", "gpu-hold", "probe", "convolution-layered",
            "repair-artifacts", "repair-rebuild", "repair-predict", "repair-difference",
            "rebuild-ultrasonics", "merge",
        };

        public static Options Parse(IEnumerable<string> args)
        {
            var options = new Options();
            string[] list = args.ToArray();
            for (int i = 0; i < list.Length; i++)
            {
                if (list[i].StartsWith("--", StringComparison.Ordinal))
                {
                    string name = list[i][2..];
                    bool hasValue = i + 1 < list.Length
                        && !list[i + 1].StartsWith("--", StringComparison.Ordinal)
                        && !Switches.Contains(name);
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

        public double? GetDouble(string name) =>
            double.TryParse(Get(name), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : null;

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

            settings.Restoration.ReduceArtifacts = Has("repair-artifacts");
            settings.Restoration.RebuildHarmonics = Has("repair-rebuild");
            settings.Restoration.Predict = Has("repair-predict");
            settings.Restoration.OutputDifference = Has("repair-difference");
            settings.Restoration.RebuildUltrasonics = Has("rebuild-ultrasonics");

            // Which model, when there is more than one installed. Without this the newest wins, which
            // is right for playing and useless for comparing two of them on the same music.
            if (Get("network") is string chosen && chosen.Length > 0)
            {
                settings.Restoration.NetworkPath = Path.GetFullPath(chosen);
            }

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
