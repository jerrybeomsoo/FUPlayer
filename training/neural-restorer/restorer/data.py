"""Training pairs for the restorer: the same crop of a coded copy and of its reference, both channels, at 44.1 or 48 kHz.

The held-out songs are chosen the way the upscaler's are, by title, so a song the upscaler was measured on is not
trained on here either, and versions of one song stay on one side of the split.
"""
from __future__ import annotations

import csv
import os
import queue
import random
import threading
from dataclasses import dataclass, field
from pathlib import Path

import numpy as np
import torch

from upscaler import data as upscaler_data

ROOT = Path(os.environ.get("FUPLAYER_RESTORER_CORPUS", "work/restorer"))
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


class Prefetch:
    """Draws crops on background threads, so the disk and the graphics card work at the same time.

    A crop is 128 KB of one file and another of its reference, drawn at random from a corpus far too large to sit
    in memory; on a drive that has to seek for them the training thread spent longer waiting than computing. Each
    thread has its own sampler, so the draws stay independent, and the queue is small enough that the memory it
    holds is a few crops rather than a corpus. The order crops arrive in is not the order one sampler would draw
    them, which is of no consequence to a sampler whose whole purpose is randomness; validation keeps using a
    plain Sampler, whose order is reproducible.
    """

    def __init__(self, items: list[Item], seed: int, lossless: float = 0.1, crop: int = CROP,
                 threads: int = 4, depth: int = 48) -> None:
        self._queue: queue.Queue = queue.Queue(maxsize=depth)
        self._stop = threading.Event()
        self._samplers = [Sampler(items, seed=seed + (9973 * i), lossless=lossless, crop=crop) for i in range(threads)]
        self._threads = [threading.Thread(target=self._fill, args=(s,), daemon=True, name=f"restorer-crops-{i}")
                         for i, s in enumerate(self._samplers)]
        for thread in self._threads:
            thread.start()

    def _fill(self, sampler: Sampler) -> None:
        while not self._stop.is_set():
            try:
                drawn = sampler.draw()
            except RuntimeError:
                continue
            while not self._stop.is_set():
                try:
                    self._queue.put(drawn, timeout=0.5)
                    break
                except queue.Full:
                    continue

    def draw(self) -> tuple[np.ndarray, np.ndarray, int, str]:
        return self._queue.get()

    def close(self) -> None:
        self._stop.set()


def batch(sampler: Sampler | Prefetch, size: int, device: torch.device):
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
