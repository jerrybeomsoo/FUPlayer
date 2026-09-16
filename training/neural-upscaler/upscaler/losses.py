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
