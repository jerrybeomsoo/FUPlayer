# Neural upscaler

A network that takes a 44.1 or 48 kHz recording to 88.2 or 96 kHz. Below 22 kHz it undoes what it
learned a lossy codec does; above the source's own Nyquist rate it writes a band the file never had.
It lives in DSP Studio under **Lossy repair**, and it is off until asked for.

Read this first: **it recovers nothing.** An MP3 does not contain the band its encoder discarded, and a
CD-rate file never contained anything above 22 kHz. What the network writes there is its estimate of
what a high-resolution master of that kind of music usually carries, learned from real masters. It
can be measured against the master it came from, which is what this page does, and it can be heard
alone with **Output the repair alone**.

No model ships with the player. A model is weights fitted to somebody's music; train your own from
your own high-resolution files with the scripts in
[training/neural-upscaler](../training/neural-upscaler/README.md), and put the `.onnx` file and the
`.json` written beside it into the `models` folder next to `FUPlayer.exe` or into
`%APPDATA%\FUPlayer\models`. DSP Studio says what it found.

## When it runs

- The source is PCM at 44.1 or 48 kHz: a file of any format, or a capture from another application.
- The output runs at least twice as fast: 88.2 kHz and up for a 44.1 kHz source, or any DSD rate.
  Otherwise the band it writes would be filtered straight back out, and Now playing says so.
- **Lossy repair** is on. For a source the upscaler runs on, the older stages (artefact reduction,
  harmonic rebuilding, prediction and ultrasonic rebuilding) stand aside; they still serve sources it
  cannot, such as a lossy file at 96 kHz.

Now playing shows which model is running and at what rate. With **Output the repair alone** on, the
output is what the network changed and nothing of the recording it changed it in.

**Upscaled band level** sets how loud the band above the source's Nyquist rate is: Quiet (−6 dB),
Measured (what the network writes, the default), Lifted (+3 dB) or Strong (+6 dB). The gain is applied
to those frequencies only, after the network, so nothing of the recording moves. On the command line it
is `--upscaler-level <dB>`.

The peak limiter runs whenever the upscaler does, whatever its own switch says. The band the network
writes adds energy, and on a loud master its peaks cross full scale; left alone they would clip, or push
a DSD modulator the way a lossy decode's overs did.

## What it does to the signal

1. **Interpolation to twice the rate**, with the exact filter the network was trained behind: a
   Kaiser-windowed sinc over 64 zero crossings, β 14.77, pass band rolled off at 99 percent of the old
   Nyquist rate. The player reproduces torchaudio's kernel coefficient for coefficient, because the
   transition band just below the edge is exactly where the network looks to decide where the music
   stops.
2. **A short-time Fourier transform** of 2,048 points at a hop of 512, periodic Hann window, the first
   frame centred on the first sample, which is what `torch.stft` does.
3. **The network**, about ten million parameters: eight ConvNeXt blocks over time with the frequency
   bins as channels, seeing 272 ms either side of every frame. It reads log magnitudes, floored 85 dB
   below each frame's loudest bin, and answers per bin with three things: a complex gain for what the
   input carries, a magnitude and phase for what it lacks, and a crossover between the two. The
   crossover puts the seam wherever this frame's music actually stops, 16 kHz for one MP3 and 22 kHz
   for a CD.
4. **Overlap-add** back to a waveform. Frames go through the network 64 at a time with 26 frames of
   music either side, and only the middle ones are kept, so every frame that comes out saw all the
   context it was trained with rather than padding. The player's output matches PyTorch's to better than
   −60 dB on the test fixture, at every chunk size tested.
5. **The chosen filter** takes it from 88.2 or 96 kHz to the output rate, PCM or DSD.

### What it costs

Measured on a four-core laptop (i7-7820HQ) with a ten-million-parameter network:

| | without | with the upscaler |
| --- | --- | --- |
| Delay | filter only | + 0.55 s at 88.2 kHz, 0.50 s at 96 kHz |
| DSP load, 44.1 kHz file to 88.2 kHz PCM, playing | — | 11 to 13 % |
| DSP load, 44.1 kHz file to DSD256, playing | 20 % | 31 to 32 % |
| Offline, 90 s of Vorbis to 88.2 kHz | 22× real time | 8.0× real time |

Smaller chunks answer sooner and cost more, because the 52 frames of context are paid again for every
call: 128 frames held the sound 0.92 s at 7.8× real time, 64 frames 0.55 s at 7.1×, 32 frames 0.36 s at
5.1×, 16 frames 0.27 s at 3.3×. 64 is what the player uses.

The network runs on the processor through ONNX Runtime (MIT), with a quarter of the logical processors
for its own threads. The graphics card is not needed.

## How the reference model was made

Everything below was done with the scripts in `training/neural-upscaler`, on one library, and is
written down because each step was measured and several of them went wrong first.

**The corpus.** Every high-resolution ALAC file in the library, 88.2 to 192 kHz, except classical and
instrumental music: 381 tracks, 28.7 hours, with Japanese pop and anime music weighted three times.
192 kHz masters were used at 96 kHz and 176.4 kHz at 88.2. Of each track a 150-second excerpt, and three
copies of it brought to 44.1 or 48 kHz: one in seven left lossless, the rest through MP3 (96 to
320 kbit/s, V0, V2), FFmpeg's and Windows' AAC (128 to 256), Vorbis (q3 to q7) or Opus, decoded, and
lined up with the original by cross-correlation. A fourth copy went through Apple's own AAC encoder,
which is what Apple Music delivers and which is not FFmpeg's: at 256 kbit/s it keeps the band to about
21 kHz where FFmpeg's stops at 20, and at 128 to 17.5 kHz where FFmpeg's stops at 16. Half of the 96 kHz
tracks took the path Apple Music takes when it is captured on Windows, coded at 44.1 kHz and resampled
to 48 on the way into the mixer.

**Not every high-resolution master is one.** The slope from 8-16 kHz to 24-32 kHz, and from 24-32 kHz to
32-40 kHz, was measured on every excerpt, decoded afresh. None of this library's masters turned out to
be a CD-rate recording sold at a higher rate. But four 88.2 kHz albums, all of them transfers from
a 1-bit source, get louder above 32 kHz by 7 to 13 dB on median: what they carry up there is the
modulator's noise, and a network fitted to them learns to add hiss. Those 40 tracks, 3 more that rise
the same way and 2 that are flat to 32 kHz were left out, which leaves 336.

**The split** is by song, not by track, so that a single, an acoustic version and an album version of
one song are never on both sides: 315 tracks to train on, 21 held out, 17 of those Japanese.

**Training** ran on a Quadro M2200 with 4 GB that was also driving the display, about nine hours in
all. First multi-resolution spectral loss (512 to 4,096 points, the band above 18 kHz counted twice)
with anti-wrapping phase losses, in batches of 12 crops a third of a second long, at 0.15 s a step;
then adversarial training with a multi-period waveform discriminator and a multi-resolution spectral
one, hinge losses and feature matching, in batches of 4, at about a second a step. The spectral phase
makes the level and the envelope right; the adversarial phase makes the texture of the written band
behave like a real one's rather than like its average.

The schedule was not the one planned, because three of the problems below were found while it ran:
30,000 spectral and 13,821 adversarial steps on the first input; 2,000 spectral and 716 adversarial
steps adapting to the floored input with the Apple copies added; then 14,000 adversarial steps on the
screened 336 tracks. 60,537 steps in all, the last 16,716 of them on what the player actually runs.

### What went wrong on the way

**A silent crop sent the loss past two thousand.** Against a target with nothing in it, spectral
convergence divides by nothing; two such crops in the first 2,500 steps threw the model off for
hundreds of steps each. Crops whose master is below −70 dBFS are now drawn again, and the ratio has a
floor far below any music.

**The network learned how the corpus was stored.** The corpus is float16, whose step is proportional
to the signal: its rounding noise sits 114 dB under each frame's loudest bin on median and 84 dB at
worst in 999 bins of 1,000. Read with an absolute floor, that noise was visible exactly above a codec's
cutoff, where the network decides what is missing, and a clean decode in the player has nothing there.
Held-out songs, coded afresh and fed in clean, came out up to 1.9 dB further from the master between 16
and 22 kHz than the stored copies of the same decodes, and the level of the written band moved by up to
5 dB. The network now reads nothing more than 85 dB below each frame's loudest bin, which the float16
noise, 16-bit dither and a clean decode all fall under alike; some crops are also requantised to
16 bits with dither during training, which is what a CD rip looks like. The model was fine-tuned on
the new input rather than trained again.

**"Best" would never have picked the adversarial phase.** The checkpoint kept was the one with the
smallest log-spectral distance on held-out songs, and adversarial training trades a little of that
distance for texture on purpose, so the rule would always have kept the spectral phase's model. The
adversarial phase now keeps its own best and a snapshot every 4,000 steps, and the model was chosen by
the measurements below, which include the level of the written band and how much it moves from frame
to frame, not by distance alone.

**The player and PyTorch disagreed at −43 dB, and that was the input, not the player.** Rendered by
the player and computed in PyTorch from the same Apple AAC file, the first network's outputs differed
by −42.6 dB overall while scoring the same against the master to a tenth of a decibel. Two things were
different: the player's interpolator delays the stream by 130 samples, which moves its frame grid, and
the PyTorch side read a copy stored as float16. Given the same decode, the same grid and the floored
input, the trained network's output from the player matches PyTorch's to −134 dB and better, and the
only samples that differ are the ones that crossed full scale.

**Which is how the upscaler was found to make overs.** Those samples reached 1.014 on a lossless file
with the limiter off. The band the network writes adds energy, so the player now runs the limiter
whenever the upscaler runs, whatever its switch says, as it already did for lossy sources.

## Does it work

### On the songs kept out of training

Every degraded copy the corpus holds of the 21 held-out songs: 12 seconds from the middle of each, both
channels, interpolated to the master's rate and put through the network. Log-spectral distance to the
master in dB, before → after (lower is closer); the level of the band above 22 kHz against the
master's; and how much that band moves from frame to frame, as a fraction of how much the master's does.

| Source | n | 0-16 kHz | 16-22 kHz | above 22 kHz | level above 22 kHz | movement |
| --- | --- | --- | --- | --- | --- | --- |
| Lossless, CD rate | 8 | 0.37 → **0.52** | 4.32 → **2.78** | 47.34 → **9.57** | -7.9 → **-0.5** dB | 0.93 |
| MP3, 96 and 128 kbit/s | 12 | 8.89 → **7.66** | 38.47 → **11.46** | 53.73 → **11.37** | -22.9 → **-3.3** dB | 0.95 |
| MP3, 192 to 320 kbit/s, V0, V2 | 18 | 2.86 → **2.86** | 28.02 → **8.63** | 51.69 → **10.32** | -19.6 → **-0.8** dB | 0.92 |
| Apple AAC, 256 kbit/s | 13 | 2.12 → **2.13** | 13.24 → **4.73** | 53.89 → **9.68** | -21.6 → **-1.4** dB | 0.94 |
| Apple AAC, 128 to 192 kbit/s and VBR | 8 | 4.41 → **4.20** | 25.19 → **8.47** | 49.01 → **10.39** | -17.7 → **-2.3** dB | 0.91 |
| FFmpeg and Windows AAC, 128 to 256 kbit/s | 15 | 3.62 → **3.57** | 21.90 → **7.27** | 51.46 → **9.95** | -20.5 → **-0.4** dB | 0.93 |
| Vorbis q3 to q7 | 7 | 4.64 → **4.63** | 24.63 → **7.87** | 51.76 → **9.62** | -18.9 → **-1.2** dB | 0.93 |
| Opus, 128 and 192 kbit/s | 3 | 3.61 → **3.61** | 17.92 → **7.90** | 51.34 → **10.94** | -14.8 → **+3.9** dB | 0.88 |
| **All** | 84 | 3.83 → **3.64** | 22.96 → **7.52** | 51.60 → **10.21** | -19.0 → **-1.2** dB | 0.93 |

- **Above 22 kHz** there was nothing, and there is now a band at about the level the masters carry,
  1.2 dB under them on average, that moves about as much as theirs do. Song by song it lands between
  5.9 dB under and 4.0 dB over its master, median 2.1 dB under, with one exception below.
- **Between 16 and 22 kHz**, where the codecs cut, two thirds of the distance to the master goes.
- **Below 16 kHz** the network mostly leaves the recording alone. Where a low bit rate damaged it, MP3 at
  96 and 128 kbit/s, it repairs some of that: 8.89 to 7.66 dB. On lossless copies it moves the quietest
  bins slightly, 0.37 to 0.52 dB.

What the adversarial phase was for, on the same songs: the network that had only the spectral loss
wrote the band 3.3 dB under the masters, moving 0.73 as much as theirs, a smooth average of what could
be there. The same network after adversarial training writes it 1.2 dB under, moving 0.93 as much. It
is 0.4 dB further from the masters between 16 and 22 kHz and 1.0 dB further above 22 kHz for it, which
is what texture costs in a distance that rewards the average.

### Through the player, on real files

The measurement that matters for listening: six held-out masters decoded afresh, 12 seconds of each made
into the files people have (an Apple AAC `.m4a` at 256 kbit/s, an MP3 at 320 and a 16-bit FLAC with
dither), played by `fuplayer-cli` into a WAV at twice the rate with the upscaler on, and scored against
the master. The 96 kHz masters were played both at 48 kHz and, resampled, at 44.1 kHz.

| File | Rate | 16-22 kHz | above 22 kHz | level above 22 kHz | movement |
| --- | --- | --- | --- | --- | --- |
| Apple AAC 256 `.m4a` | 44.1 kHz | 19.23 → **5.56** | 53.19 → **14.32** | -24.4 → **+1.0** dB | 1.07 |
| MP3 320 | 44.1 kHz | 34.25 → **8.21** | 53.52 → **15.93** | -24.2 → **+0.9** dB | 1.08 |
| FLAC, 16-bit | 44.1 kHz | 13.04 → **3.40** | 52.82 → **14.27** | -24.2 → **+1.4** dB | 1.07 |
| Apple AAC 256 `.m4a` | 48 kHz | 9.23 → **5.37** | 48.44 → **18.04** | -24.2 → **+4.0** dB | 1.05 |
| MP3 320 | 48 kHz | 32.36 → **7.81** | 48.22 → **18.00** | -24.8 → **+4.1** dB | 1.04 |
| FLAC, 16-bit | 48 kHz | 0.28 → **2.11** | 45.95 → **17.72** | -6.1 → **+4.8** dB | 1.05 |

At 44.1 kHz the written band sits within about 2.5 dB of the masters in every kilohertz from 16 to
30 kHz. The 48 kHz rows are pulled up by one song, the exception described next, which also accounts
for most of the one row where the network makes things worse: a lossless 48 kHz file already carries
16 to 22 kHz, and there is nothing there to repair.

### Where it is wrong

- **A master with nothing above 22 kHz gets a band anyway.** One held-out song's master falls away
  steeply above 20 kHz and carries almost nothing past 22. The network cannot tell that from the
  44.1 or 48 kHz file, which looks like every other, and writes the band a master of that kind usually
  has: 21 to 24 dB more than this one had. Its lossless 48 kHz copy was treated as a coded one too,
  and moved from 0.75 to 6.73 dB away from the master between 16 and 22 kHz.
- **It predicts texture, not notes.** Tones and synthesiser lines that a master carries above 22 kHz
  cannot be inferred from below it; the band it writes follows the transients and the spectral slope
  of the music, not its pitch.
- **Opus copies get a band about 4 dB too loud**, on the three held out. Opus is the smallest share of
  the corpus, and only ever at 48 kHz.
- **None of it was listened for.** These are spectral measurements against masters. What a band above
  22 kHz sounds like, through a given amplifier and tweeter, is not something this page can claim.
