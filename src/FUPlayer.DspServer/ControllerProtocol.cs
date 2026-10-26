using System.Text.Json;

namespace FUPlayer.ServerHost;

/// <summary>A local stdout report; it never changes the network DSP protocol or carries credentials.</summary>
internal sealed record ControllerStatus(int Port, int ActiveClients, int Workers, double CpuPercent, long MemoryBytes)
{
    internal const string Prefix = "FUPlayer-Controller: ";

    internal string Encode() => Prefix + JsonSerializer.Serialize(this);

    internal static ControllerStatus? Read(string line)
    {
        if (!line.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        try
        {
            var status = JsonSerializer.Deserialize<ControllerStatus>(line[Prefix.Length..]);
            return status is { Port: > 0 and <= 65535, ActiveClients: >= 0, Workers: >= 0, MemoryBytes: >= 0 }
                && double.IsFinite(status.CpuPercent) && status.CpuPercent >= 0 ? status : null;
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>Machine-readable choices gathered in a disposable process rather than the waiting controller.</summary>
internal sealed record ControllerDevices(ControllerDevice[] Convolution, string[] Neural);

internal sealed record ControllerDevice(string Id, string Name, string Backend);
