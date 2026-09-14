# Lossy repair

Three stages for material that has been through a perceptual codec. They live in the DSP studio under
**Lossy repair**, and every one of them is off until you ask for it.

Read this first: **none of them recovers what the encoder discarded.** The bits are gone. Two of the
three put back something plausible where the encoder left a hole, and the third moves what survived
closer to where it started. None of it is the master.

What can be said, because it was measured on releases the model had never been shown, is in
[Does it work](#does-it-work). The short version: a fixed algorithm does most of the work, and a
learned per-band curve levels it better than a fixed slope does.

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

If the verdict is full band, nothing is rebuilt, whatever the settings say.

It measures the signal, not the file. Anything steeply low-passed reads the same way.

## The three stages

**Artifact reduction** works on the band the codec kept. When an encoder runs short of bits it holds a
high band in one frame and drops it in the next, and that switching is heard as a watery chirping.

Be aware that the measurements in [Does it work](#does-it-work) do not support this stage: AAC at 96
to 192 kbit/s leaves the band it keeps within 0.37 dB per band of the original, and a learned
correction there measured slightly worse than leaving it alone. It is off by default and should
probably stay off unless your ears tell you otherwise on a particular recording.

**Harmonic rebuilding** works above the cutoff. The top octave below it is copied upwards in patches,
which is how spectral band replication has worked since MPEG-4 HE-AAC. The fine structure comes from
real content an octave down, so it moves with the music instead of sitting there as a static hiss, and
each patch is rotated by the frequency difference times the hop so the patches do not beat against one
another.

**Generative prediction** decides how loud everything should be. With it off, the rebuilt band follows
a fixed 12 dB per octave slope and the artefact damping is a fixed limit on how fast a bin may move.
With it on, a trained network decides both.

## The model

One model does both jobs, because they are the same question asked about different parts of the
spectrum: given this coded frame, where did the original sit? It answers with a gain in decibels for
every band. Above the cutoff that gain is the level of the invented band. Below it the gain puts back
what the quantiser took. The two switches above decide which half of that answer is used.

The format is a small feed-forward network with tanh hidden layers, trained by backpropagation with
Adam, running in a few microseconds per frame. **In practice what it usually contains is a constant
curve**: one gain per band, the same for every frame. A constant is expressible in exactly that
format, by zeroing every weight and putting the curve in the output biases.

That is not a shortcut. It is what the measurement asked for.

### Why a curve and not a network

Trained on 33 releases and judged on releases it had never heard, the frame-by-frame network scored
**5.86**, one constant gain per band scored **3.64**, and doing nothing at all scored **6.03**, all as
mean squared error in decibels. Weight decay did not close the gap; nor did a smaller network.

Whatever the network learns about an individual frame does not survive a change of record, and on
unfamiliar music that costs more than it gains. So `train-repair` measures both, and if the network
loses it saves the curve and says so.

**A model of this kind predicts a level, never a detail.** It cannot invent a cymbal strike the
encoder removed. What it knows is what the missing part of a spectrum usually looks like.

What would change this is more releases, or features that describe a frame better than forty band
levels do. Not a larger network, and not more training on the same material.

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

The AAC encoder Windows ships covers 96 to 192 kbit/s, which is most of what people actually listen
to. It does not go higher, so 256 kbit/s AAC, the rate some services use, is outside what it can make.

If **ffmpeg** happens to be on your path, the dataset builder finds it and adds whatever it can
encode: MP3 at 128 to 320, Opus at 96 to 160, Vorbis at 128 to 256, and AAC at 256 and 320. Nothing is
downloaded to make that work, and if ffmpeg is absent the set is built from AAC alone and the builder
says so.

The cutoff is measured per file and handed to the network as a feature, so a model does generalise
some way beyond the rates it was shown. How far is not something this project has measured.

| Option | Meaning |
| --- | --- |
| `--seconds <n>` | How much of each file to use. 150 is plenty when there are 40 files |
| `--rates <list>` | Bytes per second, comma separated. The Windows encoder takes 12000 to 24000 |
| `--bands <n>` | Bands the spectrum is described in. 40 is the default |
| `--context <n>` | Frames either side the network sees. 1 means three frames in all |
| `--stride <n>` | Keep every nth frame. Frames overlap by three quarters, so 2 halves the file for almost nothing |

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

On the four held-out releases, coded at 96, 128, 160 and 192 kbit/s, mean distance from the lossless
original in decibels per band:

| | coded | patched | with the curve |
| --- | --- | --- | --- |
| above the cutoff | 42.01 | 12.33 | **9.50** |
| below the cutoff | 0.37 | 0.37 | 0.40 |

Two things to take from that.

**Rebuilding works, and the learned curve improves on the fixed slope.** Patching alone closes 71
percent of the distance above the cutoff, and the curve closes a further 23 percent of what is left.

**There is almost nothing to repair below the cutoff.** AAC at these rates leaves the band it keeps
within 0.37 dB per band of the original, and applying a learned correction there makes it slightly
worse, not better. The patched column is identical to the coded column, which is the check that the
measurement is sound: patching writes only above the cutoff, and the number says so.

That is a finding against one of the three stages. Artifact reduction is aimed at a band that, by this
measurement, a modern encoder barely damages. The honest caveat in the other direction is that this
metric averages a band over time, and the warbling that artifact reduction targets is a change *in*
time inside one band, which averaging hides. So the measurement does not condemn the stage outright;
it does say that nothing here supports turning it on, which is why it is off by default.

The two halves are also reported separately, because filling a band the codec emptied is easy and
would otherwise hide a model that does nothing for the band the codec kept.

One more thing that matters: the two signals are lined up on 300 Hz to the cutoff, a region neither a
codec nor a repair writes to. Lining them up on their overall level instead makes a repair that fills
the top of the spectrum appear to improve the bottom of it as well, which it never touched.

This is a spectral measurement, not a listening test, and it does not claim to be one.

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
