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
from typing import Optional

import numpy as np
import torch
import torchaudio

SEGMENT = 32_768          # samples at the training rate: 65 frames at a hop of 512
MARGIN = 4_096            # extra either side, so the upsampler's edges fall outside the crop
EDGE = 4_096              # extra either side again, so a synthetic downsampler's edges fall outside too


@dataclass
class Crop:
    """One training example. A synthetic lossless one carries the wide master crop instead of x_low."""
    x_low: Optional[np.ndarray]
    y: np.ndarray
    low: int
    high: int
    label: str
    y_wide: Optional[np.ndarray] = None
    edge_low: int = 0
    rolloff: float = 0.99
    width: int = 64
    dither: bool = False

    @property
    def lossless(self) -> bool:
        return self.label.startswith("lossless")

    @property
    def passband_hz(self) -> float:
        """Below this the input is the master itself, for a lossless crop; 0 for a coded one."""
        if not self.lossless:
            return 0.0
        if self.y_wide is not None:
            # Where the round trip through this anti-alias filter and the player's interpolator is still
            # flat to 0.1 dB. Measured on noise for roll-offs 0.87 to 0.99 and 16 to 64 zero crossings,
            # this rule meets every one with 30 to 430 Hz to spare: a short kernel starts rolling off
            # well before its nominal edge, a long one runs to within a kilohertz of it.
            return self.rolloff * self.low / 2.0 * (1.0 - 3.0 / self.width)
        return 0.9 * self.low / 2.0


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

    def __init__(self, tracks: list[Track], seed: int, min_rms: float = 0.0, dither16: float = 0.0,
                 synthetic_lossless: float = 0.0, wide_filters: float = 0.0) -> None:
        self.tracks = tracks
        self.weights = [t.weight for t in tracks]
        self.rng = random.Random(seed)
        self.min_rms = min_rms
        self.dither16 = dither16
        self.synthetic_lossless = synthetic_lossless
        self.wide_filters = wide_filters
        self._cache: dict[Path, np.ndarray] = {}

    def _open(self, path: Path) -> np.ndarray:
        array = self._cache.get(path)
        if array is None:
            array = np.load(path, mmap_mode="r")
            self._cache[path] = array
        return array

    def draw(self) -> Crop:
        for _ in range(50):
            crop = self._draw_once()
            if self.min_rms <= 0.0 or float(np.sqrt(np.mean(np.square(crop.y, dtype=np.float64)))) >= self.min_rms:
                return crop
        return crop

    def _draw_synthetic(self, track: Track) -> Crop:
        """A lossless 44.1 or 48 kHz copy made from the master on the device, as a CD or a download is.

        The anti-alias filter varies, a short kernel rolling off from 19 kHz to a long one from 21.8, as
        mastering resamplers do; half the copies are also requantised to 16 bits with dither.
        """
        high = track.rate
        low = 48_000 if high == 96_000 and self.rng.random() < 0.7 else 44_100
        g = math.gcd(low, high)
        step_low, step_high = low // g, high // g
        units = math.ceil(EDGE / step_high)
        edge_high, edge_low = units * step_high, units * step_low

        y = self._open(track.y)
        need_high = SEGMENT + 2 * MARGIN
        wide = need_high + 2 * edge_high + 2 * step_high * 64
        last = (y.shape[1] - wide) // step_high
        j = self.rng.randrange(0, max(1, last))
        start_high = j * step_high
        channel = self.rng.randrange(2)
        y_wide = np.asarray(y[channel, start_high: start_high + wide], dtype=np.float32)
        gain = 10.0 ** (self.rng.uniform(-6.0, 1.0) / 20.0)
        y_wide = y_wide * gain
        y_crop = y_wide[edge_high + MARGIN: edge_high + MARGIN + SEGMENT].copy()
        # A share of the copies through the long, wide filters a careful mastering resampler uses, whose
        # passband runs to within a kilohertz of the new Nyquist: the case where a network that expects a
        # band to end earlier writes its own on top of the one still there.
        if self.wide_filters > 0.0 and self.rng.random() < self.wide_filters:
            rolloff, width = self.rng.uniform(0.95, 0.99), self.rng.choice((32, 64))
        else:
            rolloff, width = self.rng.uniform(0.87, 0.99), self.rng.choice((16, 32, 64))
        return Crop(None, y_crop, low, high, "lossless-synthetic", y_wide=y_wide, edge_low=edge_low,
                    rolloff=rolloff, width=width, dither=self.rng.random() < 0.5)

    def _draw_once(self) -> Crop:
        track = self.rng.choices(self.tracks, self.weights)[0]
        if self.synthetic_lossless > 0.0 and self.rng.random() < self.synthetic_lossless:
            return self._draw_synthetic(track)
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

        return Crop(x_crop, y_crop, low, high, label)


def upsample(x_low: torch.Tensor, low: int, high: int) -> torch.Tensor:
    """Band-limited sinc interpolation on the device; kept identical at training and at export tests."""
    up = torchaudio.functional.resample(x_low, low, high, lowpass_filter_width=64, rolloff=0.99,
                                        resampling_method="sinc_interp_kaiser", beta=14.769656459379492)
    return up[..., MARGIN: MARGIN + SEGMENT]


def low_input(crop: Crop, device: torch.device) -> torch.Tensor:
    """The crop's 44.1 or 48 kHz input on the device: stored, or made from the master for a synthetic one."""
    if crop.x_low is not None:
        return torch.from_numpy(crop.x_low).to(device)[None]
    wide = torch.from_numpy(crop.y_wide).to(device, torch.float64)[None]
    x = torchaudio.functional.resample(wide, crop.high, crop.low, lowpass_filter_width=crop.width,
                                       rolloff=crop.rolloff, resampling_method="sinc_interp_kaiser",
                                       beta=14.769656459379492)
    x = x[..., crop.edge_low:].float()
    if crop.dither:
        tpdf = torch.rand_like(x) - torch.rand_like(x)
        x = torch.clamp(torch.round(x * 32768.0 + tpdf), -32768, 32767) / 32768.0
    return x


def batch(sampler: Sampler, size: int, device: torch.device, flip_lossy: float = 0.0, flip_lossless: float = 0.0):
    """x, y, rates, the source flag the network is given, the passband below which x must equal y, and
    the source's own rate.

    The flag is sometimes wrong on purpose, as it is in use: a lossy file converted to FLAC arrives flagged
    lossless, and a capture of a lossless stream arrives flagged lossy. The passband target applies only
    where the flag and the source agree.
    """
    xs, ys, rates, flags, passbands, lows = [], [], [], [], [], []
    for _ in range(size):
        crop = sampler.draw()
        up = upsample(low_input(crop, device), crop.low, crop.high)[0]
        if up.shape[0] < SEGMENT:
            up = torch.nn.functional.pad(up, (0, SEGMENT - up.shape[0]))
        xs.append(up)
        ys.append(torch.from_numpy(crop.y).to(device))
        rates.append(crop.high)
        lossy = 0.0 if crop.lossless else 1.0
        if crop.lossless and sampler.rng.random() < flip_lossless:
            lossy = 1.0
        elif not crop.lossless and sampler.rng.random() < flip_lossy:
            lossy = 0.0
        flags.append(lossy)
        passbands.append(crop.passband_hz if lossy == 0.0 else 0.0)
        lows.append(crop.low)
    return (torch.stack(xs), torch.stack(ys), torch.tensor(rates, device=device),
            torch.tensor(flags, device=device), torch.tensor(passbands, device=device),
            torch.tensor(lows, device=device))
