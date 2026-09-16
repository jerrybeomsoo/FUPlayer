# Lossy repair

Four stages for material that has been through a perceptual codec, or that simply has less band than
the output does. They live in the DSP studio under **Lossy repair**, and every one of them is off
until you ask for it.

The **neural upscaler**, which takes 44.1 and 48 kHz sources to 88.2 and 96 kHz and stands in for these
stages wherever it runs, has its own page: [neural-upscaler.md](neural-upscaler.md).

Read this first: **none of them recovers what was discarded.** The bits are gone. Three of the four
put back something plausible where there is a hole, and the fourth moves what survived closer to
where it started. None of it is the master.

What can be said, because it was measured on tracks the model had never been shown, is in
[Does it work](#does-it-work). The short version: the patch does most of the work above a codec's
cutoff, the network is worth two thirds of the error that is left, and on material with no cutoff at
all it is worth a little and sometimes costs a little.

## How the cutoff is found

An encoder throws away everything above a cutoff and leaves a wall there: about 15 kHz for AAC at
96 kbit/s, 19 kHz at 192 kbit/s, 16 kHz for MP3 at 128 kbit/s. Material that never went through one
carries energy, if only dither, up to its own Nyquist rate.

FUPlayer measures this while the track plays, by averaging Blackman-Harris transforms of the loud
parts and looking for the steepest fall across a kilohertz. The cutoff is then defined as the point
where the level has dropped 6 dB from the plateau just below that edge.

It looks for the edge rather than for silence above it, and that distinction is not academic. A real
encoder does not zero the band it discards; it leaves its own quantisation noise there, around 68 dB
down. An earlier version of this detector looked for silence, and so read genuinely coded audio as
full band and did nothing at all.

If the verdict is full band, nothing is **rebuilt** — there is no missing band to invent. The levels
are still corrected, and that is not a technicality: AAC at 256 kbit/s, which is what most streaming
services deliver, band-limits nothing at all. FUPlayer used to switch the whole of lossy repair off
on such material and hand the signal back untouched.

The fill starts at the cutoff, where the fall ends, and not at the start of the roll-off. A roll-off
still carries the recording, and a patch laid over it replaces music with a copy of an octave down.
Measured against the lossless masters, filling from the start of the roll-off cost 4 dB at 17 to
18 kHz on MP3 at 256 kbit/s and 7 dB at 18 to 19 kHz on MP3 at 320. The dip that filling from the
cutoff used to leave between the roll-off and the rebuilt band is closed by the level correction
instead, which is what the level correction is for.

It measures the signal, not the file. Anything steeply low-passed reads the same way.

### The average hides what the frames show

The verdict comes from an average of many transforms, and an average is exactly the wrong instrument
for an encoder that empties a band in some frames and not others. Measured on fourteen tracks from
this corpus, the AAC encoder Windows ships, at 256 kbit/s:

| | 16-17 kHz | 17-18 kHz | 18-19 kHz | 19-20 kHz | 20-21 kHz |
| --- | --- | --- | --- | --- | --- |
| long-term average, dB below the master | 0.5 | 1.5 | 3.2 | 5.7 | 8.9 |
| per frame, rms dB | 5.3 | 18.6 | 33.4 | 42.6 | 48.1 |
| frames with the band 10 dB down or worse | 4% | 26% | 46% | 62% | 74% |

Averaged, the spectrum runs to the end of the band. Frame by frame, three quarters of them have
nothing above 20 kHz. Both statements are true and only the second one describes what a listener
hears.

## The four stages

**Artifact reduction** works on the band the codec kept. When an encoder runs short of bits it holds a
high band in one frame and drops it in the next, and that switching is heard as a watery chirping.

This stage is the fixed version of that idea: a limit on how far a bin may move between transforms.
It is off by default. What supports the *idea* is the table above, where three quarters of frames
lose everything above 20 kHz at a bit rate whose long-term average looks intact. What does not
support this particular *stage* is that a fixed limit cannot tell a codec dropping a band from a
cymbal decaying, and it damps both. With a model installed this stage is not used at all: the
network's answer covers the kept band as well, frame by frame, which is the same job done with some
idea of what the music is doing.

**Harmonic rebuilding** works above the cutoff. The top octave below it is copied upwards in patches,
which is how spectral band replication has worked since MPEG-4 HE-AAC. The fine structure comes from
real content an octave down, so it moves with the music instead of sitting there as a static hiss, and
each patch is rotated by the frequency difference times the hop so the patches do not beat against one
another.

**Generative prediction** decides how loud everything should be. With it off, the rebuilt band follows
a fixed 12 dB per octave slope and the artefact damping is a fixed limit on how fast a bin may move.
With it on, a trained network decides both. **Prediction strength** multiplies its answer: 1 is what
it was fitted to say, and anything above that is exaggeration rather than an answer to a measurement.

**Ultrasonic rebuilding** is a fourth stage and a different job. It synthesises the band above the
*source's* own Nyquist rate, when the output runs faster than the file does: 22 to 40 kHz for a CD-rate
recording played at 96 kHz. It runs after the conversion, which is the only place in the chain where
those bins exist, and it writes nothing below the source's Nyquist rate.

It is invention and not recovery, and the difference matters. A 44.1 kHz recording never had that
band; the converter that made it removed everything up there before the first sample existed. What a
model fitted to real 88.2 to 192 kHz recordings can say is what usually sits there in music of this
kind. Measured over twenty high-resolution tracks from one library, that is 24 dB under the midband at
22 to 24 kHz and 30 dB under by 32 kHz — and in the tracks that are music rather than a 1-bit
transfer, 35 to 60 dB under. Nobody hears 30 kHz. An amplifier or a tweeter asked to reproduce it may
make something audible of it, which is the argument against, and is why it is off and trimmed 6 dB
down when it is on. FUPlayer has a switch that removes ultrasonics from high-rate files for exactly
that reason; this is the same argument pointing the other way.

## Hearing what was added

**Output the repair alone** plays the difference between the repaired signal and the one that went
in: what the repair invented, and silence everywhere it invented nothing. A copy of the signal is
delayed by exactly what the repair delays it by and subtracted afterwards, so what is left is the
repair's own output rather than a comb filter.

It is a monitoring mode, not a listening one, and it is the only honest way to answer "how much of
this is the recording". On a 128 kbit/s MP3 it is loud. On a transparent 256 kbit/s AAC stream it is
nearly silent, which is the correct answer and not a fault.

## The model

One model does both jobs, because they are the same question asked about different parts of the
spectrum: given this coded frame, where did the original sit? It answers with a gain in decibels for
every band. Above the cutoff that gain is the level of the invented band. Below it the gain puts back
what the quantiser took. The two switches above decide which half of that answer is used.

The format is a small feed-forward network with tanh hidden layers, trained by backpropagation with
Adam, running in a few microseconds per frame. It can also hold a **constant curve**: one gain per
band, the same for every frame, expressed by zeroing every weight and putting the curve in the output
biases. On a thin corpus that is what training saves, because that is what wins; on the corpus
described below the network wins by a distance and the curve is not competitive.

Two networks can be installed, and they are told apart by the band they were fitted over rather than
by their filename. One stops around 22 kHz and repairs codecs. One runs to 48 kHz and invents the
band above a recording's own Nyquist rate. Each is confident nonsense in the other's job.

### A curve or a network: what the corpus decides

Fitted to **33 releases**, all of them from ambient netlabels, and all coded with one encoder, the
frame-by-frame network lost to a constant curve and lost badly: 5.86 against the curve's 3.64, with
6.03 for doing nothing at all, as mean squared error in decibels on releases none of them had heard.
Weight decay did not close the gap; nor did a smaller network. The conclusion drawn at the time was
that a constant curve is the honest answer here.

Fitted to **155 releases by 180 artists, coded with four encoders at fourteen bit rates**, the same
architecture wins by a distance:

| | held-out error |
| --- | --- |
| doing nothing | 6.982 |
| one constant gain per band | 5.516 |
| 290-48-24-96 network | 3.080 |
| 290-96-64-96 network | **3.021** |

The network is **45 % better than the curve**, where before it was 60 % worse. Nothing about the
architecture changed. What changed is that a corpus of one genre coded one way teaches a network that
genre's habits, and those do not survive a change of record; a corpus of 155 releases across four
codecs teaches it what a codec does.

That is worth stating plainly because the earlier conclusion was wrong in a way that looked rigorous.
The measurement was sound and the reasoning from it was not: "a network cannot learn this" and "a
network cannot learn this **from thirty-three ambient records**" are different claims, and only the
second one was tested.

`train-repair` still measures both on every run and saves whichever wins, so a thin corpus produces a
curve and says so rather than shipping a network that has memorised it.

**A model of this kind predicts a level, never a detail.** It cannot invent a cymbal strike the
encoder removed. What it knows is what the missing part of a spectrum usually looks like.

## Training one

The pipeline is four commands. Nothing is downloaded that is not freely licensed, and nothing that is
downloaded is redistributed by this project.

### 1. Fetch freely licensed lossless music

```bash
pwsh build/fetch-training-audio.ps1 -Destination D:\training -Count 45
```

This queries the Internet Archive for FLAC releases under CC0 or the Public Domain Mark, neither of
which asks for anything in return, and writes a `manifest.csv` recording every identifier, licence and
URL. `-Licence permissive` also takes CC-BY-SA, which wants credit and carries a share-alike term.

Lossless is not optional. The training answer is read off the part a codec would have thrown away, so
if the source has already been coded there is no answer to read.

### 2. Check what you got

```bash
fuplayer-cli bandwidth D:\training
```

Anything that is not full band is useless as training material, because it teaches the model that high
bands are empty. The dataset builder screens for this automatically, but it is worth looking: in the
first corpus used here, one file in six was band-limited at 4 kHz.

### 3. Build the pairs

```bash
fuplayer-cli dataset D:\training --out D:\repair.fudata --seconds 150
```

Each file is coded with the AAC encoder Windows ships, at 96, 128, 160 and 192 kbit/s, and decoded
back. The decoder pads the front, so the offset is found by cross-correlation rather than assumed;
below the cutoff the coded copy then matches the original to within 0.2 dB, which is how you know the
pair is lined up. Every frame is patched exactly as the player patches it, and the answer recorded is
the gain that would take each band to where the original sat.

### Which codecs it learns from

AAC and MP3, because between them they are what people listen to. The AAC encoder Windows ships is
used directly, at 128, 192 and 256 kbit/s. If **ffmpeg** happens to be on your path the builder finds
it and adds a second AAC encoder at 96 to 256 and MP3 at 128 to 320, which Windows will not write at
all. Nothing is downloaded to make that work, and if ffmpeg is absent the set is built from the one
encoder and the builder says so.

Vorbis and Opus were here and are not any more. They cost the same hours as the two codecs that
actually turn up, and this is a corpus that takes an afternoon.

Two AAC encoders rather than one, because they disagree about the same bit rate and the disagreement
is the lesson. Measured on this corpus at 256 kbit/s, ffmpeg's encoder is near enough transparent —
band energies within a decibel, frame by frame — while the Windows encoder empties the band above
20 kHz in three quarters of frames. A model shown only the first learns that 256 kbit/s needs nothing
doing.

Getting one on Windows, if MSYS2 is already there from building the FFmpeg libraries:

```bash
pacman -S mingw-w64-ucrt-x86_64-ffmpeg
```

then put `C:\msys64\ucrt64\bin` on the path for the run. That is a full ffmpeg with the GPL parts
enabled, which is fine here because it is a tool that makes training data on your own machine and
nothing from it is redistributed, in the same way that a compiler's licence is not the licence of what
it compiles. FUPlayer's own FFmpeg libraries remain the LGPL build from `build/ffmpeg/`.

Training on one codec is the mistake it looks like. Eleven passes over three encoders put the wall at
16, 17, 19 and 20 kHz and leave two passes with no wall at all, and it is those last two that carry
the case a listener meets most often. A set built without them teaches a model that every frame is
missing a band, and handed one that is not, it invents: on a held-out track coded to AAC at
256 kbit/s, the model fitted without such frames turned 0.5 dB of error at 20 to 21 kHz into 13.6.

The cutoff is measured per file and handed to the network as a feature, as a fraction of the top of
the band layout rather than of the file's Nyquist rate. That matters for captured audio: the same
44.1 kHz music arriving at 88.2 kHz would otherwise describe itself as cut in half.

| Option | Meaning |
| --- | --- |
| `--seconds <n>` | How much of each file to use |
| `--rates <list>` | Bytes per second for the Windows encoder, comma separated. 16000, 24000, 32000 |
| `--bands <n>` | Bands the spectrum is described in. 96 is the default |
| `--low <Hz>` `--high <Hz>` | Where those bands start and end. 200 Hz to 22.05 kHz by default |
| `--context <n>` | Frames either side the network sees. 1 means three frames in all |
| `--stride <n>` | Keep every nth frame. Frames overlap by three quarters, so even 8 loses little |

The layout is worth a thought before a long run. The bands are log spaced, so 40 of them from 200 Hz
put thirty below 10 kHz, where a codec changes nothing, and three above 15 kHz, where the whole of
the rebuild happens. The builder now prints how many land above 15 kHz; if that number is small, the
model cannot describe the band it is being asked to invent, however long it is trained.

### 4. Train

```bash
fuplayer-cli train-repair D:\repair.fudata --out my-network --epochs 8
```

One source file in eight is kept back from the start, spread across the corpus, and never trained on.
Holding back the tail of the set instead means holding back whichever releases happened to sort last,
and three unusual albums then look exactly like a model that has learned nothing.

Two baselines are printed, and the second is the one that matters:

- **Doing nothing**: the error from leaving every band where the codec left it.
- **One constant answer per band**: the mean gain over the training material, applied blindly. This is
  roughly what a fixed slope already does, so a model has to beat *this* to be worth its weight.

If the network cannot, the constant curve is saved instead, and the run says so. If neither beats
doing nothing, nothing is saved at all.

## Does it work

`evaluate` answers that, on files the model was never trained on:

```bash
fuplayer-cli evaluate D:\holdout --seconds 45
```

It codes each file, repairs it, and measures the mean distance from the lossless original band by
band. It reports above and below the cutoff separately, on purpose: filling a band the codec emptied
is easy and would otherwise hide a model that does nothing useful to the band the codec kept.

It reports three columns, and the middle one is why. **Patched** is the fixed algorithm with the
model's answer switched off; the step from there to the last column is all the model is worth.
Reporting one number for both gives the patch's credit to the model.

`train-repair` also prints its held-out error by frequency region, and that table is the one to read.
Over 316,000 frames from 120 tracks none of the three encoders' output was trained on, in rms decibels:

| region | bands | doing nothing | constant curve | network |
| --- | --- | --- | --- | --- |
| below 10 kHz | 80 | 1.04 | 1.03 | **1.03** |
| 10 to 15 kHz | 8 | 1.48 | 1.48 | **1.48** |
| 15 to 18 kHz | 4 | 9.64 | 9.54 | **3.77** |
| 18 to 20 kHz | 2 | 16.38 | 13.96 | **5.25** |
| above 20 kHz | 2 | 23.00 | 16.76 | **6.70** |

Three things to take from that.

**The model earns its place above 15 kHz and nowhere else**, which is where a codec does its damage.
Below 10 kHz it changes the error by a hundredth of a decibel, which is the correct answer.

**One number for the whole spectrum is how a model gets chosen badly.** The headline is 86 percent
better than doing nothing; 80 of the 96 bands sit at 1.03 dB and drown the average. A model can
improve that headline by a third while leaving the top of the band exactly as wrong as it found it.

**A constant curve is no longer competitive**, where on an earlier corpus it nearly won. A single
curve has to serve both a frame missing everything above 16 kHz and a frame missing nothing at all,
and it fits neither. That is the corpus doing its job rather than the architecture improving.

The two halves are also reported separately, because filling a band the codec emptied is easy and
would otherwise hide a model that does nothing for the band the codec kept.

One more thing that matters: the two signals are lined up on 300 Hz to the cutoff, a region neither a
codec nor a repair writes to. Lining them up on their overall level instead makes a repair that fills
the top of the spectrum appear to improve the bottom of it as well, which it never touched.

### End to end, through the player

The numbers above come from the trainer. These come from running whole files through the player's own
repair stage and measuring the result against the lossless master: sixteen tracks never trained on,
mean absolute error per band in decibels, at the default strength.

| | 16-17 kHz | 17-18 | 18-19 | 19-20 | 20-21 |
| --- | --- | --- | --- | --- | --- |
| **MP3 128** coded | 30.51 | 64.19 | 63.24 | 59.83 | 54.70 |
| patch alone | 12.34 | 17.20 | 18.92 | 20.43 | 16.93 |
| repaired | **6.28** | **5.61** | **6.43** | **7.69** | **6.61** |
| **Vorbis 128** coded | 2.66 | 11.97 | 23.52 | 69.81 | 86.54 |
| repaired | 3.26 | **9.36** | **8.93** | **9.43** | **6.66** |
| **MP3 320** coded | 1.31 | 2.83 | 4.43 | 7.66 | 28.25 |
| repaired | 1.45 | **2.35** | **3.63** | **5.06** | **11.81** |
| **AAC 256, Windows encoder** coded | 1.96 | 11.81 | 25.46 | 36.16 | 42.64 |
| repaired | 1.97 | **7.21** | **14.99** | **19.09** | **21.45** |
| **AAC 256, ffmpeg encoder** coded | 0.59 | 0.59 | 0.61 | 0.61 | **0.56** |
| repaired | 0.71 | 0.80 | 1.16 | 1.40 | 1.90 |

The last pair is the one to read carefully, and it is not good news. That encoder at 256 kbit/s is
transparent by this measurement — every band within 0.6 dB of the master — and the repair makes it
**worse**, by about 1.3 dB at 20 to 21 kHz. The Windows encoder at the same bit rate empties the band
above 20 kHz in three quarters of frames, and the repair is worth 21 dB on it.

Both read as full band to the detector, so the model cannot tell them apart from the cutoff and
settles on an average of the two. That is not a bug to be fixed by training harder; it is genuine
ambiguity, and both are real encoders at a bit rate people really use. What it costs is 1.3 dB on
material that had nothing wrong with it, where the content is already 25 dB down. What it buys is 20
to 50 dB everywhere a codec has actually taken something away. **Prediction strength** exists for
anyone who would rather have the first than the second.

The top kilohertz, 21 to 22 kHz, is left out of the table because nothing is written there: the
rebuild stops at 21 kHz and fades into what the codec left rather than into silence. On MP3 at
128 kbit/s that band reads 47.31 coded and 46.83 repaired, which is the fade doing nothing, which is
correct. It used to read 69 dB by fading into silence and erasing the encoder's own residue.

This is a spectral measurement, not a listening test, and it does not claim to be one.

## The band above the source's own Nyquist rate

`dataset --narrow-to` builds a second, different training set. The pair is a real 88.2 to 192 kHz
recording and the same recording with everything above 22.05 kHz removed, which is what that music
would have been had it been released at CD rate. The answer above the old Nyquist rate is then a
recording rather than a guess, which is the only reason to attempt the band at all.

```bash
fuplayer-cli dataset D:\hires --out D:\hires.fudata --narrow-to 44100 --bands 128 --high 48000
fuplayer-cli train-repair D:\hires.fudata --out above-nyquist --hidden 96,64 --epochs 14
```

The resulting model runs to 48 kHz, which is how the player tells it apart from a codec repair.

**Two kinds of file have to be thrown out, and they fail in opposite directions.** A transfer from a
1-bit source carries the modulator's noise, which rises with frequency: measured on one library, a
fifth of the high-rate tracks have a spectrum that is flat from 8 kHz to 40 kHz, and one of them sits
1.4 dB under its own midband at 30 kHz, which is not music by any reading. Fitting to those teaches a
model to add hiss to everything. At the other end, a 44.1 kHz master sold at 96 kHz has nothing up
there but the resampler's stopband, and teaches nothing at all. The builder screens on the slope from
8-16 kHz to 24-32 kHz and keeps what falls away between 13 and 70 dB; on 624 excerpts that kept 486.

**Does it work.** Ten high-resolution tracks that were never in the corpus, narrowed to 44.1 kHz and
back, error against the original in decibels per band:

| | 8-16 kHz | 16-22 | 22-24 | 24-28 | 28-32 | 32-40 | 40-48 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| narrowed, nothing done | 0.00 | 2.17 | 13.82 | 47.71 | 69.68 | 59.81 | 32.20 |
| patch, no levels | 0.00 | 2.09 | 20.39 | 21.05 | 16.70 | 32.16 | 38.90 |
| patch and model | 0.00 | **1.79** | **5.66** | **5.96** | **5.99** | **7.28** | **13.54** |

Below the source's own Nyquist rate nothing is written, and the table says so. Above it the synthesised
band lands within about 3 dB of where these recordings actually put it, out to 32 kHz, and drifts loud
above that.

**The cutoff here is known, not measured, and that matters.** Handing this stage a measured cutoff
reads 25.4 kHz on a 44.1 kHz round trip rather than 22.05: what sits above a resampler's wall is its
stopband rather than nothing, so the steepest fall is at the end of that and not at the start. The
player takes the source's own Nyquist rate, which is a fact about the file. Measuring it instead left
22 to 24 kHz untouched and the whole band several decibels out.

**What is actually up there is quiet.** Averaged over twenty high-resolution tracks, relative to the
2 to 8 kHz band: −24 dB at 22 to 24 kHz, −27 at 24 to 28, −29 at 28 to 32. In the tracks that pass the
screen it is lower still, −35 to −60 dB. Synthesising it is defensible as "what music of this kind
usually carries there". Hearing it is not the claim, and nobody should expect to.

## The simpler model

There is also a linear model, fitted by `fuplayer-cli train` straight from lossless music without
coding anything. It predicts only the level of the rebuilt band, not the correction below the cutoff,
so it drives harmonic rebuilding alone.

It exists because it needs no encoder and no coded pairs: point it at your own library and it learns
what your music's high band usually looks like. Where a network is installed the network is used
instead, since it does strictly more.

```bash
fuplayer-cli train "D:\Music\FLAC" --cutoff 16000 --out my-library
```

## No model ships with the player

Which music a model was fitted to decides what it predicts, and that choice belongs to whoever is
listening. There is also no set of weights whose licence would sit comfortably inside an MIT
repository. So the folder starts empty:

```
%APPDATA%\FUPlayer\models\
```

`fuplayer-cli models` prints the folder and lists what is in it. `build/install-model.ps1` installs
someone else's file and refuses anything that is not a readable model.

A model file is plain JSON: a version stamp, the band layout and network shape it was fitted with, and
the weights in base64. The loader checks the version and the shape and refuses a file that does not
match, rather than reading it wrongly.

## What this costs

Each stage adds one transform of delay at the conversion rate: 2,048 samples, which is 46 ms at
44.1 kHz. The network needs the frame after the one it is repairing, so it adds one more hop on top.
That is before the filter's own group delay, which at 2,097,152 taps is a second and a half.

Processing cost is a few percent of one core.

## What to expect

Rebuilding a band makes a recording sound more open and adds air the encoder removed. It is also
audibly synthetic on some material, particularly on cymbals and applause, where a copied octave does
not behave like the real thing. Start at **Quiet**, and turn it off for anything that was never lossy.

The honest summary is that these stages make lossy material measurably closer to the master and more
pleasant to listen to. They do not make it lossless, and the spectrum analyser in Now playing will
show you exactly what was added and where.
