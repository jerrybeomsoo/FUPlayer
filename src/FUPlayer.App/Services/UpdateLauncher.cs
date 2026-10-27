using System.Diagnostics;
using System.Text.Json;
using FUPlayer.Update;

namespace FUPlayer.App.Services;

internal static class UpdateLauncher
{
    internal static async Task StartAsync(string work, string hash, string settingsDirectory, string language)
    {
        string installed = Path.GetFullPath(AppContext.BaseDirectory);
        string helper = Path.Combine(installed, UpdateRequest.HelperName);
        if (!File.Exists(helper) || !string.Equals(Environment.ProcessPath, Path.Combine(installed, "FUPlayer.exe"), StringComparison.OrdinalIgnoreCase))
            throw new IOException("The portable update helper is unavailable. Extract the complete release ZIP first.");
        string copy = Path.Combine(work, UpdateRequest.HelperName);
        File.Copy(helper, copy);
        using Process parent = Process.GetCurrentProcess();
        var request = new UpdateRequest(1, installed, hash, Environment.ProcessId, parent.StartTime.ToUniversalTime().Ticks,
            ["--settings-dir", settingsDirectory, "--page", "Settings", "--language", language], language);
        string ticket = Path.Combine(work, "request.json");
        File.WriteAllText(ticket, JsonSerializer.Serialize(request));
        var start = new ProcessStartInfo(copy) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = work };
        start.ArgumentList.Add("--apply"); start.ArgumentList.Add(ticket);
        using Process child = Process.Start(start) ?? throw new IOException("The update helper could not start.");
        string reply = Path.Combine(work, "ready.json");
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed.TotalSeconds < 45)
        {
            if (File.Exists(reply))
            {
                // A small reply can be observed just as the helper creates it; retry an incomplete write.
                try
                {
                    using JsonDocument response = JsonDocument.Parse(File.ReadAllText(reply));
                    if (!response.RootElement.TryGetProperty("ready", out JsonElement status) || status.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw new InvalidDataException("The update helper returned an invalid reply.");
                    if (status.GetBoolean())
                    {
                        File.WriteAllText(Path.Combine(work, "install"), "Install and restart requested");
                        return;
                    }
                    string error = response.RootElement.TryGetProperty("error", out JsonElement detail) && detail.ValueKind == JsonValueKind.String
                        ? detail.GetString()! : "The update helper could not prepare the update.";
                    throw new IOException(error);
                }
                catch (JsonException) { }
            }
            if (child.HasExited) throw new IOException("The update helper exited before it was ready. See its result.log.");
            await Task.Delay(100);
        }
        File.WriteAllText(Path.Combine(work, "cancel"), "Update preparation timed out");
        throw new IOException("The update helper did not become ready. Close other instances and try again.");
    }
}
