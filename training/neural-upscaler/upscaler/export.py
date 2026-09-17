"""Exports a trained upscaler to ONNX, checks the export against PyTorch, and describes it.

    python -m upscaler.export --checkpoint work/runs/upscaler/last.pt --out work/export/neural-upscaler.onnx

Writes the network, with inputs re, im and lossy, and beside it a .json file the player reads to say what
is installed: the transform it expects, how many parameters it has, how long it trained, and how it
scored on the songs kept out of training. A checkpoint without scores takes them from the run's log.
"""
from __future__ import annotations

import argparse
import csv
import json
from pathlib import Path

import numpy as np
import onnxruntime as ort
import torch

from .model import BINS, FLOOR_BELOW_PEAK_DB, HOP, N_FFT, ExportWrapper, Upscaler

SCORE_KEYS = ["in_0-16k", "out_0-16k", "in_16-22k", "out_16-22k", "in_22k-nyq", "out_22k-nyq",
              "ll_in_22k-nyq", "ll_out_22k-nyq", "ll_passband_db"]


def scores_from_log(checkpoint: Path) -> dict[str, float]:
    """The last validation in the run's log.csv, for a checkpoint saved without its own scores."""
    log = checkpoint.parent / "log.csv"
    if not log.exists():
        return {}
    rows = [r for r in csv.reader(log.open(encoding="utf-8"))][1:]
    rows = [r for r in rows if len(r) > 14 and r[9]]
    if not rows:
        return {}
    last = rows[-1]
    return {k: float(v) for k, v in zip(SCORE_KEYS, last[9:9 + len(SCORE_KEYS)]) if v not in ("", "nan")}


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--checkpoint", default="work/runs/upscaler/last.pt")
    ap.add_argument("--out", default="work/export/neural-upscaler.onnx")
    ap.add_argument("--steps", type=int, default=0, help="training steps to record, when the checkpoint's own count is only its run's")
    args = ap.parse_args()

    checkpoint = Path(args.checkpoint)
    state = torch.load(checkpoint, map_location="cpu", weights_only=False)
    model = Upscaler(dim=state.get("dim", 384), intermediate=state.get("dim", 384) * 3, blocks=state.get("blocks", 8))
    missing, unexpected = model.load_state_dict(state["model"], strict=False)
    if unexpected or any(k != "condition" for k in missing):
        raise SystemExit(f"checkpoint does not fit: missing {missing}, unexpected {unexpected}")
    model.eval()
    wrapper = ExportWrapper(model).eval()

    out = Path(args.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    frames = 84
    re = torch.randn(2, BINS, frames) * 0.1
    im = torch.randn(2, BINS, frames) * 0.1
    lossy = torch.tensor([1.0, 0.0])
    torch.onnx.export(
        wrapper, (re, im, lossy), str(out), input_names=["re", "im", "lossy"], output_names=["out_re", "out_im"],
        dynamic_axes={"re": {0: "batch", 2: "frames"}, "im": {0: "batch", 2: "frames"}, "lossy": {0: "batch"},
                      "out_re": {0: "batch", 2: "frames"}, "out_im": {0: "batch", 2: "frames"}},
        opset_version=17, dynamo=False)

    session = ort.InferenceSession(str(out), providers=["CPUExecutionProvider"])
    with torch.no_grad():
        ref_re, ref_im = wrapper(re, im, lossy)
    got_re, got_im = session.run(None, {"re": re.numpy(), "im": im.numpy(), "lossy": lossy.numpy()})
    err = max(float(np.abs(got_re - ref_re.numpy()).max()), float(np.abs(got_im - ref_im.numpy()).max()))
    scale = float(max(ref_re.abs().max(), ref_im.abs().max()))
    print(f"exported {out} ({out.stat().st_size / 2**20:.1f} MB); ONNX vs PyTorch max error {err:.2e} of {scale:.2e}")

    meta = {"n_fft": N_FFT, "hop": HOP, "bins": BINS, "window": "hann-periodic",
            "feature_floor_below_peak_db": FLOOR_BELOW_PEAK_DB, "conditioned": True,
            "lookahead_frames": 26, "context_frames": 26, "rates": [88200, 96000],
            "scores": state.get("scores") or scores_from_log(checkpoint),
            "step": args.steps or state.get("step", 0),
            "parameters": model.parameter_count()}
    out.with_suffix(".json").write_text(json.dumps(meta, indent=2))


if __name__ == "__main__":
    main()
