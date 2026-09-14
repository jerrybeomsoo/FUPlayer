namespace FUPlayer.Core.Dsp.Acceleration;

/// <summary>
/// How much of a stage's filtering the graphics device is given, block by block, so that it and the processor
/// finish sooner together than either could alone.
/// </summary>
/// <remarks>
/// <para>
/// The work is counted in phases. A polyphase stage runs the same input through <c>Up</c> independent phases per
/// channel and interleaves them, so a conversion of two channels at sixteen phases has thirty-two pieces of work
/// in a block, and the device can be given anything from none of them to all of them.
/// </para>
/// <para>
/// They are handed over a channel at a time: the first phases go to the first channel, and only once that
/// channel is wholly on the device does the next one start giving any up. That ordering matters more than it
/// looks. A channel with no phases left of its own never waits for the device at all, so its thread carries
/// straight on into everything that comes after the filter (the modulator, the dither, the writing out) while
/// the device is still working. Spreading the same share evenly over the channels would leave every one of them
/// waiting instead, and is measurably worse.
/// </para>
/// <para>
/// Nothing here is told how fast anything is. It times a block at every division, keeps the times, and uses
/// whichever was quickest, going back over them now and then in case the machine is busier or idler than it was.
/// Measuring the whole range rather than reasoning towards a balance point is necessary, not lazy: a launch
/// costs the device about the same whether it was given one phase or twenty, so a small share is
/// disproportectionately bad value and the cost of a division is not a straight line between its two ends.
/// </para>
/// <para>
/// A division is remembered by the quickest it was ever seen to be, because a block is only ever made slower by
/// whatever else the machine was doing and never faster. And a division has to beat leaving the device alone by
/// a clear margin before it is used at all, because the difference between a small gain and none is not
/// something a noisy machine can be trusted to report. If nothing does, the device is let go for the rest of the
/// stream and stops costing anything at all.
/// </para>
/// <para>
/// Then the winner is put to a second question, and a different one. Timing the filter answers "which division
/// convolves this quickest", which is what choosing between thirty-odd of them needs; it does not answer
/// "which division plays this quickest", and on a laptop whose card and cores share one power budget the two
/// disagree: the filter finishes sooner divided while the modulator and everything after it finish later, and
/// the block as a whole is longer. So whole pipeline blocks are timed at the winning division and at none at
/// all, and the device is kept only if the pipeline agrees. Two divisions rather than thirty, because a
/// pipeline block can only be timed as often as the filter's block comes round.
/// </para>
/// </remarks>
internal sealed class PhaseBalance
{
    /// <summary>Timings taken at each division before moving to the next.</summary>
    private const int TrialBlocks = 6;

    /// <summary>Timings between one going-back-over and the next: half a minute of audio or so.</summary>
    private const int RecheckBlocks = 512;

    /// <summary>Divisions either side of the settled one that a recheck times again.</summary>
    private const int RecheckSpread = 2;

    /// <summary>Rechecks of the neighbourhood before everything is forgotten and timed again from scratch.</summary>
    private const int RechecksPerSweep = 8;

    /// <summary>How much quicker than leaving the device alone a division has to be before it is worth using.</summary>
    private const double Margin = 0.95;

    /// <summary>Divisions the pipeline check compares: the one the filter timings chose, and none at all.</summary>
    private const int Candidates = 2;

    /// <summary>
    /// Readings taken of each of those two. Fewer than a division gets in the sweep, because half of them are
    /// taken with the device left out, and coming back from that costs it a partition's worth of blocks with
    /// no phases while its history fills in again.
    /// </summary>
    private const int ConfirmTrials = 4;

    private readonly Lock _gate = new();
    private readonly int _up;
    private readonly int _channels;
    private readonly int _slots;
    private readonly bool _fixed;
    private readonly bool _sweep;
    private readonly bool _recheck;
    private readonly double[] _cost;
    private readonly bool[] _pending;
    private readonly double[] _confirmCost = new double[Candidates];
    private int _current;
    private int _reports;
    private double _worst;
    private int _trials;
    private int _since;
    private int _rechecks;
    private bool _measuring = true;
    private bool _dropped;
    private bool _watched;
    private bool _sweeping = true;
    private bool _confirming;
    private int _candidate;
    private int _confirmIndex;
    private int _confirmTrials;

    /// <param name="channels">Channels the stage will be convolving.</param>
    /// <param name="up">Phases each channel has.</param>
    /// <param name="advantage">
    /// How many times faster the device was than the processor in the opening race. It decides only where the
    /// timing starts, so the first blocks of a stream are not the worst ones; every division is timed either
    /// way, and what plays afterwards is whichever of them was actually quickest.
    /// </param>
    /// <param name="recheck">
    /// Go back over the divisions every few hundred blocks, in case the machine is not what it was. Left off, the
    /// first sweep decides and the division never moves again.
    /// </param>
    public PhaseBalance(int channels, int up, double advantage, bool recheck = true)
    {
        _recheck = recheck;
        _up = Math.Max(1, up);
        _channels = Math.Max(1, channels);
        _slots = _up * _channels;
        _cost = new double[_slots + 1];
        _pending = new bool[_slots + 1];
        Array.Fill(_pending, true);

        // Two workers finish together when each has work in proportion to its own speed. That is where the
        // timing begins; it is not where it necessarily ends.
        int guess = advantage > 0.0 ? (int)Math.Round(_slots * advantage / (1.0 + advantage)) : 0;
        _current = Math.Clamp(guess, 0, _slots);
    }

    private PhaseBalance(int channels, int up, int share, bool sweep)
    {
        _up = Math.Max(1, up);
        _channels = Math.Max(1, channels);
        _slots = _up * _channels;
        _cost = new double[_slots + 1];
        _pending = new bool[_slots + 1];
        _current = Math.Clamp(share, 0, _slots);
        _fixed = !sweep;
        _sweep = sweep;
        _measuring = false;
    }

    /// <summary>The same division every block: what the opening race and the forced setting both want.</summary>
    public static PhaseBalance Fixed(int channels, int up, int share) => new(channels, up, share, sweep: false);

    /// <summary>
    /// A different division every block, from none to all of them. Verification runs against this, so the check
    /// covers every division the balance can later arrive at rather than only the one it settles on.
    /// </summary>
    public static PhaseBalance Sweeping(int up) => new(1, up, 0, sweep: true);

    /// <summary>Phases the stage has across every channel.</summary>
    public int Slots => _slots;

    /// <summary>Phases each channel has.</summary>
    public int Up => _up;

    /// <summary>Phases the device is holding across every channel.</summary>
    public int Held => _current;

    /// <summary>Whether the divisions are still being timed, rather than the quickest one being used.</summary>
    public bool Measuring => _measuring;

    /// <summary>
    /// Whether the division the filter timings chose is being weighed against no device at all over whole
    /// pipeline blocks. It alternates between the two while this is true, so the division reported meanwhile is
    /// not yet the answer.
    /// </summary>
    public bool Confirming => _confirming;

    /// <summary>
    /// Whether nothing the device could be given beat the processor working alone by enough to be worth it, so
    /// it has been let go. The caller then stops feeding it, and the stage costs exactly what it would with no
    /// device at all.
    /// </summary>
    public bool Dropped => _dropped;

    /// <summary>
    /// Whether the device should be left entirely alone for this block, not even given the input. That is what
    /// letting it go means, and it is also the state the pipeline check has to measure: a device that is handed
    /// every block and given no phase of it still costs a round trip, about half a millisecond of it, which is
    /// most of what a division has to beat. Comparing against that instead of against silence is what kept a
    /// device that was making the music slower.
    /// </summary>
    public bool Detached => _dropped || (_confirming && _confirmIndex == 1);

    /// <summary>The division to use for the block about to be processed; hold it until the block is reported.</summary>
    public int Begin() => _current;

    /// <summary>
    /// Phases of <paramref name="channel"/> that <paramref name="share"/> gives the device. Channels are filled
    /// one at a time, so the earliest channels are wholly on the device before any of the later ones give a
    /// phase up.
    /// </summary>
    public int LanesOf(int share, int channel) => Math.Clamp(share - (channel * _up), 0, _up);

    /// <summary>Reports what a channel's block cost and chooses the division for the next one.</summary>
    /// <param name="share">The division this block was processed at, as <see cref="Begin"/> returned it.</param>
    /// <param name="blockSeconds">
    /// Seconds from handing the device its phases to having every phase of the block in hand: the part of the
    /// block the division changes, and therefore the only part worth comparing.
    /// </param>
    public void Report(int share, double blockSeconds)
    {
        if (_fixed)
        {
            return;
        }

        if (_sweep)
        {
            _current = _current >= _slots ? 0 : _current + 1;
            return;
        }

        // One balance serves every channel of a stage, because they share the one device and the right division
        // is a decision about all of them together rather than each on its own.
        lock (_gate)
        {
            // While the pipeline is being timed the division is held still by that, not by this.
            if (_dropped || _confirming || blockSeconds <= 0.0 || share != _current)
            {
                return;
            }

            // A block is only finished when its slowest channel is, and with the channels divided unevenly they
            // are deliberately not alike, so the reading is the worst of them rather than any one of them.
            _worst = Math.Max(_worst, blockSeconds);
            if (++_reports < _channels)
            {
                return;
            }

            double seconds = _worst;
            _worst = 0.0;
            _reports = 0;
            _cost[share] = _cost[share] > 0.0 ? Math.Min(_cost[share], seconds) : seconds;

            if (_measuring)
            {
                if (++_trials >= TrialBlocks)
                {
                    _trials = 0;
                    _pending[_current] = false;
                    Rotate();
                }
            }
            else if (_recheck && ++_since >= RecheckBlocks)
            {
                Recheck();
            }
        }
    }

    /// <summary>
    /// Reports what a whole block of the pipeline cost, meaning every channel through the filter, the
    /// modulator, the dither and the writing out, for the division now in force.
    /// </summary>
    /// <param name="secondsPerSample">
    /// Wall time for the block divided by the input samples in it. The caller gathers enough consecutive blocks
    /// to cover at least one of the filter's own blocks, because most blocks of a frequency-domain stage only
    /// fill its buffer and cost almost nothing; a reading over fewer would be measuring which division happened
    /// to be in force while the stage was idle.
    /// </param>
    /// <remarks>
    /// Readings arriving outside the check are still worth having: they are what tells this balance that anything
    /// is watching the pipeline at all, and therefore that the check can be made when the filter timings settle.
    /// </remarks>
    public void ReportPipeline(double secondsPerSample)
    {
        if (_fixed || _sweep)
        {
            return;
        }

        lock (_gate)
        {
            _watched = true;
            if (!_confirming || _dropped || secondsPerSample <= 0.0)
            {
                return;
            }

            double kept = _confirmCost[_confirmIndex];
            _confirmCost[_confirmIndex] = kept > 0.0 ? Math.Min(kept, secondsPerSample) : secondsPerSample;
            if (++_confirmTrials < ConfirmTrials)
            {
                return;
            }

            _confirmTrials = 0;
            if (_confirmIndex == 0 && _confirmCost[1] <= 0.0)
            {
                // Now the same measurement with the device left out, which is what it is being weighed against.
                // Only ever taken once: it is a reading of the processor and the music, not of the division, and
                // coming back from it costs the device a partition's worth of blocks with no phases.
                _confirmIndex = 1;
                _current = 0;
                return;
            }

            // Only if the whole block is clearly quicker divided. The filter being quicker is not enough: it is
            // the block that has to keep up with the music.
            bool worthIt = _confirmCost[0] > 0.0 && _confirmCost[1] > 0.0
                && _confirmCost[0] < _confirmCost[1] * Margin;
            _confirming = false;
            _current = worthIt ? _candidate : 0;
            _dropped = !worthIt;
            _since = 0;
        }
    }

    /// <summary>Moves to the next division still wanting a timing, or settles on the quickest once none does.</summary>
    private void Rotate()
    {
        for (int i = 1; i <= _slots + 1; i++)
        {
            int next = (_current + i) % (_slots + 1);
            if (_pending[next])
            {
                _current = next;
                return;
            }
        }

        Settle();
    }

    private void Settle()
    {
        int best = 0;
        double shortest = double.MaxValue;
        for (int share = 0; share <= _slots; share++)
        {
            if (_cost[share] > 0.0 && _cost[share] < shortest)
            {
                shortest = _cost[share];
                best = share;
            }
        }

        // Leaving the device alone is not free either, since it still has to be given every block in case the
        // next division wants it, so a division earns its keep by being clearly quicker, not barely quicker.
        bool worthIt = best > 0 && _cost[0] > 0.0 && shortest < _cost[0] * Margin;
        _measuring = false;
        _since = 0;
        _reports = 0;
        _worst = 0.0;
        _candidate = worthIt ? best : 0;
        _current = _candidate;

        // Where whole pipeline blocks are being timed, the filter's verdict is a nomination rather than a
        // decision, and the check that follows can still take the device away. Only after a sweep of every
        // division, though: going back over a neighbourhood is choosing between divisions that all use the
        // device, and asking the whole question again there would cost more in idle blocks than it is worth.
        _confirming = _watched && worthIt && _sweeping;
        _confirmIndex = 0;
        _confirmTrials = 0;
        _confirmCost[0] = 0.0;
        _dropped = !worthIt;
    }

    /// <summary>Times the divisions around the settled one again, in case the machine is not what it was.</summary>
    /// <remarks>
    /// What was measured before is kept, and a division only ever moves to the quickest it has been seen at, so
    /// going back over the neighbourhood cannot talk the balance into a worse division on one unlucky block.
    /// Every so often the lot is forgotten instead and timed again from nothing, which is what catches a machine
    /// that has genuinely changed rather than one that is merely busy this second.
    /// </remarks>
    private void Recheck()
    {
        _since = 0;
        _measuring = true;
        _trials = 0;
        _reports = 0;
        _worst = 0.0;

        if (++_rechecks >= RechecksPerSweep)
        {
            _rechecks = 0;
            _sweeping = true;
            Array.Clear(_cost);
            Array.Fill(_pending, true);
            _current = 0;
            return;
        }

        _sweeping = false;

        int from = Math.Max(0, _current - RecheckSpread);
        int to = Math.Min(_slots, _current + RecheckSpread);
        for (int share = from; share <= to; share++)
        {
            _pending[share] = true;
        }

        _current = from;
    }
}
