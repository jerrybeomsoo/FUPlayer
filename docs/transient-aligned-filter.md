# Transient-Aligned Filter

The Transient-Aligned Filter is a reconstruction filter built on one idea: keep the ideal filter, the sinc, exactly as it
is for as much of the filter's length as possible, and shape only its two ends. The samples around every transient are
then rebuilt as an endless ideal filter would rebuild them; what a finite filter has to give up is pushed to its far ends.
The original samples pass through unchanged. It is in DSP studio under **Resampling filter → Transient-aligned**.

The idea is Rob Watts', whose WTA filter is in Chord Electronics' converters. Chord has published what the filter is for
and how long it is, but not how it is made, so this one is FUPlayer's own design, made from what is published and from
first principles. Nothing of Chord's is used, and the two are not the same filter.

## What is published about the WTA filter

- **What it is:** a linear-phase FIR interpolation filter designed as a windowed sinc. The windowing is Watts' own and
  unpublished; it has been refined as the filters grew longer, and its name says what it is for: the timing of
  transients ([HIFICRITIC][hificritic], [Stereophile][dave]).
- **The principle:** Watts has described the WTA as a compromise between keeping as many coefficients as possible equal
  to the sinc's, which reconstructs transients perfectly, and avoiding an abrupt change where the coefficients run out
  (Head-Fi, "Watts Up…?"). With a million taps, more than half the coefficients were identical to the sinc's; Chord says
  nearly all are in its four-million-tap Quartet ([ecoustics][quartet]).
- **How it was tuned:** by extensive listening tests, to come as close as possible to an infinitely long filter
  ([Stereophile][dave]).
- **Lengths:** DAC64 1,024 taps; Hugo and Hugo TT 26,368; Hugo 2 49,152 at 16 times the sample rate; DAVE 164,000 on
  166 DSP cores; M Scaler 1,015,808 on a Xilinx XC7A200T, 528 of its 740 DSP cores, 56-bit arithmetic; Quartet about
  four million on five FPGAs ([HIFICRITIC][hificritic], [Stereophile][dave], [Hugo 2 summary][hugo2], [ecoustics][quartet]).
- **Stages:** the long filter runs to 16 times the source rate; a second, short WTA filter takes it from 16 to 256
  times (eight DSP cores in DAVE); a linear interpolator and two low-pass filters take it to 2,048 times, where Chord's
  pulse-array noise shaper runs ([Stereophile][dave], [Hugo 2 summary][hugo2]).
- **Measured:** DAVE's impulse response is time-symmetric and very long, and the response is fully down by
  22.05 kHz, where the image of a 19.1 kHz tone disappears ([Stereophile, measurements][davem]). The M Scaler falls more
  than 100 dB within 15 Hz, and delays the music by 719 ms ([GoldenSound][goldensound]): exactly half of
  1,015,808 taps at 705.6 kHz, 0.7198 s, which is what a symmetric filter of that length delays.

## The claims that were checked

An answer from another AI model came with this request. Checked against the sources above:

| Claim | Verdict |
| --- | --- |
| A window shifts the phase ("micro timing"); WTA minimises the phase error and drives the group delay error to zero. | **Wrong.** A symmetric FIR has exactly linear phase, the same delay, (N − 1)/2 samples, at every frequency, whatever the window. A window changes the magnitude near Nyquist and how far the impulse is from the sinc, not the phase. |
| 1,015,808 taps are 16 polyphase branches of 63,488, h_m[k] = h[16k + m], half-length 507,904. | **Correct arithmetic.** It is how any 16× interpolator is computed. The 719 ms GoldenSound measured is 507,904 samples at 705.6 kHz. |
| The window is exp(−α(n/M)²)·[1 + γ·cos(πn/(MΩ))], M = 507,904. | **Unsourced.** A Gaussian factor is below one everywhere but the centre, which cannot leave more than half the coefficients identical to the sinc's, as Watts says they are. |
| The second stage, 16 to 256 times, is a spline or a short sinc. | **Half right.** Chord calls it a WTA filter, an FIR. The linear interpolation comes after it, from 256 to 2,048 times. |
| 64-bit accumulators; AVX-512 code. | **Wrong for the hardware.** The filters run on FPGA DSP cores, with 56-bit arithmetic stated for the M Scaler and 51 bits for Hugo TT's filter core. |
| The coefficients are optimised by Newton–Raphson or a genetic algorithm on a cost of passband, stopband and "transient" errors. | **Unsourced.** Watts says the window is his own, unpublished, and tuned by listening. |
| A clean-room logistic window, 1/(1 + e^(σ(\|n\|/M − ν))), "perfectly preserves the sinc's zero crossings". | **True of every window**, so it says nothing: a coefficient that is zero stays zero when it is multiplied. And a logistic window does not reach zero at its ends, the abrupt change the taper is there to avoid. |

## How it works

1. **Cut off at the source's Nyquist frequency.** The coefficients are sinc(n/L) for an interpolation by L. The centre
   coefficient is exactly one and every coefficient a whole L from it exactly zero, so it is a Nyquist filter: every
   original sample comes out bit for bit, and only the samples between them are computed. Convolved tap by tap that
   is exact to the last bit; in the frequency domain it is exact to the transform's rounding, about 10⁻¹⁶. Nothing is
   normalised afterwards, which would undo it; a long filter's DC gain is the sinc's to within 10⁻⁸.
2. **The window is flat in the middle:** exactly one, so those coefficients are the sinc's own, computed with the
   argument reduced in whole numbers so that even a coefficient millions of samples out keeps its last digits.
3. **The ends taper** along the running sum of a Kaiser kernel (β 15.57, made for 150 dB). The window is a rectangle
   convolved with that kernel, so its spectrum is the rectangle's multiplied by the kernel's: the kernel decides where
   the response closes, and pushes the rectangle's sidelobes down beyond it.
4. **The tapers are as short as closing within a thousandth of the Nyquist frequency allows:** ±22 Hz around 22.05 kHz
   for a 44.1 kHz source, 10,113 source samples at each end. Outside that band the passband is flat to within 10⁻⁷ dB
   and images are at least 150 dB down. They are never longer than a quarter of the filter each, so at least the middle
   half is always the sinc's.
5. **Automatic length** is the shortest that keeps half of it exact and still closes that narrowly: 0.92 s, 647,217 taps
   at 16 times. Any length can be chosen; longer keeps more of it exact (below).
6. **Its own stages,** whatever **Filter staging** says: the long stage to 16 times the source rate (or to the largest
   whole multiple under the output rate); a second transient-aligned stage from 16 up to 256 times, 64 samples long at
   its input, cut off at its own input's Nyquist frequency so it too keeps the samples it is given, and free to close
   anywhere between the source's band and its first image; half-band stages above 256 times. An output rate that is not
   a whole multiple, such as 44.1 to 48 or to 768 kHz, is reached by the usual family bridge from twice or 16 times the
   source rate, so the long stage is never a 160-phase stage convolved tap by tap. A lower output rate is one stage cut
   off at its own Nyquist frequency, an eighth as long by default.

### Why the tapers are that long

Two things are traded: how much of the filter is the sinc's own, and how wide a band around Nyquist is left
uncontrolled.

- **The time side matters less than it looks.** Over a 16× filter of 1,015,809 taps, from a window that tapers the
  whole length to one that keeps 95 % exact, the squared error of the coefficients against the infinite sinc changes by
  less than a factor of two; most of it is the part beyond the end, which no window has. What keeping the middle exact
  changes is where the error lies: none of it near the transient, all of it in the last stretch at either end, a third
  of a second and more away, at coefficients 95 dB and more under the peak.
- **The frequency side is easy to see.** Keep 98 % of that filter exact and images are only 50 dB down 22 Hz above
  Nyquist, 69 dB at 100 Hz and 93 dB at 200 Hz: a mirror image of whatever the recording has in its top 200 Hz, faint
  but plain on a spectrogram. With the tapers this filter has, the same length keeps 68 % exact and images are 164 dB
  down 22 Hz above Nyquist.

So the response is fully controlled outside a thousandth of Nyquist, and inside that rule as much of the filter as the
length leaves is the sinc. At Chord's lengths that comes out as published: 68 % exact at the M Scaler's length ("more
than half") and 92 % at four million taps ("nearly all"), and the response falls 150 dB within 22 Hz of Nyquist.

## Measured

A 44.1 kHz source at 16 times, 705.6 kHz:

| Length | Exact sinc | −6 dB at | 22 Hz above Nyquist | Worst image beyond | Worst passband below Nyquist − 22 Hz | Designed in |
| --- | --- | --- | --- | --- | --- | --- |
| 647,217 (automatic, 0.92 s) | 50.0 % | 22,050 Hz | −156.5 dB | −172.2 dB | 2 × 10⁻⁸ dB | 0.07 s |
| 1,015,809 | 68.1 % | 22,050 Hz | −164.9 dB | −178.6 dB | 1 × 10⁻⁸ dB | 0.03 s |
| 4,194,305 | 92.3 % | 22,050 Hz | −199.8 dB | −195.8 dB | 1.4 × 10⁻⁹ dB | 0.09 s |
| 33,554,433 | 99.0 % | 22,050 Hz | −208.5 dB | −214.4 dB | 1.7 × 10⁻¹⁰ dB | 1.1 s |

- **Original samples,** random input through the stage convolved tap by tap: 22,274 of 22,274, 33,792 of 33,792 and
  133,120 of 133,120 bit for bit at the first three lengths.
- **Chains,** whole: 44.1 kHz to 1.4112 MHz (16 times, then 2) flat to 3 × 10⁻⁸ dB and −200.9 dB beyond; 44.1 to
  48 kHz (2 times, then the bridge) 9 × 10⁻⁸ dB and −183.2 dB; 44.1 to 768 kHz 3 × 10⁻⁷ dB and −160.2 dB; the second
  stage alone flat to 22.09 kHz and −168.8 dB from the first image at 683.5 kHz.
- **Speed,** 128 s of 48 kHz Vorbis to 768 kHz PCM on a four-core i7-7820HQ, eight threads, frequency-domain
  convolution: automatic length 11.4 times real time, 1,048,576 taps 10.6 times, 4,194,304 taps 9.3 times; the default
  filter, Gaussian sinc · steep, 13.8 times; with the graphics card taking 15 of the 32 phases, 11.1 times. 44.1 to
  48 kHz costs 477 multiply-adds an output sample. At 33,554,432 taps
  the stage holds 554 MB of partition spectra.

## Settings

| Setting | Values | Effect | Command line |
| --- | --- | --- | --- |
| Resampling filter | Transient-Aligned Filter | The filter above, for PCM output; the same choice exists for DSD output, where it feeds the modulator at up to 16 times. | `--filter transient-aligned` |
| Filter length | Automatic (0.92 s), or a length | Counted at the output rate, like every filter's; the long stage gets the same span at its own rate. | `--taps <n>` |

The filter has no phase variants: a minimum-phase version would no longer be the sinc. Graphics offload applies to
its long stage as to any frequency-domain stage of one block length.

## How it differs from Chord's

- The taper is FUPlayer's own, set by the rule above and measured; Watts' is unpublished and tuned by ear.
- The arithmetic is 64-bit floating point, convolved in the frequency domain or tap by tap, not 56-bit fixed point on
  FPGA DSP cores.
- There is no 2,048-times interpolator or pulse-array noise shaper: they belong to Chord's DAC hardware. FUPlayer hands
  the result to your DAC, or to its own modulator for DSD.
- Whether the length or the window can be heard is Watts' claim from his listening tests; this page does not make it.
  What can be measured is above.

## Sources

- Keith Howard, "Tapping Into Better Digital Audio", HIFICRITIC, July–September 2018 ([PDF][hificritic]).
- John Atkinson, "Chord Electronics DAVE D/A processor", Stereophile, May 2017 ([review][dave], [measurements][davem]);
  "Chord's Million-Tap Digital Filter", January 2018 ([article][milliontap]).
- Rob Watts, "Hugo 2 technical summary", 2016–2017 ([PDF][hugo2]).
- GoldenSound, "Chord Hugo M-Scaler Measurements and Technical Evaluation", March 2022 ([article][goldensound]).
- Chord Electronics, Hugo M Scaler ([product page][mscaler]); ecoustics on the Quartet, 2026 ([article][quartet]).
- Rob Watts, "Watts Up…?" thread, Head-Fi.

[hificritic]: https://chordelectronics.co.uk/wp-content/uploads/2018/07/The-theory-behind-M-Scaler-technology.pdf
[dave]: https://www.stereophile.com/content/chord-electronics-dave-da-processor
[davem]: https://www.stereophile.com/content/chord-electronics-dave-da-processor-measurements
[milliontap]: https://www.stereophile.com/content/chords-million-tap-digital-filter
[hugo2]: https://data.mactechnews.de/563900.pdf
[goldensound]: https://goldensound.audio/2022/03/17/chord-hugo-m-scaler-measurements-and-technical-evaluation/
[mscaler]: https://chordelectronics.co.uk/product/hugo-mscaler
[quartet]: https://www.ecoustics.com/products/chord-electronics-quartet/
