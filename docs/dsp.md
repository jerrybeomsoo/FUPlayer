# The signal processing

This is what happens between the file and the DAC, and why.

## Contents

- [Why resample at all](#why-resample-at-all)
- [Polyphase resampling](#polyphase-resampling)
- [Filter design](#filter-design)
- [Two delays, only one of them the filter's](#two-delays-only-one-of-them-the-filters)
- [Direct convolution](#direct-convolution)
- [Partitioned convolution](#partitioned-convolution)
- [Non-uniform partitioning](#non-uniform-partitioning)
- [Real-input transforms](#real-input-transforms)
- [Splitting the work with a GPU](#splitting-the-work-with-a-gpu)
- [Dither and noise shaping](#dither-and-noise-shaping)
- [Delta-sigma modulation](#delta-sigma-modulation)

## Why resample at all

A 44.1 kHz stream is a sequence of impulses whose spectrum repeats every 44.1 kHz. To recover the original
band-limited signal, everything above 22.05 kHz has to be removed. Every DAC does this, in a fixed-function
block with a few hundred taps and a response its designer chose.

Doing it on the host removes those constraints. At 705.6 kHz the images sit at 705.6 kHz and above, so the DAC's
own filter has nothing to remove, and the filter that shaped the audio band is one you selected, of a length the
chip could never afford.

## Polyphase resampling

Upsampling by L is conceptually: insert L-1 zeros between samples, then low-pass. The zeros mean L-1 of every L
multiply-adds are against zero, so the filter is decomposed into L **phases**, each holding every Lth
coefficient. Output sample n uses phase n mod L against the input history. No multiply is wasted.

`PolyphaseStage` stores the phases contiguously so each is a linear sweep through memory, which matters more
than the arithmetic once a filter is longer than the cache.

A conversion of 44.1 kHz to 705.6 kHz is L = 16. Filter length is quoted at the output rate, so a
262,144-tap filter is 16,384 taps per phase, and each output sample costs 16,384 multiply-adds in the direct
path.

## Filter design

Every windowed-sinc filter here is described by four numbers: a passband edge, a stopband edge, a stopband
attenuation, and a window (Kaiser or Gaussian). `FirDesign.Lowpass` produces exact coefficient symmetry by
computing half of them and mirroring.

The catalogue spans steepness rather than character: `kaiser-compact` starts falling inside the top octave,
`kaiser-extreme` has the narrowest transition available. Gaussian windows give no ripple in the transition band
at the cost of a wider one for the same length.

When you ask for more taps than the specification needs, the window is stretched over the extra length rather
than padded with zeros, so the extra taps buy a narrower transition. Roughly:

```
transition width  ~  8.5 * output rate / taps        (for a 130 dB stopband)
group delay       =  (taps - 1) / 2  output samples  (linear phase)
```

At 705.6 kHz that is a 23 Hz transition and 186 ms of delay at 262,144 taps, and a 0.18 Hz transition and 24
seconds of delay at 33,554,432.

**Phase.** Linear phase delays every frequency equally, which is what makes the delay so long and puts half the
impulse response before the peak. `ToMinimumPhase` moves all the energy after the peak through a cepstral
transform, removing almost all the delay and all the pre-ringing, at the price of a frequency-dependent phase
shift. Intermediate phase convolves a linear-phase and a minimum-phase design that share the attenuation budget.

**Apodizing** filters close before the source Nyquist frequency instead of at it, so ringing and aliasing
already recorded in the file are attenuated rather than passed on.

## Two delays, only one of them the filter's

A long filter adds two different delays, and only one of them is inherent.

**Group delay** is the filter. A linear-phase FIR of N taps genuinely delays the signal by (N-1)/2 samples. The
only ways to shorten it are a shorter filter or a minimum-phase design.

**Block delay** comes from convolving in blocks. A frequency-domain stage must collect a whole block of input
before it can transform it. This is not part of the filter: the samples that come out are identical either way.
It is a setting, and the DSP studio shows both figures separately.

## Direct convolution

One multiply-add per tap per output sample, in arrival order, no latency beyond the group delay.

Cost per output sample is exactly the taps per phase. It is the fastest thing there is for short filters,
because there is no transform overhead at all, and it stops being practical somewhere in the tens of thousands
of taps a phase.

## Partitioned convolution

The frequency-domain path is uniformly partitioned overlap-save:

1. Split the filter into P partitions of B taps each.
2. Transform each partition once, at design time, into a spectrum of 2B points.
3. For each block of B input samples, transform the block together with the previous one.
4. Multiply that spectrum against each partition spectrum and accumulate, offsetting by the partition's age.
5. Transform back, and discard the first half as the circular wrap-around.

Cost per output sample, with L phases:

```
transform cost   =  (1 + L) * (5 * log2(B) + 6) / L      one forward, one inverse per phase
partition cost   =  6 * (B + 1) / B  per partition       one complex multiply-add per bin
total            =  transform cost + P * partition cost
```

The tap count only enters through P, and P shrinks as B grows, so the cost grows with the logarithm of the
block rather than with the filter length. This is what makes a million-tap filter playable.

`FftPolyphaseStage.ChooseBlock` evaluates that model over every power-of-two block within a ceiling and picks
the cheapest. The ceiling grows with the filter, to a thirty-second of its own span, and is never below 50 ms.

## Non-uniform partitioning

Shortening the block gives back the latency but costs arithmetic, because P rises as B falls. At 262,144 taps,
going from a 46 ms block to a 1.5 ms block costs 13 times the multiply-accumulates.

The way out is that not every tap needs to answer quickly. A band of taps starting at offset O only ever
contributes to output that is O samples behind the newest input, so it can be convolved in blocks of up to
O + head samples and still never be late. Early taps go in short blocks, later ones in blocks that double and
double again:

```
taps:     0 ....... 64 ............ 192 ....................... 1024 ...................
block:    |-- 64 --|--- 128 -------|---- 256 -----------------|---- 512 ----------------
latency:  64 samples for all of them
```

`PartitionPlan` picks the band layout with a small dynamic program over the same cost model, because the trade
runs both ways: every extra band is another forward transform and another inverse per phase, and with a large L
those inverses dominate. Where the extra transforms would not pay, the answer that comes back is one band, which
is ordinary uniform partitioning.

At 262,144 taps and a 1.5 ms block, this takes the cost from 13 times the cheapest block to about 2 times.
Output matches the uniform stage to -297 dB and is bit-identical however the input is cut into buffers.

Uniform partitioning is the default because a GPU kernel takes one block size only.

## Real-input transforms

Every transform in a frequency-domain stage is of real data. The input block is real; each phase's inverse turns
a conjugate-symmetric spectrum back into real samples. Handing that to a complex FFT carries an all-zero
imaginary part through the forward pass and computes a mirrored half on the way back that is discarded.

`RealFftPlan` reads 2N real samples as N complex ones, transforms at half the size, and untangles the result. It
is exact, matching the complex transform to about -260 dB where double precision reorders.

This is worth more than it sounds, because transforms *are* the cost: one of the input per block plus one per
phase, so at L = 32 that is 33 transforms against a handful of partition multiplies. Halving them was worth
1.15x on the whole pipeline at 65,537 taps.

## Splitting the work with a GPU

A long filter's convolution is thousands of independent multiply-accumulates, which is what a GPU is for. Three
things make the difference between a speed-up and a slow-down.

**Divide by phase, not by channel.** A stereo conversion at 16 phases has 32 independent pieces of work per
block. Giving the device whole channels means a device worth less than a channel gets nothing.

**Fill channels one at a time.** A channel wholly on the device never waits for it, so its thread carries
straight on into the modulator and the dither while the device works. The same total share spread evenly over
the channels measured 20 percent slower.

**Measure, do not model.** A kernel launch costs the same for one phase as for twenty, so a small share is
disproportionately poor value and the cost of a split is not a straight line between its ends. `PhaseBalance`
times every split from none to all during playback and uses the fastest.

Timing the filter answers which split convolves quickest. It does not answer which split *plays* quickest: on a
laptop whose GPU and cores share a power budget, the filter can finish sooner while everything after it finishes
later. So the winner is then checked against whole pipeline blocks with the device left out entirely, and kept
only if the pipeline agrees.

For that comparison to be honest, "left out" has to mean the device is not handed the block at all. A round trip
with no phases in it still costs about half a millisecond. The device is also handed the spectrum the processor
has already computed rather than transforming the block again, which takes the per-block cost of having a device
attached from 30 percent of the pipeline to 11.

## Dither and noise shaping

Quantizing to the DAC's word length without dither leaves distortion correlated with the signal. `DitherCatalog`
offers eleven options, from none through rectangular, triangular, Gaussian and high-pass triangular to five
noise-shaping curves.

Noise shaping moves quantization noise out of the band where hearing is most sensitive. The five-tap
psychoacoustic curve follows Lipshitz et al.; the deeper ones trade a steeper rise above 20 kHz for more in-band
suppression, which only works when there is ultrasonic room to put the noise in.

The automatic setting picks by output rate: triangular up to 48 kHz, high-pass triangular up to 96 kHz, and
noise shaping above that.

## Delta-sigma modulation

DSD output modulates the signal to one bit at up to 45.1 MHz. The modulator is a feedback loop: each output bit
depends on the one before it, which is why this stage stays on the CPU regardless of what the GPU is doing.

`ModulatorCatalog` holds six noise transfer functions from 5th to 9th order. Higher orders suppress in-band
noise further and need more analogue filtering after the DAC, and their stability margins are narrower;
`DeltaSigmaModulator` detects an unstable state and resets, and Now playing counts how often that happens.

DSD sources can be passed through unchanged when the output rate matches, converted to PCM through a choice of
low-pass filters, or converted and remodulated.
