using FUPlayer.Core.Dsp.Resampling;

namespace FUPlayer.Core.Dsp.Acceleration;

/// <summary>
/// Moves the long-filter convolution of a playback plan onto an OpenCL device. It is entirely optional: every
/// stage it cannot take over quietly stays on the processor, and <see cref="Status"/> says why: the device
/// refused it, ran out of memory, or answered differently from the processor.
/// </summary>
/// <remarks>
/// Only long filter stages are offloaded, either convolution. Delta-sigma modulation is a feedback loop, where
/// each output bit depends on the one before it, so there is nothing in it for a device built on doing thousands
/// of independent things at once.
/// </remarks>
public sealed class GpuAccelerator : IDisposable
{
    /// <summary>
    /// Fewest input samples a pipeline reading may cover. A block-based stage spends most blocks filling its
    /// buffer and convolving nothing, so a reading has to span enough of them to hold the real work as well as
    /// the idling. Otherwise the quickest reading is simply the one that caught the stage doing nothing.
    /// </summary>
    private const int MinimumWindowSamples = 8192;

    private readonly GpuProgram _program;
    private readonly bool _force;
    private readonly int _share;
    private readonly bool _retime;
    private readonly Dictionary<FftPolyphaseStage, GpuPolyphaseFilter?> _filters = [];
    private readonly Dictionary<PolyphaseStage, GpuDirectFilter?> _directFilters = [];
    private readonly List<IDisposable> _convolvers = [];
    private readonly Dictionary<object, PhaseBalance> _balances = [];
    private readonly Dictionary<object, int> _attached = [];
    private readonly Lock _gate = new();
    private string? _failure;
    private string? _overruled;
    private string? _unchecked;
    private string? _verdict;
    private double _deviation;
    private int _accelerated;
    private bool _disposed;
    private int _channels = 1;
    private int _ownThreads = 1;
    private double _advantage;
    private int _window = MinimumWindowSamples;
    private double _windowSeconds;
    private long _windowSamples;

    private GpuAccelerator(GpuProgram program, bool force, int share, bool retime)
    {
        _program = program;
        _force = force;
        _share = share;
        _retime = retime;
    }

    public GpuDevice Device => _program.Device;

    /// <summary>Whether the device does the arithmetic in 64-bit, as the processor path does.</summary>
    public bool UsesDouble => _program.UsesDouble;

    /// <summary>Whether at least one filter stage actually runs on the device.</summary>
    public bool IsActive => _accelerated > 0;

    /// <summary>
    /// Whether any filter was uploaded and accepted, which is known once the stages have been planned and before
    /// any channel has been given its state. A device that refused every filter, for want of memory or because
    /// its answer did not match or its layout is one the kernel does not take, must not be allowed to change how
    /// the processor's own threads are shared out, or the channels oversubscribe the cores for nothing.
    /// </summary>
    public bool HasFilters
    {
        get
        {
            lock (_gate)
            {
                return _filters.Values.Any(f => f is not null) || _directFilters.Values.Any(f => f is not null);
            }
        }
    }

    /// <summary>
    /// Phases of each channel the device is holding, averaged over the channels, out of
    /// <see cref="StagePhases"/>. It moves while the music plays: every block reports how long the processor
    /// waited for the device, and the share follows.
    /// </summary>
    public double DevicePhases
    {
        get
        {
            lock (_gate)
            {
                return _balances.Count == 0 ? 0.0 : _balances.Values.Sum(b => (double)b.Held) / _balances.Count;
            }
        }
    }

    /// <summary>Phases the shared stage has across every channel, or 0 when nothing is being shared.</summary>
    public int StagePhases
    {
        get
        {
            lock (_gate)
            {
                return _balances.Count == 0 ? 0 : _balances.Values.First().Slots;
            }
        }
    }

    /// <summary>A sentence for the Now playing panel: which device is doing the work, or why none is.</summary>
    public string Status
    {
        get
        {
            if (_accelerated == 0)
            {
                string? reason = _failure ?? _verdict;
                return reason is null
                    ? "Filtering on the processor: no stage in this conversion can be offloaded."
                    : $"Filtering on the processor: {Device.Name} was not used because {reason}.";
            }

            string precision = UsesDouble
                ? "64-bit"
                : $"32-bit, {20.0 * Math.Log10(Math.Max(_deviation, 1e-12)):0} dB from the processor result";
            string extra = _failure is null ? string.Empty : $" One stage stayed on the processor: {_failure}.";

            // A filter the check could not reach is used only because the device was forced, and saying which
            // matters: this is the one case where the answers were never compared at all.
            string unchecked_ = _unchecked is null
                ? string.Empty
                : $" Output not compared with the processor: {_unchecked}.";

            // Forced against the measurement, the reading is the one thing worth saying: it is why the load went up.
            string overruled = _overruled is null ? string.Empty : $" Forced: {_overruled}.";

            // How the work is divided. Both are working at once on the same blocks: the device is given as
            // many of the phases as it turned out to be quickest with, and that is timed again as it plays, so
            // the number here is what was measured rather than anything decided in advance.
            int phases = StagePhases;
            double taken = DevicePhases;
            string how = _share >= 0
                ? $", the {_share}% requested"
                : _retime ? ", re-measured during playback" : ", measured once at the start";
            string split = phases <= 0
                ? string.Empty
                : taken <= 0.0
                    ? " No phases on it: every split was timed and none beat the processor alone, so the device " +
                      "was released."
                    : $" {taken:0.#} of {phases} phases on the device{how}; the processor convolves the rest of " +
                      "the same blocks concurrently.";
            return $"Filtering on {Device.Name} ({precision}).{split}{unchecked_}{extra}{overruled}";
        }
    }

    /// <summary>
    /// Prepares the chosen device, or returns null with the reason in <paramref name="status"/>.
    /// </summary>
    /// <param name="deviceId">Stored device id, or null for the most capable one.</param>
    /// <param name="highPrecision">
    /// Do the arithmetic in 64-bit, as the processor does. Consumer cards run 64-bit maths at a fraction of their
    /// 32-bit rate, often a thirty-second of it, so 32-bit is far faster where its accuracy is enough.
    /// </param>
    /// <param name="force">
    /// Take the filter even when the processor measured faster at it. The timed run still happens, so
    /// <see cref="Status"/> can say what it cost; only its verdict is overruled. Answering differently from the
    /// processor is not overruled by anything.
    /// </param>
    /// <param name="share">
    /// Percentage of the phases to give the device, or −1 to time every division and keep the quickest.
    /// </param>
    /// <param name="retime">Go back over the divisions as the stream plays; ignored where the share is fixed.</param>
    public static GpuAccelerator? TryCreate(
        string? deviceId, bool highPrecision, out string status, bool force = false, int share = -1, bool retime = true)
    {
        GpuDevice? device = GpuRuntime.Resolve(deviceId);
        if (device is null)
        {
            status = $"Filtering on the processor: {GpuRuntime.Unavailable ?? "no OpenCL device was found"}.";
            return null;
        }

        try
        {
            var accelerator = new GpuAccelerator(
                GpuRuntime.GetProgram(device, highPrecision && device.SupportsDouble), force, share, retime);
            status = accelerator.Status;
            return accelerator;
        }
        catch (OpenClException ex)
        {
            status = $"Filtering on the processor: {device.Name} could not be prepared ({ex.Message}).";
            return null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (IDisposable convolver in _convolvers)
            {
                convolver.Dispose();
            }

            foreach (GpuPolyphaseFilter? filter in _filters.Values)
            {
                filter?.Dispose();
            }

            foreach (GpuDirectFilter? filter in _directFilters.Values)
            {
                filter?.Dispose();
            }

            _convolvers.Clear();
            _balances.Clear();
            _attached.Clear();
            _filters.Clear();
            _directFilters.Clear();
        }
    }

    /// <summary>
    /// What a block of the pipeline cost: every channel through the filter and everything after it, before it
    /// was written out. The division of the phases is settled on the filter's own timings, which say which
    /// arithmetic finishes soonest, and then checked against this, which says whether the music does.
    /// </summary>
    /// <param name="seconds">Wall time the block took.</param>
    /// <param name="inputSamples">Input samples it carried, so blocks of different lengths compare.</param>
    public void ReportBlock(double seconds, int inputSamples)
    {
        if (seconds <= 0.0 || inputSamples <= 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed || _balances.Count == 0)
            {
                return;
            }

            _windowSeconds += seconds;
            _windowSamples += inputSamples;
            if (_windowSamples < _window)
            {
                return;
            }

            double perSample = _windowSeconds / _windowSamples;
            _windowSeconds = 0.0;
            _windowSamples = 0;
            foreach (PhaseBalance balance in _balances.Values)
            {
                balance.ReportPipeline(perSample);
            }
        }
    }

    /// <summary>Whether this stage is one a device could take over, so the plan knows to wake one at all.</summary>
    public static bool CanAccelerate(IRateStage stage, bool force) =>
        stage is FftPolyphaseStage || (stage is PolyphaseStage direct && GpuDirectFilter.Suits(direct, force));

    /// <summary>
    /// Uploads, checks and races the filter before any channel is given its state, rather than lazily on the
    /// first block of music. The race no longer decides anything on its own: it only supplies the share each
    /// channel's balance starts from, and the balance then measures its way to the truth as the music plays.
    /// </summary>
    /// <param name="stages">Stages of the chain; the ones a device cannot take are ignored.</param>
    /// <param name="channels">Channels the pipeline will process.</param>
    /// <param name="parallelism">Threads the processor would give one channel's stage, so the race is fair.</param>
    public void PlanChannels(IEnumerable<IRateStage> stages, int channels, int parallelism)
    {
        ArgumentNullException.ThrowIfNull(stages);
        lock (_gate)
        {
            _channels = Math.Max(1, channels);

            // The threads a channel has to itself, before the caller widens them on the strength of this device
            // holding whole channels. Handed to each convolver so it can give them back if the device is let go.
            _ownThreads = Math.Max(1, parallelism);
            foreach (IRateStage stage in stages)
            {
                switch (stage)
                {
                    case FftPolyphaseStage fft:
                        // A reading has to span one of this stage's blocks at least, or it is timing the buffer
                        // filling rather than the convolution.
                        _window = Math.Max(_window, fft.BlockSamples);
                        EnsureFilter(fft, parallelism);
                        break;
                    case PolyphaseStage direct when GpuDirectFilter.Suits(direct, _force):
                        EnsureDirect(direct, parallelism);
                        break;

                    // The device convolves one block length at a time. A stage that divides its taps between
                    // several block lengths to answer sooner is not something it can be given, and saying so is
                    // better than letting the panel report that there was nothing worth handing over.
                    case LayeredFftPolyphaseStage:
                        _failure ??= "its filter is convolved in blocks of several lengths, which the device does not do";
                        break;
                }
            }
        }
    }

    /// <summary>Device-side state for one channel of <paramref name="stage"/>, or null to stay on the processor.</summary>
    /// <param name="parallelism">Threads the processor would give the stage, so the two are raced on equal terms.</param>
    internal IRateStageState? TryAttach(FftPolyphaseStage stage, int parallelism)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return null;
            }

            GpuPolyphaseFilter? filter = EnsureFilter(stage, parallelism);
            if (filter is null)
            {
                return null;
            }

            PhaseBalance balance = Balance(filter, stage.Up);
            int channel = Next(filter);
            return Attach(() => new SplitConvolver(filter, parallelism, balance, channel, _ownThreads));
        }
    }

    /// <summary>Device-side state for one channel of a tap-by-tap <paramref name="stage"/>, or null to stay here.</summary>
    /// <param name="parallelism">Threads the processor would give the stage, so the two are raced on equal terms.</param>
    internal IRateStageState? TryAttach(PolyphaseStage stage, int parallelism)
    {
        lock (_gate)
        {
            if (_disposed || !GpuDirectFilter.Suits(stage, _force))
            {
                return null;
            }

            GpuDirectFilter? filter = EnsureDirect(stage, parallelism);
            if (filter is null)
            {
                return null;
            }

            PhaseBalance balance = Balance(filter, stage.Up);
            int channel = Next(filter);
            return Attach(() => new SplitDirectConvolver(filter, parallelism, balance, channel, _ownThreads));
        }
    }

    /// <summary>
    /// The one division every channel of a stage works to. It is shared because the device is: two channels
    /// each deciding for themselves would be timing a device the other one was using, and would disagree.
    /// A share that was asked for is used as asked; forced means every phase, whatever the measurements say.
    /// </summary>
    private PhaseBalance Balance(object filter, int up)
    {
        if (_balances.TryGetValue(filter, out PhaseBalance? existing))
        {
            return existing;
        }

        if ((_force || _share >= 0) && _advantage is > 0.0 and < 1.0)
        {
            _overruled ??= _verdict;
        }

        int slots = _channels * up;
        PhaseBalance balance = _share >= 0
            ? PhaseBalance.Fixed(_channels, up, (int)Math.Round(slots * _share / 100.0, MidpointRounding.AwayFromZero))
            : _force
                ? PhaseBalance.Fixed(_channels, up, slots)
                : new PhaseBalance(_channels, up, _advantage, _retime);
        _balances[filter] = balance;
        return balance;
    }

    /// <summary>Which channel of a stage is being attached; they come in order and the balance fills them so.</summary>
    private int Next(object filter)
    {
        _attached.TryGetValue(filter, out int channel);
        _attached[filter] = channel + 1;
        return channel;
    }

    private GpuPolyphaseFilter? EnsureFilter(FftPolyphaseStage stage, int parallelism)
    {
        if (!_filters.TryGetValue(stage, out GpuPolyphaseFilter? filter))
        {
            filter = Prepare(
                () => GpuPolyphaseFilter.CreateVerified(stage, _program, _force),
                f => new SplitConvolver(f, parallelism, PhaseBalance.Fixed(1, stage.Up, stage.Up), 0),
                stage,
                parallelism);
            _filters[stage] = filter;
        }

        return filter;
    }

    private GpuDirectFilter? EnsureDirect(PolyphaseStage stage, int parallelism)
    {
        if (!_directFilters.TryGetValue(stage, out GpuDirectFilter? filter))
        {
            filter = Prepare(
                () => GpuDirectFilter.CreateVerified(stage, _program, _force),
                f => new SplitDirectConvolver(f, parallelism, PhaseBalance.Fixed(1, stage.Up, stage.Up), 0),
                stage,
                parallelism);
            _directFilters[stage] = filter;
        }

        return filter;
    }

    /// <summary>
    /// Uploads a filter, checks the device reproduces the processor's answer, and then checks it is quicker at
    /// it. Either check may send the stage back to the processor, with <see cref="Status"/> saying which did.
    /// The timed run is advisory when the device has been forced.
    /// </summary>
    private TFilter? Prepare<TFilter, TState>(
        Func<TFilter> upload,
        Func<TFilter, TState> convolver,
        IRateStage stage,
        int parallelism)
        where TFilter : class, IGpuFilter
        where TState : IRateStageState, IDisposable
    {
        TFilter? filter = null;
        try
        {
            filter = upload();
            _deviation = Math.Max(_deviation, filter.WorstDeviation);
            _unchecked ??= filter.Unchecked;

            using TState device = convolver(filter);
            GpuRace.DeviceWins(stage, device, stage.CreateState(parallelism), out double advantage);

            // The race no longer decides on its own whether the device is used, only how much of the work is
            // worth giving it; a device that lost outright ends up with none of the channels anyway.
            _advantage = _advantage > 0.0 ? Math.Min(_advantage, advantage) : advantage;
            _verdict ??= advantage > 0.0
                ? $"the processor convolves this filter {1.0 / advantage:0.0} times faster than it does"
                : "the device produced nothing to time";
            return filter;
        }
        catch (OpenClException ex)
        {
            _failure ??= ex.Message;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or AccessViolationException)
        {
            _failure ??= "the OpenCL driver failed while the filter was being prepared";
        }

        filter?.Dispose();
        return null;
    }

    private IRateStageState? Attach<T>(Func<T> create)
        where T : IRateStageState, IDisposable
    {
        try
        {
            T convolver = create();
            _convolvers.Add(convolver);
            _accelerated++;
            return convolver;
        }
        catch (OpenClException ex)
        {
            _failure ??= ex.Message;
            return null;
        }
    }
}
