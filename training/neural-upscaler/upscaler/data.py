"""Training examples: a crop of a master and the same crop of one of its degraded copies.

The degraded copy is kept at 44.1 or 48 kHz. A crop is chosen on the grid where both rates have a
sample at the same instant, so that once the graphics device upsamples it the two line up exactly
rather than to within a fraction of a sample; a crop that is half a sample late teaches the model to
move everything by half a sample.
"""
from __future__ import annotations

import csv
import hashlib
import math
import random
import re
from dataclasses import dataclass
from pathlib import Path

import numpy as np
import torch
import torchaudio

SEGMENT = 32_768          # samples at the training rate: 65 frames at a hop of 512
MARGIN = 4_096            # extra either side, so the upsampler's edges fall outside the crop


@dataclass
class Track:
    id: str
    rate: int
    frames: int
    y: Path
    variants: list[tuple[Path, str, int]]
    weight: float
    japanese: bool
    title: str
    album: str


def load_manifest(corpus: Path, manifest: Path | None = None) -> list[Track]:
    """The corpus's tracks, or those of another list whose files live in the corpus folder."""
    tracks = []
    for r in csv.DictReader((manifest or corpus / "manifest.csv").open(encoding="utf-8")):
        variants = []
        for v in r["variants"].split(";"):
            name, label, low = v.split("|")
            variants.append((corpus / name, label, int(low)))
        tracks.append(Track(r["id"], int(r["rate"]), int(r["frames"]), corpus / r["y"], variants,
                            float(r["weight"]), r["japanese"] == "1", r["title"], r["album"]))
    return tracks


def _title_group(title: str) -> str:
    """Versions of one song share a group, so a song is never in both halves of the split."""
    t = title.lower()
    t = re.sub(r"[\(\[（【～~\-].*$", "", t)
    return re.sub(r"\W+", "", t) or title


def split(tracks: list[Track], held_fraction: float = 0.06) -> tuple[list[Track], list[Track]]:
    train, held = [], []
    for track in tracks:
        digest = int(hashlib.sha1(_title_group(track.title).encode("utf-8")).hexdigest()[:8], 16)
        (held if digest / 0xFFFFFFFF < held_fraction else train).append(track)
    return train, held


class Sampler:
    """Draws crops at random, tracks in proportion to their weight, variants uniformly.

    A crop whose master is quieter than min_rms is drawn again. Digital silence at the start or end of
    a track has nothing to teach, and against a silent target the spectral convergence term divides
    by nothing: a few such crops sent the loss past a thousand and threw the model off for hundreds of
    steps. Validation keeps them (min_rms 0), so its crops stay the ones it always measured.
    """

    def __init__(self, tracks: list[Track], seed: int, min_rms: float = 0.0, dither16: float = 0.0) -> None:
        self.tracks = tracks
        self.weights = [t.weight for t in tracks]
        self.rng = random.Random(seed)
        self.min_rms = min_rms
        self.dither16 = dither16
        self._cache: dict[Path, np.ndarray] = {}

    def _open(self, path: Path) -> np.ndarray:
        array = self._cache.get(path)
        if array is None:
            array = np.load(path, mmap_mode="r")
            self._cache[path] = array
        return array

    def draw(self) -> tuple[np.ndarray, np.ndarray, int, int, str]:
        for _ in range(50):
            crop = self._draw_once()
            if self.min_rms <= 0.0 or float(np.sqrt(np.mean(np.square(crop[1], dtype=np.float64)))) >= self.min_rms:
                return crop
        return crop

    def _draw_once(self) -> tuple[np.ndarray, np.ndarray, int, int, str]:
        track = self.rng.choices(self.tracks, self.weights)[0]
        path, label, low = self.rng.choice(track.variants)
        high = track.rate
        g = math.gcd(low, high)
        step_low, step_high = low // g, high // g

        y = self._open(track.y)
        x = self._open(path)
        need_high = SEGMENT + 2 * MARGIN
        need_low = math.ceil(need_high * low / high) + 16
        last = min((x.shape[1] - need_low) // step_low, (y.shape[1] - need_high) // step_high)
        j = self.rng.randrange(0, max(1, last))
        start_low, start_high = j * step_low, j * step_high

        channel = self.rng.randrange(2)
        x_crop = np.asarray(x[channel, start_low: start_low + need_low], dtype=np.float32)
        y_crop = np.asarray(y[channel, start_high + MARGIN: start_high + MARGIN + SEGMENT], dtype=np.float32)

        # A little level variation, applied to both, so the model does not learn one loudness.
        gain = 10.0 ** (self.rng.uniform(-6.0, 1.0) / 20.0)
        x_crop = x_crop * gain
        y_crop = y_crop * gain

        # CD-quality sources are 16-bit with dither: a floor of noise the stored copies do not have.
        # Some lossless copies, and a few coded ones, are requantised the way such a file was made,
        # so the network meets that floor in training too. Never drawn for validation (dither16 0),
        # which would change the crops it measures.
        if self.dither16 > 0.0:
            chance = self.dither16 if label == "lossless" else self.dither16 * 0.3
            if self.rng.random() < chance:
                noise = np.random.default_rng(self.rng.getrandbits(32))
                tpdf = noise.uniform(-0.5, 0.5, x_crop.shape) + noise.uniform(-0.5, 0.5, x_crop.shape)
                x_crop = (np.clip(np.round(x_crop * 32768.0 + tpdf), -32768, 32767) / 32768.0).astype(np.float32)

        return x_crop, y_crop, low, high, label


def upsample(x_low: torch.Tensor, low: int, high: int) -> torch.Tensor:
    """Band-limited sinc interpolation on the device; kept identical at training and at export tests."""
    up = torchaudio.functional.resample(x_low, low, high, lowpass_filter_width=64, rolloff=0.99,
                                        resampling_method="sinc_interp_kaiser", beta=14.769656459379492)
    return up[..., MARGIN: MARGIN + SEGMENT]


def batch(sampler: Sampler, size: int, device: torch.device) -> tuple[torch.Tensor, torch.Tensor, torch.Tensor]:
    xs, ys, rates = [], [], []
    for _ in range(size):
        x_low, y, low, high, _ = sampler.draw()
        up = upsample(torch.from_numpy(x_low).to(device)[None], low, high)[0]
        if up.shape[0] < SEGMENT:
            up = torch.nn.functional.pad(up, (0, SEGMENT - up.shape[0]))
        xs.append(up)
        ys.append(torch.from_numpy(y).to(device))
        rates.append(high)
    return torch.stack(xs), torch.stack(ys), torch.tensor(rates, device=device)
