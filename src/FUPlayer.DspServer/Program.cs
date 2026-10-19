using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using FUPlayer.Core.Dsp.Restoration;
using FUPlayer.Core.Dsp.Acceleration;
using FUPlayer.Core.NetworkDsp;

try { return await Run(args); }
catch (Exception ex) when (ex is not (OutOfMemoryException or AccessViolationException))
{ Console.Error.WriteLine(ex.Message); return 1; }

static async Task<int> Run(string[] arguments)
{
    var flags = new HashSet<string>(StringComparer.Ordinal) { "worker", "cpu", "gpu-fast", "gpu-force", "help", "version", "licenses", "devices" };
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (int i = 0; i < arguments.Length; i++)
    {
        if (!arguments[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Expected --option.");
        string name = arguments[i][2..];
        options[name] = flags.Contains(name) ? "true" : ++i < arguments.Length ? arguments[i] : throw new ArgumentException($"Missing --{name} value.");
    }
    if (options.ContainsKey("help"))
    {
        Console.WriteLine("""
            FUPlayer Network DSP Server
            A self-contained LAN processing service, without the player or an audio device.
              --listen <private IP>       Bind address (default 0.0.0.0; private peers only)
              --port <n>                  UDP listener (default 58181)
              --worker-port-start <n>     First UDP worker port (default 58182)
              --worker-port-end <n>       Last UDP worker port (default 58213)
              --clients <n>               Concurrent processing clients (default 2)
              --threads <n>               DSP helper threads (default cores minus one)
              --gpu-device <id|api>       Auto, cuda, vulkan, opencl or a device identifier
              --gpu-fast                 32-bit convolution instead of 64-bit
              --gpu-force                Override GPU timing, retaining numerical verification
              --restorer-device <name>    Neural restorer adapter on Windows (default CPU)
              --upscaler-device <name>    Neural upscaler adapter on Windows (default CPU)
              --cpu                      CPU convolution
              --models <directory>       Custom models instead of embedded models
              --cache <directory>        Model/runtime cache (default per-user cache)
              --extract-licenses <dir>   Write embedded licences and notices
              --devices                  List convolution and neural adapter names
              --licenses | --version | --help
            Audio uses unencrypted UDP with selective loss recovery on a trusted private network.
            Workers exit on Stop/Pause/disconnect; the waiting listener does not load models or a GPU.
            """);
        return 0;
    }
    if (options.ContainsKey("version")) { Console.WriteLine(typeof(DspServer).Assembly.GetName().Version); return 0; }
    if (options.ContainsKey("devices"))
    {
        foreach (GpuDevice device in GpuRuntime.Devices) Console.WriteLine($"{device.Id}: {device.Name} / {device.BackendName}");
        foreach (GraphicsAdapter adapter in InferenceDevices.Adapters) Console.WriteLine($"Neural adapter: {adapter.Name}");
        return 0;
    }
    if (options.TryGetValue("extract-licenses", out string? licenceDirectory)) { Extract("Licenses", licenceDirectory); return 0; }
    if (options.ContainsKey("licenses"))
    {
        Console.WriteLine("FUPlayer: MIT. Included .NET, Silk.NET and ONNX Runtime retain their licences.");
        Console.WriteLine("Use --extract-licenses <directory> for every embedded licence and notice."); return 0;
    }
    int Number(string name, int fallback) => options.TryGetValue(name, out string? value) ? int.Parse(value) : fallback;
    int first = Number("worker-port-start", 58182), last = Number("worker-port-end", 58213);
    if (first < 1024 || last < first || last > 65535) throw new ArgumentException("Invalid worker port range.");
    bool worker = options.ContainsKey("worker");
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    using PosixSignalRegistration? term = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; stop.Cancel(); });
    using var slots = new SemaphoreSlim(Number("clients", 2));
    var children = new ConcurrentDictionary<int, Process>();
    var ports = new ConcurrentDictionary<int, byte>();
    int nextPort = first - 1;
    if (worker)
    {
        if (options.TryGetValue("models", out string? external)) ModelLibrary.Directory = Path.GetFullPath(external);
        else
        {
            string cache = options.GetValueOrDefault("cache") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FUPlayer-DspServer");
            string modelBuild = Assembly.Load("FUPlayer.DspModels").ManifestModule.ModuleVersionId.ToString("N");
            ModelLibrary.Directory = Path.Combine(Path.GetFullPath(cache), "models-" + modelBuild);
            Extract("Models", ModelLibrary.Directory);
        }
        Environment.SetEnvironmentVariable("FUPLAYER_MODELS_PATH", ModelLibrary.Directory);
    }
    async Task<int> StartWorker(CancellationToken cancellation)
    {
        if (!await slots.WaitAsync(0, cancellation)) throw new IOException("All DSP workers are busy.");
        Process? process = null;
        int port = 0;
        try
        {
            for (int attempt = 0; attempt <= last - first; attempt++)
            {
                int candidate = first + (Interlocked.Increment(ref nextPort) - first) % (last - first + 1);
                if (ports.TryAdd(candidate, 0)) { port = candidate; break; }
            }
            if (port == 0) throw new IOException("All DSP worker ports are busy.");
            string executable = Environment.ProcessPath ?? throw new IOException("Cannot locate the server executable.");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
            foreach (var pair in options.Where(p => p.Key is not ("port" or "worker")))
            {
                start.ArgumentList.Add("--" + pair.Key);
                if (!flags.Contains(pair.Key)) start.ArgumentList.Add(pair.Value);
            }
            start.ArgumentList.Add("--worker"); start.ArgumentList.Add("--port"); start.ArgumentList.Add(port.ToString());
            process = Process.Start(start) ?? throw new IOException("Cannot start DSP worker.");
            children[process.Id] = process;
            string? ready = await process.StandardOutput.ReadLineAsync(cancellation).AsTask().WaitAsync(TimeSpan.FromSeconds(20), cancellation);
            if (ready != $"READY {port}") throw new IOException("DSP worker did not become ready: " + ready);
            Process child = process;
            _ = Task.Run(async () =>
            {
                try
                {
                    while (await child.StandardOutput.ReadLineAsync() is { } line) Console.WriteLine($"worker {child.Id}: {line}");
                    await child.WaitForExitAsync();
                }
                finally
                {
                    children.TryRemove(child.Id, out _); ports.TryRemove(port, out _); child.Dispose();
                    try { slots.Release(); } catch (ObjectDisposedException) { }
                }
            });
            return port;
        }
        catch
        {
            if (process is not null) { children.TryRemove(process.Id, out _); if (!process.HasExited) process.Kill(true); process.Dispose(); }
            if (port != 0) ports.TryRemove(port, out _);
            slots.Release(); throw;
        }
    }
    await using var server = new DspServer(new DspServerOptions
    {
        ListenAddress = IPAddress.Parse(options.GetValueOrDefault("listen") ?? "0.0.0.0"), Port = Number("port", 58181),
        MaximumClients = worker ? 1 : Number("clients", 2), Threads = Number("threads", Math.Clamp(Environment.ProcessorCount - 1, 0, 63)),
        GpuDevice = options.GetValueOrDefault("gpu-device") ?? "", GpuAcceleration = !options.ContainsKey("cpu"),
        GpuHighPrecision = !options.ContainsKey("gpu-fast"), GpuForce = options.ContainsKey("gpu-force"),
        RestorerDevice = options.GetValueOrDefault("restorer-device") ?? "",
        UpscalerDevice = options.GetValueOrDefault("upscaler-device") ?? "",
        ExitAfterSession = worker, StartWorker = worker ? null : StartWorker,
    });
    server.Message += Console.WriteLine;
    Console.WriteLine(worker ? $"READY {server.Endpoint.Port}" : $"FUPlayer DSP listening on UDP {server.Endpoint}; worker ports {first}–{last}.");
    try { await server.RunAsync(stop.Token); }
    finally
    {
        foreach (Process child in children.Values)
            try { if (!child.HasExited) child.Kill(true); } catch (InvalidOperationException) { }
    }
    return 0;
}

static void Extract(string group, string directory)
{
    Directory.CreateDirectory(directory);
    Assembly assembly = group == "Models" ? Assembly.Load("FUPlayer.DspModels") : Assembly.GetExecutingAssembly();
    string prefix = $"FUPlayer.Server.{group}.";
    foreach (string resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)))
    {
        using Stream source = assembly.GetManifestResourceStream(resource)!;
        string path = Path.Combine(directory, resource[prefix.Length..]);
        if (File.Exists(path) && new FileInfo(path).Length == source.Length) continue;
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (FileStream destination = File.Create(temporary)) source.CopyTo(destination);
        File.Move(temporary, path, overwrite: true);
    }
}
