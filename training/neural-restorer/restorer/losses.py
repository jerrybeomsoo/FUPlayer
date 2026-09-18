"""Training objectives for the restorer, beyond the upscaler's spectral, phase and adversarial ones.

A codec at these rates is itself an optimiser: it places its quantisation noise under the masking threshold its
psychoacoustic model computes, band by band. What is left audible is where that failed or was given up: holes it
opened, bands it replaced with noise, the band above its low-pass, the pre-echo before a transient. The masked loss
measures error against the threshold the master itself sets, so the network is pushed where a listener would hear
the difference and spends nothing reproducing what the ear cannot resolve anyway.

The masking model is the one both papers describe for AAC and CELT: energies in Bark bands, spread with the
asymmetric Schroeder function, lowered by an offset that moves between 6 dB for a noise-like masker and 18 dB for a
tonal one according to the band's spectral flatness, and never below the absolute threshold of hearing.
"""
from __future__ import annotations

import math

import torch

from .model import N_FFT

# Full scale is taken to be 96 dB SPL for the absolute threshold, which is where a 16-bit dither floor sits at 0 dB.
FULL_SCALE_SPL = 96.0

_cache: dict[tuple[int, str], tuple[torch.Tensor, ...]] = {}


def _bark(f: torch.Tensor) -> torch.Tensor:
    return 13.0 * torch.atan(0.00076 * f) + 3.5 * torch.atan((f / 7500.0) ** 2)


def _masking_tables(rate: int, device: torch.device):
    key = (rate, str(device))
    if key in _cache:
        return _cache[key]
    bins = N_FFT // 2 + 1
    hz = torch.arange(bins, device=device, dtype=torch.float32) * rate / N_FFT
    z = _bark(hz)
    bands = int(math.ceil(float(z[-1]))) + 1
    index = z.floor().long().clamp(0, bands - 1)                   # bin -> Bark band
    member = torch.zeros(bands, bins, device=device)
    member[index, torch.arange(bins, device=device)] = 1.0
    counts = member.sum(dim=1).clamp_min(1.0)
    centres = torch.arange(bands, device=device, dtype=torch.float32) + 0.5
    dz = centres.view(-1, 1) - centres.view(1, -1)                 # masked band minus masking band
    sf_db = 15.81 + 7.5 * (dz + 0.474) - 17.5 * torch.sqrt(1.0 + (dz + 0.474) ** 2)
    spread = 10.0 ** (sf_db / 10.0)                                 # [masked, masker]
    khz = (hz / 1000.0).clamp_min(0.02)
    ath_db = 3.64 * khz ** -0.8 - 6.5 * torch.exp(-0.6 * (khz - 3.3) ** 2) + 1e-3 * khz ** 4
    ath_power = 10.0 ** ((ath_db - FULL_SCALE_SPL) / 10.0) * (N_FFT * 0.375) ** 2 / 4.0
    tables = (member, counts, spread, index, ath_power)
    _cache[key] = tables
    return tables


def masking_threshold(power: torch.Tensor, rate: int) -> torch.Tensor:
    """Per-bin masking threshold, in the same units as `power` [B, bins, frames] (|STFT|^2, periodic Hann)."""
    member, counts, spread, index, ath = _masking_tables(rate, power.device)
    band_energy = torch.einsum("kb,nbt->nkt", member, power)                         # [B, bands, T]
    log_mean = torch.einsum("kb,nbt->nkt", member, torch.log(power + 1e-20)) / counts.view(1, -1, 1)
    mean = band_energy / counts.view(1, -1, 1)
    flatness_db = 10.0 * (log_mean - torch.log(mean + 1e-20)) / math.log(10.0)       # <= 0
    tonality = (flatness_db / -60.0).clamp(0.0, 1.0)
    offset_db = tonality * 18.0 + (1.0 - tonality) * 6.0
    spread_energy = torch.einsum("kj,njt->nkt", spread, band_energy)
    threshold_band = spread_energy * 10.0 ** (-offset_db / 10.0) / counts.view(1, -1, 1)
    threshold = threshold_band[:, index, :]                                           # back to bins
    return torch.maximum(threshold, ath.view(1, -1, 1))


def masked_loss(spec_out: torch.Tensor, spec_target: torch.Tensor, rate: int) -> torch.Tensor:
    """Mean of log10(1 + error / threshold) over bins and frames, error taken on magnitudes.

    Magnitudes, because a band the network writes cannot have the master's phase and should not be punished for it;
    the phase is the adversarial and phase losses' business.
    """
    target_power = spec_target.abs().pow(2)
    threshold = masking_threshold(target_power, rate)
    error = (spec_out.abs() - spec_target.abs()).pow(2)
    return torch.log10(1.0 + error / threshold).mean()


def audible_noise_db(spec_out: torch.Tensor, spec_target: torch.Tensor, rate: int) -> float:
    """For validation: the mean, over frames, of the error-to-mask ratio in dB, counted where it is above zero."""
    threshold = masking_threshold(spec_target.abs().pow(2), rate)
    error = (spec_out.abs() - spec_target.abs()).pow(2)
    nmr_db = 10.0 * torch.log10(error / threshold + 1e-12)
    return float(nmr_db.clamp_min(0.0).mean())


BAND_FROM_HZ = 4_000.0
BAND_WIDTH_HZ = 500.0


def band_level_loss(spec_out: torch.Tensor, spec_target: torch.Tensor, rates: torch.Tensor) -> torch.Tensor:
    """The level of each 500 Hz band from 4 kHz over the crop against the master's, |dB| / 10.

    Codec low-passes at these rates sit anywhere from 14 to 20 kHz and spectral holes further down, so the bands are
    counted from 4 kHz rather than from where a CD-rate file ends.
    """
    p_out = spec_out.abs().pow(2).sum(dim=-1)
    p_tgt = spec_target.abs().pow(2).sum(dim=-1)
    batch, bins = p_out.shape
    nyquist = rates.float().view(-1, 1) / 2.0
    hz = torch.arange(bins, device=p_out.device, dtype=torch.float32).view(1, -1) * (nyquist / (bins - 1))
    count = int(math.ceil((float(rates.max()) / 2.0 - BAND_FROM_HZ) / BAND_WIDTH_HZ))
    valid = (hz >= BAND_FROM_HZ) & (hz < nyquist)
    index = ((hz - BAND_FROM_HZ) / BAND_WIDTH_HZ).floor().long().clamp(0, count - 1)
    zeros = p_out.new_zeros(batch, count)
    e_out = zeros.scatter_add(1, index, torch.where(valid, p_out, 0.0))
    e_tgt = zeros.scatter_add(1, index, torch.where(valid, p_tgt, 0.0))
    present = zeros.scatter_add(1, index, valid.to(p_out.dtype)) > 0
    floor = (e_tgt.amax(dim=1, keepdim=True) * 10.0 ** (-8.5)).clamp_min(1e-20)
    diff = (torch.log10(e_out + floor) - torch.log10(e_tgt + floor)).abs()
    return (diff * present).sum() / present.sum().clamp_min(1)
