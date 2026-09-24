# Neural restorer

The neural restorer takes a coded stereo stream at 44.1 or 48 kHz (Opus, AAC, MP3 or Vorbis, 96 kbit/s upwards,
from a file or captured from a browser or a music client) back towards the lossless recording at the same rate. It
rewrites the band above the codec's low-pass, gives back the stereo width the codec collapsed in the upper bands,
and fills the holes the weaker encoders leave, with a fixed delay of 91 ms at 48 kHz and 99 ms at 44.1 kHz. It is
the first of the two networks under **Lossy repair** in DSP Studio, beside the [neural upscaler](neural-upscaler.md),
and it is off until asked for.

Read this first: **it recovers nothing.** A 128 kbit/s stream does not contain the band its encoder discarded, or
the stereo it reduced to a level ratio. What the network writes there is its estimate of what the lossless recording
usually carries at that point, learned from lossless recordings and coded copies of them. It is measured against the
masters it came from below, and **Output delta** plays what it changed on its own.

The release carries the model this page measures, `neural-restorer.onnx` and `neural-restorer.json`, in the `models`
folder next to `FUPlayer.exe`. To use another, train it with the scripts in
[training/neural-restorer](../training/neural-restorer/README.md) and put its two files in that folder or in
`%APPDATA%\FUPlayer\models`. A restorer's file name ends in `restorer.onnx`, which is how the player tells it from an
upscaler; the newest of each kind is used unless the settings name one.

## Settings

| Setting | Values | Effect | Command line |
| --- | --- | --- | --- |
| Neural restorer | on, off | Runs the network on coded 44.1 and 48 kHz stereo and mono sources. | `--neural-restore`, `--restorer <file>` |
| Source type | Automatic, Lossy, Lossless | Shared with the upscaler. Automatic restores lossy codecs and application captures and leaves PCM, FLAC, ALAC and the other lossless formats alone; Lossy restores everything; Lossless nothing. | `--source-type auto\|lossy\|lossless` |
| Output delta | on, off | Shared with the upscaler. Outputs what the networks changed: their output minus the latency-aligned input. For monitoring. | `--output-delta` |

The peak limiter runs whenever the restorer does: the band it writes adds energy, and a loud master can cross full
scale with it.

## When it runs

- The source is PCM at 44.1 or 48 kHz with one or two channels: a file, or a capture from another application. A mono
  source goes through as both channels. Other rates and more channels play without it, and Now playing says why.
- Under Automatic it is idle for a lossless source.
- With the upscaler on too, the restorer runs first, at the source rate, and the upscaler is told the source is
  lossless: it leaves the passband alone and writes only the band above the source's Nyquist frequency. At an
  output below twice the source rate the upscaler then has nothing to do and idles, and the restorer keeps running.

## The chain

| # | Stage | Rate | What it does |
| --- | --- | --- | --- |
| 1 | Decoder, ReplayGain | fs | Decoded PCM, or a capture. |
| 2 | Source meters, bandwidth readout | fs | Measurement only, of the source as it arrived. |
| 3 | STFT | fs | 2,048-point periodic Hann, hop 256, both channels, the first frame centred on the first sample. |
| 4 | Mid and side | frames | M = (L + R)/√2, S = (L − R)/√2. |
| 5 | Features | frames | Log magnitudes of mid and side, floored together 85 dB below the frame's loudest bin. |
| 6 | Network | frames | Four frames a call, with the state the last call left; each answer is for the frame six hops back. |
| 7 | Masks | frames | Per bin of mid and of side: w·e^g·e^(iθ)·X + (1 − w)·e^m·e^(iφ), w = sigmoid(u). |
| 8 | Left and right, overlap-add | fs | Back to L and R, windowed overlap-add over the window's envelope. Fixed delay of 4,351 samples. |
| 9 | Output delta (optional) | fs | Subtracts the source delayed by the same 4,351 samples; with the upscaler on, at its rate after both networks. |
| 10 | Everything after | | The upscaler if it is on, the filter, volume and limiter, dither or modulation. |

The file holds only stage 6, the network between its features and its heads; the player does the rest. On the test
fixture the player's stages 3 to 8 match PyTorch's whole-signal answer to better than −60 dB from the first sample,
at one, two and five frames a call. With the shipped model, on held-out songs coded with Opus 128, Apple AAC 128
through the 48 kHz mixer and FFmpeg AAC 96, the player's output is within −126 to −132 dB of PyTorch's.

## Why it is built this way

The design follows what the two codecs it mostly meets actually do, as their specifications describe them
(RFC 6716 for Opus, ISO/IEC 13818-7 and 14496-3 for AAC).

- **Frequency resolution.** AAC codes a long block as a 1,024-line MDCT of 2,048 samples; Opus's CELT layer codes
  20 ms frames, 960 samples at 48 kHz. A 2,048-point transform sees what they did at their own resolution, and a hop
  of 256 keeps the time resolution the short blocks and transients need.
- **Two kinds of damage, two tools per bin.** AAC quantises each spectral line with a dead zone (a ¾-power quantiser
  in 1.5 dB scale-factor steps), so weak lines round to zero: holes that open and close from frame to frame, and
  nothing at all above the encoder's low-pass, 15 to 17 kHz at 96 to 128 kbit/s. Opus keeps each band's energy
  exactly and codes only its shape, with few pulses when bits are short, a folded copy of a lower band when there are
  none, and noise injected where a band would collapse. So each bin gets a complex mask, which reshapes what is
  there without inventing a level, and a component of its own, magnitude and phase, for what is missing, with a
  learned crossover between them.
- **Mid and side.** Both code stereo as mid and side and, at these rates, reduce the side of the upper bands to a
  level ratio (intensity stereo). The network reads and writes mid and side, one network for both, told which it is
  writing, and both are held to the master's band levels. Holding left and right to them instead is not the same
  thing and is what the model before this one was fitted to: it satisfied the level it was asked for by writing
  the band it has to invent into the side, which reads as a band in each channel and is missing from the mix,
  where music keeps most of its top octave. A spectrogram of the two channels mixed showed it as a dark stripe
  from the codec's cutoff to the top of the band.
- **Pre-echo and look-ahead.** A transient's quantisation noise spreads over the whole block before it. The one layer
  that looks ahead sees six hops, 32 ms at 48 kHz; everything else is causal, which is what keeps the delay under
  100 ms.
- **What a listener can hear.** A codec places its noise under the masking threshold its psychoacoustic model
  computes. The masked loss measures the network's error against the threshold the master itself sets (Bark bands,
  Schroeder's spreading function, a 6 to 18 dB offset between noise-like and tonal maskers, and the absolute
  threshold of hearing), so the network is pushed hardest where the difference would be audible.
- **The rate.** A bin is 21.5 Hz wide at 44.1 kHz and 23.4 Hz at 48, and every codec's band edges sit at
  frequencies, not bins, so the network is told which rate it is hearing.

## What it costs

- **Delay: 4,351 samples**, 91 ms at 48 kHz and 99 ms at 44.1 kHz, on any machine. It is the transform (2,048
  samples), the look-ahead (six hops) and the frames that wait for their call (three hops), not the processor: a
  faster computer does not shorten it. The stage can hand the network one frame a call (3,583 samples, 75 ms at
  48 kHz) or two (3,839), at roughly half and three quarters of the throughput; the player uses four.
- **Processor:** 5.1 million parameters, one thread. On the machine that trained it, a four-core i7-7820HQ laptop,
  with the graphics card training at the same time, the network alone ran 8.7 times faster than real time at 44.1
  kHz, and the whole pipeline with the restorer on, from a 44.1 kHz file to 88.2 kHz output, 4.7 times.
- **Memory:** the model file is 19.4 MB.

## How the reference model was made

**The corpus.** 634 high-resolution masters from the maintainer's library, at 88.2, 96 or 192 kHz, of every genre.
They are what survived a screen of all 890 high-resolution files in it: each was decoded at its own rate, the loud
half of its frames averaged, and two numbers taken, the frequency where its spectrum drops for good and the slope
from 8-16 kHz to 24-32 kHz. 21 files were dropped for a band that ends below 22.05 kHz, which is a CD-rate master
resampled and sold at 96 kHz, and 235 for a spectrum that hardly falls at all, which is a transfer from a 1-bit
source: their 20 to 22 kHz is the modulator's noise, and a network taught from it writes hiss. What is left is
61 hours of music that carries sound to 22.05 kHz because it was recorded that way.

Every song is prepared at both delivery rates, 90 seconds from the middle of each:

- **44.1 kHz**, what a file plays at: AAC from Apple (qaac), FFmpeg and Windows Media Foundation, MP3, Vorbis and
  Opus, 96 to 320 kbit/s.
- **48 kHz**, what a browser or a music client hands Windows: Opus at its own rate, and AAC, MP3 and Vorbis coded at
  44.1 kHz and resampled up after decoding, which is Apple Music in a browser.

A fifth of the copies are made at 192 kbit/s and above, where a codec is nearly transparent and the network has to
leave what it finds alone. Four copies a song at each rate: 1,268 references, 5,072 coded copies, 95 GB at float16.
122 of the references, 61 songs, are held out by title, the upscaler's split.

**The passband the references come down with** is the point of all this. A reference has to carry music up to the
delivery rate's own Nyquist frequency, so the resample that makes it uses soxr at 28-bit precision with its passband
at 0.998 of Nyquist: measured on noise, flat to 23.2 kHz at 48 kHz, with the aliases of a 30 kHz tone 153 dB down.
An ordinary passband stops at 0.91 of Nyquist, and a corpus built with one teaches a network that the band ends
there.

**Training.** Crops of 0.68 s, eight a step, one in ten the master against itself so the network learns to leave
clean audio alone. 32,000 steps of spectral and perceptual losses — multi-resolution STFT on left and right and on
mid and side, the masked loss, the level of every 500 Hz band from 4 kHz of mid and of side, a phase loss, and an
identity loss on the lossless crops — and then 16,000 with the upscaler's multi-period and multi-resolution
spectral discriminators.

The bands a codec emptied count six times over in the band-level term, measured per crop from the master against
the copy rather than from a fixed frequency, because an encoder's low-pass moves with the bit rate and with the
music. Averaged over every band from 4 kHz, the handful that have to be invented are a rounding error: a network
can leave them twenty-five decibels short and pay almost nothing for it.

A line term holds each bin's level over the crop against its neighbours', and the same in the master. The network
writes every bin through its own row of the head, and nothing else ties one bin to the next, so a row that came out
a little low draws a dark horizontal line across every track at that bin; one bin 10 dB low moves its 500 Hz band by
0.4 dB, which the band-level term cannot see. The master's fine structure moves with the music from crop to crop and
comes to nothing on average, and a row that is low on every input does not, so that is all the term can teach.

From scratch, 7 hours 17 minutes on a 4 GB Quadro M2200 that was also driving the display; then 4 hours 12 for the
mid, 4,000 spectral steps and 8,000 adversarial ones at a fifth of the learning rate, once the band-level terms were
moved from left and right onto mid and side; then 3 hours 9 for the lines, 3,000 and 5,000 more the same way with
the line term added. The shipped model is the last of those, 68,000 steps in all.

**Why from scratch.** The model before it wrote nothing at all in three bins, and a spectrogram showed them as
narrow dead lines across every track. They were in the weights and not in the music: played something brickwalled
far below them, with digital silence above, bins 869, 881 and 894 came out 12 to 14 dB under their neighbours at
44.1 kHz **and** at 48 kHz, the same bin indices at both rates. The first corpus is where they came from. Its
44.1 kHz references were brought down with an ordinary passband, so they stopped at 20.1 kHz; its 48 kHz ones were
mostly CD masters resampled up, so they stopped at 20.9. Fitted to references that disagreed about where the band
ends, the network settled it by writing nothing in a few bins near where they disagreed. A fine-tune inherits
weights, so it inherits that; only a new fit on a corpus that agrees with itself removes it.

**What went wrong on the way.**
- A training process killed in the middle of a GPU kernel left the display driver in error, and the machine stopped
  with a bugcheck five minutes later. Runs now end cleanly when a file named `STOP` appears in the run folder.
- The first hours of this run went at a quarter speed. A crop is 128 KB of one file and another of its reference,
  drawn at random from 95 GB, and drawing them on the training thread left it waiting three quarters of each step.
  Six threads draw ahead of it now.
- Its first snapshots carried no validation scores, so the model the player describes had nothing to say about
  itself. Snapshots carry the last validation's scores now.

## Does it work

Measured on the 61 held-out songs, both rates, 1,952 stretches: four five-second stretches of each coded copy, the
first half second and last quarter of each left out so every frame measured had its full context; input (the decoded
stream) against output, both compared with the master.

- **LSD** is the log-spectral distance per frame over the band.
- **Level above 16 kHz** is the band's energy over the stretch against the master's: over the two channels, and over
  their mix, which is where a band written into the side alone goes missing.
- **Mid** and **side** are the mean distance of (L ± R)/√2's 500 Hz band levels from the master's, from 4 kHz up.
- **NMR** is the noise-to-mask ratio formed as PEAQ (ITU-R BS.1387) forms it: noise over threshold in each Bark band,
  averaged over the bands in each frame. Below 0 dB is inaudible on average.
- **Disturbed frames** are the share of frames with any band 1.5 dB or more above its threshold.

| Codec | Stretches | LSD 4–12 kHz, dB | LSD above 12 kHz, dB | Level above 16 kHz, dB | Same, mixed, dB | Mid, dB | Side, dB | NMR, dB | Disturbed frames, % |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Opus | 448 | 3.94 → 3.97 | 15.38 → 7.32 | −0.81 → −0.25 | −0.26 → −0.14 | 3.07 → 0.69 | 3.95 → 0.96 | −17.00 → −16.87 | 0.00 → 0.24 |
| AAC, Apple | 644 | 4.25 → 4.09 | 19.63 → 7.56 | −7.46 → −1.26 | −7.57 → −1.28 | 4.68 → 0.90 | 10.21 → 1.20 | −17.86 → −17.60 | 0.01 → 0.02 |
| AAC, FFmpeg | 396 | 6.13 → 5.74 | 19.60 → 7.71 | −4.22 → −0.48 | −4.44 → −0.61 | 5.12 → 0.97 | 5.27 → 1.11 | −13.21 → −13.21 | 2.26 → 2.15 |
| AAC, Windows | 168 | 7.20 → 5.80 | 23.60 → 8.59 | −14.52 → −2.17 | −14.94 → −2.31 | 6.20 → 1.27 | 6.98 → 1.41 | −14.86 → −14.52 | 0.14 → 0.17 |
| MP3 | 140 | 4.82 → 4.69 | 23.10 → 8.61 | −7.91 → −1.20 | −7.70 → −1.19 | 5.94 → 1.06 | 6.44 → 1.42 | −15.83 → −15.65 | 0.02 → 0.05 |
| Vorbis | 156 | 6.13 → 5.44 | 18.76 → 8.17 | −2.60 → −0.44 | −1.95 → −0.18 | 3.30 → 1.11 | 5.15 → 1.68 | −14.83 → −14.47 | 0.07 → 1.22 |
| **All** | 1,952 | 5.00 → 4.69 | 19.17 → 7.75 | −5.53 → −0.88 | −5.45 → −0.88 | 4.51 → 0.93 | 6.82 → 1.20 | −16.07 → −15.89 | 0.48 → 0.61 |

**Against the model in release 0.2.1**, measured the same way on the same stretches: log-spectral distance above
12 kHz 11.26 → 7.75 dB, mid 2.04 → 0.93 dB and side 3.00 → 1.20 dB from the master's band levels, the mixed level above
16 kHz −0.90 → −0.88 dB, and fourteen lines at 48 kHz, 2 to 5 dB deep, among them bins 869, 881 and 894, down to none.
A lossless file forced through it changes a little more, −65.8 → −62.0 dB on average, and frames with a band over the
masking threshold go from 0.45 to 0.61 %.

**Against the models between the two**, which were never released. The one just before this drew the lines
above and was otherwise within a hair of this one: 7.76 dB above 12 kHz, the mid at 0.88 dB and the side at 1.16, the
mixed level above 16 kHz at −0.46 dB. The line term costs a quarter of a decibel of that level, because it trims the
bins that stood above their neighbours as well as the ones below: stretch by stretch the median moves from −0.21 to
−0.44 dB, nine stretches in ten by less than 1.2 dB, and the share more than 10 dB short stays at 0.3 %.

The one before that put nothing in the middle. It scored 7.71 dB above 12 kHz, −1.07 dB on the level over the two
channels and 1.63 on the side, which is why it passed; it left the mixed level above 16 kHz at −5.38 dB where the
coded input was −5.45, and the mid's band distance at 4.15 dB where the input was 4.51. Against it, this model is
more than 5 dB short of the master's mixed level above 16 kHz on 3.1 % of stretches where that one was on 31 %.

**No lines.** A line is a bin the network writes low on every input, so it is measured on many: 30 held-out
copies at each rate whose codec emptied the top of the band, each bin's level over the stretch against the median of
the 21 around it, averaged over the copies, where the music's own fine structure comes to nothing
(`restorer.bin_lines`). At 44.1 kHz this model has no bin 2 dB or more low on average below 21.9 kHz; at 48 kHz two,
at 18.54 and 18.66 kHz, average −2.1 dB, and they are low on one copy in fifteen and one in thirty, which is two
songs' own structure rather than a line. The three bins under Nyquist are low at both rates, 3 to 15 dB, and they are the references' own: those were
resampled with the passband ending at 0.998 of Nyquist, and the network is matching them.

The model before this one had forty-odd lines at each rate, 2 to 7 dB deep, at the same bin indices at 44.1 and at
48 kHz — 818, 819, 841, 856, 869, 881, 888, 913 and others — which is how a line in the weights shows itself: bin 913
is 19.66 kHz at one rate and 21.40 at the other. Pushing the mid up by twenty-five decibels through terms that only
see 500 Hz bands moved each bin's row by a different amount. Its release checks passed: the test then was one file
brickwalled at 9 kHz and a bin 12 dB under its neighbours, which catches a dead bin and passes a line. On
`setsuna_trip.ogg`, a Vorbis stream cut at 18 kHz, bins between 18 and 23.8 kHz more than 3 dB under their
neighbours over the track went from 36 to none, and more than 5 dB from 11 to none.

Lossless copies of the same songs, the master against itself: changed by −62.0 dB on average, the worst stretch by
−33.0 dB. Under Automatic the restorer never runs on a lossless file.

## Where it is wrong

- **It has not been listened to in a controlled test.** Everything on this page is measurement.
- **Vorbis is the codec it disturbs most**: 1.22 % of its frames have a band over the masking threshold where the
  input had 0.07 %, the worst of any codec here, though its level lands within 0.5 dB of the master.
- **Masters with little above 16 kHz get some written anyway.** The worst lossless stretch changed by −33.0 dB, and
  a coded copy of such a song gets a band its lossless version never had.
- **It writes a band on every coded stream, including those whose master never had one.** Fitting to
  high-resolution references is what takes the band to Nyquist, and the cost is the other side of it: a song
  mastered with nothing above 20 kHz gets something written there anyway, and nothing in a stream cut off at 17 kHz
  says which kind of master it came from. Output delta plays exactly what was added.
- **The top three bins are the references' roll-off**, 3 to 15 dB under their neighbours at both rates, because the
  references were resampled with the passband ending at 0.998 of Nyquist. On a spectrogram that is the last 50 Hz
  under 22.05 or 24 kHz.
- **The band it writes is its own texture, not the master's.** Held to the master's level in every 500 Hz band of
  mid and of side, it lands within about a decibel; bin by bin above 12 kHz it is no closer than the model before
  it. Nothing can be closer than that: the bins a codec threw away are not recoverable from what is left.
- **The weakest encoders still come out a little dull.** In the mix, their band above 16 kHz ends 1.5 to 4.2 dB
  under the master: Windows AAC at 128 through the 48 kHz mixer −4.2, Apple AAC at 96 −2.5, MP3 at 128 −1.5.
- **One person's music.** 634 high-resolution albums, every genre, a third of them classical, each counting the
  same.
- **Stereo and mono at 44.1 and 48 kHz only**, and the delay is fixed at the number above.
