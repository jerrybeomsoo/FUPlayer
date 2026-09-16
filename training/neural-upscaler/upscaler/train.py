"""Trains the upscaler in two phases, as the design document lays out.

Phase 1 fits the generator with the multi-resolution STFT and phase losses alone, which establishes
structure without adversarial instability. Phase 2 adds the multi-period and spectral discriminators.
Both phases checkpoint every few minutes and resume from where they stopped, because a run this long
on a laptop will be interrupted.

Validation runs on tracks held out by song, never seen in any version, and reports log-spectral
distance in three bands: below 16 kHz, 16 to 22 kHz, and 22 kHz to Nyquist, against the same crop
simply upsampled. The model that scores best on the two upper bands is kept.
"""
from __future__ import annotations

import argparse
import csv
import dataclasses
import math
import time
from pathlib import Path

import torch

from . import data, losses
from .model import HOP, N_FFT, Upscaler, istft, stft

BANDS = [("0-16k", 0.0, 16_000.0), ("16-22k", 16_000.0, 22_050.0), ("22k-nyq", 22_050.0, 1e9)]


def lsd_by_band(pred: torch.Tensor, target: torch.Tensor, rate: int) -> dict[str, float]:
    p = stft(pred).abs().pow(2)
    t = stft(target).abs().pow(2)
    hz = torch.arange(p.shape[1], device=p.device) * (rate / N_FFT)
    diff = (10 * torch.log10(t + 1e-10) - 10 * torch.log10(p + 1e-10)).pow(2)
    loud = t.sum(dim=1) > 1e-6
    out = {}
    for name, lo, hi in BANDS:
        mask = (hz >= lo) & (hz < min(hi, rate / 2))
        if mask.sum() == 0:
            continue
        per_frame = diff[:, mask, :].mean(dim=1).sqrt()
        out[name] = float(per_frame[loud].mean()) if loud.any() else float("nan")
    return out


class Validation:
    """A fixed set of held-out crops, drawn once, so every evaluation measures the same audio."""

    def __init__(self, held: list[data.Track], count: int, device: torch.device) -> None:
        # The copies the corpus started with. Copies added later (Apple's AAC) change how many there
        # are to choose from, which would change every crop drawn and every score after it; they are
        # measured by the evaluation instead.
        held = [dataclasses.replace(t, variants=[v for v in t.variants if not v[1].startswith("apple")]) for t in held]
        sampler = data.Sampler(held, seed=12345)
        self.items = []
        for _ in range(count):
            x_low, y, low, high, label = sampler.draw()
            up = data.upsample(torch.from_numpy(x_low).to(device)[None], low, high)[0]
            self.items.append((up.cpu(), torch.from_numpy(y), high, label))

    @torch.no_grad()
    def run(self, model: Upscaler, device: torch.device) -> dict[str, float]:
        model.eval()
        sums: dict[str, list[float]] = {}
        for up, y, rate, label in self.items:
            x = up.to(device)[None]
            target = y.to(device)[None]
            out = istft(model(stft(x)), x.shape[1])
            for tag, wave in (("in", x), ("out", out)):
                for band, value in lsd_by_band(wave, target, rate).items():
                    if not math.isnan(value):
                        sums.setdefault(f"{tag}_{band}", []).append(value)
        model.train()
        return {k: sum(v) / len(v) for k, v in sums.items()}


def save(path: Path, **state) -> None:
    tmp = path.with_suffix(".tmp")
    torch.save(state, tmp)
    tmp.replace(path)


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--corpus", default="work/corpus")
    ap.add_argument("--manifest", default="", help="a track list other than the corpus's own manifest.csv")
    ap.add_argument("--out", default="work/runs/upscaler")
    ap.add_argument("--batch", type=int, default=12)
    ap.add_argument("--gan-batch", type=int, default=6)
    ap.add_argument("--disc-crop", type=int, default=16_384, help="samples the discriminators judge")
    ap.add_argument("--pretrain-steps", type=int, default=30_000)
    ap.add_argument("--gan-steps", type=int, default=20_000)
    ap.add_argument("--lr", type=float, default=5e-4)
    ap.add_argument("--lr-d", type=float, default=2e-4)
    ap.add_argument("--phase-weight", type=float, default=0.3)
    ap.add_argument("--adv-weight", type=float, default=1.0)
    ap.add_argument("--fm-weight", type=float, default=2.0)
    ap.add_argument("--stft-weight-gan", type=float, default=45.0)
    ap.add_argument("--validate-every", type=int, default=1000)
    ap.add_argument("--checkpoint-minutes", type=float, default=8.0)
    ap.add_argument("--dim", type=int, default=384)
    ap.add_argument("--blocks", type=int, default=8)
    ap.add_argument("--benchmark", type=int, default=0, help="time this many steps of each phase and stop")
    ap.add_argument("--min-rms", type=float, default=3e-4, help="draw again any crop whose master is quieter")
    ap.add_argument("--dither16", type=float, default=0.5, help="chance a lossless crop is requantised to 16-bit with dither")
    ap.add_argument("--init-from", default="", help="start from this checkpoint's weights, at step 0 of a new schedule")
    ap.add_argument("--snapshot-every", type=int, default=4000, help="keep the generator every this many adversarial steps")
    args = ap.parse_args()

    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    torch.backends.cudnn.benchmark = True

    tracks = data.load_manifest(Path(args.corpus), Path(args.manifest) if args.manifest else None)
    train_tracks, held_tracks = data.split(tracks)
    print(f"{len(tracks)} tracks: {len(train_tracks)} to train on, {len(held_tracks)} held out by song "
          f"({sum(t.japanese for t in held_tracks)} Japanese)", flush=True)

    model = Upscaler(dim=args.dim, intermediate=args.dim * 3, blocks=args.blocks).to(device)
    mpd = losses.MultiPeriodDiscriminator().to(device)
    msd = losses.MultiResolutionSpectralDiscriminator().to(device)
    opt_g = torch.optim.AdamW(model.parameters(), lr=args.lr, betas=(0.8, 0.99), weight_decay=0.01)
    opt_d = torch.optim.AdamW(list(mpd.parameters()) + list(msd.parameters()), lr=args.lr_d, betas=(0.8, 0.99))
    total_steps = args.pretrain_steps + args.gan_steps

    step, best, best_gan = 0, float("inf"), float("inf")
    last_path, best_path, best_gan_path = out / "last.pt", out / "best.pt", out / "best-gan.pt"
    if args.init_from and not last_path.exists():
        # A fine-tune: the generator and both discriminators as they were, a fresh schedule and a fresh
        # record of what is best, because what the scores were measured with has changed.
        start = torch.load(args.init_from, map_location=device, weights_only=False)
        model.load_state_dict(start["model"])
        if "mpd" in start:
            mpd.load_state_dict(start["mpd"])
            msd.load_state_dict(start["msd"])
            opt_d.load_state_dict(start["opt_d"])
        print(f"starting from {args.init_from} (step {start.get('step', '?')})", flush=True)
    if last_path.exists() and not args.benchmark:
        state = torch.load(last_path, map_location=device, weights_only=False)
        model.load_state_dict(state["model"])
        mpd.load_state_dict(state["mpd"])
        msd.load_state_dict(state["msd"])
        opt_g.load_state_dict(state["opt_g"])
        opt_d.load_state_dict(state["opt_d"])
        step, best = state["step"], state["best"]
        best_gan = state.get("best_gan", float("inf"))
        print(f"resumed at step {step}, best {best:.4f}", flush=True)

    def lr_at(s: int, base: float) -> float:
        warm = 1000
        if s < warm:
            return base * (s + 1) / warm
        progress = (s - warm) / max(1, total_steps - warm)
        return base * (0.05 + 0.95 * 0.5 * (1.0 + math.cos(math.pi * min(1.0, progress))))

    validation = None if args.benchmark else Validation(held_tracks, 240, device)
    sampler = data.Sampler(train_tracks, seed=step + 7, min_rms=args.min_rms, dither16=args.dither16)
    log_path = out / "log.csv"
    log_new = not log_path.exists()
    log = log_path.open("a", encoding="utf-8", newline="")
    writer = csv.writer(log)
    if log_new:
        writer.writerow(["step", "phase", "loss_g", "stft", "phase", "adv", "fm", "loss_d", "sec_per_step",
                         "in_0-16k", "out_0-16k", "in_16-22k", "out_16-22k", "in_22k-nyq", "out_22k-nyq"])

    last_checkpoint = time.time()
    window_start, window_steps = time.time(), 0
    running: dict[str, float] = {}
    phases = [("pretrain", args.pretrain_steps), ("gan", args.gan_steps)]
    if args.benchmark:
        phases = [("pretrain", args.benchmark), ("gan", args.benchmark)]
        step = 0

    phase_start = 0
    for phase, steps in phases:
        phase_end = phase_start + steps
        while step < phase_end:
            for group in opt_g.param_groups:
                group["lr"] = lr_at(step, args.lr)
            x, y, rates = data.batch(sampler, args.gan_batch if phase == "gan" else args.batch, device)

            spec_in = stft(x)
            spec_out = model(spec_in)
            y_hat = istft(spec_out, x.shape[1])

            if phase == "gan":
                # The discriminators judge a shorter crop, the same one of both, which is what fits
                # beside the generator on a 4 GB card that is also driving the display.
                offset = int(torch.randint(0, x.shape[1] - args.disc_crop + 1, ()).item())
                y_d = y[:, offset: offset + args.disc_crop]
                y_hat_d = y_hat[:, offset: offset + args.disc_crop]
                real_p, fake_p = mpd(y_d), mpd(y_hat_d.detach())
                real_s, fake_s = msd(y_d), msd(y_hat_d.detach())
                loss_d = losses.discriminator_loss(real_p, fake_p) + losses.discriminator_loss(real_s, fake_s)
                opt_d.zero_grad(set_to_none=True)
                loss_d.backward()
                opt_d.step()
            else:
                loss_d = torch.zeros((), device=device)

            l_stft = losses.multi_resolution_stft_loss(y_hat, y, rates)
            l_phase = losses.phase_loss(spec_out, stft(y))
            if phase == "gan":
                real_p, fake_p = mpd(y_d), mpd(y_hat_d)
                real_s, fake_s = msd(y_d), msd(y_hat_d)
                l_adv = losses.generator_adversarial_loss(fake_p) + losses.generator_adversarial_loss(fake_s)
                l_fm = losses.feature_matching_loss(real_p, fake_p) + losses.feature_matching_loss(real_s, fake_s)
                loss_g = (args.stft_weight_gan * l_stft + args.phase_weight * args.stft_weight_gan * l_phase
                          + args.adv_weight * l_adv + args.fm_weight * l_fm)
            else:
                l_adv = l_fm = torch.zeros((), device=device)
                loss_g = l_stft + args.phase_weight * l_phase

            opt_g.zero_grad(set_to_none=True)
            loss_g.backward()
            torch.nn.utils.clip_grad_norm_(model.parameters(), 5.0)
            opt_g.step()
            step += 1
            window_steps += 1

            for key, value in (("loss_g", loss_g), ("stft", l_stft), ("phase", l_phase), ("adv", l_adv),
                               ("fm", l_fm), ("loss_d", loss_d)):
                running[key] = running.get(key, 0.0) * 0.98 + float(value) * 0.02

            if args.benchmark and step % 5 == 0:
                torch.cuda.synchronize()
                elapsed = (time.time() - window_start) / window_steps
                print(f"  {phase} step {step}: {elapsed:.2f} s/step, "
                      f"memory {torch.cuda.max_memory_allocated() / 2**30:.2f} GB", flush=True)
                window_start, window_steps = time.time(), 0

            if not args.benchmark and step % 100 == 0:
                torch.cuda.synchronize()
                sec = (time.time() - window_start) / max(1, window_steps)
                window_start, window_steps = time.time(), 0
                row = [step, phase] + [f"{running.get(k, 0):.4f}" for k in ("loss_g", "stft", "phase", "adv", "fm", "loss_d")] + [f"{sec:.3f}"]
                if step % args.validate_every == 0:
                    scores = validation.run(model, device)
                    row += [f"{scores.get(k, float('nan')):.3f}" for k in
                            ("in_0-16k", "out_0-16k", "in_16-22k", "out_16-22k", "in_22k-nyq", "out_22k-nyq")]
                    upper = scores.get("out_16-22k", 0) + scores.get("out_22k-nyq", 0) + 0.5 * scores.get("out_0-16k", 0)
                    if upper < best:
                        best = upper
                        save(best_path, model=model.state_dict(), step=step, scores=scores,
                             dim=args.dim, blocks=args.blocks)
                    # Adversarial training trades a little spectral distance for texture, so the best of
                    # that phase is kept apart; otherwise the pretrained model would always win.
                    if phase == "gan" and upper < best_gan:
                        best_gan = upper
                        save(best_gan_path, model=model.state_dict(), step=step, scores=scores,
                             dim=args.dim, blocks=args.blocks)
                    print(f"step {step} [{phase}] stft {running['stft']:.3f} phase {running['phase']:.3f} "
                          f"| LSD in->out  0-16k {scores.get('in_0-16k', 0):.2f}->{scores.get('out_0-16k', 0):.2f}"
                          f"  16-22k {scores.get('in_16-22k', 0):.2f}->{scores.get('out_16-22k', 0):.2f}"
                          f"  22k+ {scores.get('in_22k-nyq', 0):.2f}->{scores.get('out_22k-nyq', 0):.2f}"
                          f"  | {sec:.2f} s/step{'  <- best' if upper == best else ''}", flush=True)
                writer.writerow(row)
                log.flush()

            if not args.benchmark and phase == "gan" and args.snapshot_every and (step - phase_start) % args.snapshot_every == 0:
                save(out / f"gan-{step}.pt", model=model.state_dict(), step=step, dim=args.dim, blocks=args.blocks)

            if not args.benchmark and (time.time() - last_checkpoint > args.checkpoint_minutes * 60 or step == phase_end):
                save(last_path, model=model.state_dict(), mpd=mpd.state_dict(), msd=msd.state_dict(),
                     opt_g=opt_g.state_dict(), opt_d=opt_d.state_dict(), step=step, best=best, best_gan=best_gan,
                     dim=args.dim, blocks=args.blocks)
                last_checkpoint = time.time()
        phase_start = phase_end

    log.close()
    print("TRAINING DONE" if not args.benchmark else "BENCHMARK DONE", flush=True)
    _ = HOP


if __name__ == "__main__":
    main()
