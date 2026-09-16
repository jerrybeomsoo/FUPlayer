"""Leaves out the high-resolution masters that would teach the wrong thing above 22 kHz.

    python screen_tracks.py work/tracks.csv work/corpus [--workers 3] [--ffmpeg <path>]

Writes work/corpus/screen.csv, the measurements, and work/corpus/manifest-screened.csv, the corpus
manifest without the tracks left out; train and evaluate with --manifest pointing at the latter.

Two kinds of high-resolution file teach the wrong thing. A CD-rate master resampled and sold at a higher
rate has nothing above 22 kHz but the resampler's stopband, and teaches the network to write nothing.
A transfer from a 1-bit source carries the modulator's noise, which rises with frequency, and teaches
it to write hiss. Both show in the spectrum of the excerpt the corpus holds, decoded afresh in float32 so
that the corpus's own float16 rounding cannot pass for music, averaged over its louder half:

  flat      less than 13 dB fall from 8-16 kHz to 24-32 kHz: noise, not a roll-off
  empty     more than 70 dB fall over the same span: nothing up there at all
  rising    32-40 kHz louder than 24-32 kHz by more than 3 dB, or an album whose tracks rise on median

On the reference library nothing was empty; four albums of 88.2 kHz transfers from a 1-bit source rose
by 7 to 13 dB on median, and 45 of 381 tracks were left out.
"""
from __future__ import annotations

import argparse
import csv
import hashlib
import os
import shutil
import subprocess
import tempfile
from collections import defaultdict
from concurrent.futures import ProcessPoolExecutor, as_completed
from pathlib import Path

import numpy as np
import soundfile as sf

SOXR = "aresample=resampler=soxr:precision=28"
BANDS = {"b8_16k": (8000, 16000), "b24_32k": (24000, 32000), "b32_40k": (32000, 40000)}


def measure(row: dict, rate: int, ffmpeg: str) -> dict:
    duration = float(row["duration"] or 0.0)
    start = max(0.0, (duration - 150.0) / 2.0)
    length = min(150.0, duration)
    with tempfile.TemporaryDirectory(prefix="fuml-screen-") as tmp:
        wav = os.path.join(tmp, "m.wav")
        subprocess.run([ffmpeg, "-hide_banner", "-loglevel", "error", "-y", "-ss", f"{start:.3f}", "-t", f"{length:.3f}",
                        "-i", row["path"], "-af", SOXR, "-ar", str(rate), "-ac", "2", "-c:a", "pcm_f32le", wav], check=True)
        x, _ = sf.read(wav, dtype="float32", always_2d=True)
    mono = x.mean(axis=1).astype(np.float64)
    n = 8192
    frames = np.lib.stride_tricks.sliding_window_view(mono[: (len(mono) // n) * n], n)[:: n // 2]
    power = np.abs(np.fft.rfft(frames * np.hanning(n), axis=1)) ** 2
    total = power.sum(axis=1)
    mean = power[total >= np.median(total)].mean(axis=0)
    hz = np.fft.rfftfreq(n, 1.0 / rate)
    levels = {name: float(10 * np.log10(mean[(hz >= lo) & (hz < min(hi, rate / 2))].mean() + 1e-30))
              for name, (lo, hi) in BANDS.items()}
    return {"fall": levels["b8_16k"] - levels["b24_32k"], "rise": levels["b32_40k"] - levels["b24_32k"]}


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("tracks", type=Path)
    ap.add_argument("corpus", type=Path)
    ap.add_argument("--workers", type=int, default=3)
    ap.add_argument("--ffmpeg", default=shutil.which("ffmpeg") or "ffmpeg")
    args = ap.parse_args()

    sources = {hashlib.sha1(r["path"].encode("utf-8")).hexdigest()[:12]: r
               for r in csv.DictReader(args.tracks.open(encoding="utf-8"))}
    manifest = list(csv.DictReader((args.corpus / "manifest.csv").open(encoding="utf-8")))

    measured = {}
    with ProcessPoolExecutor(max_workers=args.workers) as pool:
        futures = {pool.submit(measure, sources[r["id"]], int(r["rate"]), args.ffmpeg): r for r in manifest}
        for i, future in enumerate(as_completed(futures), 1):
            r = futures[future]
            try:
                measured[r["id"]] = future.result()
            except Exception as ex:  # noqa: BLE001 - an unreadable master is left out, not fatal
                print(f"  failed {r['title'][:30]}: {ex}", flush=True)
            if i % 25 == 0:
                print(f"  {i}/{len(manifest)}", flush=True)

    by_album = defaultdict(list)
    for r in manifest:
        if r["id"] in measured:
            by_album[(r["artist"], r["album"])].append(measured[r["id"]]["rise"])

    kept, reasons, report = [], defaultdict(int), []
    for r in manifest:
        m = measured.get(r["id"])
        if m is None:
            reason = "unreadable"
        elif np.median(by_album[(r["artist"], r["album"])]) > 0.0:
            reason = "album rises above 32 kHz"
        elif m["rise"] > 3.0:
            reason = "rises above 32 kHz"
        elif m["fall"] < 13.0:
            reason = "flat to 32 kHz"
        elif m["fall"] > 70.0:
            reason = "empty above 24 kHz"
        else:
            reason = ""
        if reason:
            reasons[reason] += 1
        else:
            kept.append(r)
        report.append({"id": r["id"], "title": r["title"], "artist": r["artist"], "album": r["album"], "rate": r["rate"],
                       "fall_8_16_to_24_32": f"{m['fall']:.1f}" if m else "", "rise_24_32_to_32_40": f"{m['rise']:.1f}" if m else "",
                       "left_out": reason})

    with (args.corpus / "screen.csv").open("w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=list(report[0].keys()))
        w.writeheader()
        w.writerows(report)
    with (args.corpus / "manifest-screened.csv").open("w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=list(manifest[0].keys()))
        w.writeheader()
        w.writerows(kept)
    print(f"kept {len(kept)} of {len(manifest)}; left out {dict(reasons)}", flush=True)


if __name__ == "__main__":
    main()
