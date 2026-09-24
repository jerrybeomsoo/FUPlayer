"""Measures where each high-resolution file's spectrum really ends, so the corpus holds only full-band masters.

A reference for a 44.1 kHz model has to carry music up to 22.05 kHz: everything the network is taught to write at
the top of the band comes from it. Two kinds of file do not, and both are sold at 96 kHz. One is a CD-rate master
resampled up, which has nothing above about 20 kHz but the resampler's stopband. The other is a transfer from a
1-bit source, which has plenty up there and all of it is the modulator's noise.

So each file is decoded at its own rate, the loud half of its frames averaged, and two numbers taken: the frequency
where its spectrum drops for good (the cutoff), and the slope from 8-16 kHz to 24-32 kHz, which is what separates
music from modulator noise. A file is kept when its cutoff reaches 22.05 kHz and its slope is a fall.

    python -m restorer.screen <library.csv> <out.csv> [workers]
"""
from __future__ import annotations

import csv
import os
import subprocess
import sys
import tempfile
from concurrent.futures import ProcessPoolExecutor, as_completed

import numpy as np
import soundfile as sf

FF = os.environ.get("FFMPEG") or shutil.which("ffmpeg") or "ffmpeg"
RATES = {"88200", "96000", "176400", "192000"}
SECONDS = 150.0
FIELDS = ["path", "rate", "cutoff_hz", "b16_20k", "b20_22k", "b22_24k", "b24_32k", "slope_db", "keep", "reason",
          "genre", "title", "album", "artist", "duration"]


def spectrum(path: str, duration: float, rate: int) -> tuple[np.ndarray, np.ndarray]:
    """The mean spectrum of the loud half of the frames, from the middle of the file, at the file's own rate."""
    start = max(0.0, (duration - SECONDS) / 2.0)
    length = min(SECONDS, duration) if duration > 0 else SECONDS
    with tempfile.TemporaryDirectory(prefix="fuml-screen44-") as tmp:
        wav = os.path.join(tmp, "m.wav")
        subprocess.run([FF, "-hide_banner", "-loglevel", "error", "-y", "-ss", f"{start:.3f}", "-t", f"{length:.3f}",
                        "-i", path, "-map", "0:a:0", "-ac", "2", "-c:a", "pcm_f32le", wav], check=True)
        x, fs = sf.read(wav, dtype="float32", always_2d=True)
    mono = x.mean(axis=1).astype(np.float64)
    n = 8192
    usable = (len(mono) // n) * n
    if usable < n * 4:
        raise RuntimeError("too short")
    frames = np.lib.stride_tricks.sliding_window_view(mono[:usable], n)[:: n // 2]
    power = np.abs(np.fft.rfft(frames * np.hanning(n), axis=1)) ** 2
    total = power.sum(axis=1)
    loud = total >= np.median(total)
    return power[loud].mean(axis=0), np.fft.rfftfreq(n, 1.0 / fs)


def band(mean: np.ndarray, hz: np.ndarray, lo: float, hi: float) -> float:
    m = (hz >= lo) & (hz < hi)
    return 10.0 * np.log10(mean[m].sum() + 1e-30) if m.any() else float("nan")


def cutoff(mean: np.ndarray, hz: np.ndarray, reference: float) -> float:
    """The top of the band: where 500 Hz of spectrum falls 55 dB under the 8-16 kHz band and stays there."""
    edge = 14_000.0
    below = 0
    while edge + 500.0 <= hz[-1]:
        level = band(mean, hz, edge, edge + 500.0) - reference
        if level < -55.0:
            below += 1
            if below >= 2:
                return edge - 500.0 * (below - 1)
        else:
            below = 0
        edge += 500.0
    return hz[-1]


def measure(row: dict) -> dict:
    rate = int(row["rate"])
    mean, hz = spectrum(row["path"], float(row["duration"] or 0.0), rate)
    reference = band(mean, hz, 8_000, 16_000)
    top = cutoff(mean, hz, reference)
    b24_32 = band(mean, hz, 24_000, 32_000) if rate > 64_000 else float("nan")
    slope = b24_32 - reference if np.isfinite(b24_32) else float("nan")
    reason = ""
    if top < 22_050.0:
        reason = f"band ends at {top / 1000:.1f} kHz"
    elif np.isfinite(slope) and slope > -20.0:
        reason = f"noise above 24 kHz only {slope:.0f} dB under the midband: a 1-bit transfer"
    return {
        "path": row["path"], "rate": rate, "cutoff_hz": round(top),
        "b16_20k": round(band(mean, hz, 16_000, 20_000) - reference, 1),
        "b20_22k": round(band(mean, hz, 20_000, 22_050) - reference, 1),
        "b22_24k": round(band(mean, hz, 22_050, 24_000) - reference, 1),
        "b24_32k": round(b24_32 - reference, 1) if np.isfinite(b24_32) else "",
        "slope_db": round(slope, 1) if np.isfinite(slope) else "",
        "keep": "" if reason else "1", "reason": reason,
        "genre": row.get("genre", ""), "title": row.get("title", ""), "album": row.get("album", ""),
        "artist": row.get("artist", ""), "duration": row.get("duration", ""),
    }


def main() -> None:
    source, out = sys.argv[1], sys.argv[2]
    workers = int(sys.argv[3]) if len(sys.argv) > 3 else 6
    rows = [r for r in csv.DictReader(open(source, encoding="utf-8")) if r["rate"] in RATES and not r["error"]]
    print(f"{len(rows)} high-resolution files to screen", flush=True)

    done: list[dict] = []
    failed = 0
    with ProcessPoolExecutor(max_workers=workers) as pool:
        futures = {pool.submit(measure, r): r for r in rows}
        for i, future in enumerate(as_completed(futures), 1):
            try:
                done.append(future.result())
            except Exception as ex:  # noqa: BLE001 - one unreadable file must not stop the screen
                failed += 1
                print(f"  failed: {futures[future]['path'][-60:]}: {ex}", flush=True)
            if i % 50 == 0:
                print(f"  {i}/{len(rows)}", flush=True)

    with open(out, "w", encoding="utf-8", newline="") as f:
        writer = csv.DictWriter(f, fieldnames=FIELDS)
        writer.writeheader()
        writer.writerows(sorted(done, key=lambda r: r["path"]))

    kept = [r for r in done if r["keep"]]
    hours = sum(float(r["duration"] or 0) for r in kept) / 3600.0
    print(f"SCREENED {len(done)} files, {failed} unreadable: {len(kept)} kept ({hours:.1f} hours)", flush=True)
    reasons: dict[str, int] = {}
    for r in done:
        if not r["keep"]:
            key = r["reason"].split(":")[0].rsplit(" ", 1)[0] if "1-bit" not in r["reason"] else "1-bit transfer"
            reasons[key] = reasons.get(key, 0) + 1
    for key, n in sorted(reasons.items(), key=lambda kv: -kv[1]):
        print(f"  dropped, {key}: {n}", flush=True)


if __name__ == "__main__":
    main()
