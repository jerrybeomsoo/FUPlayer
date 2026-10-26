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
    /// <summary>Negotiated server input FIFO snapshot.</summary>
    Telemetry,
    /// <summary>Negotiated DSD output, with post-limiter PCM for the player's meters.</summary>
    DsdResult,
    /// <summary>Output gain, only after DSD output was negotiated.</summary>
    OutputGain,
    /// <summary>Negotiated DSD reset also carries the current gain for an immediate seek boundary.</summary>
    DsdReset,
    /// <summary>Negotiated request to return the remaining input reserve at an end or flush boundary.</summary>
    Drain,
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
    double RoundTripMilliseconds,
    bool Telemetry = false,
    int ServerFifoMilliseconds = 500,
    int ServerThreads = 0,
    bool ServerGpu = true,
    bool DsdOutput = false,
    SpeakerSettings? Speakers = null,
    bool BufferedInput = false,
    int DsdMeterDivisor = 1);

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
    double FilterOperationsPerInputSecond,
    bool Telemetry = false,
    int ServerFifoMilliseconds = 0,
    int ServerThreads = 0,
    bool DsdOutput = false,
    bool BufferedInput = false,
    int DsdMeterDivisor = 1);

internal sealed record DspOutputReset(uint Epoch, double Gain);
internal sealed record DspTelemetry(double QueuedSeconds, double CapacitySeconds);
internal sealed record DsdOutputReport(long LimiterEvents, long ModulatorResets, double MinimumGain, double PeakModulation);

/// <summary>What the exciter on the server is doing, sent with each result for Now playing.</summary>
internal readonly record struct ExciterReport(double EdgeHz, double StartHz, double AddedDb, double DriveLowHz, int Rate);

/// <summary>
/// Encodes the session's messages. Audio travels as 64-bit floating point, the core's own precision, so a block
/// processed remotely is the block processed here. Finished DSD travels as packed bits, with a float32 copy
/// used only for visual meters. Metadata is JSON.
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
    internal static byte[] EncodeResult(uint epoch, long index, CoreBlock block, double computeSeconds, ExciterReport? exciter, bool meterFloat = false)
    {
        byte[] bytes = new byte[ResultHeader + Lanes(block, meterFloat)];
        Span<byte> span = bytes;
        BinaryPrimitives.WriteUInt32LittleEndian(span, epoch);
        BinaryPrimitives.WriteInt64LittleEndian(span[4..], index);
        span[12] = (byte)((block.HasDry ? 1 : 0) | (meterFloat ? 128 : 0));
        span[13] = (byte)block.Channels;
        BinaryPrimitives.WriteDoubleLittleEndian(span[16..], computeSeconds);
        ExciterReport report = exciter ?? new ExciterReport(double.NaN, double.NaN, double.NaN, double.NaN, 0);
        BinaryPrimitives.WriteDoubleLittleEndian(span[24..], report.EdgeHz);
        BinaryPrimitives.WriteDoubleLittleEndian(span[32..], report.StartHz);
        BinaryPrimitives.WriteDoubleLittleEndian(span[40..], report.AddedDb);
        BinaryPrimitives.WriteDoubleLittleEndian(span[48..], report.DriveLowHz);
        BinaryPrimitives.WriteInt32LittleEndian(span[56..], report.Rate);
        WriteLanes(span[ResultHeader..], block, meterFloat);
        return bytes;
    }

    internal static (uint Epoch, long Index, double ComputeSeconds, ExciterReport? Exciter) DecodeResult(
        ReadOnlySpan<byte> bytes, CoreBlock block, int maximumChannels, int maximumFrames, bool allowMeterFloat = false)
    {
        if (bytes.Length < ResultHeader)
        {
            throw new InvalidDataException("A DSP result is too short.");
        }

        uint epoch = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        long index = BinaryPrimitives.ReadInt64LittleEndian(bytes[4..]);
        bool meterFloat = (bytes[12] & 128) != 0;
        if ((bytes[12] & 126) != 0 || (meterFloat && !allowMeterFloat)) throw new InvalidDataException("Unexpected DSP sample encoding.");
        block.HasDry = (bytes[12] & 1) != 0;
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
        ReadLanes(bytes[ResultHeader..], block, maximumFrames, meterFloat);
        return (epoch, index, compute, report.Rate > 0 ? report : null);
    }

    internal static double AudioSeconds(ReadOnlySpan<byte> bytes, int rate)
    {
        if (bytes.Length < AudioHeader + 4) throw new InvalidDataException("A DSP audio message is too short.");
        return Math.Max(0, BinaryPrimitives.ReadInt32LittleEndian(bytes[AudioHeader..])) / (double)rate;
    }

    internal static byte[] EncodeDsdResult(byte[] pcm, byte[][] dsd, int count, DsdOutputReport report)
    {
        // PCM is already post-volume/limiter/trim. It feeds meters only; the player must never process it twice.
        byte[] bytes = new byte[4 + pcm.Length + 36 + dsd.Length * (4 + count)];
        Span<byte> target = bytes;
        BinaryPrimitives.WriteInt32LittleEndian(target, pcm.Length);
        pcm.CopyTo(target[4..]); int at = 4 + pcm.Length;
        BinaryPrimitives.WriteInt64LittleEndian(target[at..], report.LimiterEvents);
        BinaryPrimitives.WriteInt64LittleEndian(target[(at + 8)..], report.ModulatorResets);
        BinaryPrimitives.WriteDoubleLittleEndian(target[(at + 16)..], report.MinimumGain);
        BinaryPrimitives.WriteDoubleLittleEndian(target[(at + 24)..], report.PeakModulation);
        BinaryPrimitives.WriteInt32LittleEndian(target[(at + 32)..], dsd.Length); at += 36;
        foreach (byte[] channel in dsd)
        {
            BinaryPrimitives.WriteInt32LittleEndian(target[at..], count); at += 4;
            channel.AsSpan(0, count).CopyTo(target[at..]); at += count;
        }
        return bytes;
    }

    internal static (byte[] Pcm, byte[][] Dsd, DsdOutputReport Report) DecodeDsdResult(ReadOnlySpan<byte> bytes, int channels, int maximumBytes)
    {
        if (bytes.Length < 40) throw new InvalidDataException("A DSD result is too short.");
        int size = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        if (size < ResultHeader || size > bytes.Length - 40) throw new InvalidDataException("A DSD result is malformed.");
        byte[] pcm = bytes.Slice(4, size).ToArray(); int at = 4 + size;
        var report = new DsdOutputReport(BinaryPrimitives.ReadInt64LittleEndian(bytes[at..]),
            BinaryPrimitives.ReadInt64LittleEndian(bytes[(at + 8)..]), BinaryPrimitives.ReadDoubleLittleEndian(bytes[(at + 16)..]),
            BinaryPrimitives.ReadDoubleLittleEndian(bytes[(at + 24)..]));
        if (report.LimiterEvents < 0 || report.ModulatorResets < 0 || !double.IsFinite(report.MinimumGain)
            || report.MinimumGain is < 0 or > 1 || !double.IsFinite(report.PeakModulation) || report.PeakModulation < 0
            || BinaryPrimitives.ReadInt32LittleEndian(bytes[(at + 32)..]) != channels)
            throw new InvalidDataException("A DSD report is malformed.");
        at += 36; var dsd = new byte[channels][];
        for (int c = 0; c < channels; c++)
        {
            if (bytes.Length - at < 4) throw new InvalidDataException("A DSD channel is missing.");
            int count = BinaryPrimitives.ReadInt32LittleEndian(bytes[at..]); at += 4;
            if (count < 0 || count > maximumBytes || count > bytes.Length - at || (c > 0 && count != dsd[0].Length))
                throw new InvalidDataException("A DSD channel is malformed.");
            dsd[c] = bytes.Slice(at, count).ToArray(); at += count;
        }
        if (at != bytes.Length) throw new InvalidDataException("A DSD result has data left over.");
        return (pcm, dsd, report);
    }

    internal static byte[] EncodeEpoch(uint epoch)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, epoch);
        return bytes;
    }

    internal static uint DecodeEpoch(ReadOnlySpan<byte> bytes) =>
        bytes.Length == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : throw new InvalidDataException("A DSP reset is malformed.");

    private static int Lanes(CoreBlock block, bool meterFloat = false)
    {
        int length = 0;
        for (int c = 0; c < block.Channels; c++)
        {
            length = checked(length + 4 + (block.Counts[c] * (meterFloat ? 4 : 8) * (block.HasDry ? 2 : 1)));
        }

        return length;
    }

    private static void WriteLanes(Span<byte> span, CoreBlock block, bool meterFloat = false)
    {
        int at = 0;
        for (int c = 0; c < block.Channels; c++)
        {
            int count = block.Counts[c];
            BinaryPrimitives.WriteInt32LittleEndian(span[at..], count);
            at += 4;
            if (meterFloat)
            {
                Span<float> values = MemoryMarshal.Cast<byte, float>(span.Slice(at, count * 4));
                for (int i = 0; i < count; i++) values[i] = (float)block.Signal[c][i];
            }
            else MemoryMarshal.AsBytes(block.Signal[c].AsSpan(0, count)).CopyTo(span[at..]);
            at += count * (meterFloat ? 4 : 8);
            if (block.HasDry)
            {
                if (meterFloat) throw new InvalidDataException("DSD output meters cannot have a dry lane.");
                MemoryMarshal.AsBytes(block.Dry[c].AsSpan(0, count)).CopyTo(span[at..]);
                at += count * 8;
            }
        }
    }

    private static void ReadLanes(ReadOnlySpan<byte> span, CoreBlock block, int maximumFrames, bool meterFloat = false)
    {
        int at = 0;
        int sampleBytes = meterFloat ? 4 : 8;
        for (int c = 0; c < block.Channels; c++)
        {
            if (span.Length - at < 4)
            {
                throw new InvalidDataException("A DSP block is cut short.");
            }

            int count = BinaryPrimitives.ReadInt32LittleEndian(span[at..]);
            at += 4;
            int lanes = block.HasDry ? 2 : 1;
            if (count < 0 || count > maximumFrames || (long)count * sampleBytes * lanes > span.Length - at)
            {
                throw new InvalidDataException("A DSP block is malformed.");
            }

            // Into the block's own storage: after a run its lanes point at the stages' buffers, which must not be written over.
            block.Counts[c] = count;
            block.Signal[c] = Read(span.Slice(at, count * sampleBytes), ref block.SignalStore[c], count, meterFloat);
            at += count * sampleBytes;
            if (block.HasDry)
            {
                block.Dry[c] = Read(span.Slice(at, count * sampleBytes), ref block.DryStore[c], count, meterFloat);
                at += count * sampleBytes;
            }
        }

        if (at != span.Length)
        {
            throw new InvalidDataException("A DSP block has data left over.");
        }

        static double[] Read(ReadOnlySpan<byte> source, ref double[] target, int count, bool meterFloat)
        {
            if (target.Length < count)
            {
                target = new double[Math.Max(count, 64)];
            }

            if (meterFloat)
            {
                ReadOnlySpan<float> values = MemoryMarshal.Cast<byte, float>(source);
                for (int i = 0; i < count; i++) target[i] = values[i];
            }
            else source.CopyTo(MemoryMarshal.AsBytes(target.AsSpan(0, count)));
            return target;
        }
    }
}
