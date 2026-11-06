"""
RESEARCH-PLAN-3 probe: does the student fit the crops it trained on better than crops it never saw? Scores
a model on a sample of every training set and on the whole of every validation set with the trainer's own
centre-luma PSNR, per-crop hardness (train_served.hardness: 3x3-averaged |r| over 4 levels), the residual
on the target's strong edges against the rest, and line sharpness pooled over the set (the model's Sobel
over the teacher's on faint and strong edge pixels, as measure.lines). A model that is as wrong on crops it
has seen about 180 times as on crops it has never seen is under-fitting (capacity, or a target it cannot
express), not short of data like them.

col2 sets are scored on their @rest rows: the fill rows trained toward 8-orientation ensemble labels, not
the teacher targets on disk.

    python probe_gap.py hyb-c48n8e-b64n4-m1-sharp02-last --sample 1024
"""

import argparse
import math

import numpy as np
import torch
import torch.nn.functional as F

from common import WORK, collapse, load_student, luma, margin, split_name, student
from crop_stats import is_fill, stats
from sets import open_set

LUMA = (0.299, 0.587, 0.114)
PAIRS = [("train", "val"), ("served_train", "served_val"), ("col_train", "col_val"),
         ("col_served_train", "col_served_val"), ("col2_train@rest", "col2_val@rest"),
         ("col2_served_train@rest", "col2_served_val@rest")]
RUN = 128


def sobel(x: torch.Tensor) -> torch.Tensor:
    k = torch.tensor([[1., 0, -1], [2, 0, -2], [1, 0, -1]], device=x.device)
    g = F.conv2d(F.pad(x, (1, 1, 1, 1), mode="reflect"), torch.stack([k, k.t()])[:, None])
    return (g * g).sum(1, keepdim=True).sqrt()


def runs_for(name: str, sample: int, rng: np.random.Generator):
    base, _, view = name.partition("@")
    lr, hr = open_set(base)
    n = len(lr)
    keep = None
    if view:
        fill = is_fill(stats(base, n))
        keep = fill if view == "fill" else ~fill
    if sample and sample < n:
        starts = sorted(rng.choice(n - RUN, size=sample // RUN, replace=False).tolist())
    else:
        starts = list(range(0, n, RUN))
    return lr, hr, keep, starts


def score(model, name: str, sample: int, trim: int, device, rng) -> dict:
    lr, hr, keep, starts = runs_for(name, sample, rng)
    w = torch.tensor(LUMA, device=device)
    psnr, hard, edge, rest, edge_share = [], [], [], [], []
    sums = {"gp_faint": 0.0, "gt_faint": 0.0, "gp_strong": 0.0, "gt_strong": 0.0}
    for s in starts:
        e = min(s + RUN, len(lr))
        x_np, y_np = lr[s:e], hr[s:e]
        if keep is not None:
            k = keep[s:e]
            if not k.any():
                continue
            x_np, y_np = x_np[k], y_np[k]
        for j in range(0, len(x_np), 32):
            x = torch.from_numpy(np.ascontiguousarray(x_np[j:j + 32])).to(device).permute(0, 3, 1, 2).float() / 255
            y = torch.from_numpy(np.ascontiguousarray(y_np[j:j + 32, trim:-trim, trim:-trim])).to(device).float() @ w
            y = y.round().clamp(0, 255)[:, None]
            with torch.no_grad():
                p = luma(model(x).clamp(0, 1))[..., trim:-trim, trim:-trim]
            p = (p * 255).round()[:, None]
            r = p - y
            mse = (r * r).mean(dim=(1, 2, 3))
            psnr += [10 * math.log10(255 ** 2 / max(v, 1e-10)) for v in mse.tolist()]
            hard += F.relu(F.avg_pool2d(r.abs(), 3, 1) - 4).mean(dim=(1, 2, 3)).tolist()
            gt = sobel(y)
            strong = gt > 64
            on = F.max_pool2d(strong.float(), 3, 1, 1) > 0.5
            faint = (gt > 16) & (gt <= 64) & ~(F.max_pool2d(strong.float(), 7, 1, 3) > 0.5)
            gp = sobel(p)
            a = r.abs()
            for i in range(len(a)):
                m = on[i]
                edge.append(a[i][m].mean().item() if m.any() else float("nan"))
                rest.append(a[i][~m].mean().item() if (~m).any() else float("nan"))
                sq = r[i] * r[i]
                edge_share.append((sq[m].sum() / sq.sum().clamp_min(1e-6)).item())
            sums["gp_faint"] += gp[faint].sum().item()
            sums["gt_faint"] += gt[faint].sum().item()
            sums["gp_strong"] += gp[strong].sum().item()
            sums["gt_strong"] += gt[strong].sum().item()
    hard_a = np.array(hard)
    return {"n": len(psnr), "psnr": float(np.mean(psnr)), "psnr_p10": float(np.percentile(psnr, 10)),
            "hard": float(hard_a.mean()), "hard_p90": float(np.percentile(hard_a, 90)),
            "hard_top10_share": float(np.sort(hard_a)[-max(1, len(hard_a) // 10):].sum() / max(hard_a.sum(), 1e-9)),
            "edge": float(np.nanmean(edge)), "rest": float(np.nanmean(rest)), "edge_share": float(np.nanmean(edge_share)),
            "sharp_faint": sums["gp_faint"] / max(sums["gt_faint"], 1e-9),
            "sharp_strong": sums["gp_strong"] / max(sums["gt_strong"], 1e-9)}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("checkpoint")
    parser.add_argument("--sample", type=int, default=1024, help="crops per training set (validation sets are whole)")
    parser.add_argument("--cpu", action="store_true")
    args = parser.parse_args()
    device = torch.device("cpu" if args.cpu else "cuda")
    spec, _ = split_name(args.checkpoint)
    model = student(spec)
    load_student(model, torch.load(WORK / f"student-{args.checkpoint}.pt", map_location="cpu", weights_only=True))
    model = collapse(model.eval()).to(device)
    trim = 2 * margin(spec)
    rng = np.random.default_rng(3)
    print(f"{args.checkpoint}: centre PSNR, hardness (mean, p90, share of the sum in the hardest tenth), mean |r| on "
          f"strong edges (dilated 1) and elsewhere, share of squared error on edges, pooled sharpness ratios")
    print(f"{'set':24} {'n':>5} {'psnr':>6} {'p10':>6} {'hard':>6} {'p90':>6} {'top10':>6} {'edge':>5} {'rest':>5} {'eshare':>6} {'faint':>6} {'strong':>6}")
    for tr, va in PAIRS:
        for name, sample in ((tr, args.sample), (va, 0)):
            r = score(model, name, sample, trim, device, rng)
            print(f"{name:24} {r['n']:5} {r['psnr']:6.2f} {r['psnr_p10']:6.2f} {r['hard']:6.3f} {r['hard_p90']:6.3f} "
                  f"{r['hard_top10_share']:6.1%} {r['edge']:5.2f} {r['rest']:5.2f} {r['edge_share']:6.1%} "
                  f"{r['sharp_faint']:6.3f} {r['sharp_strong']:6.3f}", flush=True)


if __name__ == "__main__":
    main()
