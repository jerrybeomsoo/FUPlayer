# Settings reference

Every setting, what it changes, and what it costs. Settings are stored in `%APPDATA%\FUPlayer\settings.json`.

## DSP studio

### Output

| Setting | Options | Notes |
| --- | --- | --- |
| Output mode | PCM, DSD, Follow source | DSD bypasses the DAC's modulator as well as its filter |
| Rate selection | Highest rate, Same family only, Fixed rate | "Same family" keeps 44.1 kHz sources on 44.1 kHz multiples |
| Highest rate | Up to 1.536 MHz | Ceiling for automatic selection |
| Fixed rate | Any rate the device reports | Used when rate selection is Fixed |
| DAC word length | 16, 20, 24, 32 bit | Valid bits sent to the device |
| Highest DSD rate | DSD64 to DSD1024 | DSD1024 is 45.1 MHz and needs a fast machine |
| DSD delivery | Native DSD, DoP | Native needs an ASIO driver in DSD mode |
| DSD at 48 kHz multiples | on / off | For 48 kHz family sources. Not all DACs accept these |
| Pass DSD files through | on / off | Send matching DSD files unmodulated |

### Processing

| Setting | Options | Notes |
| --- | --- | --- |
| Filter length | Automatic, 16,384 to 33,554,432 taps | Counted at the output rate. Sets transition width and group delay |
| Filter staging | Single stage, Multi-stage | Multi-stage is roughly half the cost, at the price of a cascade of responses |
| Convolution | Frequency domain, Tap by tap | Tap by tap adds no block latency and hides the three settings below |
| Crossover, in taps a phase | Built-in 1,024, 256 to 262,144, Never | Where the FFT path takes over from direct convolution |
| Longest block (FFT window) | Cheapest, 100 ms down to 1.5 ms | Latency against arithmetic. Does not change the output samples |
| Partition layout | One block length, Layered | Layered is far cheaper at a short block and cannot use a GPU |
| Remove ultrasonics | on / off | 20 kHz low-pass for sources above 48 kHz |
| Peak limiter | on / off | Catches intersample peaks above full scale |
| Watch for band-edge ringing | on / off | Counts ringing at the source Nyquist frequency |

**What the filter length costs.** At 705.6 kHz, roughly:

| Taps | Transition | Group delay | Coefficients |
| --- | --- | --- | --- |
| 65,536 | 90 Hz | 46 ms | 0.5 MB |
| 262,144 | 23 Hz | 186 ms | 2 MB |
| 1,048,576 | 6 Hz | 743 ms | 8 MB |
| 4,194,304 | 1.4 Hz | 3.0 s | 60 MB with spectra |
| 33,554,432 | 0.18 Hz | 23.8 s | about 1 GB with spectra |

**What the block costs.** Relative to the cheapest block, for filters of 65,537 to 1,048,577 taps:

| Block ceiling | Samples at 44.1 kHz | FFT size | One block length | Layered |
| --- | --- | --- | --- | --- |
| Cheapest | 2,048 | 4,096 | 1x | 1x |
| 25 ms | 1,024 | 2,048 | 1.1x to 1.7x | same |
| 10 ms | 256 | 512 | 1.8x to 6x | 1.1x to 1.8x |
| 3 ms | 128 | 256 | 3x to 12x | 1.2x to 2x |
| 1.5 ms | 64 | 128 | 5x to 24x | 1.3x to 2.2x |

### Lossy repair

| Setting | Options | Notes |
| --- | --- | --- |
| Neural restorer | on / off | Coded 44.1/48 kHz stereo back towards lossless at the same rate; 91 ms at 48 kHz, 99 ms at 44.1. See [neural-restorer.md](neural-restorer.md) |
| Neural upscaler | on / off | 44.1/48 kHz to 88.2/96 kHz; about 0.55 s. With the restorer on, it runs after it and reads its output as lossless. See [neural-upscaler.md](neural-upscaler.md) |
| Upscaled band level | Quiet −6 dB, Measured, Lifted +3 dB, Strong +6 dB | Gain on what the upscaler writes above the source Nyquist |
| Source type | Automatic, Lossy, Lossless | Automatic treats lossy codecs and captures as coded and PCM, FLAC and ALAC as lossless. The restorer runs only on coded sources |
| Output delta | on / off | Plays only what the networks changed. For monitoring |

The peak limiter runs while either network does, whatever its own switch says.

### Dither and modulation

Eleven dither options, from none to five noise-shaping curves, and six delta-sigma modulators from 5th to 9th
order. The automatic dither setting picks by output rate. See [dsp.md](dsp.md) for what each family does.

## Output page

| Setting | Notes |
| --- | --- |
| Output type | WASAPI exclusive, WASAPI shared, ASIO, file |
| Device | Devices the back-end reports. Capabilities are probed when the page is shown |
| Output channels, first channel | For interfaces with more than two outputs |
| Device buffer | Driver default, or an explicit length. Smaller reacts faster |
| Low-latency FIFO | Halves the engine FIFO. Faster response, higher dropout risk |
| Instant pause | Pause switches straight to silence instead of fading |

## Settings page

### Playback

Gapless playback, ReplayGain (off, track, album), clipping prevention for positive ReplayGain, polarity
inversion, and the time display format.

### Processing resources

| Setting | Notes |
| --- | --- |
| DSP threads | Automatic uses every core. Explicit values are the extra threads beyond the engine thread |
| Engine FIFO | 100 ms to 60 s. The row shows the allocation, and the live line shows the real one |
| Meter the adjusted source | Whether source meters show the file before or after ReplayGain and ultrasonic filtering |

The FIFO ring is a power of two bytes, so the allocation rounds up and holds slightly longer than asked. At
44.1 kHz stereo, 1 s is 512 KiB; at 705.6 kHz or DSD512, it is 8 MiB. The engine raises the setting if the device
buffer or a block-based filter needs more, and caps the ring at 256 MiB.

### Graphics acceleration

| Setting | Notes |
| --- | --- |
| Convolve on the graphics device | Off by default |
| Device | Any OpenCL 1.2 device. Automatic picks the most capable |
| Arithmetic precision | 64-bit is bit-identical to the CPU. 32-bit is 2x to 3x faster, typically -130 dB from the CPU result |
| Phase division | Measured and re-measured, measured once, or a fixed 25, 50, 75 or 100 percent |
| Override the timed result | Use the device even where the CPU measured faster |

A device is never used until it has reproduced the processor's own output for that filter. Overriding the timed
result does not override that check.

## Live input page

| Setting | Options | Notes |
| --- | --- | --- |
| Mute the application's own playback | on (default) / off | Mutes the devices the captured application plays to while it is captured, so it is heard once, through the player. The device is muted rather than the application's session, which would silence the capture too. The player's own WASAPI device is left alone, and a device unmuted by hand stays unmuted. Anything muted is unmuted when the capture ends, or on the next start after a crash |

## Calibration page

Per-channel level trim and distance compensation, with a pink noise generator at -20 dBFS RMS that runs through
the complete processing chain.
