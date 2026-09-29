# Oversampling exciter

The exciter adds the music's own harmonics above where its spectrum ends. It makes them from the band above 7 kHz,
at four times the source rate, and runs at twice the source rate, so they reach past the source's Nyquist frequency:
up to 44.1 kHz for a 44.1 kHz source and 48 kHz for 48. It sits under **Lossy repair** in DSP Studio, runs with or
without the two networks, is off until asked for, and has nothing to set.

Like the networks, **it recovers nothing.** What it writes is made from the music below, at the level recordings
usually have there. To see it, set Now playing's analyzer to the processed output and a range above 20 kHz.

## How it works

1. **Where the music ends** comes from the codec detector: where a coded source's spectrum starts to fall, or 0.45 of
   the source rate for a source that runs to the top of its band. Until the detector has about a second of music, the
   exciter adds nothing. After the neural restorer, which fills up to Nyquist, it starts just under Nyquist.
2. **The drive** is the band from 7 kHz to 300 Hz under that point.
3. **Harmonics:** the drive goes through u² + u³/2σ at four times the source rate. The sum of two partials of one note
   is another partial of it, so the harmonics lie on the notes' own series. At four times the rate nothing folds back
   below Nyquist as a tone the music never had.
4. **How loud:** the drive band's level, continued along its own slope made 12 dB an octave steeper, which is where
   hi-res masters lie (below). In each sixth of an octave the harmonics fill only what is missing under that line:
   all of it where the band is empty, nothing where something already reaches it (the music's own band, the
   restorer's, the upscaler's). They never add more than a quarter of the drive band's power.
5. **Nothing below the top changes**, and until the top is known the exciter is a plain delay.

## Settings

| Setting | Values | Effect | Command line |
| --- | --- | --- | --- |
| Oversampling exciter | on, off (default) | The harmonics above where the music ends, up to 44.1/48 kHz. | `--exciter` |

## When it runs

- PCM sources at 48 kHz and below. From the exciter on, the chain runs at twice the source rate: the same 2×
  interpolator as the neural upscaler takes it there, and the chosen filter converts from there to the output rate.
  It comes after the restorer and the upscaler.
- At an output below twice the source rate the filter removes what lies above the output's Nyquist frequency. A
  lossless or restored source then leaves no room, and the exciter idles.
- The peak limiter runs with it.

## Measured

- **Above the source's Nyquist frequency**, against 40 stretches of hi-res masters from the library (88.2 to
  192 kHz, at most two from an album, upsampled CD masters left out), brought to CD rate and excited: +0.5, +0.6 and
  +1.5 dB from the masters in the bands at 0.52–0.65, 0.65–0.8 and 0.8–0.95 of the source rate. From song to song it
  is off by 10 to 15 dB either way: what a recording has up there varies that much.
- **Between a codec's edge and Nyquist**, on 27 held-out coded stretches without the restorer: +1.4 dB from the
  masters, and the log-spectral distance from 30.0 dB (the empty band) to 11.3 dB. With the restorer that band is
  the restorer's alone (9.4 dB).
- **Costs:** about 25 ms of delay, and 6.5 % of one core of the i7-7820HQ laptop for two channels while it writes,
  1.7 % while it waits.

## Where it is wrong

- **It is a guess.** The level fits hi-res masters on average, not the recording being played.
- **Chords.** The sum of partials of two different notes lands between their series.
- **Not listened to in a controlled test**, and most people hear little above 16 to 20 kHz.
