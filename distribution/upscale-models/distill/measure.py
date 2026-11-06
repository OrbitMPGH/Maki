"""
Step 0 of PLAN.md: how much of the teacher's flat-fill cleanup a model reproduces. Whole-page PSNR
favours edges and the owner's complaints are all about flat areas, so this scores flat fills on
their own, on a fixed reference set of whole pages, and writes contact sheets of the worst windows.

Everything is 2x luma after Maki's colour pass. T is the teacher (Real-ESRGAN, 4x per 96 px tile,
halved, Maki's tiling), S the Lanczos 2x source, P the model, R = P - T.

    python measure.py --build-reference          # pick the reference pages once
    python measure.py lanczos c48n8-served c48n8-colour

Outputs go to X:/MakiModel/measure/, outside the repo: the pages are the owner's
library and the sheets stay on this machine.
"""

import argparse
import hashlib
import json
import math
import shutil
from pathlib import Path

import numpy as np
import torch
import torch.nn.functional as F
from PIL import Image

from bakeoff import tiled, torch_runner
from common import WORK, collapse, halve, load_anime6b, load_student, load_teacher, split_name, student
from evaluate import luma_u8, take_colour_from

REFERENCE = WORK / "reference"
OUT = WORK / "measure"
USER_PAGE = Path("C:/Users/Orbit/.t3/userdata/attachments/"
                 "ea658595-c574-418c-bc37-457dc80eec2d-3465606e-2bca-4a3f-bc4f-123c711e49ad.png")
GREY_PAGES = ["005", "009", "016", "019", "039"]  # bake-off page 05 is val_pages/005
TILE, PAD = 96, 12
WINDOW = 128


def pool(x: torch.Tensor, size: int, kind: str) -> torch.Tensor:
    """Same-size `size` x `size` pool with reflect padding on an HW tensor."""
    r = size // 2
    padded = F.pad(x[None, None], (r, r, r, r), mode="reflect")
    if kind == "max":
        return F.max_pool2d(padded, size, 1)[0, 0]
    if kind == "min":
        return -F.max_pool2d(-padded, size, 1)[0, 0]
    return F.avg_pool2d(padded, size, 1)[0, 0]


def flat_mask(t: torch.Tensor, s: torch.Tensor) -> tuple[torch.Tensor, torch.Tensor]:
    """Flat fills on teacher luma: no line within 7 px (a range, so a thin line cannot hide in a
    std), not paper white or ink black, eroded 9x9. `damaged` adds that the teacher changed
    something there."""
    flat = (pool(t, 15, "max") - pool(t, 15, "min") <= 10)
    mean = pool(t, 15, "avg")
    flat &= (mean > 10) & (mean < 245)
    flat = pool(flat.float(), 9, "min") > 0.5
    damaged = flat & (pool((s - t).abs(), 15, "avg") >= 0.75)
    return flat, damaged


def fill_mask(t: torch.Tensor) -> torch.Tensor:
    """Everything but ink: not paper white or ink black, and more than 5 px from a line with a
    step of 16 levels or more. Faint creases and the fill beside them stay in, which the flat mask
    drops, and that is where the blotches the owner pointed at sit."""
    mean = pool(t, 15, "avg")
    ink = pool((sobel(t) > 64).float(), 11, "max") > 0.5
    return (mean > 10) & (mean < 245) & ~ink


def band(r: torch.Tensor) -> torch.Tensor:
    """Residual structure 3 to 15 px across: a blotch, not single-pixel noise and not a whole-area
    offset of a level, which nobody can see."""
    return pool(r, 7, "avg") - pool(r, 31, "avg")


def blocks16(r: torch.Tensor, mask: torch.Tensor) -> tuple[torch.Tensor, torch.Tensor]:
    """Per 16 px block aligned to the page origin: mean residual over the block's masked pixels,
    and whether at least half the block is masked."""
    h, w = (r.shape[0] // 16) * 16, (r.shape[1] // 16) * 16
    m = mask[:h, :w].float()
    total = F.avg_pool2d((r[:h, :w] * m)[None, None], 16)[0, 0]
    share = F.avg_pool2d(m[None, None], 16)[0, 0]
    return total / share.clamp_min(1e-6), share >= 0.5


def sobel(t: torch.Tensor) -> torch.Tensor:
    kx = torch.tensor([[-1, 0, 1], [-2, 0, 2], [-1, 0, 1]], dtype=t.dtype, device=t.device)
    x = F.pad(t[None, None], (1, 1, 1, 1), mode="reflect")
    gx = F.conv2d(x, kx[None, None])[0, 0]
    gy = F.conv2d(x, kx.T[None, None])[0, 0]
    return (gx * gx + gy * gy).sqrt()


SPOT = 1.0  # levels of |band| that count as a visible spot inside a pure fill


def texture(p: torch.Tensor, t: torch.Tensor, s: torch.Tensor, pure: torch.Tensor, crease: torch.Tensor) -> dict:
    """The output's own band energy where the drawing is meant to be smooth (`pure`), beside the teacher's
    and the source's: mean, p99 and the share of pixels over SPOT, since a blotch is a few local spots.
    `texture_crease` is the same mean beside faint creases and hair, where smoothing would be a loss."""
    nan = float("nan")
    out = {}
    bands = {"": band(p).abs(), "_teacher": band(t).abs(), "_source": band(s).abs()}
    for suffix, b in bands.items():
        inside = b[pure] if pure.any() else None
        out["texture" + suffix] = inside.mean().item() if inside is not None else nan
        out["texture_p99" + suffix] = inside[::3].quantile(0.99).item() if inside is not None else nan
        out["spots" + suffix] = (inside > SPOT).float().mean().item() if inside is not None else nan
        out["texture_crease" + suffix] = b[crease].mean().item() if crease.any() else nan
    gap = out["texture_source"] - out["texture_teacher"]
    # Only where the teacher is clearly smoother than the source is there a gap to close.
    out["smoothing"] = (out["texture_source"] - out["texture"]) / gap if gap > 0.03 else nan
    return out


def committed(x: torch.Tensor, strong: torch.Tensor) -> float:
    """Share of strong-edge pixels within 8 levels of their 5x5 neighbourhood's max or min. A crisp edge
    snaps each pixel to one side; a hedged one leaves pixels in between. Mean gradient cannot see this."""
    near = ((pool(x, 5, "max") - x) <= 8) | ((x - pool(x, 5, "min")) <= 8)
    return near[strong].float().mean().item() if strong.any() else float("nan")


def blobs(b: torch.Tensor, mask: torch.Tensor) -> float:
    """Share of `mask` covered by spots over SPOT that survive a 3x3 opening: coherent marks, not pixel noise."""
    spot = (b > SPOT) & mask
    opened = pool(pool(spot.float(), 3, "min"), 3, "max") > 0.5
    return (opened & mask).float().sum().item() / max(mask.float().sum().item(), 1.0)


def grain(p: torch.Tensor, t: torch.Tensor, flat: torch.Tensor) -> dict:
    """Pixel-scale static in pure flat fill (|x - box3(x)|), the model's and the teacher's, and the extra
    pixel-scale structure along strong edges against the teacher (stair steps). `blobs` opens away
    anything under 3 px and `texture` averages over 7 px, so neither sees what the owner called static."""
    hp = lambda x: x - pool(x, 3, "avg")  # noqa: E731
    nan = float("nan")
    edge = pool((sobel(t) > 64).float(), 5, "max") > 0.5
    gp, gt = hp(p).abs()[flat], hp(t).abs()[flat]
    return {"grain": gp.mean().item() if gp.numel() else nan,
            "grain_teacher": gt.mean().item() if gt.numel() else nan,
            "grain_p99": gp[::3].quantile(0.99).item() if gp.numel() else nan,
            "line_hf": (hp(p) - hp(t)).abs()[edge].mean().item() if edge.any() else nan}


def lines(p: torch.Tensor, t: torch.Tensor) -> dict:
    """Line sharpness by contrast: the model's mean Sobel magnitude on the teacher's edge pixels over the
    teacher's own (1.0 is as sharp, under 1 diffused), for faint (16 to 64) and strong (over 64) edges.
    The owner saw strand lines in dark hair diffused (2026-11-06), which `committed` (strong only) missed."""
    gt, gp = sobel(t), sobel(p)
    out = {}
    strong = gt > 64
    # Faint lines on their own, not the flanks of strong ones (which a steeper strong line narrows).
    faint = (gt > 16) & (gt <= 64) & ~(pool(strong.float(), 7, "max") > 0.5)
    for name, mask in (("faint", faint), ("strong", strong)):
        out[f"sharp_{name}"] = (gp[mask].mean() / gt[mask].mean()).item() if mask.any() else float("nan")
    return out


def marks(p: torch.Tensor, t: torch.Tensor) -> dict:
    """Marks anywhere on the page, beside lines included, which `blobs` (fill only) cannot see: the owner's
    dark smudge by a hair-strand tip (2026-11-06). |model - teacher| over 20 (and 12) levels, opened 3x3 so a
    line drawn half a pixel off drops out; share of page pixels, and the dark share apart."""
    d = p - t
    opened = lambda m: pool(pool(m.float(), 3, "min"), 3, "max") > 0.5  # noqa: E731
    m20 = opened(d.abs() >= 20)
    return {"marks": m20.float().mean().item(), "marks_dark": (m20 & (d < 0)).float().mean().item(),
            "marks12": opened(d.abs() >= 12).float().mean().item()}


def tail(p: torch.Tensor, t: torch.Tensor, fill: torch.Tensor) -> dict:
    """Where long still lost by eye (Fable, 2026-11-05): hedged pixels on strong edges, and marks in fills
    beside lines, which `pure` drops. Averages match the teacher; these tails do not."""
    strong = sobel(t) > 64
    bp, bt = band(p).abs(), band(t).abs()
    excess = (bp - bt)[fill] if fill.any() else None
    nan = float("nan")
    return {
        "committed": committed(p, strong),
        "committed_teacher": committed(t, strong),
        "fill_excess": excess.mean().item() if excess is not None else nan,
        "fill_excess_p95": excess[::3].quantile(0.95).item() if excess is not None else nan,
        "fill_excess_p99": excess[::3].quantile(0.99).item() if excess is not None else nan,
        "blobs": blobs(bp, fill & (bt <= SPOT)),
        "blobs_teacher": blobs(bt, fill),
    }


def score(p: torch.Tensor, t: torch.Tensor, s: torch.Tensor, damaged: torch.Tensor, edges: torch.Tensor,
          fill: torch.Tensor, flat: torch.Tensor) -> dict:
    r = p - t
    band_r, band_s = band(r), band(s - t)
    blotch = band_r.abs()[fill]
    blotch_source = band_s.abs()[fill]
    pure = fill & flat
    crease = fill & ~flat
    # How much of the source's own blotch structure the model still carries (1 for Lanczos, 0 for the
    # teacher). Removal can be low because the model adds differences of its own; this cannot.
    carried = (band_r[fill] * band_s[fill]).sum() / (band_s[fill] ** 2).sum().clamp_min(1e-6)
    values, kept = blocks16(r, damaged)
    source, _ = blocks16(s - t, damaged)
    pooled = values[kept].abs()
    baseline = source[kept].abs()
    mse = (r * r).mean().item()
    out = {
        "coverage": damaged.float().mean().item(),
        "blocks": int(kept.sum()),
        "pooled16_mean": pooled.mean().item() if pooled.numel() else float("nan"),
        "pooled16_p95": pooled.quantile(0.95).item() if pooled.numel() else float("nan"),
        "removal": (1 - pooled.mean() / baseline.mean()).item() if pooled.numel() else float("nan"),
        "edge_mae": r.abs()[edges].mean().item() if edges.any() else float("nan"),
        "psnr": 10 * math.log10(255 ** 2 / max(mse, 1e-10)),
        "fill_coverage": fill.float().mean().item(),
        "blotch_mean": blotch.mean().item(),
        "blotch_p99": blotch[::3].quantile(0.99).item(),
        "blotch_removal": (1 - blotch.mean() / blotch_source.mean()).item(),
        "blotch_carried": carried.item(),
        # The output's own blotchiness where the drawing is meant to be smooth, against the teacher's and
        # the source's. The teacher's fill texture changes with orientation (mirrored, it scores only 64-75%
        # blotch removal against itself), so matching its residual is the wrong question; being as smooth
        # as it is the right one. `smoothing` is the share of the Lanczos-to-teacher gap closed.
        **texture(p, t, s, pure, crease),
        **tail(p, t, fill),
        **grain(p, t, flat),
        **lines(p, t),
        **marks(p, t),
        "blotch_pure": band_r.abs()[pure].mean().item() if pure.any() else float("nan"),
        "blotch_pure_removal": (1 - band_r.abs()[pure].mean() / band_s.abs()[pure].mean()).item() if pure.any() else float("nan"),
        "blotch_crease": band_r.abs()[crease].mean().item() if crease.any() else float("nan"),
        "blotch_crease_removal": (1 - band_r.abs()[crease].mean() / band_s.abs()[crease].mean()).item() if crease.any() else float("nan"),
    }
    return out


def worst_windows(excess: torch.Tensor, mask: torch.Tensor, count: int = 3) -> list[tuple[int, int]]:
    """The 128 px windows (on the 16 px grid) with the largest mean `excess` over their masked blocks,
    at most `count`, not overlapping. Called with how much more band texture the model has than the
    teacher, inside pure fill: the student textured where the teacher is smooth."""
    values, kept = blocks16(excess, mask)
    a = (values * kept).float()
    n = WINDOW // 16
    if a.shape[0] < n or a.shape[1] < n:
        return []
    sums = F.avg_pool2d(a[None, None], n, 1)[0, 0]
    counts = F.avg_pool2d(kept.float()[None, None], n, 1)[0, 0]
    window = (sums / counts.clamp_min(1e-6)) * (counts >= 0.25)
    picks = []
    for _ in range(count):
        flat_index = int(window.argmax())
        by, bx = divmod(flat_index, window.shape[1])
        if window[by, bx] <= 0:
            break
        picks.append((by * 16, bx * 16))
        window[max(0, by - n + 1):by + n, max(0, bx - n + 1):bx + n] = 0
    return picks


def contact_sheet(picks, source_rgb, model_rgb, teacher_rgb, r: np.ndarray) -> Image.Image | None:
    if not picks:
        return None
    zoom = 3
    cell = WINDOW * zoom
    sheet = Image.new("RGB", (cell * 4 + 12, cell * len(picks) + 4 * (len(picks) - 1)), (40, 40, 40))
    residual = np.clip(128 + 8 * r, 0, 255).astype(np.uint8)
    for row, (y, x) in enumerate(picks):
        crops = [img[y:y + WINDOW, x:x + WINDOW] for img in (source_rgb, model_rgb, teacher_rgb)]
        crops.append(np.repeat(residual[y:y + WINDOW, x:x + WINDOW, None], 3, axis=2))
        for col, crop in enumerate(crops):
            tile = Image.fromarray(np.ascontiguousarray(crop)).resize((cell, cell), Image.NEAREST)
            sheet.paste(tile, (col * (cell + 4), row * (cell + 4)))
    return sheet


def overlay(teacher_rgb: np.ndarray, fill: np.ndarray, damaged: np.ndarray) -> Image.Image:
    """Teacher output, the fill mask tinted blue, flat_damaged tinted orange: the sanity check."""
    img = teacher_rgb.astype(np.float32)
    img[fill & ~damaged] = img[fill & ~damaged] * 0.5 + np.array([40, 90, 230]) * 0.5
    img[damaged] = img[damaged] * 0.4 + np.array([255, 130, 0]) * 0.6
    return Image.fromarray(img.astype(np.uint8))


def teacher_2x(teacher, device, page: np.ndarray) -> np.ndarray:
    run4 = torch_runner(teacher, device, torch.float32)
    return tiled(lambda context: halve(run4(context)), page, 2, TILE, PAD)


def cached_teacher(teacher, device, page: np.ndarray, path: Path) -> np.ndarray:
    if path.exists():
        return np.asarray(Image.open(path).convert("RGB"))
    out = teacher_2x(teacher, device, page)
    Image.fromarray(out).save(path)
    return out


def load_page(path: Path) -> np.ndarray:
    return np.asarray(Image.open(path).convert("RGB"))


def lanczos_2x(page: np.ndarray) -> np.ndarray:
    h, w = page.shape[:2]
    return np.asarray(Image.fromarray(page).resize((w * 2, h * 2), Image.LANCZOS))


def build_reference(device):
    """The owner's page, five colour validation pages with flat_damaged over 3%, five B&W ones."""
    teacher = load_teacher(device)
    pages_dir = REFERENCE / "pages"
    pages_dir.mkdir(parents=True, exist_ok=True)
    data = WORK / "data"
    picked = [("owner-webtoon", USER_PAGE, "colour")]
    seen = set()
    candidates = sorted((data / "val_pages_col2").glob("*.png")) + sorted((data / "val_pages_col").glob("*.png"))
    colour = []
    for path in candidates:
        digest = hashlib.sha1(path.read_bytes()).hexdigest()
        if digest in seen:
            continue
        seen.add(digest)
        page = load_page(path)
        s = lanczos_2x(page)
        t = take_colour_from(teacher_2x(teacher, device, page), s)
        _, damaged = flat_mask(*(torch.from_numpy(luma_u8(x)).float().to(device) for x in (t, s)))
        cover = damaged.float().mean().item()
        print(f"{path.parent.name}/{path.name}: flat_damaged {cover:.1%}", flush=True)
        if cover > 0.03:
            colour.append((f"colour-{len(colour) + 1}", path, "colour"))
            if len(colour) == 5:
                break
    picked += colour
    picked += [(f"grey-{i + 1}", data / "val_pages" / f"{name}.png", "grey") for i, name in enumerate(GREY_PAGES)]
    entries = []
    for key, path, kind in picked:
        shutil.copyfile(path, pages_dir / f"{key}.png")
        entries.append({"key": key, "kind": kind, "from": str(path)})
    (REFERENCE / "pages.json").write_text(json.dumps(entries, indent=1))
    verdicts = REFERENCE / "verdicts.json"
    if not verdicts.exists():
        verdicts.write_text(json.dumps({"owner-webtoon": [
            {"model": "c48n8-served", "verdict": "way worse than Real-ESRGAN"},
            {"model": "c48n8-colour", "verdict": "doesn't seem all that better; faint JPEG blotches in flat fills"},
        ]}, indent=1))
    print(f"{len(entries)} reference pages in {pages_dir}")


PSEUDO = ("lanczos", "teacher", "teacher-ens", "anime6b")


def teacher_ensemble(teacher, device, page: np.ndarray) -> np.ndarray:
    """The teacher on all 8 flips and rotations of the page, each turned back, averaged and rounded."""
    total = np.zeros((page.shape[0] * 2, page.shape[1] * 2, 3), np.float64)
    for k in range(8):
        oriented = page[:, ::-1] if k & 4 else page
        oriented = np.ascontiguousarray(np.rot90(oriented, k & 3))
        out = np.rot90(teacher_2x(teacher, device, oriented), -(k & 3))
        total += out[:, ::-1] if k & 4 else out
    return np.clip(np.floor(total / 8 + 0.5), 0, 255).astype(np.uint8)


def model_runner(name: str, device):
    """None for Lanczos. `teacher` and `teacher-ens` are reference rows, Real-ESRGAN itself and its
    8-orientation average, scored by the same code as the students."""
    if name == "lanczos":
        return None
    if name in ("teacher", "teacher-ens"):
        teacher = load_teacher(device)
        if name == "teacher":
            return lambda page: teacher_2x(teacher, device, page)
        return lambda page: teacher_ensemble(teacher, device, page)
    if name == "anime6b":
        # A candidate teacher, not a serving model: wide context so its deep receptive field never sees a seam.
        run4 = torch_runner(load_anime6b(device), device, torch.float32)
        return lambda page: tiled(lambda context: halve(run4(context)), page, 2, 192, 48)
    model = student(split_name(name)[0])
    load_student(model, torch.load(WORK / f"student-{name}.pt", map_location="cpu", weights_only=True))
    # Scored as Maki would run it: ECB branches folded into plain 3x3s.
    run = torch_runner(collapse(model).eval().to(device), device, torch.float32)
    return lambda page: tiled(run, page, 2, TILE, PAD)


PROBE_BORDER = 64  # 2x px left out at the page edge, where a shifted copy runs into reflected context


def shifted_luma(run, page: np.ndarray, k: int) -> np.ndarray:
    """Maki-tiled output for the page moved k low-res px right and down (reflected in), moved back."""
    padded = np.pad(page, ((k, 0), (k, 0), (0, 0)), mode="reflect")
    return luma_u8(tiled(run, padded, 2, TILE, PAD)[2 * k:, 2 * k:]).astype(np.float32)


def probes(run, page: np.ndarray, base: np.ndarray, fill: np.ndarray) -> dict:
    """`phase`: the page moved by one low-res px puts every detail on the other sub-pixel phase, which a
    pixel-unshuffle model handles with different weights; a 2 px pattern shows as a difference in fills.
    `seam`: moved by half a tile (an even shift, so the same phase), only the tile seams move."""
    b = PROBE_BORDER
    inner = (slice(b, -b), slice(b, -b))
    one = np.abs(shifted_luma(run, page, 1) - base)[inner][fill[inner]]
    half = shifted_luma(run, page, TILE // 2) - base
    mse = float((half[inner] ** 2).mean())
    return {"phase": float(one.mean()) if one.size else float("nan"),
            "phase_p99": float(np.quantile(one, 0.99)) if one.size else float("nan"),
            "seam": 10 * math.log10(255 ** 2 / max(mse, 1e-10))}


def summarise(rows: list[dict]) -> dict:
    keys = ("pooled16_mean", "pooled16_p95", "removal", "edge_mae", "psnr", "blotch_mean", "blotch_p99", "blotch_removal",
            "blotch_carried", "blotch_pure_removal", "blotch_crease_removal", "texture", "texture_teacher", "smoothing",
            "texture_source", "texture_p99", "texture_p99_teacher", "spots", "spots_teacher", "texture_crease",
            "texture_crease_teacher", "committed", "committed_teacher", "fill_excess", "fill_excess_p95",
            "fill_excess_p99", "blobs", "blobs_teacher", "phase", "phase_p99", "seam", "grain", "grain_teacher",
            "grain_p99", "line_hf", "sharp_faint", "sharp_strong", "marks", "marks_dark", "marks12")
    return {k: float(np.nanmean([r.get(k, float("nan")) for r in rows])) for k in keys} | {"pages": len(rows)}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("models", nargs="*", help="lanczos, or a checkpoint tag such as c48n8-colour")
    parser.add_argument("--build-reference", action="store_true")
    parser.add_argument("--no-sheets", action="store_true")
    parser.add_argument("--cpu", action="store_true", help="score on the CPU while a training run holds the GPU")
    parser.add_argument("--probes", action="store_true", help="also run the phase and seam probes (RESEARCH-PLAN.md)")
    parser.add_argument("--pages", nargs="*", help="only these reference page keys, e.g. owner-webtoon (a quick check-in)")
    args = parser.parse_args()
    device = torch.device("cpu" if args.cpu else "cuda")
    if args.cpu:
        torch.set_num_threads(8)
    if args.build_reference:
        build_reference(device)
        if not args.models:
            return

    entries = json.loads((REFERENCE / "pages.json").read_text())
    if args.pages:
        entries = [e for e in entries if e["key"] in args.pages]
    teacher = load_teacher(device)
    cache = REFERENCE / "teacher"
    cache.mkdir(exist_ok=True)
    missing = [m for m in args.models if m not in PSEUDO and not (WORK / f"student-{m}.pt").exists()]
    if missing:
        print(f"no checkpoint for {', '.join(missing)}, skipped", flush=True)
    args.models = [m for m in args.models if m not in missing]
    runners = {name: model_runner(name, device) for name in args.models}
    results = {name: {} for name in args.models}
    for entry in entries:
        key = entry["key"]
        page = load_page(REFERENCE / "pages" / f"{key}.png")
        s_rgb = lanczos_2x(page)
        t_rgb = take_colour_from(cached_teacher(teacher, device, page, cache / f"{key}.png"), s_rgb)
        t = torch.from_numpy(luma_u8(t_rgb)).float().to(device)
        s = torch.from_numpy(luma_u8(s_rgb)).float().to(device)
        flat, damaged = flat_mask(t, s)
        edges = pool((sobel(t) > 24).float(), 3, "max") > 0.5
        fill = fill_mask(t)
        overlay_path = OUT / "overlay" / f"{key}.png"
        if not overlay_path.exists():
            overlay_path.parent.mkdir(parents=True, exist_ok=True)
            overlay(t_rgb, fill.cpu().numpy(), damaged.cpu().numpy()).save(overlay_path)
        for name, run in runners.items():
            p_rgb = s_rgb if run is None else take_colour_from(run(page), s_rgb)
            p = torch.from_numpy(luma_u8(p_rgb)).float().to(device)
            row = score(p, t, s, damaged, edges, fill, flat) | {"kind": entry["kind"]}
            if args.probes and run is not None:
                base = luma_u8(run(page) if name not in ("teacher", "teacher-ens") else
                               cached_teacher(teacher, device, page, cache / f"{key}.png")).astype(np.float32)
                row |= probes(run, page, base, fill.cpu().numpy())
            results[name][key] = row
            print(f"{key:14} {name:16} cover {row['coverage']:5.1%}  pooled16 {row['pooled16_mean']:5.2f} "
                  f"p95 {row['pooled16_p95']:5.2f}  removal {row['removal']:6.1%}  edge {row['edge_mae']:5.2f}  "
                  f"psnr {row['psnr']:5.2f}  blotch {row['blotch_mean']:5.3f} p99 {row['blotch_p99']:5.2f} "
                  f"removal {row['blotch_removal']:6.1%} carried {row['blotch_carried']:6.1%}", flush=True)
            if not args.no_sheets and run is not None:
                r = (p - t).cpu().numpy()
                sheet = contact_sheet(worst_windows((band(p).abs() - band(t).abs()).clamp_min(0), fill & flat), s_rgb, p_rgb, t_rgb, r)
                if sheet:
                    (OUT / name).mkdir(parents=True, exist_ok=True)
                    sheet.save(OUT / name / f"{key}.png")

    print("\nsummary (owner's page; mean over colour pages with coverage over 3%; mean over B&W pages)")
    print(f"{'model':16} {'set':14} {'pooled16':>8} {'p95':>6} {'removal':>8} {'edge':>6} {'psnr':>6} "
          f"{'blotch':>7} {'p99':>5} {'removal':>8} {'carried':>8} {'pure':>7} {'crease':>7} {'texture':>8} {'teacher':>8} {'smooth':>7}")
    summary = {}
    for name, rows in results.items():
        groups = {
            "owner": [rows["owner-webtoon"]] if "owner-webtoon" in rows else [],
            "colour": [r for k, r in rows.items() if r["kind"] == "colour" and k != "owner-webtoon" and r["coverage"] > 0.03],
            "grey": [r for r in rows.values() if r["kind"] == "grey"],
        }
        summary[name] = {g: summarise(v) for g, v in groups.items() if v}
        for g, v in summary[name].items():
            print(f"{name:16} {g + ' (' + str(v['pages']) + ')':14} {v['pooled16_mean']:8.2f} {v['pooled16_p95']:6.2f} "
                  f"{v['removal']:8.1%} {v['edge_mae']:6.2f} {v['psnr']:6.2f} {v['blotch_mean']:7.3f} {v['blotch_p99']:5.2f} "
                  f"{v['blotch_removal']:8.1%} {v['blotch_carried']:8.1%} {v['blotch_pure_removal']:7.1%} "
                  f"{v['blotch_crease_removal']:7.1%} {v['texture']:8.3f} {v['texture_teacher']:8.3f} {v['smoothing']:7.1%}")
        (OUT / f"{name}.json").write_text(json.dumps({"pages": rows, "summary": summary[name]}, indent=1))

    print("\ntails: committed share on strong edges (teacher's beside it, higher is crisper); fill excess band "
          "over the teacher's (mean, p95, p99); coherent blobs where the teacher has none (per mille of fill)")
    print(f"{'model':16} {'set':14} {'commit':>7} {'teacher':>7} {'excess':>7} {'p95':>6} {'p99':>6} {'blobs':>7} {'teacher':>7} {'smooth':>7} {'grain':>6} {'teacher':>7} {'line_hf':>7} {'faint':>6} {'strong':>6} {'marks':>6} {'m12':>6}")
    for name in results:
        for g, v in summary[name].items():
            print(f"{name:16} {g + ' (' + str(v['pages']) + ')':14} {v['committed']:7.3f} {v['committed_teacher']:7.3f} "
                  f"{v['fill_excess']:7.3f} {v['fill_excess_p95']:6.2f} {v['fill_excess_p99']:6.2f} "
                  f"{v['blobs'] * 1000:7.2f} {v['blobs_teacher'] * 1000:7.2f} {v['smoothing']:7.1%} "
                  f"{v['grain']:6.3f} {v['grain_teacher']:7.3f} {v['line_hf']:7.3f} {v['sharp_faint']:6.3f} {v['sharp_strong']:6.3f} "
                  f"{v['marks'] * 1000:6.3f} {v['marks12'] * 1000:6.3f}")

    if args.probes:
        print("\nprobes: luma change in fills when the page moves one low-res px (mean, p99; a 2 px pattern "
              "shows here), and PSNR between Maki's tiling and the same tiling half a tile over (seams)")
        print(f"{'model':16} {'set':14} {'phase':>7} {'p99':>6} {'seam dB':>8}")
        for name in results:
            for g, v in summary[name].items():
                print(f"{name:16} {g + ' (' + str(v['pages']) + ')':14} {v['phase']:7.3f} {v['phase_p99']:6.2f} {v['seam']:8.2f}")


if __name__ == "__main__":
    main()
