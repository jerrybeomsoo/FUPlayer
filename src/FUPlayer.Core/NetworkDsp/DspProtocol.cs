using System.Runtime.InteropServices;
using System.Text.Json;
using FUPlayer.Core.Engine;
using FUPlayer.Core.Settings;
namespace FUPlayer.Core.NetworkDsp;
internal enum DspMessage : byte { Hello = 1, Resample, Restore, Upscale, Reset, Close, Redirect, UpscaleChannels, ResampleChannels, Error = 255 }
internal sealed record DspHello(int Version, PlaybackPlan Plan, RestorationSettings Restoration, bool Resampling, bool Restorer, bool Upscaler, int MaximumInput);
internal sealed record DspMetadata(double ResamplerDelay, int FlushInput, int MaximumOutput, double Operations, string Resampler, string Acceleration, double BlockSeconds, string? Restorer, int RestoreLatency, string? Upscaler, int UpscaleLatency);
/// <summary>LAN metadata and binary float64 audio, without encryption or audio compression.</summary>
internal static class DspProtocol
{
    internal const int Version = 3, MaximumMessage = 64 << 20;
    internal static byte[] Json<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value);
    internal static T Parse<T>(ReadOnlySpan<byte> bytes) => JsonSerializer.Deserialize<T>(bytes) ?? throw new InvalidDataException("Empty DSP metadata.");
    internal static void WriteSamples(BinaryWriter writer, ReadOnlySpan<double> samples)
    {
        if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("Network DSP requires little-endian peers.");
        writer.Write(MemoryMarshal.AsBytes(samples));
    }
    internal static void ReadSamples(BinaryReader reader, Span<double> samples) => reader.BaseStream.ReadExactly(MemoryMarshal.AsBytes(samples));
}
