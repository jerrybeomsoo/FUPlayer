using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using FUPlayer.Core.Audio;
using FUPlayer.Core.Decoding;
using FUPlayer.Core.Upnp;

namespace FUPlayer.Core.Streaming;

/// <summary>
/// Receives browser-permitted float PCM over loopback WebSocket and supplies the DSP engine's live decoder
/// directly in memory. No operating-system audio capture or output device participates in this transport.
/// </summary>
public sealed class BrowserAudioBridge : IDisposable
{
    public const int SampleRate = 48000, Channels = 2, FrameBytes = Channels * sizeof(float);
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly string _secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    private readonly Channel<byte[]> _pcm = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(16)
    { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Channel<string> _controls = Channel.CreateBounded<string>(16);
    private readonly Task _accept;
    private readonly object _gate = new();
    private readonly HashSet<TcpClient> _clients = [];
    private int _producer, _consumer;
    private long _frames, _consumedFrames;
    private volatile string? _error;
    private volatile string? _controlError;
    private int _mediaState, _mediaStateVersion, _actions;
    private volatile BrowserAudioStatus? _audioStatus;
    private volatile BrowserTransport _transport = new();
    private long _discardBefore;
    private int _discontinuity;
    private volatile StreamMetadata? _metadata;
    private volatile bool _persistentAddon;
    // Reader state survives decoder replacement when live DSP settings change.
    private byte[]? _packet;
    private int _offset;

    /// <summary>Reports only browser graph state and element counts, without page or account data.</summary>
    public sealed record BrowserAudioStatus(string Context, int Watched, int Captured);

    /// <summary>Normal service transport state. Durations are seconds and volume is linear from zero to one.</summary>
    public sealed record BrowserTransport(double Position = 0, double Duration = 0, double? Volume = null,
        bool? Shuffle = null, string? Repeat = null);

    public BrowserAudioBridge()
    {
        _listener.Start(4);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        // The caller is normally the UI thread. Socket work and cancellation must never capture its dispatcher.
        _accept = Task.Run(Accept);
    }

    public int Port { get; }
    /// <summary>Private, per-launch browser endpoint. Keep it in the dedicated extension's configuration.</summary>
    public string SocketUri => $"ws://127.0.0.1:{Port}/{_secret}/audio";
    /// <summary>Identifies this owned source to the engine without another network connection.</summary>
    public string StreamUri { get; } = "fuplayer:firefox/" + Guid.NewGuid().ToString("N");
    public bool IsConnected => Volatile.Read(ref _producer) != 0;
    public long ReceivedFrames => Interlocked.Read(ref _frames);
    /// <summary>Includes the decoder's partial packet, so pausing cannot strand PCM ahead of a resume acknowledgement.</summary>
    public long PendingFrames => Math.Max(0, ReceivedFrames - Interlocked.Read(ref _consumedFrames));
    public BrowserAudioStatus? AudioStatus => _audioStatus;
    public BrowserTransport Transport => _transport;
    public int DiscontinuityVersion => Volatile.Read(ref _discontinuity);
    /// <summary>Only the service's published current-track metadata, without account or queue data.</summary>
    public StreamMetadata? Metadata => _metadata;
    /// <summary>True only after the connected add-on reports the native-messaging startup mode.</summary>
    public bool IsPersistentAddon => _persistentAddon;
    public string? Error => _error;
    public string? ControlError => _controlError;
    /// <summary>Normal page playback state, or null before the first media state report.</summary>
    public bool? IsMediaPlaying => Volatile.Read(ref _mediaState) switch { 1 => true, 2 => false, _ => null };
    public int MediaStateVersion => Volatile.Read(ref _mediaStateVersion);
    public bool CanNext => (Volatile.Read(ref _actions) & 1) != 0;
    public bool CanPrevious => (Volatile.Read(ref _actions) & 2) != 0;
    public bool CanSeek => (Volatile.Read(ref _actions) & 4) != 0 && Transport.Duration > 0;
    public bool CanShuffle => (Volatile.Read(ref _actions) & 8) != 0;
    public bool CanRepeat => (Volatile.Read(ref _actions) & 16) != 0;
    public bool CanVolume => (Volatile.Read(ref _actions) & 32) != 0;

    /// <summary>Sends a media action only to this authenticated browser connection.</summary>
    public bool RequestControl(string action, object? value = null)
    {
        bool valid = action switch
        {
            "play" or "pause" or "nexttrack" or "previoustrack" => value is null,
            "seekto" => value is double time && double.IsFinite(time) && time is >= 0 and <= 604800,
            "volume" => value is double volume && double.IsFinite(volume) && volume is >= 0 and <= 1,
            "shuffle" => value is bool,
            "repeat" => value is "off" or "all" or "one",
            _ => false
        };
        if (!valid) throw new ArgumentException("Invalid media action or value.", nameof(action));
        if (!IsConnected) return false;
        _controlError = null;
        return _controls.Writer.TryWrite(JsonSerializer.Serialize(new { action, value }));
    }

    /// <summary>Fits only the managed service tabs to the host's available CSS-pixel area.</summary>
    public bool RequestViewport(int width, int height) => IsConnected && width > 0 && height > 0
        && _controls.Writer.TryWrite(JsonSerializer.Serialize(new { action = "viewport", width, height }));

    /// <summary>Allows provider navigation only after the known-title browser window has been found and presented.</summary>
    public bool RequestWindowReady() => IsConnected && _controls.Writer.TryWrite("{\"action\":\"windowready\"}");

    /// <summary>Opens the single consumer; closing it permits a subsequent playback/settings restart.</summary>
    public IAudioDecoder OpenDecoder()
    {
        ObjectDisposedException.ThrowIf(_stop.IsCancellationRequested, this);
        if (Interlocked.CompareExchange(ref _consumer, 1, 0) != 0) throw new IOException("Browser audio already has a decoder.");
        return new PcmDecoder(this);
    }

    private async Task Accept()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token);
                client.NoDelay = true;
                lock (_gate)
                {
                    if (_clients.Count >= 4) { client.Dispose(); continue; }
                    _clients.Add(client);
                }
                _ = Serve(client);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private async Task Serve(TcpClient client)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            NetworkStream stream = client.GetStream();
            string header = await Header(stream, timeout.Token);
            string[] lines = header.Split("\r\n", StringSplitOptions.None);
            string[] request = lines[0].Split(' ');
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in lines.Skip(1).Where(l => l.Length > 0))
            {
                int colon = line.IndexOf(':');
                if (colon <= 0 || !fields.TryAdd(line[..colon].Trim(), line[(colon + 1)..].Trim()))
                    throw new InvalidDataException("Invalid browser bridge request.");
            }
            if (request.Length != 3 || request[0] != "GET" || request[2] != "HTTP/1.1"
                || fields.GetValueOrDefault("Host") != $"127.0.0.1:{Port}")
                throw new InvalidDataException("Invalid browser bridge target.");
            if (request[1] == $"/{_secret}/audio")
            {
                if (fields.GetValueOrDefault("Upgrade")?.Equals("websocket", StringComparison.OrdinalIgnoreCase) != true
                    || fields.GetValueOrDefault("Sec-WebSocket-Version") != "13"
                    || !fields.GetValueOrDefault("Connection", "").Split(',').Any(s => s.Trim().Equals("Upgrade", StringComparison.OrdinalIgnoreCase))
                    || !Uri.TryCreate(fields.GetValueOrDefault("Origin"), UriKind.Absolute, out var origin) || origin.Scheme != "moz-extension")
                    throw new InvalidDataException("The audio sender must be the browser extension.");
                string key = fields.GetValueOrDefault("Sec-WebSocket-Key", "");
                if (Convert.FromBase64String(key).Length != 16) throw new InvalidDataException("Invalid WebSocket key.");
                if (Interlocked.CompareExchange(ref _producer, 1, 0) != 0) throw new InvalidDataException("An audio sender is already connected.");
                try
                {
                    string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), _stop.Token);
                    using WebSocket socket = WebSocket.CreateFromStream(stream, true, null, TimeSpan.FromSeconds(15));
                    using var connection = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    Task input = Receive(socket, connection.Token), output = SendControls(socket, connection.Token);
                    await Task.WhenAny(input, output);
                    connection.Cancel();
                    await Task.WhenAll(input, output);
                }
                finally { Volatile.Write(ref _producer, 0); }
            }
            else await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), _stop.Token);
        }
        catch (Exception ex) when (ex is IOException or SocketException or WebSocketException or OperationCanceledException or ObjectDisposedException or InvalidDataException or FormatException or JsonException or ChannelClosedException)
        { /* Malformed or disconnected local clients cannot stop the listener. */ }
        finally
        {
            lock (_gate) _clients.Remove(client);
            client.Dispose();
        }
    }

    private async Task SendControls(WebSocket socket, CancellationToken cancel)
    {
        await foreach (string message in _controls.Reader.ReadAllAsync(cancel))
            await socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, cancel);
    }

    private async Task Receive(WebSocket socket, CancellationToken cancel)
    {
        byte[] buffer = new byte[32768];
        while (!_stop.IsCancellationRequested)
        {
            int used = 0;
            WebSocketReceiveResult part;
            do
            {
                part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, used, buffer.Length - used), cancel);
                if (part.MessageType == WebSocketMessageType.Close) return;
                used += part.Count;
                if (part.MessageType == WebSocketMessageType.Text && used > 16384) throw new InvalidDataException("Browser status is too large.");
                if (part.MessageType == WebSocketMessageType.Text && part.EndOfMessage)
                {
                    using var message = JsonDocument.Parse(buffer.AsMemory(0, used));
                    var body = message.RootElement;
                    if (body.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid browser status.");
                    if (body.TryGetProperty("addon", out var addon) && addon.ValueKind == JsonValueKind.Object
                        && addon.TryGetProperty("mode", out var modeValue) && modeValue.ValueKind == JsonValueKind.String)
                        _persistentAddon = modeValue.GetString() == "persistent";
                    if (body.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                    { _error = error.GetString()?[..Math.Min(240, error.GetString()!.Length)]; return; }
                    if (body.TryGetProperty("controlError", out error) && error.ValueKind == JsonValueKind.String)
                        _controlError = error.GetString()?[..Math.Min(240, error.GetString()!.Length)];
                    if (body.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String)
                    {
                        int value = state.GetString() switch { "playing" => 1, "paused" => 2, _ => 0 };
                        if (value != 0 && Interlocked.Exchange(ref _mediaState, value) != value) Interlocked.Increment(ref _mediaStateVersion);
                    }
                    if (body.TryGetProperty("actions", out var actions) && actions.ValueKind == JsonValueKind.Array)
                    {
                        int flags = 0;
                        foreach (var action in actions.EnumerateArray())
                            if (action.ValueKind == JsonValueKind.String)
                                flags |= action.GetString() switch
                                { "nexttrack" => 1, "previoustrack" => 2, "seekto" => 4, "shuffle" => 8, "repeat" => 16, "volume" => 32, _ => 0 };
                        Volatile.Write(ref _actions, flags);
                    }
                    if (body.TryGetProperty("audio", out var audio) && audio.ValueKind == JsonValueKind.Object
                        && audio.TryGetProperty("context", out var context) && context.ValueKind == JsonValueKind.String
                        && audio.TryGetProperty("watched", out var watched) && watched.ValueKind == JsonValueKind.Number && watched.TryGetInt32(out int watchCount)
                        && audio.TryGetProperty("captured", out var captured) && captured.ValueKind == JsonValueKind.Number && captured.TryGetInt32(out int captureCount)
                        && watchCount is >= 0 and <= 4096 && captureCount >= 0 && captureCount <= watchCount
                        && context.GetString() is "none" or "suspended" or "running" or "closed" or "interrupted")
                        _audioStatus = new BrowserAudioStatus(context.GetString()!, watchCount, captureCount);
                    if (body.TryGetProperty("transport", out var transport) && transport.ValueKind == JsonValueKind.Object)
                    {
                        var current = _transport;
                        double duration = Number(transport, "duration", 604800) ?? current.Duration;
                        double position = Math.Min(Number(transport, "position", 604800) ?? current.Position, duration);
                        double? volume = Number(transport, "volume", 1) ?? current.Volume;
                        bool? shuffle = transport.TryGetProperty("shuffle", out var flag) && flag.ValueKind is JsonValueKind.True or JsonValueKind.False
                            ? flag.GetBoolean() : current.Shuffle;
                        string? repeat = transport.TryGetProperty("repeat", out var mode) && mode.ValueKind == JsonValueKind.String
                            && mode.GetString() is "off" or "all" or "one" ? mode.GetString() : current.Repeat;
                        _transport = new BrowserTransport(position, duration, volume, shuffle, repeat);
                    }
                    if (body.TryGetProperty("discontinuity", out var discontinuity) && discontinuity.ValueKind == JsonValueKind.True)
                    {
                        Interlocked.Exchange(ref _discardBefore, ReceivedFrames);
                        Interlocked.Increment(ref _discontinuity);
                    }
                    if (body.TryGetProperty("metadata", out var metadata))
                    {
                        _metadata = metadata.ValueKind == JsonValueKind.Object ? new StreamMetadata
                        {
                            Title = Text(metadata, "title", 512), Artist = Text(metadata, "artist", 512), Album = Text(metadata, "album", 512),
                            Genre = Text(metadata, "genre", 256), AlbumArtUri = Artwork(Text(metadata, "artwork", 4096)),
                            SampleRate = SampleRate, BitsPerSample = 32, Channels = Channels
                        } : null;
                    }
                    break;
                }
                if (part.MessageType is not (WebSocketMessageType.Binary or WebSocketMessageType.Text) || used > buffer.Length || (!part.EndOfMessage && used == buffer.Length))
                    throw new InvalidDataException("Invalid browser PCM packet.");
            } while (!part.EndOfMessage);
            if (part.MessageType == WebSocketMessageType.Text) continue;
            if (used == 0 || used % FrameBytes != 0) throw new InvalidDataException("Incomplete browser PCM frame.");
            for (int i = 0; i < used; i += sizeof(float))
                if (!float.IsFinite(BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(i))))
                    throw new InvalidDataException("Non-finite browser PCM.");
            byte[] packet = buffer.AsSpan(0, used).ToArray();
            // Backpressure retains samples while a pause/control reaches Firefox. Memory stays bounded.
            if (!_pcm.Writer.TryWrite(packet))
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                wait.CancelAfter(TimeSpan.FromSeconds(5));
                try { await _pcm.Writer.WriteAsync(packet, wait.Token); }
                catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
                { _error = "Browser audio reader did not resume within five seconds. Stop and reopen the browser."; return; }
            }
            Interlocked.Add(ref _frames, used / FrameBytes);
        }
    }

    private static double? Number(JsonElement body, string name, double maximum) => body.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) && double.IsFinite(number)
        && number >= 0 && number <= maximum ? number : null;

    private static string? Text(JsonElement body, string name, int maximum) => body.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String && value.GetString() is { } text && !string.IsNullOrWhiteSpace(text)
        ? text[..Math.Min(text.Length, maximum)].Replace('\0', ' ').Trim() : null;

    private static string? Artwork(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 ? uri.AbsoluteUri : null;

    private static async Task<string> Header(Stream stream, CancellationToken cancel)
    {
        byte[] bytes = new byte[8192], one = new byte[1];
        for (int i = 0; i < bytes.Length; i++)
        {
            if (await stream.ReadAsync(one, cancel) == 0) throw new IOException("Incomplete request.");
            bytes[i] = one[0];
            if (i >= 3 && bytes[i - 3] == 13 && bytes[i - 2] == 10 && bytes[i - 1] == 13 && bytes[i] == 10)
                return Encoding.ASCII.GetString(bytes, 0, i + 1);
        }
        throw new InvalidDataException("Request headers are too large.");
    }

    private sealed class PcmDecoder(BrowserAudioBridge owner) : IAudioDecoder, ILiveSource
    {
        private int _disposed;
        public StreamFormat Format => StreamFormat.Pcm(SampleRate, Channels, 32);
        public long Length => -1;
        public long Position { get; private set; }
        public bool CanSeek => false;
        public string CodecName => "Firefox PCM";
        public bool HasEnded => _disposed != 0 || owner._stop.IsCancellationRequested;
        public int ReadPcm(double[][] destination, int offset, int maxFrames)
        {
            if (HasEnded) return 0;
            ref byte[]? packet = ref owner._packet;
            ref int offsetBytes = ref owner._offset;
            int frames = 0;
            while (frames < maxFrames)
            {
                if (packet is null || offsetBytes == packet.Length)
                {
                    if (!owner._pcm.Reader.TryRead(out packet)) break;
                    offsetBytes = 0;
                }
                int take = Math.Min(maxFrames - frames, (packet.Length - offsetBytes) / FrameBytes);
                long discard = Interlocked.Read(ref owner._discardBefore) - (Interlocked.Read(ref owner._consumedFrames) + frames);
                if (discard > 0)
                {
                    int skip = (int)Math.Min(discard, take);
                    offsetBytes += skip * FrameBytes;
                    Interlocked.Add(ref owner._consumedFrames, skip);
                    continue;
                }
                bool muted = owner.Transport.Volume == 0;
                for (int i = 0; i < take; i++)
                    for (int channel = 0; channel < Channels; channel++)
                        destination[channel][offset + frames + i] = muted ? 0 : BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(offsetBytes + i * FrameBytes + channel * 4));
                offsetBytes += take * FrameBytes;
                frames += take;
            }
            Position += frames;
            Interlocked.Add(ref owner._consumedFrames, frames);
            return frames;
        }
        public int ReadDsd(byte[][] destination, int offset, int maxBytes) => throw new NotSupportedException();
        public void Seek(long position) => throw new NotSupportedException();
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Volatile.Write(ref owner._consumer, 0);
        }
    }

    public void Dispose()
    {
        _stop.Cancel(); _listener.Stop(); _pcm.Writer.TryComplete(); _controls.Writer.TryComplete();
        lock (_gate) foreach (TcpClient client in _clients) client.Dispose();
        try { _accept.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        // Active connection tasks still use the cancellation token while unwinding.
    }
}
