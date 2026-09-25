using System.Diagnostics;
using FUPlayer.Core.Dsp.Resampling;

namespace FUPlayer.Core.Dsp.Acceleration;

/// <summary>
/// One channel of frequency-domain polyphase interpolation convolved by the graphics device and the processor
/// at the same time, each taking some of the phases of every block.
/// </summary>
/// <remarks>
/// <para>
/// A polyphase stage produces its output rate by running the same input through <c>Up</c> independent phases and
/// interleaving them. Those phases are the natural unit to divide: they need the same input spectrum, they do not
/// depend on one another, and each writes into its own slots of the block's output, so two processors can fill in
/// different slots of the same block without ever meeting.
/// </para>
/// <para>
/// The order below is the whole point. The device is handed its phases and asked not to wait; the processor then
/// convolves its own phases of the same block; only afterwards does the caller block on the device. Whatever the
/// device's share was, the processor was busy for all of it. How the share is chosen is
/// <see cref="PhaseBalance"/>'s business, and it is chosen from what the last block actually cost, not from
/// anything assumed about the machine.
/// </para>
/// </remarks>
internal sealed class SplitConvolver : BlockStageState, IDisposable
{
    private readonly GpuConvolver _device;
    private readonly FftPolyphaseStage.PhaseBank _processor;
    private readonly PhaseBalance _balance;
    private readonly int _channel;
    private readonly int _up;
    private readonly int _partitions;
    private readonly int _own;
    private bool _narrowed;

    /// <summary>
    /// Blocks the device must still be fed before it may be given a phase again. Overlap-save convolves each
    /// block against a history of the ones before it, so a device left out of a block has a hole in that history
    /// and cannot be trusted with any phase until the hole has fallen off the end of it.
    /// </summary>
    private int _stale;

    /// <param name="channel">
    /// Which channel of the stage this is. The balance fills the earlier channels first, so a channel's index
    /// decides how much of the division reaches it. The later ones often get none of it, which is the point.
    /// </param>
    /// <param name="own">
    /// Threads this channel would have had to itself with no device in the picture. The wider budget it is
    /// given instead only makes sense while the device is holding whole channels, so it is handed back the
    /// moment the device is let go.
    /// </param>
    public SplitConvolver(GpuPolyphaseFilter filter, int parallelism, PhaseBalance balance, int channel, int own = 0)
        : base(filter.Stage.BlockSamples, filter.Stage.BlockSamples * filter.Stage.Up)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(balance);
        _device = new GpuConvolver(filter);
        _processor = filter.Stage.CreateBank(parallelism);
        _balance = balance;
        _channel = channel;
        _up = filter.Stage.Up;
        _partitions = filter.Stage.Partitions;
        _own = own > 0 ? own : parallelism;
        Reset();
    }

    public void Dispose() => _device.Dispose();

    protected override void ResetState()
    {
        _device.Reset();
        _processor.Reset();

        // Both sides start from silence, so the device's history is complete from the first block.
        _stale = 0;
    }

    protected override void ProcessBlock()
    {
        if (!_narrowed && _balance.Dropped)
        {
            // Nothing of this stage is on the device any more, so the channels stop drawing on each other's threads.
            _narrowed = true;
            _processor.Narrow(_own);
        }

        bool detached = _balance.Detached;
        int share = detached ? 0 : _balance.Begin();
        int lanes = detached ? 0 : _balance.LanesOf(share, _channel);

        // The processor transforms the block for its own phases whether the device is given any or not, so it
        // goes first and the device is handed the spectrum rather than transforming the block over again.
        _processor.Accept(Current);

        // Both sides keep their own spectrum history, so either can be given any phase of any later block, and
        // the device is kept out of it entirely while it is being left out, which is the point.
        bool warming = false;
        if (detached)
        {
            // Its history now has a hole in it, and every block it convolves is convolved against that history.
            _stale = _partitions;
        }
        else
        {
            _device.Accept(_processor.SpectrumRe, _processor.SpectrumIm);
            if (_stale > 0)
            {
                // Fed again but not yet fed enough: the processor keeps every phase until the hole has passed.
                warming = true;
                _stale--;
                lanes = 0;
            }
        }

        long started = Stopwatch.GetTimestamp();
        _device.Begin(0, lanes);
        _processor.Produce(lanes, _up - lanes, Produced);
        _device.Collect(Produced);

        // A block the device was kept out of for its own sake is not a reading about this division, the last block
        // of the wait included: the processor convolved all of it, whatever division it was meant to be.
        if (!warming && _stale == 0)
        {
            _balance.Report(share, Stopwatch.GetElapsedTime(started, Stopwatch.GetTimestamp()).TotalSeconds);
        }
    }
}
