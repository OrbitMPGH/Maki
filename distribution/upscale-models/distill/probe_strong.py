"""
RESEARCH-PLAN-3 probe: which strong edges are the soft ones. On the teacher's strong-edge pixels (Sobel over
64), the model's Sobel over the teacher's, split by the teacher's local contrast (7x7 range: 64 to 128, 128 to
192, over 192) and by whether the pixel lies on a thin line (at most 8 pixels of the 5x5 at least 24 levels
under the 7x7 mean, dilated 1), plus the teacher's own input-noise sensitivity in each bin. Owner / colour /
grey means.

    python probe_strong.py hyb-c48n8e-b64n4-m1-sharp02-last --cpu
"""

import argparse
import json

import numpy as np
import torch

from evaluate import luma_u8, take_colour_from
from measure import REFERENCE, cached_teacher, lanczos_2x, load_page, load_teacher, model_runner, pool, sobel

BINS = ((64, 128), (128, 192), (192, 1000))


def thin_mask(t: torch.Tensor) -> torch.Tensor:
    a7 = pool(t, 7, "avg")
    dark = t < a7 - 24
    count = pool(dark.float(), 5, "avg") * 25
    return pool((dark & (count <= 8)).float(), 3, "max") > 0.5


def split(p: torch.Tensor, t: torch.Tensor, tn: torch.Tensor) -> dict:
    gt, gp, gn = sobel(t), sobel(p), sobel(tn)
    strong = gt > 64
    contrast = pool(t, 7, "max") - pool(t, 7, "min")
    thin = thin_mask(t)
    out = {}
    for lo, hi in BINS:
        for name, extra in (("thin", thin), ("thick", ~thin)):
            m = strong & (contrast >= lo) & (contrast < hi) & extra
            key = f"{lo}-{hi if hi < 1000 else 'up'}_{name}"
            if m.sum() < 100:
                out[key] = {"share": m.float().mean().item(), "ratio": float("nan"), "p10": float("nan"), "noise": float("nan")}
                continue
            r = (gp[m] / gt[m])[::3]
            out[key] = {"share": m.float().mean().item(), "ratio": (gp[m].mean() / gt[m].mean()).item(),
                        "p10": r.quantile(0.10).item(), "noise": ((gn[m] - gt[m]).abs().mean() / gt[m].mean()).item()}
    return out


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("model")
    parser.add_argument("--cpu", action="store_true")
    args = parser.parse_args()
    device = torch.device("cpu" if args.cpu else "cuda")
    if args.cpu:
        torch.set_num_threads(8)
    teacher = load_teacher(device)
    teacher_run = model_runner("teacher", device)
    run = model_runner(args.model, device)
    rng = np.random.default_rng(7)
    rows = []
    for entry in json.loads((REFERENCE / "pages.json").read_text()):
        key = entry["key"]
        page = load_page(REFERENCE / "pages" / f"{key}.png")
        s_rgb = lanczos_2x(page)
        noisy = np.clip(page.astype(np.int16) + rng.integers(-1, 2, page.shape), 0, 255).astype(np.uint8)
        t = torch.from_numpy(luma_u8(take_colour_from(cached_teacher(teacher, device, page, REFERENCE / "teacher" / f"{key}.png"), s_rgb))).float().to(device)
        tn = torch.from_numpy(luma_u8(take_colour_from(teacher_run(noisy), s_rgb))).float().to(device)
        p = torch.from_numpy(luma_u8(take_colour_from(run(page), s_rgb))).float().to(device)
        rows.append((key, entry["kind"], split(p, t, tn)))
        print(f"{key}: done", flush=True)
    print(f"\n{args.model}: strong-edge pixels by the teacher's local contrast and thinness; share of page, model Sobel over "
          f"teacher's (mean, p10), teacher's own change under +-1 level input noise (share of its gradient)")
    print(f"{'group':7} {'bin':16} {'share':>6} {'ratio':>6} {'p10':>6} {'noise':>6}")
    for g in ("owner", "colour", "grey"):
        sel = [r for k, kind, r in rows if (k == "owner-webtoon") == (g == "owner") and (g == "owner" or kind == g)]
        for key in sel[0]:
            print(f"{g:7} {key:16} {np.mean([r[key]['share'] for r in sel]):6.2%} {np.nanmean([r[key]['ratio'] for r in sel]):6.3f} "
                  f"{np.nanmean([r[key]['p10'] for r in sel]):6.3f} {np.nanmean([r[key]['noise'] for r in sel]):6.3f}")


if __name__ == "__main__":
    main()
