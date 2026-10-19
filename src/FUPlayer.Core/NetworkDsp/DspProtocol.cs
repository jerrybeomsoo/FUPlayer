using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text.Json;
using FUPlayer.Core.Engine;
using FUPlayer.Core.Settings;

namespace FUPlayer.Core.NetworkDsp;

/// <summary>The messages of a DSP session, in the order a session uses them.</summary>
internal enum DspMessage : byte
{
    /// <summary>Player to server: the plan, the core's layout and the run of stages the server is to build.</summary>
    Hello = 1,

    /// <summary>Server to player: the stages are built; what they are, how long they take to answer, where they run.</summary>
    Ready,

    /// <summary>Server to player: the session cannot go on, and why.</summary>
    Error,

    /// <summary>Player to server: one block, as it stands in front of the first remote stage.</summary>
    Audio,

    /// <summary>Server to player: that block after the last remote stage.</summary>
    Result,

    /// <summary>Player to server: forget the blocks before this one and start the stages from silence (a seek).</summary>
    Reset,

    /// <summary>Player to server: the session is over.</summary>
    Bye,
}

/// <summary>What the player asks a server to build.</summary>
internal sealed record DspHello(
    int Version,
    PlaybackPlan Plan,
    RestorationSettings Restoration,
    CoreLayout Layout,
    CoreStage First,
    CoreStage Last,
    int MaximumFrames,
    double RoundTripMilliseconds);

/// <summary>What the server built. Latencies are in samples at each stage's own rate.</summary>
internal sealed record DspReady(
    string? Restorer,
    int RestorerLatency,
    string? Upscaler,
    int UpscalerLatency,
    string? Resampler,
    string? Acceleration,
    double FilterDelayOutputSamples,
    int FilterFlushInputSamples,
    double FilterBlockSeconds,
    double FilterOperationsPerInputSecond);

/// <summary>What the exciter on the server is doing, sent with each result for Now playing.</summary>
internal readonly record struct ExciterReport(double EdgeHz, double StartHz, double AddedDb, double DriveLowHz, int Rate);

/// <summary>
/// Encodes the session's messages. Audio travels as 64-bit floating point, the core's own precision, so a block
/// processed remotely is the block processed here. Metadata is JSON.
/// </summary>
internal static class DspProtocol
{
    internal const int Version = 4;

    private const int AudioHeader = 4 + 8 + 1 + 1 + 2 + (4 * 8);
    private const int ResultHeader = 4 + 8 + 1 + 1 + 2 + (5 * 8) + 4;

    static DspProtocol()
    {
        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException("Network DSP needs a little-endian processor.");
        }
    }

    internal static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    internal static byte[] Encode<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Json);

    internal static T Decode<T>(ReadOnlySpan<byte> bytes) =>
        JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException("Empty DSP metadata.");

    /// <summary>A block in front of the first remote stage.</summary>
    internal static byte[] EncodeAudio(uint epoch, long index, CoreBlock block)
    {
        byte[] bytes = new byte[AudioHeader + Lanes(block)];
        Span<byte> span = bytes;
        BinaryPrimitives.WriteUInt32LittleEndian(span, epoch);
        BinaryPrimitives.WriteInt64LittleEndian(span[4..], index);
        span[12] = (byte)(block.HasDry ? 1 : 0);
        span[13] = (byte)block.Channels;
        BinaryPrimitives.WriteDoubleLittleEndian(span[16..], block.Gain);
        BinaryPrimitives.WriteDoubleLittleEndian(span[24..], block.EdgeHz);
        BinaryPrimitives.WriteDoubleLittleEndian(span[32..], block.TopHz);
        BinaryPrimitives.WriteDoubleLittleEndian(span[40..], block.WallHz);
        WriteLanes(span[AudioHeader..], block);
        return bytes;
    }

    /// <summary>Reads an audio message into <paramref name="block"/>, whose arrays it grows as needed.</summary>
    internal static (uint Epoch, long Index) DecodeAudio(ReadOnlySpan<byte> bytes, CoreBlock block, int maximumChannels, int maximumFrames)
    {
        if (bytes.Length < AudioHeader)
        {
            throw new InvalidDataException("A DSP audio message is too short.");
        }

        uint epoch = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        long index = BinaryPrimitives.ReadInt64LittleEndian(bytes[4..]);
        block.HasDry = bytes[12] != 0;
        block.Channels = bytes[13];
        block.Gain = BinaryPrimitives.ReadDoubleLittleEndian(bytes[16..]);
        block.EdgeHz = BinaryPrimitives.ReadDoubleLittleEndian(bytes[24..]);
        block.TopHz = BinaryPrimitives.ReadDoubleLittleEndian(bytes[32..]);
        block.WallHz = BinaryPrimitives.ReadDoubleLittleEndian(bytes[40..]);
        if (block.Channels < 1 || block.Channels > maximumChannels || !double.IsFinite(block.Gain) || block.Gain is < 0 or > 1e6)
        {
            throw new InvalidDataException("A DSP audio message is malformed.");
        }

        ReadLanes(bytes[AudioHeader..], block, maximumFrames);
        return (epoch, index);
    }

    /// <summary>A block after the last remote stage, with what the server spent on it and what its exciter is doing.</summary>
    internal static byte[] EncodeResult(uint epoch, long index, CoreBlock block, double computeSeconds, ExciterReport? exciter)
    {
        byte[] bytes = new byte[ResultHeader + Lanes(block)];
        Span<byte> span = bytes;
        BinaryPrimitives.WriteUInt32LittleEndian(span, epoch);
        BinaryPrimitives.WriteInt64LittleEndian(span[4..], index);
        span[12] = (byte)(block.HasDry ? 1 : 0);
        span[13] = (byte)block.Channels;
        BinaryPrimitives.WriteDoubleLittleEndian(span[16..], computeSeconds);
        ExciterReport report = exciter ?? new ExciterReport(double.NaN, double.NaN, double.NaN, double.NaN, 0);
        BinaryPrimitives.WriteDoubleLittleEndian(span[24..], report.EdgeHz);
        BinaryPrimitives.WriteDoubleLittleEndian(span[32..], report.StartHz);
        BinaryPrimitives.WriteDoubleLittleEndian(span[40..], report.AddedDb);
        BinaryPrimitives.WriteDoubleLittleEndian(span[48..], report.DriveLowHz);
        BinaryPrimitives.WriteInt32LittleEndian(span[56..], report.Rate);
        WriteLanes(span[ResultHeader..], block);
        return bytes;
    }

    internal static (uint Epoch, long Index, double ComputeSeconds, ExciterReport? Exciter) DecodeResult(
        ReadOnlySpan<byte> bytes, CoreBlock block, int maximumChannels, int maximumFrames)
    {
        if (bytes.Length < ResultHeader)
        {
            throw new InvalidDataException("A DSP result is too short.");
        }

        uint epoch = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        long index = BinaryPrimitives.ReadInt64LittleEndian(bytes[4..]);
        block.HasDry = bytes[12] != 0;
        block.Channels = bytes[13];
        if (block.Channels < 1 || block.Channels > maximumChannels)
        {
            throw new InvalidDataException("A DSP result is malformed.");
        }

        double compute = BinaryPrimitives.ReadDoubleLittleEndian(bytes[16..]);
        var report = new ExciterReport(
            BinaryPrimitives.ReadDoubleLittleEndian(bytes[24..]),
            BinaryPrimitives.ReadDoubleLittleEndian(bytes[32..]),
            BinaryPrimitives.ReadDoubleLittleEndian(bytes[40..]),
            BinaryPrimitives.ReadDoubleLittleEndian(bytes[48..]),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[56..]));
        ReadLanes(bytes[ResultHeader..], block, maximumFrames);
        return (epoch, index, compute, report.Rate > 0 ? report : null);
    }

    internal static byte[] EncodeEpoch(uint epoch)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, epoch);
        return bytes;
    }

    internal static uint DecodeEpoch(ReadOnlySpan<byte> bytes) =>
        bytes.Length == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : throw new InvalidDataException("A DSP reset is malformed.");

    private static int Lanes(CoreBlock block)
    {
        int length = 0;
        for (int c = 0; c < block.Channels; c++)
        {
            length = checked(length + 4 + (block.Counts[c] * 8 * (block.HasDry ? 2 : 1)));
        }

        return length;
    }

    private static void WriteLanes(Span<byte> span, CoreBlock block)
    {
        int at = 0;
        for (int c = 0; c < block.Channels; c++)
        {
            int count = block.Counts[c];
            BinaryPrimitives.WriteInt32LittleEndian(span[at..], count);
            at += 4;
            MemoryMarshal.AsBytes(block.Signal[c].AsSpan(0, count)).CopyTo(span[at..]);
            at += count * 8;
            if (block.HasDry)
            {
                MemoryMarshal.AsBytes(block.Dry[c].AsSpan(0, count)).CopyTo(span[at..]);
                at += count * 8;
            }
        }
    }

    private static void ReadLanes(ReadOnlySpan<byte> span, CoreBlock block, int maximumFrames)
    {
        int at = 0;
        for (int c = 0; c < block.Channels; c++)
        {
            if (span.Length - at < 4)
            {
                throw new InvalidDataException("A DSP block is cut short.");
            }

            int count = BinaryPrimitives.ReadInt32LittleEndian(span[at..]);
            at += 4;
            int lanes = block.HasDry ? 2 : 1;
            if (count < 0 || count > maximumFrames || (long)count * 8 * lanes > span.Length - at)
            {
                throw new InvalidDataException("A DSP block is malformed.");
            }

            // Into the block's own storage: after a run its lanes point at the stages' buffers, which must not be written over.
            block.Counts[c] = count;
            block.Signal[c] = Read(span.Slice(at, count * 8), ref block.SignalStore[c], count);
            at += count * 8;
            if (block.HasDry)
            {
                block.Dry[c] = Read(span.Slice(at, count * 8), ref block.DryStore[c], count);
                at += count * 8;
            }
        }

        if (at != span.Length)
        {
            throw new InvalidDataException("A DSP block has data left over.");
        }

        static double[] Read(ReadOnlySpan<byte> source, ref double[] target, int count)
        {
            if (target.Length < count)
            {
                target = new double[Math.Max(count, 64)];
            }

            source.CopyTo(MemoryMarshal.AsBytes(target.AsSpan(0, count)));
            return target;
        }
    }
}
