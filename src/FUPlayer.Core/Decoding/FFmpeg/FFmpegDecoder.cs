using System.Text;
using FUPlayer.Core.Audio;
using static FUPlayer.Core.Decoding.FFmpeg.FFmpegNative;

namespace FUPlayer.Core.Decoding.FFmpeg;

/// <summary>Decoder for every audio format FFmpeg understands; output is planar PCM.</summary>
internal sealed unsafe class FFmpegDecoder : IAudioDecoder
{
    private readonly int _streamIndex;
    private readonly int _timeBaseNum;
    private readonly int _timeBaseDen;
    private readonly long _streamStart;
    private void* _format;
    private void* _codec;
    private void* _packet;
    private void* _frame;
    private double[][] _pending;
    private int _pendingCount;
    private int _pendingOffset;
    private long _position;
    private long _discardUntil = -1;
    private bool _inputFinished;
    private bool _packetWaiting;

    private FFmpegDecoder(string path)
    {
        void* format = null;
        byte[] url = Encoding.UTF8.GetBytes(path + "\0");
        fixed (byte* pointer = url)
        {
            Check(avformat_open_input(&format, pointer, null, null), "open");
        }

        _format = format;
        try
        {
            Check(avformat_find_stream_info(_format, null), "read stream information");
            void* decoder = null;
            _streamIndex = av_find_best_stream(_format, AvMediaTypeAudio, -1, -1, &decoder, 0);
            if (_streamIndex < 0)
            {
                throw new InvalidDataException("No audio stream found.");
            }

            void* stream = StreamAt(_streamIndex);
            void* parameters = (void*)Read<nint>(stream, StreamCodecParameters);
            int codecId = Read<int>(parameters, ParametersCodecId);
            if (decoder == null)
            {
                decoder = avcodec_find_decoder(codecId);
            }

            if (decoder == null)
            {
                throw new NotSupportedException($"No FFmpeg decoder for codec '{ReadString(avcodec_get_name(codecId))}'.");
            }

            _codec = avcodec_alloc_context3(decoder);
            Check(avcodec_parameters_to_context(_codec, parameters), "configure decoder");
            _timeBaseNum = Read<int>(stream, StreamTimeBase);
            _timeBaseDen = Read<int>(stream, StreamTimeBase + 4);
            Write(_codec, CodecContextPacketTimeBase, _timeBaseNum);
            Write(_codec, CodecContextPacketTimeBase + 4, _timeBaseDen);
            Check(avcodec_open2(_codec, decoder, null), "open decoder");

            _packet = av_packet_alloc();
            _frame = av_frame_alloc();

            int rate = Read<int>(_codec, CodecContextSampleRate);
            int channels = Read<int>(_codec, CodecContextChannelCount);
            if (rate <= 0 || channels is <= 0 or > 64)
            {
                throw new InvalidDataException($"Unsupported audio parameters ({rate} Hz, {channels} channels).");
            }

            int bits = Read<int>(parameters, ParametersBitsPerRawSample);
            if (bits <= 0)
            {
                bits = Read<int>(parameters, ParametersBitsPerCodedSample);
            }

            if (bits is <= 0 or > 32)
            {
                bits = Read<int>(_codec, CodecContextSampleFormat) switch
                {
                    SampleFormatU8 or SampleFormatU8P => 8,
                    SampleFormatS16 or SampleFormatS16P => 16,
                    _ => 24,
                };
            }

            Format = StreamFormat.Pcm(rate, channels, bits);
            CodecName = ReadString(avcodec_get_name(codecId))?.ToUpperInvariant() ?? "FFMPEG";

            _streamStart = Read<long>(stream, StreamStartTime);
            long streamDuration = Read<long>(stream, StreamDuration);
            double seconds = streamDuration != AvNoPtsValue && streamDuration > 0 && _timeBaseDen > 0
                ? streamDuration * (double)_timeBaseNum / _timeBaseDen
                : Read<long>(_format, FormatContextDuration) is long d and > 0 ? d / (double)AvTimeBase : -1;
            Length = seconds > 0 ? (long)Math.Round(seconds * rate) : -1;

            _pending = new double[channels][];
            for (int c = 0; c < channels; c++)
            {
                _pending[c] = new double[4096];
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public StreamFormat Format { get; }

    public long Length { get; }

    public long Position => _position;

    public bool CanSeek => true;

    public string CodecName { get; }

    public static FFmpegDecoder Open(string path)
    {
        if (!FFmpegLibrary.IsAvailable)
        {
            throw new NotSupportedException(FFmpegLibrary.LoadError ?? "FFmpeg is not available.");
        }

        return new FFmpegDecoder(path);
    }

    /// <summary>Container and stream tags (container first).</summary>
    public IReadOnlyList<KeyValuePair<string, string>> ReadTags()
    {
        var tags = new List<KeyValuePair<string, string>>();
        tags.AddRange(ReadDictionary((void*)Read<nint>(_format, FormatContextMetadata)));
        tags.AddRange(ReadDictionary((void*)Read<nint>(StreamAt(_streamIndex), StreamMetadata)));
        return tags;
    }

    /// <summary>Embedded cover picture from an attached-picture stream, if any.</summary>
    public byte[]? ReadAttachedPicture()
    {
        int count = Read<int>(_format, FormatContextStreamCount);
        for (int i = 0; i < count; i++)
        {
            void* stream = StreamAt(i);
            if ((Read<int>(stream, StreamDisposition) & AvDispositionAttachedPic) == 0)
            {
                continue;
            }

            byte* data = (byte*)Read<nint>(stream, StreamAttachedPictureData);
            int size = Read<int>(stream, StreamAttachedPictureSize);
            if (data != null && size > 0)
            {
                return new ReadOnlySpan<byte>(data, size).ToArray();
            }
        }

        return null;
    }

    public int ReadPcm(double[][] destination, int offset, int maxFrames)
    {
        int channels = Format.Channels;
        int written = 0;
        while (written < maxFrames)
        {
            if (_pendingOffset >= _pendingCount && !DecodeFrame())
            {
                break;
            }

            int count = Math.Min(maxFrames - written, _pendingCount - _pendingOffset);
            for (int c = 0; c < channels; c++)
            {
                Array.Copy(_pending[c], _pendingOffset, destination[c], offset + written, count);
            }

            _pendingOffset += count;
            written += count;
        }

        _position += written;
        return written;
    }

    public int ReadDsd(byte[][] destination, int offset, int maxBytes) =>
        throw new NotSupportedException("FFmpeg streams are decoded to PCM.");

    public void Seek(long position)
    {
        position = Math.Max(0, Length >= 0 ? Math.Min(position, Length) : position);
        double seconds = (double)position / Format.SampleRate;
        long timestamp = _timeBaseNum > 0 ? (long)(seconds * _timeBaseDen / _timeBaseNum) : 0;
        if (_streamStart != AvNoPtsValue)
        {
            timestamp += _streamStart;
        }

        av_seek_frame(_format, _streamIndex, timestamp, AvSeekFlagBackward);
        avcodec_flush_buffers(_codec);
        av_packet_unref(_packet);
        _packetWaiting = false;
        _inputFinished = false;
        _pendingCount = 0;
        _pendingOffset = 0;
        _discardUntil = position;
        _position = position;
    }

    public void Dispose()
    {
        void* frame = _frame;
        void* packet = _packet;
        void* codec = _codec;
        void* format = _format;
        if (frame != null)
        {
            av_frame_free(&frame);
        }

        if (packet != null)
        {
            av_packet_free(&packet);
        }

        if (codec != null)
        {
            avcodec_free_context(&codec);
        }

        if (format != null)
        {
            avformat_close_input(&format);
        }

        _frame = null;
        _packet = null;
        _codec = null;
        _format = null;
    }

    private void* StreamAt(int index)
    {
        void** streams = (void**)Read<nint>(_format, FormatContextStreams);
        return streams[index];
    }

    private bool DecodeFrame()
    {
        while (true)
        {
            int result = avcodec_receive_frame(_codec, _frame);
            if (result == 0)
            {
                bool produced = ConvertFrame();
                av_frame_unref(_frame);
                if (produced)
                {
                    return true;
                }

                continue;
            }

            if (result == AvErrorEof)
            {
                return false;
            }

            if (result != AvErrorEagain)
            {
                // Corrupt data: keep feeding packets so playback continues past the damage.
                if (_inputFinished)
                {
                    return false;
                }
            }

            if (!_packetWaiting)
            {
                if (_inputFinished)
                {
                    return false;
                }

                if (av_read_frame(_format, _packet) < 0)
                {
                    avcodec_send_packet(_codec, null);
                    _inputFinished = true;
                    continue;
                }

                if (Read<int>(_packet, PacketStreamIndex) != _streamIndex)
                {
                    av_packet_unref(_packet);
                    continue;
                }

                _packetWaiting = true;
            }

            int sent = avcodec_send_packet(_codec, _packet);
            if (sent != AvErrorEagain)
            {
                av_packet_unref(_packet);
                _packetWaiting = false;
            }
        }
    }

    private bool ConvertFrame()
    {
        int samples = Read<int>(_frame, FrameSampleCount);
        int format = Read<int>(_frame, FrameFormat);
        int channels = Format.Channels;
        if (samples <= 0 || Read<int>(_frame, FrameChannelCount) != channels)
        {
            return false;
        }

        if (_pending[0].Length < samples)
        {
            for (int c = 0; c < channels; c++)
            {
                _pending[c] = new double[samples];
            }
        }

        byte** data = (byte**)Read<nint>(_frame, FrameExtendedData);
        bool planar = format is >= SampleFormatU8P and <= SampleFormatDblP or SampleFormatS64P;
        int bytes = format switch
        {
            SampleFormatU8 or SampleFormatU8P => 1,
            SampleFormatS16 or SampleFormatS16P => 2,
            SampleFormatS32 or SampleFormatS32P or SampleFormatFlt or SampleFormatFltP => 4,
            _ => 8,
        };

        for (int c = 0; c < channels; c++)
        {
            byte* plane = planar ? data[c] : data[0];
            int step = planar ? bytes : bytes * channels;
            int start = planar ? 0 : c * bytes;
            double[] target = _pending[c];
            for (int i = 0; i < samples; i++)
            {
                byte* s = plane + start + i * step;
                target[i] = format switch
                {
                    SampleFormatU8 or SampleFormatU8P => (*s - 128) / 128.0,
                    SampleFormatS16 or SampleFormatS16P => *(short*)s / 32768.0,
                    SampleFormatS32 or SampleFormatS32P => *(int*)s / 2147483648.0,
                    SampleFormatFlt or SampleFormatFltP => *(float*)s,
                    SampleFormatDbl or SampleFormatDblP => *(double*)s,
                    _ => *(long*)s / 9223372036854775808.0,
                };
            }
        }

        _pendingCount = samples;
        _pendingOffset = 0;

        if (_discardUntil >= 0)
        {
            long timestamp = Read<long>(_frame, FrameBestEffortTimestamp);
            if (timestamp != AvNoPtsValue && _timeBaseDen > 0)
            {
                if (_streamStart != AvNoPtsValue)
                {
                    timestamp -= _streamStart;
                }

                long frameStart = (long)Math.Round(timestamp * (double)_timeBaseNum / _timeBaseDen * Format.SampleRate);
                long skip = _discardUntil - frameStart;
                if (skip >= samples)
                {
                    _pendingCount = 0;
                    return false;
                }

                _pendingOffset = (int)Math.Max(0, skip);
            }

            _discardUntil = -1;
        }

        return true;
    }

    private static void Check(int result, string action)
    {
        if (result < 0)
        {
            throw new InvalidDataException($"FFmpeg could not {action}: {DescribeError(result)}");
        }
    }
}
