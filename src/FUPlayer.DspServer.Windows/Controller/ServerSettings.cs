using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using FUPlayer.Core.Localization;

namespace FUPlayer.ServerController;

/// <summary>Server-wide choices. The access key stays in its own file, never in the controller settings.</summary>
internal sealed record ServerSettings
{
    public string ListenAddress { get; init; } = "0.0.0.0";
    public int Port { get; init; } = 58181;
    public int WorkerPortStart { get; init; } = 58182;
    public int WorkerPortEnd { get; init; } = 58213;
    public int Clients { get; init; } = 2;
    public int Threads { get; init; } = Math.Clamp(Environment.ProcessorCount - 1, 0, 63);
    public bool AllowInternet { get; init; }
    public string KeyFile { get; init; } = "";
    public string GpuDevice { get; init; } = "";
    public bool GpuEnabled { get; init; } = true;
    public bool GpuHighPrecision { get; init; } = true;
    public bool GpuForce { get; init; }
    public string RestorerDevice { get; init; } = "cpu";
    public string UpscalerDevice { get; init; } = "auto";
    public string ModelsDirectory { get; init; } = "";
    public string CacheDirectory { get; init; } = "";
    public bool StartOnOpen { get; init; }
    public bool CloseToTray { get; init; }
    public string Language { get; init; } = "";

    internal void Validate(bool checkFiles = true)
    {
        if (KeyFile is null || GpuDevice is null || RestorerDevice is null || UpscalerDevice is null
            || ModelsDirectory is null || CacheDirectory is null || Language is null)
            throw new ArgumentException(Loc.T("The controller settings file is empty or invalid."));
        if (!IPAddress.TryParse(ListenAddress, out _)) throw new ArgumentException(Loc.T("Enter a valid listening IP address."));
        if (Port is < 1024 or > 65535 || WorkerPortStart is < 1024 or > 65535 || WorkerPortEnd < WorkerPortStart || WorkerPortEnd > 65535)
            throw new ArgumentException(Loc.T("Ports must be between 1024 and 65535, with the worker range in order."));
        if (Port >= WorkerPortStart && Port <= WorkerPortEnd) throw new ArgumentException(Loc.T("The listener port must be outside the worker port range."));
        if (Clients is < 1 or > 16 || Clients > WorkerPortEnd - WorkerPortStart + 1)
            throw new ArgumentException(Loc.T("Choose 1 to 16 clients and at least one worker port per client."));
        if (Threads is < 0 or > 63) throw new ArgumentException(Loc.T("DSP helper threads must be between 0 and 63."));
        if (AllowInternet && string.IsNullOrWhiteSpace(KeyFile)) throw new ArgumentException(Loc.T("Internet connections require an access key file."));
        if (!checkFiles) return;
        if (!string.IsNullOrWhiteSpace(KeyFile)) ReadKey(KeyFile);
        if (!string.IsNullOrWhiteSpace(ModelsDirectory) && !Directory.Exists(ModelsDirectory))
            throw new DirectoryNotFoundException(Loc.T("The custom model folder does not exist."));
    }

    internal string[] Arguments()
    {
        Validate();
        var args = new List<string> { "--controller" };
        void Add(string name, string value) { args.Add("--" + name); args.Add(value); }
        Add("listen", ListenAddress.Trim()); Add("port", Port.ToString(CultureInfo.InvariantCulture));
        Add("worker-port-start", WorkerPortStart.ToString(CultureInfo.InvariantCulture));
        Add("worker-port-end", WorkerPortEnd.ToString(CultureInfo.InvariantCulture));
        Add("clients", Clients.ToString(CultureInfo.InvariantCulture)); Add("threads", Threads.ToString(CultureInfo.InvariantCulture));
        if (AllowInternet) args.Add("--allow-internet");
        if (!string.IsNullOrWhiteSpace(KeyFile)) Add("key-file", Path.GetFullPath(KeyFile));
        if (!GpuEnabled) args.Add("--cpu");
        if (!string.IsNullOrWhiteSpace(GpuDevice)) Add("gpu-device", GpuDevice);
        if (!GpuHighPrecision) args.Add("--gpu-fast");
        if (GpuForce) args.Add("--gpu-force");
        Add("restorer-device", RestorerDevice); Add("upscaler-device", UpscalerDevice);
        if (!string.IsNullOrWhiteSpace(ModelsDirectory)) Add("models", Path.GetFullPath(ModelsDirectory));
        if (!string.IsNullOrWhiteSpace(CacheDirectory)) Add("cache", Path.GetFullPath(CacheDirectory));
        return args.ToArray();
    }

    internal static string ReadKey(string path)
    {
        if (new FileInfo(path).Length > 1024) throw new IOException(Loc.T("The access key file must contain a Base64 256-bit key."));
        string key = File.ReadAllText(path).Trim();
        byte[] bytes;
        try { bytes = Convert.FromBase64String(key); }
        catch (FormatException) { throw new IOException(Loc.T("The access key file must contain a Base64 256-bit key.")); }
        bool valid = bytes.Length == 32;
        CryptographicOperations.ZeroMemory(bytes);
        if (!valid) throw new IOException(Loc.T("The access key file must contain a Base64 256-bit key."));
        return key;
    }

    internal static void CreateKey(string path)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(file);
        byte[] key = RandomNumberGenerator.GetBytes(32);
        try { writer.WriteLine(Convert.ToBase64String(key)); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
}

/// <summary>Atomic settings writes avoid losing configuration after an interrupted save.</summary>
internal sealed class ControllerSettingsStore(string directory)
{
    internal string DirectoryPath { get; } = Path.GetFullPath(directory);
    internal string FilePath => Path.Combine(DirectoryPath, "controller.json");

    internal ServerSettings Load()
    {
        if (!File.Exists(FilePath)) return new ServerSettings();
        var settings = JsonSerializer.Deserialize<ServerSettings>(File.ReadAllText(FilePath))
            ?? throw new InvalidDataException(Loc.T("The controller settings file is empty or invalid."));
        settings.Validate(checkFiles: false);
        return settings;
    }

    internal void Save(ServerSettings settings)
    {
        settings.Validate(checkFiles: false);
        Directory.CreateDirectory(DirectoryPath);
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
