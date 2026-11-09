"""
Round four onwards: Real-ESRGAN's look on clean and damaged sources alike. Every target is Real-ESRGAN's
own output for that exact input, clean crops from make_dataset.py and damaged copies from
make_served.py, so the student is never asked for an average of answers.

Targets are luma only, because that is all Maki keeps from the model (`UpscalePixels.TakeColourFrom`);
it makes every set a third of the size on the GPU. Training sets move to the GPU a chunk at a time from
files on disk, so the process holds no copy of them in RAM. Validation sets stay on disk and are read a
chunk at a time at each eval, which keeps them off the GPU (the GAN stage spilled into system memory
with them there).

Each validation set is scored on its own, beside round one's score, so a gain on one kind of page is
never hidden by a loss on another. A checkpoint is kept only if no set loses more than --slack dB.

    python train_served.py c48n8 --init student-c48n8-served.pt --tag colour \
        --sets train served_train col_train col_served_train --val val served_val col_val col_served_val

A set name may end in `@fill` or `@rest`: the crops whose teacher target is, or is not, mostly flat
fill by crop_stats.py. Both views share one copy of the set on the GPU.

Run A2's options (PLAN.md): --pooled adds w * |avg_pool8(r)| to the loss, which charges a blotch (a
coherent block of residual) and not pixel noise; --ema keeps an exponential average of the weights and
scores and saves that; --flips applies one random flip or rotation per batch to input and target.
RESEARCH-PLAN.md's: --mssim, --grad and --ldl (losses.py), and the discriminator fixes in gan.py.
The crop margin the loss ignores follows the student's reach (common.margin).
"""

import argparse
import math
import re
import sys
import time

import numpy as np
import torch
import torch.nn.functional as F

import torch.nn as nn

from common import WORK, load_student, load_teacher, luma, margin, student
from crop_stats import is_fill, stats
from gan import Discriminator, d_loss, g_loss, logit_gap
from losses import (coherent_error, edge_band, fill_mask, gradient_variance, masked_fine, sharpness_deficit, sharpness_deficit_relative, ldl_map,
                    masked_band, masked_pooled, ms_ssim, power_loss, sharpness_deficit_line, sobel_l1, thin_line_deficit, top_share)
from sets import open_set, to_gpu

LUMA = (0.299, 0.587, 0.114)


def load_sets(names: list[str], device, trim: int, swaps: dict[str, str] | None = None) -> list[tuple]:
    """(inputs, targets, rows or None) per name; `base@fill` and `base@rest` index one upload of base.
    `swaps` maps a base set to a make_ensemble.py set whose targets replace that base's fill rows in place,
    so ensemble labels cost no GPU memory."""
    uploaded = {}
    out = []
    for name in names:
        base, _, view = name.partition("@")
        if base not in uploaded:
            lr, hr = open_set(base)
            # Only the centre of a target is ever scored, so only the centre goes to the GPU.
            uploaded[base] = (to_gpu(lr, device), to_gpu(hr, device, luma_only=True, trim=trim))
            if swaps and base in swaps:
                fill = torch.from_numpy(is_fill(stats(base, len(lr))).nonzero()[0]).to(device)
                se_lr, se_hr = open_set(swaps[base])
                assert len(se_hr) == len(fill), f"{swaps[base]} has {len(se_hr)} rows, {base} has {len(fill)} fill rows"
                assert (np.asarray(se_lr[0:1]) == lr[int(fill[0]):int(fill[0]) + 1]).all(), f"{swaps[base]} is out of order"
                uploaded[base][1][fill] = to_gpu(se_hr, device, luma_only=True, trim=trim)
                print(f"{base}: {len(fill)} fill targets swapped for {swaps[base]}", flush=True)
        x, y = uploaded[base]
        rows = None
        if view:
            fill = is_fill(stats(base, len(x)))
            rows = torch.from_numpy((fill if view == "fill" else ~fill).nonzero()[0]).to(device)
        out.append((x, y, rows))
        print(f"{name}: {len(x) if rows is None else len(rows)} crops", flush=True)
    return out


class ValSet:
    """A validation set read from disk at each eval, `@fill`/`@rest` views as row masks."""

    def __init__(self, name: str, trim: int, device):
        base, _, view = name.partition("@")
        self.lr, self.hr = open_set(base)
        self.keep = None
        if view:
            fill = is_fill(stats(base, len(self.lr)))
            self.keep = fill if view == "fill" else ~fill
        self.trim, self.device = trim, device
        self.weights = torch.tensor(LUMA, device=device)
        print(f"{name}: {len(self.lr) if self.keep is None else int(self.keep.sum())} crops (on disk)", flush=True)

    def batches(self, size: int = 256):
        t = self.trim
        for i in range(0, len(self.lr), size):
            lr, hr = self.lr[i:i + size], self.hr[i:i + size]
            if self.keep is not None:
                k = self.keep[i:i + len(lr)]
                if not k.any():
                    continue
                lr, hr = lr[k], hr[k]
            x = torch.from_numpy(np.ascontiguousarray(lr)).to(self.device).permute(0, 3, 1, 2)
            h = torch.from_numpy(np.ascontiguousarray(hr[:, t:hr.shape[1] - t, t:hr.shape[2] - t])).to(self.device)
            yield x, (h.float() @ self.weights).round().clamp(0, 255)


def reference_curve(path: str) -> dict[int, float]:
    """step -> mean validation dB from another run's log, for --behind."""
    curve = {}
    for line in open(path, encoding="utf-8"):
        if line.startswith("step ") and "(" in line:
            head, rest = line.split(":", 1)
            values = [float(v) for v in re.findall(r" (\d+\.\d+) \(", rest)]
            if values:
                curve[int(head.split()[1])] = sum(values) / len(values)
    return curve


def with_features(model, x):
    """The model's output and its last body activation (the input to the final conv)."""
    return model.forward_features(x)


def standardise(f: torch.Tensor) -> torch.Tensor:
    mean = f.mean(dim=(0, 2, 3), keepdim=True)
    std = f.std(dim=(0, 2, 3), keepdim=True).clamp_min(1e-4)
    return (f - mean) / std


def pick(x, y, rows, n, device, probs=None):
    if probs is None:
        i = torch.randint(0, len(x) if rows is None else len(rows), (n,), device=device)
    else:
        i = torch.multinomial(probs, n, replacement=True)
    if rows is not None:
        i = rows[i]
    return x[i], y[i]


def hardness(model, x, y, trim: int, at: float) -> torch.Tensor:
    """Per crop, the mean over its centre of how far the 3x3-averaged |model - target| sits over `at` levels:
    the smudges, kept source marks and soft edges the owner circles, which a uniform draw reaches rarely."""
    out = []
    with torch.no_grad(), torch.autocast("cuda", dtype=torch.bfloat16):
        for i in range(0, len(x), 128):
            p = luma(model(x[i:i + 128].float() / 255).float().clamp(0, 1))[..., trim:-trim, trim:-trim]
            e = F.avg_pool2d((p - y[i:i + 128, 0].float() / 255).abs()[:, None] * 255, 3, 1)
            out.append(F.relu(e - at).mean(dim=(1, 2, 3)).float())
    return torch.cat(out)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("spec")
    parser.add_argument("--init", required=True, help="checkpoint under WORK, or none to start from scratch")
    parser.add_argument("--tag", required=True)
    parser.add_argument("--sets", nargs="+", required=True)
    parser.add_argument("--weights", nargs="+", type=float, help="sampling share per set (default: by size)")
    parser.add_argument("--val", nargs="+", required=True)
    parser.add_argument("--iterations", type=int, default=40000)
    parser.add_argument("--batch", type=int, default=32)
    parser.add_argument("--lr", type=float, default=2e-4)
    parser.add_argument("--slack", type=float, default=0.1, help="dB any validation set may lose against the start")
    parser.add_argument("--eval-every", type=int, default=2500)
    parser.add_argument("--pooled", type=float, default=0.0, help="weight of the |avg_pool8(r)| term")
    parser.add_argument("--ema", type=float, default=0.0, help="EMA decay, e.g. 0.999; 0 is off")
    parser.add_argument("--flips", action="store_true")
    parser.add_argument("--band", type=float, default=0.0, help="weight of measure.py's band-pass blotch term")
    parser.add_argument("--swap", nargs="*", default=[], help="base=ensemble_set: replace base's fill targets with make_ensemble.py labels")
    parser.add_argument("--mssim", type=float, default=0.0, help="weight of 1 - MS-SSIM on the luma centre")
    parser.add_argument("--grad", type=float, default=0.0, help="weight of the Sobel-gradient L1")
    parser.add_argument("--ldl", type=float, default=0.0, help="weight of LDL's artifact-weighted L1 (needs --ema)")
    parser.add_argument("--fill-mask", action="store_true",
                        help="apply the pooled and band terms to fill pixels only (RESEARCH-PLAN-2 B1)")
    parser.add_argument("--gv", type=float, default=0.0, help="weight of the gradient variance loss")
    parser.add_argument("--fine", type=float, default=0.0, help="weight of the pixel-scale residual inside fills (static)")
    parser.add_argument("--fill-faint", type=float, default=0.0,
                        help="also keep the fill terms 2 px off lines fainter than 64 (a Sobel threshold, e.g. 24)")
    parser.add_argument("--sharp", type=float, default=0.0,
                        help="weight of the one-sided sharpness deficit on the target's edges, faint ones included")
    parser.add_argument("--sharp-rel", type=float, default=0.0,
                        help="weight of the relative sharpness deficit on faint edges (16 to 64 levels, off strong ones)")
    parser.add_argument("--sharp-line", type=float, default=0.0,
                        help="weight of the relative sharpness deficit on line-like faint edges, with an over-sharpening charge")
    parser.add_argument("--thin", type=float, default=0.0,
                        help="weight of the one-sided depth deficit on the target's thin dark lines")
    parser.add_argument("--fine-edge", type=float, default=0.0,
                        help="weight of the pixel-scale residual within 2 px of strong edges (jagged lines)")
    parser.add_argument("--marks", type=float, default=0.0,
                        help="weight of the coherent-error term (3x3-averaged |r| over --marks-at, squared)")
    parser.add_argument("--marks-at", type=float, default=8.0, help="levels the 3x3-averaged |r| may reach for free")
    parser.add_argument("--gain", nargs="*", default=[], help="term=x: multiplies that term's weight after --quarter")
    parser.add_argument("--hard", type=float, default=0.0,
                        help="share of each set's draws weighted by the crop's hardness (0 is a uniform draw)")
    parser.add_argument("--hard-every", type=int, default=5000, help="steps between hardness rescores")
    parser.add_argument("--hard-at", type=float, default=4.0, help="levels of 3x3-averaged error that count as easy")
    parser.add_argument("--topk", type=float, default=0.0, help="weight of the mean of each crop's worst 20%% of |r|")
    parser.add_argument("--feat", type=float, default=0.0,
                        help="weight of feature distillation from the teacher's last body activation")
    parser.add_argument("--lp", type=float, default=0.0, help="replace the L1 with (r^2 + eps^2)^(p/2) at this p")
    parser.add_argument("--quarter", nargs="*", default=[],
                        help="terms whose weight is set so they start at a quarter of the L1 (mssim grad gv topk feat ldl)")
    parser.add_argument("--gan", type=float, default=0.0, help="weight of gan.py's adversarial term (Real-ESRGAN's stage 2)")
    parser.add_argument("--gan-warmup", type=int, default=1000, help="steps the discriminator trains before the student feels it")
    parser.add_argument("--d-lr", type=float, default=3e-4)
    parser.add_argument("--d-gain", type=float, default=8.0, help="gain on the detail the discriminator sees")
    parser.add_argument("--d-mask", action="store_true",
                        help="average the GAN terms over fills plus the 11 px band around strong edges only")
    parser.add_argument("--d-noise", type=float, default=0.0, help="instance noise in levels on real and fake")
    parser.add_argument("--gap-min", type=float, default=0.0,
                        help="stop unless the logit gap is at least this at the end of warm-up and rising; "
                             "one extension of the warm-up is allowed (0 keeps the old loss test)")
    parser.add_argument("--clip", type=float, default=4.0,
                        help="clip the gradient norm at this multiple of its running average (0 is off)")
    parser.add_argument("--drop-stop", type=float, default=3.0,
                        help="stop when any validation set falls this many dB under its own best (0 is off)")
    parser.add_argument("--behind", help="LOG:DB:FROM (LOG under WORK/logs) - stop when the mean validation score is more than DB under "
                                         "LOG's at the same step, from step FROM on (past LOG's last step, its last)")
    args = parser.parse_args()
    if args.ldl and not args.ema:
        parser.error("--ldl compares the live model with its EMA; pass --ema")

    torch.manual_seed(0)
    torch.backends.cudnn.benchmark = True  # the batch shape is fixed, so the tuned kernels are reused
    device = torch.device("cuda")
    m = margin(args.spec)
    m2 = 2 * m
    print(f"{args.spec}: margin {m} low-res px", flush=True)
    train = load_sets(args.sets, device, m2, dict(s.split("=") for s in args.swap))
    sizes = torch.tensor([float(len(x) if rows is None else len(rows)) for x, _, rows in train])
    shares = torch.tensor(args.weights) if args.weights else sizes
    shares = shares / shares.sum()
    val = {name: ValSet(name, m2, device) for name in args.val}

    def centre(t):
        return t[..., m2:-m2, m2:-m2]

    def score(model, valset):
        values = []
        with torch.no_grad():
            for x, b in valset.batches():
                for j in range(0, len(x), 64):
                    p = luma(model(x[j:j + 64].float() / 255).clamp(0, 1))
                    a = (centre(p) * 255).round()
                    values.append(10 * math.log10(255 ** 2 / max(((a - b[j:j + 64]) ** 2).mean().item(), 1e-10)))
        return sum(values) / len(values)

    model = student(args.spec).to(device)
    if args.init != "none":
        load_student(model, torch.load(WORK / args.init, map_location="cpu", weights_only=True))
    model.eval()
    start = {k: score(model, v) for k, v in val.items()}
    print("start: " + "  ".join(f"{k} {v:.2f}" for k, v in start.items()), flush=True)
    model.train()
    ema = None
    if args.ema:
        ema = torch.optim.swa_utils.AveragedModel(
            model, multi_avg_fn=torch.optim.swa_utils.get_ema_multi_avg_fn(args.ema))
        ema.eval()

    teacher = proj = None
    if args.feat:
        # The teacher's last body activation (64 channels, low-res) is the richer signal; the student's own
        # last one reaches it through a trained 1x1, and both are standardised per channel over the batch.
        teacher = load_teacher(device)
        for q in teacher.parameters():
            q.requires_grad_(False)
        with torch.no_grad():
            width = with_features(model, torch.zeros(1, 3, 16, 16, device=device))[1].shape[1]
        proj = nn.Conv2d(width, 64, 1).to(device)

    probs = [None] * len(train)

    def rescore(judge):
        judge.eval()
        scored = {}
        for x, y, _ in train:
            if id(x) not in scored:
                scored[id(x)] = hardness(judge, x, y, m2, args.hard_at)
        model.train()
        parts = []
        for n, (x, _, rows) in enumerate(train):
            h = scored[id(x)] if rows is None else scored[id(x)][rows]
            h = h.clamp(max=float(torch.quantile(h, 0.995)))
            top = h.sort(descending=True).values
            probs[n] = (1 - args.hard) / len(h) + args.hard * h / h.sum().clamp_min(1e-12)
            parts.append(f"{args.sets[n]} {float(h.mean()):.3f} top10% {float(top[:max(1, len(h) // 10)].sum() / top.sum().clamp_min(1e-12)):.0%}")
        print("hardness: " + "  ".join(parts), flush=True)

    def batch():
        which = torch.multinomial(shares, args.batch, replacement=True)
        # One forward over the whole batch: every term is a mean over crops, so this is the same loss as
        # one pass per set weighted by its share, and 11 small passes cost three times as much.
        picked = [pick(*train[s], int((which == s).sum()), device, probs[s]) for s in which.unique().tolist()]
        x, y = torch.cat([a for a, _ in picked]), torch.cat([b for _, b in picked])
        if args.flips:
            k = int(torch.randint(0, 8, ()))
            if k & 4:
                x, y = x.flip(3), y.flip(3)
            x, y = x.rot90(k & 3, (2, 3)), y.rot90(k & 3, (2, 3))
        return x, y

    def terms(x, y):
        """Every loss term, unweighted; the loss is their sum under `weights`."""
        xf = x.float() / 255
        with torch.autocast("cuda", dtype=torch.bfloat16):
            p, feats = with_features(model, xf)
        pl = centre(luma(p.float()))
        t = y[:, 0].float() / 255
        r = (pl - t)[:, None]
        out = {"l1": power_loss(r, args.lp) if args.lp else r.abs().mean()}
        mask = fill_mask(t[:, None] * 255, args.fill_faint) if (args.fill_mask or args.fine) else None
        if args.fine:
            out["fine"] = masked_fine(r, mask)
        if args.sharp:
            out["sharp"] = sharpness_deficit(pl[:, None], t[:, None])
        if args.sharp_rel:
            out["sharp_rel"] = sharpness_deficit_relative(pl[:, None], t[:, None])
        if args.sharp_line:
            out["sharp_line"] = sharpness_deficit_line(pl[:, None], t[:, None])
        if args.thin:
            out["thin"] = thin_line_deficit(pl[:, None], t[:, None])
        if args.fine_edge:
            out["fine_edge"] = masked_fine(r, edge_band(t[:, None] * 255))
        fills = mask if args.fill_mask else None
        if args.pooled:
            out["pooled"] = masked_pooled(r, fills) if fills is not None else F.avg_pool2d(r, 8).abs().mean()
        if args.band:
            # measure.py's blotch: structure 3 to 15 px across, minus the whole-area offset.
            out["band"] = masked_band(r, fills) if fills is not None else \
                (F.avg_pool2d(r, 7, 1)[..., 12:-12, 12:-12] - F.avg_pool2d(r, 31, 1)).abs().mean()
        if args.mssim:
            out["mssim"] = 1 - ms_ssim(pl[:, None].clamp(0, 1), t[:, None])
        if args.grad:
            out["grad"] = sobel_l1(pl[:, None], t[:, None])
        if args.gv:
            out["gv"] = gradient_variance(pl[:, None], t[:, None])
        if args.topk:
            out["topk"] = top_share(r)
        if args.marks:
            out["marks"] = coherent_error(r, args.marks_at / 255)
        if args.feat:
            with torch.no_grad(), torch.autocast("cuda", dtype=torch.bfloat16):
                ft = with_features(teacher, xf)[1].float()
            fs = proj(feats.float())
            out["feat"] = (standardise(fs) - standardise(ft))[..., m:-m, m:-m].abs().mean()
        if args.ldl:
            with torch.no_grad(), torch.autocast("cuda", dtype=torch.bfloat16):
                pe = centre(luma(ema.module(xf).float()))
            out["ldl"] = (ldl_map(r, (pe - t)[:, None]) * r.abs()).mean()
        return out, (pl, t, xf)

    weights = {"l1": 1.0, "pooled": args.pooled, "band": args.band, "mssim": args.mssim, "grad": args.grad,
               "gv": args.gv, "topk": args.topk, "feat": args.feat, "ldl": args.ldl, "fine": args.fine,
               "fine_edge": args.fine_edge, "sharp": args.sharp, "sharp_rel": args.sharp_rel, "sharp_line": args.sharp_line,
               "thin": args.thin, "marks": args.marks}
    if args.quarter:
        # RESEARCH-PLAN-2's rule: a new term starts at a quarter of the L1, whatever its natural scale.
        with torch.no_grad():
            sums = {}
            for _ in range(10):
                parts, _ = terms(*batch())
                for k, v in parts.items():
                    sums[k] = sums.get(k, 0.0) + float(v)
        for k in args.quarter:
            assert k in sums, f"--quarter {k}: that term is not on"
            weights[k] = 0.25 * sums["l1"] / max(sums[k], 1e-12)
        print("weights: " + "  ".join(f"{k} {weights[k]:.4g}" for k in sums), flush=True)
    for item in args.gain:
        k, v = item.split("=")
        assert k in weights, f"--gain {k}: no such term"
        weights[k] *= float(v)
    if args.gain:
        print("gained: " + "  ".join(f"{k} {weights[k]:.4g}" for k in weights if weights[k]), flush=True)
    seen = {}

    params = list(model.parameters()) + (list(proj.parameters()) if proj is not None else [])
    optimizer = torch.optim.AdamW(params, lr=args.lr, weight_decay=0)
    schedule = torch.optim.lr_scheduler.OneCycleLR(optimizer, max_lr=args.lr, total_steps=args.iterations,
                                                   pct_start=0.05, final_div_factor=100)
    disc = d_optimizer = None
    if args.gan:
        disc = Discriminator(gain=args.d_gain).to(device)
        d_optimizer = torch.optim.Adam(disc.parameters(), lr=args.d_lr, betas=(0.5, 0.999))
    d_seen = g_seen = 0.0
    warmup, gaps = args.gan_warmup, {}
    best = -1.0
    peak = dict(start)
    grad_avg = 0.0
    behind = None
    if args.behind:
        path, gap, since = args.behind.rsplit(":", 2)
        behind = (reference_curve(str(WORK / "logs" / path)), float(gap), int(since))
    if args.hard:
        rescore(model)
    started = time.time()
    for step in range(1, args.iterations + 1):
        if args.hard and step > 1 and step % args.hard_every == 1:
            rescore(model if ema is None else ema.module)
        fakes, reals, conditions, d_masks = [], [], [], []
        x, y = batch()
        parts, (pl, t, xf) = terms(x, y)
        loss = sum(weights[k] * v for k, v in parts.items())
        for k, v in parts.items():
            seen[k] = 0.99 * seen.get(k, float(v)) + 0.01 * float(v)
        if disc is not None:
            fakes.append(pl[:, None])
            reals.append(t[:, None])
            if args.d_mask:
                t255 = t[:, None] * 255
                k = torch.tensor([[1., 0, -1], [2, 0, -2], [1, 0, -1]], device=device)
                gx = F.conv2d(F.pad(t255, (1, 1, 1, 1), mode="reflect"), torch.stack([k, k.t()])[:, None])
                edges = F.max_pool2d(((gx * gx).sum(1, keepdim=True).sqrt() > 64).float(), 11, 1, 5)
                d_masks.append(((fill_mask(t255) + edges) > 0).float())
            up = F.interpolate(luma(xf)[:, None], scale_factor=2, mode="bilinear", align_corners=False)
            conditions.append(centre(up))
        if disc is not None:
            fake, real, condition = torch.cat(fakes), torch.cat(reals), torch.cat(conditions)
            dmask = torch.cat(d_masks) if d_masks else None
            noise = args.d_noise / 255
            if step > warmup:
                for q in disc.parameters():
                    q.requires_grad_(False)
                with torch.autocast("cuda", dtype=torch.bfloat16):
                    adversarial = g_loss(disc, fake, condition, dmask, noise).float()
                loss = loss + args.gan * adversarial
                for q in disc.parameters():
                    q.requires_grad_(True)
                g_seen = 0.99 * g_seen + 0.01 * adversarial.item()
        if not torch.isfinite(loss):
            print(f"step {step}: loss is not finite; stopping", flush=True)
            sys.exit(4)
        optimizer.zero_grad(set_to_none=True)
        loss.backward()
        if args.clip:
            # Relative, so it needs no idea of the loss's scale: a step whose gradient is several times the
            # recent norm is cut back to that, which is what a run does just before it diverges.
            limit = args.clip * grad_avg if step > 100 else float("inf")
            norm = float(torch.nn.utils.clip_grad_norm_(params, limit))
            grad_avg = norm if step == 1 else 0.99 * grad_avg + 0.01 * min(norm, limit)
        optimizer.step()
        schedule.step()
        if disc is not None:
            d_optimizer.zero_grad(set_to_none=True)
            with torch.autocast("cuda", dtype=torch.bfloat16):
                d_term = d_loss(disc, real, fake, condition, dmask, noise).float()
            d_term.backward()
            d_optimizer.step()
            d_seen = 0.99 * d_seen + 0.01 * d_term.item()
            if args.gap_min and step % 500 == 0 and step <= warmup:
                with torch.autocast("cuda", dtype=torch.bfloat16):
                    gap, share = logit_gap(disc, real, fake.detach(), condition, dmask)
                gaps[step] = gap
                print(f"step {step}: discriminator d {d_seen:.3f} logit gap {gap:+.4f} real>fake {share:.1%}", flush=True)
                if step == warmup:
                    earlier = gaps.get(warmup // 2, float("-inf"))
                    if gap >= args.gap_min and gap > earlier:
                        print(f"discriminator learning (gap {gap:+.4f}, was {earlier:+.4f}); the student feels it from here", flush=True)
                    elif warmup == args.gan_warmup:
                        warmup *= 2
                        print(f"discriminator gap {gap:+.4f} short of {args.gap_min}; warm-up extended to {warmup}", flush=True)
                    else:
                        print(f"discriminator inert (gap {gap:+.4f}); stopping", flush=True)
                        sys.exit(3)
            elif not args.gap_min and step == args.gan_warmup:
                print(f"discriminator after warm-up: d {d_seen:.3f}", flush=True)
                if d_seen > 1.95:
                    # Still the hinge loss of a constant output: the student would train against nothing.
                    print("discriminator did not learn during warm-up; stopping", flush=True)
                    sys.exit(3)
        if ema is not None:
            ema.update_parameters(model)
        if step == 200:
            print(f"step 200: {step / (time.time() - started):.1f} it/s, "
                  f"{torch.cuda.max_memory_allocated() / 2 ** 30:.1f} GB peak", flush=True)
        if step % args.eval_every == 0 or step == args.iterations:
            judged = model if ema is None else ema.module
            model.eval()
            now = {k: score(judged, v) for k, v in val.items()}
            model.train()
            kept = all(now[k] >= start[k] - args.slack for k in now)
            mean = sum(now.values()) / len(now)
            if kept and mean > best:
                best = mean
                torch.save(judged.state_dict(), WORK / f"student-{args.spec}-{args.tag}.pt")
            # The current weights too: a run that trades validation PSNR for the tails never beats its
            # first eval, so the gated checkpoint alone would hide what it learned (check-ins read this).
            torch.save(judged.state_dict(), WORK / f"student-{args.spec}-{args.tag}-last.pt")
            detail = "  ".join(f"{k} {v:.2f} ({v - start[k]:+.2f})" for k, v in now.items())
            gan = f"  d {d_seen:.3f} g {g_seen:+.3f}" if disc is not None else ""
            gan += "  terms " + " ".join(f"{k} {weights[k] * v:.2e}" for k, v in seen.items())
            print(f"step {step}: {detail}  {'kept' if kept else 'a set lost ground'}  best {best:.2f}  "
                  f"{step / (time.time() - started):.0f} it/s{gan}", flush=True)
            peak = {k: max(peak[k], now[k]) for k in now}
            fallen = [k for k in now if args.drop_stop and now[k] < peak[k] - args.drop_stop]
            if fallen:
                print(f"step {step}: {', '.join(fallen)} fell over {args.drop_stop} dB under its best; stopping", flush=True)
                sys.exit(5)
            if behind and step >= behind[2] and behind[0]:
                curve, gap, _ = behind
                ref = curve.get(step, curve[max(curve)] if step > max(curve) else None)
                if ref is not None and mean < ref - gap:
                    print(f"step {step}: mean {mean:.2f} dB is over {gap} dB behind the reference's {ref:.2f}; stopping", flush=True)
                    sys.exit(6)
    # The final weights whatever the gate said, so a run can still be judged by measure.py.
    torch.save((model if ema is None else ema.module).state_dict(), WORK / f"student-{args.spec}-{args.tag}-last.pt")


if __name__ == "__main__":
    main()
