using System.Net;
using System.Net.Sockets;

namespace FUPlayer.Core.NetworkDsp;

/// <summary>Normalises a DNS name or pasted URL without treating the DSP transport as HTTP.</summary>
internal readonly record struct DspEndpoint(string Host, int Port)
{
    internal static DspEndpoint Parse(string text, int port)
    {
        text = text.Trim();
        if (text.Length == 0 || port is < 1 or > 65535) throw new IOException("Enter a DSP server address and UDP port.");
        if (!Uri.TryCreate(text.Contains("://", StringComparison.Ordinal) ? text : "udp://" + text, UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("udp" or "http" or "https") || uri.Host.Length == 0
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath is not ("" or "/"))
            throw new IOException("Use a DSP server IP address, domain or udp://host:port URL, without a path.");
        return new DspEndpoint(uri.IdnHost, uri.Port > 0 && !uri.IsDefaultPort ? uri.Port : port);
    }

    internal static IPAddress SelectAddress(IEnumerable<IPAddress> addresses, bool authenticated)
        => addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && (authenticated || UdpDspPeer.IsLan(a)))
            ?? throw new IOException(authenticated ? "The DSP server has no IPv4 DNS address." : "Internet DSP requires the server access key.");
}
