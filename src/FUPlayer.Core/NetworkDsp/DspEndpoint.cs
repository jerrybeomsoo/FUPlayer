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
        => addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && (authenticated || IsLan(a)))
            ?? throw new IOException(authenticated ? "The DSP server has no IPv4 DNS address." : "Internet DSP requires the server access key.");

    /// <summary>Loopback, link-local and private addresses: those a plain, unauthenticated session may come from.</summary>
    internal static bool IsLan(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal)
        {
            return true;
        }

        byte[] b = address.GetAddressBytes();
        return b.Length == 16
            ? (b[0] & 0xfe) == 0xfc
            : b[0] == 10 || (b[0] == 192 && b[1] == 168) || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 169 && b[1] == 254);
    }

    /// <summary>
    /// On Windows, a UDP socket that sent to a port with nothing listening reports the ICMP answer as an error on its
    /// next receive; a server would drop the session over a player that has merely gone. Turned off where it applies.
    /// </summary>
    internal static void IgnoreConnectionReset(Socket socket)
    {
        if (OperatingSystem.IsWindows())
        {
            const int SioUdpConnReset = unchecked((int)0x9800000C);
            socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
        }
    }
}
