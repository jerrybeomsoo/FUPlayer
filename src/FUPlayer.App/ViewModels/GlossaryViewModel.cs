using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FUPlayer.Core.Localization;

namespace FUPlayer.App.ViewModels;

/// <summary>A term the player uses and what it means, in the interface's language.</summary>
public sealed class GlossaryEntry(string term, string explanation)
{
    /// <summary>The term as the interface names it.</summary>
    public string Term { get; } = Loc.T(term);

    /// <summary>The usual English name, shown beside a translated term so it can be recognised elsewhere.</summary>
    public string Original { get; } = term;

    public bool HasOriginal => !string.Equals(Term, Original, StringComparison.Ordinal);

    public string Explanation { get; } = Loc.T(explanation);

    public bool Matches(string search) =>
        Term.Contains(search, StringComparison.CurrentCultureIgnoreCase)
        || Original.Contains(search, StringComparison.OrdinalIgnoreCase)
        || Explanation.Contains(search, StringComparison.CurrentCultureIgnoreCase);
}

public sealed record GlossaryGroup(string Title, IReadOnlyList<GlossaryEntry> Entries);

/// <summary>
/// Plain explanations of the words the player's pages use: the listener this is for knows music, not signal
/// processing, and most of the settings are named after the mathematics behind them.
/// </summary>
public sealed partial class GlossaryViewModel : ObservableObject
{
    private readonly IReadOnlyList<GlossaryGroup> _all = Build();

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _hasResults = true;

    public GlossaryViewModel() => Groups = new ObservableCollection<GlossaryGroup>(_all);

    public ObservableCollection<GlossaryGroup> Groups { get; }

    partial void OnSearchTextChanged(string value)
    {
        string search = value.Trim();
        Groups.Clear();
        foreach (GlossaryGroup group in _all)
        {
            GlossaryEntry[] entries = search.Length == 0 ? [.. group.Entries] : group.Entries.Where(e => e.Matches(search)).ToArray();
            if (entries.Length > 0)
            {
                Groups.Add(group with { Entries = entries });
            }
        }

        HasResults = Groups.Count > 0;
    }

    private static List<GlossaryGroup> Build() =>
    [
        Group("The basics",
            ("Sample rate",
                "How many times a second the waveform is measured and stored. CD audio uses 44,100 measurements a second (44.1 kHz); high-resolution files use 88.2, 96, 176.4 or 192 kHz and more. A higher rate can hold higher frequencies, up to half the rate."),
            ("Bit depth (word length)",
                "How many bits each measurement has. 16 bits give 65,536 steps and a dynamic range of about 96 dB; 24 bits give about 144 dB, more than any real recording or DAC uses. Fewer bits mean a higher noise floor."),
            ("PCM",
                "Pulse-code modulation: the usual way to store digital audio, as a series of numbers, one per channel for every sample. WAV and FLAC files, and the decoded audio of most other formats, are PCM."),
            ("Nyquist frequency",
                "Half the sample rate: the highest frequency a sampled signal can hold, 22.05 kHz for 44.1 kHz audio. Anything above it cannot be represented, and if it gets in it folds back down as aliasing."),
            ("Aliasing",
                "Distortion that appears when content above the Nyquist frequency is sampled or created: it folds back into the band below as tones that were never in the music. Filters exist largely to prevent it."),
            ("Decibel (dB) and dBFS",
                "A logarithmic unit for level ratios: +6 dB is about twice the amplitude, −20 dB a tenth of it. dBFS measures against digital full scale, the largest value a file can hold, so every real signal is at 0 dBFS or below."),
            ("Clipping and overs",
                "A sample that would go beyond full scale cannot be stored and is cut off flat: that is clipping, heard as harsh distortion. An over is such a peak; inter-sample overs are peaks that only appear between the samples once the waveform is reconstructed."),
            ("Headroom",
                "The space left between a signal's peaks and full scale. Processing that can raise peaks, such as filtering or adding gain, needs headroom so that it does not clip."),
            ("Lossless and lossy",
                "Lossless formats (WAV, FLAC, ALAC) keep every sample exactly. Lossy codecs (MP3, AAC, Opus, Vorbis) throw away what a model of hearing says will not be missed, usually including most of the top octave, to make files much smaller."),
            ("Codec",
                "The method that encodes and decodes a file's audio: FLAC, MP3, AAC and so on. The container, such as .m4a or .ogg, is the box the coded audio travels in."),
            ("Bit-perfect",
                "Playback in which the samples reach the DAC exactly as they are stored, with no change of rate, level or bits. It is what the None (bit-perfect) filter gives with a device that accepts the file's own format."),
            ("Latency",
                "The delay between audio going in and coming out. Long filters and large buffers add latency; that is harmless for music, but it matters when the sound has to match a picture."),
            ("Spectrum and FFT",
                "A spectrum shows how much of each frequency a signal contains. The FFT (fast Fourier transform) is the calculation that computes it from a block of samples; more points give finer frequency detail over a longer stretch of time."),
            ("Window function",
                "A smooth curve that fades a block of samples in and out before a transform, or a filter in and out when it is cut to length. It decides how much energy leaks from one frequency into its neighbours (the side lobes) against how sharply frequencies are told apart. Hann, Kaiser, Gaussian and Nuttall are windows."),
            ("Bin",
                "One frequency slot of an FFT. Its width is the sample rate divided by the FFT size: 48 kHz over 8,192 points is about 5.9 Hz a bin.")),

        Group("Filters and rate conversion",
            ("Resampling",
                "Changing a signal from one sample rate to another, for example from 44.1 kHz to 705.6 kHz. Raising the rate (upsampling or oversampling) adds no new musical information; it moves the filtering the DAC would otherwise do into software, where it can be done far more precisely."),
            ("Images",
                "When the sample rate is raised, copies of the original spectrum appear mirrored around multiples of the old rate. Removing these images is the resampling filter's job; what it lets through remains as ultrasonic noise and can cause intermodulation."),
            ("Low-pass filter",
                "A filter that lets low frequencies through and blocks high ones. The resampling filters here are low-pass filters placed at the source's Nyquist frequency."),
            ("Passband, transition band and stopband",
                "The passband is the range a filter lets through unchanged, the stopband the range it blocks, and the transition band the stretch between, where it goes from one to the other. A steep filter has a narrow transition band, and that takes a longer filter."),
            ("Attenuation",
                "How strongly the stopband is suppressed, in dB. At 150 dB what is left is a thirty-billionth of the original amplitude, far below the noise of any recording."),
            ("Taps",
                "The coefficients of a FIR filter: every output sample is a weighted sum of that many input samples. More taps allow a steeper, deeper filter, and cost more arithmetic and more delay."),
            ("FIR and IIR filters",
                "A FIR (finite impulse response) filter computes each output from a fixed stretch of input and can have exactly linear phase. An IIR (infinite impulse response) filter feeds its output back into itself: very few coefficients for a steep response, but its phase cannot be linear."),
            ("Sinc and windowed sinc",
                "The sinc function, sin(x)/x, is the impulse response of the ideal low-pass filter, but it goes on for ever. A windowed sinc cuts it to a usable length with a window function, and the window chosen (Kaiser, Gaussian, Nuttall) sets the balance between steepness, stopband depth and ringing."),
            ("Kaiser, Gaussian and Nuttall windows",
                "Three families of window. Kaiser has a parameter that trades transition width for stopband depth and is close to the best possible for its length. Gaussian has no ripple in the transition band and a compact impulse. Nuttall's cosine windows have a fixed stopband that keeps falling further away from its edge."),
            ("Ringing and pre-ringing",
                "A steep filter answers a sudden sound with a short, decaying oscillation at its cut-off frequency: ringing. A linear-phase filter rings both before and after the event (pre-ringing and post-ringing); a minimum-phase filter only after it. For a 44.1 kHz source the ringing is at about 22 kHz."),
            ("Linear, minimum and intermediate phase",
                "Linear phase delays every frequency by the same time, so the waveform keeps its shape, with ringing on both sides of a transient. Minimum phase puts all the ringing after the transient but delays the frequencies near the cut-off a little more than the rest. Intermediate phase lies between the two."),
            ("Group delay",
                "How long a filter delays the signal. For a linear-phase filter it is half its length: 1,048,576 taps at 705.6 kHz is about 0.74 seconds. It does not change the sound, only when it arrives."),
            ("Transient-aligned filter",
                "A reconstruction filter that keeps the ideal sinc exactly over most of its length and shapes only its ends, so that the waveform around each transient is rebuilt as the ideal, endless filter would rebuild it. The original samples pass unchanged. FUPlayer's own design of an idea Rob Watts described for Chord Electronics' converters; see docs/transient-aligned-filter.md."),
            ("Apodizing filter",
                "A filter that is already fully closed below the source's Nyquist frequency. It gives up a little of the top octave to remove ringing and aliasing that the recording's own converters left at the band edge."),
            ("Impulse and step response",
                "What a filter makes of a single click (an impulse) and of a sudden jump in level (a step). They show its ringing and delay in time, where the magnitude response shows its behaviour across frequency."),
            ("Convolution",
                "The operation that applies a FIR filter: every output sample is the sum of input samples multiplied by the filter's taps. Done directly, its cost grows with the number of taps; done with FFTs on blocks (partitioned convolution), it grows only slowly with length, at the price of a block of delay."),
            ("Polyphase",
                "An efficient way to change the rate by a ratio: the long filter is split into phases so that only the samples actually produced are computed. A 16× conversion has 16 phases."),
            ("Half-band filter",
                "A filter for exact 2× conversions in which almost every other tap is zero, so it costs about half as much. Multi-stage conversion uses a chain of them after the selected filter."),
            ("Multi-stage conversion",
                "Converting a large ratio in steps: the selected filter converts 2×, and cheaper half-band stages double the rate the rest of the way. It costs roughly half as much as a single stage, but the result is a cascade of several filters' responses."),
            ("Rate families",
                "Sample rates come in two families: multiples of 44.1 kHz (44.1, 88.2, 176.4 …) and multiples of 48 kHz (48, 96, 192 …). Converting within a family is a simple whole-number ratio; converting between them needs a more complex fractional ratio.")),

        Group("Word length, dither and noise shaping",
            ("Quantization",
                "Rounding each sample to the nearest value the output word length can hold. The rounding error is quantization noise; without dither it follows the music and becomes distortion at low levels."),
            ("Dither",
                "A tiny amount of random noise added before quantization. It turns the rounding error into a steady, benign hiss that no longer depends on the music, which removes quantization distortion. It is needed whenever the word length is reduced, for example from the player's 64-bit processing to a 24-bit DAC."),
            ("Noise shaping",
                "Filtering the quantization noise so that less of it lies where hearing is sensitive and more lies where it is not, usually above 20 kHz. At high output rates there is plenty of room up there, so the audible band can be made much quieter."),
            ("LSB",
                "Least significant bit: the smallest step a word length can express. Dither levels are given in LSBs.")),

        Group("DSD",
            ("DSD (Direct Stream Digital)",
                "The 1-bit format of the SACD: instead of multi-bit samples, a very fast stream of single bits whose density follows the waveform. DSD64 runs at 2.8224 MHz, 64 times 44.1 kHz; DSD128, DSD256 and DSD512 are two, four and eight times faster."),
            ("Delta-sigma modulation",
                "The process that turns a multi-bit signal into a 1-bit stream at a very high rate. It moves the large quantization noise of a single bit out of the audio band into the ultrasonic range. Almost every modern DAC does this inside; FUPlayer can do it in software and send DSD."),
            ("Modulator order",
                "How aggressively a delta-sigma modulator shapes its noise. A higher order pushes more noise out of the audio band, leaves more above it and takes more care to keep stable. The DAC's analogue filter has to remove what is pushed up."),
            ("Noise transfer function (NTF)",
                "The curve that shows how a noise shaper or modulator spreads its noise across frequency: low in the audio band, rising above it. The Noise shaping view in the DSP studio plots it."),
            ("DoP (DSD over PCM)",
                "A way to send DSD through connections that only carry PCM: 16 DSD bits and a marker are packed into each 24-bit PCM sample. The DAC recognises the markers and plays DSD. DSD64 travels as 176.4 kHz PCM."),
            ("Native DSD",
                "Sending DSD to the driver as it is, without packing it into PCM. It needs a driver that supports it, usually ASIO with a DSD mode, and allows higher DSD rates than DoP.")),

        Group("Output and playback",
            ("DAC",
                "Digital-to-analogue converter: the device that turns the digital signal into the voltage that goes to the amplifier and the speakers or headphones."),
            ("Driver, ASIO and WASAPI",
                "The driver is the software through which a program talks to the audio device. ASIO is a professional driver standard with low latency and often native DSD. WASAPI is Windows' own: in exclusive mode it gives the device to one program unchanged (bit-perfect), in shared mode Windows mixes every program's sound at one rate."),
            ("Buffer, FIFO and underrun",
                "Buffers hold audio waiting to be played. The FIFO is the player's own buffer between the processing and the device. If processing falls behind and a buffer runs empty, that is an underrun, heard as a dropout."),
            ("Limiter",
                "An automatic level control that briefly turns the level down just enough to keep peaks below full scale, so that they are not clipped. The look-ahead limiter here sees each peak 1 ms before it arrives."),
            ("ReplayGain",
                "Tags that record how loud a track or album is, so that the player can play everything at a similar loudness. Track gain evens out single songs; album gain keeps the differences between an album's songs."),
            ("Gapless playback",
                "Playing one track after another without a pause between them, as a live album or a classical work needs."),
            ("Polarity",
                "Whether the waveform goes up or down first. Inverting polarity flips it; some recordings were made with inverted polarity, and some listeners hear a difference.")),

        Group("Neural processing",
            ("Neural restorer",
                "A neural network trained to bring lossy-coded music (MP3, AAC, Opus, Vorbis) back towards the lossless original: it rebuilds high frequencies and stereo detail the codec removed. It runs with ONNX Runtime, on the processor or a graphics adapter."),
            ("Exciter",
                "A process that adds harmonics to a sound. FUPlayer's oversampling exciter makes them from the music above 7 kHz at twice the rate it runs at, so none fold back as tones the music never had. They fade in under the music from about 15 kHz and fill what is missing above where its spectrum ends, up to 44.1 or 48 kHz when the output allows."),
            ("Neural upscaler",
                "A neural network that doubles the sample rate of 44.1 and 48 kHz music and writes plausible content above the original Nyquist frequency, where plain resampling leaves silence."),
            ("LSD (log-spectral distance)",
                "A measure of how far one spectrum is from another, in dB: the smaller it is, the closer the result is to the original. The model descriptions give it before and after processing.")),

        Group("Connections and tools",
            ("UPnP and DLNA",
                "Network standards that let one device play to another. A controller (such as foobar2000 or a phone app) chooses what to play; a renderer (such as FUPlayer's UPnP input) receives the stream and plays it."),
            ("FFmpeg and LGPL",
                "FFmpeg is a widely used library that decodes almost every audio format. FUPlayer uses its LGPL-licensed parts as separate files that can be replaced, as the LGPL requires, and can build them from the official source for you."),
            ("OpenCL and GPU offload",
                "OpenCL lets programs run calculations on a graphics card. FUPlayer can move the convolution of long filters there, leaving the processor free for the rest."),
            ("DirectML",
                "Windows' own way of running neural networks on a graphics adapter. FUPlayer can run the restorer and the upscaler through it, each on the device chosen for it. Whether that is faster depends on the card and the network: a large network gains most, a small one called often can lose to the trips to the card."),
            ("Loopback capture",
                "Taking what another application plays before it reaches the speakers, so that it can go through the player's processing. Live input works this way.")),
    ];

    private static GlossaryGroup Group(string title, params (string Term, string Explanation)[] entries) =>
        new(Loc.T(title), entries.Select(e => new GlossaryEntry(e.Term, e.Explanation)).ToArray());
}
