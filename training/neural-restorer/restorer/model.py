"""The low-latency restorer: a lossy 44.1 or 48 kHz stream in, a pseudo-lossless one at the same rate out.

What a codec at 96 to 160 kbit/s does wrong falls in two kinds, and the model is built around both.

Energy that is gone. AAC quantises every line with a dead zone, so weak lines round to zero: holes in the spectrum
that open and close from frame to frame, and above the encoder's low-pass nothing at all. The network generates a
magnitude and a phase for bins the input leaves empty.

Energy in the wrong shape. Opus keeps each band's energy exactly and codes only its shape, with a few pulses when
bits are short, or a folded copy of a lower band when there are none; AAC substitutes noise for bands it judges
noise-like. The level is right and the fine structure is not. The network applies a complex mask where the input
carries energy, reshaping it without inventing a level.

    S = w * X * exp(g + i*theta) + (1 - w) * exp(m) * exp(i*phi),  w = sigmoid(u), per bin

Both codecs code stereo as mid and side, and at these rates collapse the side of the upper bands to a level ratio
(intensity stereo). The network therefore reads and writes mid and side, one network for both, with a learned
vector telling the heads which of the two they are writing. Another tells it the rate, 44.1 or 48 kHz, since a bin
is 21.5 Hz wide at one and 23.4 at the other and every codec's band edges sit at frequencies, not bins.

Latency. The transform is 2,048 points at a hop of 256, the frequency resolution of the codecs' own long blocks.
Every layer is causal except one: a depthwise convolution after the embedding that looks LOOKAHEAD frames ahead.
Streaming runs the same layers with their past carried as state, and the output frame lags the newest input frame
by LOOKAHEAD. The first LOOKAHEAD frames out answer for frames before the stream began and are thrown away; the
causal blocks' state is then cleared (`WARMUP_CLEARS`), which is what the whole-signal pass's padding amounts to,
and from there the stream matches it exactly. The delay of the whole stage is N_FFT + LOOKAHEAD * HOP samples plus
the frames the player gathers per call: 3,584 samples, 75 ms at 48 kHz, for one frame at a time.
"""
from __future__ import annotations

import torch
import torch.nn.functional as F
from torch import nn

N_FFT = 2048
HOP = 256
BINS = N_FFT // 2 + 1
LOOKAHEAD = 6
FLOOR_BELOW_PEAK = 10.0 ** (-85.0 / 20.0)
SQRT_HALF = 0.7071067811865476


def stft(wave: torch.Tensor, n_fft: int = N_FFT, hop: int = HOP) -> torch.Tensor:
    """[B, samples] -> complex [B, bins, frames]: periodic Hann, centred, as the player does it."""
    window = torch.hann_window(n_fft, periodic=True, device=wave.device, dtype=wave.dtype)
    return torch.stft(wave, n_fft, hop, n_fft, window, center=True, pad_mode="constant", return_complex=True)


def istft(spec: torch.Tensor, length: int, n_fft: int = N_FFT, hop: int = HOP) -> torch.Tensor:
    window = torch.hann_window(n_fft, periodic=True, device=spec.device, dtype=torch.float32)
    return torch.istft(spec, n_fft, hop, n_fft, window, center=True, length=length)


def to_mid_side(left: torch.Tensor, right: torch.Tensor) -> tuple[torch.Tensor, torch.Tensor]:
    return (left + right) * SQRT_HALF, (left - right) * SQRT_HALF


def to_left_right(mid: torch.Tensor, side: torch.Tensor) -> tuple[torch.Tensor, torch.Tensor]:
    return (mid + side) * SQRT_HALF, (mid - side) * SQRT_HALF


def features(mid_mag: torch.Tensor, side_mag: torch.Tensor) -> torch.Tensor:
    """Log magnitudes of mid and side, floored together 85 dB below the frame's loudest mid or side bin.

    [B, bins, frames] each -> [B, 2 * bins, frames]. One floor for both, so a quiet side is read as quiet rather than
    lifted to the mid's floor. Frame by frame, so a stream cut into blocks sees what the whole signal would.
    """
    peak = torch.maximum(mid_mag.amax(dim=1, keepdim=True), side_mag.amax(dim=1, keepdim=True))
    floor = (peak * FLOOR_BELOW_PEAK).clamp_min(1e-7)
    return torch.cat([torch.log(torch.maximum(mid_mag, floor)), torch.log(torch.maximum(side_mag, floor))], dim=1)


class StreamConv(nn.Module):
    """A 1-D convolution over frames that sees `lookahead` frames ahead and the rest behind.

    forward pads the way a stream that started in silence would; step carries the frames behind as state, so running
    a signal in blocks of any size gives the answer forward gives, `lookahead` frames later.
    """

    def __init__(self, cin: int, cout: int, kernel: int, groups: int = 1, lookahead: int = 0) -> None:
        super().__init__()
        self.conv = nn.Conv1d(cin, cout, kernel, groups=groups)
        self.kernel = kernel
        self.lookahead = lookahead
        self.cin = cin

    def forward(self, x: torch.Tensor) -> torch.Tensor:
        return self.conv(F.pad(x, (self.kernel - 1 - self.lookahead, self.lookahead)))

    def step(self, x: torch.Tensor, state: torch.Tensor) -> tuple[torch.Tensor, torch.Tensor]:
        """x [B, cin, T >= 1], state [B, cin, kernel - 1] -> y [B, cout, T], new state."""
        joined = torch.cat([state, x], dim=2)
        return self.conv(joined), joined[:, :, -(self.kernel - 1):]

    def state_shape(self) -> tuple[int, int]:
        return self.cin, self.kernel - 1


class CausalConvNeXtBlock(nn.Module):
    def __init__(self, dim: int, intermediate: int, layer_scale: float, kernel: int = 7) -> None:
        super().__init__()
        self.dwconv = StreamConv(dim, dim, kernel, groups=dim)
        self.norm = nn.LayerNorm(dim, eps=1e-6)
        self.pwconv1 = nn.Linear(dim, intermediate)
        self.act = nn.GELU()
        self.pwconv2 = nn.Linear(intermediate, dim)
        self.gamma = nn.Parameter(torch.full((dim,), layer_scale))

    def _mix(self, residual: torch.Tensor, x: torch.Tensor) -> torch.Tensor:
        x = self.pwconv2(self.act(self.pwconv1(self.norm(x.transpose(1, 2)))))
        return residual + (self.gamma * x).transpose(1, 2)

    def forward(self, x: torch.Tensor) -> torch.Tensor:
        return self._mix(x, self.dwconv(x))

    def step(self, x: torch.Tensor, state: torch.Tensor) -> tuple[torch.Tensor, torch.Tensor]:
        y, state = self.dwconv.step(x, state)
        return self._mix(x, y), state


class Restorer(nn.Module):
    def __init__(self, dim: int = 256, intermediate: int = 768, blocks: int = 8, bins: int = BINS,
                 lookahead: int = LOOKAHEAD) -> None:
        super().__init__()
        self.bins = bins
        self.dim = dim
        self.lookahead = lookahead
        self.embed = nn.Conv1d(2 * bins, dim, kernel_size=1)
        self.embed_norm = nn.LayerNorm(dim, eps=1e-6)
        # 44.1 kHz (0) or 48 kHz (1).
        self.rate = nn.Parameter(torch.zeros(2, dim))
        # The one layer that looks ahead: 2 * lookahead + 1 frames centred on the frame being answered.
        self.ahead = StreamConv(dim, dim, 2 * lookahead + 1, groups=dim, lookahead=lookahead)
        self.ahead_mix = nn.Conv1d(dim, dim, kernel_size=1)
        self.blocks = nn.ModuleList(CausalConvNeXtBlock(dim, intermediate, 1.0 / blocks) for _ in range(blocks))
        self.final_norm = nn.LayerNorm(dim, eps=1e-6)
        # Which of mid (0) and side (1) the shared head is writing.
        self.channel = nn.Parameter(torch.zeros(2, dim))
        self.head = nn.Conv1d(dim, 5 * bins, kernel_size=1)
        self._init_head()

    def _init_head(self) -> None:
        nn.init.normal_(self.head.weight, std=1e-3)
        with torch.no_grad():
            bias = self.head.bias.view(5, self.bins)
            bias[0].zero_()        # g: unit mask gain
            bias[1].zero_()        # theta: no rotation
            bias[2].fill_(-12.0)   # m: generated magnitude negligible
            bias[3].zero_()        # phi
            bias[4].fill_(6.0)     # u: the input, w = 0.9975
            nn.init.normal_(self.channel, std=0.02)
            nn.init.normal_(self.rate, std=0.02)

    def _heads(self, x: torch.Tensor) -> tuple[tuple[torch.Tensor, ...], tuple[torch.Tensor, ...]]:
        x = self.final_norm(x.transpose(1, 2)).transpose(1, 2)
        answers = []
        for c in range(2):
            out = self.head(x + self.channel[c].view(1, -1, 1))
            b, _, t = out.shape
            g, theta, m, phi, u = out.view(b, 5, self.bins, t).unbind(1)
            answers.append((g.clamp(-8.0, 4.0), theta, m.clamp(-20.0, 8.0), phi, u))
        return answers[0], answers[1]

    def _embed(self, feats: torch.Tensor, rate48: torch.Tensor) -> torch.Tensor:
        x = self.embed(feats)
        x = self.embed_norm(x.transpose(1, 2)).transpose(1, 2)
        r = rate48.to(x.dtype).view(-1, 1)
        return x + (self.rate[0] * (1.0 - r) + self.rate[1] * r).unsqueeze(-1)

    def forward_features(self, feats: torch.Tensor, rate48: torch.Tensor):
        x = self._embed(feats, rate48)
        x = x + self.ahead_mix(self.ahead(x))
        for block in self.blocks:
            x = block(x)
        return self._heads(x)

    @staticmethod
    def apply(spec: torch.Tensor, head: tuple[torch.Tensor, ...]) -> torch.Tensor:
        g, theta, m, phi, u = head
        w = torch.sigmoid(u)
        return w * spec * torch.exp(torch.complex(g, theta)) + (1.0 - w) * torch.exp(torch.complex(m, phi))

    def forward(self, left: torch.Tensor, right: torch.Tensor, rate48: torch.Tensor) -> tuple[torch.Tensor, torch.Tensor]:
        """Complex spectra of the left and right channels [B, bins, frames], and [B] 1.0 at 48 kHz or 0.0 at 44.1
        -> restored spectra, same frames."""
        mid, side = to_mid_side(left, right)
        head_mid, head_side = self.forward_features(features(mid.abs(), side.abs()), rate48)
        return to_left_right(self.apply(mid, head_mid), self.apply(side, head_side))

    def parameter_count(self) -> int:
        return sum(p.numel() for p in self.parameters())

    # ----- streaming ---------------------------------------------------------------------------------------------

    def warmup_clears(self) -> list[int]:
        """The states to zero once the first LOOKAHEAD frames have gone in: the blocks', which saw only frames that
        answer for time before the stream began."""
        return list(range(1, 1 + len(self.blocks)))

    def state_shapes(self) -> list[tuple[int, int]]:
        """Per-stream state, batch dimension left out: the look-ahead layer, each block, and the delayed input."""
        shapes = [self.ahead.state_shape()]
        shapes += [block.dwconv.state_shape() for block in self.blocks]
        shapes.append((4 * self.bins, self.lookahead))   # mid re, mid im, side re, side im of the last frames
        return shapes

    def step(self, left_re, left_im, right_re, right_im, rate48, states: list[torch.Tensor]):
        """New frames in [B, bins, T >= 1]; restored frames out, each LOOKAHEAD frames behind the input it came with."""
        mid_re, mid_im = (left_re + right_re) * SQRT_HALF, (left_im + right_im) * SQRT_HALF
        side_re, side_im = (left_re - right_re) * SQRT_HALF, (left_im - right_im) * SQRT_HALF
        mid_mag = torch.sqrt(mid_re * mid_re + mid_im * mid_im)
        side_mag = torch.sqrt(side_re * side_re + side_im * side_im)
        x = self._embed(features(mid_mag, side_mag), rate48)
        new_states = []
        y, s = self.ahead.step(x, states[0])
        new_states.append(s)
        # The look-ahead layer's answer is for the frame LOOKAHEAD behind; so is everything after it, and the residual
        # it is added to has to be that frame's too, which the delayed features supply.
        delayed = torch.cat([states[-1], torch.cat([mid_re, mid_im, side_re, side_im], dim=1)], dim=2)
        t = x.shape[2]
        x_delayed = torch.cat([states[0][:, :, states[0].shape[2] - self.lookahead:], x], dim=2)[:, :, :t]
        x = x_delayed + self.ahead_mix(y)
        for i, block in enumerate(self.blocks):
            x, s = block.step(x, states[1 + i])
            new_states.append(s)
        head_mid, head_side = self._heads(x)
        spec = delayed[:, :, :t]
        b = self.bins
        m_re, m_im, s_re, s_im = spec[:, 0:b], spec[:, b:2 * b], spec[:, 2 * b:3 * b], spec[:, 3 * b:]
        out_mid_re, out_mid_im = self._apply_real(m_re, m_im, head_mid)
        out_side_re, out_side_im = self._apply_real(s_re, s_im, head_side)
        new_states.append(delayed[:, :, -self.lookahead:])
        return ((out_mid_re + out_side_re) * SQRT_HALF, (out_mid_im + out_side_im) * SQRT_HALF,
                (out_mid_re - out_side_re) * SQRT_HALF, (out_mid_im - out_side_im) * SQRT_HALF, new_states)

    # ----- what the player runs ------------------------------------------------------------------------------------

    def core_state_shapes(self) -> list[tuple[int, int]]:
        """The state `core_step` carries: the look-ahead layer's, then each block's."""
        return [self.ahead.state_shape()] + [block.dwconv.state_shape() for block in self.blocks]

    def core_step(self, feats: torch.Tensor, rate48: torch.Tensor, states: list[torch.Tensor]):
        """The network without its ends, which the player does itself: features in [1, 2 * bins, T], every head out.

        Returns [2, 5 * bins, T], mid first, each bin's g, theta, m, phi and u unclamped, laid out [c, 5, bins, T]
        flattened; and the new state. Everything around it (mid and side, the log-magnitude features, the frames held
        back for the look-ahead, the masks) is arithmetic the player does in a loop, where a graph of small operators
        costs more to dispatch than to compute: the whole `step` was 233 of them, 2.7 ms a call.
        """
        x = self._embed(feats, rate48)
        new_states = []
        y, s = self.ahead.step(x, states[0])
        new_states.append(s)
        t = x.shape[2]
        x = torch.cat([states[0][:, :, states[0].shape[2] - self.lookahead:], x], dim=2)[:, :, :t] + self.ahead_mix(y)
        for i, block in enumerate(self.blocks):
            x, s = block.step(x, states[1 + i])
            new_states.append(s)
        x = self.final_norm(x.transpose(1, 2)).transpose(1, 2)
        both = torch.cat([x + self.channel[0].view(1, -1, 1), x + self.channel[1].view(1, -1, 1)], dim=0)
        return self.head(both), new_states

    @staticmethod
    def _apply_real(re, im, head):
        g, theta, m, phi, u = head
        w = torch.sigmoid(u)
        gain = torch.exp(g)
        cos_t, sin_t = torch.cos(theta), torch.sin(theta)
        kept_re = gain * (re * cos_t - im * sin_t)
        kept_im = gain * (re * sin_t + im * cos_t)
        made = torch.exp(m)
        return w * kept_re + (1.0 - w) * made * torch.cos(phi), w * kept_im + (1.0 - w) * made * torch.sin(phi)


if __name__ == "__main__":
    torch.manual_seed(0)
    net = Restorer().eval()
    print(f"parameters: {net.parameter_count() / 1e6:.2f} M")
    left, right = torch.randn(1, 48000) * 0.1, torch.randn(1, 48000) * 0.1
    sl, sr = stft(left), stft(right)
    rate48 = torch.ones(1)
    with torch.no_grad():
        ol, orr = net(sl, sr, rate48)
        err = float((istft(ol, 48000) - left).pow(2).mean() / left.pow(2).mean())
        print("identity error at init:", err)
        # Streaming against the whole signal, in uneven blocks.
        states = [torch.zeros(1, c, k) for c, k in net.state_shapes()]
        outs = []
        frames = sl.shape[2]
        at = 0
        # The first call takes exactly the frames that answer for time before the start, then the blocks forget them.
        for size in [LOOKAHEAD] + [1, 3, 7, 2, 5] * 100:
            if at >= frames:
                break
            chunk = slice(at, min(frames, at + size))
            re_l, im_l, re_r, im_r, states = net.step(sl.real[..., chunk], sl.imag[..., chunk],
                                                      sr.real[..., chunk], sr.imag[..., chunk], rate48, states)
            outs.append(torch.complex(re_l, im_l))
            if at == 0:
                for i in net.warmup_clears():
                    states[i] = torch.zeros_like(states[i])
            at = chunk.stop
        streamed = torch.cat(outs, dim=2)
        # Streamed frame t answers input frame t - LOOKAHEAD, from the very first.
        whole = ol[..., : frames - LOOKAHEAD]
        got = streamed[..., LOOKAHEAD:]
        n = min(whole.shape[2], got.shape[2])
        print("stream vs whole from the first frame, max |diff|:",
              float((whole[..., :n] - got[..., :n]).abs().max()), "of", float(whole.abs().max()))
