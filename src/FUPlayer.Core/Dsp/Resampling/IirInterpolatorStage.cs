using FUPlayer.Core.Dsp.Filters;

namespace FUPlayer.Core.Dsp.Resampling;

/// <summary>Integer-factor interpolator: zero stuffing followed by an IIR lowpass at the output rate.</summary>
public sealed class IirInterpolatorStage : IRateStage
{
    private readonly Biquad[] _sections;

    public IirInterpolatorStage(int inputRate, int factor, Biquad[] sections, string description)
    {
        if (factor < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(factor));
        }

        InputRate = inputRate;
        Factor = factor;
        OutputRate = checked(inputRate * factor);
        _sections = sections;
        Description = description;
        DelayOutputSamples = GroupDelay(sections, 1000.0 / OutputRate);
    }

    public int InputRate { get; }

    public int OutputRate { get; }

    public int Factor { get; }

    public IReadOnlyList<Biquad> Sections => _sections;

    public double DelayOutputSamples { get; }

    public double CostPerOutputSample => 5.0 * _sections.Length;

    public string Description { get; }

    public int MaxOutput(int inputSamples) => checked(inputSamples * Factor);

    public IRateStageState CreateState() => new State(this);

    private static double GroupDelay(Biquad[] sections, double frequency)
    {
        double delta = frequency * 1e-3;
        double before = Biquad.CascadeResponse(sections, frequency - delta).Phase;
        double after = Biquad.CascadeResponse(sections, frequency + delta).Phase;
        double difference = Math.IEEERemainder(after - before, 2.0 * Math.PI);
        return Math.Max(0.0, -difference / (2.0 * Math.PI * 2.0 * delta));
    }

    private sealed class State : IRateStageState
    {
        private readonly IirInterpolatorStage _stage;
        private readonly SosCascade _cascade;

        public State(IirInterpolatorStage stage)
        {
            _stage = stage;
            _cascade = new SosCascade(stage._sections);
        }

        public int Process(ReadOnlySpan<double> input, Span<double> output)
        {
            int factor = _stage.Factor;
            int written = input.Length * factor;
            Span<double> target = output[..written];
            target.Clear();
            for (int i = 0; i < input.Length; i++)
            {
                target[i * factor] = input[i] * factor;
            }

            _cascade.Process(target);
            return written;
        }

        public void Reset() => _cascade.Reset();
    }
}
