# Training a neural upscaler

These scripts train the network behind **Neural upscaler** in DSP Studio: a model that takes a 44.1 or
48 kHz recording, lossy or not, to 88.2 or 96 kHz. No model ships with FUPlayer. A model is weights
fitted to somebody's music, so you train your own from your own high-resolution files. What it does,
and how well the reference model did it, is in [docs/neural-upscaler.md](../../docs/neural-upscaler.md).

## What you need

- Lossless files above 48 kHz: 88.2, 96, 176.4 or 192 kHz ALAC, FLAC, WavPack or WAV. The reference
  model used 336 tracks, about 25 hours, after screening. Fewer will train, and will generalise worse.
- FFmpeg with `libmp3lame`, `libvorbis` and `libopus`, and ffprobe. Encoders your build lacks are
  skipped. Optionally qaac with Apple's CoreAudioToolbox, for Apple AAC copies (see `add_apple_aac.py`).
- Python 3.11 or later, and PyTorch with torchaudio. An NVIDIA card makes it practical: the reference
  model took about nine hours on a 4 GB Quadro M2200 that was also driving the display, a second or so
  per adversarial step. On the processor alone expect weeks.
- About 60 GB of disk for a corpus of that size.

```bash
python -m venv venv
venv/Scripts/pip install torch torchaudio --index-url https://download.pytorch.org/whl/cu126
venv/Scripts/pip install -r requirements.txt
```

## Steps

```bash
# 1. What is in the library
python scan_library.py "D:/Music" work/library.csv

# 2. Which of it to learn from: lossless above 48 kHz, classical and instrumental music left out
python select_tracks.py work/library.csv work/tracks.csv --favour-japanese

# 3. The corpus: each master at 88.2 or 96 kHz, and three degraded copies at 44.1 or 48 kHz
python prepare_data.py work/tracks.csv work/corpus --workers 6

# 3a. Optional, Windows: a fourth copy through Apple's own AAC encoder
python add_apple_aac.py work/corpus --qaac C:/tools/qaac/x64/qaac64.exe

# 4. Leave out masters with nothing above 22 kHz, or with a 1-bit transfer's rising noise
python screen_tracks.py work/tracks.csv work/corpus

# 5. Train. Stops and resumes from work/runs/upscaler/last.pt; run the same command again
python -m upscaler.train --manifest work/corpus/manifest-screened.csv --batch 12 --gan-batch 4 --pretrain-steps 30000 --gan-steps 24000

# 6. Measure it on the songs kept out of training, codec by codec
python -m upscaler.evaluate --manifest work/corpus/manifest-screened.csv --checkpoint work/runs/upscaler/last.pt

# 7. Export, and install beside the player
python -m upscaler.export --checkpoint work/runs/upscaler/last.pt --out work/export/neural-upscaler.onnx
```

Copy `neural-upscaler.onnx` and `neural-upscaler.json` into the `models` folder next to `FUPlayer.exe`,
or into `%APPDATA%\FUPlayer\models`. DSP Studio then shows what it found under **Neural upscaler**, and
`FUPlayer.exe --diagnostics` writes it down.

On a graphics card with less than 4 GB, lower `--batch` and `--gan-batch`; on one with more, raise
them. The adversarial phase is what runs out of memory first. `--init-from` starts a new schedule from
another run's weights, which is how the reference model was fine-tuned when its input changed.

`make_test_fixture.py` writes the numbers the player's own tests check its implementation against; it
is not part of training a model.

## What the steps do

**The corpus.** For every track, a 150-second excerpt of the master at its training rate (176.4 and
192 kHz are halved), then three copies: the master taken to 44.1 or 48 kHz with SoX's resampler and, in
six copies out of seven, through a lossy encoder and back. The mix is MP3 from 96 to 320 kbit/s and V0/V2,
FFmpeg's AAC and Windows' AAC from 128 to 256 kbit/s, Vorbis q3 to q7 and Opus. A decoded copy is lined
up with the original by cross-correlation, because every encoder adds its own delay, and a copy half a
sample late teaches the network to move everything by half a sample. Everything is stored as float16.

**The split.** About 6 percent of songs are kept out of training by title, so that a single, a
remaster and an album version of one song are never on both sides.

**The network.** It never sees a waveform. The degraded copy is upsampled with a Kaiser-windowed sinc
and cut into Fourier frames of 2,048 points at a hop of 512; the network reads their log magnitude,
floored 85 dB below each frame's loudest bin, and answers, per bin, with a complex gain for what the
input carries, a magnitude and a phase for what it lacks, and a crossover between the two. Eight
ConvNeXt blocks, about ten million parameters, and 272 ms of music seen either side of every frame. The
player repeats the same transform, the same interpolator and the same overlap-add, coefficient for
coefficient, and its tests hold it to the numbers PyTorch produces.

The floor matters more than it looks. Without it the network reads the float16 rounding of its own
corpus above a codec's cutoff, where a clean decode in the player has nothing, and learns to rely on it.

**Training.** Multi-resolution spectral loss (512 to 4,096 points, the band above 18 kHz counted twice)
with anti-wrapping phase losses first; then a multi-period waveform discriminator and a
multi-resolution spectral one are added, with feature matching. The spectral phase gets the level and
envelope right; the adversarial phase makes the texture of the band it writes behave like the texture of
a real one rather than like its average. Crops quieter than −70 dBFS are drawn again, and some lossless
crops are requantised to 16 bits with dither, which is what a CD rip is. Each phase keeps its own best
checkpoint, and the adversarial phase a snapshot every 4,000 steps: its best by spectral distance is not
necessarily the best to listen to, so evaluate the snapshots and choose.

## Whose music

A trained model carries no audio, but it was fitted to specific recordings, and what it writes above
22 kHz is its estimate of what those recordings had there. Keep models of music you do not have the
rights to to yourself.
