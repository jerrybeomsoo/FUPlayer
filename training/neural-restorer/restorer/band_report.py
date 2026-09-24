"""What a checkpoint puts back above a codec's low-pass, in the middle of the picture and at its sides.

Two things hide a failure here. The validation scores are energy-weighted, so they are dominated by the bands just
above 16 kHz where most coded streams still have something; and they are taken per channel, where a band written
into the side alone reads as a band. Music keeps most of its top octave in the middle, so a side-only band is
missing from the mix an analyser shows and from anything centred in the picture. This measures both, one codec at
a time, on masters that really have a band up there.

    python -m restorer.band_report <checkpoint.pt> [songs] [--all] [--per-codec]

`--all` keeps every held-out song instead of only those whose master has a real band above 16 kHz.
"""
from __future__ import annotations

import sys
from collections import defaultdict

import numpy as np
import torch

from . import data
from .model import Restorer
from .train import restore

SECONDS = 25
FROM = 20            # seconds into the song
WIDTH = 1000.0
REPORT_FROM = 14_000.0
LOW_FROM, LOW_TO = 8_000.0, 12_000.0
TOP_FROM, TOP_TO = 16_000.0, 20_000.0

# A master counts as full-band when its 16-20 kHz level is within this of its 8-12 kHz level.
TOP_WITHIN_DB = 45.0


def spectrum(x: np.ndarray, fs: int) -> tuple[np.ndarray, np.ndarray]:
    n = 16384
    w = np.hanning(n)
    acc = np.zeros(n // 2 + 1)
    k = 0
    for i in range(0, len(x) - n, n):
        acc += np.abs(np.fft.rfft(x[i:i + n] * w)) ** 2
        k += 1
    return acc / max(k, 1), np.fft.rfftfreq(n, 1.0 / fs)


def bands(power: np.ndarray, f: np.ndarray, fs: int) -> dict[int, float]:
    return {int(lo): 10 * np.log10(power[(f >= lo) & (f < lo + WIDTH)].sum() + 1e-30)
            for lo in np.arange(REPORT_FROM, fs / 2 - WIDTH + 1, WIDTH)}


def level(power: np.ndarray, f: np.ndarray, lo: float, hi: float) -> float:
    return 10 * np.log10(power[(f >= lo) & (f < hi)].sum() + 1e-30)


def parts(sig: np.ndarray) -> dict[str, np.ndarray]:
    return {"mid": (sig[0] + sig[1]) * 0.7071, "side": (sig[0] - sig[1]) * 0.7071}


def main() -> None:
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    every = "--all" in sys.argv
    per_codec = "--per-codec" in sys.argv
    checkpoint = args[0]
    wanted = int(args[1]) if len(args) > 1 else 14
    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    state = torch.load(checkpoint, map_location="cpu", weights_only=False)
    model = Restorer(dim=state.get("dim", 256), blocks=state.get("blocks", 8)).to(device).eval()
    model.load_state_dict(state["model"])
    print(f"{checkpoint}, step {state.get('step')}, "
          f"{'every held-out song' if every else f'masters with a band within {TOP_WITHIN_DB:.0f} dB of 8-12 kHz'}")

    _, held = data.split(data.load())
    for rate in (44_100, 48_000):
        rows: dict[tuple[str, str, str], list[dict[int, float]]] = defaultdict(list)
        songs = 0
        for item in [i for i in held if i.rate == rate]:
            if songs >= wanted:
                break
            y = np.load(item.reference).astype(np.float32)[:, rate * FROM: rate * (FROM + SECONDS)]
            if y.shape[1] < rate * SECONDS or np.sqrt(np.mean(np.square(y, dtype=np.float64))) < 3e-3:
                continue
            p_mix, f = spectrum(y.mean(axis=0), rate)
            if not every and level(p_mix, f, TOP_FROM, TOP_TO) < level(p_mix, f, LOW_FROM, LOW_TO) - TOP_WITHIN_DB:
                continue
            songs += 1
            base = {name: bands(spectrum(part, rate)[0], f, rate) for name, part in parts(y).items()}
            for path, label in item.variants:
                x = np.load(path).astype(np.float32)[:, rate * FROM: rate * (FROM + SECONDS)]
                with torch.no_grad():
                    got, _, _ = restore(model, torch.from_numpy(x).unsqueeze(0).to(device),
                                        torch.tensor([rate], device=device))
                for tag, sig in (("coded", x), ("restored", got.squeeze(0).cpu().numpy())):
                    for name, part in parts(sig).items():
                        b = bands(spectrum(part, rate)[0], f, rate)
                        rows[(label, tag, name)].append({k: v - base[name][k] for k, v in b.items()})

        if not rows:
            print(f"\n{rate} Hz: no song matched")
            continue

        keys = sorted(next(iter(rows.values()))[0])
        labels = sorted({label for label, _, _ in rows})
        print(f"\n{rate} Hz, {songs} songs, median dB against the master (coded -> restored)")
        print(f"{'codec':>16} {'':>4} {'n':>3} " + " ".join(f"{k // 1000:>11}" for k in keys))
        for label in (labels if per_codec else []) + ["all"]:
            got = [label] if label != "all" else labels
            for name in ("mid", "side"):
                n = sum(len(rows[(g, 'coded', name)]) for g in got)
                line = f"{label if name == 'mid' else '':>16} {name:>4} {n:>3} "
                for k in keys:
                    c = np.median([r[k] for g in got for r in rows[(g, "coded", name)]])
                    o = np.median([r[k] for g in got for r in rows[(g, "restored", name)]])
                    line += f" {c:5.1f}->{o:5.1f}"
                print(line)


if __name__ == "__main__":
    main()
