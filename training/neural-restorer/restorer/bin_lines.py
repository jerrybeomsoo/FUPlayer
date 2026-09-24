"""Finds lines a checkpoint draws in the band it writes: bins it writes consistently less than their neighbours.

The network reads a whole frame into one vector and writes every bin back out through its own row of the head, so
nothing ties one bin's level to the next one's. A bin whose row came out a little low is low on every input, and a
spectrogram shows it as a dark line across every track. The losses hardly see it: one bin 10 dB low in a 500 Hz band
moves that band's energy by about 0.4 dB, and a per-bin loss on a band that has to be invented is mostly texture.

This runs held-out coded copies whose codec left the top of the band empty, measures each output bin's mean level
over the stretch against the median of the 21 around it, and averages that over the copies. A line is a bin that
comes out low on most of them, not on one: the music's own structure moves around, the weights' does not.

    python -m restorer.bin_lines <checkpoint.pt> [copies] [--from 16] [--threshold 2]
"""
from __future__ import annotations

import argparse

import numpy as np
import torch

from . import data
from .model import N_FFT, Restorer, stft
from .train import restore

SECONDS = 12


def per_bin_db(spec: torch.Tensor) -> np.ndarray:
    """Mean power per bin over the frames, dB. spec [bins, frames] complex."""
    return (10.0 * torch.log10(spec.abs().pow(2).mean(dim=-1) + 1e-30)).cpu().numpy()


def relative_to_neighbours(level: np.ndarray, width: int = 10) -> np.ndarray:
    out = np.zeros_like(level)
    for b in range(len(level)):
        out[b] = level[b] - np.median(level[max(0, b - width): b + width + 1])
    return out


def cutoff_bin(level_in: np.ndarray, rate: int) -> int:
    """First bin of the top band where the coded input is more than 40 dB under its 8-12 kHz level."""
    hz = np.arange(len(level_in)) * rate / N_FFT
    ref = np.median(level_in[(hz >= 8_000) & (hz < 12_000)])
    above = np.where((hz >= 12_000) & (level_in < ref - 40.0))[0]
    return int(above[0]) if len(above) else len(level_in)


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("checkpoint")
    ap.add_argument("copies", type=int, nargs="?", default=24)
    ap.add_argument("--from", dest="from_khz", type=float, default=16.0)
    ap.add_argument("--threshold", type=float, default=2.0, help="report bins whose mean dip is at least this, dB")
    args = ap.parse_args()

    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    state = torch.load(args.checkpoint, map_location="cpu", weights_only=False)
    model = Restorer(dim=state.get("dim", 256), blocks=state.get("blocks", 8)).to(device).eval()
    model.load_state_dict(state["model"])
    print(f"{args.checkpoint}, step {state.get('step')}")

    _, held = data.split(data.load())
    for rate in (44_100, 48_000):
        dips: list[np.ndarray] = []
        written: list[np.ndarray] = []
        for item in [i for i in held if i.rate == rate]:
            if len(dips) >= args.copies:
                break
            for path, label in item.variants:
                if len(dips) >= args.copies:
                    break
                x = np.load(path).astype(np.float32)[:, rate * 20: rate * (20 + SECONDS)]
                if x.shape[1] < rate * SECONDS or np.sqrt(np.mean(np.square(x, dtype=np.float64))) < 3e-3:
                    continue
                xt = torch.from_numpy(x).unsqueeze(0).to(device)
                with torch.no_grad():
                    y, _, _ = restore(model, xt, torch.tensor([rate], device=device))
                mid_in = stft((xt[:, 0] + xt[:, 1]) * 0.7071)[0]
                mid_out = stft((y[:, 0] + y[:, 1]) * 0.7071)[0]
                lin, lout = per_bin_db(mid_in), per_bin_db(mid_out)
                cut = cutoff_bin(lin, rate)
                if cut >= len(lin) - 20:
                    continue           # the codec kept the band: nothing here is the network's own
                rel = relative_to_neighbours(lout)
                mask = np.zeros_like(rel, dtype=bool)
                mask[cut + 4:] = True  # a few bins clear of the codec's own edge
                dips.append(np.where(mask, rel, np.nan))
                written.append(mask)

        if not dips:
            print(f"\n{rate} Hz: no copy with an emptied top band")
            continue
        stack = np.stack(dips)
        counted = np.sum(~np.isnan(stack), axis=0)
        mean = np.nanmean(np.where(counted[None, :] > 0, stack, 0.0), axis=0)
        low_share = np.nanmean(stack < -3.0, axis=0)
        hz = np.arange(stack.shape[1]) * rate / N_FFT
        start = int(np.ceil(args.from_khz * 1000 * N_FFT / rate))
        rows = [(b, hz[b], mean[b], low_share[b], counted[b]) for b in range(start, stack.shape[1] - 1)
                if counted[b] >= max(3, len(dips) // 3) and mean[b] <= -args.threshold]
        rms = float(np.sqrt(np.nanmean(np.where(counted[start:-1] >= 3, mean[start:-1], np.nan) ** 2)))
        print(f"\n{rate} Hz: {len(dips)} copies with an emptied top band; bin-to-bin roughness (rms of the mean "
              f"dip, {args.from_khz:.0f} kHz up) {rms:.2f} dB; {len(rows)} bins at least {args.threshold} dB low on "
              f"average")
        print(f"{'bin':>6} {'kHz':>8} {'mean dip':>9} {'low on':>8} {'copies':>7}")
        for b, f, m, share, n in rows:
            print(f"{b:6d} {f / 1000:8.3f} {m:8.1f}  {100 * share:6.0f} % {n:7d}")


if __name__ == "__main__":
    main()
