namespace FUPlayer.Core.Audio;

/// <summary>How the samples of a stream are encoded.</summary>
public enum SampleEncoding
{
    /// <summary>Pulse-code modulation (multi-bit samples).</summary>
    Pcm,

    /// <summary>1-bit delta-sigma bit stream (DSD).</summary>
    Dsd,
}

/// <summary>Format of a decoded source stream.</summary>
/// <param name="Encoding">PCM or DSD.</param>
/// <param name="SampleRate">PCM frame rate, or the 1-bit sample rate for DSD (for example 2 822 400).</param>
/// <param name="Channels">Number of channels.</param>
/// <param name="BitsPerSample">Original resolution of the source (1 for DSD).</param>
public readonly record struct StreamFormat(SampleEncoding Encoding, int SampleRate, int Channels, int BitsPerSample)
{
    public static StreamFormat Pcm(int sampleRate, int channels, int bitsPerSample) =>
        new(SampleEncoding.Pcm, sampleRate, channels, bitsPerSample);

    public static StreamFormat Dsd(int bitRate, int channels) =>
        new(SampleEncoding.Dsd, bitRate, channels, 1);

    public bool IsDsd => Encoding == SampleEncoding.Dsd;

    public bool IsValid => SampleRate > 0 && Channels > 0 && BitsPerSample > 0;

    public string Describe() => IsDsd
        ? $"DSD{AudioRates.DsdMultiplier(SampleRate)} · {AudioRates.Format(SampleRate)} · {Channels} ch"
        : $"PCM · {AudioRates.Format(SampleRate)} · {BitsPerSample}-bit · {Channels} ch";

    public string DescribeShort() => IsDsd
        ? $"DSD{AudioRates.DsdMultiplier(SampleRate)}/{Channels}"
        : $"{AudioRates.FormatShort(SampleRate)}/{BitsPerSample}/{Channels}";

    public override string ToString() => Describe();
}
