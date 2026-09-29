# Oversampling exciter

The exciter adds the music's own harmonics to the top of its spectrum and above it. It makes them from the band above
7 kHz and writes them in two ways: faintly under the music from about 15 kHz, and, where the music is missing above a
codec's edge, up to the level recordings usually have there. With an output above the source rate they reach 44.1 kHz
for a 44.1 kHz source and 48 kHz for 48. It sits under **Lossy repair** in DSP Studio, runs with or without the two
networks, is off until asked for, and has nothing to set.

Like the networks, **it recovers nothing.** To see what it adds, turn on **Output delta** and set Now playing's
analyzer to the processed output's spectrogram.

## How it works

1. **Where the music ends** comes from a codec detector that weighs the last three seconds: where a coded source's
   spectrum starts to fall (its edge) and its cutoff (its wall), or 0.45 of the source rate and its Nyquist frequency
   for a source that runs to the top of its band. New values are glided to over about half a second, so a live stream
   that changes source moves the band with it. Until the source has been measured it is taken to be full band.
2. **Where the harmonics start:** three quarters of the edge, held between 12 and 18 kHz: 14.4 kHz for AAC at
   256 kbit/s that starts to fall at 19.2 kHz, 14.9 kHz for a full-band CD.
3. **The drive** is the band from 7 kHz (or half the start, if that is lower) to 300 Hz under the edge.
4. **Harmonics:** the drive goes through u² + u³/2σ at twice the rate the exciter runs at. The sum of two partials of
   one note is another partial of it, so the harmonics lie on the notes' own series, and at the doubled rate nothing
   folds back as a tone the music never had. The part the music already holds in phase (the third degree carries a
   copy of the drive) is fitted out in each band first, so what is added under the music adds power, not gain.
5. **How loud,** in each sixth of an octave, frame by frame:
   - **Under the music**, from the start to the edge, a fade even in decibels from 50 dB under it to 6 dB under it,
     and 6 dB under whatever lies above the edge: the restorer's band, the upscaler's, or the music's own.
   - **Where the music is missing above the edge**, up to a line: the drive band's level continued along its own slope
     made 12 dB an octave steeper, which is where hi-res masters lie (below). The line rises in across the codec's
     roll-off, from the edge to the wall, and a bin the music half fills gets the other half.

   Together they never add more than a quarter of the drive band's power.

## Settings

| Setting | Values | Effect | Command line |
| --- | --- | --- | --- |
| Oversampling exciter | on, off (default) | Harmonics from about 15 kHz, and above where the music ends up to the output's top. | `--exciter` |

## When it runs

- PCM sources at 48 kHz and below: coded or lossless, with or without the restorer and the upscaler.
- **Output above the source rate:** at twice the source rate, after the restorer and the upscaler. The same 2×
  interpolator as the upscaler's takes the source there, and the chosen filter converts from there to the output rate.
- **Output at or below the source rate:** at the source rate, in front of the chosen filter, up to the source's
  Nyquist frequency. At the source rate itself no conversion runs at all.
- The peak limiter runs with it.

## Measured

- **Above the source's Nyquist frequency**, against 40 stretches of hi-res masters from the library (88.2 to 192 kHz,
  at most two from an album, upsampled CD masters left out), brought to CD rate and excited: +0.8, +0.6 and +1.3 dB
  from the masters in the bands at 0.52–0.65, 0.65–0.8 and 0.8–0.95 of the source rate. From song to song it is off
  by 10 to 15 dB either way: what a recording has up there varies that much.
- **Between a codec's edge and 0.47 of the source rate**, on 27 held-out coded stretches without the restorer:
  +0.2 dB from the masters at the source rate and +0.7 dB at twice it, and the log-spectral distance from 29.1 dB (the
  empty band) to 10.7 dB; 0.4.1, which filled the roll-off at full height, was +1.4 dB and 11.3 dB. After the restorer
  the band goes from 0.9 dB under the masters to 0.0 dB, the distance from 9.16 to 9.21 dB.
- **Under the edge** the level moves +0.02 dB on average, at most 0.24 dB: the fade shows on a spectrogram, and
  hardly in the level. The codecs measured keep the level there within 0.1 dB of the masters.
- **Costs:** about 25 ms of delay, and 3.1 % of one core of the i7-7820HQ laptop for two channels at 48 kHz, 7.1 % at
  96 kHz.

## Where it is wrong

- **It is a guess.** The level fits hi-res masters on average, not the recording being played.
- **Under the edge it is an effect.** The harmonics there add density to a band the codec kept, not accuracy.
- **Chords.** The sum of partials of two different notes lands between their series.
- **The first second** of a source is taken as full band until it has been measured; the band then glides to where
  it belongs.
- **Not listened to in a controlled test**, and most people hear little above 16 to 20 kHz.
