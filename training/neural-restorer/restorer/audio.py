"""Running FFmpeg, reading what it writes, and lining a decoded copy up with its reference.

FFmpeg is found through the FFMPEG environment variable, or on the PATH. It needs libopus, libvorbis and libmp3lame
for the copies the restorer is trained on, and the soxr resampler.
"""
from __future__ import annotations

import os
import shutil
import subprocess

import numpy as np
import soundfile as sf

FFMPEG = os.environ.get("FFMPEG") or shutil.which("ffmpeg") or "ffmpeg"
SOXR = "aresample=resampler=soxr:precision=28"


def run(args: list[str]) -> None:
    result = subprocess.run([FFMPEG, "-hide_banner", "-loglevel", "error", "-y", *args], capture_output=True)
    if result.returncode != 0:
        raise RuntimeError(result.stderr.decode("utf-8", errors="replace")[-400:])


def read(path: str) -> tuple[np.ndarray, int]:
    data, rate = sf.read(path, dtype="float32", always_2d=True)
    return data.T.copy(), rate  # (channels, frames)


def align(reference: np.ndarray, decoded: np.ndarray) -> np.ndarray:
    """Slides the decoded copy against the reference to undo the encoder's delay, then trims to length."""
    frames = reference.shape[1]
    window = min(frames, 480_000)
    ref = reference[0, :window].astype(np.float64)
    search = 8192
    dec = decoded[0, : window + search].astype(np.float64)
    if dec.shape[0] < window:
        dec = np.pad(dec, (0, window - dec.shape[0]))
    # Correlation over lags 0..search in the frequency domain.
    n = 1 << int(np.ceil(np.log2(window + search)))
    spectrum = np.fft.rfft(dec, n) * np.conj(np.fft.rfft(ref, n))
    corr = np.fft.irfft(spectrum, n)[: search + 1]
    lag = int(np.argmax(corr))
    out = decoded[:, lag: lag + frames]
    if out.shape[1] < frames:
        out = np.pad(out, ((0, 0), (0, frames - out.shape[1])))
    return out
