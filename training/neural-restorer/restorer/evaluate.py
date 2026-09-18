"""Measures a trained restorer on the held-out songs, codec by codec and rate by rate.

    python -m restorer.evaluate [--checkpoint runs/restorer/best.pt] [--out runs/restorer/eval.csv]

For every coded copy of every held-out song, four five-second stretches spread over its fifty seconds, each restored
whole on the graphics device; the first half second and the last quarter of each are left out of the measurement, so
every frame measured had its full context. Input (the decoded stream) and output are compared with the master:

- log-spectral distance below 4 kHz, from 4 to 12 kHz and from 12 kHz to Nyquist, dB;
- the level above 16 kHz against the master's over the stretch, dB;
- the side channel's distance from the master's, the mean over 500 Hz bands from 4 kHz of |level difference|, dB;
- the noise-to-mask ratio per frame as PEAQ forms it: noise energy over masking threshold in each Bark band, averaged
  over the bands and then expressed in dB (below 0 dB is inaudible on average), and the share of frames in which any
  band's noise is 1.5 dB or more above its threshold. Noise is the magnitude difference from the master, so a band the
  network writes is not charged for having a phase of its own. The threshold is the one the masked loss uses.

The lossless copies of the same songs (the master against itself) say how much the network changes what it should
leave alone. Everything goes to a CSV, one row per stretch, and a table by codec and rate.
"""
from __future__ import annotations

import argparse
import csv
import math
from collections import defaultdict
from pathlib import Path

import numpy as np
import torch

from . import data, losses
from .model import HOP, N_FFT, Restorer, stft
from .train import level_above_db, lsd_by_band, restore

SECONDS = 5.0
SKIP_START = 0.5
SKIP_END = 0.25
STRETCHES = 4


def nmr(spec_out: torch.Tensor, spec_target: torch.Tensor, rate: int) -> tuple[float, float]:
    """Mean total NMR over loud frames in dB, and the share of loud frames with a band at or above 1.5 dB, per cent."""
    member, _, _, _, _ = losses._masking_tables(rate, spec_out.device)
    target_power = spec_target.abs().pow(2)
    threshold = losses.masking_threshold(target_power, rate)
    noise = (spec_out.abs() - spec_target.abs()).pow(2)
    noise_band = torch.einsum("kb,nbt->nkt", member, noise)
    threshold_band = torch.einsum("kb,nbt->nkt", member, threshold)
    ratio = noise_band / threshold_band.clamp_min(1e-30)                    # [N, bands, T]
    loud = target_power.sum(dim=1) > target_power.sum(dim=1).amax(dim=1, keepdim=True) * 1e-4
    total_db = 10.0 * torch.log10(ratio.mean(dim=1) + 1e-12)
    worst_db = 10.0 * torch.log10(ratio.amax(dim=1) + 1e-12)
    return float(total_db[loud].mean()), float(100.0 * (worst_db[loud] >= 1.5).float().mean())


def side_distance_db(x: torch.Tensor, y: torch.Tensor, rate: int) -> float:
    sx, sy = stft((x[:, 0] - x[:, 1]) * 0.7071), stft((y[:, 0] - y[:, 1]) * 0.7071)
    return 10.0 * float(losses.band_level_loss(sx, sy, torch.tensor([rate], device=x.device)))


def measure(x: torch.Tensor, y: torch.Tensor, rate: int) -> dict[str, float]:
    """x, y [1, 2, samples], trimmed to the measured part already."""
    spec_x = torch.cat([stft(x[:, 0]), stft(x[:, 1])])
    spec_y = torch.cat([stft(y[:, 0]), stft(y[:, 1])])
    px, py = spec_x.abs().pow(2), spec_y.abs().pow(2)
    out = {f"lsd_{k}": v for k, v in lsd_by_band(px, py, rate).items()}
    out["above16k_db"] = level_above_db(px, py, rate)
    out["side_db"] = side_distance_db(x, y, rate)
    out["nmr_db"], out["disturbed_pct"] = nmr(spec_x, spec_y, rate)
    return out


def family(label: str) -> str:
    base = label.replace("-mix48", "")
    name = {"opus": "Opus", "aac": "AAC (FFmpeg)", "apple": "AAC (Apple)", "aacmf": "AAC (Windows)", "mp3": "MP3",
            "vorbis": "Vorbis"}[base.split("-")[0]]
    return name + (" via 48 kHz mixer" if label.endswith("-mix48") else "")


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--checkpoint", default="work/runs/restorer/best.pt")
    ap.add_argument("--out", default="")
    args = ap.parse_args()

    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    state = torch.load(args.checkpoint, map_location=device, weights_only=False)
    dim, blocks = state.get("dim", 256), state.get("blocks", 8)
    model = Restorer(dim=dim, intermediate=dim * 3, blocks=blocks).to(device).eval()
    model.load_state_dict(state["model"])
    out_path = Path(args.out) if args.out else Path(args.checkpoint).with_name(f"eval-{Path(args.checkpoint).stem}.csv")

    _, held = data.split(data.load())
    print(f"{len(held)} held-out songs, checkpoint step {state.get('step')}", flush=True)
    rows = []
    with torch.no_grad():
        for n, item in enumerate(held, 1):
            reference = np.load(item.reference, mmap_mode="r")
            rate = item.rate
            length = int(SECONDS * rate)
            skip_start, skip_end = int(SKIP_START * rate), int(SKIP_END * rate)
            starts = np.linspace(0, reference.shape[1] - length, STRETCHES).astype(int)
            copies = [(path, label) for path, label in item.variants] + [(item.reference, "lossless")]
            for path, label in copies:
                coded = np.load(path, mmap_mode="r")
                for start in starts:
                    y = torch.from_numpy(np.asarray(reference[:, start:start + length], dtype=np.float32)).to(device)[None]
                    x = torch.from_numpy(np.asarray(coded[:, start:start + length], dtype=np.float32)).to(device)[None]
                    if float(y.pow(2).mean()) < 1e-7:
                        continue
                    restored, _, _ = restore(model, x, torch.tensor([rate], device=device))
                    keep = slice(skip_start, length - skip_end)
                    xi, yo, yt = x[..., keep], restored[..., keep], y[..., keep]
                    row = {"id": item.id, "title": item.title, "label": label, "rate": rate, "start": int(start)}
                    if label == "lossless":
                        change = (yo - xi).pow(2).sum() / xi.pow(2).sum().clamp_min(1e-12)
                        row["change_db"] = float(10.0 * torch.log10(change + 1e-20))
                    else:
                        for tag, signal in (("in", xi), ("out", yo)):
                            for key, value in measure(signal, yt, rate).items():
                                row[f"{tag}_{key}"] = value
                    rows.append(row)
            if n % 10 == 0:
                print(f"  {n}/{len(held)}", flush=True)

    fields = sorted({k for r in rows for k in r}, key=lambda k: (k not in ("id", "title", "label", "rate", "start"), k))
    with out_path.open("w", encoding="utf-8", newline="") as f:
        writer = csv.DictWriter(f, fieldnames=fields)
        writer.writeheader()
        writer.writerows(rows)

    metrics = [("lsd_0-4k", "LSD <4k"), ("lsd_4-12k", "LSD 4-12k"), ("lsd_12k-nyq", "LSD >12k"),
               ("above16k_db", ">16k level"), ("side_db", "side"), ("nmr_db", "NMR"), ("disturbed_pct", "disturbed %")]

    def table(group_of) -> None:
        groups: dict[str, list[dict]] = defaultdict(list)
        for r in rows:
            if r["label"] != "lossless":
                groups[group_of(r)].append(r)
        head = f"{'':30s} {'n':>4s}  " + "  ".join(f"{name:>15s}" for _, name in metrics)
        print(head)
        for group in sorted(groups):
            rs = groups[group]
            cells = []
            for key, _ in metrics:
                a = np.nanmean([r[f"in_{key}"] for r in rs])
                b = np.nanmean([r[f"out_{key}"] for r in rs])
                cells.append(f"{a:6.2f} -> {b:6.2f}")
            print(f"{group:30s} {len(rs):4d}  " + "  ".join(f"{c:>15s}" for c in cells))

    print("\nBy codec and rate (input -> restored):")
    table(lambda r: r["label"])
    print("\nBy codec:")
    table(lambda r: family(r["label"]))
    print("\nAll coded copies:")
    table(lambda r: "all")
    lossless = [r["change_db"] for r in rows if r["label"] == "lossless"]
    print(f"\nLossless copies: {len(lossless)} stretches, changed by {np.mean(lossless):.1f} dB on average, "
          f"{max(lossless):.1f} dB at most (relative to the signal)")
    print(f"written {out_path}", flush=True)
    _ = (HOP, N_FFT, math)


if __name__ == "__main__":
    main()
