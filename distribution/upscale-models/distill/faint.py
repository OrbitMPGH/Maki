"""
The owner's 2026-11-06 screenshot: strand lines inside dark hair "more diffused" than Real-ESRGAN's.
Those are faint edges (a Sobel step of 16 to 64 levels), which `committed` (strong edges only) never
scores and which `fill_mask` counts as fill, so the fill terms act on them. This scores line sharpness by
contrast band, on the reference set and on extra pages:

- `sharp`: mean Sobel magnitude of the model on the teacher's edge pixels over the teacher's own (1.0
  is as sharp as the teacher, under 1 is diffused), for faint (16-64) and strong (over 64) edges.
- `snap`: share of those pixels within a quarter of the local 5x5 range of its max or min (a relative
  `committed`, so a 30-level line is judged on its own contrast).

    python faint.py c48n8-long c48n8e-b6-lp-last teacher --extra C:/path/page.png
"""

import argparse
import json
from pathlib import Path

import numpy as np
import torch

from evaluate import luma_u8, take_colour_from
from measure import REFERENCE, cached_teacher, lanczos_2x, load_page, load_teacher, model_runner, pool, sobel, teacher_2x


def scores(p: torch.Tensor, t: torch.Tensor) -> dict:
    gt, gp = sobel(t), sobel(p)
    hi, lo = pool(p, 5, "max"), pool(p, 5, "min")
    span = (hi - lo).clamp_min(1)
    snap = ((hi - p) <= 0.25 * span) | ((p - lo) <= 0.25 * span)
    out = {}
    strong = gt > 64
    # Faint lines on their own: the flanks of a strong line also have a 16-64 gradient, and a model that
    # steepens strong lines narrows those flanks, which would read as a diffused faint line.
    faint = (gt > 16) & (gt <= 64) & ~(pool(strong.float(), 7, "max") > 0.5)
    for name, mask in (("faint", faint), ("strong", strong)):
        out[f"sharp_{name}"] = (gp[mask].mean() / gt[mask].mean()).item()
        out[f"snap_{name}"] = snap[mask].float().mean().item()
    return out


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("models", nargs="+")
    parser.add_argument("--extra", nargs="*", default=[], help="more pages (PNG paths) scored on their own")
    parser.add_argument("--cpu", action="store_true")
    args = parser.parse_args()
    device = torch.device("cpu" if args.cpu else "cuda")
    if args.cpu:
        torch.set_num_threads(6)
    teacher = load_teacher(device)
    runners = {m: model_runner(m, device) for m in args.models}
    pages = [(e["key"], e["kind"], load_page(REFERENCE / "pages" / f"{e['key']}.png"), REFERENCE / "teacher" / f"{e['key']}.png")
             for e in json.loads((REFERENCE / "pages.json").read_text())]
    pages += [(Path(p).stem[:12], "extra", load_page(Path(p)), None) for p in args.extra]
    rows = {m: [] for m in args.models}
    for key, kind, page, cache in pages:
        s_rgb = lanczos_2x(page)
        t_raw = cached_teacher(teacher, device, page, cache) if cache else teacher_2x(teacher, device, page)
        t = torch.from_numpy(luma_u8(take_colour_from(t_raw, s_rgb))).float().to(device)
        for m, run in runners.items():
            raw = t_raw if m == "teacher" else run(page)
            p = torch.from_numpy(luma_u8(take_colour_from(raw, s_rgb))).float().to(device)
            rows[m].append((key, kind, scores(p, t)))
    keys = ("sharp_faint", "snap_faint", "sharp_strong", "snap_strong")
    print(f"{'model':30} {'group':8} " + " ".join(f"{k:>12}" for k in keys))
    for m, r in rows.items():
        for group in ("owner", "colour", "grey", "extra"):
            vals = [s for key, kind, s in r if (key == "owner-webtoon") == (group == "owner") and
                    (group == "owner" or kind == group)]
            if vals:
                print(f"{m:30} {group:8} " + " ".join(f"{np.mean([v[k] for v in vals]):12.3f}" for k in keys))


if __name__ == "__main__":
    main()
