"""Exports a trained restorer to ONNX for the player, checks it against PyTorch, and describes it.

    python -m restorer.export --checkpoint work/runs/restorer/best.pt --out work/export/neural-restorer.onnx

The file holds `Restorer.core_step`, the network between its features and its heads: log-magnitude features of mid
and side [1, 2050, frames], the rate as [1] (1 at 48 kHz, 0 at 44.1) and the state the previous call left, one tensor
for the look-ahead layer and one per block; out come the heads for mid and side [2, 5125, frames], unclamped, and the
state for the next call. The player computes the features, holds each frame's spectrum back for the look-ahead,
applies the masks, and does the transform and the overlap-add, which keeps the graph small: dispatching operators, not
arithmetic, was most of what a call cost.

The check does all of that in NumPy, the way the player does, over a signal cut into uneven calls, and compares it with
the whole-signal forward pass. Beside the network goes a .json the player reads to say what is installed.
"""
from __future__ import annotations

import argparse
import copy
import json
from pathlib import Path

import numpy as np
import onnxruntime as ort
import torch
from torch import nn

from .model import BINS, FLOOR_BELOW_PEAK, HOP, LOOKAHEAD, N_FFT, SQRT_HALF, Restorer, stft

FLOOR_BELOW_PEAK_DB = 85.0


class Conv1dAs2d(nn.Module):
    """The same convolution over a height of one. ONNX Runtime's processor kernels for two-dimensional convolutions,
    depthwise ones above all, are the fast ones: measured on the full network, 8.7 times real time against 7.2."""

    def __init__(self, conv: nn.Conv1d) -> None:
        super().__init__()
        self.conv = nn.Conv2d(conv.in_channels, conv.out_channels, (1, conv.kernel_size[0]), groups=conv.groups,
                              bias=conv.bias is not None)
        with torch.no_grad():
            self.conv.weight.copy_(conv.weight.unsqueeze(2))
            if conv.bias is not None:
                self.conv.bias.copy_(conv.bias)

    def forward(self, x: torch.Tensor) -> torch.Tensor:
        return self.conv(x.unsqueeze(2)).squeeze(2)


def with_2d_convolutions(model: Restorer) -> Restorer:
    converted = copy.deepcopy(model)
    for module in list(converted.modules()):
        for name, child in list(module.named_children()):
            if isinstance(child, nn.Conv1d):
                setattr(module, name, Conv1dAs2d(child))
    return converted


class CoreWrapper(nn.Module):
    def __init__(self, model: Restorer) -> None:
        super().__init__()
        self.model = with_2d_convolutions(model)

    def forward(self, features, rate48, *states):
        heads, new_states = self.model.core_step(features, rate48, list(states))
        return (heads, *new_states)


def stream(session: ort.InferenceSession, model: Restorer, left: np.ndarray, right: np.ndarray, rate48: float,
           sizes: list[int]) -> tuple[np.ndarray, np.ndarray]:
    """Complex STFT frames of both channels [bins, frames] through the exported core, as the player runs it.

    Returns restored frames aligned with the input: output k answers input k - LOOKAHEAD, so the first LOOKAHEAD
    are dropped and the last LOOKAHEAD input frames have no answer yet.
    """
    shapes = model.core_state_shapes()
    names = [f"state{i}" for i in range(len(shapes))]
    states = [np.zeros((1, c, k), dtype=np.float32) for c, k in shapes]
    mid, side = (left + right) * SQRT_HALF, (left - right) * SQRT_HALF
    total = left.shape[1]
    out_l, out_r = [], []
    at, i, sent = 0, 0, 0
    while at < total:
        size = LOOKAHEAD if at == 0 else sizes[i % len(sizes)]
        chunk = slice(at, min(total, at + size))
        m, s = np.abs(mid[:, chunk]), np.abs(side[:, chunk])
        peak = np.maximum(m.max(axis=0), s.max(axis=0))
        floor = np.maximum(peak * FLOOR_BELOW_PEAK, 1e-7)
        feats = np.concatenate([np.log(np.maximum(m, floor)), np.log(np.maximum(s, floor))])[None].astype(np.float32)
        feed = {"features": feats, "rate48": np.array([rate48], dtype=np.float32)}
        feed.update(dict(zip(names, states)))
        result = session.run(None, feed)
        heads = result[0].reshape(2, 5, BINS, -1)
        states = list(result[1:])
        if at == 0:
            for k in range(1, len(states)):
                states[k] = np.zeros_like(states[k])
        for t in range(heads.shape[3]):
            k = sent + t - LOOKAHEAD
            if k < 0:
                continue
            restored = []
            for c, spec in ((0, mid[:, k]), (1, side[:, k])):
                g, theta, mm, phi, u = heads[c, :, :, t].astype(np.float64)
                w = 1.0 / (1.0 + np.exp(-u))
                kept = w * np.exp(np.clip(g, -8.0, 4.0)) * spec * np.exp(1j * theta)
                made = (1.0 - w) * np.exp(np.clip(mm, -20.0, 8.0)) * np.exp(1j * phi)
                restored.append(kept + made)
            out_l.append((restored[0] + restored[1]) * SQRT_HALF)
            out_r.append((restored[0] - restored[1]) * SQRT_HALF)
        sent += heads.shape[3]
        at, i = chunk.stop, i + 1
    return np.stack(out_l, axis=1), np.stack(out_r, axis=1)


def export(model: Restorer, out: Path, check_seconds: float = 3.0) -> float:
    """Writes the core network and returns the worst streaming error against the whole-signal pass, relative."""
    model.eval()
    wrapper = CoreWrapper(model).eval()
    shapes = model.core_state_shapes()
    names = [f"state{i}" for i in range(len(shapes))]
    example = torch.randn(1, 2 * BINS, 3)
    rate48 = torch.ones(1)
    states = [torch.zeros(1, c, k) for c, k in shapes]
    out.parent.mkdir(parents=True, exist_ok=True)
    torch.onnx.export(wrapper, (example, rate48, *states), str(out),
                      input_names=["features", "rate48", *names], output_names=["heads", *[f"new_{n}" for n in names]],
                      dynamic_axes={"features": {2: "frames"}, "heads": {2: "frames"}}, opset_version=17, dynamo=False)

    torch.manual_seed(1)
    length = int(check_seconds * 48_000)
    t = torch.arange(length) / 48_000.0
    left = 0.3 * torch.sin(2 * torch.pi * 440.0 * t) + 0.05 * torch.randn(length)
    right = 0.2 * torch.sin(2 * torch.pi * 660.0 * t) + 0.05 * torch.randn(length)
    sl, sr = stft(left[None]), stft(right[None])
    with torch.no_grad():
        whole_l, whole_r = model(sl, sr, rate48)

    session = ort.InferenceSession(str(out), providers=["CPUExecutionProvider"])
    got_l, got_r = stream(session, model, sl[0].numpy().astype(np.complex128), sr[0].numpy().astype(np.complex128),
                          1.0, [1, 4, 2, 7, 3])
    n = got_l.shape[1]
    expected = np.concatenate([whole_l[0].numpy()[:, :n], whole_r[0].numpy()[:, :n]])
    got = np.concatenate([got_l, got_r])
    return float(np.abs(got - expected).max() / np.abs(expected).max())


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--checkpoint", default="work/runs/restorer/best.pt")
    ap.add_argument("--out", default="work/export/neural-restorer.onnx")
    ap.add_argument("--steps", type=int, default=0, help="training steps to record, when the checkpoint's own count is only its run's")
    args = ap.parse_args()

    state = torch.load(args.checkpoint, map_location="cpu", weights_only=False)
    dim, blocks = state.get("dim", 256), state.get("blocks", 8)
    model = Restorer(dim=dim, intermediate=dim * 3, blocks=blocks)
    model.load_state_dict(state["model"])
    out = Path(args.out)
    error = export(model, out)
    print(f"exported {out} ({out.stat().st_size / 2**20:.1f} MB); streamed as the player runs it against the "
          f"whole-signal pass, worst error {error:.2e} of the largest bin", flush=True)
    if error > 1e-4:
        raise SystemExit("the exported network does not reproduce the model")

    meta = {"kind": "restorer", "n_fft": N_FFT, "hop": HOP, "bins": BINS, "window": "hann-periodic",
            "feature_floor_below_peak_db": FLOOR_BELOW_PEAK_DB, "lookahead_frames": LOOKAHEAD,
            "rates": [44100, 48000], "state_shapes": [list(s) for s in model.core_state_shapes()],
            "head_clamps": {"g": [-8.0, 4.0], "m": [-20.0, 8.0]},
            "scores": state.get("scores", {}), "step": args.steps or state.get("step", 0),
            "parameters": model.parameter_count()}
    out.with_suffix(".json").write_text(json.dumps(meta, indent=2))


if __name__ == "__main__":
    main()
