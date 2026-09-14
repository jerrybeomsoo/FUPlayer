using System.Diagnostics;
using FUPlayer.Core.Dsp.Resampling;

namespace FUPlayer.Core.Dsp.Acceleration;

/// <summary>
/// Times a device against the processor on the same filter, so the filtering ends up wherever it is actually
/// done faster.
/// </summary>
/// <remarks>
/// A graphics device is not automatically the quicker place for a filter. Consumer cards do 64-bit arithmetic at
/// a fraction of their 32-bit rate, often a thirty-second of it, and a processor with every core sweeping the
/// coefficients can beat them outright, while the same card in 32-bit wins comfortably. Rather than guess from
/// the card's name, this runs the same block through both and answers with what it measured. The two alternate
/// so that neither is favoured by the machine warming up over the course of the test.
/// </remarks>
internal static class GpuRace
{
    /// <summary>How much faster the device has to be before moving the work onto it is worth doing at all.</summary>
    private const double Margin = 1.15;

    private const int WarmUpBlocks = 2;
    private const int TimedBlocks = 4;

    /// <summary>
    /// Runs blocks through both and returns whether the device deserves the work. Both are left reset, so the
    /// race leaves no trace of itself in what is played afterwards.
    /// </summary>
    /// <param name="advantage">How many times faster the device turned out to be; below one it lost.</param>
    /// <remarks>
    /// One channel is raced against one channel. Both sides are shared between the channels in the same way:
    /// the device queues them one behind another, and the processor has already divided its cores between them
    /// in <paramref name="processor"/>'s thread count. The ratio a single channel gives is therefore the ratio
    /// they all give together.
    /// </remarks>
    public static bool DeviceWins(
        IRateStage stage, IRateStageState device, IRateStageState processor, out double advantage)
    {
        // Roughly the ten-millisecond block the pipeline feeds a stage, and never less than a block-based stage
        // holds back, or it would be timed producing nothing at all.
        int block = Math.Clamp(Math.Max(stage.InputRate / 100, stage.FlushInputSamples * 2), 256, 8192);
        var random = new Random(0x51DE);
        var input = new double[block];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = random.NextDouble() * 2.0 - 1.0;
        }

        var scratch = new double[stage.MaxOutput(block) + 16];
        for (int i = 0; i < WarmUpBlocks; i++)
        {
            device.Process(input, scratch);
            processor.Process(input, scratch);
        }

        double deviceSeconds = 0.0;
        double processorSeconds = 0.0;
        for (int i = 0; i < TimedBlocks; i++)
        {
            long start = Stopwatch.GetTimestamp();
            device.Process(input, scratch);
            long middle = Stopwatch.GetTimestamp();
            processor.Process(input, scratch);
            long end = Stopwatch.GetTimestamp();
            deviceSeconds += Stopwatch.GetElapsedTime(start, middle).TotalSeconds;
            processorSeconds += Stopwatch.GetElapsedTime(middle, end).TotalSeconds;
        }

        device.Reset();
        processor.Reset();
        advantage = deviceSeconds > 0.0 ? processorSeconds / deviceSeconds : 0.0;
        return advantage >= Margin;
    }
}
