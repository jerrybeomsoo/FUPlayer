using System.Diagnostics;
using FUPlayer.Core.Dsp.Resampling;

namespace FUPlayer.Core.Dsp.Acceleration;

/// <summary>
/// One channel of tap-by-tap polyphase interpolation convolved by the graphics device and the processor at the
/// same time, each taking some of the phases of every run of samples.
/// </summary>
/// <remarks>
/// The same division as <see cref="SplitConvolver"/>, and for the same reason, but there is no block to wait for
/// here: the samples are cut into runs only because a launch wants a decent amount of work in it, and each run
/// produces its own output before the next is started. The stage still adds no delay of its own.
/// </remarks>
internal sealed class SplitDirectConvolver : IRateStageState, IDisposable
{
    private readonly GpuDirectConvolver _device;
    private readonly PolyphaseStage.DirectPhaseBank _processor;
    private readonly PhaseBalance _balance;
    private readonly double[] _produced;
    private readonly int _channel;
    private readonly int _up;
    private readonly int _warmUp;
    private readonly int _own;
    private bool _narrowed;

    /// <summary>
    /// Runs the device must still be fed before it may be given a phase again. It convolves each output sample
    /// against the tail of input before it, so a run it was left out of leaves a hole in that tail.
    /// </summary>
    private int _stale;

    /// <param name="channel">Which channel of the stage this is; the balance fills the earlier ones first.</param>
    /// <param name="own">Threads this channel would have had to itself with no device in the picture.</param>
    public SplitDirectConvolver(GpuDirectFilter filter, int parallelism, PhaseBalance balance, int channel, int own = 0)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(balance);
        _device = new GpuDirectConvolver(filter);
        _processor = filter.Stage.CreateBank(parallelism);
        _balance = balance;
        _channel = channel;
        _up = filter.Stage.Up;
        _produced = new double[GpuDirectConvolver.MaxBlock * _up];
        _warmUp = ((filter.Stage.TapsPerPhase + GpuDirectConvolver.MaxBlock - 1) / GpuDirectConvolver.MaxBlock) + 1;
        _own = own > 0 ? own : parallelism;
    }

    public int Process(ReadOnlySpan<double> input, Span<double> output)
    {
        if (!_narrowed && _balance.Dropped)
        {
            // Nothing of this stage is on the device any more, so the channels stop drawing on each other's threads.
            _narrowed = true;
            _processor.Narrow(_own);
        }

        int written = 0;
        int offset = 0;
        while (offset < input.Length)
        {
            int take = Math.Min(GpuDirectConvolver.MaxBlock, input.Length - offset);
            ReadOnlySpan<double> run = input.Slice(offset, take);
            bool detached = _balance.Detached;
            int share = detached ? 0 : _balance.Begin();
            int lanes = detached ? 0 : _balance.LanesOf(share, _channel);
            if (detached)
            {
                // The device's tail of past input now has a hole in it; it convolves against that tail.
                _stale = _warmUp;
            }
            else
            {
                _device.Accept(run);
                if (_stale > 0)
                {
                    _stale--;
                    lanes = 0;
                }
            }

            _processor.Accept(run);

            long started = Stopwatch.GetTimestamp();
            _device.Begin(0, lanes);
            _processor.Produce(lanes, _up - lanes, _produced);
            _device.Collect(_produced);
            if (_stale == 0)
            {
                _balance.Report(share, Stopwatch.GetElapsedTime(started, Stopwatch.GetTimestamp()).TotalSeconds);
            }

            _produced.AsSpan(0, take * _up).CopyTo(output[written..]);
            offset += take;
            written += take * _up;
        }

        return written;
    }

    public void Reset()
    {
        _device.Reset();
        _processor.Reset();
        _stale = 0;
    }

    public void Dispose() => _device.Dispose();
}
