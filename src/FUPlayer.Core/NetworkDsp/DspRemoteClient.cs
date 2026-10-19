using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FUPlayer.Core.Dsp.Resampling;
using FUPlayer.Core.Dsp.Restoration;
using FUPlayer.Core.Engine;
using FUPlayer.Core.Localization;
using FUPlayer.Core.Settings;
namespace FUPlayer.Core.NetworkDsp;

/// <summary>One ordered LAN session shared by the selected expensive stages of a local playback pipeline.</summary>
internal sealed class DspRemoteClient : IDisposable
{
    private Socket _socket = null!;
    private UdpDspPeer _peer = null!;
    private CancellationTokenSource _receiveStop = null!;
    private Task _receiving = Task.CompletedTask;
    private readonly object _gate = new();
    private long _sequence;
    private readonly string _host;
    private int _disposed;
    private Timer? _heartbeat;
    internal DspMetadata Metadata { get; private set; } = null!;
    internal double RoundTripMilliseconds => _peer.RoundTripMilliseconds;
    internal long RetransmittedDatagrams => _peer.RetransmittedDatagrams;
    internal double LastExchangeMilliseconds { get; private set; }
    internal string Summary => Loc.F("DSP server {0} over UDP", _host);

    internal DspRemoteClient(PlaybackPlan plan, PlayerSettings settings, int maximumInput, CancellationToken cancellation = default)
    {
        _host = settings.Processing.NetworkDspHost.Trim();
        if (_host.Length == 0) throw new IOException("Enter a LAN DSP server address.");
        IPAddress address = Dns.GetHostAddressesAsync(_host).WaitAsync(TimeSpan.FromSeconds(5), cancellation).GetAwaiter().GetResult()
            .FirstOrDefault(a => UdpDspPeer.IsLan(a) && a.AddressFamily == AddressFamily.InterNetwork)
            ?? throw new IOException("LAN DSP requires a reachable private IPv4 address.");
        int port = settings.Processing.NetworkDspPort;
        var hello = new DspHello(DspProtocol.Version, plan, settings.Restoration, settings.Processing.RemoteResampling,
            settings.Processing.RemoteRestorer, settings.Processing.RemoteUpscaler, maximumInput);
        try
        {
            for (int redirect = 0; redirect < 2; redirect++)
            {
                Open(new IPEndPoint(address, port));
                using CancellationTokenRegistration registration = cancellation.Register(Abort);
                _peer.Send(DspMessage.Hello, _sequence = 1, DspProtocol.Json(hello));
                var response = _peer.Receive(TimeSpan.FromSeconds(redirect == 0 ? 10 : 120), cancellation);
                if (response.Sequence != 1) throw new InvalidDataException("Invalid DSP hello sequence.");
                if (response.Type == DspMessage.Redirect && response.Bytes.Length == 4 && redirect == 0)
                {
                    port = BitConverter.ToInt32(response.Bytes);
                    if (port is < 1 or > 65535) throw new InvalidDataException("Invalid DSP worker port.");
                    CloseTransport(); continue;
                }
                if (response.Type == DspMessage.Error) throw new IOException(Encoding.UTF8.GetString(response.Bytes));
                if (response.Type != DspMessage.Hello) throw new InvalidDataException("Invalid DSP hello response.");
                Metadata = DspProtocol.Parse<DspMetadata>(response.Bytes);
                if (Metadata.MaximumOutput < 0 || Metadata.MaximumOutput > DspProtocol.MaximumMessage / 8
                    || !double.IsFinite(Metadata.ResamplerDelay) || Metadata.ResamplerDelay < 0)
                    throw new InvalidDataException("Invalid DSP stage capacity.");
                _peer.Probe();
                _heartbeat = new Timer(_ =>
                {
                    try { _peer.Probe(); } catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
                }, null, 1000, 1000);
                return;
            }
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or AccessViolationException))
        { CloseTransport(); throw new IOException(Loc.F("DSP server {0} could not be opened: {1}", _host, ex.Message), ex); }
        throw new IOException("DSP worker redirect failed.");
    }
    private void Open(IPEndPoint endpoint)
    {
        _receiveStop = new CancellationTokenSource();
        _socket = new Socket(endpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp)
            { ReceiveBufferSize = 4 << 20, SendBufferSize = 4 << 20 };
        _socket.Connect(endpoint);
        Socket socket = _socket;
        _peer = new UdpDspPeer(Guid.NewGuid(), packet => socket.Send(packet), server: false);
        UdpDspPeer peer = _peer;
        CancellationToken stop = _receiveStop.Token;
        _receiving = Task.Run(async () =>
        {
            var buffer = new byte[UdpDspPeer.Header + UdpDspPeer.Payload];
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    int count = await socket.ReceiveAsync(buffer, SocketFlags.None, stop).ConfigureAwait(false);
                    peer.Accept(buffer.AsSpan(0, count).ToArray());
                }
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException) { peer.Dispose(); }
        });
    }
    private byte[] Exchange(DspMessage type, byte[] bytes)
    {
        _peer.Probe();
        long started = Stopwatch.GetTimestamp();
        _peer.Send(type, ++_sequence, bytes);
        var response = _peer.Receive(TimeSpan.FromSeconds(8));
        if (response.Sequence != _sequence) throw new InvalidDataException("Invalid DSP block sequence.");
        if (response.Type == DspMessage.Error) throw new IOException(Encoding.UTF8.GetString(response.Bytes));
        if (response.Type != type) throw new InvalidDataException("Unexpected DSP stage response.");
        LastExchangeMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return response.Bytes;
    }
    private int Process(DspMessage type, int channel, ReadOnlySpan<double> input, Span<double> output, ReadOnlySpan<double> right = default)
    {
        lock (_gate)
        {
            using var packet = new MemoryStream(); using var writer = new BinaryWriter(packet);
            writer.Write(channel); writer.Write(input.Length); DspProtocol.WriteSamples(writer, input);
            if (!right.IsEmpty) DspProtocol.WriteSamples(writer, right);
            byte[] response = Exchange(type, packet.ToArray());
            using var reader = new BinaryReader(new MemoryStream(response, false));
            int count = reader.ReadInt32();
            if (count < 0 || count > output.Length || (long)count * 8 != response.Length - 4) throw new InvalidDataException("Invalid DSP sample count.");
            DspProtocol.ReadSamples(reader, output[..count]); return count;
        }
    }
    internal INeuralRestorer CreateRestorer() => new Restorer(this);
    internal int[] ProcessChannels(DspMessage type, double[][] input, int[] counts, double[][] output)
    {
        lock (_gate)
        {
            using var packet = new MemoryStream(); using var writer = new BinaryWriter(packet);
            writer.Write(input.Length);
            for (int c = 0; c < input.Length; c++)
            { writer.Write(counts[c]); DspProtocol.WriteSamples(writer, input[c].AsSpan(0, counts[c])); }
            byte[] response = Exchange(type, packet.ToArray());
            using var reader = new BinaryReader(new MemoryStream(response, false));
            if (reader.ReadInt32() != input.Length) throw new InvalidDataException("Invalid DSP channel count.");
            var produced = new int[input.Length];
            for (int c = 0; c < input.Length; c++)
            {
                int count = reader.ReadInt32();
                if (count < 0 || count > output[c].Length) throw new InvalidDataException("Invalid DSP channel length.");
                DspProtocol.ReadSamples(reader, output[c].AsSpan(0, count)); produced[c] = count;
            }
            if (reader.BaseStream.Position != response.Length) throw new InvalidDataException("Unexpected DSP channel data.");
            return produced;
        }
    }
    internal INeuralUpscaler CreateUpscaler(int channel) => new Upscaler(this, channel);
    internal ResamplerChain CreateResampler(PlaybackPlan plan)
    {
        int input = plan.PreUpsampleRate > 0 ? plan.PreUpsampleRate : plan.ConversionRate;
        return new ResamplerChain(input, plan.ProcessingRate, [new RateStage(this, input, plan.ProcessingRate)], Metadata.Resampler);
    }
    private sealed class Restorer(DspRemoteClient client) : INeuralRestorer
    {
        public int Latency => client.Metadata.RestoreLatency;
        public void Process(Span<double> left, Span<double> right)
        {
            var output = new double[left.Length + right.Length];
            int count = client.Process(DspMessage.Restore, 0, left, output, right);
            if (count != output.Length) throw new InvalidDataException("Restorer changed block length.");
            output.AsSpan(0, left.Length).CopyTo(left); output.AsSpan(left.Length).CopyTo(right);
        }
        public void Reset() { }
    }
    private sealed class Upscaler(DspRemoteClient client, int channel) : INeuralUpscaler
    {
        public int Latency => client.Metadata.UpscaleLatency;
        public void Process(Span<double> samples)
        {
            int count = client.Process(DspMessage.Upscale, channel, samples, samples);
            if (count != samples.Length) throw new InvalidDataException("Upscaler changed block length.");
        }
        public void Reset() { }
    }
    private sealed class RateStage(DspRemoteClient client, int inputRate, int outputRate) : IRateStage
    {
        private int _nextChannel;
        public int InputRate => inputRate;
        public int OutputRate => outputRate;
        public double DelayOutputSamples => client.Metadata.ResamplerDelay;
        public double CostPerOutputSample => client.Metadata.Operations / outputRate;
        public int FlushInputSamples => client.Metadata.FlushInput;
        public string Description => client.Metadata.Resampler;
        public int MaxOutput(int count) => client.Metadata.MaximumOutput;
        public IRateStageState CreateState() => new RateState(client, _nextChannel++);
    }
    private sealed class RateState(DspRemoteClient client, int channel) : IRateStageState
    {
        public int Process(ReadOnlySpan<double> input, Span<double> output) => client.Process(DspMessage.Resample, channel, input, output);
        public void Reset() { }
    }
    internal void Reset() { lock (_gate) _ = Exchange(DspMessage.Reset, []); }
    internal void Abort()
    {
        _peer?.Dispose();
        try { _receiveStop?.Cancel(); } catch (ObjectDisposedException) { }
        _socket?.Dispose();
    }
    private void CloseTransport()
    {
        _heartbeat?.Dispose();
        Abort();
        try { _receiving.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        _receiveStop?.Dispose();
    }
    public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) CloseTransport(); }
}
