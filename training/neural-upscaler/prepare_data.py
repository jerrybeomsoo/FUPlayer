"""Builds the training corpus for the neural upscaler.

    python prepare_data.py tracks.csv work/corpus [--workers 6] [--ffmpeg <path>]

For every chosen track: the master at its training rate (88.2 or 96 kHz), and three degraded copies of
the same excerpt, each made the way a real release is made. The master is brought down to 44.1 or
48 kHz, possibly put through a lossy encoder, decoded, and lined up again with the original. Degraded
copies are stored at their low rate; the training loop upsamples them on the graphics device, which
is cheap there and halves what has to be kept on disk.

Everything is float16. Its smallest step is about -144 dBFS, finer than 24-bit integers, so the
ultrasonic content a high-resolution master carries at -60 to -90 dBFS keeps its detail.

The run can be stopped and started again; tracks already in the manifest are skipped. Encoders this
FFmpeg lacks are left out of the mix and the others share their weight: aac_mf, Windows' own AAC
encoder, exists only on Windows.
"""
from __future__ import annotations

import argparse
import csv
import hashlib
import os
import random
import shutil
import subprocess
import tempfile
from concurrent.futures import ProcessPoolExecutor, as_completed
from pathlib import Path

import numpy as np
import soundfile as sf

EXCERPT_SECONDS = 150.0
VARIANTS = 3
SOXR = "aresample=resampler=soxr:precision=28"

# (label, weight, encoder arguments or None for lossless, extension, needs 48 kHz)
CODECS = [
    ("lossless", 0.14, None, ".wav", False),
    ("mp3-96", 0.03, ["-c:a", "libmp3lame", "-b:a", "96k"], ".mp3", False),
    ("mp3-128", 0.08, ["-c:a", "libmp3lame", "-b:a", "128k"], ".mp3", False),
    ("mp3-192", 0.07, ["-c:a", "libmp3lame", "-b:a", "192k"], ".mp3", False),
    ("mp3-256", 0.05, ["-c:a", "libmp3lame", "-b:a", "256k"], ".mp3", False),
    ("mp3-320", 0.07, ["-c:a", "libmp3lame", "-b:a", "320k"], ".mp3", False),
    ("mp3-v0", 0.05, ["-c:a", "libmp3lame", "-q:a", "0"], ".mp3", False),
    ("mp3-v2", 0.04, ["-c:a", "libmp3lame", "-q:a", "2"], ".mp3", False),
    ("aac-128", 0.05, ["-c:a", "aac", "-b:a", "128k"], ".m4a", False),
    ("aac-192", 0.06, ["-c:a", "aac", "-b:a", "192k"], ".m4a", False),
    ("aac-256", 0.08, ["-c:a", "aac", "-b:a", "256k"], ".m4a", False),
    ("aacmf-192", 0.03, ["-c:a", "aac_mf", "-b:a", "192k"], ".m4a", False),
    ("aacmf-256", 0.05, ["-c:a", "aac_mf", "-b:a", "256k"], ".m4a", False),
    ("vorbis-q3", 0.04, ["-c:a", "libvorbis", "-q:a", "3"], ".ogg", False),
    ("vorbis-q5", 0.05, ["-c:a", "libvorbis", "-q:a", "5"], ".ogg", False),
    ("vorbis-q7", 0.04, ["-c:a", "libvorbis", "-q:a", "7"], ".ogg", False),
    ("opus-128", 0.03, ["-c:a", "libopus", "-b:a", "128k"], ".opus", True),
    ("opus-192", 0.04, ["-c:a", "libopus", "-b:a", "192k"], ".opus", True),
]


def run(ffmpeg: str, args: list[str]) -> None:
    result = subprocess.run([ffmpeg, "-hide_banner", "-loglevel", "error", "-y", *args], capture_output=True)
    if result.returncode != 0:
        raise RuntimeError(result.stderr.decode("utf-8", errors="replace")[-400:])


def available_codecs(ffmpeg: str) -> list[tuple]:
    listing = subprocess.run([ffmpeg, "-hide_banner", "-encoders"], capture_output=True).stdout.decode("utf-8", "replace")
    names = {line.split()[1] for line in listing.splitlines() if len(line.split()) > 1 and line.startswith(" A")}
    usable = [c for c in CODECS if c[2] is None or c[2][1] in names]
    missing = sorted({c[2][1] for c in CODECS if c[2] is not None and c[2][1] not in names})
    if missing:
        print(f"this FFmpeg has no {', '.join(missing)}; the other codecs share their weight", flush=True)
    return usable


def read(path: str) -> tuple[np.ndarray, int]:
    data, rate = sf.read(path, dtype="float32", always_2d=True)
    return data.T.copy(), rate  # (channels, frames)


def align(reference: np.ndarray, decoded: np.ndarray) -> np.ndarray:
    """Slides the decoded copy against the reference to undo the encoder's delay, then trims to length."""
    frames = reference.shape[1]
    window = min(frames, 480_000)
    ref = reference[0, :window].astype(np.float64)
    search = 8192
    dec = decoded[0, : window + search].astype(np.float64)
    if dec.shape[0] < window:
        dec = np.pad(dec, (0, window - dec.shape[0]))
    # Correlation over lags 0..search in the frequency domain.
    n = 1 << int(np.ceil(np.log2(window + search)))
    spectrum = np.fft.rfft(dec, n) * np.conj(np.fft.rfft(ref, n))
    corr = np.fft.irfft(spectrum, n)[: search + 1]
    lag = int(np.argmax(corr))
    out = decoded[:, lag: lag + frames]
    if out.shape[1] < frames:
        out = np.pad(out, ((0, 0), (0, frames - out.shape[1])))
    return out


def prepare(row: dict, out_dir: str, ffmpeg: str, codecs: list[tuple]) -> dict:
    track_id = hashlib.sha1(row["path"].encode("utf-8")).hexdigest()[:12]
    target = Path(out_dir) / f"{track_id}.y.npy"
    rng = random.Random(track_id)
    rate = int(row["train_rate"])
    duration = float(row["duration"] or 0.0)
    start = max(0.0, (duration - EXCERPT_SECONDS) / 2.0)
    length = min(EXCERPT_SECONDS, duration)

    with tempfile.TemporaryDirectory(prefix="fuml-") as tmp:
        master_wav = os.path.join(tmp, "master.wav")
        run(ffmpeg, ["-ss", f"{start:.3f}", "-t", f"{length:.3f}", "-i", row["path"],
                     "-af", SOXR, "-ar", str(rate), "-ac", "2", "-c:a", "pcm_f32le", master_wav])
        master, got_rate = read(master_wav)
        assert got_rate == rate, (got_rate, rate)
        np.save(target, master.astype(np.float16))

        variants = []
        labels = [c[0] for c in codecs]
        weights = [c[1] for c in codecs]
        for k in range(VARIANTS):
            family_48 = rate == 96000
            base = 48000 if family_48 and rng.random() < 0.7 else 44100
            while True:
                label = rng.choices(labels, weights)[0]
                spec = next(c for c in codecs if c[0] == label)
                if not spec[4] or base == 48000:
                    break

            low_wav = os.path.join(tmp, f"low{k}.wav")
            run(ffmpeg, ["-i", master_wav, "-af", SOXR, "-ar", str(base), "-c:a", "pcm_f32le", low_wav])
            reference, _ = read(low_wav)

            if spec[2] is None:
                degraded = reference
            else:
                coded = os.path.join(tmp, f"coded{k}{spec[3]}")
                back = os.path.join(tmp, f"back{k}.wav")
                run(ffmpeg, ["-i", low_wav, *spec[2], coded])
                run(ffmpeg, ["-i", coded, "-af", SOXR, "-ar", str(base), "-ac", "2", "-c:a", "pcm_f32le", back])
                decoded, _ = read(back)
                degraded = align(reference, decoded)

            path = Path(out_dir) / f"{track_id}.x{k}.npy"
            np.save(path, degraded.astype(np.float16))
            variants.append(f"{path.name}|{label}|{base}")

    return {
        "id": track_id,
        "rate": rate,
        "frames": master.shape[1],
        "y": target.name,
        "variants": ";".join(variants),
        "weight": row["weight"],
        "japanese": row["japanese"],
        "album": row["album"],
        "title": row["title"],
        "artist": row["artist"],
    }


def write_manifest(path: Path, results: list[dict]) -> None:
    if not results:
        return
    with path.open("w", encoding="utf-8", newline="") as f:
        w = csv.DictWriter(f, fieldnames=list(results[0].keys()))
        w.writeheader()
        w.writerows(results)


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("tracks", type=Path)
    ap.add_argument("out", type=Path)
    ap.add_argument("--workers", type=int, default=6)
    ap.add_argument("--ffmpeg", default=shutil.which("ffmpeg") or "ffmpeg")
    args = ap.parse_args()

    args.out.mkdir(parents=True, exist_ok=True)
    codecs = available_codecs(args.ffmpeg)
    rows = list(csv.DictReader(args.tracks.open(encoding="utf-8")))
    manifest_path = args.out / "manifest.csv"

    done = {}
    if manifest_path.exists():
        for r in csv.DictReader(manifest_path.open(encoding="utf-8")):
            done[r["id"]] = r

    todo = [r for r in rows if hashlib.sha1(r["path"].encode("utf-8")).hexdigest()[:12] not in done]
    print(f"{len(rows)} tracks, {len(done)} already prepared, {len(todo)} to go", flush=True)

    results = list(done.values())
    failures = 0
    with ProcessPoolExecutor(max_workers=args.workers) as pool:
        futures = {pool.submit(prepare, r, str(args.out), args.ffmpeg, codecs): r for r in todo}
        for i, future in enumerate(as_completed(futures), 1):
            try:
                results.append(future.result())
            except Exception as ex:  # noqa: BLE001 - one bad file must not stop the corpus
                failures += 1
                print(f"  failed: {futures[future]['title'][:40]}: {ex}", flush=True)
            if i % 10 == 0:
                print(f"  {i}/{len(todo)}", flush=True)
                write_manifest(manifest_path, results)

    write_manifest(manifest_path, results)
    print(f"prepared {len(results)} tracks, {failures} failed", flush=True)


if __name__ == "__main__":
    main()
