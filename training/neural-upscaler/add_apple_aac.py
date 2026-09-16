"""Adds a fourth degraded copy of every track in a corpus, coded with Apple's own AAC encoder.

    python add_apple_aac.py work/corpus --qaac <path to qaac64.exe> [--workers 3] [--ffmpeg <path>]

Optional, and Windows only. Apple Music and the iTunes Store deliver Apple AAC, and Apple's encoder is
not FFmpeg's: at 256 kbit/s it keeps the band to about 21 kHz where FFmpeg's stops at 20, at 128 kbit/s
to 17.5 kHz where FFmpeg's stops at 16. qaac (https://github.com/nu774/qaac) drives it through the
CoreAudioToolbox library that iTunes or Apple's device software installs; copy that library and the
ones it needs into a QTfiles64 folder beside qaac64.exe, as qaac's own documentation describes, and
check with `qaac64 --check`.

The mix is 256 kbit/s constrained VBR, what Apple sells and streams, most of the time, and 192, 128 and
true VBR the rest. Half of the 96 kHz tracks take the path Apple Music takes when its output is
captured on Windows: coded at 44.1 kHz, decoded, and resampled to 48 kHz on the way into the mixer.

The copy is built from the stored master excerpt, so it lines up with the other three exactly.
Resumable: tracks that already have an Apple copy are skipped.
"""
from __future__ import annotations

import argparse
import csv
import os
import random
import shutil
import subprocess
import tempfile
from concurrent.futures import ProcessPoolExecutor, as_completed
from pathlib import Path

import numpy as np
import soundfile as sf

from prepare_data import SOXR, align, read, run

# (label, weight, qaac arguments)
MODES = [
    ("apple-cvbr256", 0.55, ["--cvbr", "256"]),
    ("apple-tvbr91", 0.15, ["--tvbr", "91"]),
    ("apple-cvbr192", 0.15, ["--cvbr", "192"]),
    ("apple-cvbr128", 0.15, ["--cvbr", "128"]),
]


def extend(row: dict, corpus: str, qaac: str, ffmpeg: str) -> tuple[str, str]:
    track_id = row["id"]
    rng = random.Random(track_id + "/apple")
    rate = int(row["rate"])
    label, _, qaac_args = rng.choices(MODES, [m[1] for m in MODES])[0]
    via_mixer = rate == 96000 and rng.random() < 0.5
    base = 48000 if via_mixer else 44100
    if via_mixer:
        label += "-mix48"

    master = np.load(Path(corpus) / row["y"]).astype(np.float32)
    with tempfile.TemporaryDirectory(prefix="fuml-apple-") as tmp:
        master_wav = os.path.join(tmp, "master.wav")
        sf.write(master_wav, master.T, rate, subtype="FLOAT")

        low_wav = os.path.join(tmp, "low.wav")
        run(ffmpeg, ["-i", master_wav, "-af", SOXR, "-ar", str(base), "-c:a", "pcm_f32le", low_wav])
        reference, _ = read(low_wav)

        encode_in = low_wav
        if via_mixer:
            encode_in = os.path.join(tmp, "enc44.wav")
            run(ffmpeg, ["-i", master_wav, "-af", SOXR, "-ar", "44100", "-c:a", "pcm_f32le", encode_in])

        coded = os.path.join(tmp, "coded.m4a")
        result = subprocess.run([qaac, "-q", "2", *qaac_args, "--silent", "-o", coded, encode_in], capture_output=True)
        if result.returncode != 0:
            raise RuntimeError(result.stderr.decode("utf-8", errors="replace")[-400:])

        back = os.path.join(tmp, "back.wav")
        run(ffmpeg, ["-i", coded, "-af", SOXR, "-ar", str(base), "-ac", "2", "-c:a", "pcm_f32le", back])
        decoded, _ = read(back)
        degraded = align(reference, decoded)

    name = f"{track_id}.x3.npy"
    np.save(Path(corpus) / name, degraded.astype(np.float16))
    return track_id, f"{name}|{label}|{base}"


def write_manifest(path: Path, rows: list[dict]) -> None:
    tmp = path.with_suffix(".tmp")
    with tmp.open("w", encoding="utf-8", newline="") as f:
        w = csv.DictWriter(f, fieldnames=list(rows[0].keys()))
        w.writeheader()
        w.writerows(rows)
    tmp.replace(path)


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("corpus", type=Path)
    ap.add_argument("--qaac", required=True)
    ap.add_argument("--workers", type=int, default=3)
    ap.add_argument("--ffmpeg", default=shutil.which("ffmpeg") or "ffmpeg")
    args = ap.parse_args()

    manifest = args.corpus / "manifest.csv"
    rows = list(csv.DictReader(manifest.open(encoding="utf-8")))
    todo = [r for r in rows if "|apple-" not in r["variants"]]
    print(f"{len(rows)} tracks, {len(rows) - len(todo)} already have an Apple copy, {len(todo)} to go", flush=True)

    by_id = {r["id"]: r for r in rows}
    failures = 0
    with ProcessPoolExecutor(max_workers=args.workers) as pool:
        futures = {pool.submit(extend, r, str(args.corpus), args.qaac, args.ffmpeg): r for r in todo}
        for i, future in enumerate(as_completed(futures), 1):
            try:
                track_id, variant = future.result()
                by_id[track_id]["variants"] += ";" + variant
            except Exception as ex:  # noqa: BLE001 - one bad track must not stop the rest
                failures += 1
                print(f"  failed: {futures[future]['title'][:40]}: {ex}", flush=True)
            if i % 10 == 0:
                print(f"  {i}/{len(todo)}", flush=True)
                write_manifest(manifest, rows)

    write_manifest(manifest, rows)
    print(f"done, {failures} failed", flush=True)


if __name__ == "__main__":
    main()
