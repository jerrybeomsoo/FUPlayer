using FUPlayer.Core.Dsp.Acceleration;

namespace FUPlayer.Core.Dsp.Resampling;

/// <summary>Immutable sequence of rate stages converting <see cref="InputRate"/> to <see cref="OutputRate"/>.</summary>
public sealed class ResamplerChain
{
    public ResamplerChain(int inputRate, int outputRate, IReadOnlyList<IRateStage> stages, string summary)
    {
        InputRate = inputRate;
        OutputRate = outputRate;
        Stages = stages;
        Summary = summary;

        int rate = inputRate;
        foreach (IRateStage stage in stages)
        {
            if (stage.InputRate != rate)
            {
                throw new ArgumentException($"Stage '{stage.Description}' expects {stage.InputRate} Hz but receives {rate} Hz.");
            }

            rate = stage.OutputRate;
        }

        if (rate != outputRate)
        {
            throw new ArgumentException($"Stages end at {rate} Hz instead of {outputRate} Hz.");
        }
    }

    public int InputRate { get; }

    public int OutputRate { get; }

    public IReadOnlyList<IRateStage> Stages { get; }

    public string Summary { get; }

    public bool IsIdentity => Stages.Count == 0;

    /// <summary>Total delay expressed in output samples.</summary>
    public double DelayOutputSamples => Stages.Sum(s => s.DelayOutputSamples * ((double)OutputRate / s.OutputRate));

    /// <summary>Multiply-adds per second of input for one channel.</summary>
    public double OperationsPerInputSecond => Stages.Sum(s => s.CostPerOutputSample * s.OutputRate);

    /// <summary>What the same chain would cost convolving tap by tap, for comparison with the above.</summary>
    public double DirectOperationsPerInputSecond => Stages.Sum(s => s.DirectCostPerOutputSample * s.OutputRate);

    /// <summary>Taps of the longest FIR in the chain (the filter the user chose), or 0 when there is none.</summary>
    public int Taps => Stages.Count == 0 ? 0 : Stages.Max(s => s.Taps);

    /// <summary>Extra input samples needed to push everything buffered inside block-based stages out.</summary>
    public int FlushInputSamples =>
        (int)Math.Ceiling(Stages.Sum(s => s.FlushInputSamples * (InputRate / (double)s.InputRate)));

    public int MaxOutput(int inputSamples)
    {
        int n = inputSamples;
        foreach (IRateStage stage in Stages)
        {
            n = stage.MaxOutput(n);
        }

        return n;
    }

    /// <param name="parallelism">Threads a single stage may use internally (for one channel of a multi-core pipeline).</param>
    /// <param name="accelerator">Optional OpenCL device for the frequency-domain stages.</param>
    public ResamplerChainState CreateState(int parallelism = 1, GpuAccelerator? accelerator = null) =>
        new(this, parallelism, accelerator);
}

/// <summary>Per-channel streaming state of a <see cref="ResamplerChain"/>.</summary>
public sealed class ResamplerChainState
{
    private readonly ResamplerChain _chain;
    private readonly IRateStageState[] _states;
    private readonly double[][] _scratch;

    internal ResamplerChainState(ResamplerChain chain, int parallelism = 1, GpuAccelerator? accelerator = null)
    {
        _chain = chain;
        _states = chain.Stages
            .Select(s => s switch
            {
                FftPolyphaseStage fft => fft.CreateState(parallelism, accelerator),
                PolyphaseStage direct => direct.CreateState(parallelism, accelerator),
                _ => s.CreateState(parallelism),
            })
            .ToArray();
        _scratch = new double[Math.Max(0, _states.Length - 1)][];
        for (int i = 0; i < _scratch.Length; i++)
        {
            _scratch[i] = [];
        }
    }

    /// <summary>Runs <paramref name="input"/> through every stage; returns the number of output samples.</summary>
    public int Process(ReadOnlySpan<double> input, Span<double> output)
    {
        if (_states.Length == 0)
        {
            input.CopyTo(output);
            return input.Length;
        }

        ReadOnlySpan<double> current = input;
        int produced = 0;
        for (int i = 0; i < _states.Length; i++)
        {
            Span<double> destination;
            if (i == _states.Length - 1)
            {
                destination = output;
            }
            else
            {
                int needed = _chain.Stages[i].MaxOutput(current.Length);
                if (_scratch[i].Length < needed)
                {
                    _scratch[i] = new double[needed];
                }

                destination = _scratch[i];
            }

            produced = _states[i].Process(current, destination);
            current = destination[..produced];
        }

        return produced;
    }

    public void Reset()
    {
        foreach (IRateStageState state in _states)
        {
            state.Reset();
        }
    }
}
