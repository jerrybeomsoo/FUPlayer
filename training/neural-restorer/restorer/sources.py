"""Chooses the CD-rate lossless music the restorer learns from, which the upscaler could not use.

    python -m restorer.sources library.csv [--keep-classical] [--keep-instrumental] [--favour-japanese]
                                           [--favour <regex>] [--favour-weight 3]

Reads the library scan ../neural-upscaler/scan_library.py writes. The upscaler needed masters with something above
22 kHz; the restorer's references end at 22.05 or 24 kHz, so every lossless file at 44.1 or 48 kHz will do. The same
exclusions and weights as ../neural-upscaler/select_tracks.py apply, and a song already in the high-resolution corpus
under the same title and artist is not taken twice. Writes work/restorer/cd-sources.csv.
"""
from __future__ import annotations

import argparse
import csv
import re
from pathlib import Path

from select_tracks import (CLASSICAL_CREDIT, CLASSICAL_GENRE, INSTRUMENTAL, INSTRUMENTAL_GENRE,  # noqa: E402
                           JAPANESE_GENRE, JAPANESE_TEXT, LOSSLESS)
from upscaler.data import _title_group  # noqa: E402

OUT = Path("work/restorer")
SCREENED = Path("work/corpus/manifest-screened.csv")


def key(title: str, artist: str) -> tuple[str, str]:
    return _title_group(title), "".join(ch for ch in artist.lower() if ch.isalnum())


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("scan", type=Path)
    ap.add_argument("--keep-classical", action="store_true")
    ap.add_argument("--keep-instrumental", action="store_true")
    ap.add_argument("--favour-japanese", action="store_true")
    ap.add_argument("--favour", default="", help="regular expression over genre and credits")
    ap.add_argument("--favour-weight", type=float, default=3.0)
    args = ap.parse_args()
    favour = re.compile(args.favour, re.I) if args.favour else None

    have = set()
    if SCREENED.exists():
        have = {key(r["title"], r["artist"]) for r in csv.DictReader(SCREENED.open(encoding="utf-8"))}
    chosen, reasons = [], {}
    for r in csv.DictReader(args.scan.open(encoding="utf-8")):
        if r.get("error") or not LOSSLESS.match(r["codec"] or "") or r["rate"] not in ("44100", "48000"):
            continue
        if int(r.get("channels") or 0) != 2 or float(r["duration"] or 0) < 60:
            reasons["not stereo or under a minute"] = reasons.get("not stereo or under a minute", 0) + 1
            continue
        credits = " ".join(r[k] for k in ("artist", "album_artist", "album", "composer", "title"))
        people = " ".join(r[k] for k in ("artist", "album_artist", "album", "composer"))
        reason = None
        if not args.keep_classical and (CLASSICAL_GENRE.search(r["genre"]) or CLASSICAL_CREDIT.search(people)):
            reason = "classical"
        elif not args.keep_instrumental and (
                INSTRUMENTAL.search(r["title"]) or INSTRUMENTAL.search(r["album"]) or INSTRUMENTAL_GENRE.search(r["genre"])):
            reason = "instrumental"
        elif key(r["title"], r["artist"]) in have:
            reason = "already in the high-resolution corpus"
        if reason is not None:
            reasons[reason] = reasons.get(reason, 0) + 1
            continue
        japanese = bool(JAPANESE_GENRE.search(r["genre"]) or JAPANESE_TEXT.search(credits))
        favoured = (args.favour_japanese and japanese) or bool(favour and (favour.search(r["genre"]) or favour.search(credits)))
        r["japanese"] = "1" if japanese else "0"
        r["weight"] = f"{args.favour_weight:g}" if favoured else "1"
        have.add(key(r["title"], r["artist"]))
        chosen.append(r)

    if not chosen:
        raise SystemExit("nothing chosen: no lossless stereo files at 44.1 or 48 kHz in the scan")
    OUT.mkdir(parents=True, exist_ok=True)
    with (OUT / "cd-sources.csv").open("w", encoding="utf-8", newline="") as f:
        writer = csv.DictWriter(f, fieldnames=list(chosen[0].keys()))
        writer.writeheader()
        writer.writerows(chosen)
    hours = sum(float(r["duration"]) for r in chosen) / 3600
    print(f"chosen {len(chosen)} CD-rate tracks, {hours:.1f} h; left out {reasons}")


if __name__ == "__main__":
    main()
