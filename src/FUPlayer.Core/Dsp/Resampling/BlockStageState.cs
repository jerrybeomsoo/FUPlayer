namespace FUPlayer.Core.Dsp.Resampling;

/// <summary>
/// Input buffering for a stage that convolves a whole block at a time: it collects samples until a block is full,
/// converts it, and hands the block's worth of output back.
/// </summary>
/// <remarks>
/// Collecting the block and emitting its output is the same work whether the transform runs on the processor or on
/// a graphics device; only <see cref="ProcessBlock"/> differs. Keeping one copy of it is what lets the two paths
/// be compared sample for sample, which is how a device earns the right to be used at all.
/// </remarks>
internal abstract class BlockStageState : IRateStageState
{
    private int _count;

    /// <param name="blockSamples">Input samples per block.</param>
    /// <param name="outputsPerBlock">Output samples one block produces.</param>
    protected BlockStageState(int blockSamples, int outputsPerBlock)
    {
        BlockSamples = blockSamples;
        Current = new double[blockSamples];
        Produced = new double[outputsPerBlock];
    }

    /// <summary>Input samples per block.</summary>
    protected int BlockSamples { get; }

    /// <summary>The block being collected; complete when <see cref="ProcessBlock"/> is called.</summary>
    protected double[] Current { get; }

    /// <summary>Where <see cref="ProcessBlock"/> leaves the output of the block it just converted.</summary>
    protected double[] Produced { get; }

    public int Process(ReadOnlySpan<double> input, Span<double> output)
    {
        int written = 0;
        int offset = 0;
        while (offset < input.Length)
        {
            int take = Math.Min(BlockSamples - _count, input.Length - offset);
            input.Slice(offset, take).CopyTo(Current.AsSpan(_count));
            _count += take;
            offset += take;
            if (_count < BlockSamples)
            {
                break;
            }

            ProcessBlock();
            Produced.CopyTo(output[written..]);
            written += Produced.Length;
            _count = 0;
        }

        return written;
    }

    public void Reset()
    {
        _count = 0;
        Array.Clear(Current);
        ResetState();
    }

    /// <summary>Converts <see cref="Current"/> into <see cref="Produced"/> and carries it into the block history.</summary>
    protected abstract void ProcessBlock();

    /// <summary>Clears whatever the implementation remembers between blocks.</summary>
    protected abstract void ResetState();
}
