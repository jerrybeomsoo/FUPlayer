using System.Diagnostics;
using System.Text;
using FUPlayer.Core.Dsp.Acceleration;
using FUPlayer.Core.Dsp.Processing;
using FUPlayer.Core.Dsp.Resampling;
using FUPlayer.Core.Dsp.Restoration;
using FUPlayer.Core.Engine;
using FUPlayer.Core.Localization;

namespace FUPlayer.Core.NetworkDsp;

/// <summary>
/// The server's half of one session: the run of core stages the player asked for, built from the server's own models
/// and devices, and the loop that takes blocks off the link, processes them in order and sends them back.
///
/// The loop never waits on the network between blocks: what arrives while a block is being processed queues on the
/// link, and results leave through the link's own sending thread. A reset makes every block queued in front of it
/// stale, and those are dropped unprocessed. When the player goes, the block being processed is finished before the
/// stages are released, so that nothing is torn down under a running network.
/// </summary>
internal sealed class DspSession : IDisposable
{
    private readonly DspHello _hello;
    private readonly ProcessingCore _core;
    private readonly ParallelWorkers _workers;
    private readonly GpuAccelerator? _accelerator;
    private readonly int _maximumChannels;
    private readonly int _maximumFrames;

    private DspSession(DspHello hello, ProcessingCore core, ParallelWorkers workers, GpuAccelerator? accelerator, DspReady ready)
    {
        _hello = hello;
        _core = core;
        _workers = workers;
        _accelerator = accelerator;
        Ready = ready;
        _maximumChannels = Math.Max(hello.Layout.SourceChannels, hello.Layout.OutputChannels);
        _maximumFrames = (hello.MaximumFrames * 4) + 4096;
    }

    public DspReady Ready { get; }

    /// <summary>Builds the stages a player asked for, or throws with the reason, which the player is told.</summary>
    public static DspSession Open(DspHello hello, DspServerOptions options)
    {
        CoreLayout layout = hello.Layout;
        PlaybackPlan plan = hello.Plan;
        if (hello.Version != DspProtocol.Version)
        {
            throw new InvalidDataException(Loc.F(
                "The DSP server speaks protocol {0} and the player {1}. Update both to the same version.", DspProtocol.Version, hello.Version));
        }

        if (!plan.Source.IsValid || layout.SourceChannels is < 1 or > 8 || layout.OutputChannels is < 1 or > 8
            || plan.FilterTaps is < 0 or > ResamplerFactory.MaxPrototypeLength || hello.MaximumFrames is < 1 or > 1 << 20
            || layout.ProcessingRate is < 1 or > 50_000_000 || hello.First > hello.Last
            || !Enum.IsDefined(hello.First) || !Enum.IsDefined(hello.Last))
        {
            throw new InvalidDataException("The DSP plan is not one this server can build.");
        }

        int budget = Math.Clamp(options.Threads + 1, 1, 64);
        int channelThreads = Math.Min(budget, layout.OutputChannels);
        bool builds(CoreStage stage) => stage >= hello.First && stage <= hello.Last && layout.Has(stage);

        INeuralRestorer? restorer = null;
        string? restorerStatus = null;
        if (builds(CoreStage.Restore))
        {
            NeuralRestorerModel model = ModelLibrary.TryLoadRestorer(
                LocalModel(hello.Restoration.NeuralRestorerPath), options.RestorerDevice, out string? failure, Math.Min(budget, 4), allowSpinning: false)
                ?? throw new InvalidDataException(Loc.F("The DSP server has no neural restorer: {0}", failure));
            var created = new NeuralRestorer(model, plan.Source.SampleRate);
            restorer = created;
            restorerStatus = Path.GetFileName(model.Path) + ". " + InferenceDevices.Describe(options.RestorerDevice, model.Adapter, model.AdapterFailure);
        }

        NeuralUpscalerModel? upscalerModel = null;
        string? upscalerStatus = null;
        if (builds(CoreStage.Upscale) && layout.Upscale)
        {
            upscalerModel = ModelLibrary.TryLoadUpscaler(
                LocalModel(hello.Restoration.NeuralUpscalerPath), options.UpscalerDevice, out string? failure,
                Math.Clamp(budget / channelThreads, 1, 4), allowSpinning: false)
                ?? throw new InvalidDataException(Loc.F("The DSP server has no neural upscaler: {0}", failure));
            upscalerStatus = Path.GetFileName(upscalerModel.Path) + ". "
                + InferenceDevices.Describe(options.UpscalerDevice, upscalerModel.Adapter, upscalerModel.AdapterFailure);
        }

        ResamplerChain? resampler = null;
        GpuAccelerator? accelerator = null;
        string? acceleration = null;
        int filterParallelism = Math.Clamp(budget / channelThreads, 1, 16);
        if (builds(CoreStage.Filter))
        {
            int inputRate = plan.PreUpsampleRate > 0 ? plan.PreUpsampleRate : plan.ConversionRate;
            resampler = ResamplerFactory.Create(plan.Filter!, inputRate, plan.ProcessingRate, plan.Staging, plan.FilterTaps,
                plan.Convolution, ServerConvolution(plan, hello.RoundTripMilliseconds));
            acceleration = Loc.F("Filtering on {0} processor threads ({1} channels, {2} each).", channelThreads * filterParallelism, channelThreads, filterParallelism);
            if (options.GpuAcceleration && resampler.Stages.Any(s => GpuAccelerator.CanAccelerate(s, options.GpuForce)))
            {
                accelerator = GpuAccelerator.TryCreate(options.GpuDevice, options.GpuHighPrecision, out acceleration,
                    options.GpuForce, options.GpuPhaseShare, options.GpuRetime);
                accelerator?.PlanChannels(resampler.Stages, layout.OutputChannels, filterParallelism);
                if (accelerator?.HasFilters == true)
                {
                    filterParallelism = Math.Clamp(budget, 1, 16);
                }
            }
        }

        var workers = new ParallelWorkers(channelThreads - 1);
        try
        {
            var parts = new CoreParts
            {
                Restorer = restorer,
                UpscalerModel = upscalerModel,
                Resampler = resampler,
                FilterParallelism = filterParallelism,
                Accelerator = accelerator,
                Ultrasonic = builds(CoreStage.Map) && layout.Clean ? ProcessingFilters.CreateUltrasonicFilter(layout.SourceRate) : null,
            };
            var core = new ProcessingCore(layout, hello.First, hello.Last, parts, workers);
            var ready = new DspReady(
                restorerStatus, restorer?.Latency ?? 0, upscalerStatus, core.UpscalerLatency,
                resampler?.Summary, accelerator?.Status ?? acceleration,
                resampler?.DelayOutputSamples ?? 0, resampler?.FlushInputSamples ?? 0,
                resampler is null ? 0 : resampler.FlushInputSamples / (double)Math.Max(1, resampler.InputRate),
                resampler?.OperationsPerInputSecond ?? 0);
            return new DspSession(hello, core, workers, accelerator, ready);
        }
        catch
        {
            workers.Dispose();
            accelerator?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Automatic convolution belongs to the machine doing it. A crossover set high on the player must not leave
    /// thousands of tap products per output sample here, so the server keeps its own default. Over the Internet a
    /// frequency-domain block may also hold input as long as a round trip, up to 50 ms: that wait is already spent on
    /// the network, and a longer block costs far less arithmetic.
    /// </summary>
    internal static ConvolutionOptions ServerConvolution(PlaybackPlan plan, double roundTripMilliseconds)
    {
        ConvolutionOptions options = plan.ConvolutionOptions;
        if (plan.Convolution != ConvolutionMode.Automatic)
        {
            return options;
        }

        int threshold = options.ThresholdTapsPerPhase > 0 ? options.ThresholdTapsPerPhase : FftPolyphaseStage.DefaultThresholdTapsPerPhase;
        options = options with { ThresholdTapsPerPhase = Math.Min(threshold, FftPolyphaseStage.DefaultThresholdTapsPerPhase) };
        if (options.MaxBlockMilliseconds > 0 && double.IsFinite(roundTripMilliseconds) && roundTripMilliseconds > 2)
        {
            options = options with { MaxBlockMilliseconds = Math.Max(options.MaxBlockMilliseconds, Math.Min(50, roundTripMilliseconds)) };
        }

        return options;
    }

    /// <summary>Processes blocks until the player says goodbye or the link closes.</summary>
    public void Run(DspLink link, Action<string>? message, CancellationToken cancellation)
    {
        var block = new CoreBlock(_maximumChannels);
        var pending = new Queue<DspLinkMessage>();
        uint epoch = 0;
        CoreStage first = _hello.First;
        CoreStage last = _hello.Last;
        bool exciterHere = _core.Exciter is not null;
        while (!cancellation.IsCancellationRequested)
        {
            while (link.TryReceive(out DspLinkMessage arrived, TimeSpan.Zero))
            {
                pending.Enqueue(arrived);
            }

            if (pending.Count == 0)
            {
                if (link.TryReceive(out DspLinkMessage arrived, TimeSpan.FromMilliseconds(500), cancellation))
                {
                    pending.Enqueue(arrived);
                }

                continue;
            }

            // A reset ahead makes everything in front of it stale.
            if (pending.Any(m => m.Type == (byte)DspMessage.Reset))
            {
                while (pending.Any(m => m.Type == (byte)DspMessage.Reset))
                {
                    DspLinkMessage dropped = pending.Dequeue();
                    if (dropped.Type == (byte)DspMessage.Reset)
                    {
                        epoch = DspProtocol.DecodeEpoch(dropped.Body);
                    }
                    else if (dropped.Type == (byte)DspMessage.Bye)
                    {
                        return;
                    }
                }

                _core.Reset();
                continue;
            }

            DspLinkMessage next = pending.Dequeue();
            switch ((DspMessage)next.Type)
            {
                case DspMessage.Audio:
                {
                    (uint blockEpoch, long index) = DspProtocol.DecodeAudio(next.Body, block, _maximumChannels, _maximumFrames);
                    if (blockEpoch != epoch)
                    {
                        break;
                    }

                    int frames = block.Counts[0];
                    long started = Stopwatch.GetTimestamp();
                    _core.Run(block, first, last);
                    double seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
                    _accelerator?.ReportBlock(seconds, frames);
                    ExciterReport? report = exciterHere && _core.Exciter is { } exciter
                        ? new ExciterReport(exciter.EdgeHz, exciter.StartHz, exciter.AddedDb, exciter.DriveLowHz, exciter.Rate)
                        : null;
                    link.Send((byte)DspMessage.Result, DspProtocol.EncodeResult(epoch, index, block, seconds, report), cancellation);
                    break;
                }

                case DspMessage.Bye:
                    return;
                default:
                    throw new InvalidDataException("An unexpected DSP message arrived.");
            }
        }
    }

    /// <summary>Opens a session on a link: reads the hello, builds the stages, says they are ready, and runs them.</summary>
    public static void Serve(DspLink link, DspServerOptions options, Action<string>? message, CancellationToken cancellation)
    {
        DspSession? session = null;
        try
        {
            DspLinkMessage hello = link.Receive(TimeSpan.FromSeconds(10), cancellation);
            if (hello.Type != (byte)DspMessage.Hello)
            {
                throw new InvalidDataException("A DSP session must begin with a hello.");
            }

            DspHello request = DspProtocol.Decode<DspHello>(hello.Body);
            link.NominalRate = NominalRate(request.Layout, request.Last);

            // The link pinged the player as it opened; the answer says how far away it is, which the filter's block
            // length can use. Nothing waits on it longer than a fraction of a second.
            long asked = Stopwatch.GetTimestamp();
            while (link.RoundTripMilliseconds == 0 && Stopwatch.GetElapsedTime(asked).TotalMilliseconds < 400 && !link.IsClosed)
            {
                Thread.Sleep(2);
            }

            request = request with { RoundTripMilliseconds = link.RoundTripMilliseconds };
            session = Open(request, options);
            link.Send((byte)DspMessage.Ready, DspProtocol.Encode(session.Ready), cancellation);
            message?.Invoke("DSP session opened.");
            session.Run(link, message, cancellation);
            link.Drain(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when ((ex is IOException or OperationCanceledException) && link.IsClosed)
        {
            // The player went away; nothing to tell it.
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or AccessViolationException))
        {
            if (ex is not OperationCanceledException)
            {
                message?.Invoke($"DSP session ended: {ex.Message}");
                try
                {
                    link.Send((byte)DspMessage.Error, Encoding.UTF8.GetBytes(ex.Message));
                    link.Drain(TimeSpan.FromSeconds(1));
                }
                catch (IOException)
                {
                }
            }
        }
        finally
        {
            session?.Dispose();
        }
    }

    /// <summary>What a session sends back a second, in bytes, on average: what the link paces around.</summary>
    internal static double NominalRate(CoreLayout layout, CoreStage last)
    {
        int rate = last >= CoreStage.Filter && layout.Filter ? layout.ProcessingRate
            : last >= CoreStage.Upscale && layout.UpsampleRate > 0 ? layout.UpsampleRate : layout.SourceRate;
        int channels = last >= CoreStage.Map ? layout.OutputChannels : layout.SourceChannels;
        int lanes = last < CoreStage.Excite && layout.Delta ? 2 : 1;
        return (double)rate * channels * lanes * 8;
    }

    private static string? LocalModel(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string name = path.Split('/', '\\').Last();
        if (!name.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase) || name.Contains(':'))
        {
            throw new InvalidDataException("The model asked for is not a model name.");
        }

        return Path.Combine(ModelLibrary.Directory, name);
    }

    public void Dispose()
    {
        _core.Dispose();
        _workers.Dispose();
        _accelerator?.Dispose();
    }
}
