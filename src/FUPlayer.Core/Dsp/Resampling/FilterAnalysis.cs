using FUPlayer.Core.Dsp.Design;
using FUPlayer.Core.Localization;

namespace FUPlayer.Core.Dsp.Resampling;

/// <summary>Measured behaviour of a complete resampler chain for display.</summary>
public sealed record FilterAnalysisResult
{
    public required string Summary { get; init; }

    public required int InputRate { get; init; }

    public required int OutputRate { get; init; }

    /// <summary>Overall magnitude response of the chain (up to twice the source Nyquist frequency).</summary>
    public required ResponseCurve Magnitude { get; init; }

    /// <summary>Time axis in milliseconds relative to the impulse peak.</summary>
    public required double[] TimeMilliseconds { get; init; }

    public required double[] Impulse { get; init; }

    public required double[] Step { get; init; }

    /// <summary>The filters' own group delay: part of what they do, and not affected by how they are convolved.</summary>
    public double LatencyMilliseconds { get; init; }

    /// <summary>Delay added by convolving in blocks, on top of the group delay. Zero for tap by tap.</summary>
    public double BlockDelayMilliseconds { get; init; }

    /// <summary>Multiply-adds per second for one channel.</summary>
    public double OperationsPerSecond { get; init; }

    /// <summary>What tap-at-a-time convolution would cost, for comparison with <see cref="OperationsPerSecond"/>.</summary>
    public double DirectOperationsPerSecond { get; init; }

    /// <summary>Taps of the longest FIR in the chain.</summary>
    public int Taps { get; init; }

    public IReadOnlyList<string> Stages { get; init; } = [];
}

/// <summary>
/// Computes the impulse, step and frequency response of a filter preset for given rates: the chosen filter's from its
/// coefficients, and whatever follows it in the chain by running it.
/// </summary>
public static class FilterAnalysis
{
    /// <summary>Input samples run through a chain at a time, so a cancelled analysis stops within one of them.</summary>
    private const int Piece = 1 << 16;

    public static FilterAnalysisResult Analyze(
        FilterPreset preset,
        int inputRate,
        int outputRate,
        StagingMode staging,
        int taps = 0,
        ConvolutionMode convolution = ConvolutionMode.Automatic,
        int points = 1600,
        ConvolutionOptions options = default,
        CancellationToken cancellation = default)
    {
        if (preset.Family == FilterFamily.None || inputRate == outputRate)
        {
            double[] frequencies = Enumerable.Range(0, points).Select(i => outputRate / 2.0 * i / (points - 1)).ToArray();
            return new FilterAnalysisResult
            {
                Summary = Loc.T("No rate conversion: the signal is passed through unchanged."),
                InputRate = inputRate,
                OutputRate = outputRate,
                Magnitude = new ResponseCurve(frequencies, new double[points], new double[points]),
                TimeMilliseconds = [-0.1, 0.0, 0.1],
                Impulse = [0.0, 1.0, 0.0],
                Step = [0.0, 1.0, 1.0],
            };
        }

        ResamplerChain chain = ResamplerFactory.Create(preset, inputRate, outputRate, staging, taps, convolution, options);
        cancellation.ThrowIfCancellationRequested();
        double ratio = (double)outputRate / inputRate;
        double[] impulse = ChainImpulse(chain, cancellation);
        int produced = impulse.Length;
        for (int i = 0; i < produced; i++)
        {
            impulse[i] /= ratio;
        }

        cancellation.ThrowIfCancellationRequested();
        double maxFrequency = outputRate > inputRate ? Math.Min(outputRate / 2.0, inputRate) : outputRate / 2.0;
        ResponseCurve magnitude = FilterResponse.Fir(impulse, outputRate, maxFrequency, points);
        cancellation.ThrowIfCancellationRequested();

        int peak = FirDesign.PeakIndex(impulse);
        double threshold = Math.Abs(impulse[peak]) * 1e-4;
        int first = 0;
        while (first < peak && Math.Abs(impulse[first]) < threshold)
        {
            first++;
        }

        int last = produced - 1;
        while (last > peak && Math.Abs(impulse[last]) < threshold)
        {
            last--;
        }

        int padding = Math.Max(8, (last - first) / 10);
        first = Math.Max(0, first - padding);
        last = Math.Min(produced - 1, last + padding);

        double total = FirDesign.Sum(impulse);
        double running = FirDesign.Sum(impulse.AsSpan(0, first));
        int length = last - first + 1;
        var time = new double[length];
        var window = new double[length];
        var step = new double[length];
        for (int i = 0; i < length; i++)
        {
            int n = first + i;
            running += impulse[n];
            time[i] = (n - peak) * 1000.0 / outputRate;
            window[i] = impulse[n];
            step[i] = total != 0.0 ? running / total : running;
        }

        return new FilterAnalysisResult
        {
            Summary = chain.Summary,
            InputRate = inputRate,
            OutputRate = outputRate,
            Magnitude = magnitude,
            TimeMilliseconds = time,
            Impulse = window,
            Step = step,
            LatencyMilliseconds = chain.DelayOutputSamples * 1000.0 / outputRate,
            BlockDelayMilliseconds = chain.FlushInputSamples * 1000.0 / chain.InputRate,
            OperationsPerSecond = chain.OperationsPerInputSecond,
            DirectOperationsPerSecond = chain.DirectOperationsPerInputSecond,
            Taps = chain.Taps,
            Stages = chain.Stages.Select(s => s.Description).ToArray(),
        };
    }

    /// <summary>
    /// What the chain puts out for a unit impulse. The first stage is the chosen filter and the only long one, and it
    /// answers from its coefficients; what follows it (half-band stages, a bridge between the rate families) is short
    /// and is run. Running the impulse through the first stage as well took minutes for tens of millions of taps at a
    /// rational ratio, where each output sample is a tap-by-tap sum over a whole phase and the run had been sized in
    /// the prototype's taps rather than in output samples: 33,554,432 taps at 44.1 → 48 kHz asked for 42 million
    /// output samples, fifteen minutes of audio, each 210,000 taps long.
    /// </summary>
    private static double[] ChainImpulse(ResamplerChain chain, CancellationToken cancellation)
    {
        IRateStage first = chain.Stages[0];
        double[]? head = first.ImpulseResponse();
        if (head is null)
        {
            // A recursive first stage has no coefficients to read: run the whole chain for long enough to cover its
            // decay. The window has to cover the whole impulse, and the group delay alone does not say how long that is:
            // a minimum-phase filter has almost no delay, which is why the taps count as well.
            double span = Math.Max(chain.DelayOutputSamples * 3.0, chain.Taps * 1.25);
            int length = (int)Math.Ceiling(span * chain.InputRate / chain.OutputRate) + (2 * chain.FlushInputSamples) + 1024;
            var impulse = new double[length];
            impulse[0] = 1.0;
            return Run(chain, impulse, cancellation);
        }

        if (chain.Stages.Count == 1)
        {
            return head;
        }

        // The rest, from the first stage's output rate, with room behind the first stage's response for its own delay
        // and for the blocks it may hold back.
        var rest = new ResamplerChain(first.OutputRate, chain.OutputRate, chain.Stages.Skip(1).ToArray(), string.Empty);
        double restRatio = (double)rest.OutputRate / rest.InputRate;
        int tail = (int)Math.Ceiling(rest.DelayOutputSamples * 3.0 / restRatio) + (2 * rest.FlushInputSamples) + 1024;
        var input = new double[head.Length + tail];
        head.CopyTo(input, 0);
        return Run(rest, input, cancellation);
    }

    /// <summary>Runs samples through a chain a piece at a time, so a cancelled analysis stops within one.</summary>
    private static double[] Run(ResamplerChain chain, double[] input, CancellationToken cancellation)
    {
        ResamplerChainState state = chain.CreateState();
        var piece = new double[chain.MaxOutput(Piece)];
        var output = new double[chain.MaxOutput(input.Length) + piece.Length];
        int produced = 0;
        for (int at = 0; at < input.Length; at += Piece)
        {
            cancellation.ThrowIfCancellationRequested();
            int count = state.Process(input.AsSpan(at, Math.Min(Piece, input.Length - at)), piece);
            if (produced + count > output.Length)
            {
                Array.Resize(ref output, Math.Max(output.Length * 2, produced + count));
            }

            piece.AsSpan(0, count).CopyTo(output.AsSpan(produced));
            produced += count;
        }

        return output[..produced];
    }
}
