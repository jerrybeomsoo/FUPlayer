# Training a neural restorer

These scripts train the network behind **Neural restorer** in DSP Studio: a model that takes a coded 44.1 or
48 kHz stereo stream (Opus, AAC, MP3 and Vorbis at 96 to 160 kbit/s) back towards the lossless recording at the
same rate, with about 90 ms of delay. The release ships the reference model these scripts made; a model is weights
fitted to somebody's music, so you can train your own from your own files instead. What it does, and how well the
reference model did it, is in [docs/neural-restorer.md](../../docs/neural-restorer.md).

The restorer shares the upscaler's corpus, split and discriminators, so these scripts expect
[../neural-upscaler](../neural-upscaler/README.md) beside them and use its high-resolution corpus if there is one.

## What you need

- Lossless music at any rate: 44.1 and 48 kHz ALAC, FLAC, WavPack or WAV files, and, if you built the upscaler's
  corpus, its screened high-resolution masters. The reference model used 924 songs, 336 of them high-resolution
  masters and 588 CD-rate files, fifty seconds of each.
- FFmpeg with `libopus`, `libvorbis`, `libmp3lame` and the soxr resampler, found through the `FFMPEG` environment
  variable or on the `PATH`. Optionally qaac with Apple's CoreAudioToolbox, for Apple AAC copies, found through the
  `QAAC` variable (see [../neural-upscaler/add_apple_aac.py](../neural-upscaler/add_apple_aac.py)); without it the
  Apple copies fail and are skipped.
- Python 3.11 or later with PyTorch, torchaudio and the packages in
  [../neural-upscaler/requirements.txt](../neural-upscaler/requirements.txt). The reference model took 4 hours 45
  minutes on a 4 GB Quadro M2200 laptop card that was also driving the display, and another 1 hour 10 minutes of
  fine-tuning.
- About 40 GB of disk for a corpus of that size, each song's reference and four coded copies at float16.

## Steps

Run from this folder. `work/` is where everything goes.

```bash
# 1. The CD-rate lossless files, from the upscaler's library scan (../neural-upscaler/scan_library.py)
python -m restorer.sources work/library.csv --favour-japanese

# 2. The corpus: for every song, its reference at one delivery rate and three coded copies of the same 50 seconds
python -m restorer.prepare 6

# 3. Optionally, one more copy per song of the kinds the first run saw least: Vorbis q4 to q6, and 192 to 320 kbit/s
python -m restorer.extra --table finetune --songs train --per-song 1

# 4. Train: 20,000 spectral steps, then 12,000 with the discriminators; resumable, and a file named STOP in the run
#    folder ends it cleanly at the next hundredth step
python -m restorer.train --out work/runs/restorer

# 5. Measure it on the held-out songs, codec by codec
python -m restorer.evaluate --checkpoint work/runs/restorer/best.pt

# 6. Export for the player, checked against PyTorch as the player runs it
python -m restorer.export --checkpoint work/runs/restorer/best.pt --out work/export/neural-restorer.onnx
```

Put `neural-restorer.onnx` and `neural-restorer.json` into the `models` folder next to `FUPlayer.exe`, or into
`%APPDATA%\FUPlayer\models`. The player tells restorers from upscalers by the name: a restorer's file name ends in
`restorer.onnx`.

A fine-tune starts from a trained model at step 0 of a new schedule:

```bash
python -m restorer.train --out work/runs/restorer-ft --init-from work/runs/restorer/best.pt \
    --init-discriminators work/runs/restorer/last.pt --pretrain-steps 3000 --gan-steps 3000 --lr 2e-4 --warmup 300
```

## What the files are

| File | What it does |
| --- | --- |
| `restorer/sources.py` | Chooses the CD-rate lossless files, with the upscaler's exclusions and weights, leaving out songs already in its corpus. |
| `restorer/prepare.py` | Builds the corpus: one delivery rate per song, three coded copies drawn from the delivery tables, decoded and lined up with the reference. |
| `restorer/extra.py` | Adds coded copies to the corpus, made from the stored references. |
| `restorer/model.py` | The network, its whole-signal pass for training, and the streaming core the player runs. |
| `restorer/losses.py` | The masking threshold and the masked and band-level losses. |
| `restorer/data.py` | Crops for training, and the split by title the upscaler uses. |
| `restorer/train.py` | Training and validation. |
| `restorer/evaluate.py` | The held-out tables. |
| `restorer/export.py` | The ONNX export and its streaming check. |
| `restorer/make_test_fixture.py` | The numbers the player's own tests hold its restorer to. |
| `restorer/audio.py` | FFmpeg, reading its output, lining copies up. |
