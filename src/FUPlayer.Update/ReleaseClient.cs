using System.Net.Http.Headers;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FUPlayer.Update;

public sealed record PlayerRelease(string Tag, Version Version, Uri Archive, long Size, Uri Checksums, string? Digest)
{
    public string ArchiveName => $"FUPlayer-{Tag[1..]}-win-x64.zip";
}

/// <summary>Manual, anonymous requests to the project's stable GitHub release and checksum assets.</summary>
public sealed partial class ReleaseClient(HttpClient http)
{
    public const string Repository = "jerrybeomsoo/FUPlayer";
    public static readonly Uri Latest = new($"https://api.github.com/repos/{Repository}/releases/latest");
    public const long MaximumDownload = 512L * 1024 * 1024;

    public async Task<PlayerRelease> GetLatestAsync(CancellationToken cancellation)
    {
        using var request = Request(Latest);
        using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        byte[] json = await ReadBoundedAsync(await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false), 1024 * 1024, cancellation).ConfigureAwait(false);
        return Parse(json);
    }

    public static PlayerRelease Parse(ReadOnlySpan<byte> json)
    {
        try { return ParseCore(json); }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        { throw new InvalidDataException("GitHub returned invalid release information.", ex); }
    }

    private static PlayerRelease ParseCore(ReadOnlySpan<byte> json)
    {
        using JsonDocument doc = JsonDocument.Parse(json.ToArray());
        JsonElement root = doc.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())
            throw new InvalidDataException("The release is not stable.");
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!StableTag().IsMatch(tag) || !Version.TryParse(tag[1..], out Version? version))
            throw new InvalidDataException("Invalid release version.");
        string archiveName = $"FUPlayer-{tag[1..]}-win-x64.zip";
        JsonElement[] assets = root.GetProperty("assets").EnumerateArray().ToArray();
        JsonElement Asset(string name)
        {
            JsonElement[] matches = assets.Where(a => a.GetProperty("name").GetString() == name).ToArray();
            if (matches.Length != 1) throw new InvalidDataException("The release package or checksum manifest is missing or duplicated.");
            return matches[0];
        }
        JsonElement archive = Asset(archiveName), checksums = Asset("SHA256SUMS.txt");
        Uri Address(JsonElement asset, string name)
        {
            string expected = $"https://github.com/{Repository}/releases/download/{tag}/{name}";
            if (asset.GetProperty("browser_download_url").GetString() != expected)
                throw new InvalidDataException("The download is not a FUPlayer release asset.");
            return new Uri(expected);
        }
        long size = archive.GetProperty("size").GetInt64();
        if (size <= 0 || size > MaximumDownload) throw new InvalidDataException("The update download is too large or empty.");
        string? digest = archive.TryGetProperty("digest", out JsonElement field) ? field.GetString() : null;
        if (digest is not null && (!digest.StartsWith("sha256:", StringComparison.Ordinal) || !HashText().IsMatch(digest[7..])))
            throw new InvalidDataException("Invalid GitHub asset digest.");
        return new(tag, version, Address(archive, archiveName), size, Address(checksums, "SHA256SUMS.txt"), digest);
    }

    public static bool IsNewer(PlayerRelease release, string current)
    {
        string text = current.Split('+')[0];
        string stable = text.Split('-')[0];
        if (!Version.TryParse(stable, out Version? version)) throw new InvalidDataException("Invalid installed version.");
        version = new(version.Major, version.Minor, Math.Max(0, version.Build));
        return release.Version > version || (release.Version == version && text.Contains('-'));
    }

    public async Task<string> DownloadAsync(PlayerRelease release, string work, IProgress<double>? progress, CancellationToken cancellation)
    {
        Directory.CreateDirectory(work);
        string archive = Path.Combine(work, "package.zip");
        using var manifestRequest = Request(release.Checksums);
        using HttpResponseMessage manifest = await http.SendAsync(manifestRequest, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        manifest.EnsureSuccessStatusCode();
        byte[] text = await ReadBoundedAsync(await manifest.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false), 64 * 1024, cancellation).ConfigureAwait(false);
        string expected = ManifestHash(System.Text.Encoding.UTF8.GetString(text), release.ArchiveName);
        if (release.Digest is not null && !release.Digest[7..].Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("GitHub's digest and the release manifest disagree.");
        bool created = false;
        try
        {
            using var request = Request(release.Archive);
            using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long declared && declared != release.Size)
                throw new InvalidDataException("The download size does not match the release.");
            await using Stream input = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
            await using (var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
            {
                created = true;
                using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = new byte[128 * 1024];
                long total = 0;
                long lastProgress = Stopwatch.GetTimestamp();
                while (true)
                {
                    int count = await input.ReadAsync(buffer, cancellation).ConfigureAwait(false);
                    if (count == 0) break;
                    total += count;
                    if (total > release.Size) throw new InvalidDataException("The download exceeded its declared size.");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellation).ConfigureAwait(false);
                    if (progress is not null && (total == release.Size || Stopwatch.GetElapsedTime(lastProgress).TotalMilliseconds >= 100))
                    {
                        progress.Report((double)total / release.Size);
                        lastProgress = Stopwatch.GetTimestamp();
                    }
                }
                if (total != release.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The downloaded update failed SHA-256 verification.");
            }
            UpdateArchive.Inspect(archive);
            return expected;
        }
        catch { if (created && File.Exists(archive)) File.Delete(archive); throw; }
    }

    public static string ManifestHash(string manifest, string name)
    {
        string? found = null;
        foreach (string line in manifest.Split('\n'))
        {
            Match match = ManifestLine().Match(line.TrimEnd('\r'));
            if (!match.Success || match.Groups[2].Value != name) continue;
            if (found is not null) throw new InvalidDataException("Duplicate package checksum.");
            found = match.Groups[1].Value.ToLowerInvariant();
        }
        return found ?? throw new InvalidDataException("The release has no checksum for this package.");
    }

    private static HttpRequestMessage Request(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("FUPlayer", typeof(ReleaseClient).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return request;
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximum, CancellationToken cancellation)
    {
        using (stream)
        using (var output = new MemoryStream())
        {
            byte[] buffer = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(buffer, cancellation).ConfigureAwait(false)) != 0)
            {
                if (output.Length + count > maximum) throw new InvalidDataException("The release response is too large.");
                output.Write(buffer, 0, count);
            }
            return output.ToArray();
        }
    }

    [GeneratedRegex(@"^v\d+\.\d+\.\d+$", RegexOptions.CultureInvariant)] private static partial Regex StableTag();
    [GeneratedRegex(@"^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant)] private static partial Regex HashText();
    [GeneratedRegex(@"^([a-fA-F0-9]{64}) [ *](.+)$", RegexOptions.CultureInvariant)] private static partial Regex ManifestLine();
}
