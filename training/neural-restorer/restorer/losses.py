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
BAND_FLOOR_DB = 85.0

# A band the codec emptied by this much counts as wholly taken away.
DEFICIT_FULL_DB = 20.0

BAND_EMPHASIS_FROM_HZ = 16_000.0


def _band_layout(bins: int, rates: torch.Tensor, device: torch.device):
    """Per item: each bin's frequency, which 500 Hz band it belongs to, whether it counts, and how many bands."""
    nyquist = rates.float().view(-1, 1) / 2.0
    hz = torch.arange(bins, device=device, dtype=torch.float32).view(1, -1) * (nyquist / (bins - 1))
    count = int(math.ceil((float(rates.max()) / 2.0 - BAND_FROM_HZ) / BAND_WIDTH_HZ))
    valid = (hz >= BAND_FROM_HZ) & (hz < nyquist)
    index = ((hz - BAND_FROM_HZ) / BAND_WIDTH_HZ).floor().long().clamp(0, count - 1)
    return hz, index, valid, count


def _band_energy(power: torch.Tensor, index: torch.Tensor, valid: torch.Tensor, count: int) -> torch.Tensor:
    zeros = power.new_zeros(power.shape[0], count)
    return zeros.scatter_add(1, index, torch.where(valid, power, 0.0))


def coded_deficit(spec_in: torch.Tensor, spec_target: torch.Tensor, rates: torch.Tensor) -> torch.Tensor:
    """How much of each 500 Hz band from 4 kHz the codec took away: 0 where it kept the band, 1 where it is gone.

    A codec low-pass lands anywhere from 14 to 20 kHz and its holes further down, and the same encoder moves it
    with the music, so where a network has to invent a band cannot be read off the rate. This reads it off the
    crop: the master's level in each band against the coded copy's, floored so that two empty bands read as no
    deficit rather than as a large one. Nothing here is differentiated; it only says which bands to weigh.
    """
    with torch.no_grad():
        p_in = spec_in.abs().pow(2).sum(dim=-1)
        p_tgt = spec_target.abs().pow(2).sum(dim=-1)
        _, index, valid, count = _band_layout(p_in.shape[1], rates, p_in.device)
        e_in = _band_energy(p_in, index, valid, count)
        e_tgt = _band_energy(p_tgt, index, valid, count)
        floor = (e_tgt.amax(dim=1, keepdim=True) * 10.0 ** (-BAND_FLOOR_DB / 10.0)).clamp_min(1e-20)
        short_db = 10.0 * (torch.log10(e_tgt + floor) - torch.log10(e_in + floor))
        return (short_db / DEFICIT_FULL_DB).clamp(0.0, 1.0)


def deficit_bin_weight(deficit: torch.Tensor, n_fft: int, rates: torch.Tensor, relax: float) -> torch.Tensor:
    """[B, bins, 1] weights for a per-bin loss: 1 where the codec kept the band, `relax` where it emptied it.

    Above a codec's low-pass the master's bins cannot be predicted from what is left, so a per-bin loss there is
    minimised by writing almost nothing: the error of a band written at the master's level with the wrong fine
    structure is larger than the error of no band at all. That is the whole of why a network fitted with these
    losses leaves the top of the band twenty-five decibels short. Relaxing them where the codec emptied a band
    leaves the level to `band_level_loss` and the texture to the discriminators, which is how the upscaler is
    fitted to the band above a source's Nyquist frequency.
    """
    bins = n_fft // 2 + 1
    _, index, _, count = _band_layout(bins, rates, deficit.device)
    alpha = deficit.gather(1, index.clamp(0, deficit.shape[1] - 1))
    return (1.0 - (1.0 - relax) * alpha).unsqueeze(-1)


def band_level_loss(spec_out: torch.Tensor, spec_target: torch.Tensor, rates: torch.Tensor,
                    emphasis: float = 1.0, deficit: torch.Tensor | None = None,
                    deficit_weight: float = 1.0) -> torch.Tensor:
    """The level of each 500 Hz band from 4 kHz over the crop against the master's, |dB| / 10.

    Codec low-passes at these rates sit anywhere from 14 to 20 kHz and spectral holes further down, so the bands
    are counted from 4 kHz rather than from where a CD-rate file ends.

    `emphasis` multiplies the bands above 16 kHz, and `deficit_weight` the bands a codec emptied, as
    `coded_deficit` measures them. Every band counting the same is every band counting by how many there are, and
    the bands that need writing are a handful of quiet ones: a network can leave them 25 dB short and pay almost
    nothing, which is what one fitted to a corpus with a lot of quiet top bands does.
    """
    p_out = spec_out.abs().pow(2).sum(dim=-1)
    p_tgt = spec_target.abs().pow(2).sum(dim=-1)
    _, index, valid, count = _band_layout(p_out.shape[1], rates, p_out.device)
    e_out = _band_energy(p_out, index, valid, count)
    e_tgt = _band_energy(p_tgt, index, valid, count)
    present = _band_energy(valid.to(p_out.dtype).expand(p_out.shape[0], -1), index, valid, count) > 0
    floor = (e_tgt.amax(dim=1, keepdim=True) * 10.0 ** (-BAND_FLOOR_DB / 10.0)).clamp_min(1e-20)
    diff = (torch.log10(e_out + floor) - torch.log10(e_tgt + floor)).abs()
    weight = present.to(p_out.dtype)
    if emphasis != 1.0:
        centre = BAND_FROM_HZ + (torch.arange(count, device=p_out.device, dtype=torch.float32) + 0.5) * BAND_WIDTH_HZ
        weight = weight * torch.where(centre.view(1, -1) >= BAND_EMPHASIS_FROM_HZ, emphasis, 1.0)
    if deficit is not None and deficit_weight != 1.0:
        weight = weight * (1.0 + (deficit_weight - 1.0) * deficit)
    return (diff * weight).sum() / weight.sum().clamp_min(1)


# The neighbours a bin's level is judged against: this many either side, 234 Hz at 48 kHz.
LINE_WIDTH = 10


def _neighbour_mean(level: torch.Tensor, width: int = LINE_WIDTH) -> torch.Tensor:
    """[B, bins] -> the mean of each bin's 2 * width + 1 neighbours, itself included, edges extended."""
    padded = torch.nn.functional.pad(level.unsqueeze(1), (width, width), mode="replicate")
    return torch.nn.functional.avg_pool1d(padded, 2 * width + 1, stride=1).squeeze(1)


def line_loss(spec_out: torch.Tensor, spec_target: torch.Tensor, rates: torch.Tensor,
              deficit: torch.Tensor | None = None, deficit_weight: float = 1.0) -> torch.Tensor:
    """How far each bin's level over the crop sits from its neighbours', against the same in the master: |dB| / 10.

    The network writes every bin through its own row of the head, and nothing else ties a bin to the next one, so a
    row that came out a little low draws a dark line across every track at that bin. No other term sees it: one bin
    10 dB low moves its 500 Hz band by 0.4 dB, and a per-bin spectral loss over a band that has to be invented is
    mostly texture. Here each bin is measured against its own neighbourhood, in the output and in the master, and it
    is the difference of the two that counts. The master's fine structure moves with the music from crop to crop and
    comes to nothing on average; a row that is low on every input does not, and that is all this can teach.

    `deficit`, from `coded_deficit`, weighs the bands a codec emptied `deficit_weight` times over, as the band-level
    term does: that is where every bin is the network's own.
    """
    p_out = spec_out.abs().pow(2).mean(dim=-1)
    p_tgt = spec_target.abs().pow(2).mean(dim=-1)
    _, index, valid, _ = _band_layout(p_out.shape[1], rates, p_out.device)
    floor = (p_tgt.amax(dim=1, keepdim=True) * 10.0 ** (-BAND_FLOOR_DB / 10.0)).clamp_min(1e-20)
    l_out = torch.log10(p_out + floor)
    l_tgt = torch.log10(p_tgt + floor)
    diff = ((l_out - _neighbour_mean(l_out)) - (l_tgt - _neighbour_mean(l_tgt))).abs()
    weight = valid.to(p_out.dtype).expand(p_out.shape[0], -1).clone()
    if deficit is not None and deficit_weight != 1.0:
        alpha = deficit.gather(1, index.expand(p_out.shape[0], -1).clamp(0, deficit.shape[1] - 1))
        weight = weight * (1.0 + (deficit_weight - 1.0) * alpha)
    return (diff * weight).sum() / weight.sum().clamp_min(1)


def line_profile_db(spec_out: torch.Tensor, spec_target: torch.Tensor) -> torch.Tensor:
    """For validation: each bin's level against its neighbours', output less master, dB, [B, bins]. Averaged over
    many crops, what is left is the network's own lines."""
    p_out = spec_out.abs().pow(2).mean(dim=-1)
    p_tgt = spec_target.abs().pow(2).mean(dim=-1)
    floor = (p_tgt.amax(dim=1, keepdim=True) * 10.0 ** (-BAND_FLOOR_DB / 10.0)).clamp_min(1e-20)
    l_out = 10.0 * torch.log10(p_out + floor)
    l_tgt = 10.0 * torch.log10(p_tgt + floor)
    return (l_out - _neighbour_mean(l_out)) - (l_tgt - _neighbour_mean(l_tgt))
