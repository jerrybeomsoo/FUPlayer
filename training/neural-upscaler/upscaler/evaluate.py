"""Scores a trained upscaler on the songs held out of training, codec by codec.

Every held-out track is measured in every degraded version the corpus has of it: the same stretch of
music, both channels, taken to the master's rate by the interpolator the network was trained behind.
Each band is scored twice, before and after the network, against the master:

  lsd    log-spectral distance, dB: how far the spectrum's shape is from the master's, frame by frame
  level  energy relative to the master's in that band, dB: 0 is right, -60 is nothing there at all
  motion how much each bin's level moves from frame to frame, as a fraction of how much the master's
         does: 1 is as lively as the master, 0.5 is a band smoothed to half its movement

The level column is the one that says whether a band was written at all and at what strength; the
distance says whether its shape resembles the master's. A band written at the right level with the
wrong shape scores a good level and a poor distance.
"""
from __future__ import annotations

import argparse
import csv
import math
from collections import defaultdict
from pathlib import Path

import numpy as np
import torch
import torchaudio

from . import data
from .model import N_FFT, Upscaler, istft, stft

BANDS = [("0-16k", 0.0, 16_000.0), ("16-22k", 16_000.0, 22_050.0), ("22k-nyq", 22_050.0, 1e9)]


def measure(pred: torch.Tensor, target: torch.Tensor, rate: int) -> dict[str, float]:
    p = stft(pred).abs().pow(2)
    t = stft(target).abs().pow(2)
    hz = torch.arange(p.shape[1], device=p.device) * (rate / N_FFT)
    loud = t.sum(dim=1) > 1e-6
    out = {}
    for name, lo, hi in BANDS:
        mask = (hz >= lo) & (hz < min(hi, rate / 2))
        pm, tm = p[:, mask, :], t[:, mask, :]
        diff = (10 * torch.log10(tm + 1e-10) - 10 * torch.log10(pm + 1e-10)).pow(2)
        per_frame = diff.mean(dim=1).sqrt()
        out[f"lsd_{name}"] = float(per_frame[loud].mean()) if loud.any() else float("nan")
        out[f"level_{name}"] = float(10 * torch.log10((pm.sum() + 1e-12) / (tm.sum() + 1e-12)))
        # Frame-to-frame movement of each bin's level, over the loud frames only.
        lp = 10 * torch.log10(pm[..., loud[0]] + 1e-10) if loud.dim() == 2 else 10 * torch.log10(pm + 1e-10)
        lt = 10 * torch.log10(tm[..., loud[0]] + 1e-10) if loud.dim() == 2 else 10 * torch.log10(tm + 1e-10)
        if lp.shape[-1] > 2:
            mp = torch.diff(lp, dim=-1).abs().mean()
            mt = torch.diff(lt, dim=-1).abs().mean()
            out[f"motion_{name}"] = float(mp / mt.clamp_min(1e-6))
        else:
            out[f"motion_{name}"] = float("nan")
    return out


@torch.no_grad()
def run_model(model: Upscaler, wave: torch.Tensor, chunk: int = 1024, context: int = 32) -> torch.Tensor:
    """The whole excerpt through the network, in overlapping stretches of frames so memory stays bounded."""
    spec = stft(wave)
    frames = spec.shape[-1]
    out = torch.empty_like(spec)
    for start in range(0, frames, chunk):
        lo = max(0, start - context)
        hi = min(frames, start + chunk + context)
        part = model(spec[..., lo:hi])
        out[..., start: min(frames, start + chunk)] = part[..., start - lo: start - lo + min(chunk, frames - start)]
    return istft(out, wave.shape[-1])


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--corpus", default="work/corpus")
    ap.add_argument("--manifest", default="", help="a track list other than the corpus's own manifest.csv")
    ap.add_argument("--checkpoint", default="work/runs/upscaler/best-gan.pt")
    ap.add_argument("--seconds", type=float, default=20.0, help="length of the stretch measured in each track")
    ap.add_argument("--tracks", type=int, default=0, help="measure only this many held-out tracks (0 = all)")
    ap.add_argument("--device", default="cuda" if torch.cuda.is_available() else "cpu")
    ap.add_argument("--csv", default="work/runs/upscaler/evaluation.csv")
    args = ap.parse_args()

    device = torch.device(args.device)
    state = torch.load(args.checkpoint, map_location="cpu", weights_only=False)
    model = Upscaler(dim=state.get("dim", 384), intermediate=state.get("dim", 384) * 3, blocks=state.get("blocks", 8))
    model.load_state_dict(state["model"])
    model.to(device).eval()
    print(f"{args.checkpoint}: step {state.get('step', '?')}", flush=True)

    _, held = data.split(data.load_manifest(Path(args.corpus), Path(args.manifest) if args.manifest else None))
    if args.tracks:
        held = held[: args.tracks]

    rows = []
    for n, track in enumerate(held, 1):
        y_all = np.load(track.y, mmap_mode="r")
        high = track.rate
        for path, label, low in track.variants:
            x_all = np.load(path, mmap_mode="r")
            g = math.gcd(low, high)
            step_low, step_high = low // g, high // g
            margin = 8192
            need_high = int(args.seconds * high)
            need_low = math.ceil((need_high + 2 * margin) * low / high) + 16
            last = min((x_all.shape[1] - need_low) // step_low, (y_all.shape[1] - need_high - 2 * margin) // step_high)
            if last <= 0:
                continue
            j = last // 2  # the middle of the excerpt, the same place in every version
            s_low, s_high = j * step_low, j * step_high
            x = torch.from_numpy(np.asarray(x_all[:, s_low: s_low + need_low], dtype=np.float32)).to(device)
            y = torch.from_numpy(np.asarray(y_all[:, s_high + margin: s_high + margin + need_high], dtype=np.float32)).to(device)
            up = torchaudio.functional.resample(x, low, high, lowpass_filter_width=64, rolloff=0.99,
                                                resampling_method="sinc_interp_kaiser", beta=14.769656459379492)
            up = up[:, margin: margin + need_high]
            out = run_model(model, up)
            before, after = measure(up, y, high), measure(out, y, high)
            row = {"track": track.title, "japanese": int(track.japanese), "codec": label, "low": low, "high": high}
            row.update({f"in_{k}": v for k, v in before.items()})
            row.update({f"out_{k}": v for k, v in after.items()})
            rows.append(row)
        print(f"[{n}/{len(held)}] {track.title}", flush=True)

    with open(args.csv, "w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=list(rows[0].keys()))
        writer.writeheader()
        writer.writerows(rows)

    by_codec: dict[str, list[dict]] = defaultdict(list)
    for r in rows:
        by_codec[r["codec"]].append(r)
    by_codec["ALL"] = rows

    print()
    print(f"{'codec':<20}{'n':>4} | {'LSD 0-16k':>13} {'LSD 16-22k':>13} {'LSD 22k+':>13} | "
          f"{'level 16-22k':>14} {'level 22k+':>14} | {'motion 22k+':>11}")
    for codec, items in by_codec.items():
        def mean(key: str) -> float:
            values = [r[key] for r in items if not math.isnan(r[key])]
            return sum(values) / max(1, len(values))
        print(f"{codec:<20}{len(items):>4} | "
              f"{mean('in_lsd_0-16k'):5.2f}->{mean('out_lsd_0-16k'):5.2f} "
              f"{mean('in_lsd_16-22k'):5.2f}->{mean('out_lsd_16-22k'):5.2f} "
              f"{mean('in_lsd_22k-nyq'):5.1f}->{mean('out_lsd_22k-nyq'):5.1f} | "
              f"{mean('in_level_16-22k'):6.1f}->{mean('out_level_16-22k'):5.1f} "
              f"{mean('in_level_22k-nyq'):6.1f}->{mean('out_level_22k-nyq'):5.1f} | "
              f"{mean('out_motion_22k-nyq'):11.2f}")
    print(f"\nrows written to {args.csv}")


if __name__ == "__main__":
    main()
