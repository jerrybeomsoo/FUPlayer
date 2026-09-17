"""Markdown tables from evaluation CSVs written by upscaler.evaluate.

    python tools/eval_tables.py <csv>                     coded copies, by codec family
    python tools/eval_tables.py <csv> --lossless [<old>]  lossless copies, against an earlier model's CSV
"""
import csv
import math
import sys
from collections import OrderedDict

FAMILIES = OrderedDict([
    ("MP3, 96 and 128 kbit/s", lambda c: c in ("mp3-96", "mp3-128")),
    ("MP3, 192 kbit/s to 320, V0, V2", lambda c: c.startswith("mp3") and c not in ("mp3-96", "mp3-128")),
    ("Apple AAC, 256 kbit/s", lambda c: c.startswith("apple-cvbr256")),
    ("Apple AAC, 128 to 192 kbit/s and VBR", lambda c: c.startswith("apple") and not c.startswith("apple-cvbr256")),
    ("FFmpeg and Windows AAC, 128 to 256 kbit/s", lambda c: c.startswith("aac")),
    ("Vorbis q3 to q7", lambda c: c.startswith("vorbis")),
    ("Opus, 128 and 192 kbit/s", lambda c: c.startswith("opus")),
    ("All coded", lambda c: not c.startswith("lossless")),
])

LOSSLESS = OrderedDict([
    ("44.1 kHz, 24-bit", lambda c: c == "lossless-cd-24"),
    ("44.1 kHz, 16-bit, TPDF", lambda c: c == "lossless-cd-16"),
    ("48 kHz, 24-bit", lambda c: c == "lossless-dvd-24"),
    ("48 kHz, 16-bit, TPDF", lambda c: c == "lossless-dvd-16"),
    ("Corpus lossless copies", lambda c: c == "lossless"),
    ("48 kHz, 24-bit, told lossy", lambda c: c == "lossless-dvd-24 (flagged lossy)"),
])


def mean(rows, key):
    values = [float(r[key]) for r in rows if r.get(key, "") not in ("", "nan") and not math.isnan(float(r[key]))]
    return sum(values) / len(values) if values else float("nan")


def coded(rows) -> None:
    print("| Source | n | 0-16 kHz | 16-22 kHz | above 22 kHz | level above 22 kHz | movement |")
    print("| --- | --- | --- | --- | --- | --- | --- |")
    for name, test in FAMILIES.items():
        group = [r for r in rows if test(r["codec"])]
        if not group:
            continue
        cells = [f"{mean(group, 'in_lsd_' + b):.2f} → **{mean(group, 'out_lsd_' + b):.2f}**" for b in ("0-16k", "16-22k", "22k-nyq")]
        level = f"{mean(group, 'in_level_22k-nyq'):+.1f} → **{mean(group, 'out_level_22k-nyq'):+.1f}** dB"
        motion = f"{mean(group, 'out_motion_22k-nyq'):.2f}"
        label = f"**{name}**" if name.startswith("All") else name
        print(f"| {label} | {len(group)} | {cells[0]} | {cells[1]} | {cells[2]} | {level} | {motion} |")


def lossless(rows, old) -> None:
    print("| Source | n | 16-22 kHz | level above 22 kHz | passband change |")
    print("| --- | --- | --- | --- | --- |")
    for name, test in LOSSLESS.items():
        group = [r for r in rows if test(r["codec"])]
        before = [r for r in old if test(r["codec"])]
        if not group:
            continue
        was = lambda key, fmt: format(mean(before, key), fmt) + " → " if before else ""
        lsd = f"{mean(group, 'in_lsd_16-22k'):.2f} → {was('out_lsd_16-22k', '.2f')}**{mean(group, 'out_lsd_16-22k'):.2f}**"
        level = f"{mean(group, 'in_level_22k-nyq'):+.1f} → {was('out_level_22k-nyq', '+.1f')}**{mean(group, 'out_level_22k-nyq'):+.1f}** dB"
        moved = f"{was('passband_db', '.1f')}**{mean(group, 'passband_db'):.1f}** dB"
        print(f"| {name} | {len(group)} | {lsd} | {level} | {moved} |")


def main() -> None:
    rows = list(csv.DictReader(open(sys.argv[1], encoding="utf-8")))
    if "--lossless" in sys.argv:
        rest = sys.argv[sys.argv.index("--lossless") + 1:]
        old = list(csv.DictReader(open(rest[0], encoding="utf-8"))) if rest else []
        lossless(rows, old)
    else:
        coded(rows)


if __name__ == "__main__":
    main()
