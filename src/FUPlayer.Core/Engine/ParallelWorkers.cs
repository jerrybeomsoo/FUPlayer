using System.Runtime.ExceptionServices;

namespace FUPlayer.Core.Engine;

/// <summary>
/// Dedicated worker threads that run per-channel DSP in parallel. The calling thread participates, so
/// <c>N</c> extra threads give <c>N + 1</c> way parallelism with no thread-pool scheduling jitter.
/// </summary>
public sealed class ParallelWorkers : IDisposable
{
    private readonly Thread[] _threads;
    private readonly ManualResetEventSlim[] _wake;
    private readonly ManualResetEventSlim _finished = new(false);
    private Action<int>? _body;
    private int _count;
    private int _next;
    private int _activeWorkers;
    private Exception? _error;
    private volatile bool _disposed;

    public ParallelWorkers(int extraThreads)
    {
        extraThreads = Math.Clamp(extraThreads, 0, 64);
        _threads = new Thread[extraThreads];
        _wake = new ManualResetEventSlim[extraThreads];
        for (int i = 0; i < extraThreads; i++)
        {
            int id = i;
            _wake[i] = new ManualResetEventSlim(false);
            _threads[i] = new Thread(() => WorkerLoop(id))
            {
                IsBackground = true,
                Name = $"FUPLAYER DSP worker {i + 1}",
                Priority = ThreadPriority.AboveNormal,
            };
            _threads[i].Start();
        }
    }

    public int Parallelism => _threads.Length + 1;

    /// <summary>Extra threads for automatic configuration: one per channel beyond the first, bounded by the core count.</summary>
    public static int AutomaticExtraThreads(int channels) =>
        Math.Clamp(Math.Min(channels, Environment.ProcessorCount) - 1, 0, 15);

    /// <summary>Runs <paramref name="body"/> for indices 0..count-1 and waits for completion.</summary>
    public void For(int count, Action<int> body)
    {
        if (count <= 0)
        {
            return;
        }

        int helpers = Math.Min(_threads.Length, count - 1);
        if (helpers == 0 || _disposed)
        {
            for (int i = 0; i < count; i++)
            {
                body(i);
            }

            return;
        }

        _body = body;
        _count = count;
        _next = -1;
        _error = null;
        _finished.Reset();
        Volatile.Write(ref _activeWorkers, helpers);
        for (int w = 0; w < helpers; w++)
        {
            _wake[w].Set();
        }

        Drain();
        _finished.Wait();
        _body = null;
        if (_error is not null)
        {
            ExceptionDispatchInfo.Throw(_error);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (ManualResetEventSlim wake in _wake)
        {
            wake.Set();
        }

        bool stopped = true;
        foreach (Thread thread in _threads)
        {
            stopped &= thread.Join(500);
        }

        // Changing the DSP thread count replaces this whole set, so the handles have to go back: leaving them to
        // the finaliser would accumulate one per worker every time the setting is touched. A worker still inside
        // a block would fault on a disposed handle, though, so in that case the handles are left to the finaliser.
        if (!stopped)
        {
            return;
        }

        foreach (ManualResetEventSlim wake in _wake)
        {
            wake.Dispose();
        }

        _finished.Dispose();
    }

    private void Drain()
    {
        Action<int>? body = _body;
        if (body is null)
        {
            return;
        }

        int index;
        while ((index = Interlocked.Increment(ref _next)) < _count)
        {
            try
            {
                body(index);
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref _error, ex, null);
            }
        }
    }

    private void WorkerLoop(int id)
    {
        while (true)
        {
            _wake[id].Wait();
            _wake[id].Reset();
            if (_disposed)
            {
                return;
            }

            Drain();
            if (Interlocked.Decrement(ref _activeWorkers) == 0)
            {
                _finished.Set();
            }
        }
    }
}
