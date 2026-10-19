using System.Collections.Concurrent;

namespace FUPlayer.Core.NetworkDsp;

/// <summary>Overlaps the next ordered server request with local output processing, with at most two blocks in flight.</summary>
internal sealed class DspRemotePrefetch : IDisposable
{
    internal sealed record Result(double[][] Input, int Frames, double ReplayGain, double[][] Output, int[] Produced);
    private sealed record Request(double[][] Input, int Frames, double Gain, TaskCompletionSource<Result> Completion);
    private readonly BlockingCollection<Request> _requests = new(1);
    private readonly CancellationTokenSource _stop = new();
    private readonly DspRemoteClient _client;
    private readonly Task _worker;
    private readonly ConcurrentBag<double[][]> _outputs = new();
    private int _disposed;

    internal DspRemotePrefetch(DspRemoteClient client)
    {
        _client = client;
        _worker = Task.Factory.StartNew(Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    internal Task<Result> Submit(double[][] input, int frames, double gain)
    {
        double[][] copy = input.Select(channel => channel.AsSpan(0, frames).ToArray()).ToArray();
        var completion = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        try { _requests.Add(new Request(copy, frames, gain, completion), _stop.Token); }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        { throw new IOException("DSP prefetch stopped.", ex); }
        return completion.Task;
    }

    private void Run()
    {
        Exception failure = new OperationCanceledException("DSP prefetch stopped.");
        try
        {
            foreach (Request request in _requests.GetConsumingEnumerable(_stop.Token))
            {
                try
                {
                    if (!_outputs.TryTake(out double[][]? output))
                        output = request.Input.Select(_ => new double[_client.Metadata.MaximumOutput]).ToArray();
                    int[] counts = Enumerable.Repeat(request.Frames, request.Input.Length).ToArray();
                    int[] produced = _client.ProcessChannels(DspMessage.CombinedChannels, request.Input, counts, output, request.Gain);
                    request.Completion.TrySetResult(new Result(request.Input, request.Frames, request.Gain, output, produced));
                }
                catch (Exception ex) when (ex is not (OutOfMemoryException or AccessViolationException))
                { request.Completion.TrySetException(ex); failure = ex; break; }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _requests.CompleteAdding();
            while (_requests.TryTake(out Request? pending)) pending.Completion.TrySetException(failure);
        }
    }

    internal void Reuse(double[][] output) => _outputs.Add(output);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel(); _client.Abort();
        _worker.GetAwaiter().GetResult();
        _requests.Dispose(); _stop.Dispose();
    }
}
