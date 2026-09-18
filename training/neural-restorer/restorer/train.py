"""Trains the restorer: spectral and perceptual objectives first, then the same with discriminators.

Pretraining fits the network with

    L = L_mrstft(L, R) + 0.5 L_mrstft(side) + w_mask L_masked + w_band L_band + w_phase L_phase + w_id L_identity

where the masked term measures the error against the masking threshold the master itself sets, the band term holds
every 500 Hz band from 4 kHz to its level in the master over the crop, and the identity term keeps lossless crops
as they came. The adversarial phase adds the upscaler's multi-period and multi-resolution spectral discriminators,
which is what gives a generated band texture rather than an average.

Validation runs on songs held out by title and reports, input against output: log-spectral distance below 4 kHz,
4 to 12 kHz and 12 kHz to Nyquist; the audible noise, as the mean noise-to-mask ratio above 0 dB; the level above
16 kHz against the master; the side channel's distance from the master's over 500 Hz bands; and on lossless crops,
how much the network changed them.

    python -m restorer.train [--benchmark 20] [--out work/runs/restorer]
"""
from __future__ import annotations

import argparse
import csv
import dataclasses
import math
import time
from pathlib import Path

import torch

from upscaler import losses as upscaler_losses

from . import data, losses
from .model import N_FFT, Restorer, istft, stft

BANDS = [("0-4k", 0.0, 4_000.0), ("4-12k", 4_000.0, 12_000.0), ("12k-nyq", 12_000.0, 1e9)]
RESOLUTIONS = [(512, 128), (1024, 256), (2048, 512), (4096, 1024)]


def mrstft_loss(pred: torch.Tensor, target: torch.Tensor) -> torch.Tensor:
    """Spectral convergence plus log-magnitude L1 at four resolutions, [N, samples]."""
    total = pred.new_zeros(())
    for n_fft, hop in RESOLUTIONS:
        p = stft(pred, n_fft, hop).abs()
        t = stft(target, n_fft, hop).abs()
        sc = torch.linalg.norm((t - p).flatten(1), dim=1) / torch.linalg.norm(t.flatten(1), dim=1).clamp_min(0.1)
        log_l1 = (torch.log(t.clamp_min(1e-7)) - torch.log(p.clamp_min(1e-7))).abs().mean(dim=(1, 2))
        total = total + sc.mean() + log_l1.mean()
    return total / len(RESOLUTIONS)


def restore(model: Restorer, x: torch.Tensor, rates: torch.Tensor):
    """x [B, 2, samples] -> restored waveform [B, 2, samples], and the input and output spectra [2B, bins, frames]."""
    left, right = stft(x[:, 0]), stft(x[:, 1])
    out_left, out_right = model(left, right, (rates == 48_000).float())
    y_hat = torch.stack([istft(out_left, x.shape[2]), istft(out_right, x.shape[2])], dim=1)
    return y_hat, torch.cat([left, right]), torch.cat([out_left, out_right])


def masked_loss_by_rate(spec_out: torch.Tensor, spec_target: torch.Tensor, rates: torch.Tensor) -> torch.Tensor:
    total, count = spec_out.real.new_zeros(()), 0
    for rate in rates.unique().tolist():
        sel = rates == rate
        total = total + losses.masked_loss(spec_out[sel], spec_target[sel], int(rate)) * int(sel.sum())
        count += int(sel.sum())
    return total / max(1, count)


def identity_loss(spec_out: torch.Tensor, spec_in: torch.Tensor, lossless: torch.Tensor) -> torch.Tensor:
    """How far lossless crops moved: |out - in| against |in|, floored 85 dB under each frame's loudest bin."""
    if not bool(lossless.any()):
        return spec_out.real.new_zeros(())
    out, inp = spec_out[lossless], spec_in[lossless]
    magnitude = inp.abs()
    floor = (magnitude.amax(dim=1, keepdim=True) * 10.0 ** (-85.0 / 20.0)).clamp_min(1e-7)
    return ((out - inp).abs() / torch.maximum(magnitude, floor)).mean()


def lsd_by_band(pred_power: torch.Tensor, target_power: torch.Tensor, rate: int) -> dict[str, float]:
    hz = torch.arange(pred_power.shape[1], device=pred_power.device) * (rate / N_FFT)
    diff = (10 * torch.log10(target_power + 1e-10) - 10 * torch.log10(pred_power + 1e-10)).pow(2)
    loud = target_power.sum(dim=1) > 1e-6
    out = {}
    for name, lo, hi in BANDS:
        mask = (hz >= lo) & (hz < min(hi, rate / 2))
        per_frame = diff[:, mask, :].mean(dim=1).sqrt()
        out[name] = float(per_frame[loud].mean()) if loud.any() else float("nan")
    return out


def level_above_db(pred_power: torch.Tensor, target_power: torch.Tensor, rate: int, from_hz: float = 16_000.0) -> float:
    hz = torch.arange(pred_power.shape[1], device=pred_power.device) * (rate / N_FFT)
    band = hz >= from_hz
    return float(10.0 * torch.log10((pred_power[:, band].sum() + 1e-12) / (target_power[:, band].sum() + 1e-12)))


class Validation:
    """Fixed crops from held-out songs, drawn once so every evaluation measures the same audio."""

    def __init__(self, held: list[data.Item], coded: int, lossless: int, crop: int = 65_536) -> None:
        sampler = data.Sampler(held, seed=4242, lossless=0.0, min_rms=3e-4, augment=False, crop=crop)
        self.coded = [sampler.draw() for _ in range(coded)]
        clean = data.Sampler(held, seed=4343, lossless=1.0, min_rms=3e-4, augment=False, crop=crop)
        self.lossless = [clean.draw() for _ in range(lossless)]

    @torch.no_grad()
    def run(self, model: Restorer, device: torch.device) -> dict[str, float]:
        model.eval()
        sums: dict[str, list[float]] = {}

        def add(key: str, value: float) -> None:
            if not math.isnan(value):
                sums.setdefault(key, []).append(value)

        for x_np, y_np, rate, label in self.coded:
            x = torch.from_numpy(x_np).to(device)[None]
            y = torch.from_numpy(y_np).to(device)[None]
            rates = torch.tensor([rate], device=device)
            y_hat, spec_in, _ = restore(model, x, rates)
            spec_y = torch.cat([stft(y[:, 0]), stft(y[:, 1])])
            spec_hat = torch.cat([stft(y_hat[:, 0]), stft(y_hat[:, 1])])
            p_y = spec_y.abs().pow(2)
            for tag, spec in (("in", spec_in), ("out", spec_hat)):
                p = spec.abs().pow(2)
                for band, value in lsd_by_band(p, p_y, rate).items():
                    add(f"{tag}_{band}", value)
                add(f"{tag}_nmr", losses.audible_noise_db(spec, spec_y, rate))
                add(f"{tag}_above16k_db", level_above_db(p, p_y, rate))
            side_in = stft((x[:, 0] - x[:, 1]) * 0.7071)
            side_out = stft((y_hat[:, 0] - y_hat[:, 1]) * 0.7071)
            side_y = stft((y[:, 0] - y[:, 1]) * 0.7071)
            rate_t = torch.tensor([rate], device=device)
            add("in_side_db", 10.0 * float(losses.band_level_loss(side_in, side_y, rate_t)))
            add("out_side_db", 10.0 * float(losses.band_level_loss(side_out, side_y, rate_t)))
            family = "opus" if label.startswith("opus") else "mp3" if label.startswith("mp3") else \
                "vorbis" if label.startswith("vorbis") else "aac"
            add(f"{family}_nmr_in", sums["in_nmr"][-1])
            add(f"{family}_nmr_out", sums["out_nmr"][-1])

        for x_np, _, rate, _ in self.lossless:
            x = torch.from_numpy(x_np).to(device)[None]
            y_hat, _, _ = restore(model, x, torch.tensor([rate], device=device))
            change = (y_hat - x).pow(2).sum() / x.pow(2).sum().clamp_min(1e-12)
            add("ll_change_db", float(10.0 * torch.log10(change + 1e-20)))
        model.train()
        return {k: sum(v) / len(v) for k, v in sums.items()}


def score(s: dict[str, float]) -> float:
    """Lower is better: audible noise first, then the two upper bands' distance, and a lossless crop left alone."""
    return (s.get("out_nmr", 99.0) + 0.25 * (s.get("out_4-12k", 99.0) + s.get("out_12k-nyq", 99.0))
            + 0.2 * max(0.0, s.get("ll_change_db", 0.0) + 30.0))


def save(path: Path, **state) -> None:
    tmp = path.with_suffix(".tmp")
    torch.save(state, tmp)
    tmp.replace(path)


LOG_SCORES = ["in_0-4k", "out_0-4k", "in_4-12k", "out_4-12k", "in_12k-nyq", "out_12k-nyq", "in_nmr", "out_nmr",
              "in_above16k_db", "out_above16k_db", "in_side_db", "out_side_db", "ll_change_db",
              "opus_nmr_in", "opus_nmr_out", "aac_nmr_in", "aac_nmr_out", "mp3_nmr_in", "mp3_nmr_out",
              "vorbis_nmr_in", "vorbis_nmr_out"]
RUNNING = ["loss_g", "stft", "side", "masked", "band", "phase", "identity", "adv", "fm", "loss_d"]


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default="work/runs/restorer")
    ap.add_argument("--batch", type=int, default=8)
    ap.add_argument("--gan-batch", type=int, default=4)
    ap.add_argument("--crop", type=int, default=data.CROP)
    ap.add_argument("--disc-crop", type=int, default=16_384)
    ap.add_argument("--pretrain-steps", type=int, default=20_000)
    ap.add_argument("--gan-steps", type=int, default=12_000)
    ap.add_argument("--lr", type=float, default=5e-4)
    ap.add_argument("--lr-d", type=float, default=2e-4)
    ap.add_argument("--side-weight", type=float, default=0.5)
    ap.add_argument("--mask-weight", type=float, default=10.0)
    ap.add_argument("--band-weight", type=float, default=1.0)
    ap.add_argument("--phase-weight", type=float, default=0.3)
    ap.add_argument("--identity-weight", type=float, default=2.0)
    ap.add_argument("--adv-weight", type=float, default=1.0)
    ap.add_argument("--fm-weight", type=float, default=2.0)
    ap.add_argument("--spectral-weight-gan", type=float, default=45.0)
    ap.add_argument("--lossless", type=float, default=0.1)
    ap.add_argument("--validate-every", type=int, default=1000)
    ap.add_argument("--snapshot-every", type=int, default=4000)
    ap.add_argument("--checkpoint-minutes", type=float, default=8.0)
    ap.add_argument("--dim", type=int, default=256)
    ap.add_argument("--blocks", type=int, default=8)
    ap.add_argument("--benchmark", type=int, default=0, help="time this many steps of each phase and stop")
    ap.add_argument("--init-from", default="", help="a fine-tune: the generator from this checkpoint, at step 0 of a new schedule")
    ap.add_argument("--init-discriminators", default="", help="and the discriminators from this one (a run's last.pt)")
    ap.add_argument("--warmup", type=int, default=1000, help="steps the learning rate takes to rise")
    args = ap.parse_args()

    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    torch.backends.cudnn.benchmark = True

    items = data.load()
    train_items, held_items = data.split(items)
    print(f"{len(items)} songs: {len(train_items)} to train on, {len(held_items)} held out by title "
          f"({sum(i.japanese for i in held_items)} Japanese)", flush=True)

    model = Restorer(dim=args.dim, intermediate=args.dim * 3, blocks=args.blocks).to(device)
    mpd = upscaler_losses.MultiPeriodDiscriminator().to(device)
    msd = upscaler_losses.MultiResolutionSpectralDiscriminator().to(device)
    opt_g = torch.optim.AdamW(model.parameters(), lr=args.lr, betas=(0.8, 0.99), weight_decay=0.01)
    opt_d = torch.optim.AdamW(list(mpd.parameters()) + list(msd.parameters()), lr=args.lr_d, betas=(0.8, 0.99))
    total_steps = args.pretrain_steps + args.gan_steps

    step, best, best_gan = 0, float("inf"), float("inf")
    last_path = out / "last.pt"
    if args.init_from and not last_path.exists():
        start = torch.load(args.init_from, map_location=device, weights_only=False)
        model.load_state_dict(start["model"])
        print(f"starting from {args.init_from} (step {start.get('step', '?')})", flush=True)
        if args.init_discriminators:
            discriminators = torch.load(args.init_discriminators, map_location=device, weights_only=False)
            mpd.load_state_dict(discriminators["mpd"])
            msd.load_state_dict(discriminators["msd"])
            opt_d.load_state_dict(discriminators["opt_d"])
            print(f"discriminators from {args.init_discriminators}", flush=True)
    if last_path.exists() and not args.benchmark:
        state = torch.load(last_path, map_location=device, weights_only=False)
        model.load_state_dict(state["model"])
        mpd.load_state_dict(state["mpd"])
        msd.load_state_dict(state["msd"])
        opt_g.load_state_dict(state["opt_g"])
        opt_d.load_state_dict(state["opt_d"])
        step, best, best_gan = state["step"], state["best"], state.get("best_gan", float("inf"))
        print(f"resumed at step {step}, best {best:.3f}", flush=True)

    def lr_at(s: int) -> float:
        warm = args.warmup
        if s < warm:
            return args.lr * (s + 1) / warm
        progress = (s - warm) / max(1, total_steps - warm)
        return args.lr * (0.05 + 0.95 * 0.5 * (1.0 + math.cos(math.pi * min(1.0, progress))))

    # Validation draws from the copies the first run was measured on, so its scores stay comparable across runs;
    # copies added later (restorer.extra) are measured by restorer.evaluate instead.
    original = [dataclasses.replace(i, variants=[v for v in i.variants if ".v" in v[0].name]) for i in held_items]
    validation = None if args.benchmark else Validation(original, coded=160, lossless=40)
    sampler = data.Sampler(train_items, seed=step + 11, lossless=args.lossless, crop=args.crop)
    log_path = out / "log.csv"
    log_new = not log_path.exists()
    log = log_path.open("a", encoding="utf-8", newline="")
    writer = csv.writer(log)
    if log_new:
        writer.writerow(["step", "phase", *RUNNING, "sec_per_step", *LOG_SCORES, "score"])

    last_checkpoint = time.time()
    window_start, window_steps = time.time(), 0
    running: dict[str, float] = {}
    phases = [("pretrain", args.pretrain_steps), ("gan", args.gan_steps)]
    if args.benchmark:
        phases, step = [("pretrain", args.benchmark), ("gan", args.benchmark)], 0

    phase_start = 0
    for phase, steps in phases:
        phase_end = phase_start + steps
        while step < phase_end:
            for group in opt_g.param_groups:
                group["lr"] = lr_at(step)
            size = args.gan_batch if phase == "gan" else args.batch
            x, y, rates, labels = data.batch(sampler, size, device)
            lossless = torch.tensor([label == "lossless" for label in labels], device=device).repeat(2)
            rates2 = rates.repeat(2)

            y_hat, spec_in, spec_out = restore(model, x, rates)
            y_flat, y_hat_flat = y.transpose(0, 1).reshape(-1, y.shape[2]), y_hat.transpose(0, 1).reshape(-1, y.shape[2])

            if phase == "gan":
                # One channel of each crop, the same one of both, over a shorter stretch: what the discriminators
                # need to judge texture, at half the cost of both channels.
                offset = int(torch.randint(0, y.shape[2] - args.disc_crop + 1, ()).item())
                rows = torch.arange(size, device=device)
                pick = torch.randint(0, 2, (size,), device=device)
                y_d = y[rows, pick, offset: offset + args.disc_crop]
                y_hat_d = y_hat[rows, pick, offset: offset + args.disc_crop]
                real_p, fake_p = mpd(y_d), mpd(y_hat_d.detach())
                real_s, fake_s = msd(y_d), msd(y_hat_d.detach())
                loss_d = (upscaler_losses.discriminator_loss(real_p, fake_p)
                          + upscaler_losses.discriminator_loss(real_s, fake_s))
                opt_d.zero_grad(set_to_none=True)
                loss_d.backward()
                opt_d.step()
            else:
                loss_d = torch.zeros((), device=device)

            spec_y = stft(y_flat)
            spec_hat = stft(y_hat_flat)
            side, side_hat = (y[:, 0] - y[:, 1]) * 0.7071, (y_hat[:, 0] - y_hat[:, 1]) * 0.7071
            l_stft = mrstft_loss(y_hat_flat, y_flat)
            l_side = mrstft_loss(side_hat, side)
            l_mask = masked_loss_by_rate(spec_hat, spec_y, rates2)
            l_band = losses.band_level_loss(spec_hat, spec_y, rates2)
            l_phase = upscaler_losses.phase_loss(spec_out, spec_y)
            l_identity = identity_loss(spec_out, spec_in, lossless)
            spectral = (l_stft + args.side_weight * l_side + args.mask_weight * l_mask + args.band_weight * l_band
                        + args.phase_weight * l_phase + args.identity_weight * l_identity)

            if phase == "gan":
                real_p, fake_p = mpd(y_d), mpd(y_hat_d)
                real_s, fake_s = msd(y_d), msd(y_hat_d)
                l_adv = (upscaler_losses.generator_adversarial_loss(fake_p)
                         + upscaler_losses.generator_adversarial_loss(fake_s))
                l_fm = upscaler_losses.feature_matching_loss(real_p, fake_p) + upscaler_losses.feature_matching_loss(real_s, fake_s)
                loss_g = args.spectral_weight_gan * spectral + args.adv_weight * l_adv + args.fm_weight * l_fm
            else:
                l_adv = l_fm = torch.zeros((), device=device)
                loss_g = spectral

            opt_g.zero_grad(set_to_none=True)
            loss_g.backward()
            torch.nn.utils.clip_grad_norm_(model.parameters(), 5.0)
            opt_g.step()
            step += 1
            window_steps += 1

            for key, value in zip(RUNNING, (loss_g, l_stft, l_side, l_mask, l_band, l_phase, l_identity, l_adv, l_fm, loss_d)):
                value = float(value.detach())
                running[key] = running.get(key, value) * 0.98 + value * 0.02

            if args.benchmark and step % 5 == 0:
                torch.cuda.synchronize()
                elapsed = (time.time() - window_start) / window_steps
                print(f"  {phase} step {step}: {elapsed:.2f} s/step, memory {torch.cuda.max_memory_allocated() / 2**30:.2f} GB"
                      f" | " + " ".join(f"{k} {running[k]:.3f}" for k in RUNNING), flush=True)
                window_start, window_steps = time.time(), 0

            if not args.benchmark and step % 100 == 0:
                torch.cuda.synchronize()
                sec = (time.time() - window_start) / max(1, window_steps)
                window_start, window_steps = time.time(), 0
                row = [step, phase] + [f"{running.get(k, 0):.4f}" for k in RUNNING] + [f"{sec:.3f}"]
                if step % args.validate_every == 0:
                    scores = validation.run(model, device)
                    value = score(scores)
                    row += [f"{scores.get(k, float('nan')):.3f}" for k in LOG_SCORES] + [f"{value:.3f}"]
                    marker = ""
                    if value < best:
                        best, marker = value, "  <- best"
                        save(out / "best.pt", model=model.state_dict(), step=step, scores=scores, dim=args.dim,
                             blocks=args.blocks)
                    if phase == "gan" and value < best_gan:
                        best_gan, marker = value, marker + "  <- best adversarial"
                        save(out / "best-gan.pt", model=model.state_dict(), step=step, scores=scores, dim=args.dim,
                             blocks=args.blocks)
                    print(f"step {step} [{phase}] stft {running['stft']:.3f} masked {running['masked']:.3f} "
                          f"band {running['band']:.3f} | LSD in->out 0-4k {scores['in_0-4k']:.2f}->{scores['out_0-4k']:.2f}"
                          f"  4-12k {scores['in_4-12k']:.2f}->{scores['out_4-12k']:.2f}"
                          f"  12k+ {scores['in_12k-nyq']:.2f}->{scores['out_12k-nyq']:.2f}"
                          f" | audible noise {scores['in_nmr']:.2f}->{scores['out_nmr']:.2f} dB"
                          f" | above 16k {scores['in_above16k_db']:+.1f}->{scores['out_above16k_db']:+.1f} dB"
                          f" | side {scores['in_side_db']:.2f}->{scores['out_side_db']:.2f} dB"
                          f" | lossless change {scores['ll_change_db']:.1f} dB | {sec:.2f} s/step{marker}", flush=True)
                writer.writerow(row)
                log.flush()

            if not args.benchmark and phase == "gan" and args.snapshot_every and (step - phase_start) % args.snapshot_every == 0:
                save(out / f"gan-{step}.pt", model=model.state_dict(), step=step, dim=args.dim, blocks=args.blocks)

            # A file named STOP in the run folder ends the run at the next hundredth step, after a checkpoint. Killing
            # a process in the middle of a CUDA kernel left the display driver in error, and the machine stopped with
            # a bugcheck five minutes later; this is the way to stop a run instead.
            stop = not args.benchmark and step % 100 == 0 and (out / "STOP").exists()
            if not args.benchmark and (stop or time.time() - last_checkpoint > args.checkpoint_minutes * 60 or step == phase_end):
                save(last_path, model=model.state_dict(), mpd=mpd.state_dict(), msd=msd.state_dict(),
                     opt_g=opt_g.state_dict(), opt_d=opt_d.state_dict(), step=step, best=best, best_gan=best_gan,
                     dim=args.dim, blocks=args.blocks)
                last_checkpoint = time.time()
            if stop:
                (out / "STOP").unlink(missing_ok=True)
                log.close()
                print(f"STOPPED at step {step}; run again to resume", flush=True)
                return
        phase_start = phase_end

    log.close()
    print("TRAINING DONE" if not args.benchmark else "BENCHMARK DONE", flush=True)


if __name__ == "__main__":
    main()
