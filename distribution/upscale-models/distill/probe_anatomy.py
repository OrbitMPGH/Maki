"""
RESEARCH-PLAN-3 probe: where a model's residual to the teacher sits on the reference pages, and how it
renders thin lines. Everything is 2x luma after Maki's colour pass, as in measure.py. Per page:

- regions of the teacher's luma, in priority order: strong edges (Sobel over 64, dilated 1), faint edges
  (16 to 64, off strong ones), paper and ink, pure flat fill, fill beside creases, the rest; the share of
  pixels, the mean |r| and the share of the squared error in each;
- on strong edges, the error when the model may be a pixel off (min over the 3x3 around x of
  |p(x + d) - t(x)|): placement error drops under that, softness does not;
- thin dark lines: teacher pixels that are a local 5x5 minimum at least 24 levels under their 7x7 mean,
  with at most 8 such pixels in the 5x5 (hair strands, lettering strokes, thin lines on grey); the model's
  depth at the same spot (7x7 mean minus its 3x3 minimum) over the teacher's, and its width (5x5 pixels
  under half depth) over the teacher's;
- the p10, p25 and p50 of the model's Sobel over the teacher's on strong and on faint edge pixels.

    python probe_anatomy.py hyb-c48n8e-b64n4-m1-sharp02-last teacher-ens --cpu
"""

import argparse
import json

import numpy as np
import torch

from evaluate import luma_u8, take_colour_from
from measure import REFERENCE, WORK, cached_teacher, fill_mask, flat_mask, lanczos_2x, load_page, load_teacher, model_runner, pool, sobel

ORDER = ("strong", "faint", "paper", "pure", "crease", "rest")


def labels(t: torch.Tensor) -> torch.Tensor:
    g = sobel(t)
    strong = pool((g > 64).float(), 3, "max") > 0.5
    faint = (g > 16) & (g <= 64) & ~(pool((g > 64).float(), 7, "max") > 0.5)
    mean = pool(t, 15, "avg")
    paper = (mean >= 245) | (mean <= 10)
    fill = fill_mask(t)
    flat, _ = flat_mask(t, t)
    out = torch.full_like(t, len(ORDER) - 1, dtype=torch.long)
    for i, m in reversed(list(enumerate((strong, faint, paper, fill & flat, fill & ~flat)))):
        out[m] = i
    return out


def gather5(x: torch.Tensor, ys: torch.Tensor, xs: torch.Tensor) -> torch.Tensor:
    """The 5x5 neighbourhoods of the listed pixels, (25, n)."""
    out = []
    h, w = x.shape
    for dy in range(-2, 3):
        for dx in range(-2, 3):
            out.append(x[(ys + dy).clamp(0, h - 1), (xs + dx).clamp(0, w - 1)])
    return torch.stack(out)


def thin_lines(p: torch.Tensor, t: torch.Tensor) -> dict:
    a7 = pool(t, 7, "avg")
    dark = t < a7 - 24
    count = pool(dark.float(), 5, "avg") * 25
    cand = dark & (t <= pool(t, 5, "min") + 0.5) & (count <= 8)
    ys, xs = cand.nonzero(as_tuple=True)
    if len(ys) == 0:
        return {"thin_n": 0, "thin_depth": float("nan"), "thin_depth_p25": float("nan"), "thin_width": float("nan")}
    depth_t = (a7 - t)[ys, xs]
    depth_p = (a7 - pool(p, 3, "min"))[ys, xs]
    half = (a7[ys, xs] - depth_t / 2)[None]
    width_t = (gather5(t, ys, xs) < half).float().sum(0)
    width_p = (gather5(p, ys, xs) < half).float().sum(0)
    ratio = depth_p / depth_t.clamp_min(1)
    return {"thin_n": int(len(ys)), "thin_depth": (depth_p.sum() / depth_t.sum()).item(),
            "thin_depth_p25": ratio.quantile(0.25).item(), "thin_width": (width_p.sum() / width_t.sum().clamp_min(1)).item()}


def anatomy(p: torch.Tensor, t: torch.Tensor) -> dict:
    r = p - t
    lab = labels(t)
    sq = r * r
    total = sq.sum().clamp_min(1e-6)
    out = {}
    for i, name in enumerate(ORDER):
        m = lab == i
        out[f"{name}_share"] = m.float().mean().item()
        out[f"{name}_mae"] = r.abs()[m].mean().item() if m.any() else float("nan")
        out[f"{name}_err"] = (sq[m].sum() / total).item()
    strong = lab == 0
    shifted = torch.full_like(t, 1e9)
    for dy in (-1, 0, 1):
        for dx in (-1, 0, 1):
            shifted = torch.minimum(shifted, (torch.roll(p, (dy, dx), (0, 1)) - t).abs())
    out["strong_mae_shift"] = shifted[strong].mean().item()
    gt, gp = sobel(t), sobel(p)
    for name, m in (("strong", gt > 64), ("faint", (gt > 16) & (gt <= 64) & ~(pool((gt > 64).float(), 7, "max") > 0.5))):
        q = (gp[m] / gt[m])[::3] if m.any() else None
        for k in (10, 25, 50):
            out[f"{name}_ratio_p{k}"] = q.quantile(k / 100).item() if q is not None else float("nan")
    out |= thin_lines(p, t)
    return out


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("models", nargs="+")
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
    results = {m: {} for m in args.models}
    for entry in entries:
        key = entry["key"]
        page = load_page(REFERENCE / "pages" / f"{key}.png")
        s_rgb = lanczos_2x(page)
        t_rgb = take_colour_from(cached_teacher(teacher, device, page, REFERENCE / "teacher" / f"{key}.png"), s_rgb)
        t = torch.from_numpy(luma_u8(t_rgb)).float().to(device)
        for m, run in runners.items():
            p_rgb = s_rgb if run is None else take_colour_from(run(page), s_rgb)
            p = torch.from_numpy(luma_u8(p_rgb)).float().to(device)
            row = anatomy(p, t) | {"kind": entry["kind"]}
            results[m][key] = row
            print(f"{key:14} {m[:34]:34} err share strong {row['strong_err']:5.1%} faint {row['faint_err']:5.1%} "
                  f"pure {row['pure_err']:5.1%} crease {row['crease_err']:5.1%} | strong mae {row['strong_mae']:4.2f} "
                  f"shift {row['strong_mae_shift']:4.2f} | thin n {row['thin_n']:6} depth {row['thin_depth']:.3f} "
                  f"p25 {row['thin_depth_p25']:.3f} width {row['thin_width']:.3f} | ratio strong p10 {row['strong_ratio_p10']:.2f} "
                  f"p50 {row['strong_ratio_p50']:.2f} faint p10 {row['faint_ratio_p10']:.2f} p50 {row['faint_ratio_p50']:.2f}",
                  flush=True)
    out = WORK / "probe3"
    out.mkdir(exist_ok=True)
    keys = [k for k in next(iter(next(iter(results.values())).values())) if k != "kind"]
    print("\nmeans: owner page / colour pages / grey pages")
    for m, rows in results.items():
        groups = {"owner": [r for k, r in rows.items() if k == "owner-webtoon"],
                  "colour": [r for k, r in rows.items() if r["kind"] == "colour" and k != "owner-webtoon"],
                  "grey": [r for r in rows.values() if r["kind"] == "grey"]}
        summary = {g: {k: float(np.nanmean([r[k] for r in v])) for k in keys} for g, v in groups.items() if v}
        (out / f"anatomy-{m}.json").write_text(json.dumps({"pages": rows, "summary": summary}, indent=1))
        for g, s in summary.items():
            print(f"{m[:34]:34} {g:7} pixels strong {s['strong_share']:5.1%} faint {s['faint_share']:5.1%} paper {s['paper_share']:5.1%} "
                  f"pure {s['pure_share']:5.1%} crease {s['crease_share']:5.1%} rest {s['rest_share']:5.1%}")
            print(f"{'':34} {'':7} err    strong {s['strong_err']:5.1%} faint {s['faint_err']:5.1%} paper {s['paper_err']:5.1%} "
                  f"pure {s['pure_err']:5.1%} crease {s['crease_err']:5.1%} rest {s['rest_err']:5.1%}")
            print(f"{'':34} {'':7} mae    strong {s['strong_mae']:5.2f} faint {s['faint_mae']:5.2f} paper {s['paper_mae']:5.2f} "
                  f"pure {s['pure_mae']:5.2f} crease {s['crease_mae']:5.2f} rest {s['rest_mae']:5.2f}  strong shift-tolerant {s['strong_mae_shift']:.2f}")
            print(f"{'':34} {'':7} thin lines depth {s['thin_depth']:.3f} (p25 {s['thin_depth_p25']:.3f}) width {s['thin_width']:.3f}; "
                  f"sobel ratio strong p10/p25/p50 {s['strong_ratio_p10']:.2f}/{s['strong_ratio_p25']:.2f}/{s['strong_ratio_p50']:.2f} "
                  f"faint {s['faint_ratio_p10']:.2f}/{s['faint_ratio_p25']:.2f}/{s['faint_ratio_p50']:.2f}")


if __name__ == "__main__":
    main()
