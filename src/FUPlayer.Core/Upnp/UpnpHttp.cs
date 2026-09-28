using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace FUPlayer.Core.Upnp;

/// <summary>One request a control point sent: the method, the path, the headers and the body.</summary>
internal sealed class UpnpRequest
{
    public required string Method { get; init; }

    public required string Path { get; init; }

    public required Dictionary<string, string> Headers { get; init; }

    public required byte[] Body { get; init; }

    public required IPEndPoint Remote { get; init; }

    public required IPEndPoint Local { get; init; }

    public string? Header(string name) => Headers.TryGetValue(name, out string? value) ? value : null;

    public string BodyText => Encoding.UTF8.GetString(Body);
}

/// <summary>What the device answers with.</summary>
internal sealed record UpnpResponse(int Status, string Reason, string? ContentType = null, byte[]? Body = null)
{
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static UpnpResponse Xml(string xml) =>
        new(200, "OK", "text/xml; charset=\"utf-8\"", Encoding.UTF8.GetBytes(xml));

    public static UpnpResponse Empty(int status, string reason) => new(status, reason);
}

/// <summary>
/// Just enough HTTP/1.1 for a UPnP device: requests with a length or chunked, the methods UPnP adds (SUBSCRIBE,
/// UNSUBSCRIBE), and one request per connection, which every control point copes with. HttpListener would need an
/// administrator's URL reservation to listen on anything but localhost, and a renderer has to be reachable at the
/// address it advertises.
/// </summary>
internal static class UpnpHttp
{
    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxBodyBytes = 1 << 20;

    public const string ServerHeader = "Windows/10 UPnP/1.0 DLNADOC/1.50 FUPlayer/1.0";

    /// <summary>Reads one request, or null when the peer closed the connection or sent something that is not HTTP.</summary>
    public static async Task<UpnpRequest?> ReadAsync(Stream stream, IPEndPoint remote, IPEndPoint local, CancellationToken cancel)
    {
        var head = new MemoryStream();
        var buffer = new byte[4096];
        int headerEnd = -1;
        byte[] data = [];
        while (headerEnd < 0)
        {
            int read = await stream.ReadAsync(buffer, cancel).ConfigureAwait(false);
            if (read <= 0)
            {
                return null;
            }

            head.Write(buffer, 0, read);
            if (head.Length > MaxHeaderBytes)
            {
                return null;
            }

            data = head.GetBuffer();
            headerEnd = IndexOf(data.AsSpan(0, (int)head.Length), "\r\n\r\n"u8);
        }

        string text = Encoding.ASCII.GetString(data, 0, headerEnd);
        string[] lines = text.Split("\r\n");
        string[] first = lines[0].Split(' ', 3);
        if (first.Length < 2)
        {
            return null;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon > 0)
            {
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }

        var body = new MemoryStream();
        int bodyStart = headerEnd + 4;
        body.Write(data, bodyStart, (int)head.Length - bodyStart);

        if (headers.TryGetValue("Transfer-Encoding", out string? encoding) && encoding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            byte[]? chunked = await ReadChunkedAsync(stream, body.ToArray(), cancel).ConfigureAwait(false);
            if (chunked is null)
            {
                return null;
            }

            body = new MemoryStream(chunked);
        }
        else if (headers.TryGetValue("Content-Length", out string? lengthText)
                 && int.TryParse(lengthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int length) && length > 0)
        {
            if (length > MaxBodyBytes)
            {
                return null;
            }

            while (body.Length < length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - body.Length)), cancel).ConfigureAwait(false);
                if (read <= 0)
                {
                    return null;
                }

                body.Write(buffer, 0, read);
            }
        }

        return new UpnpRequest
        {
            Method = first[0].ToUpperInvariant(),
            Path = first[1],
            Headers = headers,
            Body = body.ToArray(),
            Remote = remote,
            Local = local,
        };
    }

    public static async Task WriteAsync(Stream stream, UpnpResponse response, CancellationToken cancel)
    {
        byte[] body = response.Body ?? [];
        var head = new StringBuilder();
        head.Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {response.Status} {response.Reason}\r\n");
        head.Append(CultureInfo.InvariantCulture, $"Date: {DateTime.UtcNow:R}\r\n");
        head.Append("Server: ").Append(ServerHeader).Append("\r\n");
        if (response.ContentType is not null)
        {
            head.Append("Content-Type: ").Append(response.ContentType).Append("\r\n");
        }

        foreach ((string name, string value) in response.Headers)
        {
            head.Append(name).Append(": ").Append(value).Append("\r\n");
        }

        head.Append(CultureInfo.InvariantCulture, $"Content-Length: {body.Length}\r\n");
        head.Append("Connection: close\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), cancel).ConfigureAwait(false);
        if (body.Length > 0)
        {
            await stream.WriteAsync(body, cancel).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancel).ConfigureAwait(false);
    }

    private static async Task<byte[]?> ReadChunkedAsync(Stream stream, byte[] already, CancellationToken cancel)
    {
        var raw = new MemoryStream();
        raw.Write(already);
        var buffer = new byte[4096];
        var body = new MemoryStream();
        int offset = 0;
        while (true)
        {
            byte[] data = raw.GetBuffer();
            int available = (int)raw.Length;
            int lineEnd = IndexOf(data.AsSpan(offset, available - offset), "\r\n"u8);
            if (lineEnd >= 0)
            {
                string sizeText = Encoding.ASCII.GetString(data, offset, lineEnd).Split(';')[0].Trim();
                if (!int.TryParse(sizeText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int size) || size < 0 || body.Length + size > MaxBodyBytes)
                {
                    return null;
                }

                int chunkStart = offset + lineEnd + 2;
                if (size == 0)
                {
                    return body.ToArray();
                }

                if (available >= chunkStart + size + 2)
                {
                    body.Write(data, chunkStart, size);
                    offset = chunkStart + size + 2;
                    continue;
                }
            }

            int read = await stream.ReadAsync(buffer, cancel).ConfigureAwait(false);
            if (read <= 0)
            {
                return null;
            }

            raw.Write(buffer, 0, read);
            if (raw.Length > MaxBodyBytes * 2)
            {
                return null;
            }
        }
    }

    private static int IndexOf(ReadOnlySpan<byte> data, ReadOnlySpan<byte> pattern) => data.IndexOf(pattern);

    /// <summary>Whether an address belongs to this computer, whichever interface it arrived on.</summary>
    public static bool IsLocalMachine(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        IPAddress v4 = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        return LocalAddresses().Any(a => a.Equals(v4));
    }

    /// <summary>
    /// Whether an address is one a home network hands out: the private ranges, link-local, and loopback. A renderer
    /// that plays whatever it is told to fetch has no business taking orders from anything further away.
    /// </summary>
    public static bool IsPrivate(IPAddress address)
    {
        IPAddress v4 = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        if (IPAddress.IsLoopback(v4))
        {
            return true;
        }

        if (v4.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return v4.IsIPv6LinkLocal || v4.IsIPv6SiteLocal || v4.IsIPv6UniqueLocal;
        }

        byte[] b = v4.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254);
    }

    /// <summary>This computer's IPv4 addresses on interfaces that are up, loopback left out.</summary>
    public static IReadOnlyList<IPAddress> LocalAddresses()
    {
        var addresses = new List<IPAddress>();
        foreach (System.Net.NetworkInformation.NetworkInterface nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up
                || nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (System.Net.NetworkInformation.UnicastIPAddressInformation unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(unicast.Address))
                {
                    addresses.Add(unicast.Address);
                }
            }
        }

        return addresses;
    }
}
