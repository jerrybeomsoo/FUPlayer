"""Training objectives, as the design document lays them out.

    L = L_multi-STFT + l_phase * L_phase + l_adv * L_adv + l_fm * L_fm

Multi-resolution STFT loss at 512/1024/2048/4096 with hops of a quarter, spectral convergence plus
log-magnitude L1, and the band above 18 kHz weighted twice. Anti-wrapping phase losses in the
AP-BWE form: instantaneous phase, group delay across frequency and phase difference across time,
each weighted by the target's magnitude so that silence does not dominate and so that a generated
band, whose absolute phase cannot be known, is held to a consistent structure rather than to a
number. Discriminators are a multi-period one on the waveform and a multi-resolution one on the
complex STFT, with hinge losses and feature matching.
"""
from __future__ import annotations

import math

import torch
import torch.nn.functional as F
from torch import nn

RESOLUTIONS = [(512, 128), (1024, 256), (2048, 512), (4096, 1024)]
HIGH_BAND_HZ = 18_000.0
HIGH_BAND_WEIGHT = 2.0


def _stft(wave: torch.Tensor, n_fft: int, hop: int) -> torch.Tensor:
    window = torch.hann_window(n_fft, periodic=True, device=wave.device, dtype=wave.dtype)
    return torch.stft(wave, n_fft, hop, n_fft, window, center=True, return_complex=True)


def _band_weight(n_fft: int, rate: torch.Tensor, device) -> torch.Tensor:
    """[B, bins, 1] weights: 1 below 18 kHz, 2 above, per item because rates differ within a batch."""
    bins = n_fft // 2 + 1
    hz = torch.arange(bins, device=device).float()[None, :] * (rate[:, None].float() / n_fft)
    return torch.where(hz >= HIGH_BAND_HZ, HIGH_BAND_WEIGHT, 1.0)[:, :, None]


def multi_resolution_stft_loss(pred: torch.Tensor, target: torch.Tensor, rate: torch.Tensor) -> torch.Tensor:
    total = pred.new_zeros(())
    for n_fft, hop in RESOLUTIONS:
        p = _stft(pred, n_fft, hop).abs()
        t = _stft(target, n_fft, hop).abs()
        w = _band_weight(n_fft, rate, pred.device)
        # The floor is far below any music (about -80 dBFS over a crop) and keeps a silent target from
        # turning the ratio into a division by zero.
        sc = torch.linalg.norm((w * (t - p)).flatten(1), dim=1) / torch.linalg.norm((w * t).flatten(1), dim=1).clamp_min(0.1)
        log_l1 = (w * (torch.log(t.clamp_min(1e-7)) - torch.log(p.clamp_min(1e-7))).abs()).mean(dim=(1, 2))
        total = total + sc.mean() + log_l1.mean()
    return total / len(RESOLUTIONS)


BAND_FROM_HZ = 12_000.0
BAND_WIDTH_HZ = 500.0
BAND_FLOOR_DB = 85.0


def band_levels_db(spec_out: torch.Tensor, spec_target: torch.Tensor, rate: torch.Tensor):
    """Level of each 500 Hz band above 12 kHz over the whole crop, output against master, in dB.

    Returns the differences [B, bands], which bands each item has below its Nyquist, and where each band
    starts. Bands more than 85 dB under the master's loudest band above 12 kHz count only down to that floor.
    """
    p_out = spec_out.abs().pow(2).sum(dim=-1)
    p_tgt = spec_target.abs().pow(2).sum(dim=-1)
    batch, bins = p_out.shape
    nyquist = rate.float()[:, None] / 2.0
    hz = torch.arange(bins, device=p_out.device, dtype=torch.float32)[None, :] * (nyquist / (bins - 1))
    count = int(math.ceil((float(rate.max()) / 2.0 - BAND_FROM_HZ) / BAND_WIDTH_HZ))
    valid = (hz >= BAND_FROM_HZ) & (hz < nyquist)
    index = ((hz - BAND_FROM_HZ) / BAND_WIDTH_HZ).floor().long().clamp(0, count - 1)
    zeros = p_out.new_zeros(batch, count)
    e_out = zeros.scatter_add(1, index, torch.where(valid, p_out, 0.0))
    e_tgt = zeros.scatter_add(1, index, torch.where(valid, p_tgt, 0.0))
    present = zeros.scatter_add(1, index, valid.to(p_out.dtype)) > 0
    floor = (e_tgt.amax(dim=1, keepdim=True) * 10.0 ** (-BAND_FLOOR_DB / 10.0)).clamp_min(1e-20)
    diff = 10.0 * (torch.log10(e_out + floor) - torch.log10(e_tgt + floor))
    starts = BAND_FROM_HZ + BAND_WIDTH_HZ * torch.arange(count, device=p_out.device, dtype=torch.float32)
    return diff, present, starts


def band_level_loss(spec_out: torch.Tensor, spec_target: torch.Tensor, rate: torch.Tensor,
                    low: torch.Tensor | None = None, seam_weight: float = 1.0) -> torch.Tensor:
    """The level of each 500 Hz band above 12 kHz over the whole crop, against the master's: |dB| / 10.

    The per-bin losses see a band written 6 dB too loud across a kilohertz as forty bins among a
    thousand, each already noisy with texture. Summed over a crop, a band's energy has no texture left
    in it, and such an overshoot, where a source's own band ends and the written one begins, is the
    whole of the difference. With the source rates given, the bands from 2 kHz under a source's Nyquist
    to 1 kHz over it count seam_weight times: six bands of seventy-two are otherwise too few to move.
    """
    diff, present, starts = band_levels_db(spec_out, spec_target, rate)
    weight = present.to(diff.dtype)
    if low is not None and seam_weight != 1.0:
        edge = low.float()[:, None] / 2.0
        seam = (starts[None, :] >= edge - 2000.0) & (starts[None, :] < edge + 1000.0)
        weight = weight * torch.where(seam, seam_weight, 1.0)
    return (diff.abs() / 10.0 * weight).sum() / weight.sum().clamp_min(1)


def passband_identity_loss(spec_out: torch.Tensor, spec_in: torch.Tensor, rate: torch.Tensor,
                           passband_hz: torch.Tensor) -> torch.Tensor:
    """How far a lossless source's own passband moved: |out - in| relative to |in|, below passband_hz.

    Relative to the input, floored 85 dB under each frame's loudest bin the way the network's own reading
    is, so a quiet bin counts as much as a loud one and noise the network cannot see does not count.
    Items with no passband (coded sources) contribute nothing.
    """
    active = passband_hz > 0.0
    if not bool(active.any()):
        return spec_out.real.new_zeros(())
    out, inp = spec_out[active], spec_in[active]
    bins = inp.shape[1]
    hz = torch.arange(bins, device=inp.device, dtype=torch.float32)[None, :] * (rate[active][:, None].float() / (2 * (bins - 1)))
    mask = (hz < passband_hz[active][:, None]).to(inp.real.dtype)[:, :, None]
    magnitude = inp.abs()
    floor = (magnitude.amax(dim=1, keepdim=True) * 10.0 ** (-85.0 / 20.0)).clamp_min(1e-7)
    relative = (out - inp).abs() / torch.maximum(magnitude, floor)
    frames = inp.shape[2]
    return ((relative * mask).sum(dim=(1, 2)) / (mask.sum(dim=(1, 2)) * frames).clamp_min(1.0)).mean()


def _anti_wrap(x: torch.Tensor) -> torch.Tensor:
    """|x - 2*pi*round(x / 2*pi)|: the distance between two angles, whatever their wrapping."""
    return (x - 2.0 * torch.pi * torch.round(x / (2.0 * torch.pi))).abs()


def phase_loss(pred_spec: torch.Tensor, target_spec: torch.Tensor) -> torch.Tensor:
    """Instantaneous phase, group delay and phase time difference, magnitude weighted."""
    # Weighted by magnitude relative to the item's mean, not its loudest bin: against the loudest bin
    # nearly every weight is a thousandth or less, and the whole term vanished beside the STFT loss.
    mag = target_spec.abs()
    weight = mag / mag.flatten(1).mean(dim=1).clamp_min(1e-7)[:, None, None]
    p = torch.angle(pred_spec)
    t = torch.angle(target_spec)
    ip = (_anti_wrap(p - t) * weight).mean()
    gd = (_anti_wrap(torch.diff(p, dim=1) - torch.diff(t, dim=1)) * weight[:, 1:, :]).mean()
    ptd = (_anti_wrap(torch.diff(p, dim=2) - torch.diff(t, dim=2)) * weight[:, :, 1:]).mean()
    return ip + gd + ptd


class PeriodDiscriminator(nn.Module):
    def __init__(self, period: int, channels: tuple[int, ...] = (16, 64, 256, 512)) -> None:
        super().__init__()
        self.period = period
        layers, previous = [], 1
        for c in channels:
            layers.append(nn.utils.parametrizations.weight_norm(nn.Conv2d(previous, c, (5, 1), (3, 1), padding=(2, 0))))
            previous = c
        layers.append(nn.utils.parametrizations.weight_norm(nn.Conv2d(previous, previous, (5, 1), 1, padding=(2, 0))))
        self.layers = nn.ModuleList(layers)
        self.out = nn.utils.parametrizations.weight_norm(nn.Conv2d(previous, 1, (3, 1), 1, padding=(1, 0)))

    def forward(self, wave: torch.Tensor) -> tuple[torch.Tensor, list[torch.Tensor]]:
        b, n = wave.shape
        if n % self.period:
            wave = F.pad(wave, (0, self.period - n % self.period), mode="reflect")
        x = wave.view(b, 1, -1, self.period)
        features = []
        for layer in self.layers:
            x = F.leaky_relu(layer(x), 0.1)
            features.append(x)
        x = self.out(x)
        features.append(x)
        return x.flatten(1), features


class MultiPeriodDiscriminator(nn.Module):
    def __init__(self, periods: tuple[int, ...] = (2, 3, 5, 7, 11)) -> None:
        super().__init__()
        self.discriminators = nn.ModuleList(PeriodDiscriminator(p) for p in periods)

    def forward(self, wave: torch.Tensor):
        return [d(wave) for d in self.discriminators]


class SpectralDiscriminator(nn.Module):
    """Judges a complex STFT as two real channels, which is where MP3 birdies and phase smear show."""

    def __init__(self, n_fft: int, hop: int, channels: int = 32) -> None:
        super().__init__()
        self.n_fft, self.hop = n_fft, hop
        wn = nn.utils.parametrizations.weight_norm
        self.layers = nn.ModuleList([
            wn(nn.Conv2d(2, channels, (3, 9), padding=(1, 4))),
            wn(nn.Conv2d(channels, channels, (3, 9), stride=(1, 2), dilation=(1, 1), padding=(1, 4))),
            wn(nn.Conv2d(channels, channels, (3, 9), stride=(1, 2), dilation=(2, 1), padding=(2, 4))),
            wn(nn.Conv2d(channels, channels, (3, 9), stride=(1, 2), dilation=(4, 1), padding=(4, 4))),
            wn(nn.Conv2d(channels, channels, (3, 3), padding=(1, 1))),
        ])
        self.out = wn(nn.Conv2d(channels, 1, (3, 3), padding=(1, 1)))

    def forward(self, wave: torch.Tensor):
        spec = _stft(wave, self.n_fft, self.hop)
        x = torch.stack([spec.real, spec.imag], dim=1).transpose(2, 3)  # [B, 2, frames, bins]
        # Compress the dynamic range the way the loss does, so quiet ultrasonic bins are visible.
        x = torch.sign(x) * torch.log1p(x.abs() * 10.0)
        features = []
        for layer in self.layers:
            x = F.leaky_relu(layer(x), 0.1)
            features.append(x)
        x = self.out(x)
        features.append(x)
        return x.flatten(1), features


class MultiResolutionSpectralDiscriminator(nn.Module):
    def __init__(self) -> None:
        super().__init__()
        self.discriminators = nn.ModuleList(
            SpectralDiscriminator(n, h) for n, h in [(2048, 512), (1024, 256), (512, 128)])

    def forward(self, wave: torch.Tensor):
        return [d(wave) for d in self.discriminators]


def discriminator_loss(real_outputs, fake_outputs) -> torch.Tensor:
    loss = 0.0
    for (real, _), (fake, _) in zip(real_outputs, fake_outputs):
        loss = loss + F.relu(1.0 - real).mean() + F.relu(1.0 + fake).mean()
    return loss


def generator_adversarial_loss(fake_outputs) -> torch.Tensor:
    return sum(F.relu(1.0 - fake).mean() for fake, _ in fake_outputs)


def feature_matching_loss(real_outputs, fake_outputs) -> torch.Tensor:
    loss = 0.0
    for (_, real_features), (_, fake_features) in zip(real_outputs, fake_outputs):
        for r, f in zip(real_features, fake_features):
            loss = loss + (r.detach() - f).abs().mean()
    return loss
