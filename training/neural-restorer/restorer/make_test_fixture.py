"""Writes the numbers the player's own tests hold its neural restorer to.

    python -m restorer.make_test_fixture ../../tests/FUPlayer.Core.Tests/Fixtures/neural-restorer

A network far too small to be of use (two blocks of sixteen channels), its output head pushed well away from passing
its input through, so a mistake anywhere in the player's chain shows up as a difference rather than hiding behind an
identity. Then, from PyTorch:

  left.f32, right.f32            1.5 seconds of tones and noise, different in each channel
  out48_left/right.f32           torch.stft, the whole network and torch.istft over all of it, flagged 48 kHz
  out44_left/right.f32           the same, flagged 44.1 kHz
  tiny.onnx                      the network between its features and its heads, exported as the player loads it
  shape.json                     sizes, the look-ahead, and the state the network carries

Little-endian float32 throughout. No music is involved.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path

import numpy as np
import torch

from .export import export
from .model import BINS, HOP, LOOKAHEAD, N_FFT, Restorer, istft, stft


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("out", type=Path)
    args = ap.parse_args()
    args.out.mkdir(parents=True, exist_ok=True)
    torch.manual_seed(20260917)

    model = Restorer(dim=16, intermediate=48, blocks=2).eval()
    with torch.no_grad():
        model.head.weight.normal_(0.0, 0.05)
        bias = model.head.bias.view(5, BINS)
        bias[0].normal_(0.0, 0.3)          # g: gains well away from unity
        bias[1].normal_(0.0, 0.5)          # theta: rotated phases
        bias[2].normal_(-4.0, 1.0)         # m: a generated component loud enough to matter
        bias[3].normal_(0.0, 1.0)          # phi
        bias[4].normal_(0.0, 1.5)          # u: crossovers all over the place
        model.rate.normal_(0.0, 0.5)       # a rate flag that changes the answer
        model.channel.normal_(0.0, 0.5)

    rate = 48_000
    samples = int(1.5 * rate)
    t = torch.arange(samples, dtype=torch.float64) / rate
    gen = torch.Generator().manual_seed(7)
    left = (0.3 * torch.sin(2 * torch.pi * 997 * t) + 0.05 * torch.sin(2 * torch.pi * 12_007 * t)
            + 0.01 * torch.randn(samples, dtype=torch.float64, generator=gen)).float()
    right = (0.25 * torch.sin(2 * torch.pi * 1_499 * t) + 0.04 * torch.sin(2 * torch.pi * 9_001 * t)
             + 0.01 * torch.randn(samples, dtype=torch.float64, generator=gen)).float()

    sl, sr = stft(left[None]), stft(right[None])
    outputs = {}
    with torch.no_grad():
        for name, flag in (("out48", 1.0), ("out44", 0.0)):
            ol, orr = model(sl, sr, torch.tensor([flag]))
            outputs[f"{name}_left"] = istft(ol, samples)[0]
            outputs[f"{name}_right"] = istft(orr, samples)[0]

    error = export(model, args.out / "tiny.onnx", check_seconds=1.0)
    print(f"streaming check: {error:.2e}")

    def write(name: str, values: torch.Tensor) -> None:
        np.asarray(values.numpy(), dtype="<f4").tofile(args.out / f"{name}.f32")

    write("left", left)
    write("right", right)
    for name, values in outputs.items():
        write(name, values)
    changed = float((outputs["out48_left"] - left).pow(2).mean() / left.pow(2).mean())
    print(f"the network changes the left channel by {10 * np.log10(changed):.1f} dB relative")

    (args.out / "shape.json").write_text(json.dumps({
        "samples": samples, "rate": rate, "n_fft": N_FFT, "hop": HOP, "bins": BINS, "lookahead_frames": LOOKAHEAD,
        "state_shapes": [list(s) for s in model.core_state_shapes()],
    }, indent=2))


if __name__ == "__main__":
    main()
