using FUPlayer.Core.Dsp.Design;

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

/// <summary>Computes the measured impulse, step and frequency response of a filter preset for given rates.</summary>
public static class FilterAnalysis
{
    public static FilterAnalysisResult Analyze(
        FilterPreset preset,
        int inputRate,
        int outputRate,
        StagingMode staging,
        int taps = 0,
        ConvolutionMode convolution = ConvolutionMode.Automatic,
        int points = 1600,
        ConvolutionOptions options = default)
    {
        if (preset.Family == FilterFamily.None || inputRate == outputRate)
        {
            double[] frequencies = Enumerable.Range(0, points).Select(i => outputRate / 2.0 * i / (points - 1)).ToArray();
            return new FilterAnalysisResult
            {
                Summary = "No rate conversion: the signal is passed through unchanged.",
                InputRate = inputRate,
                OutputRate = outputRate,
                Magnitude = new ResponseCurve(frequencies, new double[points], new double[points]),
                TimeMilliseconds = [-0.1, 0.0, 0.1],
                Impulse = [0.0, 1.0, 0.0],
                Step = [0.0, 1.0, 1.0],
            };
        }

        ResamplerChain chain = ResamplerFactory.Create(preset, inputRate, outputRate, staging, taps, convolution, options);
        double ratio = (double)outputRate / inputRate;
        // The window has to cover the whole impulse, and the group delay alone does not say how long that is: a
        // minimum-phase filter has almost no delay but exactly as many taps as its linear-phase twin. Sizing on the
        // delay cut long minimum-phase filters in half, which made the plotted stopband tens of decibels worse than
        // the filter really is. Block-based stages hold input back on top of that.
        double outputSamples = Math.Max(chain.DelayOutputSamples * 3.0, chain.Taps * 1.25);
        int inputLength = (int)Math.Ceiling(outputSamples / ratio) + 2 * chain.FlushInputSamples + 1024;
        var input = new double[inputLength];
        input[0] = 1.0;
        var output = new double[chain.MaxOutput(inputLength)];
        int produced = chain.CreateState().Process(input, output);

        var impulse = new double[produced];
        for (int i = 0; i < produced; i++)
        {
            impulse[i] = output[i] / ratio;
        }

        double maxFrequency = outputRate > inputRate ? Math.Min(outputRate / 2.0, inputRate) : outputRate / 2.0;
        ResponseCurve magnitude = FilterResponse.Fir(impulse, outputRate, maxFrequency, points);

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
}
