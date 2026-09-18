"""Builds the corpus for the low-latency restoration model.

Two kinds of source, both lossless:

- the upscaler's screened high-resolution masters (their stored excerpts, brought down with SoX's resampler);
- the CD-rate ALAC files in the library that `restorer.sources` chose, decoded straight from the file.

Each song gets one delivery rate. At 48 kHz it is what a browser or a streaming application hands Windows: Opus,
AAC coded at 44.1 kHz and resampled on its way into the mixer (Apple Music in a browser), or AAC coded at 48 kHz.
At 44.1 kHz it is a file or an application that keeps the stream's own rate: AAC from three encoders, MP3, and a
little Vorbis. The reference is the master at that rate; three coded copies of the same 50 seconds are decoded,
lined up with it by cross-correlation and stored as float16.

    python -m restorer.prepare [workers]

Resumable: songs already in the manifest are skipped.
"""
from __future__ import annotations

import csv
import hashlib
import os
import random
import shutil
import subprocess
import sys
import tempfile
from concurrent.futures import ProcessPoolExecutor, as_completed
from pathlib import Path

import numpy as np
import soundfile as sf

from .audio import FFMPEG, SOXR, align, read, run  # noqa: E402

QAAC = os.environ.get("QAAC", "qaac64")  # qaac with Apple's CoreAudioToolbox; see ../neural-upscaler/add_apple_aac.py
MASTERS = Path("work/corpus")
SCREENED = MASTERS / "manifest-screened.csv"
OUT = Path("work/restorer")
CD_SOURCES = OUT / "cd-sources.csv"

EXCERPT_SECONDS = 50.0
VARIANTS = 3
RATE_48K_SHARE = 0.55

# Encoders: label -> (tool, arguments). Rates are decided by the delivery tables below.
ENCODERS = {
    "opus-96": ("ffmpeg", ["-c:a", "libopus", "-b:a", "96k"]),
    "opus-112": ("ffmpeg", ["-c:a", "libopus", "-b:a", "112k"]),
    "opus-128": ("ffmpeg", ["-c:a", "libopus", "-b:a", "128k"]),
    "opus-160": ("ffmpeg", ["-c:a", "libopus", "-b:a", "160k"]),
    "aac-96": ("ffmpeg", ["-c:a", "aac", "-b:a", "96k"]),
    "aac-128": ("ffmpeg", ["-c:a", "aac", "-b:a", "128k"]),
    "aac-160": ("ffmpeg", ["-c:a", "aac", "-b:a", "160k"]),
    "apple-cvbr96": ("qaac", ["--cvbr", "96"]),
    "apple-cvbr128": ("qaac", ["--cvbr", "128"]),
    "apple-cvbr160": ("qaac", ["--cvbr", "160"]),
    "aacmf-96": ("ffmpeg", ["-c:a", "aac_mf", "-b:a", "96k"]),
    "aacmf-128": ("ffmpeg", ["-c:a", "aac_mf", "-b:a", "128k"]),
    "aacmf-160": ("ffmpeg", ["-c:a", "aac_mf", "-b:a", "160k"]),
    "mp3-128": ("ffmpeg", ["-c:a", "libmp3lame", "-b:a", "128k"]),
    "mp3-160": ("ffmpeg", ["-c:a", "libmp3lame", "-b:a", "160k"]),
    "vorbis-q2": ("ffmpeg", ["-c:a", "libvorbis", "-q:a", "2"]),
    "vorbis-q5": ("ffmpeg", ["-c:a", "libvorbis", "-q:a", "5"]),
}
EXTENSIONS = {"libopus": ".opus", "aac": ".m4a", "aac_mf": ".m4a", "libmp3lame": ".mp3", "libvorbis": ".ogg"}

# (encoder, coding rate, weight). At 48 kHz a 44.1 kHz coding rate means resampled after decoding: "-mix48".
DELIVERY_48K = [
    ("opus-96", 48_000, 0.12), ("opus-112", 48_000, 0.08), ("opus-128", 48_000, 0.14), ("opus-160", 48_000, 0.14),
    ("apple-cvbr96", 44_100, 0.07), ("apple-cvbr128", 44_100, 0.10), ("apple-cvbr160", 44_100, 0.08),
    ("aac-128", 44_100, 0.04), ("aacmf-128", 44_100, 0.03),
    ("apple-cvbr128", 48_000, 0.05), ("aac-96", 48_000, 0.03), ("aac-128", 48_000, 0.04), ("aac-160", 48_000, 0.03),
    ("vorbis-q2", 44_100, 0.025), ("vorbis-q5", 44_100, 0.025),
]
DELIVERY_44K = [
    ("apple-cvbr96", 44_100, 0.14), ("apple-cvbr128", 44_100, 0.16), ("apple-cvbr160", 44_100, 0.14),
    ("aac-96", 44_100, 0.07), ("aac-128", 44_100, 0.08), ("aac-160", 44_100, 0.07),
    ("aacmf-96", 44_100, 0.05), ("aacmf-128", 44_100, 0.06), ("aacmf-160", 44_100, 0.05),
    ("mp3-128", 44_100, 0.06), ("mp3-160", 44_100, 0.05),
    ("vorbis-q2", 44_100, 0.035), ("vorbis-q5", 44_100, 0.035),
]


def encode(label: str, source: str, coded_base: str) -> str:
    tool, args = ENCODERS[label]
    if tool == "qaac":
        coded = coded_base + ".m4a"
        result = subprocess.run([QAAC, "-q", "2", *args, "--silent", "-o", coded, source], capture_output=True)
        if result.returncode != 0:
            raise RuntimeError(result.stderr.decode("utf-8", errors="replace")[-400:])
        return coded

    coded = coded_base + EXTENSIONS[args[1]]
    run(["-i", source, *args, coded])
    return coded


def master_excerpt(row: dict, tmp: str) -> tuple[str, int]:
    """The middle EXCERPT_SECONDS of the song as a float WAV, and its rate."""
    path = os.path.join(tmp, "master.wav")
    if row["kind"] == "hires":
        rate = int(row["rate"])
        stored = np.load(MASTERS / row["y"], mmap_mode="r")
        frames = int(EXCERPT_SECONDS * rate)
        start = max(0, (stored.shape[1] - frames) // 2)
        sf.write(path, np.asarray(stored[:, start: start + frames], dtype=np.float32).T, rate, subtype="FLOAT")
        return path, rate

    rate = int(row["rate"])
    start = max(0.0, (float(row["duration"]) - EXCERPT_SECONDS) / 2.0)
    run(["-ss", f"{start:.3f}", "-t", f"{EXCERPT_SECONDS:.3f}", "-i", row["path"], "-map", "0:a:0",
         "-c:a", "pcm_f32le", path])
    return path, rate


_usable: set[str] | None = None


def usable() -> set[str]:
    """The encoders this machine has: FFmpeg's own list, and qaac if it is there. Modes needing others are not drawn."""
    global _usable
    if _usable is None:
        try:
            listing = subprocess.run([FFMPEG, "-hide_banner", "-encoders"], capture_output=True).stdout.decode("utf-8", "replace")
        except FileNotFoundError:
            raise SystemExit(f"FFmpeg was not found at {FFMPEG}: set FFMPEG or put ffmpeg on the PATH") from None
        have = {line.split()[1] for line in listing.splitlines() if len(line.split()) > 1}
        qaac = shutil.which(QAAC) is not None or os.path.isfile(QAAC)
        _usable = {label for label, (tool, args) in ENCODERS.items() if (qaac if tool == "qaac" else args[1] in have)}
    return _usable


def prepare(row: dict) -> dict:
    track_id = row["id"]
    rng = random.Random(track_id + "/restorer")
    store_rate = 48_000 if rng.random() < RATE_48K_SHARE else 44_100
    table = [mode for mode in (DELIVERY_48K if store_rate == 48_000 else DELIVERY_44K) if mode[0] in usable()]

    chosen: list[tuple[str, int]] = []
    while len(chosen) < VARIANTS:
        label, rate, _ = rng.choices(table, [w for _, _, w in table])[0]
        if (label, rate) not in chosen:
            chosen.append((label, rate))

    variants = []
    with tempfile.TemporaryDirectory(prefix="fuml-restorer-") as tmp:
        master_wav, master_rate = master_excerpt(row, tmp)
        at_rate: dict[int, str] = {}

        def resampled(rate: int) -> str:
            if rate not in at_rate:
                if rate == master_rate:
                    at_rate[rate] = master_wav
                else:
                    path = os.path.join(tmp, f"ref{rate}.wav")
                    run(["-i", master_wav, "-af", SOXR, "-ar", str(rate), "-c:a", "pcm_f32le", path])
                    at_rate[rate] = path
            return at_rate[rate]

        reference, _ = read(resampled(store_rate))
        for k, (label, code_rate) in enumerate(chosen):
            coded = encode(label, resampled(code_rate), os.path.join(tmp, f"coded{k}"))
            back = os.path.join(tmp, f"back{k}.wav")
            run(["-i", coded, "-af", SOXR, "-ar", str(store_rate), "-ac", "2", "-c:a", "pcm_f32le", back])
            decoded, _ = read(back)
            degraded = align(reference, decoded)

            name = f"{track_id}.v{k}.npy"
            np.save(OUT / name, degraded.astype(np.float16))
            suffix = "-mix48" if code_rate != store_rate else ""
            variants.append(f"{name}|{label}{suffix}|{store_rate}")

        ref_name = f"{track_id}.r{store_rate // 1000}.npy"
        np.save(OUT / ref_name, reference.astype(np.float16))

    return {
        "id": track_id,
        "kind": row["kind"],
        "master_rate": master_rate,
        "rate": store_rate,
        "ref": ref_name,
        "variants": ";".join(variants),
        "weight": row["weight"],
        "japanese": row["japanese"],
        "album": row["album"],
        "title": row["title"],
        "artist": row["artist"],
    }


def sources() -> list[dict]:
    rows = []
    for r in csv.DictReader(SCREENED.open(encoding="utf-8")):
        rows.append({**r, "kind": "hires", "y": r["y"]})
    if CD_SOURCES.exists():
        for r in csv.DictReader(CD_SOURCES.open(encoding="utf-8")):
            track_id = hashlib.sha1(r["path"].encode("utf-8")).hexdigest()[:12]
            rows.append({**r, "id": track_id, "kind": "cd"})
    return rows


def write_manifest(path: Path, rows: list[dict]) -> None:
    if not rows:
        return
    tmp = path.with_suffix(".tmp")
    with tmp.open("w", encoding="utf-8", newline="") as f:
        writer = csv.DictWriter(f, fieldnames=list(rows[0].keys()))
        writer.writeheader()
        writer.writerows(rows)
    tmp.replace(path)


def main() -> None:
    workers = int(sys.argv[1]) if len(sys.argv) > 1 else 6
    OUT.mkdir(parents=True, exist_ok=True)
    manifest = OUT / "manifest.csv"
    tracks = sources()
    done = {r["id"]: r for r in csv.DictReader(manifest.open(encoding="utf-8"))} if manifest.exists() else {}
    todo = [t for t in tracks if t["id"] not in done]
    print(f"{len(tracks)} songs, {len(done)} prepared, {len(todo)} to go", flush=True)

    results = list(done.values())
    failures = 0
    with ProcessPoolExecutor(max_workers=workers) as pool:
        futures = {pool.submit(prepare, t): t for t in todo}
        for i, future in enumerate(as_completed(futures), 1):
            try:
                results.append(future.result())
            except Exception as ex:  # noqa: BLE001 - one bad song must not stop the corpus
                failures += 1
                print(f"  failed: {futures[future]['title'][:40]}: {ex}", flush=True)
            if i % 20 == 0:
                print(f"  {i}/{len(todo)}", flush=True)
                write_manifest(manifest, results)

    write_manifest(manifest, results)
    counts: dict[str, int] = {}
    for r in results:
        for v in r["variants"].split(";"):
            counts[v.split("|")[1]] = counts.get(v.split("|")[1], 0) + 1
    rates = {rate: sum(1 for r in results if str(r["rate"]) == rate) for rate in ("44100", "48000")}
    print(f"PREPARED {len(results)} songs ({rates}), {failures} failed", flush=True)
    for label, n in sorted(counts.items()):
        print(f"  {label:<24} {n}", flush=True)


if __name__ == "__main__":
    main()
