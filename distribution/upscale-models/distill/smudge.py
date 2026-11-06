"""
The owner's 2026-11-06 screenshot: a dark smudge beside a light hair-strand tip on every student, clean on
Real-ESRGAN. `blobs` only looks inside fill (more than 5 px from a strong line), so a mark beside a line
is invisible to it. This scores marks anywhere on the page:

- the luma difference to the teacher, `d = model - teacher`, kept where |d| >= MARK levels;
- opened 3x3, so a line drawn half a pixel off (a 1 to 2 px sliver of difference along it) drops out
  and only a mark at least 3 px across stays;
- reported per mille of page pixels, dark (model under the teacher) and light apart, and as `near`,
  the share of that within 6 px of a teacher line (Sobel over 24).

`--sheet` writes, per model, the worst windows on the extra pages: source, model, teacher, difference.

    python smudge.py c48n8-long hyb-c48n8e-b64n4-f-hyb-last teacher --extra C:/path/page.png --sheet
"""

import argparse
import json
from pathlib import Path

import numpy as np
import torch
import torch.nn.functional as F
from PIL import Image

from common import WORK
from evaluate import luma_u8, take_colour_from
from measure import REFERENCE, cached_teacher, lanczos_2x, load_page, load_teacher, model_runner, pool, sobel, teacher_2x

MARK = 20
WINDOW = 48


def marks(d: torch.Tensor, threshold: float) -> torch.Tensor:
    spot = (d.abs() >= threshold).float()
    return pool(pool(spot, 3, "min"), 3, "max") > 0.5


def scores(p: torch.Tensor, t: torch.Tensor) -> tuple[dict, torch.Tensor]:
    d = p - t
    m = marks(d, MARK)
    near = pool((sobel(t) > 24).float(), 13, "max") > 0.5
    n = d.numel()
    out = {
        "marks": m.float().sum().item() / n,
        "dark": (m & (d < 0)).float().sum().item() / n,
        "light": (m & (d > 0)).float().sum().item() / n,
        "near": (m & near).float().sum().item() / max(m.float().sum().item(), 1.0),
        "marks12": marks(d, 12).float().sum().item() / n,
    }
    return out, m.float() * d.abs()


def worst(weight: torch.Tensor, count: int) -> list[tuple[int, int]]:
    w = F.avg_pool2d(weight[None, None], WINDOW // 2, WINDOW // 4)[0, 0]
    picks = []
    for _ in range(count):
        i = int(w.argmax())
        by, bx = divmod(i, w.shape[1])
        if w[by, bx] <= 0:
            break
        picks.append((by * WINDOW // 4, bx * WINDOW // 4))
        w[max(0, by - 4):by + 5, max(0, bx - 4):bx + 5] = 0
    return picks


def sheet(picks, images, d: np.ndarray, path: Path, zoom: int = 4):
    if not picks:
        return
    cell = WINDOW * zoom
    out = Image.new("RGB", ((cell + 4) * (len(images) + 1), (cell + 4) * len(picks)), (40, 40, 40))
    diff = np.clip(128 + 3 * d, 0, 255).astype(np.uint8)
    for row, (y, x) in enumerate(picks):
        y = max(0, min(y - WINDOW // 4, d.shape[0] - WINDOW))
        x = max(0, min(x - WINDOW // 4, d.shape[1] - WINDOW))
        crops = [img[y:y + WINDOW, x:x + WINDOW] for img in images]
        crops.append(np.repeat(diff[y:y + WINDOW, x:x + WINDOW, None], 3, axis=2))
        for col, crop in enumerate(crops):
            tile = Image.fromarray(np.ascontiguousarray(crop)).resize((cell, cell), Image.NEAREST)
            out.paste(tile, (col * (cell + 4), row * (cell + 4)))
    out.save(path)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("models", nargs="+")
    parser.add_argument("--extra", nargs="*", default=[], help="more pages (PNG paths) scored on their own")
    parser.add_argument("--sheet", action="store_true", help="write worst-window sheets for the extra pages")
    parser.add_argument("--cpu", action="store_true")
    args = parser.parse_args()
    device = torch.device("cpu" if args.cpu else "cuda")
    if args.cpu:
        torch.set_num_threads(6)
    teacher = load_teacher(device)
    runners = {m: model_runner(m, device) for m in args.models}
    pages = [(e["key"], e["kind"], REFERENCE / "pages" / f"{e['key']}.png", REFERENCE / "teacher" / f"{e['key']}.png")
             for e in json.loads((REFERENCE / "pages.json").read_text())]
    pages += [(Path(p).stem[:12], "extra", Path(p), None) for p in args.extra]
    out_dir = WORK / "show" / "smudge"
    out_dir.mkdir(parents=True, exist_ok=True)
    rows = {m: [] for m in args.models}
    for key, kind, path, cache in pages:
        page = load_page(path)
        s_rgb = lanczos_2x(page)
        t_raw = cached_teacher(teacher, device, page, cache) if cache else teacher_2x(teacher, device, page)
        t_rgb = take_colour_from(t_raw, s_rgb)
        t = torch.from_numpy(luma_u8(t_rgb)).float().to(device)
        for m, run in runners.items():
            if m == "teacher":
                continue
            rgb = take_colour_from(run(page), s_rgb)
            p = torch.from_numpy(luma_u8(rgb)).float().to(device)
            s, weight = scores(p, t)
            rows[m].append((key, kind, s))
            if args.sheet and kind == "extra":
                sheet(worst(weight, 8), [s_rgb, rgb, t_rgb], (p - t).cpu().numpy(), out_dir / f"{key}-{m}.png")
    keys = ("marks", "dark", "light", "near", "marks12")
    print(f"marks: |model - teacher| >= {MARK} levels, opened 3x3, per mille of page; near = share within 6 px of a line")
    print(f"{'model':34} {'group':8} " + " ".join(f"{k:>8}" for k in keys))
    for m, r in rows.items():
        for group in ("owner", "colour", "grey", "extra"):
            vals = [s for key, kind, s in r if (key == "owner-webtoon") == (group == "owner") and
                    (group == "owner" or kind == group)]
            if vals:
                cells = [np.mean([v[k] for v in vals]) * (1 if k == "near" else 1000) for k in keys]
                print(f"{m:34} {group:8} " + " ".join(f"{c:8.3f}" for c in cells))


if __name__ == "__main__":
    main()
