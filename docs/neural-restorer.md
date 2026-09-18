# Neural restorer

The neural restorer takes a coded stereo stream at 44.1 or 48 kHz (Opus, AAC, MP3 or Vorbis at 96 to 160 kbit/s,
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
  writing.
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

**The corpus.** 924 songs from the maintainer's library: the upscaler's 336 screened high-resolution masters,
brought down with SoX's resampler, and 588 CD-rate ALAC files not already among them. Classical music and
instrumentals were left out and Japanese pop and anime weighted three times, as for the upscaler. Fifty seconds from
the middle of each. Each song got one delivery rate:

- **48 kHz (511 songs):** what a browser or a streaming application hands Windows. Opus at 96, 112, 128 and
  160 kbit/s; Apple, FFmpeg and Windows AAC coded at 44.1 kHz and resampled into the mixer, which is Apple Music in a
  browser; AAC at 48 kHz; and a little Vorbis.
- **44.1 kHz (413 songs):** files and applications that keep the stream's own rate. AAC from Apple (qaac), FFmpeg and
  Windows Media Foundation at 96, 128 and 160 kbit/s; MP3 at 128 and 160; Vorbis q2 and q5.

Each song has three coded copies, decoded and lined up with the reference, 32 GB in all at float16. 55 songs are held
out by title, the upscaler's split, so no song either model was measured on was trained on by the other.

**Training.** Crops of 0.68 s, eight a step, one in ten the master against itself so the network learns to leave
clean audio alone. First 20,000 steps of spectral and perceptual losses: multi-resolution STFT on left and right and
on the side channel, the masked loss, the level of every 500 Hz band from 4 kHz, a phase loss, and an identity loss
on the lossless crops. Then 12,000 steps with the upscaler's multi-period and multi-resolution spectral
discriminators added. That took 4 hours 45 minutes on a 4 GB Quadro M2200 that was also driving the display.

**Fine-tune.** On the held-out songs the first model laid a band on top of Vorbis q5, which reaches 20 kHz on its
own: 3.0 dB too much above 16 kHz on average, and up to 20 dB on quiet passages. It had also never met a stream above
160 kbit/s, while Apple Music plays AAC at 256 kbit/s in a browser. One more copy of every training song (Vorbis q4
to q9, and AAC, Opus and MP3 at 192 to 320 kbit/s) and 3,000 + 3,000 steps from the best checkpoint, 1 hour 16
minutes, took the Vorbis q5 overshoot to 1.7 dB. They also halved the distance above 12 kHz on 192 kbit/s AAC, and
took the stretches written more than 3 dB too loud above 16 kHz from 90 to 54 of 2,464. The shipped model is the
fine-tune's last adversarial checkpoint, 36,000 steps in all.

**What went wrong on the way.**
- The first launch's output path was mangled by the shell that started it, and the run was restarted.
- A training process killed in the middle of a GPU kernel left the display driver in error, and the machine stopped
  with a bugcheck five minutes later. Runs now end cleanly when a file named `STOP` appears in the run folder.
- The library drive was not mounted after the reboot, so the extra copies were made from the stored references
  instead of the original files.
- The first export of the network's streaming step was a graph of 233 operators, and dispatching them cost 2.7 ms a
  call whatever the frames. The file now holds only the network's core, the player does the ends, and the
  convolutions are exported in the two-dimensional form ONNX Runtime runs fastest.

## Does it work

Measured on the 55 held-out songs: four five-second stretches of each coded copy, the first half second and last
quarter of each left out so every frame measured had its full context; input (the decoded stream) against output,
both compared with the master.

- **LSD** is the log-spectral distance per frame over the band.
- **Level above 16 kHz** is the band's energy over the stretch against the master's.
- **Side** is the mean distance of the side channel's 500 Hz band levels from the master's, from 4 kHz up.
- **NMR** is the noise-to-mask ratio formed as PEAQ (ITU-R BS.1387) forms it: noise over threshold in each Bark band,
  averaged over the bands in each frame. Below 0 dB is inaudible on average.
- **Disturbed frames** are the share of frames with any band 1.5 dB or more above its threshold.

At the rates it was made for, 96 to 160 kbit/s:

| Codec | Stretches | LSD 4–12 kHz, dB | LSD above 12 kHz, dB | Level above 16 kHz, dB | Side, dB | NMR, dB | Disturbed frames, % |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Opus | 184 | 4.23 → 4.24 | 12.88 → 9.45 | −0.14 → −0.14 | 4.29 → 2.24 | −13.54 → −13.54 | 0.00 → 0.00 |
| AAC, Apple | 204 | 4.66 → 4.60 | 24.56 → 9.26 | −11.30 → −1.31 | 7.50 → 2.11 | −13.53 → −13.40 | 0.02 → 0.02 |
| AAC, FFmpeg | 116 | 5.61 → 5.50 | 24.01 → 9.59 | −4.68 → −1.03 | 6.80 → 2.34 | −10.47 → −10.50 | 3.55 → 3.19 |
| AAC, Windows | 76 | 5.06 → 4.89 | 29.08 → 9.68 | −22.79 → −2.01 | 8.81 → 2.21 | −13.49 → −13.22 | 0.19 → 0.21 |
| MP3 | 36 | 4.74 → 4.71 | 27.67 → 8.77 | −7.92 → −1.91 | 7.67 → 1.88 | −12.98 → −13.13 | 0.01 → 0.01 |
| Vorbis | 264 | 4.53 → 4.44 | 16.54 → 9.24 | −0.70 → +0.91 | 4.02 → 2.23 | −13.72 → −13.73 | 0.08 → 0.06 |
| **All** | 880 | 4.69 → 4.63 | 20.16 → 9.36 | −5.77 → −0.45 | 5.81 → 2.20 | −13.16 → −13.12 | 0.51 → 0.46 |

What moves is the band above the codec's low-pass and the stereo. AAC at 96 kbit/s from Windows Media Foundation
keeps nothing above 16 kHz (−33 dB against the master); restored, it is 3.7 dB under. The side channel's distance
from the master falls from 5.8 to 2.2 dB. Below 12 kHz little changes: the weakest encoders gain most (4 to 12 kHz
from FFmpeg's AAC at 96 kbit/s: 7.27 → 6.85 dB; Windows AAC at 96: 7.67 → 6.74), and Opus, which keeps every band's
energy, not at all.

The noise-to-mask ratio hardly moves, and that is a finding rather than a flaw of the table. By the standard masking
model, the coding noise at these rates is already under threshold on average (−13 dB), which is what the encoders
are built to achieve. The band above 16 kHz, where the restorer does most, sits near the absolute threshold of
hearing at ordinary listening levels. The frames with an audible band are few, and fall where there are any: FFmpeg's
AAC at 96 kbit/s, 11.2 → 10.2 %. Whether the rewritten band and the wider stereo are heard is a matter for a listening
test, which has not been done.

Near-transparent streams are left nearly as they are, which is what a restorer in front of Apple Music's 256 kbit/s
AAC has to do:

| Stream | Stretches | LSD 4–12 kHz, dB | LSD above 12 kHz, dB | Level above 16 kHz, dB | Side, dB |
| --- | --- | --- | --- | --- | --- |
| AAC 256, Apple | 220 | 2.16 → 2.16 | 6.87 → 6.75 | −0.29 → −0.11 | 0.81 → 0.84 |
| AAC 256, FFmpeg | 220 | 3.20 → 3.20 | 6.59 → 6.63 | −0.32 → −0.22 | 0.79 → 0.84 |
| AAC 192, Apple | 220 | 2.99 → 3.01 | 15.98 → 8.91 | −0.94 → −0.37 | 3.46 → 1.57 |
| Opus 192 | 132 | 2.81 → 2.83 | 12.31 → 8.56 | −0.37 → −0.22 | 2.47 → 1.54 |
| Opus 256 | 132 | 1.83 → 1.87 | 11.98 → 8.19 | −0.38 → −0.19 | 2.23 → 1.50 |
| MP3 320 | 220 | 1.68 → 1.71 | 13.21 → 9.41 | −0.19 → +0.26 | 2.10 → 1.53 |
| Vorbis q6 | 220 | 3.68 → 3.66 | 13.91 → 9.71 | −0.34 → +0.59 | 1.60 → 1.79 |
| Vorbis q9 | 220 | 1.60 → 1.60 | 8.89 → 8.34 | −0.27 → −0.15 | 0.72 → 0.76 |

Lossless copies of the same songs, the master against itself: changed by −71.8 dB on average (median −76.9 dB),
three of 220 stretches by more than −40 dB, the worst by −29.8 dB.

## Where it is wrong

- **It has not been listened to in a controlled test.** Everything on this page is measurement.
- **Vorbis q5 at 44.1 kHz**, which is what Spotify's desktop client plays at 160 kbit/s: the band above 16 kHz comes
  out 1.7 dB louder than the master on average, down from 3.0 before the fine-tune. It is loudest on quiet passages.
  Vorbis q6 is 0.6 dB over; resampled to 48 kHz on the way into the mixer, as a browser plays it, Vorbis q5 is right.
- **Masters with little above 16 kHz get some written anyway.** The worst lossless stretches changed by −30 dB, and a
  coded copy of such a song gets a band its lossless version never had. Under Automatic the restorer never runs on a
  lossless file.
- **The weakest AAC encoders come out a little dull.** Their band above 16 kHz ends 1.5 to 3.7 dB under the master
  (Windows AAC at 96 and 128 kbit/s, FFmpeg AAC at 96).
- **One person's music.** 924 songs, Japanese pop and anime weighted three times, no classical, no instrumentals.
- **Stereo and mono at 44.1 and 48 kHz only**, and the delay is fixed at the number above.
