# Lossy repair

Three stages for material that has been through a perceptual codec. They live in the DSP studio under
**Lossy repair**, and every one of them is off until you ask for it.

Read this first: **none of them recovers what the encoder discarded.** The bits are gone. Two of the
three add signal that was never in the file, and the third only stops what survived from flapping. If
what you want is the recording, the answer is a better copy of the recording.

## How the cutoff is found

An encoder throws away everything above a cutoff and leaves a wall there: about 16 kHz for MP3 at
128 kbit/s, 19 to 20 kHz for AAC and Opus at streaming rates. Material that never went through one
carries energy, if only dither, right up to its own Nyquist rate.

FUPlayer measures this while the track plays. It averages Blackman-Harris transforms of the loud
parts, walks down from Nyquist to the last bin still 75 dB inside the 300 Hz to 5 kHz reference, and
then asks how hard the spectrum falls across the kilohertz above that point. Steepness is what
decides, not the cutoff alone: dull music loses the same energy but takes octaves over it, while a
codec stops dead. An edge of 24 dB per kilohertz counts as a codec.

If the verdict is full band, nothing is rebuilt at all, whatever the settings say.

It measures the signal, not the file. Anything steeply low-passed reads the same way.

## Artifact reduction

When an encoder runs short of bits it keeps a high band in one frame and drops it in the next. The
band switching on and off at the frame rate is the watery chirping usually called birdies.

This limits how far a high bin's magnitude may move between transforms. A band that flickers is held
steady; a band that is genuinely there passes through unchanged, and so does a real onset, because an
onset moves every bin at once and the limit is relaxed in proportion to how much of the band moved
together.

| Strength | A bin may move | Use |
| --- | --- | --- |
| Light | 18 dB per transform | The worst chirping only |
| Moderate | 12 dB | The usual setting |
| Firm | 7 dB | Audibly smooths cymbals as well as artefacts |
| Heavy | 3 dB | Badly warbling material, at the cost of the top octave's life |

It cannot put back what was discarded. It stops the remains from flapping.

## Harmonic rebuilding

The top octave below the cutoff is copied upwards in patches and levelled to continue the spectrum,
which is how spectral band replication has worked since MPEG-4 HE-AAC.

The fine structure comes from real content an octave down, so it moves with the music rather than
sitting there as a static hiss, and each patch is rotated by the frequency difference times the hop so
the patches do not beat against one another.

Below the cutoff nothing is written. What leaks back down through the skirt of the squared analysis
window measures **66 dB under the band it came from**, which is not zero and is not claimed to be.

The rebuilt level follows a fixed 12 dB per octave slope away from the cutoff unless a model says
otherwise.

## Generative prediction

A model that decides the rebuilt levels from the shape of the surviving band, instead of the fixed
slope.

It is ridge regression: the log level of sixteen bands below the cutoff, with the overall loudness
taken out, onto eight bands above it. A few hundred numbers, solved in closed form. **A linear model
invents no detail.** What it does is learn that a recording whose top octave looks a certain way tends
to have a high band of a certain shape, which is a better guess than a straight line. On held-out
material it predicts each band within about 3 dB.

### No model ships with the player

Which music a model was fitted to decides what it predicts, and that choice belongs to whoever is
listening. There is also no set of weights whose licence would sit comfortably inside an MIT
repository. So the folder starts empty:

```
%APPDATA%\FUPlayer\models\
```

`fuplayer-cli models` prints the folder and lists what is in it.

### Training one from your own music

This is the recommended route, and it needs no downloads. Point it at lossless files whose sample rate
is high enough to still carry the band being learned:

```bash
fuplayer-cli train "D:\Music\FLAC" --cutoff 16000 --out my-library
```

It skips anything whose rate is below 2.2 times the cutoff, skips quiet frames that would teach it
only what the noise floor looks like, and refuses to write a model from fewer than a thousand frames.
A few albums is plenty; an hour of music gives roughly 100,000 frames.

| Option | Meaning |
| --- | --- |
| `--cutoff <Hz>` | Where the model's prediction starts. Match it to the material you will play: 16000 for MP3, 19000 or 20000 for AAC and Opus |
| `--out <name>` | File name, written into the models folder as `<name>.fumodel.json` |
| `--bands <n>` | Input bands, 4 to 48. The default of 16 is a reasonable trade |
| `--ridge <r>` | Regularisation in thousandths. The default of 1 suits a few albums; raise it if you train on very little |

Train a separate model per cutoff. A model fitted at 16 kHz is the wrong shape for 20 kHz material.

### Installing someone else's

`build/install-model.ps1` copies a file, or downloads one, into the models folder and refuses anything
that is not a readable model:

```bash
pwsh build/install-model.ps1 -Source https://example.com/some.fumodel.json
pwsh build/install-model.ps1 -Source D:\downloads\shared.fumodel.json -Name aac-20k
```

Or just copy the `.fumodel.json` file into the folder yourself; nothing else is needed.

A model file is plain JSON: a version stamp, the rate and band layout it was fitted at, and the
weights. The loader checks the version and the weight count and refuses a file that does not match,
rather than reading it wrongly.

## What this costs

Each stage adds one transform of delay at the conversion rate: 2,048 samples, which is 43 ms at
48 kHz and 21 ms at 96 kHz. That is on top of the filter's own group delay. Both stages together cost
a few percent of one core.

## What to expect

Rebuilding a band makes a recording sound more open and adds air that the encoder removed. It is also
audibly synthetic on some material, particularly on cymbals and on applause, where the copied octave
does not behave like the real thing. Start at **Quiet**, and turn it off for anything that was never
lossy in the first place.

The honest summary is that these stages make lossy material more pleasant. They do not make it
lossless, and the spectrum analyser in Now playing will show you exactly what was added and where.
