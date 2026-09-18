"""Checks the player's restorer against PyTorch on a real coded copy of a held-out song, with a real model.

    python -m restorer.player_check --checkpoint runs/restorer/best.pt --onnx export/neural-restorer.onnx --cli <fuplayer-cli.exe>

Writes the coded copy as a 32-bit float WAV (6 dB down, so the limiter the restorer always runs with has nothing to
do), renders it through fuplayer-cli at the source rate with the restorer on and no dither, and compares the result
with the whole-signal PyTorch pass after lining the two up. Also reports what both did against the master.
"""
from __future__ import annotations

import argparse
import subprocess
import tempfile
from pathlib import Path

import numpy as np
import soundfile as sf
import torch

from . import data
from .model import Restorer
from .train import restore


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--checkpoint", required=True)
    ap.add_argument("--onnx", required=True)
    ap.add_argument("--cli", required=True)
    ap.add_argument("--label", default="opus-128")
    ap.add_argument("--seconds", type=float, default=20.0)
    args = ap.parse_args()

    _, held = data.split(data.load())
    item, path = next((i, p) for i in held for p, label in i.variants if label == args.label)
    rate = item.rate
    frames = int(args.seconds * rate)
    coded = np.asarray(np.load(path, mmap_mode="r")[:, :frames], dtype=np.float32) * 0.5
    master = np.asarray(np.load(item.reference, mmap_mode="r")[:, :frames], dtype=np.float32) * 0.5
    print(f"{item.title} ({args.label}, {rate} Hz, {args.seconds:.0f} s)")

    state = torch.load(args.checkpoint, map_location="cpu", weights_only=False)
    model = Restorer(dim=state.get("dim", 256), intermediate=state.get("dim", 256) * 3, blocks=state.get("blocks", 8))
    model.load_state_dict(state["model"])
    model.eval()
    with torch.no_grad():
        expected, _, _ = restore(model, torch.from_numpy(coded)[None], torch.tensor([rate]))
    expected = expected[0].numpy()

    with tempfile.TemporaryDirectory(prefix="fuml-player-check-") as tmp:
        source = Path(tmp) / "coded.wav"
        sf.write(source, coded.T, rate, subtype="FLOAT")
        out_dir = Path(tmp) / "out"
        result = subprocess.run([args.cli, "render", str(source), "--out", str(out_dir), "--mode", "pcm", "--rate", str(rate),
                                 "--bits", "32", "--dither", "none", "--neural-restore", "--restorer", args.onnx,
                                 "--source-type", "lossy"], capture_output=True, text=True)
        print(result.stdout.strip().splitlines()[-1] if result.stdout.strip() else result.stderr[-500:])
        rendered, out_rate = sf.read(next(out_dir.glob("*.wav")), dtype="float64", always_2d=True)
        rendered = rendered.T
    assert out_rate == rate

    # Line the render up with PyTorch's answer: the player's delay, plus the rate filter's own.
    search = 20_000
    a, b = expected[0, 20_000:120_000], rendered[0]
    n = 1 << int(np.ceil(np.log2(a.size + search + b.size)))
    corr = np.fft.irfft(np.fft.rfft(b, n) * np.conj(np.fft.rfft(a, n)), n)[20_000: 20_000 + search]
    lag = int(np.argmax(corr))
    aligned = rendered[:, lag: lag + frames]
    keep = slice(rate // 2, frames - rate // 2)
    diff = aligned[:, keep] - expected[:, keep]
    rel = 10 * np.log10(np.sum(diff ** 2) / np.sum(expected[:, keep] ** 2))
    print(f"player delay {lag} samples; player against PyTorch: {rel:.1f} dB")

    def error_db(signal: np.ndarray) -> float:
        d = signal[:, keep] - master[:, keep]
        return float(10 * np.log10(np.sum(d ** 2) / np.sum(master[:, keep] ** 2)))

    print(f"against the master: coded {error_db(coded):.2f} dB, PyTorch {error_db(expected):.2f} dB, "
          f"player {error_db(aligned):.2f} dB (waveform error, relative)")


if __name__ == "__main__":
    main()
