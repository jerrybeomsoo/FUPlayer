"""Writes the numbers the player's own tests hold its neural upscaler to.

    python make_test_fixture.py ../../tests/FUPlayer.Core.Tests/Fixtures/neural-upscaler

A network far too small to be of use (two blocks of sixteen channels), with its output head pushed well
away from passing its input through, so that a mistake anywhere in the player's chain shows up as a
difference rather than hiding behind an identity. Then, from PyTorch and torchaudio:

  wave.f32              two seconds of tones and noise at 96 kHz
  rebuilt.f32           torch.stft, the network and torch.istft over the whole of it, flagged lossy
  rebuilt_lossless.f32  the same, flagged lossless
  frame100_re/_im.f32   frame 100 of torch.stft, which the player's transform must reproduce
  up_in.f32, up_out.f32 the 2x interpolator the network is trained behind, on its own
  tiny.onnx             the network, exported as the player loads it
  shape.json            sizes, and how many frames either way the network looks

Little-endian float32 throughout. No music is involved.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path

import numpy as np
import torch
import torchaudio

from upscaler.model import BINS, ExportWrapper, Upscaler, istft, stft


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("out", type=Path)
    args = ap.parse_args()
    args.out.mkdir(parents=True, exist_ok=True)
    torch.manual_seed(20260917)

    model = Upscaler(dim=16, intermediate=48, blocks=2).eval()
    with torch.no_grad():
        model.head.weight.normal_(0.0, 0.05)
        bias = model.head.bias.view(5, BINS)
        bias[0].normal_(0.0, 0.3)          # g: gains well away from unity
        bias[1].normal_(0.0, 0.5)          # theta: rotated phases
        bias[2].normal_(-4.0, 1.0)         # m: a generated component loud enough to matter
        bias[3].normal_(0.0, 1.0)          # phi
        bias[4].normal_(0.0, 1.5)          # u: crossovers all over the place
        model.condition.normal_(0.0, 0.5)  # a source flag that changes the answer

    rate = 96_000
    t = torch.arange(2 * rate, dtype=torch.float64) / rate
    noise = torch.randn(t.shape[0], dtype=torch.float64, generator=torch.Generator().manual_seed(7))
    wave = (0.3 * torch.sin(2 * torch.pi * 997 * t) + 0.1 * torch.sin(2 * torch.pi * 7919 * t)
            + 0.03 * torch.sin(2 * torch.pi * 18181 * t) + 0.003 * noise).float()

    spec = stft(wave[None])
    with torch.no_grad():
        rebuilt = istft(model(spec, torch.ones(1)), wave.shape[0])[0]
        rebuilt_lossless = istft(model(spec, torch.zeros(1)), wave.shape[0])[0]
        onnx_path = args.out / "tiny.onnx"
        wrapper = ExportWrapper(model).eval()
        re, im = spec.real.contiguous(), spec.imag.contiguous()
        torch.onnx.export(
            wrapper, (re[..., :84], im[..., :84], torch.ones(1)), str(onnx_path), input_names=["re", "im", "lossy"],
            output_names=["out_re", "out_im"],
            dynamic_axes={"re": {0: "batch", 2: "frames"}, "im": {0: "batch", 2: "frames"}, "lossy": {0: "batch"},
                          "out_re": {0: "batch", 2: "frames"}, "out_im": {0: "batch", 2: "frames"}},
            opset_version=17, dynamo=False)

    up_in = (0.4 * torch.sin(2 * torch.pi * 3000 * torch.arange(6000) / 48_000)
             + 0.05 * torch.randn(6000, generator=torch.Generator().manual_seed(11))).float()
    up_out = torchaudio.functional.resample(up_in, 48_000, 96_000, lowpass_filter_width=64, rolloff=0.99,
                                            resampling_method="sinc_interp_kaiser", beta=14.769656459379492)

    def write(name: str, tensor: torch.Tensor) -> None:
        tensor.detach().numpy().astype("<f4").tofile(args.out / name)

    write("wave.f32", wave)
    write("rebuilt.f32", rebuilt)
    write("rebuilt_lossless.f32", rebuilt_lossless)
    write("frame100_re.f32", spec.real[0, :, 100])
    write("frame100_im.f32", spec.imag[0, :, 100])
    write("up_in.f32", up_in)
    write("up_out.f32", up_out)
    receptive = 1 + 3 * len(model.blocks)
    (args.out / "shape.json").write_text(json.dumps(
        {"samples": wave.shape[0], "bins": BINS, "frames": spec.shape[-1], "receptive": receptive}))

    difference = float((rebuilt - wave).pow(2).mean() / wave.pow(2).mean())
    print(f"fixture written to {args.out}: the network moves the signal {10 * np.log10(difference):.1f} dB from its input")


if __name__ == "__main__":
    main()
