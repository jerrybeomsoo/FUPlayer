using System.Buffers.Binary;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using FUPlayer.Core.Audio;
using FUPlayer.Core.Decoding.Flac;
using FUPlayer.Core.Engine;
using FUPlayer.Core.Localization;

namespace FUPlayer.Core.Decoding.Network;

/// <summary>
/// Decodes an audio stream a network controller hands the player by URL: FLAC, WAV, or raw LPCM (DLNA's
/// <c>audio/L16</c>, and <c>audio/L24</c>), over HTTP, of known length or none at all.
/// </summary>
/// <remarks>
/// <para>
/// A reader thread keeps about a second of the stream ahead of the decoder, so the network's jitter never reaches
/// the engine thread: a read hands out what has arrived and comes back empty, rather than waiting, when nothing has
/// (<see cref="ILiveSource"/>). The engine then waits a moment and asks again, answering commands in between. The
/// look-ahead is kept short on purpose: a controller such as foobar2000 produces its stream as fast as it is taken,
/// so whatever sits here is heard that much later than the controller shows it.
/// </para>
/// <para>
/// Only <c>http</c> and <c>https</c> are opened. The address comes from another device on the network, and a
/// <c>file:</c> address from there must never read this computer's disk.
/// </para>
/// </remarks>
public sealed class NetworkStreamDecoder : IAudioDecoder, ILiveSource
{
    /// <summary>What the look-ahead holds once the format is known, in seconds of audio.</summary>
    private const double AheadSeconds = 1.0;

    private static readonly HttpClient Client = CreateClient();

    private readonly HttpResponseMessage _response;
    private readonly ReadAhead _source;
    private readonly ForwardStream _stream;
    private readonly FlacDecoder? _flac;
    private readonly PcmSampleEncoding _encoding;
    private readonly int _blockAlign;
    private readonly long _lengthFrames;
    private readonly int _flacGate;
    private byte[] _scratch = [];
    private long _position;
    private bool _flacEnded;

    private NetworkStreamDecoder(
        HttpResponseMessage response, ReadAhead source, ForwardStream stream, StreamFormat format, string codec,
        FlacDecoder? flac, PcmSampleEncoding encoding, int blockAlign, long lengthFrames)
    {
        _response = response;
        _source = source;
        _stream = stream;
        _flac = flac;
        _encoding = encoding;
        _blockAlign = blockAlign;
        _lengthFrames = lengthFrames;
        Format = format;
        CodecName = codec;
        ContentType = response.Content.Headers.ContentType?.ToString();

        // Two frames' worth, stored uncompressed: no encoder writes a FLAC frame bigger than its own samples, so a
        // frame can always be decoded once this much has arrived and never has to wait for the network part way.
        int block = flac is null ? 0 : Math.Max(4608, flac.MaxBlockSize);
        _flacGate = flac is null ? 0 : (2 * block * format.Channels * ((format.BitsPerSample + 7) / 8)) + 1024;

        double bytesPerSecond = (double)format.SampleRate * format.Channels * ((format.BitsPerSample + 7) / 8);
        double compression = flac is null ? 1.0 : 0.7;
        source.Target = (int)Math.Clamp(bytesPerSecond * compression * AheadSeconds, 64 * 1024, ReadAhead.MaximumTarget);
    }

    public StreamFormat Format { get; }

    /// <summary>Length in frames when the stream announced one (a WAV file served whole), or −1 for a live stream.</summary>
    public long Length => _lengthFrames;

    public long Position => _position;

    public bool CanSeek => false;

    public string CodecName { get; }

    /// <summary>The Content-Type the server sent, for the page that shows what is arriving.</summary>
    public string? ContentType { get; }

    /// <summary>Seconds of audio waiting in the look-ahead, which is how far behind the sender the player is.</summary>
    public double BufferedSeconds
    {
        get
        {
            double bytesPerSecond = (double)Format.SampleRate * Math.Max(1, _blockAlign);
            if (_flac is not null)
            {
                bytesPerSecond = Format.SampleRate * Format.Channels * ((Format.BitsPerSample + 7) / 8) * 0.6;
            }

            return bytesPerSecond > 0 ? _stream.Buffered / bytesPerSecond : 0.0;
        }
    }

    /// <summary>Reads that found nothing waiting because the network had not delivered it yet.</summary>
    public long StarvedReads { get; private set; }

    public bool HasEnded
    {
        get
        {
            if (_lengthFrames >= 0 && _position >= _lengthFrames)
            {
                return true;
            }

            return _flac is not null
                ? _flacEnded
                : _source.Completed && _stream.Buffered < _blockAlign;
        }
    }

    /// <summary>
    /// Opens a stream: connects, reads the response headers and enough of the body to know the format. Blocks for as
    /// long as that takes, up to <paramref name="timeout"/>.
    /// </summary>
    /// <param name="uri">The stream's address; only http and https are accepted.</param>
    /// <param name="protocolInfo">The controller's protocolInfo for it, when known, as a hint for raw LPCM.</param>
    /// <param name="timeout">How long the server may take to answer and to send the header.</param>
    public static NetworkStreamDecoder Open(string uri, string? protocolInfo = null, TimeSpan? timeout = null)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out Uri? address) || address.Scheme is not ("http" or "https"))
        {
            throw new NotSupportedException(Loc.F("Only http streams can be played from the network, not '{0}'.", uri));
        }

        TimeSpan limit = timeout ?? TimeSpan.FromSeconds(8);
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.TryAddWithoutValidation("transferMode.dlna.org", "Streaming");
        request.Headers.TryAddWithoutValidation("GetContentFeatures.dlna.org", "1");

        HttpResponseMessage response;
        using (var connect = new CancellationTokenSource(limit))
        {
            try
            {
                response = Client.Send(request, HttpCompletionOption.ResponseHeadersRead, connect.Token);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                throw new IOException(Loc.F("The stream at {0} could not be opened: {1}", address, ex.Message), ex);
            }
        }

        if (!response.IsSuccessStatusCode)
        {
            int status = (int)response.StatusCode;
            response.Dispose();
            throw new IOException(Loc.F("The stream at {0} answered {1}.", address, status));
        }

        ReadAhead? source = null;
        try
        {
            source = new ReadAhead(response.Content.ReadAsStream());
            var stream = new ForwardStream(source) { Timeout = limit };
            string mime = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? string.Empty;
            string? hint = protocolInfo?.Split(':') is { Length: >= 3 } fields ? fields[2] : null;

            if (IsRawPcm(mime) || (mime is "" or "application/octet-stream" && IsRawPcm(ContentKind(hint))))
            {
                (StreamFormat format, PcmSampleEncoding encoding) = RawFormat(
                    mime.Length > 0 && IsRawPcm(mime) ? response.Content.Headers.ContentType!.ToString() : hint!);
                return new NetworkStreamDecoder(
                    response, source, stream, format, "LPCM", null, encoding, format.Channels * PcmConversion.BytesPerSample(encoding), -1);
            }

            Span<byte> magic = stackalloc byte[4];
            if (stream.ReadAtLeast(magic, 4, throwOnEndOfStream: false) < 4)
            {
                throw new InvalidDataException(Loc.F("The stream at {0} ended before it began.", address));
            }

            stream.Position -= 4;
            string tag = Encoding.ASCII.GetString(magic);
            switch (tag)
            {
                case "RIFF" or "RF64" or "BW64":
                {
                    (StreamFormat format, PcmSampleEncoding encoding, long dataBytes) = ReadWaveHeader(stream);
                    int blockAlign = format.Channels * PcmConversion.BytesPerSample(encoding);
                    return new NetworkStreamDecoder(
                        response, source, stream, format, "WAV", null, encoding, blockAlign, dataBytes > 0 ? dataBytes / blockAlign : -1);
                }

                case "fLaC":
                {
                    FlacDecoder flac = FlacDecoder.Create(stream);
                    return new NetworkStreamDecoder(response, source, stream, flac.Format, "FLAC", flac, default, 0, flac.Length);
                }

                default:
                    throw new NotSupportedException(
                        mime.Length > 0
                            ? Loc.F("The stream at {0} is {1}; the player takes FLAC, WAV and LPCM streams.", address, mime)
                            : Loc.F("The stream at {0} is in a format that is not recognised; the player takes FLAC, WAV and LPCM streams.", address));
            }
        }
        catch
        {
            source?.Dispose();
            response.Dispose();
            throw;
        }
    }

    public int ReadPcm(double[][] destination, int offset, int maxFrames)
    {
        if (maxFrames <= 0)
        {
            return 0;
        }

        if (_lengthFrames >= 0)
        {
            maxFrames = (int)Math.Min(maxFrames, _lengthFrames - _position);
            if (maxFrames <= 0)
            {
                return 0;
            }
        }

        int frames;
        if (_flac is not null)
        {
            // Only once a frame is sure to be here, or the sender is done, so the decoder never waits on the network.
            if (!_source.Completed && _stream.Buffered < _flacGate)
            {
                StarvedReads++;
                return 0;
            }

            // Nothing from a gate that let the read through means the sender is done, or sent something undecodable.
            frames = _flac.ReadPcm(destination, offset, maxFrames);
            _flacEnded = frames == 0;
        }
        else
        {
            frames = (int)Math.Min(maxFrames, _stream.Buffered / _blockAlign);
            if (frames <= 0)
            {
                StarvedReads += _source.Completed ? 0 : 1;
                return 0;
            }

            int bytes = frames * _blockAlign;
            if (_scratch.Length < bytes)
            {
                _scratch = new byte[bytes];
            }

            int read = _stream.ReadAtLeast(_scratch.AsSpan(0, bytes), bytes, throwOnEndOfStream: false);
            frames = read / _blockAlign;
            PcmConversion.ToPlanar(_scratch.AsSpan(0, frames * _blockAlign), _encoding, Format.Channels, destination, offset, frames);
        }

        _position += frames;
        return frames;
    }

    public int ReadDsd(byte[][] destination, int offset, int maxBytes) =>
        throw new NotSupportedException("Network streams are PCM.");

    public void Seek(long position) =>
        throw new NotSupportedException("A network stream cannot be sought.");

    public void Dispose()
    {
        _source.Dispose();
        _response.Dispose();
    }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            UseCookies = false,
        };

        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FUPlayer/1.0");
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "UPnP/1.0 DLNADOC/1.50");
        return client;
    }

    private static string? ContentKind(string? mimeWithParameters) =>
        mimeWithParameters?.Split(';')[0].Trim().ToLowerInvariant();

    private static bool IsRawPcm(string? mime) => mime is "audio/l16" or "audio/l24" or "audio/lpcm";

    /// <summary>
    /// The format of raw LPCM from its media type, <c>audio/L16;rate=44100;channels=2</c>: big-endian samples, 16
    /// bits for L16 and 24 for L24, stereo at 44.1 kHz where the type leaves either out.
    /// </summary>
    private static (StreamFormat Format, PcmSampleEncoding Encoding) RawFormat(string mediaType)
    {
        string[] parts = mediaType.Split(';');
        string kind = parts[0].Trim().ToLowerInvariant();
        int rate = 44_100;
        int channels = 2;
        foreach (string part in parts.Skip(1))
        {
            string[] pair = part.Split('=', 2);
            if (pair.Length != 2)
            {
                continue;
            }

            string name = pair[0].Trim().ToLowerInvariant();
            string value = pair[1].Trim().Trim('"');
            if (name == "rate" && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int r) && r > 0)
            {
                rate = r;
            }
            else if (name == "channels" && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int c) && c is > 0 and <= 32)
            {
                channels = c;
            }
        }

        return kind == "audio/l24"
            ? (StreamFormat.Pcm(rate, channels, 24), PcmSampleEncoding.Int24Be)
            : (StreamFormat.Pcm(rate, channels, 16), PcmSampleEncoding.Int16Be);
    }

    /// <summary>
    /// Reads a WAVE header off the front of a stream and leaves it at the first sample. A live stream has no length
    /// to announce, so its sizes are zero or all ones, and both mean the data goes on until the sender stops.
    /// </summary>
    private static (StreamFormat Format, PcmSampleEncoding Encoding, long DataBytes) ReadWaveHeader(Stream stream)
    {
        Span<byte> head = stackalloc byte[12];
        stream.ReadExactly(head);
        if (Encoding.ASCII.GetString(head[8..12]) != "WAVE")
        {
            throw new InvalidDataException("The stream is a RIFF file but not a WAVE one.");
        }

        int channels = 0, rate = 0, bits = 0, validBits = 0, formatTag = 0;
        long ds64Data = -1;
        Span<byte> chunk = stackalloc byte[8];
        for (int chunks = 0; chunks < 64; chunks++)
        {
            stream.ReadExactly(chunk);
            string id = Encoding.ASCII.GetString(chunk[..4]);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            if (id == "data")
            {
                if (channels <= 0 || rate <= 0)
                {
                    throw new InvalidDataException("The WAVE stream's data comes before its format.");
                }

                PcmSampleEncoding encoding = (formatTag, bits) switch
                {
                    (1, 8) => PcmSampleEncoding.UInt8,
                    (1, 16) => PcmSampleEncoding.Int16Le,
                    (1, 24) => PcmSampleEncoding.Int24Le,
                    (1, 32) => PcmSampleEncoding.Int32Le,
                    (3, 32) => PcmSampleEncoding.Float32Le,
                    (3, 64) => PcmSampleEncoding.Float64Le,
                    _ => throw new NotSupportedException($"WAVE format tag {formatTag} with {bits} bits is not decoded."),
                };

                long dataBytes = size is 0 or 0xFFFFFFFF ? ds64Data : size;

                // A sender that does not know the length often writes the largest size it can rather than zero.
                if (dataBytes >= 0x7FFF0000)
                {
                    dataBytes = -1;
                }

                int reported = PcmConversion.IsFloat(encoding) ? bits : Math.Clamp(validBits > 0 ? validBits : bits, 1, bits);
                return (StreamFormat.Pcm(rate, channels, reported), encoding, dataBytes);
            }

            if (size > 1 << 20)
            {
                throw new InvalidDataException($"The WAVE stream has a '{id}' chunk of {size:N0} bytes before its data.");
            }

            byte[] body = new byte[size + (size & 1)];
            stream.ReadExactly(body);
            if (id == "fmt " && size >= 16)
            {
                formatTag = BinaryPrimitives.ReadUInt16LittleEndian(body);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(2));
                rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(14));
                validBits = bits;
                if (formatTag == 0xFFFE && size >= 40)
                {
                    validBits = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(18));
                    formatTag = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(24));
                }
            }
            else if (id == "ds64" && size >= 16)
            {
                ds64Data = (long)BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(8));
            }
        }

        throw new InvalidDataException("The WAVE stream has no data chunk.");
    }

    /// <summary>
    /// Reads the response body on its own thread into a ring, never more than <see cref="Target"/> bytes ahead of the
    /// decoder, so the sender is held back by the network rather than piling up audio here.
    /// </summary>
    private sealed class ReadAhead : IDisposable
    {
        public const int MaximumTarget = 3 << 20;

        private readonly Stream _body;
        private readonly SpscByteRing _ring = new(4 << 20);
        private readonly Thread _thread;
        private readonly AutoResetEvent _arrived = new(false);
        private readonly AutoResetEvent _taken = new(false);
        private volatile bool _completed;
        private volatile bool _stopping;
        private volatile int _target = 512 * 1024;

        public ReadAhead(Stream body)
        {
            _body = body;
            _thread = new Thread(Run) { IsBackground = true, Name = "FUPLAYER network stream" };
            _thread.Start();
        }

        public int Target
        {
            get => _target;
            set => _target = Math.Clamp(value, 16 * 1024, MaximumTarget);
        }

        /// <summary>The sender closed the stream or it failed, and everything it sent is in the ring.</summary>
        public bool Completed => _completed;

        public long Available => _ring.Count;

        /// <summary>Waits for at least one byte, or for the end; false when neither came in time.</summary>
        public bool WaitForData(TimeSpan timeout)
        {
            long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
            while (_ring.Count == 0 && !_completed)
            {
                long left = deadline - Environment.TickCount64;
                if (left <= 0)
                {
                    return false;
                }

                _arrived.WaitOne((int)Math.Min(left, 50));
            }

            return _ring.Count > 0;
        }

        public int Read(Span<byte> destination)
        {
            int read = _ring.Read(destination);
            if (read > 0)
            {
                _taken.Set();
            }

            return read;
        }

        public void Dispose()
        {
            _stopping = true;
            _taken.Set();
            try
            {
                _body.Dispose();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // Closing a connection mid-read may complain; it is closed either way.
            }

            _thread.Join(2000);
        }

        private void Run()
        {
            var chunk = new byte[64 * 1024];
            try
            {
                while (!_stopping)
                {
                    int room = _target - (int)_ring.Count;
                    if (room < 4096)
                    {
                        _taken.WaitOne(20);
                        continue;
                    }

                    int read = _body.Read(chunk, 0, Math.Min(chunk.Length, room));
                    if (read <= 0)
                    {
                        break;
                    }

                    int written = 0;
                    while (written < read && !_stopping)
                    {
                        written += _ring.Write(chunk.AsSpan(written, read - written));
                        if (written < read)
                        {
                            _taken.WaitOne(20);
                        }
                    }

                    _arrived.Set();
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or HttpRequestException or OperationCanceledException)
            {
                // The sender went away or the stream was closed here; what arrived is still played.
            }
            finally
            {
                _completed = true;
                _arrived.Set();
            }
        }
    }

    /// <summary>
    /// The look-ahead as a stream that can be read forwards and stepped a little way back, which is all the FLAC and
    /// WAVE header readers need of a file: they peek at a few bytes and then skip over whole blocks.
    /// </summary>
    private sealed class ForwardStream(ReadAhead source) : Stream
    {
        private const int HistorySize = 1 << 16;

        private readonly byte[] _history = new byte[HistorySize];
        private long _position;
        private long _end;

        /// <summary>How long a read may wait for the network before the stream is taken to have ended.</summary>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>Bytes that can be read without waiting.</summary>
        public long Buffered => (_end - _position) + source.Available;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _position;
            set
            {
                if (value < _position)
                {
                    if (_end - value > HistorySize)
                    {
                        throw new NotSupportedException("A network stream can only step back a little way.");
                    }

                    _position = value;
                    return;
                }

                Span<byte> skip = stackalloc byte[4096];
                while (_position < value)
                {
                    int read = Read(skip[..(int)Math.Min(skip.Length, value - _position)]);
                    if (read <= 0)
                    {
                        throw new EndOfStreamException();
                    }
                }
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty)
            {
                return 0;
            }

            // Bytes already read once, handed out again after a step back.
            if (_position < _end)
            {
                int replay = (int)Math.Min(buffer.Length, _end - _position);
                for (int i = 0; i < replay; i++)
                {
                    buffer[i] = _history[(int)((_position + i) % HistorySize)];
                }

                _position += replay;
                return replay;
            }

            if (!source.WaitForData(Timeout))
            {
                return 0;
            }

            int read = source.Read(buffer);
            for (int i = 0; i < read; i++)
            {
                _history[(int)((_end + i) % HistorySize)] = buffer[i];
            }

            _end += read;
            _position = _end;
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
