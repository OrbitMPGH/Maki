"""
The owner's 2026-11-06 screenshots: large grey panels "staticy" where Real-ESRGAN is solid, and thin lines
jagged where Real-ESRGAN's are straight. measure.py cannot see either: `blobs` opens away anything under
3 px and `texture` averages over 7 px, so pixel-scale grain passes both. This scores it directly.

- `grain`: mean |P - box3(P)| inside pure flat fill (measure.flat_mask), the pixel-scale noise the eye
  reads as static, beside the teacher's own. `grain_p99` the same at the 99th percentile.
- `line_hf`: mean |hp(P) - hp(T)| on pixels within 2 px of a strong edge, hp = x - box3(x): extra
  pixel-scale structure along lines (stair steps, wobble) against the teacher's.

    python grain.py c48n8-long c48n8e-b6-lp-last teacher --cpu
"""

import argparse

import numpy as np
import torch
import torch.nn.functional as F

from evaluate import luma_u8, take_colour_from
from measure import (PSEUDO, REFERENCE, WORK, cached_teacher, flat_mask, lanczos_2x, load_page, load_teacher,
                     model_runner, pool, sobel)
import json


def hp(x: torch.Tensor) -> torch.Tensor:
    return x - pool(x, 3, "avg")


def scores(p: torch.Tensor, t: torch.Tensor, flat: torch.Tensor) -> dict:
    g = hp(p).abs()[flat]
    edge = pool((sobel(t) > 64).float(), 5, "max") > 0.5
    return {"grain": g.mean().item() if g.numel() else float("nan"),
            "grain_p99": g[::3].quantile(0.99).item() if g.numel() else float("nan"),
            "line_hf": (hp(p) - hp(t)).abs()[edge].mean().item()}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("models", nargs="+")
    parser.add_argument("--cpu", action="store_true")
    parser.add_argument("--pages", nargs="*")
    args = parser.parse_args()
    device = torch.device("cpu" if args.cpu else "cuda")
    if args.cpu:
        torch.set_num_threads(6)
    entries = json.loads((REFERENCE / "pages.json").read_text())
    if args.pages:
        entries = [e for e in entries if e["key"] in args.pages]
    teacher = load_teacher(device)
    runners = {m: model_runner(m, device) for m in args.models}
    rows = {m: {} for m in args.models}
    for entry in entries:
        key = entry["key"]
        page = load_page(REFERENCE / "pages" / f"{key}.png")
        s_rgb = lanczos_2x(page)
        t = torch.from_numpy(luma_u8(take_colour_from(cached_teacher(teacher, device, page, REFERENCE / "teacher" / f"{key}.png"), s_rgb))).float()
        s = torch.from_numpy(luma_u8(s_rgb)).float()
        flat, _ = flat_mask(t, s)
        for m, run in runners.items():
            p_rgb = s_rgb if run is None else take_colour_from(run(page), s_rgb)
            rows[m][key] = scores(torch.from_numpy(luma_u8(p_rgb)).float(), t, flat) | {"kind": entry["kind"]}
        print(key, flush=True)
    print(f"\n{'model':26} {'group':7} {'grain':>6} {'p99':>5} {'line_hf':>8}   (grain: |P - box3 P| in pure flat fill; line_hf: extra pixel-scale structure along strong edges)")
    for m, r in rows.items():
        groups = {"owner": [v for k, v in r.items() if k == "owner-webtoon"],
                  "colour": [v for k, v in r.items() if v["kind"] == "colour" and k != "owner-webtoon"],
                  "grey": [v for v in r.values() if v["kind"] == "grey"]}
        for g, vals in groups.items():
            if vals:
                mean = {k: float(np.nanmean([v[k] for v in vals])) for k in ("grain", "grain_p99", "line_hf")}
                print(f"{m:26} {g:7} {mean['grain']:6.3f} {mean['grain_p99']:5.2f} {mean['line_hf']:8.3f}")


if __name__ == "__main__":
    main()
