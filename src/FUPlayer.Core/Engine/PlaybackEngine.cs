using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using FUPlayer.Core.Audio;
using FUPlayer.Core.Capture;
using FUPlayer.Core.Dsp.Analysis;
using FUPlayer.Core.Decoding;
using FUPlayer.Core.Dsp.Dsd;
using FUPlayer.Core.Metadata;
using FUPlayer.Core.Output;
using FUPlayer.Core.Playlists;
using FUPlayer.Core.Settings;

namespace FUPlayer.Core.Engine;

/// <summary>
/// The player: owns the queue, decoders, DSP pipeline and device stream. All state changes run on a dedicated
/// engine thread fed by a command channel; public methods are thread-safe and return immediately. Events are
/// raised on the engine thread (or the calling thread for pause/resume) and must be marshalled by UI code.
/// </summary>
public sealed class PlaybackEngine : IDisposable
{
    private const int MaxMarkers = 16;

    private readonly AudioBackendRegistry _backends;
    private readonly ICaptureProvider? _capture;
    private readonly Channel<Command> _commands = Channel.CreateUnbounded<Command>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Thread _thread;
    private readonly AutoResetEvent _spaceAvailable = new(false);
    private readonly object _statusGate = new();
    private readonly List<Marker> _markers = [];

    private PlayerSettings _settings;
    private ParallelWorkers _workers;
    private int _workerThreads;
    private int _interrupts;
    private volatile bool _disposed;

    // Engine-thread state.
    private IAudioDecoder? _decoder;
    private int _decoderIndex = -1;
    private QueueItem? _decoderItem;
    private IAudioBackend? _backend;
    private DeviceCapabilities? _capabilities;
    private string? _capabilitiesKey;
    private bool _nativeDsdRefused;
    private OutputWriter? _writer;
    private bool _streamStarted;
    private PendingTransition? _transition;
    private bool _draining;
    private bool _drainFlushed;
    private long _drainedTimestamp;
    private TestToneMode _testToneMode;
    private int _captureProcessId;
    private double[][] _pcmBlock = [];
    private byte[][] _dsdBlock = [];

    // Shared with the UI and device threads.
    private volatile DspPipeline? _pipeline;
    private volatile IAudioStream? _stream;
    private volatile RenderSource? _render;
    private volatile SpscByteRing? _ring;
    private volatile string? _lastError;
    private volatile string? _streamFailure;
    private volatile string? _deviceName;
    private volatile EngineState _state;
    private volatile bool _invert;
    private double _volumeDb;
    private double _dspLoad;
    private double _loadWorkSeconds;
    private double _loadAudioSeconds;
    private int _fifoMilliseconds;
    private int _reportedIndex = -1;
    private CounterBaseline _baseline;

    public PlaybackEngine(PlayerSettings settings, AudioBackendRegistry backends, ICaptureProvider? capture = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(backends);
        _settings = settings.Clone();
        _backends = backends;
        _capture = capture;
        _volumeDb = ClampVolume(_settings.Volume, _settings.Volume.VolumeDb);
        _invert = _settings.Playback.InvertPolarity;
        _workerThreads = DesiredWorkerThreads(_settings);
        _workers = new ParallelWorkers(_workerThreads);
        Queue = new PlayQueue();
        _thread = new Thread(Run) { IsBackground = true, Name = "FUPLAYER engine", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public event EventHandler<EngineState>? StateChanged;

    /// <summary>The audible queue index changed (−1 for the test tone).</summary>
    public event EventHandler<int>? CurrentIndexChanged;

    public event EventHandler<string>? ErrorOccurred;

    public PlayQueue Queue { get; }

    public EngineState State => _state;

    /// <summary>Meters of the running pipeline, or null when stopped.</summary>
    public MeterHub? Meters => _pipeline?.Meters;

    /// <summary>Native window handle passed to drivers that need one (ASIO).</summary>
    public IntPtr WindowHandle { get; set; }

    public double VolumeDb
    {
        get => Volatile.Read(ref _volumeDb);
        set
        {
            Volatile.Write(ref _volumeDb, ClampVolume(_settings.Volume, value));
            ApplyLiveGain();
        }
    }

    public bool InvertPolarity
    {
        get => _invert;
        set
        {
            _invert = value;
            ApplyLiveGain();
        }
    }

    public void Play(int index, TimeSpan start = default) => Post(new PlayCommand(index, start), interrupt: true);

    public void PlayPause()
    {
        switch (_state)
        {
            case EngineState.Playing:
                Pause();
                break;
            case EngineState.Paused:
                Resume();
                break;
            default:
                Play(Math.Max(0, Volatile.Read(ref _reportedIndex)));
                break;
        }
    }

    public void Pause()
    {
        if (_state != EngineState.Playing || _render is not { } render)
        {
            return;
        }

        render.Paused = true;
        SetState(EngineState.Paused);
    }

    public void Resume()
    {
        if (_state != EngineState.Paused || _render is not { } render)
        {
            return;
        }

        render.Paused = false;
        SetState(EngineState.Playing);
        _spaceAvailable.Set();
    }

    public void Stop() => Post(new StopCommand(), interrupt: true);

    public void Next() => Post(new SkipCommand(1), interrupt: true);

    public void Previous() => Post(new SkipCommand(-1), interrupt: true);

    public void Seek(TimeSpan position) => Post(new SeekCommand(position), interrupt: true);

    /// <summary>Applies new settings; playback re-arms at the current position if the processing changes.</summary>
    public void ApplySettings(PlayerSettings settings) => Post(new SettingsCommand(settings.Clone()), interrupt: true);

    /// <summary>Changes repeat and shuffle without touching the audio path.</summary>
    public void SetQueueOptions(RepeatMode repeat, bool shuffle) => Post(new QueueOptionsCommand(repeat, shuffle), interrupt: false);

    public void PlayTestTone(TestToneMode mode) => Post(new TestToneCommand(mode), interrupt: true);

    /// <summary>
    /// Plays another application's output. Like the test tone this does not touch the queue: a live
    /// source has no place in a list of tracks and nothing follows it.
    /// </summary>
    public void PlayCapture(int processId) => Post(new CaptureCommand(processId), interrupt: true);

    /// <summary>Applications whose audio can be captured, or an empty list where that is not possible.</summary>
    public IReadOnlyList<CaptureTarget> CaptureTargets =>
        _capture is { IsSupported: true } ? _capture.List() : [];

    /// <summary>Why capture is unavailable, or null when it works.</summary>
    public string? CaptureUnavailable => _capture is null ? "This build cannot capture an application." : _capture.UnsupportedReason;

    /// <summary>The bandwidth verdict as a line for the interface, or null while it is still measuring.</summary>
    private static string? Describe(DspPipeline? pipeline)
    {
        if (pipeline is null)
        {
            return null;
        }

        BandwidthEstimate estimate = pipeline.Bandwidth;
        return estimate.Verdict == BandwidthVerdict.Unknown ? null : estimate.Describe();
    }

    public PlaybackStatus GetStatus()
    {
        (Marker? marker, TimeSpan position) = ComputePlayback();
        DspPipeline? pipeline = _pipeline;
        SpscByteRing? ring = _ring;
        RenderSource? render = _render;
        CounterBaseline baseline;
        lock (_statusGate)
        {
            baseline = _baseline;
        }

        int index = marker is not null ? ResolveIndex(marker.Index, marker.Item) : Volatile.Read(ref _reportedIndex);
        return new PlaybackStatus
        {
            State = _state,
            CurrentIndex = index,
            CurrentItem = marker is not null ? marker.Item : index >= 0 ? Queue.Get(index) : null,
            IsTestTone = marker?.IsTestTone ?? false,
            IsCapture = marker?.IsCapture ?? false,
            CaptureProcessId = marker?.IsCapture == true ? _captureProcessId : 0,
            CaptureNote = marker?.IsCapture == true ? (_decoder as IDiagnosticCapture)?.DirectOutputNote : null,
            Bandwidth = Describe(pipeline),
            Position = position,
            Duration = marker is { Length: > 0 } ? TimeSpan.FromSeconds((double)marker.Length / marker.SampleRate) : TimeSpan.Zero,
            Plan = pipeline?.Plan,
            ResamplerSummary = pipeline?.ResamplerSummary,
            Acceleration = pipeline?.AccelerationSummary,
            Upscaler = pipeline?.UpscalerStatus,
            IsUpscaling = pipeline?.IsUpscaling ?? false,
            Restorer = pipeline?.RestorerStatus,
            IsRestoring = pipeline?.IsRestoring ?? false,
            BackendName = _backend?.DisplayName,
            DeviceName = _deviceName,
            DspLoad = Volatile.Read(ref _dspLoad),
            BufferFill = ring is null ? 0.0 : (double)ring.Count / ring.Capacity,
            FifoBytes = ring?.Capacity ?? 0,
            FifoSeconds = ring is null || _stream is null
                ? 0.0
                : (double)ring.Capacity / _stream.Format.CanonicalBytesPerFrame / _stream.Format.CanonicalFrameRate,
            PipelineLatencySeconds = pipeline?.LatencySeconds ?? 0.0,
            FilterBlockSeconds = pipeline?.FilterBlockSeconds ?? 0.0,
            LimiterEvents = pipeline is null ? 0 : pipeline.LimiterEvents - baseline.Limiter,
            ApodizationEvents = pipeline is null ? 0 : pipeline.ApodizationEvents - baseline.Apodization,
            ClippedSamples = pipeline is null ? 0 : pipeline.ClippedSamples - baseline.Clipped,
            ModulatorResets = pipeline is null ? 0 : pipeline.ModulatorResets - baseline.Resets,
            UnderrunFrames = render?.UnderrunFrames ?? 0,
            VolumeDb = VolumeDb,
            LastError = _lastError,
        };
    }

    /// <summary>Plans playback of a format with the current settings without starting anything (for previews).</summary>
    public PlaybackPlan PreviewPlan(StreamFormat format, PlayerSettings settings)
    {
        IAudioBackend backend = _backends.Resolve(settings.Output.BackendId);
        return OutputPlanner.Plan(format, settings, backend, backend.GetCapabilities(settings.Output.DeviceId, settings.Output.Channels));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Post(new ShutdownCommand(), interrupt: true);
        _disposed = true;
        _thread.Join(5000);
        _workers.Dispose();
        _spaceAvailable.Dispose();
    }

    private static double ClampVolume(VolumeSettings volume, double value) =>
        volume.IsBypassed ? 0.0 : Math.Clamp(value, volume.MinimumDb, volume.MaximumDb);

    private static int DesiredWorkerThreads(PlayerSettings settings) =>
        settings.Processing.DspThreads >= 0
            ? Math.Min(settings.Processing.DspThreads, 63)
            : ParallelWorkers.AutomaticExtraThreads(Math.Max(2, settings.Output.Channels));

    private static bool IsRecoverable(Exception ex) => ex is not (OutOfMemoryException or AccessViolationException);

    private void Post(Command command, bool interrupt)
    {
        if (_disposed)
        {
            return;
        }

        _commands.Writer.TryWrite(command);
        if (interrupt)
        {
            Interlocked.Increment(ref _interrupts);
        }

        _spaceAvailable.Set();
    }

    private void Run()
    {
        while (true)
        {
            try
            {
                bool active = _pipeline is not null && _state != EngineState.Stopped;
                if (!ProcessCommands(wait: !active))
                {
                    break;
                }

                if (_pipeline is not null && _state != EngineState.Stopped)
                {
                    Step();
                }
            }
            catch (Exception ex) when (IsRecoverable(ex))
            {
                ReportError($"Playback stopped: {ex.Message}");
                try
                {
                    StopInternal();
                }
                catch (Exception cleanup) when (IsRecoverable(cleanup))
                {
                    _lastError = cleanup.Message;
                }
            }
        }

        StopInternal();
    }

    private bool ProcessCommands(bool wait)
    {
        if (wait)
        {
            _commands.Reader.WaitToReadAsync().AsTask().Wait(250);
        }

        Interlocked.Exchange(ref _interrupts, 0);
        while (_commands.Reader.TryRead(out Command? command))
        {
            if (command is ShutdownCommand)
            {
                return false;
            }

            Handle(command);
        }

        return true;
    }

    private void Handle(Command command)
    {
        switch (command)
        {
            case PlayCommand play:
                StartPlayback(play.Index, play.Start, keepPaused: false);
                break;
            case StopCommand:
                StopInternal();
                break;
            case SeekCommand seek:
                SeekInternal(seek.Position);
                break;
            case SkipCommand skip:
                SkipInternal(skip.Direction);
                break;
            case SettingsCommand settings:
                ApplySettingsInternal(settings.Settings);
                break;
            case QueueOptionsCommand options:
                _settings.Playback.Repeat = options.Repeat;
                _settings.Playback.Shuffle = options.Shuffle;
                break;
            case TestToneCommand tone:
                StartTestTone(tone.Mode);
                break;
            case CaptureCommand capture:
                StartCapture(capture.ProcessId);
                break;
        }
    }

    private void Step()
    {
        if (_streamFailure is { } failure)
        {
            ReportError($"The audio device stopped: {failure}");
            StopInternal();
            return;
        }

        DspPipeline pipeline = _pipeline!;
        OutputWriter writer = _writer!;
        if (!pipeline.WritePending(writer))
        {
            return;
        }

        if (_transition is not null)
        {
            ContinueTransition();
            return;
        }

        if (_draining)
        {
            ContinueDrain();
            return;
        }

        IAudioDecoder decoder = _decoder!;
        int count;
        try
        {
            count = ReadBlock(decoder, pipeline);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        {
            ReportError($"Decoding failed: {ex.Message}");
            count = 0;
        }

        if (count <= 0)
        {
            BeginNextTrack();
            return;
        }

        bool written = decoder.Format.IsDsd
            ? pipeline.ProcessDsd(_dsdBlock, count, writer)
            : pipeline.ProcessPcm(_pcmBlock, count, writer);

        // Start a new device only once the FIFO holds audio, so playback does not open with underruns.
        if (!_streamStarted && _ring is { } ring && ring.Count >= ring.Capacity / 2)
        {
            StartStream();
        }

        // A block-based filter does nothing for many calls and then everything in one, so the load is the share
        // of a stretch of audio spent working, not the ratio of one call. The stretch has to cover a whole filter
        // block or it would keep reading zero and then several hundred per cent.
        double blockSeconds = decoder.Format.IsDsd ? count * 8.0 / decoder.Format.SampleRate : (double)count / decoder.Format.SampleRate;
        _loadWorkSeconds += pipeline.LastProcessingSeconds;
        _loadAudioSeconds += blockSeconds;
        double window = Math.Max(0.5, 2.0 * pipeline.FilterBlockSeconds);
        if (_loadAudioSeconds >= window)
        {
            double load = _loadWorkSeconds / _loadAudioSeconds;
            Volatile.Write(ref _dspLoad, (Volatile.Read(ref _dspLoad) * 0.5) + (load * 0.5));
            _loadWorkSeconds = 0.0;
            _loadAudioSeconds = 0.0;
        }

        if (written)
        {
            UpdateCurrentIndex();
        }
    }

    private int ReadBlock(IAudioDecoder decoder, DspPipeline pipeline)
    {
        if (!decoder.Format.IsDsd)
        {
            return decoder.ReadPcm(_pcmBlock, 0, pipeline.InputBlock);
        }

        int bytes = decoder.ReadDsd(_dsdBlock, 0, pipeline.InputBlock);
        if (bytes > 0 && (bytes & 1) == 1 && pipeline.Plan.PassThrough && pipeline.Plan.Output.Kind == OutputSampleKind.Dop)
        {
            foreach (byte[] channel in _dsdBlock)
            {
                channel[bytes] = DsdConstants.SilenceByte;
            }

            bytes++;
        }

        return bytes;
    }

    private void StartPlayback(int index, TimeSpan start, bool keepPaused)
    {
        DisposeTransition();
        _draining = false;
        CloseDecoder();

        int attempts = Math.Max(1, Queue.Count);
        while (attempts-- > 0 && index >= 0)
        {
            if (TryOpenDecoder(index, out IAudioDecoder? decoder, out TrackMetadata? metadata))
            {
                try
                {
                    StartPipeline(index, decoder, metadata, PlanFor(decoder), start, keepPaused);
                }
                catch (Exception ex) when (IsRecoverable(ex))
                {
                    decoder.Dispose();
                    ReportError($"Playback could not start: {ex.Message}");
                    StopInternal();
                }

                return;
            }

            index = Queue.NextIndex(index, RepeatMode.Off, shuffle: false, userRequested: true);
            start = TimeSpan.Zero;
        }

        StopInternal();
    }

    internal static CaptureOptions CaptureOptionsFor(PlayerSettings settings) =>
        new(settings.Playback.SilenceCapturedApplication, settings.Output.BackendId, settings.Output.DeviceId);

    /// <summary>Re-opens a live capture, which is what a device or format change needs.</summary>
    private void StartCapture(int processId)
    {
        if (_capture is null || !_capture.IsSupported)
        {
            ReportError(CaptureUnavailable ?? "This build cannot capture an application.");
            return;
        }

        DisposeTransition();
        _draining = false;
        CloseDecoder();
        try
        {
            IAudioDecoder decoder = _capture.Open(processId, _settings.Output.Channels, CaptureOptionsFor(_settings));
            _captureProcessId = processId;
            StartPipeline(_decoderIndex, decoder, null, PlanFor(decoder), TimeSpan.Zero, keepPaused: false);
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            ReportError($"The capture could not restart: {ex.Message}");
            StopInternal();
        }
    }

    private void StartTestTone(TestToneMode mode)
    {
        DisposeTransition();
        _draining = false;
        CloseDecoder();
        _captureProcessId = 0;
        _testToneMode = mode;
        var decoder = new TestToneDecoder(_settings.Output.Channels, mode);
        try
        {
            StartPipeline(-1, decoder, null, PlanFor(decoder), TimeSpan.Zero, keepPaused: false);
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            ReportError($"Test tone could not start: {ex.Message}");
            StopInternal();
        }
    }

    private void StartPipeline(int index, IAudioDecoder decoder, TrackMetadata? metadata, PlaybackPlan plan, TimeSpan start, bool keepPaused)
    {
        bool paused = keepPaused && _render is { Paused: true };
        SetPipeline(null);
        EnsureWorkers();
        try
        {
            EnsureStream(plan);
        }
        catch (Exception ex) when (plan.Output.Kind == OutputSampleKind.NativeDsd && IsRecoverable(ex))
        {
            // Some drivers advertise ASIO DSD mode but refuse it when a stream opens: fall back to DoP.
            _nativeDsdRefused = true;
            ReportError($"Native DSD could not be started ({ex.Message}); sending DSD as DoP instead.");
            plan = PlanFor(decoder);
            EnsureStream(plan);
        }

        var pipeline = new DspPipeline(plan, _settings, _workers);
        AllocateBlocks(decoder.Format, pipeline.InputBlock);
        if (start > TimeSpan.Zero && decoder.CanSeek)
        {
            decoder.Seek(decoder.ToPosition(start));
        }

        ClearRing();
        RenderSource render = _render!;
        render.EndOfStream = false;
        render.Paused = paused;
        _draining = false;
        _drainFlushed = false;
        lock (_statusGate)
        {
            _markers.Clear();
            _baseline = default;
        }

        Volatile.Write(ref _reportedIndex, int.MinValue);
        pipeline.SetVolume(CurrentVolumeLinear(plan));
        SetPipeline(pipeline);
        AttachDecoder(index, decoder, metadata, pipeline);

        SetState(paused ? EngineState.Paused : EngineState.Playing);
        UpdateCurrentIndex();
    }

    private void AttachDecoder(int index, IAudioDecoder decoder, TrackMetadata? metadata, DspPipeline pipeline, QueueItem? item = null)
    {
        _decoder = decoder;
        _decoderIndex = index;
        _decoderItem = item ?? (index >= 0 ? Queue.Get(index) : null);
        pipeline.SetReplayGain(ReplayGainFor(metadata));
        var marker = new Marker(
            _ring!.WriteTotal,
            index,
            _decoderItem,
            decoder.Position,
            decoder.Format.SampleRate,
            decoder.Length,
            decoder is TestToneDecoder,
            _captureProcessId > 0);

        lock (_statusGate)
        {
            _markers.Add(marker);
            if (_markers.Count > MaxMarkers)
            {
                _markers.RemoveAt(0);
            }
        }
    }

    /// <summary>
    /// Current queue position of a playing item: the queue may be reordered or edited during playback.
    /// For a removed item this is the position before its successor, so "next" continues naturally.
    /// </summary>
    private int ResolveIndex(int index, QueueItem? item)
    {
        if (item is null)
        {
            return index;
        }

        int current = Queue.IndexOf(item.Id);
        return current >= 0 ? current : Math.Min(index, Queue.Count) - 1;
    }

    private void BeginNextTrack()
    {
        int finished = ResolveIndex(_decoderIndex, _decoderItem);
        bool wasTestTone = _decoder is TestToneDecoder || _captureProcessId > 0;
        CloseDecoder();

        if (!wasTestTone)
        {
            PlaybackSettings playback = _settings.Playback;
            int next = Queue.NextIndex(finished, playback.Repeat, playback.Shuffle, userRequested: false);
            int attempts = Queue.Count;
            while (next >= 0 && attempts-- > 0)
            {
                if (TryOpenDecoder(next, out IAudioDecoder? decoder, out TrackMetadata? metadata))
                {
                    try
                    {
                        PlaybackPlan plan = PlanFor(decoder);
                        if (playback.Gapless && plan.HasSameProcessing(_pipeline!.Plan))
                        {
                            AttachDecoder(next, decoder, metadata, _pipeline);
                        }
                        else
                        {
                            _transition = new PendingTransition(next, decoder, metadata, plan);
                        }

                        return;
                    }
                    catch (Exception ex) when (IsRecoverable(ex))
                    {
                        decoder.Dispose();
                        ReportError(ex.Message);
                    }
                }

                next = Queue.NextIndex(next, playback.Repeat == RepeatMode.One ? RepeatMode.Off : playback.Repeat, playback.Shuffle, userRequested: true);
            }
        }

        _draining = true;
        _drainFlushed = false;
        _drainedTimestamp = 0;
    }

    private void ContinueTransition()
    {
        StartStream();
        PendingTransition transition = _transition!;
        if (!transition.Flushed)
        {
            if (!_pipeline!.Flush(_writer!))
            {
                return;
            }

            transition.Flushed = true;
        }

        if (_ring!.Count > 0)
        {
            _spaceAvailable.WaitOne(10);
            UpdateCurrentIndex();
            return;
        }

        _transition = null;
        if (_stream is { } stream && stream.Format != transition.Plan.Output)
        {
            // Let the device play out its own buffer before the stream is replaced.
            Thread.Sleep((int)Math.Clamp(stream.LatencySeconds * 1000.0, 0.0, 200.0));
        }

        try
        {
            StartPipeline(transition.Index, transition.Decoder, transition.Metadata, transition.Plan, TimeSpan.Zero, keepPaused: true);
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            transition.Decoder.Dispose();
            ReportError($"Playback could not continue: {ex.Message}");
            StopInternal();
        }
    }

    private void ContinueDrain()
    {
        StartStream();
        if (!_drainFlushed)
        {
            if (!_pipeline!.Flush(_writer!))
            {
                return;
            }

            _drainFlushed = true;
            _render!.EndOfStream = true;
        }

        if (_ring!.Count > 0 || _render is { Paused: true })
        {
            _spaceAvailable.WaitOne(10);
            UpdateCurrentIndex();
            return;
        }

        if (_drainedTimestamp == 0)
        {
            _drainedTimestamp = Stopwatch.GetTimestamp();
            return;
        }

        double wait = Math.Min(1.0, (_stream?.LatencySeconds ?? 0.0) + 0.05);
        if (Stopwatch.GetElapsedTime(_drainedTimestamp).TotalSeconds < wait)
        {
            Thread.Sleep(5);
            return;
        }

        StopInternal();
    }

    private void SeekInternal(TimeSpan position)
    {
        if (_pipeline is null)
        {
            return;
        }

        (Marker? marker, _) = ComputePlayback();
        bool paused = _render is { Paused: true };
        int audible = marker?.Index ?? _decoderIndex;
        if (marker?.IsTestTone == true || marker?.IsCapture == true)
        {
            return;
        }

        if (_transition is not null || _draining || _decoder is null || audible != _decoderIndex)
        {
            if (audible >= 0)
            {
                StartPlayback(audible, position, paused);
            }

            return;
        }

        if (!_decoder.CanSeek)
        {
            return;
        }

        _pipeline.Reset();
        _decoder.Seek(_decoder.ToPosition(position));
        ClearRing();
        lock (_statusGate)
        {
            _markers.Clear();
        }

        AttachDecoder(ResolveIndex(_decoderIndex, _decoderItem), _decoder, _decoderItem?.Metadata, _pipeline, _decoderItem);
    }

    private void SkipInternal(int direction)
    {
        (Marker? marker, TimeSpan position) = ComputePlayback();
        int current = marker is not null ? ResolveIndex(marker.Index, marker.Item) : Volatile.Read(ref _reportedIndex);
        bool paused = _state == EngineState.Paused;
        int target;
        if (direction < 0)
        {
            target = current < 0 || position > TimeSpan.FromSeconds(3)
                ? Math.Max(current, 0)
                : Queue.PreviousIndex(current, _settings.Playback.Shuffle);
        }
        else
        {
            target = Queue.NextIndex(current, _settings.Playback.Repeat, _settings.Playback.Shuffle, userRequested: true);
            if (target < 0)
            {
                StopInternal();
                return;
            }
        }

        StartPlayback(target, TimeSpan.Zero, paused);
    }

    private void ApplySettingsInternal(PlayerSettings settings)
    {
        PlayerSettings previous = _settings;
        _settings = settings;
        _invert = settings.Playback.InvertPolarity;
        Volatile.Write(ref _volumeDb, ClampVolume(settings.Volume, Volatile.Read(ref _volumeDb)));

        bool outputChanged = OutputChanged(previous, settings);
        if (outputChanged)
        {
            _capabilities = null;
            _capabilitiesKey = null;
            _nativeDsdRefused = false;
        }

        if (_pipeline is null || _state == EngineState.Stopped)
        {
            if (outputChanged)
            {
                CloseStream();
            }

            return;
        }

        (Marker? marker, TimeSpan position) = ComputePlayback();
        bool paused = _render is { Paused: true };
        if (_decoder is not null && _transition is null && !_draining && !outputChanged && !ProcessingOptionsChanged(previous, settings))
        {
            try
            {
                if (PlanFor(_decoder).HasSameProcessing(_pipeline.Plan))
                {
                    ApplyLiveGain();
                    _pipeline.SetReplayGain(ReplayGainFor(_decoderItem?.Metadata));
                    return;
                }
            }
            catch (Exception ex) when (IsRecoverable(ex))
            {
                ReportError(ex.Message);
            }
        }

        SetPipeline(null);
        if (outputChanged)
        {
            CloseStream();
        }

        if (marker?.IsTestTone == true)
        {
            StartTestTone(_testToneMode);
        }
        else if (marker?.IsCapture == true && _captureProcessId > 0)
        {
            StartCapture(_captureProcessId);
        }
        else if ((marker is not null ? ResolveIndex(marker.Index, marker.Item) : ResolveIndex(_decoderIndex, _decoderItem)) is int index and >= 0)
        {
            StartPlayback(index, position, paused);
        }
        else
        {
            StopInternal();
        }
    }

    private static bool OutputChanged(PlayerSettings a, PlayerSettings b) =>
        a.Output.BackendId != b.Output.BackendId
        || a.Output.DeviceId != b.Output.DeviceId
        || a.Output.Channels != b.Output.Channels
        || a.Output.FirstChannel != b.Output.FirstChannel
        || a.Output.BufferMilliseconds != b.Output.BufferMilliseconds
        || a.Output.LowLatencyFifo != b.Output.LowLatencyFifo
        || a.Output.InstantPause != b.Output.InstantPause
        || a.Output.FileOutputDirectory != b.Output.FileOutputDirectory
        || a.Processing.FifoMilliseconds != b.Processing.FifoMilliseconds
        || a.Processing.GpuPhaseShare != b.Processing.GpuPhaseShare
        || a.Processing.GpuRetimeWhilePlaying != b.Processing.GpuRetimeWhilePlaying;

    /// <summary>
    /// Whether anything changed that the running pipeline cannot be told about, and so needs it built
    /// again.
    ///
    /// The lossy repair belongs here and was once missing from it. Its settings decide whether the
    /// pipeline creates the upscaler and how, and they are read once when it is built, so switching one
    /// while a track played changed the setting, saved it, and did nothing to the sound until the next
    /// track or the next run of the program.
    /// </summary>
    internal static bool ProcessingOptionsChanged(PlayerSettings a, PlayerSettings b)
    {
        if (RestorationChanged(a.Restoration, b.Restoration))
        {
            return true;
        }

        if (a.Processing.MeterAdjustedSource != b.Processing.MeterAdjustedSource
            || a.Processing.ApodizationDetection != b.Processing.ApodizationDetection
            || a.Processing.DspThreads != b.Processing.DspThreads
            || a.Processing.GpuAcceleration != b.Processing.GpuAcceleration
            || a.Processing.GpuDeviceId != b.Processing.GpuDeviceId
            || a.Processing.GpuHighPrecision != b.Processing.GpuHighPrecision
            || a.Processing.GpuForce != b.Processing.GpuForce
            || a.Speakers.Enabled != b.Speakers.Enabled)
        {
            return true;
        }

        if (!b.Speakers.Enabled)
        {
            return false;
        }

        return !a.Speakers.Channels.Select(t => (t.LevelDb, t.DistanceCm))
            .SequenceEqual(b.Speakers.Channels.Select(t => (t.LevelDb, t.DistanceCm)));
    }

    internal static bool RestorationChanged(RestorationSettings a, RestorationSettings b) =>
        a.NeuralUpscaler != b.NeuralUpscaler
        || a.NeuralUpscalerPath != b.NeuralUpscalerPath
        || a.NeuralRestorer != b.NeuralRestorer
        || a.NeuralRestorerPath != b.NeuralRestorerPath
        || a.SourceType != b.SourceType
        || a.UpscalerBandDb != b.UpscalerBandDb
        || a.OutputDelta != b.OutputDelta;

    /// <summary>
    /// Swaps the live pipeline and releases the old one's accelerator. Only the engine thread calls this; the
    /// status readers keep working because disposal frees device memory and leaves the counters intact.
    /// </summary>
    private void SetPipeline(DspPipeline? pipeline)
    {
        DspPipeline? previous = _pipeline;
        _pipeline = pipeline;
        if (!ReferenceEquals(previous, pipeline))
        {
            previous?.Dispose();
        }
    }

    private void StopInternal()
    {
        DisposeTransition();
        CloseDecoder();
        SetPipeline(null);
        CloseStream();
        _draining = false;
        lock (_statusGate)
        {
            _markers.Clear();
        }

        SetState(EngineState.Stopped);
    }

    private bool TryOpenDecoder(int index, [NotNullWhen(true)] out IAudioDecoder? decoder, out TrackMetadata? metadata)
    {
        decoder = null;
        metadata = null;
        QueueItem? item = Queue.Get(index);
        if (item is null)
        {
            return false;
        }

        try
        {
            _captureProcessId = 0;
            if (TestToneDecoder.TryParse(item.Path, out TestToneMode mode))
            {
                decoder = new TestToneDecoder(_settings.Output.Channels, mode);
                return true;
            }

            if (CaptureUri.TryParse(item.Path, out int processId))
            {
                if (_capture is null || !_capture.IsSupported)
                {
                    throw new NotSupportedException(_capture?.UnsupportedReason ?? "This build cannot capture an application.");
                }

                decoder = _capture.Open(processId, _settings.Output.Channels, CaptureOptionsFor(_settings));
                _captureProcessId = processId;
                return true;
            }

            decoder = DecoderFactory.Open(item.Path);
            if (item.Metadata is null)
            {
                try
                {
                    item.Metadata = MetadataReader.Read(item.Path, probeFormat: false);
                }
                catch (Exception ex) when (IsRecoverable(ex))
                {
                    // Tags are optional for playback.
                }
            }

            metadata = item.Metadata;
            item.Error = null;
            return true;
        }
        catch (Exception ex) when (ex is AudioDecoderException or IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            item.Error = ex.Message;
            ReportError(ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Plans a decoder's stream, then settles what depends on its codec.
    ///
    /// Turning the limiter off is a request to leave the signal untouched, and for a lossless file that
    /// is a coherent thing to want. A lossy decode's overs are not the signal: they are what is left
    /// of the harmonics the encoder removed, and left alone they clip into a spray of noise above
    /// 40 kHz on a PCM output, or push a 9th-order modulator unstable on a DSD one. The limiter does
    /// nothing below full scale, so on a file with no overs this changes nothing at all.
    /// </summary>
    private PlaybackPlan PlanFor(IAudioDecoder decoder) => ApplySource(PlanFor(decoder.Format), decoder.CodecName, _settings);

    /// <summary>
    /// The limiter on for a lossy codec; the neural upscaler told whether the source is lossy; and the upscaler
    /// dropped for a lossless source at an output below twice its rate, where there is neither a passband to
    /// correct nor room for the band above it.
    /// </summary>
    internal static PlaybackPlan ApplySource(PlaybackPlan plan, string? codecName, PlayerSettings settings)
    {
        bool coded = LossyCodecs.IsLossy(codecName);
        plan = plan with { SourceCoded = coded, SourceCodec = codecName };
        if (!plan.Limiter && coded)
        {
            plan = plan with { Limiter = true };
        }

        bool lossy = settings.Restoration.SourceType switch
        {
            UpscalerSource.Lossy => true,
            UpscalerSource.Lossless => false,
            _ => coded,
        };

        if (plan.Restore && !lossy)
        {
            plan = plan with
            {
                Restore = false,
                Limiter = settings.Processing.Limiter || coded || plan.UpscaleRate > 0,
                Notes = [.. plan.Notes, "Neural restorer idle: lossless source."],
            };
        }

        if (plan.UpscaleRate == 0)
        {
            return plan;
        }

        // What the restorer hands on is lossless in all but origin, and the upscaler is told so: its passband is left
        // alone and only the band above the source's Nyquist frequency is written.
        bool upscalerLossy = lossy && !plan.Restore;
        if (!upscalerLossy && plan.ProcessingRate < plan.UpscaleRate)
        {
            string reason = plan.Restore ? "restored source" : "lossless source";
            return plan with
            {
                UpscaleRate = 0,
                SourceIsLossy = false,
                Limiter = settings.Processing.Limiter || coded || plan.Restore,
                Notes = [.. plan.Notes, $"Neural upscaler idle: {reason}, output below {AudioRates.Format(plan.UpscaleRate)}."],
            };
        }

        return plan with { SourceIsLossy = upscalerLossy };
    }

    private PlaybackPlan PlanFor(StreamFormat format)
    {
        IAudioBackend backend = _backends.Resolve(_settings.Output.BackendId);
        string key = $"{backend.Id}|{_settings.Output.DeviceId}|{_settings.Output.Channels}";
        if (_capabilities is null || _capabilitiesKey != key)
        {
            _capabilities = backend.GetCapabilities(_settings.Output.DeviceId, _settings.Output.Channels);
            _capabilitiesKey = key;
        }

        DeviceCapabilities probed = _capabilities!;
        DeviceCapabilities capabilities = _nativeDsdRefused
            ? new DeviceCapabilities { MaxChannels = probed.MaxChannels, PcmRates = probed.PcmRates, ContainerBits = probed.ContainerBits, Notes = probed.Notes }
            : probed;
        return OutputPlanner.Plan(format, _settings, backend, capabilities);
    }

    private void EnsureWorkers()
    {
        int desired = DesiredWorkerThreads(_settings);
        if (desired == _workerThreads)
        {
            return;
        }

        _workers.Dispose();
        _workers = new ParallelWorkers(desired);
        _workerThreads = desired;
    }

    private void EnsureStream(PlaybackPlan plan)
    {
        OutputFormat format = plan.Output;

        // A filter that convolves a block at a time sends nothing to the device while it works on one, so the
        // FIFO has to be able to play through that pause however the user has set it.
        int fifoMilliseconds = Math.Max(
            Math.Max(
                _settings.Processing.FifoMilliseconds / (_settings.Output.LowLatencyFifo ? 2 : 1),
                2 * _settings.Output.BufferMilliseconds),
            (int)Math.Ceiling(DspPipeline.RequiredFifoSeconds(plan) * 1000.0));

        if (_stream is not null && _stream.Format == format && _streamFailure is null && _fifoMilliseconds >= fifoMilliseconds)
        {
            return;
        }

        CloseStream();
        IAudioBackend backend = _backends.Resolve(_settings.Output.BackendId);
        // In doubles throughout: frames per second times milliseconds passes two thousand million at high output
        // rates with a long buffer, and in int arithmetic that wraps to a negative, giving a ring of no size.
        long fifoBytes = (long)Math.Ceiling((double)format.CanonicalFrameRate * fifoMilliseconds / 1000.0)
            * format.CanonicalBytesPerFrame;
        var ring = new SpscByteRing((int)Math.Min(fifoBytes, 256L << 20));
        var render = new RenderSource(ring, format, _spaceAvailable, _settings.Output.InstantPause);
        var options = new AudioStreamOptions(_settings.Output.BufferMilliseconds, _settings.Output.FirstChannel, WindowHandle);

        IAudioStream stream = backend.OpenStream(_settings.Output.DeviceId, format, options, render);
        stream.Failed += OnStreamFailed;
        _streamFailure = null;
        _streamStarted = false;
        _backend = backend;
        _ring = ring;
        _render = render;
        _writer = new OutputWriter(ring, _spaceAvailable, format.CanonicalBytesPerFrame, () => Volatile.Read(ref _interrupts) != 0, StartStream);
        _stream = stream;
        _fifoMilliseconds = fifoMilliseconds;

        IReadOnlyList<AudioDevice> devices = backend.GetDevices();
        _deviceName = devices.FirstOrDefault(d => d.Id == _settings.Output.DeviceId)?.Name
            ?? devices.FirstOrDefault(d => d.IsDefault)?.Name
            ?? devices.FirstOrDefault()?.Name;
    }

    private void OnStreamFailed(object? sender, Exception exception)
    {
        _streamFailure = exception.Message;
        Interlocked.Increment(ref _interrupts);
        _spaceAvailable.Set();
    }

    private void CloseStream()
    {
        IAudioStream? stream = _stream;
        _stream = null;
        if (stream is not null)
        {
            stream.Failed -= OnStreamFailed;
            try
            {
                stream.Stop();
            }
            finally
            {
                stream.Dispose();
            }
        }

        _streamStarted = false;
        _ring = null;
        _render = null;
        _writer = null;
        _streamFailure = null;
    }

    /// <summary>Starts a newly opened device (once the FIFO is primed, or as soon as the writer would have to wait).</summary>
    private void StartStream()
    {
        if (!_streamStarted && _stream is { } stream)
        {
            stream.Start();
            _streamStarted = true;
        }
    }

    private void ClearRing()
    {
        SpscByteRing? ring = _ring;
        if (ring is null)
        {
            return;
        }

        if (!_streamStarted)
        {
            ring.ClearNow();
            return;
        }

        ring.RequestClear();
        long deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 2;
        while (ring.IsClearPending && Stopwatch.GetTimestamp() < deadline)
        {
            Thread.Sleep(1);
        }

        if (ring.IsClearPending)
        {
            ring.ClearNow();
        }
    }

    private void AllocateBlocks(StreamFormat format, int block)
    {
        int channels = format.Channels;
        if (format.IsDsd)
        {
            if (_dsdBlock.Length != channels || _dsdBlock[0].Length < block + 2)
            {
                _dsdBlock = new byte[channels][];
                for (int c = 0; c < channels; c++)
                {
                    _dsdBlock[c] = new byte[block + 2];
                }
            }
        }
        else if (_pcmBlock.Length != channels || _pcmBlock[0].Length < block)
        {
            _pcmBlock = new double[channels][];
            for (int c = 0; c < channels; c++)
            {
                _pcmBlock[c] = new double[block];
            }
        }
    }

    private void CloseDecoder()
    {
        _decoder?.Dispose();
        _decoder = null;
    }

    private void DisposeTransition()
    {
        _transition?.Decoder.Dispose();
        _transition = null;
    }

    private (Marker? Marker, TimeSpan Position) ComputePlayback()
    {
        SpscByteRing? ring = _ring;
        IAudioStream? stream = _stream;
        DspPipeline? pipeline = _pipeline;
        lock (_statusGate)
        {
            if (_markers.Count == 0)
            {
                return (null, TimeSpan.Zero);
            }

            if (ring is null || stream is null)
            {
                return (_markers[^1], TimeSpan.Zero);
            }

            OutputFormat format = stream.Format;
            double bytesPerSecond = (double)format.CanonicalFrameRate * format.CanonicalBytesPerFrame;
            double latency = stream.LatencySeconds + (pipeline?.LatencySeconds ?? 0.0);
            double played = ring.ReadTotal - latency * bytesPerSecond;
            Marker marker = _markers[0];
            foreach (Marker candidate in _markers)
            {
                if (candidate.WriteBytes <= played)
                {
                    marker = candidate;
                }
            }

            double seconds = (double)marker.SourcePosition / marker.SampleRate + Math.Max(0.0, played - marker.WriteBytes) / bytesPerSecond;
            if (marker.Length > 0)
            {
                seconds = Math.Min(seconds, (double)marker.Length / marker.SampleRate);
            }

            return (marker, TimeSpan.FromSeconds(Math.Max(0.0, seconds)));
        }
    }

    private void UpdateCurrentIndex()
    {
        (Marker? marker, _) = ComputePlayback();
        if (marker is null || marker.Index == Volatile.Read(ref _reportedIndex))
        {
            return;
        }

        Volatile.Write(ref _reportedIndex, marker.Index);
        DspPipeline? pipeline = _pipeline;
        lock (_statusGate)
        {
            _baseline = pipeline is null
                ? default
                : new CounterBaseline(pipeline.LimiterEvents, pipeline.ApodizationEvents, pipeline.ClippedSamples, pipeline.ModulatorResets);
        }

        CurrentIndexChanged?.Invoke(this, marker.Index);
    }

    private double ReplayGainFor(TrackMetadata? metadata)
    {
        PlaybackSettings playback = _settings.Playback;
        if (playback.ReplayGain == ReplayGainMode.Off || metadata is null)
        {
            return 1.0;
        }

        bool album = playback.ReplayGain == ReplayGainMode.Album;
        double? gain = album ? metadata.AlbumGainDb ?? metadata.TrackGainDb : metadata.TrackGainDb ?? metadata.AlbumGainDb;
        double? peak = album ? metadata.AlbumPeak ?? metadata.TrackPeak : metadata.TrackPeak ?? metadata.AlbumPeak;
        if (gain is null)
        {
            return 1.0;
        }

        double linear = Math.Pow(10.0, gain.Value / 20.0);
        if (playback.PreventReplayGainClipping && peak is > 0.0)
        {
            linear = Math.Min(linear, 1.0 / peak.Value);
        }

        return linear;
    }

    private double CurrentVolumeLinear(PlaybackPlan plan)
    {
        if (plan.PassThrough)
        {
            return 1.0;
        }

        double linear = _settings.Volume.IsBypassed ? 1.0 : Math.Pow(10.0, Volatile.Read(ref _volumeDb) / 20.0);
        return _invert ? -linear : linear;
    }

    private void ApplyLiveGain()
    {
        DspPipeline? pipeline = _pipeline;
        pipeline?.SetVolume(CurrentVolumeLinear(pipeline.Plan));
    }

    private void SetState(EngineState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        StateChanged?.Invoke(this, state);
    }

    private void ReportError(string message)
    {
        _lastError = message;
        ErrorOccurred?.Invoke(this, message);
    }

    private sealed record Marker(long WriteBytes, int Index, QueueItem? Item, long SourcePosition, int SampleRate, long Length, bool IsTestTone, bool IsCapture);

    private readonly record struct CounterBaseline(long Limiter, long Apodization, long Clipped, long Resets);

    private sealed class PendingTransition(int index, IAudioDecoder decoder, TrackMetadata? metadata, PlaybackPlan plan)
    {
        public int Index { get; } = index;

        public IAudioDecoder Decoder { get; } = decoder;

        public TrackMetadata? Metadata { get; } = metadata;

        public PlaybackPlan Plan { get; } = plan;

        public bool Flushed { get; set; }
    }

    private abstract record Command;

    private sealed record PlayCommand(int Index, TimeSpan Start) : Command;

    private sealed record StopCommand : Command;

    private sealed record SeekCommand(TimeSpan Position) : Command;

    private sealed record SkipCommand(int Direction) : Command;

    private sealed record SettingsCommand(PlayerSettings Settings) : Command;

    private sealed record QueueOptionsCommand(RepeatMode Repeat, bool Shuffle) : Command;

    private sealed record TestToneCommand(TestToneMode Mode) : Command;

    private sealed record CaptureCommand(int ProcessId) : Command;

    private sealed record ShutdownCommand : Command;
}
