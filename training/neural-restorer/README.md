# Training a neural restorer

These scripts train the network behind **Neural restorer** in DSP Studio: a model that takes a coded 44.1 or
48 kHz stereo stream (Opus, AAC, MP3 and Vorbis, 96 kbit/s upwards) back towards the lossless recording at the
same rate, with about 90 ms of delay. The release ships the reference model these scripts made; a model is weights
fitted to somebody's music, so you can train your own from your own files instead. What it does, and how well the
reference model did it, is in [docs/neural-restorer.md](../../docs/neural-restorer.md).

The restorer shares the upscaler's discriminators and its split by title, so these scripts expect
[../neural-upscaler](../neural-upscaler/README.md) beside them.

## What you need

- **High-resolution music, and nothing else.** Files at 88.2, 96, 176.4 or 192 kHz, which `screen.py` then checks
  really carry music to 22.05 kHz. A CD-rate file cannot be a reference: brought to 44.1 or 48 kHz it has nothing
  above about 20 kHz, where the resampler's own passband ends. Neither can a transfer from a 1-bit source, whose
  top band is the modulator's noise. The screen drops both, and it matters more than it sounds: a corpus whose
  references disagree about where the band ends teaches the network to write nothing at all in a few bins, which
  a spectrogram shows as narrow dead lines across every track.
- FFmpeg with `libopus`, `libvorbis`, `libmp3lame` and the soxr resampler, found through the `FFMPEG` environment
  variable or on the `PATH`. Optionally qaac with Apple's CoreAudioToolbox, for Apple AAC copies, found through the
  `QAAC` variable (see [../neural-upscaler/add_apple_aac.py](../neural-upscaler/add_apple_aac.py)); without it the
  Apple copies fail and are skipped.
- Python 3.11 or later with PyTorch, torchaudio and the packages in
  [../neural-upscaler/requirements.txt](../neural-upscaler/requirements.txt). The reference model was trained from
  scratch on a 4 GB Quadro M2200 laptop card that was also driving the display.
- Disk for the corpus: about 150 MB a song, which is its reference and four coded copies at each of the two
  delivery rates, 90 seconds each at float16. The reference corpus is 634 songs and 95 GB.

## Steps

Run from this folder. `work/` is where everything goes; the corpus can be somewhere else through
`FUPLAYER_RESTORER_CORPUS`, which is worth doing when it will not fit beside the scripts.

```bash
# 1. What is in the library: one row a file, with its rate and tags
python ../neural-upscaler/scan_library.py "<music folder>" work/library.csv

# 2. Which of its high-resolution files carry music to 22.05 kHz, and which are resampled CDs or 1-bit transfers
python -m restorer.screen work/library.csv work/screen.csv 6

# 3. The corpus: every master at both delivery rates, each with four coded copies of the same 90 seconds
python -m restorer.prepare 6

# 4. Train: 32,000 spectral steps, then 16,000 with the discriminators; resumable, and a file named STOP in the run
#    folder ends it cleanly at the next hundredth step
python -m restorer.train --out work/runs/restorer --pretrain-steps 32000 --gan-steps 16000

# 5. Measure it on the held-out songs, codec by codec
python -m restorer.evaluate --checkpoint work/runs/restorer/best-gan.pt

# 5a. And the band above each codec's low-pass, mid and side apart, on masters that really have one. Read this
#     one: a band written into the side alone passes every per-channel measurement and is not in the mix
python -m restorer.band_report work/runs/restorer/best-gan.pt

# 5b. And the lines it draws: bins it writes low on every input, which a spectrogram shows as dark horizontal lines
#     across every track. Nothing should be listed; a few bins at the very top are the references' own roll-off
python -m restorer.bin_lines work/runs/restorer/best-gan.pt 30 --threshold 2

# 6. Export for the player, checked against PyTorch as the player runs it
python -m restorer.export --checkpoint work/runs/restorer/best-gan.pt --out work/export/neural-restorer.onnx

# 7. Play something brickwalled well below the band through the player with the new model, and look for bins it
#    never writes
python -m restorer.dead_bins <restored.wav>
```

Put `neural-restorer.onnx` and `neural-restorer.json` into the `models` folder next to `FUPlayer.exe`, or into
`%APPDATA%\FUPlayer\models`. The player tells restorers from upscalers by the name: a restorer's file name ends in
`restorer.onnx`.

A fine-tune starts from a trained model at step 0 of a new schedule. It can undo something the model learned wrongly
when the objective is changed so that it sees the fault — the reference model's side-only band and its lines were
both taken out that way — and it cannot when the fault comes from the corpus it is still fitted to, since the
weights come with it and the corpus teaches the same thing again; that took a new corpus and a fit from scratch:

```bash
python -m restorer.train --out work/runs/restorer-ft --init-from work/runs/restorer/best-gan.pt \
    --init-discriminators work/runs/restorer/last.pt --pretrain-steps 3000 --gan-steps 3000 --lr 2e-4 --warmup 300
```

## What the files are

| File | What it does |
| --- | --- |
| `restorer/screen.py` | Measures where each high-resolution file's spectrum ends and whether its top band is music or modulator noise, and keeps the ones that reach 22.05 kHz. |
| `restorer/prepare.py` | Builds the corpus: every reference a screened master brought down to 44.1 and to 48 kHz with a passband that reaches Nyquist, four coded copies of each drawn from the delivery tables, decoded and lined up with it. |
| `restorer/extra.py` | Adds coded copies to the corpus, made from the stored references. |
| `restorer/model.py` | The network, its whole-signal pass for training, and the streaming core the player runs. |
| `restorer/losses.py` | The masking threshold, the masked and band-level losses, and how much of each band a codec took away. |
| `restorer/data.py` | Crops for training, and the split by title the upscaler uses. |
| `restorer/train.py` | Training and validation. |
| `restorer/evaluate.py` | The held-out tables. |
| `restorer/export.py` | The ONNX export and its streaming check. |
| `restorer/dead_bins.py` | Finds bins the network never writes in one rendered file: dead bins, 12 dB down and more. |
| `restorer/bin_lines.py` | Finds bins a checkpoint writes low on every input, averaged over held-out copies, down to 2 dB: the lines a spectrogram shows, which one file's own fine structure would hide. |
| `restorer/band_report.py` | What a checkpoint puts back above a codec's low-pass, in the middle of the picture and at its sides, which the energy-weighted per-channel scores hide. |
| `restorer/make_test_fixture.py` | The numbers the player's own tests hold its restorer to. |
| `restorer/audio.py` | FFmpeg, reading its output, lining copies up. |
