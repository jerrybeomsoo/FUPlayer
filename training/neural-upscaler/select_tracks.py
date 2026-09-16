"""Chooses the training tracks from a library scan: lossless files above 48 kHz.

    python select_tracks.py library.csv tracks.csv [--keep-classical] [--keep-instrumental]
                            [--favour-japanese] [--favour <regex>] [--favour-weight 3]

Classical music is left out by default, by genre tag and, for albums that carry none, by what the
credits say; instrumental music by title and album markers. Both are the choices the reference model
was trained with: they are the kinds of music least like what it was asked to improve, and a network
of this size spends what it can learn on whatever it is shown.

Favouring music weights it in the training sampler; nothing else is cut. --favour-japanese matches
Japanese text in the credits and J-pop, anime, game and idol genres.

A master at 176.4 or 192 kHz is used at 88.2 or 96 kHz, which is the rate the upscaler writes.
"""
from __future__ import annotations

import argparse
import csv
import re
from pathlib import Path

LOSSLESS = re.compile(r"^(alac|flac|wavpack|tta|ape|pcm_[suf]\d+[lb]e)$", re.I)
HIRES = {88200, 96000, 176400, 192000}
TRAIN_RATE = {192000: 96000, 176400: 88200}

CLASSICAL_GENRE = re.compile(r"classi|klassik|クラシック|opera|orchestral|baroque|symphon", re.I)
CLASSICAL_CREDIT = re.compile(
    r"philharmoni|symphon|orchest|concerto|sonata|quartet|karajan|böhm|bohm|rattle|haitink|bernstein"
    r"|abbado|mahler|bruckner|beethoven|mozart|brahms|tchaikovsky|rachmaninov|debussy|ravel|chopin"
    r"|bach|haydn|schubert|dvorak|dvořák|sibelius|wagner|verdi|puccini|op\.\s*\d|bwv|k\.\s*\d", re.I)
INSTRUMENTAL = re.compile(
    r"instrumental|\binst\b|\binst\.|\(inst\)|off[\s_-]*vocal|karaoke|カラオケ|backing\s*track"
    r"|without\s*vocal|minus\s*one|\bbgm\b", re.I)
INSTRUMENTAL_GENRE = re.compile(r"^\s*(jazz|krautrock|ambient|new age|soundtrack score)\s*$", re.I)
JAPANESE_GENRE = re.compile(r"j-?pop|anime|アニメ|game|vocaloid|ボーカロイド|j-?rock|アイドル|idol", re.I)
JAPANESE_TEXT = re.compile(r"[぀-ヿ㐀-䶿一-鿿ｦ-ﾟ]")


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("scan", type=Path)
    ap.add_argument("out", type=Path)
    ap.add_argument("--keep-classical", action="store_true")
    ap.add_argument("--keep-instrumental", action="store_true")
    ap.add_argument("--favour-japanese", action="store_true")
    ap.add_argument("--favour", default="", help="regular expression over genre and credits")
    ap.add_argument("--favour-weight", type=float, default=3.0)
    args = ap.parse_args()
    favour = re.compile(args.favour, re.I) if args.favour else None

    rows = list(csv.DictReader(args.scan.open(encoding="utf-8")))
    chosen, reasons = [], {}
    for r in rows:
        rate = int(r["rate"] or 0)
        if r.get("error") or not LOSSLESS.match(r["codec"] or "") or rate not in HIRES:
            continue
        credits = " ".join(r[k] for k in ("artist", "album_artist", "album", "composer", "title"))
        # Credits only, never the title: a pop song called "Symphony" is not a symphony.
        people = " ".join(r[k] for k in ("artist", "album_artist", "album", "composer"))
        if not args.keep_classical and (CLASSICAL_GENRE.search(r["genre"]) or CLASSICAL_CREDIT.search(people)):
            reasons["classical"] = reasons.get("classical", 0) + 1
            continue
        if not args.keep_instrumental and (
                INSTRUMENTAL.search(r["title"]) or INSTRUMENTAL.search(r["album"]) or INSTRUMENTAL_GENRE.search(r["genre"])):
            reasons["instrumental"] = reasons.get("instrumental", 0) + 1
            continue

        japanese = bool(JAPANESE_GENRE.search(r["genre"]) or JAPANESE_TEXT.search(credits))
        favoured = (args.favour_japanese and japanese) or bool(favour and (favour.search(r["genre"]) or favour.search(credits)))
        r["japanese"] = "1" if japanese else "0"
        r["weight"] = f"{args.favour_weight:g}" if favoured else "1"
        r["train_rate"] = str(TRAIN_RATE.get(rate, rate))
        chosen.append(r)

    if not chosen:
        raise SystemExit("nothing chosen: no lossless files above 48 kHz in the scan")

    with args.out.open("w", encoding="utf-8", newline="") as f:
        w = csv.DictWriter(f, fieldnames=list(chosen[0].keys()))
        w.writeheader()
        w.writerows(chosen)

    hours = sum(float(r["duration"] or 0) for r in chosen) / 3600
    favoured = [r for r in chosen if r["weight"] != "1"]
    print(f"chosen {len(chosen)} tracks, {hours:.1f} h; left out {reasons}")
    print(f"  favoured {len(favoured)} ({sum(float(r['duration'] or 0) for r in favoured) / 3600:.1f} h)")


if __name__ == "__main__":
    main()
