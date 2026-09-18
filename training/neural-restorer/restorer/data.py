"""Training pairs for the restorer: the same crop of a coded copy and of its reference, both channels, at 44.1 or 48 kHz.

The held-out songs are chosen the way the upscaler's are, by title, so a song the upscaler was measured on is not
trained on here either, and versions of one song stay on one side of the split.
"""
from __future__ import annotations

import csv
import random
from dataclasses import dataclass, field
from pathlib import Path

import numpy as np
import torch

from upscaler import data as upscaler_data

ROOT = Path("work/restorer")
CROP = 32_768   # samples at either rate: 0.68 s at 48 kHz, twice what the network sees of the past and future


@dataclass
class Item:
    id: str
    title: str
    weight: float
    japanese: bool
    rate: int
    reference: Path
    variants: list[tuple[Path, str]] = field(default_factory=list)


def load(root: Path = ROOT) -> list[Item]:
    items = []
    for row in csv.DictReader((root / "manifest.csv").open(encoding="utf-8")):
        variants = []
        for v in row["variants"].split(";"):
            name, label, _ = v.split("|")
            variants.append((root / name, label))
        items.append(Item(row["id"], row["title"], float(row["weight"]), row["japanese"] == "1", int(row["rate"]),
                          root / row["ref"], variants))
    return items


def split(items: list[Item]) -> tuple[list[Item], list[Item]]:
    """The upscaler's split: a song is held out if its title group hashes into the same six per cent."""
    shim = [upscaler_data.Track(i.id, 0, 0, Path(), [], i.weight, i.japanese, i.title, "") for i in items]
    _, held = upscaler_data.split(shim)
    held_ids = {t.id for t in held}
    return [i for i in items if i.id not in held_ids], [i for i in items if i.id in held_ids]


class Sampler:
    """Crops drawn at random: songs in proportion to their weight, copies uniformly.

    One crop in `lossless` is the reference against itself, so the network learns to leave clean audio alone; a
    crop whose reference is quieter than `min_rms` is drawn again.
    """

    def __init__(self, items: list[Item], seed: int, lossless: float = 0.1, min_rms: float = 3e-4,
                 augment: bool = True, crop: int = CROP) -> None:
        self.items = items
        self.crop = crop
        self.weights = [i.weight for i in items]
        self.rng = random.Random(seed)
        self.lossless = lossless
        self.min_rms = min_rms
        self.augment = augment
        self._cache: dict[Path, np.ndarray] = {}

    def _open(self, path: Path) -> np.ndarray:
        if path not in self._cache:
            self._cache[path] = np.load(path, mmap_mode="r")
        return self._cache[path]

    def draw(self) -> tuple[np.ndarray, np.ndarray, int, str]:
        for _ in range(50):
            item = self.rng.choices(self.items, self.weights)[0]
            y_all = self._open(item.reference)
            if self.rng.random() < self.lossless:
                x_all, label = y_all, "lossless"
            else:
                path, label = self.rng.choice(item.variants)
                x_all = self._open(path)

            length = min(x_all.shape[1], y_all.shape[1])
            start = self.rng.randrange(0, max(1, length - self.crop))
            y = np.asarray(y_all[:, start: start + self.crop], dtype=np.float32)
            if y.shape[1] < self.crop or float(np.sqrt(np.mean(np.square(y, dtype=np.float64)))) < self.min_rms:
                continue

            x = np.asarray(x_all[:, start: start + self.crop], dtype=np.float32)
            if self.augment:
                gain = np.float32(10.0 ** (self.rng.uniform(-6.0, 1.0) / 20.0))
                x, y = x * gain, y * gain
                if self.rng.random() < 0.5:
                    x, y = x[::-1].copy(), y[::-1].copy()
            return x, y, item.rate, label

        raise RuntimeError("no crop loud enough after 50 draws")


def batch(sampler: Sampler, size: int, device: torch.device):
    """x, y [B, 2, CROP] on the device, rates [B], labels."""
    xs, ys, rates, labels = [], [], [], []
    for _ in range(size):
        x, y, rate, label = sampler.draw()
        xs.append(torch.from_numpy(np.ascontiguousarray(x)))
        ys.append(torch.from_numpy(np.ascontiguousarray(y)))
        rates.append(rate)
        labels.append(label)
    return (torch.stack(xs).to(device, non_blocking=True), torch.stack(ys).to(device, non_blocking=True),
            torch.tensor(rates, device=device), labels)
