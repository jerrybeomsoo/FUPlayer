namespace FUPlayer.Core.Dsp.Resampling;

/// <summary>Immutable, shareable description of one sample-rate conversion stage.</summary>
public interface IRateStage
{
    int InputRate { get; }

    int OutputRate { get; }

    /// <summary>Delay introduced by the stage (impulse peak), in output samples.</summary>
    double DelayOutputSamples { get; }

    /// <summary>Approximate multiply-adds per output sample, used for DSP load estimates.</summary>
    double CostPerOutputSample { get; }

    /// <summary>
    /// What convolving the same filter one tap at a time would cost per output sample. Equal to
    /// <see cref="CostPerOutputSample"/> unless the stage uses a faster algorithm; the two together show how much
    /// that algorithm saves, which is the only reason a filter of a hundred thousand taps can play in real time.
    /// </summary>
    double DirectCostPerOutputSample => CostPerOutputSample;

    /// <summary>Length of the stage's FIR prototype in taps, or 0 when the stage is not an FIR.</summary>
    int Taps => 0;

    /// <summary>
    /// Input samples the stage may hold before it produces output (block-based stages); 0 when it works
    /// sample by sample. Flushing has to push at least this many extra samples through.
    /// </summary>
    int FlushInputSamples => 0;

    string Description { get; }

    /// <summary>Upper bound of output samples produced from <paramref name="inputSamples"/> input samples.</summary>
    int MaxOutput(int inputSamples);

    /// <summary>Creates per-channel streaming state.</summary>
    IRateStageState CreateState();

    /// <summary>
    /// Creates per-channel streaming state that may divide its own work over <paramref name="parallelism"/>
    /// threads. Channels already run side by side; this is for the cores left over once they have, and it is what
    /// keeps a filter of a hundred thousand taps from leaving most of the processor idle. A stage with nothing to
    /// divide simply ignores it.
    /// </summary>
    IRateStageState CreateState(int parallelism) => CreateState();
}

/// <summary>Per-channel streaming state of an <see cref="IRateStage"/>.</summary>
public interface IRateStageState
{
    /// <summary>Consumes all of <paramref name="input"/> and returns the number of samples written.</summary>
    int Process(ReadOnlySpan<double> input, Span<double> output);

    void Reset();
}
