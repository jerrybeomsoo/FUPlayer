using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace FUPlayer.Core.Upnp;

/// <summary>
/// The discovery half of a UPnP device (SSDP): says the renderer is here when it starts and every so often after,
/// answers control points that search for one, and says goodbye when it stops.
/// </summary>
/// <remarks>
/// The listening socket shares UDP port 1900 with Windows' own SSDP service, which holds it for the devices Windows
/// hosts; with the address reuse option both receive the multicast searches. The announcements go out on every
/// IPv4 interface with that interface's own address in them, and multicast loopback, which Windows leaves on,
/// delivers them to a control point on this same computer, which is where foobar2000 usually is.
/// </remarks>
internal sealed class SsdpServer : IDisposable
{
    public const int MaxAge = 1800;

    private static readonly IPAddress Group = IPAddress.Parse("239.255.255.250");
    private const int Port = 1900;

    private readonly string _udn;
    private readonly string[] _types;
    private readonly Func<IPAddress, string> _location;
    private readonly Func<IPAddress, bool> _mayAnswer;
    private readonly bool _announce;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<(IPAddress Address, IPAddress Mask, Socket Socket)> _interfaces = [];
    private Socket? _listener;
    private Task? _receiving;
    private Task? _announcing;

    /// <param name="udn">The device's unique name, <c>uuid:…</c>.</param>
    /// <param name="deviceType">The device type, which is also what most searches ask for.</param>
    /// <param name="serviceTypes">The services the device offers, each of which is announced as well.</param>
    /// <param name="location">The description URL to hand out for a given local interface address.</param>
    /// <param name="mayAnswer">Whether a search from this address gets an answer at all.</param>
    /// <param name="announce">Announce on the network unasked; off, the device only answers searches.</param>
    public SsdpServer(
        string udn, string deviceType, IEnumerable<string> serviceTypes, Func<IPAddress, string> location,
        Func<IPAddress, bool> mayAnswer, bool announce, Action<string> log)
    {
        _udn = udn;
        _types = ["upnp:rootdevice", udn, deviceType, .. serviceTypes];
        _location = location;
        _mayAnswer = mayAnswer;
        _announce = announce;
        _log = log;
    }

    public void Start()
    {
        var listener = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.Bind(new IPEndPoint(IPAddress.Any, Port));

        foreach ((IPAddress address, IPAddress mask) in Interfaces())
        {
            try
            {
                listener.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Group, address));
                var sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                sender.Bind(new IPEndPoint(address, 0));
                sender.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
                sender.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 4);
                sender.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
                _interfaces.Add((address, mask, sender));
            }
            catch (SocketException ex)
            {
                _log($"SSDP: interface {address} skipped ({ex.SocketErrorCode})");
            }
        }

        if (_interfaces.Count == 0)
        {
            listener.Dispose();
            throw new InvalidOperationException("No network interface could join the UPnP discovery group.");
        }

        _listener = listener;
        _receiving = Task.Run(ReceiveLoop);
        if (_announce)
        {
            _announcing = Task.Run(AnnounceLoop);
        }
    }

    public void Dispose()
    {
        if (_listener is null)
        {
            return;
        }

        _stop.Cancel();
        if (_announce)
        {
            foreach ((IPAddress address, _, Socket socket) in _interfaces)
            {
                foreach (string type in _types)
                {
                    Send(socket, Notify(address, type, alive: false), new IPEndPoint(Group, Port));
                }
            }
        }

        _listener.Dispose();
        foreach ((_, _, Socket socket) in _interfaces)
        {
            socket.Dispose();
        }

        _interfaces.Clear();
        _listener = null;
        try
        {
            Task.WaitAll([.. new[] { _receiving, _announcing }.OfType<Task>()], 1000);
        }
        catch (AggregateException)
        {
            // Closed sockets end both loops, some of them with an exception.
        }
    }

    /// <summary>Up, IPv4, with an address, and able to multicast: the interfaces a control point could be on.</summary>
    private static IEnumerable<(IPAddress Address, IPAddress Mask)> Interfaces()
    {
        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || !nic.SupportsMulticast
                || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            foreach (UnicastIPAddressInformation unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(unicast.Address))
                {
                    yield return (unicast.Address, unicast.IPv4Mask);
                }
            }
        }
    }

    private async Task AnnounceLoop()
    {
        try
        {
            // Three times at the start, a moment apart, because UDP drops packets and a control point that misses
            // the first announcement would otherwise not see the device until its next search.
            for (int round = 0; !_stop.IsCancellationRequested; round++)
            {
                foreach ((IPAddress address, _, Socket socket) in _interfaces.ToArray())
                {
                    foreach (string type in _types)
                    {
                        Send(socket, Notify(address, type, alive: true), new IPEndPoint(Group, Port));
                    }
                }

                await Task.Delay(round < 2 ? TimeSpan.FromMilliseconds(300) : TimeSpan.FromSeconds(60), _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ReceiveLoop()
    {
        var buffer = new byte[8192];
        Socket? listener = _listener;
        while (listener is not null && !_stop.IsCancellationRequested)
        {
            SocketReceiveFromResult received;
            try
            {
                received = await listener.ReceiveFromAsync(buffer, new IPEndPoint(IPAddress.Any, 0), _stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                // A reset from an earlier reply's destination; the socket is still good.
                continue;
            }

            if (received.RemoteEndPoint is not IPEndPoint from)
            {
                continue;
            }

            string message = Encoding.ASCII.GetString(buffer, 0, received.ReceivedBytes);
            if (!message.StartsWith("M-SEARCH", StringComparison.OrdinalIgnoreCase) || !_mayAnswer(from.Address))
            {
                continue;
            }

            Dictionary<string, string> headers = Headers(message);
            if (!headers.TryGetValue("ST", out string? target)
                || !(headers.TryGetValue("MAN", out string? man) && man.Contains("ssdp:discover", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            string[] matches = target == "ssdp:all"
                ? _types
                : _types.Where(t => string.Equals(t, target, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 0)
            {
                continue;
            }

            (IPAddress local, _, Socket socket) = Route(from.Address);
            foreach (string type in matches)
            {
                Send(socket, SearchResponse(local, type), from);
            }
        }
    }

    /// <summary>The interface a reply to this address goes out on: the one on its subnet, or the first.</summary>
    private (IPAddress Address, IPAddress Mask, Socket Socket) Route(IPAddress remote)
    {
        byte[] target = remote.GetAddressBytes();
        foreach ((IPAddress address, IPAddress mask, Socket socket) entry in _interfaces)
        {
            byte[] a = entry.address.GetAddressBytes();
            byte[] m = entry.mask.GetAddressBytes();
            if (target.Length == 4 && Enumerable.Range(0, 4).All(i => (a[i] & m[i]) == (target[i] & m[i])))
            {
                return entry;
            }
        }

        return _interfaces[0];
    }

    private string Notify(IPAddress address, string type, bool alive)
    {
        var text = new StringBuilder();
        text.Append("NOTIFY * HTTP/1.1\r\n");
        text.Append("HOST: 239.255.255.250:1900\r\n");
        if (alive)
        {
            text.Append(CultureInfo.InvariantCulture, $"CACHE-CONTROL: max-age={MaxAge}\r\n");
            text.Append("LOCATION: ").Append(_location(address)).Append("\r\n");
            text.Append("SERVER: ").Append(UpnpHttp.ServerHeader).Append("\r\n");
        }

        text.Append("NT: ").Append(type).Append("\r\n");
        text.Append("NTS: ").Append(alive ? "ssdp:alive" : "ssdp:byebye").Append("\r\n");
        text.Append("USN: ").Append(Usn(type)).Append("\r\n\r\n");
        return text.ToString();
    }

    private string SearchResponse(IPAddress address, string type)
    {
        var text = new StringBuilder();
        text.Append("HTTP/1.1 200 OK\r\n");
        text.Append(CultureInfo.InvariantCulture, $"CACHE-CONTROL: max-age={MaxAge}\r\n");
        text.Append(CultureInfo.InvariantCulture, $"DATE: {DateTime.UtcNow:R}\r\n");
        text.Append("EXT:\r\n");
        text.Append("LOCATION: ").Append(_location(address)).Append("\r\n");
        text.Append("SERVER: ").Append(UpnpHttp.ServerHeader).Append("\r\n");
        text.Append("ST: ").Append(type).Append("\r\n");
        text.Append("USN: ").Append(Usn(type)).Append("\r\n\r\n");
        return text.ToString();
    }

    private string Usn(string type) => type == _udn ? _udn : $"{_udn}::{type}";

    private static void Send(Socket socket, string message, IPEndPoint to)
    {
        try
        {
            socket.SendTo(Encoding.ASCII.GetBytes(message), to);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // Discovery is best effort: the next announcement or search tries again.
        }
    }

    private static Dictionary<string, string> Headers(string message)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in message.Split("\r\n").Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon > 0)
            {
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }

        return headers;
    }
}
