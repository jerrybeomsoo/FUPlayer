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
    private readonly int _history;
    private readonly int _own;
    private bool _narrowed;

    /// <summary>
    /// Input samples the device must still be fed before it may be given a phase again. It convolves each output
    /// sample against the <c>TapsPerPhase − 1</c> samples before it, so a run it was left out of leaves a hole in
    /// that tail, and the hole has passed once that many samples have gone in behind it.
    /// </summary>
    /// <remarks>
    /// Counted in samples, not runs. It used to be a number of runs worked out as though every run were as long as
    /// a launch takes, but a run is whatever the pipeline hands over, often an eighth of that, and the device was
    /// then given phases again while its tail still reached back past the hole: wrong samples, for a few blocks
    /// each time the pipeline check took the device back.
    /// </remarks>
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
        _history = filter.Stage.TapsPerPhase - 1;
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
            bool warming = false;
            if (detached)
            {
                // The device's tail of past input now has a hole in it; it convolves against that tail.
                _stale = _history;
            }
            else
            {
                _device.Accept(run);
                if (_stale > 0)
                {
                    // Fed again, but this run is convolved against a tail that still reaches back into the hole.
                    warming = true;
                    lanes = 0;
                    _stale = Math.Max(0, _stale - take);
                }
            }

            _processor.Accept(run);

            long started = Stopwatch.GetTimestamp();
            _device.Begin(0, lanes);
            _processor.Produce(lanes, _up - lanes, _produced);
            _device.Collect(_produced);

            // A run the device was kept out of, for its own sake or because it was let go, says nothing about the
            // division it was meant to be convolved at.
            if (!warming && _stale == 0)
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
