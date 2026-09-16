"""Exports a trained upscaler to ONNX, checks the export against PyTorch, and describes it.

    python -m upscaler.export --checkpoint work/runs/upscaler/best-gan.pt --out work/export/neural-upscaler.onnx

Writes the network and, beside it, a .json file the player reads to say what is installed: the
transform it expects, how many parameters it has, how long it trained, and how it scored on the songs
kept out of training. The numbers the player's own tests use come from make_test_fixture.py instead.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path

import numpy as np
import onnxruntime as ort
import torch

from .model import BINS, FLOOR_BELOW_PEAK_DB, HOP, N_FFT, ExportWrapper, Upscaler


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--checkpoint", default="work/runs/upscaler/best-gan.pt")
    ap.add_argument("--out", default="work/export/neural-upscaler.onnx")
    args = ap.parse_args()

    state = torch.load(args.checkpoint, map_location="cpu", weights_only=False)
    model = Upscaler(dim=state.get("dim", 384), intermediate=state.get("dim", 384) * 3, blocks=state.get("blocks", 8))
    model.load_state_dict(state["model"])
    model.eval()
    wrapper = ExportWrapper(model).eval()

    out = Path(args.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    frames = 84
    re = torch.randn(2, BINS, frames) * 0.1
    im = torch.randn(2, BINS, frames) * 0.1
    torch.onnx.export(
        wrapper, (re, im), str(out), input_names=["re", "im"], output_names=["out_re", "out_im"],
        dynamic_axes={"re": {0: "batch", 2: "frames"}, "im": {0: "batch", 2: "frames"},
                      "out_re": {0: "batch", 2: "frames"}, "out_im": {0: "batch", 2: "frames"}},
        opset_version=17, dynamo=False)

    session = ort.InferenceSession(str(out), providers=["CPUExecutionProvider"])
    with torch.no_grad():
        ref_re, ref_im = wrapper(re, im)
    got_re, got_im = session.run(None, {"re": re.numpy(), "im": im.numpy()})
    err = max(float(np.abs(got_re - ref_re.numpy()).max()), float(np.abs(got_im - ref_im.numpy()).max()))
    scale = float(max(ref_re.abs().max(), ref_im.abs().max()))
    print(f"exported {out} ({out.stat().st_size / 2**20:.1f} MB); ONNX vs PyTorch max error {err:.2e} of {scale:.2e}")

    meta = {"n_fft": N_FFT, "hop": HOP, "bins": BINS, "window": "hann-periodic",
            "feature_floor_below_peak_db": FLOOR_BELOW_PEAK_DB,
            "lookahead_frames": 26, "context_frames": 26, "rates": [88200, 96000],
            "scores": state.get("scores", {}), "step": state.get("step", 0),
            "parameters": model.parameter_count()}
    out.with_suffix(".json").write_text(json.dumps(meta, indent=2))


if __name__ == "__main__":
    main()
