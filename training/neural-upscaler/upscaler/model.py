"""The neural upscaler: a dual-path Fourier-domain vocoder in the manner of Vocos and AP-BWE.

The network never sees a waveform. It reads the magnitude spectrum of the upsampled lossy signal,
frame by frame, and answers with five numbers per bin:

    g, theta   a complex ratio mask, exp(g + i*theta), applied to the input where the input carries
               music; it removes coding artefacts without inventing a phase
    m, phi     a log-magnitude and a phase for a generated component, for the bins the codec or the
               sample rate emptied
    u          a per-bin crossover, w = sigmoid(u), between the two

    S = w * X * exp(g + i*theta) + (1 - w) * exp(m) * exp(i*phi)

The crossover is the document's Linkwitz-Riley refiner made data-dependent. A fixed crossover at
20 kHz assumes every input is cut at 20 kHz; an MP3 at 128 kbit/s is cut at 16, a CD-rate lossless
file at 22.05, and a learned per-bin gate puts the seam wherever this frame's music actually stops.

The heads are initialised so that the untrained network passes its input through unchanged: mask
zero, generated magnitude negligible, crossover leaning to the input. Training then starts from
plain upsampling rather than from noise, and can only move away from it where that helps.
"""
from __future__ import annotations

import math

import torch
from torch import nn

N_FFT = 2048
HOP = 512
BINS = N_FFT // 2 + 1

# The network reads nothing more than 85 dB below the loudest bin of the same frame. Below that lies
# noise that says how a copy was stored rather than what it sounds like: float16 rounding in the
# training corpus sits 114 dB under the frame's peak on median and 84 dB at worst in 999 bins of 1000,
# 16-bit dither about as low, and a clean decode in the player nothing at all. Read with an absolute
# floor, those three looked different in exactly the band above a codec's cutoff, where the network
# decides what is missing; measured on held-out tracks, clean inputs came out up to 1.9 dB further
# from the master between 16 and 22 kHz than the stored copies of the same decodes.
FLOOR_BELOW_PEAK_DB = 85.0
FLOOR_BELOW_PEAK = 10.0 ** (-FLOOR_BELOW_PEAK_DB / 20.0)


def stft(wave: torch.Tensor, n_fft: int = N_FFT, hop: int = HOP) -> torch.Tensor:
    """[B, samples] -> complex [B, bins, frames]. Periodic Hann, centred, as the player will do it."""
    window = torch.hann_window(n_fft, periodic=True, device=wave.device, dtype=wave.dtype)
    return torch.stft(wave, n_fft, hop, n_fft, window, center=True, pad_mode="constant", return_complex=True)


def istft(spec: torch.Tensor, length: int, n_fft: int = N_FFT, hop: int = HOP) -> torch.Tensor:
    window = torch.hann_window(n_fft, periodic=True, device=spec.device, dtype=torch.float32)
    return torch.istft(spec, n_fft, hop, n_fft, window, center=True, length=length)


def log_features(magnitude: torch.Tensor) -> torch.Tensor:
    """[B, bins, frames] magnitudes -> log magnitudes floored relative to each frame's peak.

    Frame by frame, so a stream cut into chunks sees exactly what the whole signal would.
    """
    floor = (magnitude.amax(dim=1, keepdim=True) * FLOOR_BELOW_PEAK).clamp_min(1e-7)
    return torch.log(torch.maximum(magnitude, floor))


class ConvNeXtBlock(nn.Module):
    """1-D ConvNeXt block over time, bins as channels: depthwise 7, norm, inverted bottleneck, scale."""

    def __init__(self, dim: int, intermediate: int, layer_scale: float) -> None:
        super().__init__()
        self.dwconv = nn.Conv1d(dim, dim, kernel_size=7, padding=3, groups=dim)
        self.norm = nn.LayerNorm(dim, eps=1e-6)
        self.pwconv1 = nn.Linear(dim, intermediate)
        self.act = nn.GELU()
        self.pwconv2 = nn.Linear(intermediate, dim)
        self.gamma = nn.Parameter(torch.full((dim,), layer_scale))

    def forward(self, x: torch.Tensor) -> torch.Tensor:
        residual = x
        x = self.dwconv(x).transpose(1, 2)
        x = self.pwconv2(self.act(self.pwconv1(self.norm(x))))
        return residual + (self.gamma * x).transpose(1, 2)


class Upscaler(nn.Module):
    def __init__(self, dim: int = 384, intermediate: int = 1152, blocks: int = 8, bins: int = BINS) -> None:
        super().__init__()
        self.bins = bins
        self.embed = nn.Conv1d(bins, dim, kernel_size=3, padding=1)
        self.embed_norm = nn.LayerNorm(dim, eps=1e-6)
        self.blocks = nn.ModuleList(ConvNeXtBlock(dim, intermediate, 1.0 / blocks) for _ in range(blocks))
        self.final_norm = nn.LayerNorm(dim, eps=1e-6)
        self.head = nn.Conv1d(dim, 5 * bins, kernel_size=1)
        self._init_head()

    def _init_head(self) -> None:
        nn.init.normal_(self.head.weight, std=1e-3)
        with torch.no_grad():
            bias = self.head.bias.view(5, self.bins)
            bias[0].zero_()          # g: unit mask gain
            bias[1].zero_()          # theta: no rotation
            bias[2].fill_(-12.0)     # m: generated magnitude negligible
            bias[3].zero_()          # phi
            bias[4].fill_(6.0)       # u: crossover on the input, w = 0.9975

    @staticmethod
    def features(spec: torch.Tensor) -> torch.Tensor:
        """Log magnitude, floored 85 dB below the frame's loudest bin (and never below 1e-7)."""
        return log_features(spec.abs())

    def forward_features(self, feats: torch.Tensor) -> tuple[torch.Tensor, ...]:
        """[B, bins, frames] log magnitude -> (g, theta, m, phi, u), each [B, bins, frames]."""
        x = self.embed(feats)
        x = self.embed_norm(x.transpose(1, 2)).transpose(1, 2)
        for block in self.blocks:
            x = block(x)
        x = self.final_norm(x.transpose(1, 2)).transpose(1, 2)
        out = self.head(x)
        b, _, t = out.shape
        g, theta, m, phi, u = out.view(b, 5, self.bins, t).unbind(1)
        return g.clamp(-8.0, 4.0), theta, m.clamp(-20.0, 8.0), phi, u

    def forward(self, spec: torch.Tensor) -> torch.Tensor:
        """Complex input spectrum [B, bins, frames] -> complex output spectrum of the same shape."""
        g, theta, m, phi, u = self.forward_features(self.features(spec))
        w = torch.sigmoid(u)
        kept = spec * torch.exp(torch.complex(g, theta))
        made = torch.exp(torch.complex(m, phi))
        return w * kept + (1.0 - w) * made

    def parameter_count(self) -> int:
        return sum(p.numel() for p in self.parameters())


class ExportWrapper(nn.Module):
    """What the player runs: real tensors in, real tensors out, no complex numbers in the graph.

    Input  : re, im  [B, bins, frames]  the STFT of the upsampled signal
    Output : re, im  [B, bins, frames]  the STFT of the repaired signal
    The player does the STFT and the overlap-add itself.
    """

    def __init__(self, model: Upscaler) -> None:
        super().__init__()
        self.model = model

    def forward(self, re: torch.Tensor, im: torch.Tensor) -> tuple[torch.Tensor, torch.Tensor]:
        magnitude = torch.sqrt(re * re + im * im)
        feats = log_features(magnitude)
        g, theta, m, phi, u = self.model.forward_features(feats)
        w = torch.sigmoid(u)
        gain = torch.exp(g)
        cos_t, sin_t = torch.cos(theta), torch.sin(theta)
        kept_re = gain * (re * cos_t - im * sin_t)
        kept_im = gain * (re * sin_t + im * cos_t)
        made = torch.exp(m)
        made_re = made * torch.cos(phi)
        made_im = made * torch.sin(phi)
        return w * kept_re + (1.0 - w) * made_re, w * kept_im + (1.0 - w) * made_im


if __name__ == "__main__":
    net = Upscaler()
    print(f"parameters: {net.parameter_count() / 1e6:.2f} M")
    wave = torch.randn(2, 96000) * 0.1
    spec = stft(wave)
    out = net(spec)
    rec = istft(out, wave.shape[1])
    print("spec", tuple(spec.shape), "identity error at init:",
          float((rec - wave).pow(2).mean() / wave.pow(2).mean()))
    receptive = 1 + 2 + len(net.blocks) * 6
    print(f"receptive field: {receptive} frames = {receptive * HOP / 96000 * 1000:.0f} ms at 96 kHz")
    _ = math
