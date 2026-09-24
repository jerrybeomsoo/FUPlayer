"""Adds coded copies to the restorer corpus: more of a kind it saw too little of, or kinds it never saw.

    python -m restorer.extra --table highrate --songs held           every mode in the table, held-out songs only
    python -m restorer.extra --table finetune --songs all --per-song 1   one mode per song, drawn by weight

The first model saw Vorbis at q5 in 51 copies and nothing above 160 kbit/s. It put a band on top of Vorbis q5's own,
which reaches 20 kHz already, and a stream that is nearly full band (Apple Music plays AAC at 256 kbit/s in a browser)
had never been in front of it. These copies are made from the song's stored reference, which is the master's fifty
seconds at the song's delivery rate: coded at that rate (a 44.1 kHz coding rate on a 48 kHz song means resampled to
44.1 kHz first and back to 48 kHz after decoding, the way a browser feeds the mixer), decoded, lined up with the
reference, and stored as float16 as {id}.x{k}.npy. The manifest's variant list grows; the split, being by title, is
untouched. Working from the reference rather than the original file means the library does not have to be mounted.
"""
from __future__ import annotations

import argparse
import csv
import os
import random
import sys
import tempfile
from concurrent.futures import ProcessPoolExecutor, as_completed
from pathlib import Path

import numpy as np

from .audio import SOXR, align, read, run  # noqa: E402

from . import data  # noqa: E402
import soundfile as sf  # noqa: E402

from .prepare import ENCODERS, OUT, encode, usable, write_manifest  # noqa: E402

EXTRA_ENCODERS = {
    "apple-cvbr192": ("qaac", ["--cvbr", "192"]),
    "apple-cvbr256": ("qaac", ["--cvbr", "256"]),
    "aac-192": ("ffmpeg", ["-c:a", "aac", "-b:a", "192k"]),
    "aac-256": ("ffmpeg", ["-c:a", "aac", "-b:a", "256k"]),
    "opus-192": ("ffmpeg", ["-c:a", "libopus", "-b:a", "192k"]),
    "opus-256": ("ffmpeg", ["-c:a", "libopus", "-b:a", "256k"]),
    "vorbis-q4": ("ffmpeg", ["-c:a", "libvorbis", "-q:a", "4"]),
    "vorbis-q6": ("ffmpeg", ["-c:a", "libvorbis", "-q:a", "6"]),
    "vorbis-q9": ("ffmpeg", ["-c:a", "libvorbis", "-q:a", "9"]),
    "mp3-256": ("ffmpeg", ["-c:a", "libmp3lame", "-b:a", "256k"]),
    "mp3-320": ("ffmpeg", ["-c:a", "libmp3lame", "-b:a", "320k"]),
}
ENCODERS.update(EXTRA_ENCODERS)

# (encoder, weight). Opus is coded at 48 kHz only, so it is left out for 44.1 kHz songs; everything else is coded at
# the song's rate, or for a 48 kHz song at 44.1 kHz and resampled after decoding, six times in ten.
TABLES = {
    "highrate": [("apple-cvbr192", 1), ("apple-cvbr256", 1), ("aac-256", 1), ("opus-192", 1), ("opus-256", 1),
                 ("vorbis-q5", 1), ("vorbis-q6", 1), ("vorbis-q9", 1), ("mp3-320", 1)],
    "finetune": [("vorbis-q5", 0.22), ("vorbis-q4", 0.07), ("vorbis-q6", 0.08), ("vorbis-q9", 0.03),
                 ("apple-cvbr256", 0.2), ("apple-cvbr192", 0.08), ("aac-256", 0.08), ("aac-192", 0.03),
                 ("opus-192", 0.08), ("opus-256", 0.05), ("mp3-256", 0.03), ("mp3-320", 0.05)],
}


def make(task: dict) -> dict:
    song_id, store_rate, reference_name, modes, first_index = (task["id"], task["rate"], task["ref"], task["modes"],
                                                               task["first"])
    rng = random.Random(song_id + "/extra/" + ",".join(modes))
    reference = np.load(OUT / reference_name).astype(np.float32)
    made = []
    with tempfile.TemporaryDirectory(prefix="fuml-restorer-extra-") as tmp:
        master_wav, master_rate = os.path.join(tmp, "master.wav"), store_rate
        sf.write(master_wav, reference.T, store_rate, subtype="FLOAT")
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

        for k, label in enumerate(modes):
            if label.startswith("opus"):
                code_rate = 48_000
            elif store_rate == 48_000 and rng.random() < 0.6:
                code_rate = 44_100
            else:
                code_rate = store_rate
            coded = encode(label, resampled(code_rate), os.path.join(tmp, f"coded{k}"))
            back = os.path.join(tmp, f"back{k}.wav")
            run(["-i", coded, "-af", SOXR, "-ar", str(store_rate), "-ac", "2", "-c:a", "pcm_f32le", back])
            decoded, _ = read(back)
            name = f"{song_id}.x{first_index + k}.npy"
            np.save(OUT / name, align(reference, decoded).astype(np.float16))
            made.append(f"{name}|{label}{'-mix48' if code_rate != store_rate else ''}|{store_rate}")
    return {"id": song_id, "variants": made}


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--table", choices=sorted(TABLES), required=True)
    ap.add_argument("--songs", choices=["held", "train", "all"], default="all")
    ap.add_argument("--per-song", type=int, default=0, help="modes drawn per song by weight; 0 makes every mode")
    ap.add_argument("--workers", type=int, default=6)
    args = ap.parse_args()

    manifest_path = OUT / "manifest.csv"
    manifest = list(csv.DictReader(manifest_path.open(encoding="utf-8")))
    by_id = {r["id"]: r for r in manifest}
    train, held = data.split(data.load())
    chosen = {"held": held, "train": train, "all": train + held}[args.songs]
    table = TABLES[args.table]

    tasks = []
    for item in chosen:
        entry = by_id[item.id]
        existing = [v.split("|")[1].replace("-mix48", "") for v in entry["variants"].split(";")]
        extras = [v.split("|")[1].replace("-mix48", "") for v in entry["variants"].split(";") if ".x" in v.split("|")[0]]
        candidates = [(m, w) for m, w in table
                      if m in usable() and not (m.startswith("opus") and item.rate == 44_100)]
        if args.per_song:
            rng = random.Random(item.id + "/" + args.table)
            modes = []
            while len(modes) < min(args.per_song, len(candidates)):
                mode = rng.choices([m for m, _ in candidates], [w for _, w in candidates])[0]
                if mode not in modes:
                    modes.append(mode)
        else:
            modes = [m for m, _ in candidates]
        # A mode already made for this song is not made again; the evaluation set may repeat a training mode.
        modes = [m for m in modes if m not in extras and (m not in existing or args.table == "highrate")]
        if not modes:
            continue
        first = sum(1 for v in entry["variants"].split(";") if ".x" in v.split("|")[0])
        tasks.append({"id": item.id, "title": item.title, "rate": item.rate, "ref": entry["ref"], "modes": modes,
                      "first": first})

    print(f"{len(tasks)} songs, {sum(len(t['modes']) for t in tasks)} copies to make", flush=True)
    failures = 0
    with ProcessPoolExecutor(max_workers=args.workers) as pool:
        futures = {pool.submit(make, t): t for t in tasks}
        for i, future in enumerate(as_completed(futures), 1):
            try:
                result = future.result()
                entry = by_id[result["id"]]
                entry["variants"] = ";".join(entry["variants"].split(";") + result["variants"])
            except Exception as ex:  # noqa: BLE001 - one bad song must not stop the rest
                failures += 1
                print(f"  failed: {futures[future]['title'][:40]}: {ex}", flush=True)
            if i % 25 == 0:
                print(f"  {i}/{len(tasks)}", flush=True)
                write_manifest(manifest_path, manifest)
    write_manifest(manifest_path, manifest)
    print(f"EXTRA DONE, {failures} failed", flush=True)


if __name__ == "__main__":
    main()
