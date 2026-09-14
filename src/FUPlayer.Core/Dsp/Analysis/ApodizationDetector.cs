using FUPlayer.Core.Dsp.Design;
using FUPlayer.Core.Dsp.Numerics;
using FUPlayer.Core.Dsp.Resampling;

namespace FUPlayer.Core.Dsp.Analysis;

/// <summary>
/// Heuristic detector for source defects that an apodizing filter would clean up, run per channel on PCM sources.
/// It counts events of two kinds:
/// <list type="bullet">
///   <item>energy bunched up just below Nyquist, well above the neighbouring band (ringing or aliasing left by a
///   non-apodizing ADC or resampler);</item>
///   <item>runs of samples stuck at digital full scale (clipping from mastering).</item>
/// </list>
/// Events are debounced. The result is guidance, not proof.
/// </summary>
public sealed class ApodizationDetector
{
    public const int MaximumSampleRate = 192_000;

    private const double WindowSeconds = 0.02;
    private const double DebounceSeconds = 0.25;
    private const double TopBandMinimumDb = -75.0;
    private const double TopBandExcessDb = 6.0;
    private const double ClipLevel = 0.99995;
    private const int ClipRun = 3;

    private readonly IRateStageState _topBand;
    private readonly IRateStageState _referenceBand;
    private readonly int _windowSamples;
    private readonly long _debounceSamples;
    private double[] _topBuffer = [];
    private double[] _referenceBuffer = [];
    private double _topEnergy;
    private double _referenceEnergy;
    private int _windowFill;
    private int _clipRun;
    private long _time;
    private long _lastEventTime = long.MinValue / 2;

    public ApodizationDetector(int sampleRate)
    {
        const double attenuation = 70.0;
        double[] top = FirDesign.Highpass(new LowpassSpec(0.440, 0.470, attenuation));
        double[] upper = FirDesign.Lowpass(new LowpassSpec(0.415, 0.445, attenuation), 1.0, top.Length);
        double[] lower = FirDesign.Lowpass(new LowpassSpec(0.365, 0.395, attenuation), 1.0, top.Length);
        var reference = new double[top.Length];
        for (int i = 0; i < reference.Length; i++)
        {
            reference[i] = upper[i] - lower[i];
        }

        _topBand = new PolyphaseStage(sampleRate, sampleRate, top, "apodization top band").CreateState();
        _referenceBand = new PolyphaseStage(sampleRate, sampleRate, reference, "apodization reference band").CreateState();
        _windowSamples = Math.Max(64, (int)(sampleRate * WindowSeconds));
        _debounceSamples = (long)(sampleRate * DebounceSeconds);
    }

    public long Events { get; private set; }

    public void Process(ReadOnlySpan<double> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        if (_topBuffer.Length < data.Length + 2)
        {
            _topBuffer = new double[data.Length + 2];
            _referenceBuffer = new double[data.Length + 2];
        }

        int topCount = _topBand.Process(data, _topBuffer);
        int referenceCount = _referenceBand.Process(data, _referenceBuffer);
        int count = Math.Min(Math.Min(topCount, referenceCount), data.Length);

        for (int i = 0; i < data.Length; i++)
        {
            if (Math.Abs(data[i]) >= ClipLevel)
            {
                if (++_clipRun == ClipRun)
                {
                    RegisterEvent(_time + i);
                }
            }
            else
            {
                _clipRun = 0;
            }
        }

        int offset = 0;
        while (offset < count)
        {
            int take = Math.Min(_windowSamples - _windowFill, count - offset);
            _topEnergy += SimdMath.SumOfSquares(_topBuffer.AsSpan(offset, take));
            _referenceEnergy += SimdMath.SumOfSquares(_referenceBuffer.AsSpan(offset, take));
            _windowFill += take;
            offset += take;

            if (_windowFill == _windowSamples)
            {
                double topDb = 10.0 * Math.Log10(_topEnergy / _windowSamples + 1e-30);
                double referenceDb = 10.0 * Math.Log10(_referenceEnergy / _windowSamples + 1e-30);
                if (topDb > TopBandMinimumDb && topDb > referenceDb + TopBandExcessDb)
                {
                    RegisterEvent(_time + offset);
                }

                _topEnergy = 0.0;
                _referenceEnergy = 0.0;
                _windowFill = 0;
            }
        }

        _time += data.Length;
    }

    public void Reset()
    {
        _topBand.Reset();
        _referenceBand.Reset();
        _topEnergy = 0.0;
        _referenceEnergy = 0.0;
        _windowFill = 0;
        _clipRun = 0;
    }

    private void RegisterEvent(long time)
    {
        if (time - _lastEventTime >= _debounceSamples)
        {
            Events++;
            _lastEventTime = time;
        }
    }
}
