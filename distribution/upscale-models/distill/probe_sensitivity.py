"""
RESEARCH-PLAN-3 probe: how much a model's own output changes when its input changes by one level of noise
(the JPEG floor), on faint lines against strong ones. A teacher whose faint lines move a lot under a change
nobody can see is a chaotic target there: a smaller student fits the conditional mean of that placement and
renders the line soft, and no width fixes it. Scored as measure.lines does, model(noisy) against
model(clean) on the clean output's own edge pixels, plus the PSNR between the two.

    python probe_sensitivity.py teacher hyb-c48n8e-b64n4-m1-sharp02-last --cpu
"""

import argparse
import json
import math

import numpy as np
import torch

from evaluate import luma_u8, take_colour_from
from measure import REFERENCE, cached_teacher, lanczos_2x, load_page, load_teacher, model_runner, pool, sobel


def compare(a: torch.Tensor, b: torch.Tensor) -> dict:
    """b is the clean output, a the noisy-input output."""
    gb, ga = sobel(b), sobel(a)
    strong = gb > 64
    faint = (gb > 16) & (gb <= 64) & ~(pool(strong.float(), 7, "max") > 0.5)
    ratio = (ga[faint] / gb[faint])[::3] if faint.any() else None
    mse = ((a - b) ** 2).mean().item()
    return {"psnr": 10 * math.log10(255 ** 2 / max(mse, 1e-10)),
            "faint": (ga[faint].mean() / gb[faint].mean()).item() if faint.any() else float("nan"),
            "faint_p25": ratio.quantile(0.25).item() if ratio is not None else float("nan"),
            "faint_p75": ratio.quantile(0.75).item() if ratio is not None else float("nan"),
            "strong": (ga[strong].mean() / gb[strong].mean()).item() if strong.any() else float("nan"),
            "faint_mae": (a - b).abs()[faint].mean().item() if faint.any() else float("nan"),
            "strong_mae": (a - b).abs()[strong].mean().item() if strong.any() else float("nan")}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("models", nargs="+", help="teacher, or checkpoint names")
    parser.add_argument("--levels", type=int, default=1, help="noise amplitude in levels (uniform integers)")
    parser.add_argument("--pages", nargs="*")
    parser.add_argument("--cpu", action="store_true")
    args = parser.parse_args()
    device = torch.device("cpu" if args.cpu else "cuda")
    if args.cpu:
        torch.set_num_threads(8)
    teacher = load_teacher(device)
    entries = json.loads((REFERENCE / "pages.json").read_text())
    if args.pages:
        entries = [e for e in entries if e["key"] in args.pages]
    runners = {m: model_runner(m, device) for m in args.models}
    rng = np.random.default_rng(7)
    rows = {m: [] for m in args.models}
    for entry in entries:
        key = entry["key"]
        page = load_page(REFERENCE / "pages" / f"{key}.png")
        noisy = np.clip(page.astype(np.int16) + rng.integers(-args.levels, args.levels + 1, page.shape), 0, 255).astype(np.uint8)
        s_rgb = lanczos_2x(page)
        for m, run in runners.items():
            clean = cached_teacher(teacher, device, page, REFERENCE / "teacher" / f"{key}.png") if m == "teacher" else run(page)
            noised = run(noisy)
            a = torch.from_numpy(luma_u8(take_colour_from(noised, s_rgb))).float().to(device)
            b = torch.from_numpy(luma_u8(take_colour_from(clean, s_rgb))).float().to(device)
            r = compare(a, b) | {"kind": entry["kind"], "key": key}
            rows[m].append(r)
            print(f"{key:14} {m[:34]:34} psnr {r['psnr']:6.2f}  faint {r['faint']:.3f} (p25 {r['faint_p25']:.2f} p75 {r['faint_p75']:.2f}) "
                  f"strong {r['strong']:.3f}  mae faint {r['faint_mae']:.2f} strong {r['strong_mae']:.2f}", flush=True)
    print(f"\n+-{args.levels} level input noise: model(noisy) against model(clean); owner / colour / grey means")
    for m, r in rows.items():
        groups = {"owner": [x for x in r if x["key"] == "owner-webtoon"],
                  "colour": [x for x in r if x["kind"] == "colour" and x["key"] != "owner-webtoon"],
                  "grey": [x for x in r if x["kind"] == "grey"]}
        for g, v in groups.items():
            if v:
                print(f"{m[:34]:34} {g:7} psnr {np.mean([x['psnr'] for x in v]):6.2f}  faint {np.mean([x['faint'] for x in v]):.3f} "
                      f"(p25 {np.mean([x['faint_p25'] for x in v]):.2f})  strong {np.mean([x['strong'] for x in v]):.3f}  "
                      f"mae faint {np.mean([x['faint_mae'] for x in v]):.2f} strong {np.mean([x['strong_mae'] for x in v]):.2f}")


if __name__ == "__main__":
    main()
