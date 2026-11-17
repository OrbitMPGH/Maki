"""
Round 4, step 1: a measure for hard-edged patches in dark fills, the blotches the owner sees on an
OLED. Errors are scored in CIE L* (perceptual lightness), where a 2-level step near black weighs about
as much as a 10-level step in mid grey; every earlier measure worked in 0-255 levels.

All 2x luma after Maki's colour pass. P the model, T the teacher, S the Lanczos 2x source.

    python dark_steps.py maki-2x teacher lanczos
    python dark_steps.py maki-2x --pages 001 005 006

`maki-2x` is the shipped ONNX file, run with Maki's tiling. Outputs go to X:/MakiModel/probe4/.
"""

import argparse
import json
from pathlib import Path

import numpy as np
import onnxruntime as ort
import torch
import torch.nn.functional as F
from PIL import Image

from bakeoff import onnx_runner, tiled
from common import WORK
from evaluate import luma_u8, take_colour_from
from measure import PAD, TILE, cached_teacher, lanczos_2x, load_page, load_teacher, model_runner

PAGES = WORK / "reference" / "mage"
OUT = WORK / "probe4"
SHIPPED = WORK / "release" / "maki-2x.onnx"
DEFAULT_PAGES = ["001", "002", "004", "005", "006", "007", "010"]

EDGE_SIGMA = 1.5     # 2x px, blur before the source edge test
EDGE_LIMIT = 4.0     # L* per px: above this the source has an edge here
EDGE_GROW = 9        # 2x px, edges dilated so line halos are never counted
STEP_SIGMA = 0.8     # 2x px, blur before the step test: single-pixel noise is not a step
STEP_LIMIT = 1.0     # L* per px: a gradient this steep with no source edge is a hard step
DARK = 30.0          # L*: the dark range (about luma 70)


def lstar(y: torch.Tensor) -> torch.Tensor:
    y = y / 255.0
    lin = torch.where(y <= 0.04045, y / 12.92, ((y + 0.055) / 1.055) ** 2.4)
    return torch.where(lin > 0.008856, 116 * lin.clamp_min(1e-12) ** (1 / 3) - 16, 903.3 * lin)


def blur(x: torch.Tensor, sigma: float) -> torch.Tensor:
    r = max(1, int(3 * sigma + 0.5))
    k = torch.exp(-torch.arange(-r, r + 1, device=x.device).float() ** 2 / (2 * sigma * sigma))
    k = k / k.sum()
    x = x[None, None]
    x = F.conv2d(F.pad(x, (r, r, 0, 0), mode="replicate"), k.view(1, 1, 1, -1))
    x = F.conv2d(F.pad(x, (0, 0, r, r), mode="replicate"), k.view(1, 1, -1, 1))
    return x[0, 0]


def grad(x: torch.Tensor) -> torch.Tensor:
    gx = F.pad(x[:, 1:] - x[:, :-1], (0, 1))
    gy = F.pad(x[1:] - x[:-1], (0, 0, 0, 1))
    return torch.hypot(gx, gy)


def masks(s: torch.Tensor) -> tuple[torch.Tensor, torch.Tensor]:
    """Edge-free pixels of the source, and the dark ones among them."""
    edge = grad(blur(lstar(s), EDGE_SIGMA)) > EDGE_LIMIT
    edge = F.max_pool2d(edge.float()[None, None], EDGE_GROW, 1, EDGE_GROW // 2)[0, 0] > 0
    free = ~edge
    return free, free & (blur(lstar(s), 2.0) < DARK)


def steps(p: torch.Tensor) -> torch.Tensor:
    return grad(blur(lstar(p), STEP_SIGMA))


def lab(rgb: torch.Tensor) -> torch.Tensor:
    """HWC 0-255 sRGB to HWC CIE Lab (D65)."""
    c = rgb / 255.0
    c = torch.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)
    m = torch.tensor([[0.4124, 0.3576, 0.1805], [0.2126, 0.7152, 0.0722], [0.0193, 0.1192, 0.9505]],
                     device=rgb.device)
    xyz = c @ m.T / torch.tensor([0.95047, 1.0, 1.08883], device=rgb.device)
    f = torch.where(xyz > 0.008856, xyz.clamp_min(1e-12) ** (1 / 3), 7.787 * xyz + 16 / 116)
    return torch.stack([116 * f[..., 1] - 16, 500 * (f[..., 0] - f[..., 1]), 200 * (f[..., 1] - f[..., 2])], -1)


BLOCK = 16  # the source's 8 px JPEG grid at 2x


def across(x: torch.Tensor, m: torch.Tensor, offset: int) -> tuple[torch.Tensor, torch.Tensor]:
    """Steps between column c-1 and c for every c = offset mod BLOCK, both sides in the mask, both axes."""
    steps_, keep = [], []
    for a, mk in ((x, m), (x.transpose(0, 1), m.transpose(0, 1))):
        cols = torch.arange(offset or BLOCK, a.shape[1], BLOCK, device=a.device)
        d = a[:, cols] - a[:, cols - 1]
        steps_.append(d.reshape(-1, *d.shape[2:]))
        keep.append((mk[:, cols] & mk[:, cols - 1]).reshape(-1))
    return torch.cat(steps_), torch.cat(keep)


def blockiness(x: torch.Tensor, m: torch.Tensor) -> float:
    """Mean step size on the JPEG grid minus mid-block: how much the grid shows, in the units of x."""
    def size(d: torch.Tensor) -> torch.Tensor:
        return d.abs() if d.dim() == 1 else d.norm(dim=-1)
    on, keep_on = across(x, m, 0)
    mid, keep_mid = across(x, m, BLOCK // 2)
    if keep_on.sum() == 0 or keep_mid.sum() == 0:
        return float("nan")
    return (size(on[keep_on]).mean() - size(mid[keep_mid]).mean()).item()


def score(p: torch.Tensor, t: torch.Tensor, free: torch.Tensor, dark: torch.Tensor) -> dict:
    """p and t are HWC RGB floats. L* blocks, ab (colour) blocks, and smoothed L* deviation from the teacher."""
    lp, lt = lab(p), lab(t)
    out = {"dark_cover": dark.float().mean().item()}
    for name, m in (("dark", dark), ("free", free)):
        if m.sum() == 0:
            continue
        out[f"{name}_lblock"] = blockiness(lp[..., 0], m)
        out[f"{name}_cblock"] = blockiness(lp[..., 1:], m)
        out[f"{name}_dev"] = (blur(lp[..., 0], 3) - blur(lt[..., 0], 3)).abs()[m].mean().item()
    return out


def runner(name: str, device):
    if name == "maki-2x":
        session = ort.InferenceSession(str(SHIPPED), providers=["CPUExecutionProvider"])
        run = onnx_runner(session, half=False)
        return lambda page: tiled(run, page, 2, TILE, PAD)
    return model_runner(name, device)


def ycc(rgb: np.ndarray) -> tuple[torch.Tensor, torch.Tensor, torch.Tensor]:
    x = torch.from_numpy(rgb.astype(np.float32)).cuda() if torch.cuda.is_available() else torch.from_numpy(rgb.astype(np.float32))
    r, g, b = x[..., 0], x[..., 1], x[..., 2]
    y = 0.299 * r + 0.587 * g + 0.114 * b
    return y, 0.564 * (b - y), 0.713 * (r - y)


def to_rgb(y: torch.Tensor, cb: torch.Tensor, cr: torch.Tensor) -> np.ndarray:
    r = y + cr / 0.713
    b = y + cb / 0.564
    g = (y - 0.299 * r - 0.114 * b) / 0.587
    out = torch.stack([r, g, b], -1).clamp(0, 255) + 0.5
    return out.floor().byte().cpu().numpy()


def box(x: torch.Tensor, r: int) -> torch.Tensor:
    return F.avg_pool2d(x[None, None], 2 * r + 1, 1, r, count_include_pad=False)[0, 0]


def guided(guide: torch.Tensor, p: torch.Tensor, r: int, eps: float) -> torch.Tensor:
    mi, mp = box(guide, r), box(p, r)
    a = (box(guide * p, r) - mi * mp) / (box(guide * guide, r) - mi * mi + eps)
    b = mp - a * mi
    return box(a, r) * guide + box(b, r)


def bilateral(y: torch.Tensor, cb: torch.Tensor, cr: torch.Tensor, taps: int, step: int,
              s_space: float, s_luma: float, s_chroma: float) -> tuple[torch.Tensor, torch.Tensor]:
    """Averages source chroma over neighbours close in space, in model luma and in chroma itself, so the
    small steps of a JPEG block grid flatten while real colour edges (large chroma steps) and line edges
    (large luma steps) stay."""
    r = taps // 2
    num_cb = torch.zeros_like(cb)
    num_cr = torch.zeros_like(cr)
    den = torch.zeros_like(cb)
    pad = r * step
    yp, bp, rp = (F.pad(x[None, None], (pad, pad, pad, pad), mode="replicate")[0, 0] for x in (y, cb, cr))
    h, w = y.shape
    for dy in range(-r, r + 1):
        for dx in range(-r, r + 1):
            oy, ox = pad + dy * step, pad + dx * step
            ny, nb, nr = yp[oy:oy + h, ox:ox + w], bp[oy:oy + h, ox:ox + w], rp[oy:oy + h, ox:ox + w]
            d2 = ((dy * step) ** 2 + (dx * step) ** 2) / (2 * s_space ** 2)
            wgt = torch.exp(-d2 - (ny - y) ** 2 / (2 * s_luma ** 2)
                            - ((nb - cb) ** 2 + (nr - cr) ** 2) / (2 * s_chroma ** 2))
            num_cb += wgt * nb
            num_cr += wgt * nr
            den += wgt
    return num_cb / den, num_cr / den


SOURCE_1X: list = [None]
GUARD = (2.0, 5.0)  # source chroma magnitude (Cb/Cr levels, blurred): below the first stays the source's


def colour(mode: str, model: np.ndarray, s: np.ndarray) -> np.ndarray:
    """Ways to give the model's luma a colour. `served` is Maki's TakeColourFrom."""
    if mode == "served":
        return take_colour_from(model, s)
    if mode == "raw":
        return model
    y, mcb, mcr = ycc(model)
    _, scb, scr = ycc(s)
    if mode == "guided":
        return to_rgb(y, guided(y, scb, 8, 100.0), guided(y, scr, 8, 100.0))
    if mode == "bilat1x":
        # smooth at the source's own size, guided by the model luma shrunk 2x, then bilinear back up
        ym = F.avg_pool2d(y[None, None], 2)[0, 0]
        small = s[::2, ::2]  # placeholder replaced below
        src1 = SOURCE_1X[0]
        _, cb1, cr1 = ycc(src1)
        cb, cr = bilateral(ym, cb1, cr1, 5, 2, 4.0, 4.0, 3.0)
        up = lambda c: F.interpolate(c[None, None], scale_factor=2, mode="bilinear", align_corners=False)[0, 0]
        return to_rgb(y, up(cb), up(cr))
    if mode.startswith("bilat"):
        # bilat or bilat-<luma sigma>-<chroma sigma>
        parts = mode.split("-")
        s_luma, s_chroma = (float(parts[1]), float(parts[2])) if len(parts) >= 3 else (4.0, 3.0)
        taps, step = (int(parts[3]), int(parts[4])) if len(parts) == 5 else (9, 3)
        cb, cr = bilateral(y, scb, scr, taps, step, 8.0, s_luma, s_chroma)
        return to_rgb(y, cb, cr)
    if mode == "guard":
        mag = blur(torch.hypot(scb, scr), 4.0)
        w = ((mag - GUARD[0]) / (GUARD[1] - GUARD[0])).clamp(0, 1)
        return to_rgb(y, w * mcb + (1 - w) * scb, w * mcr + (1 - w) * scr)
    raise ValueError(mode)


def tint(p: np.ndarray, s: np.ndarray) -> float:
    """Mean colour (Lab ab length) the output adds where the source is grey."""
    ls, lp = (lab(torch.from_numpy(x.astype(np.float32))) for x in (s, p))
    grey = ls[..., 1:].norm(dim=-1) < 1.5
    if grey.sum() == 0:
        return float("nan")
    return (lp[..., 1:].norm(dim=-1)[grey].mean() - ls[..., 1:].norm(dim=-1)[grey].mean()).item()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("models", nargs="+", help="lanczos, teacher, maki-2x or a checkpoint tag, with :served "
                                                  "(default), :raw, :guided or :guard for the colour pass")
    parser.add_argument("--pages", nargs="*", default=DEFAULT_PAGES)
    parser.add_argument("--ref", action="store_true", help="also the reference set's colour pages (tint check)")
    parser.add_argument("--save", action="store_true", help="keep each output for crops")
    args = parser.parse_args()
    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    OUT.mkdir(parents=True, exist_ok=True)
    teacher = load_teacher(device)
    paths = {k: PAGES / f"{k}.jpg" for k in args.pages}
    if args.ref:
        for p in sorted((WORK / "reference" / "pages").glob("*.png")):
            if not p.stem.startswith("grey"):
                paths[p.stem] = p
    pages = {k: load_page(p) for k, p in paths.items()}
    sources = {k: lanczos_2x(page) for k, page in pages.items()}
    outputs: dict[str, dict[str, np.ndarray]] = {}

    def model_out(model: str, k: str) -> np.ndarray:
        if model == "lanczos":
            return sources[k]
        cache = outputs.setdefault(model, {})
        if k not in cache:
            path = OUT / f"{model}-raw-{k}.png"
            if path.exists():
                cache[k] = np.asarray(Image.open(path).convert("RGB"))
            else:
                run = (lambda page: teacher_2x_cached(page)) if model == "teacher" else runners.setdefault(model, runner(model, device))
                cache[k] = run(pages[k])
                Image.fromarray(cache[k]).save(path)
        return cache[k]

    runners: dict = {}
    from measure import teacher_2x
    teacher_2x_cached = lambda page: teacher_2x(teacher, device, page)

    results = {}
    for name in args.models:
        model, _, mode = name.partition(":")
        mode = mode or "served"
        rows = {}
        for k in pages:
            s = sources[k]
            t = take_colour_from(model_out("teacher", k), s)
            SOURCE_1X[0] = pages[k]
            p = colour(mode, model_out(model, k), s) if model != "lanczos" else s
            if args.save:
                Image.fromarray(p).save(OUT / f"{model}-{mode}-{k}.png")
            st = torch.from_numpy(luma_u8(s)).float().to(device)
            tt, pt = (torch.from_numpy(x.astype(np.float32)).to(device) for x in (t, p))
            free, dark = masks(st)
            rows[k] = score(pt, tt, free, dark) | {"tint": tint(p, s)}
            r = rows[k]
            print(f"{name:20s} {k:14s} dark {r['dark_cover']:4.0%}  L* block {r.get('dark_lblock', float('nan')):.3f}  "
                  f"colour block {r.get('dark_cblock', float('nan')):.3f}  tint {r['tint']:.3f}", flush=True)
        keys = sorted({key for r in rows.values() for key in r})
        results[name] = {"pages": rows, "mean": {key: float(np.nanmean([r.get(key, np.nan) for r in rows.values()]))
                                                 for key in keys}}
    path = OUT / "dark_steps.json"
    old = json.loads(path.read_text()) if path.exists() else {}
    path.write_text(json.dumps(old | results, indent=1))
    for name, r in results.items():
        m = r["mean"]
        print(f"MEAN {name:20s} dark: L* block {m.get('dark_lblock', 0):.3f}  colour block "
              f"{m.get('dark_cblock', 0):.3f}  | flat: L* block {m.get('free_lblock', 0):.3f}  colour block "
              f"{m.get('free_cblock', 0):.3f}  | tint {m.get('tint', 0):.3f}")


if __name__ == "__main__":
    main()
