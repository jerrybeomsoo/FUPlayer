"""Where the level of the written band goes wrong on lossless copies, by anti-alias filter and by band.

A lossless 44.1 or 48 kHz copy of each master is made with several filters, interpolated back as the
player does, put through the network flagged lossless, and the level of the output and of the input
against the master is printed band by band.
"""
from __future__ import annotations

import argparse
import math
from collections import defaultdict
from pathlib import Path

import numpy as np
import torch
import torchaudio

from upscaler import data
from upscaler.evaluate import run_model
from upscaler.model import N_FFT, Upscaler, stft

BETA = 14.769656459379492
EDGES = [16_000, 20_000, 21_000, 22_000, 23_000, 24_000, 26_000, 30_000, 36_000, 44_100]
FILTERS = [(0.99, 64), (0.95, 64), (0.91, 64), (0.87, 16)]


def band_power(wave: torch.Tensor, rate: int) -> list[float]:
    p = stft(wave).abs().pow(2)
    hz = torch.arange(p.shape[1], device=p.device) * (rate / N_FFT)
    return [float(p[:, (hz >= lo) & (hz < hi), :].sum()) for lo, hi in zip(EDGES[:-1], EDGES[1:])]


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--checkpoint", required=True)
    ap.add_argument("--corpus", default="work/corpus")
    ap.add_argument("--manifest", default="work/corpus/manifest-screened.csv")
    ap.add_argument("--split", choices=("held", "train"), default="held")
    ap.add_argument("--tracks", type=int, default=0)
    ap.add_argument("--seconds", type=float, default=8.0)
    ap.add_argument("--flag", type=float, default=0.0, help="0 lossless, 1 lossy")
    args = ap.parse_args()

    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    state = torch.load(args.checkpoint, map_location="cpu", weights_only=False)
    model = Upscaler(dim=state.get("dim", 384), intermediate=state.get("dim", 384) * 3, blocks=state.get("blocks", 8))
    model.load_state_dict(state["model"], strict=False)
    model.to(device).eval()

    train, held = data.split(data.load_manifest(Path(args.corpus), Path(args.manifest)))
    tracks = train if args.split == "train" else held
    if args.tracks:
        tracks = tracks[: args.tracks]

    sums: dict[tuple, list[list[float]]] = defaultdict(list)
    for n, track in enumerate(tracks, 1):
        high = track.rate
        low = high // 2
        y_all = np.load(track.y, mmap_mode="r")
        margin = 8192
        need = int(args.seconds * high)
        mid = (y_all.shape[1] // 2) // 2 * 2
        wide = torch.from_numpy(np.asarray(y_all[:, mid - 2 * margin: mid + need + 2 * margin], dtype=np.float32)).to(device)
        y = wide[:, 2 * margin: 2 * margin + need]
        master = band_power(y, high)
        for rolloff, width in FILTERS:
            x = torchaudio.functional.resample(wide.double(), high, low, lowpass_filter_width=width, rolloff=rolloff,
                                               resampling_method="sinc_interp_kaiser", beta=BETA).float()
            up = torchaudio.functional.resample(x, low, high, lowpass_filter_width=64, rolloff=0.99,
                                                resampling_method="sinc_interp_kaiser", beta=BETA)
            up = up[:, 2 * margin: 2 * margin + need]
            out = run_model(model, up, args.flag)
            key = (high, rolloff, width)
            sums[key].append([10 * math.log10((a + 1e-12) / (m + 1e-12)) for a, m in zip(band_power(up, high), master)]
                             + [10 * math.log10((b + 1e-12) / (m + 1e-12)) for b, m in zip(band_power(out, high), master)])
        print(f"[{n}/{len(tracks)}] {track.title}", flush=True)

    names = [f"{lo // 1000}-{hi // 1000}k" for lo, hi in zip(EDGES[:-1], EDGES[1:])]
    k = len(names)
    print()
    print(f"level against the master, dB, mean over tracks (flag {args.flag:g}, {args.split})")
    print(f"{'source':<30}" + "".join(f"{n:>9}" for n in names))
    for (high, rolloff, width), rows in sorted(sums.items()):
        a = np.mean(np.array(rows), axis=0)
        m = np.median(np.array(rows), axis=0)
        label = f"{high // 2000}k r{rolloff} w{width}"
        print(f"{label + ' input':<30}" + "".join(f"{v:9.1f}" for v in a[:k]))
        print(f"{label + ' output':<30}" + "".join(f"{v:9.1f}" for v in a[k:]))
        print(f"{'  median output, n=' + str(len(rows)):<30}" + "".join(f"{v:9.1f}" for v in m[k:]))


if __name__ == "__main__":
    main()
