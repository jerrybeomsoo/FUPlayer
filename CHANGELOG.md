# Changelog

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
