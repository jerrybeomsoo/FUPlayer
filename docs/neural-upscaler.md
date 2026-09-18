# Neural upscaler

Lossy repair in FUPlayer is one function: a neural network that takes 44.1 or 48 kHz PCM to 88.2 or
96 kHz. Below the codec's cutoff it corrects what a lossy encoder did to the passband; above the cutoff,
and above the source's own Nyquist frequency, it synthesises the band the file does not carry. It lives
in DSP Studio under **Lossy repair**, and it is off until asked for.

Read this first: **it recovers nothing.** An MP3 does not contain the band its encoder discarded, and a
CD-rate file never contained anything above 22 kHz. What the network writes there is its estimate of
what a high-resolution master of that kind of music usually carries, learned from real masters. It can
be measured against the master it came from, which is what this page does, and it can be heard alone
with **Output delta**.

The release carries the model this page measures, in the `models` folder next to `FUPlayer.exe`. A model is
weights fitted to somebody's music; to use your own instead, train it from your own high-resolution files with the
scripts in [training/neural-upscaler](../training/neural-upscaler/README.md), and put the `.onnx` file and the
`.json` written beside it into that folder or into `%APPDATA%\FUPlayer\models`. DSP Studio says what it found.
For coded streams it can run after the [neural restorer](neural-restorer.md), which then hands it a source to read
as lossless.

## Settings

| Setting | Values | Effect | Command line |
| --- | --- | --- | --- |
| Neural upscaler | on, off | Runs the network on 44.1 and 48 kHz PCM sources. | `--neural-upscale`, `--upscaler <file>` |
| Source type | Automatic, Lossy, Lossless | Automatic reads lossy codecs and application captures as lossy, PCM, FLAC, ALAC and other lossless formats as lossless. Lossy: passband correction and synthesis above the cutoff. Lossless: passband left as it is, synthesis above Nyquist only. | `--source-type auto\|lossy\|lossless` |
| Upscaled band level | Quiet −6 dB, Measured 0 dB, Lifted +3 dB, Strong +6 dB | Gain on the frequency bins above the source Nyquist, applied after the network; nothing of the recording moves. | `--upscaler-level <dB>` |
| Output delta | on, off | Outputs y − x: the upscaler's output minus its latency-aligned input. Silent when the upscaler is idle. For monitoring. | `--output-delta` |

The peak limiter runs whenever the upscaler does, whatever its own switch says: the band the network
writes adds energy, and on a loud master its peaks cross full scale.

## When it runs

- The source is PCM at 44.1 or 48 kHz: a file of any format, or a capture from another application.
- The network always runs at twice the source rate, and the chosen filter converts from there to the
  output rate, up or down. At 88.2 or 96 kHz and above, or any DSD rate, the synthesised band reaches the
  output; below that the filter removes it again and only the passband correction remains.
- A **lossless** source at an output below twice its rate leaves the upscaler idle: there is no passband
  to correct and nowhere to put the band above it. Now playing says so.

## The chain

In the order the signal meets it, per channel:

| # | Stage | Rate | What it does |
| --- | --- | --- | --- |
| 1 | Decoder, ReplayGain | fs (44.1/48 kHz) | Decoded PCM, level-matched. With the [neural restorer](neural-restorer.md) on, a coded stereo source is restored here first, both channels together, and the upscaler then reads it as lossless. |
| 2 | Source meters, bandwidth readout | fs | Measurement only: levels, spectrum, where the source's spectrum ends. |
| 3 | 2× interpolation | fs → 2 fs | Kaiser-windowed sinc, 64 zero crossings, β 14.77, roll-off 0.99: the exact kernel the network was trained behind. |
| 4 | STFT | 2 fs | 2,048-point periodic Hann, hop 512, first frame centred on the first sample. |
| 5 | Network | frames | Reads log magnitudes floored 85 dB below each frame's peak, and the lossy/lossless flag. Answers per bin with a complex gain for what the input carries, a magnitude and phase for what it lacks, and a crossover between them; 272 ms of context either side. |
| 6 | Upscaled band level | frames | Gain on the bins above fs/2. |
| 7 | Overlap-add | 2 fs | 64 frames per network call with 26 of context either side; fixed delay of 48,128 samples. |
| 8 | Output delta (optional) | 2 fs | Subtracts the stage-3 signal, delayed by the same 48,128 samples. |
| 9 | Rate conversion | 2 fs → output | The chosen filter, to the PCM output rate or the modulator's processing rate. |
| 10 | Volume, peak limiter, speaker trims | output | Limiter forced on while the upscaler runs. |
| 11 | Dither and noise shaping, or delta-sigma modulation | output | PCM word length, or DSD. |

The player's stages 3 to 7 match PyTorch's to better than −60 dB on the test fixture at every chunk size
tested, and on a real Apple AAC file, given the same decode and frame grid, to −134 dB wherever nothing
crossed full scale.

## What it replaced

Until this version lossy repair had four more stages. Each did part of what the network does, measured
worse on the same held-out songs, and was removed:

| Stage | What it did | Why it went |
| --- | --- | --- |
| Artifact reduction | Limited how far a high bin could move between transforms, to steady a band an encoder switched on and off. | The network's complex mask corrects the passband from data; the stage's own measurements never supported it. |
| Harmonic rebuilding | Copied the octave below the codec cutoff upwards in patches. | Fixed synthesis of what the network generates with learned magnitude, phase and a per-frame crossover. |
| Generative prediction | A small network setting the levels of the two stages above. | No stage of its own: with nothing else switched on it did nothing, which is also why the difference monitor stayed silent. |
| Ultrasonic rebuilding | A patch above the source Nyquist, levelled by a small network. | The network writes that band with a texture that moves 0.95 as much as a master's. |

Their one advantage, running when the output rate is below twice the source rate, is now the network's
too (see *When it runs*).

## What it costs

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
written down because each step was measured and several of them went wrong first. `level_check.py`
there measures the written band 500 Hz at a time; `eval_tables.py` turns an evaluation into the tables
below.

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

**Training** ran on a Quadro M2200 with 4 GB that was also driving the display, about fifteen hours
in all. First multi-resolution spectral loss (512 to 4,096 points, the band above 18 kHz counted twice)
with anti-wrapping phase losses, in batches of 12 crops a third of a second long, at 0.15 s a step;
then adversarial training with a multi-period waveform discriminator and a multi-resolution spectral
one, hinge losses and feature matching, in batches of 4, at about a second a step. The spectral phase
makes the level and the envelope right; the adversarial phase makes the texture of the written band
behave like a real one's rather than like its average.

**Told what it is listening to.** That model had seen lossless copies, one in seven, but was never told
which input was which, and a lossless file got the treatment a coded one does. On lossless 48 kHz copies
of the held-out masters it changed a passband it had no reason to touch, 49 dB under the signal, and
wrote the band above 22 kHz 9.3 dB louder than the masters carry it. The network now takes a flag, lossy
or lossless: a learned vector for each block, added with the flag's sign. The corpus's own lossless
copies were too few and too alike to teach it, so three crops in ten are made lossless as they are
drawn, from the master: to 48 kHz (seven in ten of the 96 kHz masters) or 44.1 kHz, through a
Kaiser-windowed sinc whose roll-off (0.87 to 0.99) and length (16 to 64 zero crossings) vary as
mastering resamplers do, half of them requantised to 16 bits with TPDF dither. On those crops a further
loss holds the output to the input across the filter's passband. One lossless crop in ten is flagged
lossy and one coded crop in twenty lossless, so that a wrong setting costs a little rather than
everything.

**Where the source's band ends.** A 500 Hz band-level loss, and more copies through wide filters,
followed when the flag turned out not to be enough; see *What went wrong on the way*.

The schedule was not the one planned, because the problems below were found while it ran: 30,000
spectral and 13,821 adversarial steps on the first input; 2,000 spectral and 716 adversarial steps
adapting to the floored input with the Apple copies added; 14,000 adversarial steps on the screened
336 tracks; 1,500 spectral and 8,000 adversarial steps learning the flag; then 3,500 spectral and
6,000 adversarial steps learning where a source's band ends. 79,537 steps in all.

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

**The written band landed on top of the source's own.** With the flag, the band above 22 kHz on
lossless 48 kHz copies came down from 9.3 to 2.9 dB over the masters, and that was still wrong in a
way no band-wide number shows. Measured 500 Hz at a time, copies made through a long filter whose
passband runs close to the new Nyquist came out of the network 6.6 dB louder than the master between
23 and 24 kHz (median 5.9 dB over 19 songs) and 3.1 dB louder between 22 and 23; at 44.1 kHz, 7.5 dB
between 21 and 22. The first model had done the same at 15.7 dB. Above 24 kHz the level was within a
decibel. The same numbers on songs the network was trained on ruled out chance: it began its band where
most sources end theirs, and a wide filter's source had not ended. Two things let it. The passband loss
had covered only 80 % of the filter's nominal cutoff, 19.0 kHz on a filter measured flat to 22.7; and
the per-bin losses see a kilohertz written 6 dB too loud as forty bins among a thousand, each noisy
with texture. The passband loss now runs to the measured flat edge, half the synthetic copies go
through wide filters (roll-off 0.95 to 0.99, 32 or 64 zero crossings), and a band-level loss compares
the energy of every 500 Hz band above 12 kHz over the crop with the master's, counting the six bands
around the source's Nyquist four times.

## Does it work

### On the songs kept out of training

Every coded copy the corpus holds of the 21 held-out songs: 8 seconds from the middle of each, both
channels, interpolated to the master's rate and put through the network flagged lossy. Log-spectral
distance to the master in dB, before → after (lower is closer); the level of the band above 22 kHz
against the master's; and how much that band moves from frame to frame, as a fraction of how much the
master's does.

| Source | n | 0-16 kHz | 16-22 kHz | above 22 kHz | level above 22 kHz | movement |
| --- | --- | --- | --- | --- | --- | --- |
| MP3, 96 and 128 kbit/s | 12 | 8.84 → **7.69** | 38.18 → **11.35** | 53.49 → **11.33** | -17.0 → **-2.8** dB | 0.97 |
| MP3, 192 to 320 kbit/s, V0, V2 | 18 | 2.84 → **2.83** | 27.98 → **8.83** | 51.63 → **10.46** | -20.1 → **-0.4** dB | 0.94 |
| Apple AAC, 256 kbit/s | 13 | 2.12 → **2.12** | 13.17 → **4.83** | 53.63 → **9.67** | -18.9 → **-0.8** dB | 0.95 |
| Apple AAC, 128 to 192 kbit/s and VBR | 8 | 4.50 → **4.31** | 25.06 → **8.63** | 49.15 → **10.19** | -18.6 → **-1.4** dB | 0.93 |
| FFmpeg and Windows AAC, 128 to 256 kbit/s | 15 | 3.61 → **3.55** | 21.91 → **7.38** | 51.40 → **10.05** | -20.3 → **-0.1** dB | 0.94 |
| Vorbis q3 to q7 | 7 | 4.66 → **4.65** | 24.49 → **8.04** | 51.65 → **9.60** | -19.2 → **-0.7** dB | 0.95 |
| Opus, 128 and 192 kbit/s | 3 | 3.58 → **3.58** | 17.98 → **8.14** | 51.46 → **11.10** | -15.9 → **+4.0** dB | 0.89 |
| **All coded** | 76 | 4.19 → **3.97** | 24.83 → **8.14** | 51.95 → **10.30** | -19.0 → **-0.7** dB | 0.95 |

- **Above 22 kHz** there was nothing, and there is now a band at about the level the masters carry,
  0.7 dB under them on average, that moves about as much as theirs do. Song by song it lands between
  5.3 dB under and 5.2 dB over its master, median 1.7 dB under, with one exception below.
- **Between 16 and 22 kHz**, where the codecs cut, two thirds of the distance to the master goes.
- **Below 16 kHz** the network mostly leaves the recording alone. Where a low bit rate damaged it, MP3 at
  96 and 128 kbit/s, it repairs some of that: 8.84 to 7.69 dB.

Coded sources came through both later fine-tunes within a quarter of a decibel of distance in every
band and family, and the written band came up half a decibel, nearer the masters.

What the adversarial phase was for, measured on the first model: with only the spectral loss the
network wrote the band 3.3 dB under the masters, moving 0.73 as much as theirs, a smooth average of what
could be there. After adversarial training it wrote it 1.2 dB under, moving 0.93 as much, and was
0.4 dB further from the masters between 16 and 22 kHz and 1.0 dB further above 22 kHz for it, which is
what texture costs in a distance that rewards the average.

### Lossless sources

Lossless copies made from the same masters the way a download or a CD is: through a long
Kaiser-windowed sinc (roll-off 0.99, 64 zero crossings) to 48 kHz from the 19 masters at 96 kHz and
to 44.1 kHz from the 2 at 88.2, kept at 24 bits or requantised to 16 with TPDF dither, and read as
lossless, as **Automatic** reads a FLAC. Before → the first model, never told → this one. The passband
change is the energy of whatever the network altered below 90 % of the source's Nyquist, against the
energy there.

| Source | n | 16-22 kHz | level above 22 kHz | passband change |
| --- | --- | --- | --- | --- |
| 44.1 kHz, 24-bit | 2 | 2.88 → 5.63 → **4.00** | -16.4 → +0.7 → **+1.0** dB | -46.7 → **-55.4** dB |
| 44.1 kHz, 16-bit, TPDF | 2 | 2.89 → 5.63 → **4.01** | -16.4 → +0.7 → **+1.0** dB | -46.7 → **-55.4** dB |
| 48 kHz, 24-bit | 19 | 0.00 → 1.02 → **0.38** | -4.1 → +9.3 → **+1.6** dB | -49.2 → **-60.2** dB |
| 48 kHz, 16-bit, TPDF | 19 | 0.32 → 1.09 → **0.60** | -4.1 → +9.3 → **+1.6** dB | -49.2 → **-60.2** dB |
| Corpus lossless copies | 8 | 4.37 → 2.85 → **2.07** | -9.7 → -1.1 → **-1.4** dB | -47.5 → **-60.5** dB |
| 48 kHz, 24-bit, read as lossy | 19 | 0.00 → 1.02 → **0.77** | -4.1 → +9.3 → **+1.7** dB | -49.2 → **-48.2** dB |

Where the written band meets the source's own, by kilohertz: the level against the master, median over
the songs, through the widest filter (roll-off 0.99) and a narrower one (0.91), for the first model, the
model with the flag alone, and this one. The 44.1 kHz rows are 21 songs: the two 88.2 kHz masters, and
the 19 at 96 kHz taken to 88.2 first.

| Copy | Band | First model | Flag alone | This model |
| --- | --- | --- | --- | --- |
| 48 kHz, roll-off 0.99 | 22-23 kHz | +1.9 | +2.3 | **+1.2** dB |
| 48 kHz, roll-off 0.99 | 23-24 kHz | +16.0 | +5.9 | **+2.2** dB |
| 48 kHz, roll-off 0.99 | 24-26 kHz | +2.3 | -0.3 | **+0.3** dB |
| 44.1 kHz, roll-off 0.99 | 21-22 kHz | +15.4 | +5.6 | **+2.5** dB |
| 44.1 kHz, roll-off 0.99 | 22-23 kHz | +3.2 | +0.2 | **+0.9** dB |
| 48 kHz, roll-off 0.91 | 22-23 kHz | -6.3 | -3.4 | **-2.2** dB |
| 44.1 kHz, roll-off 0.91 | 20-21 kHz | -6.7 | -3.5 | **-2.6** dB |

- **The passband stays the source's.** What the network alters below 90 % of Nyquist is 60 dB under
  the signal at 48 kHz and 55 dB at 44.1, from 49 and 47 before it was told. Told lossy by mistake, the
  same copies go back to 48 dB, which is what the flag is worth.
- **Above the source's band, the level is the masters'.** Past Nyquist the median song is within a
  decibel of its master at 48 kHz and within two at 44.1. The 1.6 dB over the whole band above 22 kHz
  at 48 kHz is mostly the seam.
- **The seam is 1 to 2.5 dB high** in the last kilohertz or two under Nyquist, from 15 to 16 dB in the
  first model; behind a gentler filter, whose transition band the network has to restore, it comes back
  2 to 3 dB short, and more behind the shortest.
- **At 44.1 kHz the top of the band is rewritten.** A copy through the widest filter moves from 2.88 to
  4.00 dB from its master between 16 and 22 kHz: the transition band is filled in, at the right level
  and not bin for bin. Through FFmpeg's SoX resampler, whose band ends earlier, the same kind of file
  goes the other way, 13.04 to 3.90 dB (next table).

### Through the player, on real files

The measurement that matters for listening: six held-out masters decoded afresh, 12 seconds of each made
into the files people have (an Apple AAC `.m4a` at 256 kbit/s, an MP3 at 320 and a 16-bit FLAC with
dither, all through FFmpeg's SoX resampler), played by `fuplayer-cli` into a WAV at twice the rate with
the upscaler on and **Source type** automatic, and scored against the master. The 96 kHz masters were
played both at 48 kHz and, resampled, at 44.1 kHz.

| File | Rate | 16-22 kHz | above 22 kHz | level above 22 kHz | movement |
| --- | --- | --- | --- | --- | --- |
| Apple AAC 256 `.m4a` | 44.1 kHz | 19.23 → **5.47** | 53.19 → **14.42** | -24.4 → **+2.2** dB | 1.05 |
| MP3 320 | 44.1 kHz | 34.25 → **8.27** | 53.52 → **16.02** | -24.2 → **+2.4** dB | 1.07 |
| FLAC, 16-bit | 44.1 kHz | 13.04 → **3.90** | 52.82 → **14.49** | -24.2 → **+1.9** dB | 1.05 |
| Apple AAC 256 `.m4a` | 48 kHz | 9.23 → **5.43** | 48.44 → **18.23** | -24.2 → **+5.5** dB | 1.05 |
| MP3 320 | 48 kHz | 32.36 → **7.92** | 48.22 → **18.24** | -24.8 → **+4.8** dB | 1.05 |
| FLAC, 16-bit | 48 kHz | 0.28 → **1.56** | 45.95 → **17.68** | -6.1 → **+5.0** dB | 1.04 |

Every row is pulled up by one song, the exception described next, and the 48 kHz rows by more, since
only four of the six masters are at 96 kHz. Without it, the written band sits between 0.5 and 2.1 dB
under the masters in every row, and the 48 kHz FLAC copies are 0.23 dB from their masters between 16
and 22 kHz, against 0.13 dB before: a lossless file's passband is left alone.

### Where it is wrong

- **A master with nothing above 22 kHz gets a band anyway.** One held-out song's master falls away
  steeply above 20 kHz and carries almost nothing past 22. The network cannot tell that from the
  44.1 or 48 kHz file, which looks like every other, and writes the band a master of that kind usually
  has: 21 to 24 dB more than this one had. Its lossless 48 kHz copy also moved from 0.75 to 5.57 dB away
  from the master between 16 and 22 kHz, where the master has little more than the file's dither, which
  the network is not held to.
- **The seam is not flat.** Through the widest anti-alias filters the last kilohertz under a lossless
  source's Nyquist comes out 2 to 2.5 dB high on the median song; behind gentler ones, 2 to 3 dB low.
- **It predicts texture, not notes.** Tones and synthesiser lines that a master carries above 22 kHz
  cannot be inferred from below it; the band it writes follows the transients and the spectral slope
  of the music, not its pitch.
- **Opus copies get a band about 4 dB too loud**, on the three held out. Opus is the smallest share of
  the corpus, and only ever at 48 kHz.
- **None of it was listened for.** These are spectral measurements against masters. What a band above
  22 kHz sounds like, through a given amplifier and tweeter, is not something this page can claim.
