using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;

namespace FUPlayer.Core.Streaming;

/// <summary>Supplies only the running player's session to Firefox's allowlisted native-messaging add-on.</summary>
public static class FirefoxNativeMessaging
{
    public const string HostName = "com.fuplayer.streaming";
    public const string AddonId = "fuplayer-direct-audio@local";
    public const string ManifestName = HostName + ".json";
    private const string SessionName = "session.json";
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>The immutable signed add-on receives these values at runtime, never inside its package.</summary>
    public sealed record Session(string Socket, string Address, string Origin, string WindowTitleMarker, string StartupAddress);
    private sealed record Owner(int ProcessId, long StartTicks, string Executable, Session Session);

    /// <summary>Writes the Firefox manifest and a session scoped to this process, outside Firefox profile data.</summary>
    public static string Prepare(string settingsDirectory, string executable, Session session)
    {
        string folder = Path.Combine(Path.GetFullPath(settingsDirectory), "browser", "native-messaging");
        Directory.CreateDirectory(folder);
        string manifest = Path.Combine(folder, ManifestName);
        File.WriteAllText(manifest, JsonSerializer.Serialize(new
        { name = HostName, description = "FUPlayer streaming session", path = Path.GetFullPath(executable), type = "stdio", allowed_extensions = new[] { AddonId } }));
        using var process = Process.GetCurrentProcess();
        File.WriteAllText(Path.Combine(folder, SessionName), JsonSerializer.Serialize(
            new Owner(process.Id, process.StartTime.ToUniversalTime().Ticks, Path.GetFullPath(executable), session), Json));
        return manifest;
    }

    /// <summary>Recognises Firefox's documented manifest-path and add-on-ID arguments before normal player startup.</summary>
    public static bool IsInvocation(string[] args) => args.Length == 2 && args[1] == AddonId
        && Path.GetFileName(args[0]).Equals(ManifestName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads one bounded native request and writes one framed reply, without opening a player or audio device.</summary>
    public static async Task Run(Stream input, Stream output, string manifest, string addonId, Func<int, bool> isOwnedCaller, CancellationToken cancel)
    {
        object reply;
        try
        {
            if (addonId != AddonId || !Path.GetFileName(manifest).Equals(ManifestName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException();
            byte[] prefix = new byte[4];
            await input.ReadExactlyAsync(prefix, cancel);
            int length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
            if (length is <= 0 or > 4096) throw new InvalidDataException();
            byte[] bytes = new byte[length];
            await input.ReadExactlyAsync(bytes, cancel);
            using var request = JsonDocument.Parse(bytes);
            if (!request.RootElement.TryGetProperty("type", out var type) || type.GetString() != "session") throw new InvalidDataException();
            using var registration = JsonDocument.Parse(await File.ReadAllTextAsync(manifest, cancel));
            var root = registration.RootElement;
            if (root.GetProperty("name").GetString() != HostName || root.GetProperty("type").GetString() != "stdio"
                || !root.GetProperty("allowed_extensions").EnumerateArray().Any(id => id.GetString() == AddonId)) throw new InvalidDataException();
            string path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(manifest))!, SessionName);
            var owner = JsonSerializer.Deserialize<Owner>(await File.ReadAllTextAsync(path, cancel), Json) ?? throw new InvalidDataException();
            using var process = Process.GetProcessById(owner.ProcessId);
            if (owner.Session is null || process.HasExited || process.StartTime.ToUniversalTime().Ticks != owner.StartTicks
                || !Path.GetFullPath(process.MainModule!.FileName).Equals(owner.Executable, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFullPath(root.GetProperty("path").GetString()!).Equals(owner.Executable, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFullPath(Environment.ProcessPath!).Equals(owner.Executable, StringComparison.OrdinalIgnoreCase)
                || !isOwnedCaller(owner.ProcessId)) throw new InvalidDataException();
            reply = owner.Session;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException
            or ArgumentException or KeyNotFoundException or System.ComponentModel.Win32Exception)
        { reply = new { error = "FUPlayer has no active streaming session." }; }
        byte[] response = JsonSerializer.SerializeToUtf8Bytes(reply, Json);
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, response.Length);
        await output.WriteAsync(header, cancel);
        await output.WriteAsync(response, cancel);
        await output.FlushAsync(cancel);
    }

    /// <summary>Removes only this process's current session, so a later process cannot inherit its capability.</summary>
    public static void Revoke(string? manifest)
    {
        if (manifest is null) return;
        try
        {
            string path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(manifest))!, SessionName);
            var owner = JsonSerializer.Deserialize<Owner>(File.ReadAllText(path), Json);
            using var process = Process.GetCurrentProcess();
            if (owner?.ProcessId == process.Id && owner.StartTicks == process.StartTime.ToUniversalTime().Ticks) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }
}
