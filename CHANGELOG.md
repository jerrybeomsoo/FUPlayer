# Changelog

## 0.2.2 — 2026-09-25

### Changed

- **A new neural restorer model.** Fitted from scratch to 634 high-resolution masters of every genre, screened so
  that every reference really carries music to 22.05 kHz, at both delivery rates. Against the model in 0.2.1, on
  the same 1,952 held-out stretches: log-spectral distance above 12 kHz 11.3 → 7.8 dB, the mid's and the side's
  band levels 2.0 → 0.9 and 3.0 → 1.2 dB from the master, the level above 16 kHz unchanged at 0.9 dB under it. A
  lossless file forced through it changes a little more, −65.8 → −62.0 dB, and frames with a band over the
  masking threshold go from 0.45 to 0.61 % ([docs](docs/neural-restorer.md)).
- The overload warning names the convolution mode when a long filter is running tap by tap, which is the largest
  lever there is.
- The Upscaled band level choices say which band they move: the one above the source's Nyquist frequency, and
  nothing below it.
- The live input page says what format the captured application arrives in, which Windows decides, and opens the
  setting that changes it; it names the collision when the application also plays directly to the output device,
  and opens the Windows pages that separate the two.
- The DSP studio says which way Automatic read the playing source ("VORBIS, read as lossy").
- A long filter running tap by tap on the graphics device keeps its history on the device, and sends it only the
  new samples of each block.
- With the neural upscaler on, the 2× interpolator in front of the network runs 5.4 times faster (8.6 → 1.6 ms
  per second of audio, per channel), and finding where the source's spectrum ends costs a tenth of what it did
  (34 → 3.5 ms per second of audio on the engine thread): it was worked out again after every block, a hundred
  times a second, where the spectrum changes twenty-three times.
- The limiter costs 40 % less at high output rates (12.7 → 7.5 ms per second of audio per channel at 705.6 kHz),
  its output unchanged bit for bit.

### Fixed

- Dark horizontal lines across the restored band, the same bins on every track: the 0.2.1 model wrote fourteen
  bins 2 to 5 dB under their neighbours at 48 kHz, among them 869, 881 and 894, on every input. None now at
  either rate, and the training scripts measure it (`restorer/bin_lines.py`).
- The spectrogram and waterfall drew the low octaves as blocks at short transform lengths.
- A model placed in `%APPDATA%\FUPlayer\models` under the shipped model's own name was ignored in favour of the one
  beside the program. The newest file of a name is the one used now, as the documentation always said.
- Tap by tap on a graphics device: after the device had been let go and taken back, which the check of whether it
  makes the music quicker does, it was given phases again before the input it convolves against had been
  refilled, and a few blocks came out wrong, at 44.1 kHz as loud as the music itself. It now waits for a whole
  filter's worth of samples.
- The limiter could lose the loudest sample of its look-ahead window when the level had been falling for longer
  than the window, and steer its gain by a quieter one. It takes a smooth fall, a low tone at a high rate; the
  music it was measured on never produced one.
- Changing how many of the phases the graphics device is given closed and reopened the audio device, which on an
  ASIO interface relocks the DAC. It now rebuilds the processing only.
- Now playing could, in a narrow window, read the audio device's format while the engine was closing it, and crash
  the player. Found by reading the code, not in use.
- The DSP studio's plots kept their progress bar running, with nothing said, when a filter could not be designed
  for them; they now say why.
- A damaged AIFF header is reported as damage, so FFmpeg is given the file, rather than failing with an internal
  error.
- A WAV render with an odd number of data bytes (24-bit mono, an odd number of frames) ends with the pad byte RIFF
  requires.

## 0.2.1 — 2026-09-18

### Added

- **Neural restorer** (DSP Studio → Lossy repair): coded 44.1 and 48 kHz stereo (Opus, AAC, MP3, Vorbis at 96 to
  160 kbit/s) back towards the lossless recording at the same rate, in 91 ms at 48 kHz and 99 ms at 44.1. A causal
  STFT network on mid and side with a 6-frame look-ahead: it rewrites the band above the codec's low-pass, restores
  the side channel the codec collapsed, and corrects spectral holes ([docs](docs/neural-restorer.md)).
- The restorer and the upscaler **chain**: with both on, the restorer runs first and the upscaler reads its output
  as lossless. Source type and Output delta cover both.
- **Both models ship** in the package's `models` folder.
- `--neural-restore` and `--restorer` in `fuplayer-cli`; `models` lists restorers and upscalers.
- `FUPlayer.exe --capture <pid>` starts a capture from the command line.
- `asio.log` and `player.log` in the settings folder record the ASIO driver's life and the engine's errors.

### Fixed

- Live input into a device that also plays the captured application directly (RME ASIO drivers do at a matching
  rate) was heard twice. The capture now mutes the application's own playback devices, not its session, so the
  capture keeps every sample, and unmutes them when it ends or on the next start after a crash. A switch on the
  live input page turns this off.
- The ASIO driver's callback table and buffers now outlive the driver, and nothing thrown in a driver callback
  leaves it: a fail-fast crash during live input to DSD256 was not reproduced, and these are the causes that could
  be closed.
- The graphics-device filter path leaked an OpenCL event every block.

### Changed

- The release package lists every third-party component it contains and carries each license and notice text in
  `licenses/`.

## 0.2.0 — 2026-09-17

### Added

- **Neural upscaler** (DSP Studio → Lossy repair): 44.1 and 48 kHz PCM to 88.2 and 96 kHz through an STFT-domain
  ConvNeXt network on ONNX Runtime (CPU, about 0.55 s of latency). It corrects codec artefacts below the cutoff
  and synthesises the band above it and above the source Nyquist. No model ships: train one from your own
  high-resolution files with `training/neural-upscaler` ([docs](docs/neural-upscaler.md)).
- **Source type** (Automatic, Lossy, Lossless), **Upscaled band level** (−6 to +6 dB) and **Output delta**
  (the upscaler's contribution alone).
- **Application capture**: send another application's audio through the whole chain (Windows process loopback).
- `FUPlayer.exe --diagnostics`; `--neural-upscale`, `--upscaler`, `--source-type`, `--upscaler-level`,
  `--output-delta` and `models` in `fuplayer-cli`.

### Fixed

- Lossy files and captures are always peak-limited, so decoder overshoots no longer clip into noise above 40 kHz
  or destabilise a DSD modulator. The limiter also runs whenever the upscaler does.
- Processed-output spectrum, spectrogram and waterfall at DSD and high PCM rates: the signal is decimated to the
  displayed band and analysed at the source's frequency resolution, so the low octaves no longer render as blocks.
- Ogg files that failed to open without a message, and the LGPL FFmpeg build script.

### Changed

- Settings files from 0.1.0 are migrated automatically.

## 0.1.0 — 2026-09-14

First release: PCM and DSD output up to DSD1024, long linear- and minimum-phase filters with optional GPU
offload, WASAPI and ASIO back-ends, native FLAC, WAV, AIFF, DSF and DFF decoding, the library, the analyzer and
`fuplayer-cli`.
