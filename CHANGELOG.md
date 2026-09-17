# Changelog

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
