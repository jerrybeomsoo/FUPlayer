"""Looks for bins the network never writes: the narrow dead lines a spectrogram shows across a whole track.

A network fitted to references that disagreed about where the band ends can settle the disagreement by writing
nothing at all in a few bins. That shows in a spectrogram as horizontal lines, and it is a property of the weights
rather than of the music: the same bin indices go quiet at any rate and on any input.

The test feeds the player a piece of music brickwalled far below the band in question, with digital silence above
it, so everything the player writes up there is the network's own. Each bin's mean level over the file is compared
with the median of the 21 bins around it; a bin far below that is dead.

This finds dead bins and nothing milder. One file's fine structure moves a bin by a few decibels on its own, so the
threshold has to sit well above that, and a line 3 to 8 dB deep passes: session 33's model passed here with some forty
of them, plainly visible on a spectrogram with a 30 dB colour range. `restorer.bin_lines` is the test for lines. It
averages a checkpoint's output over many held-out copies, where the music's fine structure comes to nothing and a
line the weights draw on every input does not, and reports down to 2 dB.

    python -m restorer.dead_bins <restored.wav> [drop_db]
"""
from __future__ import annotations

import sys

import numpy as np
import soundfile as sf

N_FFT = 2048       # the restorer's own transform, so a bin here is a bin there
DROP_DB = 12.0


def main() -> None:
    path = sys.argv[1]
    drop = float(sys.argv[2]) if len(sys.argv) > 2 else DROP_DB
    x, fs = sf.read(path, dtype="float32", always_2d=True)
    mono = x.mean(axis=1).astype(np.float64)

    window = np.hanning(N_FFT)
    usable = (len(mono) // (N_FFT // 2)) * (N_FFT // 2)
    frames = np.lib.stride_tricks.sliding_window_view(mono[:usable], N_FFT)[:: N_FFT // 2]
    power = np.abs(np.fft.rfft(frames * window, axis=1)) ** 2
    total = power.sum(axis=1)
    mean = power[total >= np.median(total)].mean(axis=0)
    hz = np.fft.rfftfreq(N_FFT, 1.0 / fs)

    level = 10.0 * np.log10(mean + 1e-30)
    written = level > level.max() - 80.0
    dead = []
    for b in range(11, len(level) - 11):
        if hz[b] < 12_000 or not written[b - 11:b + 12].all():
            continue
        neighbours = np.median(np.concatenate([level[b - 11:b - 1], level[b + 2:b + 12]]))
        if level[b] < neighbours - drop:
            dead.append((b, hz[b], level[b] - neighbours))

    print(f"{path}")
    print(f"  {fs} Hz, bins {N_FFT // 2 + 1}, {len(dead)} bins more than {drop:.0f} dB under their neighbours above 12 kHz")
    for b, f, d in dead:
        print(f"    bin {b:4d}  {f / 1000:6.2f} kHz  {d:6.1f} dB")
    if not dead:
        print("    none: the written band is continuous")


if __name__ == "__main__":
    main()
