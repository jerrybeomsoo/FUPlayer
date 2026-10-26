using System.Reflection;
using System.Text;
using System.Text.Json;
using System.IO.Compression;
using FUPlayer.Core.Localization;
using FUPlayer.Core.Streaming;

namespace FUPlayer.App.Services;

/// <summary>Stores the signed add-on unchanged, or stages the optional temporary developer preview.</summary>
internal static class FirefoxAudioAddon
{
    internal const string BundledVersion = "0.3.1";
    internal static string InstalledMarker(string settingsDirectory) => Path.Combine(settingsDirectory, "browser", "streaming-addon-installed");
    internal static string PackagePath(string settingsDirectory) => Path.Combine(settingsDirectory, "browser", $"streaming-addon-{BundledVersion}.xpi");

    internal static bool NeedsBundledInstall(string settingsDirectory) =>
        !File.Exists(InstalledMarker(settingsDirectory))
        || !Version.TryParse(File.ReadAllText(InstalledMarker(settingsDirectory)).Trim(), out Version? installed)
        || installed < Version.Parse(BundledVersion);

    internal static Version? PackageVersion(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry("manifest.json");
        if (entry is null || entry.Length > 65536) return null;
        using var stream = entry.Open(); using var manifest = JsonDocument.Parse(stream);
        return manifest.RootElement.TryGetProperty("version", out var value) && value.ValueKind == JsonValueKind.String
            && Version.TryParse(value.GetString(), out Version? version) ? version : null;
    }

    internal static string EnsureBundledPackage(string settingsDirectory)
    {
        string target = PackagePath(settingsDirectory);
        if (File.Exists(target)) return target;
        using var package = Assembly.GetExecutingAssembly().GetManifestResourceStream($"FUPlayer.App.StreamingAddon.fuplayer-streaming-{BundledVersion}.xpi")
            ?? throw new InvalidDataException(Loc.T("Choose a Mozilla-signed FUPlayer streaming add-on."));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using (var output = File.Create(target)) package.CopyTo(output);
        return target;
    }

    internal static string StorePackage(string settingsDirectory, string source)
    {
        using (var archive = ZipFile.OpenRead(source))
        {
            var entry = archive.GetEntry("manifest.json") ?? throw new InvalidDataException(Loc.T("Choose a Mozilla-signed FUPlayer streaming add-on."));
            if (entry.Length > 65536) throw new InvalidDataException(Loc.T("Choose a Mozilla-signed FUPlayer streaming add-on."));
            using var data = entry.Open();
            using var manifest = JsonDocument.Parse(data);
            if (!manifest.RootElement.TryGetProperty("browser_specific_settings", out var settings)
                || !settings.TryGetProperty("gecko", out var gecko) || !gecko.TryGetProperty("id", out var id)
                || id.GetString() != FirefoxNativeMessaging.AddonId
                || !archive.Entries.Any(e => e.FullName.Equals("META-INF/mozilla.rsa", StringComparison.OrdinalIgnoreCase)
                    || e.FullName.Equals("META-INF/cose.sig", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException(Loc.T("Choose a Mozilla-signed FUPlayer streaming add-on."));
        }
        string target = Path.GetFullPath(PackagePath(settingsDirectory));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        // This is a candidate check only. Firefox performs the actual signature verification and installation.
        if (!target.Equals(Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase)) File.Copy(source, target, overwrite: true);
        return target;
    }

    internal static string Prepare(string settingsDirectory, string address, BrowserAudioBridge bridge, string windowTitleMarker)
    {
        Uri service = FirefoxLaunch.Address(address);
        string folder = Path.Combine(settingsDirectory, "browser", "direct-audio-addon");
        Directory.CreateDirectory(folder);
        foreach (string name in new[] { "background.js", "content.js", "worklet.js", "page-controls.js", "host.html" })
        {
            using Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream($"FUPlayer.App.StreamingAddon.{name}")!;
            using var reader = new StreamReader(resource, Encoding.UTF8);
            string text = reader.ReadToEnd();
            if (name == "host.html") text = text.Replace("FUPlayer Streaming | FUPlayer",
                System.Net.WebUtility.HtmlEncode(windowTitleMarker + "FUPlayer"), StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(folder, name), text, new UTF8Encoding(false));
        }
        string origin = service.GetLeftPart(UriPartial.Authority);
        string match = $"{service.Scheme}://{service.Host}/*";
        File.WriteAllText(Path.Combine(folder, "config.js"), "const FUPlayerBridgeReady = Promise.resolve(" + JsonSerializer.Serialize(new
        { socket = bridge.SocketUri, address = service.AbsoluteUri, origin, windowTitleMarker, mode = "temporary" }) + ");", new UTF8Encoding(false));
        var manifest = new
        {
            manifest_version = 2, name = "FUPlayer direct audio preview", version = "0.1.0",
            description = "Sends browser-permitted media PCM directly to FUPlayer. No Windows capture.",
            browser_specific_settings = new { gecko = new { id = "fuplayer-direct-audio@local", strict_min_version = "128.0" } },
            permissions = new[] { match, "tabs", "http://127.0.0.1/*" },
            background = new { scripts = new[] { "config.js", "background.js" } },
            content_scripts = new[]
            {
                new { matches = new[] { match }, js = new[] { "page-controls.js" }, run_at = "document_start", world = "MAIN" },
                new { matches = new[] { match }, js = new[] { "content.js" }, run_at = "document_start", world = "ISOLATED" }
            },
            web_accessible_resources = new[] { "worklet.js" },
            content_security_policy = "script-src 'self'; object-src 'self'; connect-src ws://127.0.0.1:*"
        };
        string path = Path.Combine(folder, "manifest.json");
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        return path;
    }
}
