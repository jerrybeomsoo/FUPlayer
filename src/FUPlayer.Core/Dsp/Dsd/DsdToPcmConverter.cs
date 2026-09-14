using FUPlayer.Core.Audio;
using FUPlayer.Core.Dsp.Design;
using FUPlayer.Core.Dsp.Resampling;

namespace FUPlayer.Core.Dsp.Dsd;

/// <summary>
/// Immutable DSD → PCM conversion design.
/// Stage A decimates by 8 with a byte-lookup FIR (one table per input byte); stage B applies the selected
/// noise filter and decimates to the output rate with a polyphase FIR.
/// </summary>
public sealed class DsdToPcmDesign
{
    private DsdToPcmDesign(int dsdRate, int outputRate, double[][] tables, int tableBytes, ResamplerChain stageB, double gain, string description)
    {
        DsdRate = dsdRate;
        OutputRate = outputRate;
        Tables = tables;
        TableBytes = tableBytes;
        StageB = stageB;
        Gain = gain;
        Description = description;
        double stageADelay = (8.0 * tableBytes - 1.0) / 2.0 / 8.0;
        DelayOutputSamples = stageADelay * ((double)outputRate / (dsdRate / 8)) + stageB.DelayOutputSamples;
    }

    public int DsdRate { get; }

    public int OutputRate { get; }

    /// <summary>Linear gain (1.0 maps 100 % modulation to PCM full scale; DSD's 0 dB reference is 50 %).</summary>
    public double Gain { get; }

    public double DelayOutputSamples { get; }

    public string Description { get; }

    internal double[][] Tables { get; }

    internal int TableBytes { get; }

    internal ResamplerChain StageB { get; }

    /// <param name="dsdRate">1-bit rate (e.g. 2 822 400).</param>
    /// <param name="outputRate">PCM rate; must divide dsdRate / 8 (e.g. dsdRate / 8, / 16, / 32).</param>
    /// <param name="preset">Noise filter.</param>
    /// <param name="gain">Linear gain applied to the result.</param>
    public static DsdToPcmDesign Create(int dsdRate, int outputRate, DsdFilterPreset preset, double gain)
    {
        int stageARate = dsdRate / 8;
        if (dsdRate % 8 != 0 || outputRate <= 0 || stageARate % outputRate != 0)
        {
            throw new ArgumentException($"Cannot convert {dsdRate} Hz DSD to {outputRate} Hz PCM by integer decimation.");
        }

        double scale = Math.Max(1.0, AudioRates.DsdMultiplier(dsdRate) / 64.0);
        double passband = preset.PassbandHz * scale;
        double stopband = Math.Min(preset.StopbandHz * scale, outputRate - passband);
        if (stopband <= passband)
        {
            stopband = Math.Min(outputRate / 2.0, passband * 1.2);
            passband = Math.Min(passband, stopband * 0.8);
        }

        // Stage A: protect everything below the stage B stopband from aliasing when decimating by 8.
        double stageAStop = stageARate - stopband;
        var stageASpec = new LowpassSpec(passband / dsdRate, Math.Max(stageAStop, passband * 1.5) / dsdRate, 130.0);
        int stageALength = FirDesign.EstimateLength(stageASpec);
        int tableBytes = Math.Clamp((stageALength + 7) / 8, 2, 64);
        double[] h = FirDesign.Lowpass(stageASpec, 1.0, tableBytes * 8 - 1);
        double[] taps = new double[tableBytes * 8];
        Array.Copy(h, taps, h.Length);

        var tables = new double[tableBytes][];
        for (int k = 0; k < tableBytes; k++)
        {
            var table = new double[256];
            for (int v = 0; v < 256; v++)
            {
                double sum = 0.0;
                for (int r = 0; r < 8; r++)
                {
                    sum += ((v >> r) & 1) != 0 ? taps[8 * k + r] : -taps[8 * k + r];
                }

                table[v] = sum;
            }

            tables[k] = table;
        }

        // Stage B: the user-visible noise filter at stage A's rate, decimating to the output rate.
        var stageBSpec = new LowpassSpec(passband / stageARate, stopband / stageARate, preset.AttenuationDb);
        double[] prototype = FirDesign.DesignLowpass(stageBSpec, 1.0, preset.Phase);
        var stage = new PolyphaseStage(stageARate, outputRate, prototype,
            $"DSD low-pass {AudioRates.FormatShort(stageARate)}→{AudioRates.FormatShort(outputRate)}");
        var stageB = new ResamplerChain(stageARate, outputRate, [stage], stage.Description);

        string description = $"DSD{AudioRates.DsdMultiplier(dsdRate)} → PCM {AudioRates.Format(outputRate)} ({preset.Name})";
        return new DsdToPcmDesign(dsdRate, outputRate, tables, tableBytes, stageB, gain, description);
    }

    public DsdToPcmState CreateState() => new(this);
}

/// <summary>Per-channel streaming state of a <see cref="DsdToPcmDesign"/>.</summary>
public sealed class DsdToPcmState
{
    private readonly DsdToPcmDesign _design;
    private readonly ResamplerChainState _stageB;

    /// <summary>The last <c>TableBytes</c> input bytes, twice over, so any window of them is contiguous.</summary>
    private readonly byte[] _history;
    private double[] _intermediate = [];
    private int _position;

    internal DsdToPcmState(DsdToPcmDesign design)
    {
        _design = design;
        _stageB = design.StageB.CreateState();
        _history = new byte[2 * design.TableBytes];
        Reset();
    }

    /// <summary>Upper bound of PCM samples produced from <paramref name="bytes"/> DSD bytes.</summary>
    public int MaxOutput(int bytes) => _design.StageB.MaxOutput(bytes);

    /// <summary>Converts MSB-first DSD bytes; returns PCM samples written.</summary>
    public int Process(ReadOnlySpan<byte> dsd, Span<double> output)
    {
        if (_intermediate.Length < dsd.Length)
        {
            _intermediate = new double[dsd.Length];
        }

        double[][] tables = _design.Tables;
        int k = _design.TableBytes;
        byte[] history = _history;
        double gain = _design.Gain;
        int position = _position;

        // The newest byte is written at both `position` and `position + k`, so history[position .. position + k)
        // is always the last k bytes newest-first without any of them being moved.
        for (int i = 0; i < dsd.Length; i++)
        {
            position = position == 0 ? k - 1 : position - 1;
            byte value = dsd[i];
            history[position] = value;
            history[position + k] = value;

            double sum = 0.0;
            for (int j = 0; j < k; j++)
            {
                sum += tables[j][history[position + j]];
            }

            _intermediate[i] = sum * gain;
        }

        _position = position;
        return _stageB.Process(_intermediate.AsSpan(0, dsd.Length), output);
    }

    public void Reset()
    {
        Array.Fill(_history, DsdConstants.SilenceByte);
        _position = 0;
        _stageB.Reset();
    }
}
