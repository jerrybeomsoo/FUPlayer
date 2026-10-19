using System.Numerics;
using FUPlayer.Core.Engine;
using FUPlayer.Core.Dsp.Resampling;

namespace FUPlayer.Core.NetworkDsp;

/// <summary>Amortises ordered stage round trips while bounding audio messages and playback buffering.</summary>
internal static class DspNetworkBuffering
{
    internal static PlaybackPlan ServerPlan(PlaybackPlan plan, int maximumInput, double roundTripMilliseconds)
    {
        double requested = plan.ConvolutionOptions.MaxBlockMilliseconds;
        if (plan.Convolution != ConvolutionMode.Automatic || maximumInput < 8192 || requested <= 0
            || !double.IsFinite(roundTripMilliseconds) || roundTripMilliseconds <= 2) return plan;
        int inputRate = plan.PreUpsampleRate > 0 ? plan.PreUpsampleRate : plan.ConversionRate;
        // FFT partitioning need not be shorter than propagation when the transfer already contains
        // a much larger audio batch. Stay within a quarter of that advertised input and 50 ms.
        double budget = Math.Min(50, Math.Min(roundTripMilliseconds, maximumInput * 250.0 / Math.Max(1, inputRate)));
        return budget > requested ? plan with { ConvolutionOptions = plan.ConvolutionOptions with { MaxBlockMilliseconds = budget } } : plan;
    }

    internal static int MaximumBlock(PlaybackPlan plan, int minimum)
    {
        int inputRate = plan.PreUpsampleRate > 0 ? plan.PreUpsampleRate : plan.ConversionRate;
        // Leave room for channel headers and filter output rounding. The advertised stage input can be
        // twice a source block, including interpolation in front of the neural upscaler.
        double expansion = Math.Max(1, plan.ProcessingRate / (double)Math.Max(1, inputRate));
        int messageLimit = (int)Math.Max(1, ((16 << 20) / (8.0 * plan.Output.Channels) / expansion - 128) / 2);
        int limit = Math.Min(plan.Source.IsDsd ? 65536 : 32768, messageLimit);
        return Math.Max(minimum, 1 << BitOperations.Log2((uint)Math.Max(1, limit)));
    }

    internal static int SelectBlock(int minimum, int maximum, double inputRate, double roundTripMilliseconds, int remoteStages)
    {
        if (remoteStages <= 0 || !double.IsFinite(roundTripMilliseconds) || roundTripMilliseconds <= 2) return minimum;
        // Spend at most a sixth of each audio block on propagation. Local stages, inference and packet
        // transfer still need headroom. LAN sessions retain the ordinary short processing blocks.
        double frames = Math.Min(maximum, inputRate * roundTripMilliseconds * remoteStages * .006);
        return Math.Clamp((int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(minimum, Math.Ceiling(frames))), minimum, maximum);
    }
}
