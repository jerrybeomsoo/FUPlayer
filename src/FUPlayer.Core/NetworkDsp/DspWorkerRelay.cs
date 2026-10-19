using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace FUPlayer.Core.NetworkDsp;

/// <summary>
/// Keeps an isolated worker behind the listener's UDP endpoint. Worker ports bind loopback and never need NAT rules.
/// The listener authenticates admission; the worker authenticates subsequent encrypted datagrams itself.
/// </summary>
internal sealed class DspWorkerRelay(Func<CancellationToken, Task<int>> startWorker, Action<byte[]> send, TimeSpan idle) : IDisposable
{
    private readonly Channel<(byte[] Bytes, bool Close)> _packets = Channel.CreateBounded<(byte[], bool)>(new BoundedChannelOptions(16384)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _lastPacket = Stopwatch.GetTimestamp();
    internal Task Finished => _finished.Task;
    internal void Accept(ReadOnlySpan<byte> wire, bool close = false)
    {
        Interlocked.Exchange(ref _lastPacket, Stopwatch.GetTimestamp());
        _packets.Writer.TryWrite((wire.ToArray(), close));
    }
    internal async Task RunAsync(CancellationToken cancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _stop.Token);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
            { ReceiveBufferSize = 8 << 20, SendBufferSize = 8 << 20 };
        DspEndpoint.IgnoreConnectionReset(socket);
        Task receive = Task.CompletedTask;
        try
        {
            int port = await startWorker(linked.Token).ConfigureAwait(false);
            socket.Connect(IPAddress.Loopback, port);
            long responseAt = Stopwatch.GetTimestamp();
            receive = Task.Run(async () =>
            {
                var buffer = new byte[2048];
                try
                {
                    while (!linked.IsCancellationRequested)
                    {
                        int count = await socket.ReceiveAsync(buffer, SocketFlags.None, linked.Token).ConfigureAwait(false);
                        Interlocked.Exchange(ref responseAt, Stopwatch.GetTimestamp());
                        send(buffer.AsSpan(0, count).ToArray());
                    }
                }
                finally { linked.Cancel(); }
            }, CancellationToken.None);
            while (!linked.IsCancellationRequested)
            {
                using var tick = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                tick.CancelAfter(1000);
                try
                {
                    if (!await _packets.Reader.WaitToReadAsync(tick.Token).ConfigureAwait(false)) break;
                    while (_packets.Reader.TryRead(out var packet))
                    { socket.Send(packet.Bytes); if (packet.Close) return; }
                }
                catch (OperationCanceledException) when (!linked.IsCancellationRequested) { }
                // Responses include echoed client heartbeats, so a dead worker cannot reserve a relay forever.
                if (Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastPacket)) > idle
                    || Stopwatch.GetElapsedTime(Interlocked.Read(ref responseAt)) > TimeSpan.FromSeconds(150)) break;
            }
        }
        finally
        {
            linked.Cancel();
            try { await receive.ConfigureAwait(false); }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException) { }
            _finished.TrySetResult();
        }
    }
    public void Dispose() { _stop.Cancel(); _packets.Writer.TryComplete(); }
}
