"""Builds the restorer's corpus: high-resolution masters brought down to a delivery rate, and coded copies of them.

Every reference is a master at 88.2, 96, 176.4 or 192 kHz that `screen44` found to carry music up to 22.05 kHz,
brought down through soxr at 28-bit precision with its passband at 0.998 of Nyquist: flat to within a decibel of the
delivery rate's own Nyquist frequency, with the aliases of a tone above it 153 dB down. Nothing at CD rate is a
reference, and nothing a resampler's stopband has already emptied: what the network writes at the top of the band is
learned from what these files carry there, and a file that carries nothing teaches it to write nothing.

Every song is prepared at both delivery rates, from the same excerpt of the same master:

- **44.1 kHz**, what a file plays at. Opus is coded at 48 kHz whatever it is given, so an Opus copy is decoded at 48
  and brought back, which is what playing an Opus file at 44.1 kHz does.
- **48 kHz**, what a browser or a music client hands Windows. Some copies are coded at 44.1 kHz and resampled up
  after decoding, which is Apple Music in a browser; the rest are coded at 48.

Both are full band to their own Nyquist frequency, so the network is taught the same thing at both rates: write to
the top of the band. The first corpus was not: its 44.1 kHz references came down with an ordinary passband that
stopped at 20.1 kHz and its 48 kHz ones were mostly CD masters resampled up, which stop at 20.9, and the network
settled the disagreement by writing nothing at all in a handful of bins — the narrow dead lines a spectrogram shows
at any rate, since they are bins and not frequencies.

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

from .audio import FFMPEG, SOXR, align, read, run  # noqa: E402

QAAC = os.environ.get("QAAC", "qaac64")  # qaac with Apple's CoreAudioToolbox; see ../neural-upscaler/add_apple_aac.py
SCREENED = Path("work/screen.csv")
OUT = Path("work/restorer")

# The reference's own resample: the passband has to reach the delivery rate's Nyquist frequency, since that is what
# is being taught. A coded copy's resample after decoding keeps the ordinary passband: that one stands for a mixer.
STEEP = "aresample=resampler=soxr:precision=28:cutoff=0.998"
RATES = (44_100, 48_000)
EXCERPT_SECONDS = 90.0
VARIANTS = 4

ENCODERS = {
    "apple-cvbr96": ("qaac", ["--cvbr", "96"]),
    "apple-cvbr128": ("qaac", ["--cvbr", "128"]),
    "apple-cvbr160": ("qaac", ["--cvbr", "160"]),
    "apple-cvbr192": ("qaac", ["--cvbr", "192"]),
    "apple-cvbr256": ("qaac", ["--cvbr", "256"]),
    "aac-96": ("ffmpeg", ["-c:a", "aac", "-b:a", "96k"]),
    "aac-128": ("ffmpeg", ["-c:a", "aac", "-b:a", "128k"]),
    "aac-160": ("ffmpeg", ["-c:a", "aac", "-b:a", "160k"]),
    "aac-192": ("ffmpeg", ["-c:a", "aac", "-b:a", "192k"]),
    "aac-256": ("ffmpeg", ["-c:a", "aac", "-b:a", "256k"]),
    "aacmf-96": ("ffmpeg", ["-c:a", "aac_mf", "-b:a", "96k"]),
    "aacmf-128": ("ffmpeg", ["-c:a", "aac_mf", "-b:a", "128k"]),
    "aacmf-160": ("ffmpeg", ["-c:a", "aac_mf", "-b:a", "160k"]),
    "mp3-128": ("ffmpeg", ["-c:a", "libmp3lame", "-b:a", "128k"]),
    "mp3-160": ("ffmpeg", ["-c:a", "libmp3lame", "-b:a", "160k"]),
    "mp3-256": ("ffmpeg", ["-c:a", "libmp3lame", "-b:a", "256k"]),
    "mp3-320": ("ffmpeg", ["-c:a", "libmp3lame", "-b:a", "320k"]),
    "vorbis-q2": ("ffmpeg", ["-c:a", "libvorbis", "-q:a", "2"]),
    "vorbis-q4": ("ffmpeg", ["-c:a", "libvorbis", "-q:a", "4"]),
    "vorbis-q5": ("ffmpeg", ["-c:a", "libvorbis", "-q:a", "5"]),
    "vorbis-q6": ("ffmpeg", ["-c:a", "libvorbis", "-q:a", "6"]),
    "vorbis-q9": ("ffmpeg", ["-c:a", "libvorbis", "-q:a", "9"]),
    "opus-96": ("ffmpeg", ["-c:a", "libopus", "-b:a", "96k"]),
    "opus-128": ("ffmpeg", ["-c:a", "libopus", "-b:a", "128k"]),
    "opus-160": ("ffmpeg", ["-c:a", "libopus", "-b:a", "160k"]),
    "opus-192": ("ffmpeg", ["-c:a", "libopus", "-b:a", "192k"]),
    "opus-256": ("ffmpeg", ["-c:a", "libopus", "-b:a", "256k"]),
}
EXTENSIONS = {"libopus": ".opus", "aac": ".m4a", "aac_mf": ".m4a", "libmp3lame": ".mp3", "libvorbis": ".ogg"}

# (encoder, coding rate, weight). A fifth of the weight is on 192 kbit/s and above, where a codec is nearly
# transparent and the network has to leave what it finds alone. At 48 kHz a 44.1 kHz coding rate means the copy is
# resampled after decoding, the way a browser feeds the Windows mixer.
DELIVERY_44K = [
    ("apple-cvbr96", 44_100, 0.09), ("apple-cvbr128", 44_100, 0.11), ("apple-cvbr160", 44_100, 0.09),
    ("aac-96", 44_100, 0.05), ("aac-128", 44_100, 0.06), ("aac-160", 44_100, 0.05),
    ("aacmf-96", 44_100, 0.04), ("aacmf-128", 44_100, 0.05), ("aacmf-160", 44_100, 0.04),
    ("mp3-128", 44_100, 0.05), ("mp3-160", 44_100, 0.05),
    ("vorbis-q2", 44_100, 0.03), ("vorbis-q4", 44_100, 0.03), ("vorbis-q5", 44_100, 0.05),
    ("opus-96", 44_100, 0.03), ("opus-128", 44_100, 0.04), ("opus-160", 44_100, 0.04),
    ("apple-cvbr192", 44_100, 0.04), ("apple-cvbr256", 44_100, 0.05),
    ("aac-192", 44_100, 0.02), ("aac-256", 44_100, 0.02),
    ("mp3-256", 44_100, 0.01), ("mp3-320", 44_100, 0.02),
    ("vorbis-q6", 44_100, 0.02), ("vorbis-q9", 44_100, 0.01),
    ("opus-192", 44_100, 0.02), ("opus-256", 44_100, 0.02),
]
DELIVERY_48K = [
    ("opus-96", 48_000, 0.07), ("opus-128", 48_000, 0.09), ("opus-160", 48_000, 0.09), ("opus-192", 48_000, 0.04),
    ("opus-256", 48_000, 0.03),
    ("apple-cvbr96", 44_100, 0.05), ("apple-cvbr128", 44_100, 0.08), ("apple-cvbr160", 44_100, 0.06),
    ("apple-cvbr192", 44_100, 0.03), ("apple-cvbr256", 44_100, 0.06),
    ("aac-128", 44_100, 0.03), ("aacmf-128", 44_100, 0.03), ("mp3-320", 44_100, 0.02),
    ("vorbis-q5", 44_100, 0.02), ("vorbis-q6", 44_100, 0.02),
    ("aac-96", 48_000, 0.04), ("aac-128", 48_000, 0.05), ("aac-160", 48_000, 0.04), ("aac-192", 48_000, 0.02),
    ("aac-256", 48_000, 0.03),
    ("aacmf-96", 48_000, 0.02), ("aacmf-128", 48_000, 0.03),
    ("mp3-128", 48_000, 0.02), ("mp3-160", 48_000, 0.02),
    ("vorbis-q2", 48_000, 0.01),
]
DELIVERY = {44_100: DELIVERY_44K, 48_000: DELIVERY_48K}


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


_usable: set[str] | None = None


def usable() -> set[str]:
    """The encoders this machine has: FFmpeg's own list, and qaac if it is there."""
    global _usable
    if _usable is None:
        listing = subprocess.run([FFMPEG, "-hide_banner", "-encoders"], capture_output=True).stdout.decode("utf-8", "replace")
        have = {line.split()[1] for line in listing.splitlines() if len(line.split()) > 1}
        qaac = shutil.which(QAAC) is not None or os.path.isfile(QAAC)
        _usable = {label for label, (tool, args) in ENCODERS.items() if (qaac if tool == "qaac" else args[1] in have)}
    return _usable


def prepare(row: dict) -> list[dict]:
    track = hashlib.sha1(row["path"].encode("utf-8")).hexdigest()[:12]
    rng = random.Random(track + "/restorer44")
    duration = float(row["duration"] or 0.0)
    start = max(0.0, (duration - EXCERPT_SECONDS) / 2.0)
    length = min(EXCERPT_SECONDS, duration) if duration > 0 else EXCERPT_SECONDS
    japanese = any(ord(c) > 0x3000 for c in f"{row.get('artist', '')}{row.get('album', '')}{row.get('title', '')}")

    made = []
    with tempfile.TemporaryDirectory(prefix="fuml-restorer44-") as tmp:
        # Both references first, from the master itself: a copy coded at 44.1 kHz for a 48 kHz song is made from the
        # 44.1 kHz reference, which is what a browser plays, and not from a second conversion of the 48 kHz one.
        references: dict[int, str] = {}
        for rate in RATES:
            path = os.path.join(tmp, f"ref{rate}.wav")
            run(["-ss", f"{start:.3f}", "-t", f"{length:.3f}", "-i", row["path"], "-map", "0:a:0",
                 "-af", STEEP, "-ar", str(rate), "-c:a", "pcm_f32le", path])
            references[rate] = path

        for rate in RATES:
            table = [mode for mode in DELIVERY[rate] if mode[0] in usable()]
            chosen: list[tuple[str, int]] = []
            while len(chosen) < VARIANTS:
                label, coding_rate, _ = rng.choices(table, [w for _, _, w in table])[0]
                if (label, coding_rate) not in chosen:
                    chosen.append((label, coding_rate))

            reference_wav = references[rate]
            reference, _ = read(reference_wav)

            variants = []
            for k, (label, coding_rate) in enumerate(chosen):
                coded = encode(label, references[coding_rate], os.path.join(tmp, f"coded{rate}_{k}"))
                back = os.path.join(tmp, f"back{rate}_{k}.wav")
                run(["-i", coded, "-af", SOXR, "-ar", str(rate), "-ac", "2", "-c:a", "pcm_f32le", back])
                decoded, _ = read(back)
                degraded = align(reference, decoded)

                name = f"{track}.{rate // 1000}.v{k}.npy"
                np.save(OUT / name, degraded.astype(np.float16))
                suffix = "-mix" if coding_rate != rate else ""
                variants.append(f"{name}|{label}{suffix}|{rate}")

            ref_name = f"{track}.{rate // 1000}.ref.npy"
            np.save(OUT / ref_name, reference.astype(np.float16))
            made.append({
                "id": f"{track}.{rate // 1000}",
                "kind": "hires",
                "master_rate": row["rate"],
                "rate": rate,
                "ref": ref_name,
                "variants": ";".join(variants),
                "weight": 1,
                "japanese": "1" if japanese else "0",
                "album": row.get("album", ""),
                "title": row.get("title", ""),
                "artist": row.get("artist", ""),
            })

    return made


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
    tracks = [r for r in csv.DictReader(SCREENED.open(encoding="utf-8")) if r["keep"] == "1"]
    done = {r["id"]: r for r in csv.DictReader(manifest.open(encoding="utf-8"))} if manifest.exists() else {}
    todo = [t for t in tracks
            if f"{hashlib.sha1(t['path'].encode('utf-8')).hexdigest()[:12]}.44" not in done]
    print(f"{len(tracks)} screened masters, {len(done)} rows prepared, {len(todo)} songs to go", flush=True)

    results = list(done.values())
    failures = 0
    with ProcessPoolExecutor(max_workers=workers) as pool:
        futures = {pool.submit(prepare, t): t for t in todo}
        for i, future in enumerate(as_completed(futures), 1):
            try:
                results.extend(future.result())
            except Exception as ex:  # noqa: BLE001 - one bad song must not stop the corpus
                failures += 1
                print(f"  failed: {futures[future]['title'][:40]}: {ex}", flush=True)
            if i % 25 == 0:
                print(f"  {i}/{len(todo)}", flush=True)
                write_manifest(manifest, results)

    write_manifest(manifest, results)
    counts: dict[str, int] = {}
    for r in results:
        for v in r["variants"].split(";"):
            counts[v.split("|")[1]] = counts.get(v.split("|")[1], 0) + 1
    hours = len(results) * EXCERPT_SECONDS / 3600.0
    rates = {rate: sum(1 for r in results if int(r["rate"]) == rate) for rate in RATES}
    print(f"PREPARED {len(results)} rows ({rates}), {hours:.1f} hours of reference, {failures} songs failed", flush=True)
    for label, n in sorted(counts.items()):
        print(f"  {label:<20} {n}", flush=True)


if __name__ == "__main__":
    main()
