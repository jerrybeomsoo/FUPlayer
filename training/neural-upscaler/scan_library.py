"""Reads every audio file under a folder: codec, rate, duration and the tags the selection needs.

    python scan_library.py <music folder> library.csv [--ffprobe <path>]

Python rather than a shell loop because music libraries are full of paths a shell mangles; a census
of a Japanese library done in bash lost a fifth of its files that way.
"""
from __future__ import annotations

import argparse
import csv
import json
import shutil
import subprocess
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

EXTENSIONS = {".m4a", ".flac", ".wav", ".aif", ".aiff", ".wv", ".ape", ".tta"}
FIELDS = ["path", "codec", "rate", "bits", "channels", "duration", "genre", "title", "album",
          "artist", "album_artist", "composer", "error"]


def probe(ffprobe: str, path: Path) -> dict:
    cmd = [
        ffprobe, "-v", "error", "-print_format", "json",
        "-show_entries",
        "stream=codec_name,sample_rate,bits_per_raw_sample,channels:format=duration:format_tags",
        str(path),
    ]
    try:
        raw = subprocess.run(cmd, capture_output=True, timeout=120).stdout
        data = json.loads(raw.decode("utf-8", errors="replace"))
    except Exception as ex:  # noqa: BLE001 - a broken file is a row, not a crash
        return {"path": str(path), "error": str(ex)}

    streams = [s for s in data.get("streams", []) if s.get("sample_rate")]
    stream = streams[0] if streams else {}
    tags = {k.lower(): v for k, v in data.get("format", {}).get("tags", {}).items()}
    return {
        "path": str(path),
        "codec": stream.get("codec_name", ""),
        "rate": int(stream.get("sample_rate", 0) or 0),
        "bits": stream.get("bits_per_raw_sample", ""),
        "channels": stream.get("channels", ""),
        "duration": float(data.get("format", {}).get("duration", 0) or 0),
        "genre": tags.get("genre", ""),
        "title": tags.get("title", ""),
        "album": tags.get("album", ""),
        "artist": tags.get("artist", ""),
        "album_artist": tags.get("album_artist", ""),
        "composer": tags.get("composer", ""),
        "error": "",
    }


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("root", type=Path)
    ap.add_argument("out", type=Path)
    ap.add_argument("--ffprobe", default=shutil.which("ffprobe") or "ffprobe")
    ap.add_argument("--workers", type=int, default=6)
    args = ap.parse_args()

    files = sorted(p for p in args.root.rglob("*") if p.suffix.lower() in EXTENSIONS)
    print(f"{len(files)} files", flush=True)
    rows = []
    with ThreadPoolExecutor(max_workers=args.workers) as pool:
        for i, row in enumerate(pool.map(lambda p: probe(args.ffprobe, p), files), 1):
            rows.append(row)
            if i % 200 == 0:
                print(f"  {i}", flush=True)

    with args.out.open("w", encoding="utf-8", newline="") as f:
        writer = csv.DictWriter(f, fieldnames=FIELDS)
        writer.writeheader()
        writer.writerows(rows)
    print(f"wrote {args.out}", flush=True)


if __name__ == "__main__":
    main()
