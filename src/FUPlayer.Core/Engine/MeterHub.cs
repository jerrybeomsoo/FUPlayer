using FUPlayer.Core.Dsp.Analysis;
using FUPlayer.Core.Dsp.Numerics;

namespace FUPlayer.Core.Engine;

/// <summary>
/// Meters of the running pipeline: source levels and spectrum, and post-processing output levels and spectrum.
///
/// The pipeline processes audio ahead of the device by the length of the FIFO, and with a DSP server a block's result
/// comes back a few hundred milliseconds after it was read; fed as they were processed, the meters would run ahead of
/// what is heard and move in bursts. So each block's readings are queued with the time it will be heard, its levels and
/// its samples for the spectrum, already filtered to the band shown, and they reach the meters only when the device has
/// played that far.
/// </summary>
public sealed class MeterHub
{
    /// <summary>Readings kept for longer than this without the device playing them are let through, as when no device is consuming.</summary>
    private const double MaximumHeldSeconds = 10.0;

    private readonly object _gate = new();
    private readonly Queue<Reading> _source = new();
    private readonly Queue<Reading> _output = new();

    private sealed record Reading(double Time, int Channel, int Samples, double Peak, double MeanSquare,
        (double[] Samples, AnalysisDecimator.Design Design)? Spectrum);

    public MeterHub(int sourceChannels, int sourceRate, int outputChannels, int processingRate)
    {
        SourceRate = sourceRate;
        ProcessingRate = processingRate;
        Source = new LevelMeter(sourceChannels, sourceRate);
        Output = processingRate > 0 ? new LevelMeter(outputChannels, processingRate) : null;
        Spectrum = new SpectrumAnalyzer(sourceChannels, sourceRate);
        OutputSpectrum = processingRate > 0 ? new SpectrumAnalyzer(outputChannels, processingRate) { IsEnabled = false } : null;
    }

    public int SourceRate { get; }

    /// <summary>Rate of the output meters (0 when there is no PCM processing stage).</summary>
    public int ProcessingRate { get; }

    public LevelMeter Source { get; }

    /// <summary>Levels after volume, limiter and resampling (inter-sample peaks included); null for DSD pass-through.</summary>
    public LevelMeter? Output { get; }

    public SpectrumAnalyzer Spectrum { get; }

    /// <summary>
    /// Spectrum of the processed PCM just before dither or modulation; null for DSD pass-through. Starts disabled because
    /// feeding it at high processing rates is not free; a display enables it while it is shown.
    /// </summary>
    public SpectrumAnalyzer? OutputSpectrum { get; }

    /// <summary>A source block of one channel, heard at <paramref name="time"/> seconds into the pipeline's output.</summary>
    internal void QueueSource(double time, int channel, ReadOnlySpan<double> samples) =>
        Queue(_source, time, channel, samples, Spectrum);

    /// <summary>An output block of one channel, its last sample heard at <paramref name="time"/> seconds into the pipeline's output.</summary>
    internal void QueueOutput(double time, int channel, ReadOnlySpan<double> samples)
    {
        if (Output is not null)
        {
            Queue(_output, time, channel, samples, OutputSpectrum);
        }
    }

    /// <summary>Lets the readings through that the device has played: those at or before <paramref name="played"/> seconds.</summary>
    internal void Release(double played)
    {
        lock (_gate)
        {
            Drain(_source, played, Source, Spectrum);
            if (Output is not null)
            {
                Drain(_output, played, Output, OutputSpectrum!);
            }
        }
    }

    /// <summary>Forgets the readings not yet heard (a seek, or a new start).</summary>
    internal void Discard()
    {
        lock (_gate)
        {
            _source.Clear();
            _output.Clear();
        }
    }

    private void Queue(Queue<Reading> queue, double time, int channel, ReadOnlySpan<double> samples, SpectrumAnalyzer? analyzer)
    {
        if (samples.IsEmpty)
        {
            return;
        }

        var reading = new Reading(time, channel, samples.Length, SimdMath.MaxAbs(samples), SimdMath.SumOfSquares(samples) / samples.Length,
            analyzer?.Prepare(channel, samples));
        lock (_gate)
        {
            queue.Enqueue(reading);

            // Nothing playing them: a render, or a device that has stopped. The oldest go through rather than pile up.
            while (queue.Count > 0 && queue.Peek().Time < time - MaximumHeldSeconds)
            {
                Apply(queue.Dequeue(), queue == _source ? Source : Output!, queue == _source ? Spectrum : OutputSpectrum!);
            }
        }
    }

    private static void Drain(Queue<Reading> queue, double played, LevelMeter meter, SpectrumAnalyzer analyzer)
    {
        while (queue.Count > 0 && queue.Peek().Time <= played)
        {
            Apply(queue.Dequeue(), meter, analyzer);
        }
    }

    private static void Apply(Reading reading, LevelMeter meter, SpectrumAnalyzer analyzer)
    {
        meter.ProcessStatistics(reading.Channel, reading.Samples, reading.Peak, reading.MeanSquare);
        if (reading.Spectrum is { } spectrum)
        {
            analyzer.Commit(reading.Channel, spectrum.Samples, spectrum.Design);
        }
    }
}
