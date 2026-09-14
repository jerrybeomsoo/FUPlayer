using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FUPlayer.Core.Decoding.FFmpeg;

/// <summary>
/// Minimal FFmpeg 9.0 bindings. Struct field offsets were measured with <c>offsetof</c> against the
/// FFmpeg 9.0.1 public headers (64-bit, identical on Windows, Linux and macOS for these structs).
/// </summary>
internal static unsafe partial class FFmpegNative
{
    public const string AvformatLibrary = "avformat";
    public const string AvcodecLibrary = "avcodec";
    public const string AvutilLibrary = "avutil";

    public const int AvLogQuiet = -8;
    public const int AvMediaTypeAudio = 1;
    public const int AvErrorEof = -541478725;
    public const int AvErrorEagain = -11;
    public const long AvNoPtsValue = long.MinValue;
    public const long AvTimeBase = 1_000_000;
    public const int AvSeekFlagBackward = 1;
    public const int AvDispositionAttachedPic = 1024;

    public const int SampleFormatU8 = 0;
    public const int SampleFormatS16 = 1;
    public const int SampleFormatS32 = 2;
    public const int SampleFormatFlt = 3;
    public const int SampleFormatDbl = 4;
    public const int SampleFormatU8P = 5;
    public const int SampleFormatS16P = 6;
    public const int SampleFormatS32P = 7;
    public const int SampleFormatFltP = 8;
    public const int SampleFormatDblP = 9;
    public const int SampleFormatS64 = 10;
    public const int SampleFormatS64P = 11;

    // AVFormatContext
    public const int FormatContextInputFormat = 8;
    public const int FormatContextStreamCount = 44;
    public const int FormatContextStreams = 48;
    public const int FormatContextDuration = 104;
    public const int FormatContextBitRate = 112;
    public const int FormatContextMetadata = 192;

    // AVInputFormat
    public const int InputFormatName = 0;
    public const int InputFormatLongName = 8;

    // AVStream
    public const int StreamCodecParameters = 16;
    public const int StreamTimeBase = 32;
    public const int StreamStartTime = 40;
    public const int StreamDuration = 48;
    public const int StreamDisposition = 64;
    public const int StreamMetadata = 80;
    public const int StreamAttachedPictureData = 120;
    public const int StreamAttachedPictureSize = 128;

    // AVCodecParameters
    public const int ParametersCodecType = 0;
    public const int ParametersCodecId = 4;
    public const int ParametersFormat = 44;
    public const int ParametersBitRate = 48;
    public const int ParametersBitsPerCodedSample = 56;
    public const int ParametersBitsPerRawSample = 60;
    public const int ParametersChannelCount = 132;
    public const int ParametersSampleRate = 152;

    // AVCodecContext
    public const int CodecContextPacketTimeBase = 92;
    public const int CodecContextSampleRate = 344;
    public const int CodecContextSampleFormat = 348;
    public const int CodecContextChannelCount = 356;

    // AVFrame
    public const int FrameExtendedData = 96;
    public const int FrameSampleCount = 112;
    public const int FrameFormat = 116;
    public const int FramePts = 136;
    public const int FrameBestEffortTimestamp = 304;
    public const int FrameChannelCount = 388;

    // AVPacket
    public const int PacketStreamIndex = 36;

    [LibraryImport(AvutilLibrary)]
    public static partial uint avutil_version();

    [LibraryImport(AvcodecLibrary)]
    public static partial uint avcodec_version();

    [LibraryImport(AvformatLibrary)]
    public static partial uint avformat_version();

    [LibraryImport(AvutilLibrary)]
    public static partial void av_log_set_level(int level);

    [LibraryImport(AvutilLibrary)]
    public static partial int av_strerror(int error, byte* buffer, nuint size);

    [LibraryImport(AvutilLibrary)]
    public static partial void* av_dict_iterate(void* dictionary, void* previous);

    [LibraryImport(AvutilLibrary)]
    public static partial void* av_frame_alloc();

    [LibraryImport(AvutilLibrary)]
    public static partial void av_frame_free(void** frame);

    [LibraryImport(AvutilLibrary)]
    public static partial void av_frame_unref(void* frame);

    [LibraryImport(AvformatLibrary)]
    public static partial int avformat_open_input(void** context, byte* url, void* format, void** options);

    [LibraryImport(AvformatLibrary)]
    public static partial int avformat_find_stream_info(void* context, void** options);

    [LibraryImport(AvformatLibrary)]
    public static partial int av_find_best_stream(void* context, int type, int wantedStream, int relatedStream, void** decoder, int flags);

    [LibraryImport(AvformatLibrary)]
    public static partial int av_read_frame(void* context, void* packet);

    [LibraryImport(AvformatLibrary)]
    public static partial int av_seek_frame(void* context, int streamIndex, long timestamp, int flags);

    [LibraryImport(AvformatLibrary)]
    public static partial void avformat_close_input(void** context);

    [LibraryImport(AvcodecLibrary)]
    public static partial void* avcodec_find_decoder(int codecId);

    [LibraryImport(AvcodecLibrary)]
    public static partial void* avcodec_alloc_context3(void* codec);

    [LibraryImport(AvcodecLibrary)]
    public static partial int avcodec_parameters_to_context(void* context, void* parameters);

    [LibraryImport(AvcodecLibrary)]
    public static partial int avcodec_open2(void* context, void* codec, void** options);

    [LibraryImport(AvcodecLibrary)]
    public static partial int avcodec_send_packet(void* context, void* packet);

    [LibraryImport(AvcodecLibrary)]
    public static partial int avcodec_receive_frame(void* context, void* frame);

    [LibraryImport(AvcodecLibrary)]
    public static partial void avcodec_flush_buffers(void* context);

    [LibraryImport(AvcodecLibrary)]
    public static partial void avcodec_free_context(void** context);

    [LibraryImport(AvcodecLibrary)]
    public static partial byte* avcodec_get_name(int codecId);

    [LibraryImport(AvcodecLibrary)]
    public static partial void* av_packet_alloc();

    [LibraryImport(AvcodecLibrary)]
    public static partial void av_packet_free(void** packet);

    [LibraryImport(AvcodecLibrary)]
    public static partial void av_packet_unref(void* packet);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T Read<T>(void* structure, int offset)
        where T : unmanaged => Unsafe.ReadUnaligned<T>((byte*)structure + offset);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write<T>(void* structure, int offset, T value)
        where T : unmanaged => Unsafe.WriteUnaligned((byte*)structure + offset, value);

    public static string? ReadString(byte* text) => text == null ? null : Marshal.PtrToStringUTF8((IntPtr)text);

    public static string DescribeError(int error)
    {
        byte* buffer = stackalloc byte[256];
        return av_strerror(error, buffer, 256) == 0 ? ReadString(buffer) ?? $"error {error}" : $"FFmpeg error {error}";
    }

    /// <summary>Enumerates key/value pairs of an AVDictionary.</summary>
    public static IEnumerable<KeyValuePair<string, string>> ReadDictionary(void* dictionary)
    {
        var result = new List<KeyValuePair<string, string>>();
        if (dictionary == null)
        {
            return result;
        }

        void* entry = null;
        while ((entry = av_dict_iterate(dictionary, entry)) != null)
        {
            string? key = ReadString(Read<nint>(entry, 0) == 0 ? null : (byte*)Read<nint>(entry, 0));
            string? value = ReadString(Read<nint>(entry, 8) == 0 ? null : (byte*)Read<nint>(entry, 8));
            if (key is not null)
            {
                result.Add(new KeyValuePair<string, string>(key, value ?? string.Empty));
            }
        }

        return result;
    }
}
