using System.Buffers.Binary;
using System.Runtime.InteropServices;
using FUPlayer.Core.Dsp.Dsd;
using FUPlayer.Core.Output;

namespace FUPlayer.Core.Engine;

/// <summary>
/// Device-side end of the FIFO. Hands buffered frames to the back-end, produces format-correct silence
/// (PCM zeros, DSD idle pattern, DoP silence with a continuing marker sequence) and applies short fades
/// around pauses for PCM.
/// </summary>
internal sealed class RenderSource : IAudioRenderSource
{
    private readonly SpscByteRing _ring;
    private readonly OutputFormat _format;
    private readonly AutoResetEvent _spaceAvailable;
    private readonly int _frameBytes;
    private readonly bool _quickPause;
    private readonly int _fadeFrames;
    private long _renderedFrames;
    private long _underrunFrames;
    private volatile bool _paused;
    private volatile bool _endOfStream;
    private double _fadeGain = 1.0;
    private byte _lastDopMarker = DsdConstants.DopMarkerB;

    // Device thread only: audio has arrived since the FIFO was last cleared, so a shortfall is a real underrun
    // rather than the initial fill after a start, seek or settings change.
    private bool _primed;

    public RenderSource(SpscByteRing ring, OutputFormat format, AutoResetEvent spaceAvailable, bool quickPause)
    {
        _ring = ring;
        _format = format;
        _spaceAvailable = spaceAvailable;
        _frameBytes = format.CanonicalBytesPerFrame;
        _quickPause = quickPause;
        _fadeFrames = Math.Max(1, format.CanonicalFrameRate / 100);
    }

    public bool Paused
    {
        get => _paused;
        set => _paused = value;
    }

    /// <summary>Set by the engine once the last frame of the queue has been written.</summary>
    public bool EndOfStream
    {
        get => _endOfStream;
        set => _endOfStream = value;
    }

    public long RenderedFrames => Interlocked.Read(ref _renderedFrames);

    public long UnderrunFrames => Interlocked.Read(ref _underrunFrames);

    public bool IsDrained => _endOfStream && _ring.Count == 0;

    public int Render(Span<byte> destination, int frames, bool realtime)
    {
        Span<byte> target = destination[..(frames * _frameBytes)];

        if (_paused && realtime)
        {
            if (_format.Kind == OutputSampleKind.Pcm && !_quickPause && _fadeGain > 0.0)
            {
                int faded = ReadFrames(target);
                ApplyFade(target[..(faded * _frameBytes)], faded, _fadeGain, 0.0);
                WriteSilence(target[(faded * _frameBytes)..], frames - faded);
                _fadeGain = 0.0;
                Interlocked.Add(ref _renderedFrames, faded);
            }
            else
            {
                _fadeGain = 0.0;
                WriteSilence(target, frames);
            }

            return frames;
        }

        int got = ReadFrames(target);
        if (got > 0)
        {
            _primed = true;
            if (_format.Kind == OutputSampleKind.Pcm && _fadeGain < 1.0)
            {
                int ramp = Math.Min(got, _fadeFrames);
                ApplyFade(target[..(ramp * _frameBytes)], ramp, _fadeGain, 1.0);
                _fadeGain = 1.0;
            }
            else if (_format.Kind == OutputSampleKind.Dop)
            {
                _lastDopMarker = target[(got - 1) * _frameBytes + 3];
            }

            Interlocked.Add(ref _renderedFrames, got);
            _spaceAvailable.Set();
        }

        if (!realtime)
        {
            return got;
        }

        if (got < frames)
        {
            if (_primed && !_endOfStream)
            {
                Interlocked.Add(ref _underrunFrames, frames - got);
            }

            WriteSilence(target[(got * _frameBytes)..], frames - got);
            if (_format.Kind == OutputSampleKind.Pcm && got == 0)
            {
                _fadeGain = 0.0;
            }
        }

        return frames;
    }

    private int ReadFrames(Span<byte> target)
    {
        if (_ring.IsClearPending)
        {
            _ring.Read(Span<byte>.Empty);
            _primed = false;
        }

        long available = _ring.Count;
        int bytes = (int)Math.Min(target.Length, available - available % _frameBytes);
        return bytes > 0 ? _ring.Read(target[..bytes]) / _frameBytes : 0;
    }

    private void ApplyFade(Span<byte> data, int frames, double from, double to)
    {
        Span<int> samples = MemoryMarshal.Cast<byte, int>(data);
        int channels = _format.Channels;
        for (int f = 0; f < frames; f++)
        {
            double gain = from + (to - from) * (f + 1) / frames;
            for (int c = 0; c < channels; c++)
            {
                int index = f * channels + c;
                samples[index] = (int)(samples[index] * gain);
            }
        }
    }

    private void WriteSilence(Span<byte> data, int frames)
    {
        switch (_format.Kind)
        {
            case OutputSampleKind.Pcm:
                data.Clear();
                break;
            case OutputSampleKind.NativeDsd:
                data.Fill(DsdConstants.SilenceByte);
                break;
            default:
                int channels = _format.Channels;
                for (int f = 0; f < frames; f++)
                {
                    _lastDopMarker = _lastDopMarker == DsdConstants.DopMarkerA ? DsdConstants.DopMarkerB : DsdConstants.DopMarkerA;
                    uint value = ((uint)_lastDopMarker << 24) | ((uint)DsdConstants.SilenceByte << 16) | ((uint)DsdConstants.SilenceByte << 8);
                    for (int c = 0; c < channels; c++)
                    {
                        BinaryPrimitives.WriteUInt32LittleEndian(data.Slice((f * channels + c) * 4, 4), value);
                    }
                }

                break;
        }
    }
}
