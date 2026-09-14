using System.Numerics;

namespace FUPlayer.Core.Dsp.Resampling;

/// <summary>One band of a filter's taps, convolved in blocks of its own length.</summary>
/// <param name="Offset">First tap of the band, counted per phase from the start of the filter.</param>
/// <param name="Block">Input samples per block here; always the head block times a power of two.</param>
/// <param name="Count">Partitions of <see cref="Block"/> taps the band covers.</param>
public readonly record struct PartitionLevel(int Offset, int Block, int Count)
{
    /// <summary>One past the last tap the band covers.</summary>
    public int End => Offset + (Count * Block);

    /// <summary>Transform length the band works in: two blocks, so the wrap-around half can be thrown away.</summary>
    public int FftSize => 2 * Block;
}

/// <summary>
/// How a filter's taps are divided into blocks. The early taps go in short blocks, so the stage can answer
/// quickly; later taps go in longer and longer ones, which cost far less per tap and can afford to wait because
/// nothing they contribute is due yet.
/// </summary>
/// <remarks>
/// <para>
/// The rule that makes it work is a single inequality. A band whose first tap is at <c>offset</c> only ever
/// contributes to output that far behind the newest input, so it may convolve in blocks of up to
/// <c>offset + head</c> samples and still never be late. Convolving in one uniform block, by contrast, forces
/// every tap, including the first, to wait for a whole block of input, which is where a long filter's wait
/// comes from.
/// </para>
/// <para>
/// Choosing the bands is a small dynamic program over the same cost model the uniform stage uses, because the
/// trade is real in both directions: every extra band is another forward transform and another inverse per
/// phase, and with a large interpolation factor those inverses are most of the bill. Where they outweigh the
/// partition multiplies they save, the answer that comes back is a single band, which is uniform partitioning.
/// </para>
/// </remarks>
public sealed class PartitionPlan
{
    private PartitionPlan(PartitionLevel[] levels, int headBlock, double cost)
    {
        Levels = levels;
        HeadBlock = headBlock;
        CostPerOutputSample = cost;
        LongestBlock = levels[^1].Block;
    }

    /// <summary>The bands, in tap order; the first starts at tap 0 and the last covers the tail.</summary>
    public IReadOnlyList<PartitionLevel> Levels { get; }

    /// <summary>Input samples the stage holds before it can answer: the shortest block, used by the first band.</summary>
    public int HeadBlock { get; }

    /// <summary>Longest block any band uses.</summary>
    public int LongestBlock { get; }

    /// <summary>Modelled multiply-adds per output sample for the whole plan.</summary>
    public double CostPerOutputSample { get; }

    /// <summary>Whether this is plain uniform partitioning: one block length for every tap.</summary>
    public bool IsUniform => Levels.Count == 1;

    /// <summary>
    /// What one band's transforms cost per output sample: one forward for the input and one inverse per phase,
    /// spread over the block's worth of output. It depends on the block length alone, not on how many taps the
    /// band holds, which is why a band that covers few taps is expensive and bands are worth merging.
    /// </summary>
    /// <remarks>
    /// The transforms are of real samples and are carried out as complex ones of half the length plus a pass to
    /// untangle them (<see cref="Numerics.RealFftPlan"/>), so a transform over a block of 2n real points costs
    /// 5·n·log2(n) for the transform and about 6·n for the untangling, not 5·2n·log2(2n).
    /// </remarks>
    public static double TransformCost(int block, int up) =>
        (1 + up) * ((5.0 * Math.Log2(block)) + 6.0) / up;

    /// <summary>What one more partition costs per output sample: a complex multiply-add over the block's bins.</summary>
    public static double PartitionCost(int block) => 6.0 * (block + 1) / block;

    /// <summary>Multiply-adds per output sample for a band of <paramref name="count"/> partitions.</summary>
    public static double LevelCost(int block, int count, int up) =>
        TransformCost(block, up) + (count * PartitionCost(block));

    /// <summary>
    /// The cheapest division of <paramref name="tapsPerPhase"/> taps that still answers within
    /// <paramref name="headBlock"/> input samples.
    /// </summary>
    /// <param name="headBlock">Shortest block, and therefore the wait the plan is allowed to add.</param>
    /// <param name="longestBlock">Longest block any band may use.</param>
    public static PartitionPlan Choose(int tapsPerPhase, int up, int headBlock, int longestBlock)
    {
        headBlock = Math.Max(1, headBlock);
        up = Math.Max(1, up);
        int units = Math.Max(1, (Math.Max(1, tapsPerPhase) + headBlock - 1) / headBlock);

        // Bands are the head block times a power of two. Anything else would land a band's output between two of
        // the head block's, where there is nothing to add it to.
        int widest = 1;
        while (widest * 2 <= units && (long)widest * 2 * headBlock <= longestBlock)
        {
            widest *= 2;
        }

        int sizes = BitOperations.Log2((uint)widest) + 1;

        // f[o] is the cheapest way to cover the taps from unit o onwards. h[k][o] is the same but with a band of
        // 2^k units already open, so reaching unit o costs one more partition instead of a whole new transform.
        // Both look one band ahead of themselves, so the sweep runs backwards from the end of the filter.
        var f = new double[units + 1];
        var open = new double[sizes][];
        var pick = new int[units];
        for (int k = 0; k < sizes; k++)
        {
            open[k] = new double[units + 1];
        }

        for (int o = units - 1; o >= 0; o--)
        {
            for (int k = 0; k < sizes; k++)
            {
                int step = 1 << k;
                int next = o + step;
                double rest = next >= units ? 0.0 : Math.Min(f[next], open[k][next]);
                open[k][o] = PartitionCost(step * headBlock) + rest;
            }

            double best = double.MaxValue;
            int chosen = 0;
            for (int k = 0; k < sizes; k++)
            {
                // A band may only use blocks it can fill without ever being late: its first tap has to be far
                // enough back that the extra input the longer block needs has already arrived.
                int step = 1 << k;
                if (step > o + 1)
                {
                    break;
                }

                double cost = TransformCost(step * headBlock, up) + open[k][o];
                if (cost < best)
                {
                    best = cost;
                    chosen = k;
                }
            }

            f[o] = best;
            pick[o] = chosen;
        }

        // Walk the choices back out, following the same comparison the sweep made: keep the band open while that
        // is cheaper than starting a new one here.
        var levels = new List<PartitionLevel>();
        int at = 0;
        while (at < units)
        {
            int k = pick[at];
            int step = 1 << k;
            int cursor = at;
            int count = 0;
            while (true)
            {
                count++;
                cursor += step;
                if (cursor >= units || open[k][cursor] >= f[cursor])
                {
                    break;
                }
            }

            levels.Add(new PartitionLevel(at * headBlock, step * headBlock, count));
            at = cursor;
        }

        return new PartitionPlan([.. levels], headBlock, f[0]);
    }

    /// <summary>A short phrase for the stage description: how many bands there are and how far they spread.</summary>
    public override string ToString() => IsUniform
        ? $"{Levels[0].Count:N0} × {Levels[0].FftSize:N0}-point FFT"
        : $"{Levels.Count} layers, {2 * HeadBlock:N0}- to {2 * LongestBlock:N0}-point FFT";
}
