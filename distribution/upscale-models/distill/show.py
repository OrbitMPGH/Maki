"""
A local comparison page for the owner: on each reference page, the windows where a baseline model was
most textured in flat fill (where the teacher is smooth), plus the window where the new model still is,
with every model and the teacher side by side at 3x nearest-neighbour. Written to
X:/MakiModel/show/index.html, a local file on purpose: the crops are pages of the owner's
library and the page is never published.

    python show.py --baseline c48n8-colour --best c48n8-se lanczos c48n8-colour c48n8-se teacher teacher-ens

`--marks-from` adds the windows where that model left the most smudges (smudge.py's marks), and `--extra`
scores more pages that are not in the reference set (their teacher is run, not cached).
"""

import argparse
import html
import json

import numpy as np
import torch
from PIL import Image

from pathlib import Path

from evaluate import luma_u8, take_colour_from
from measure import (OUT, PSEUDO, REFERENCE, WORK, band, cached_teacher, fill_mask, flat_mask, lanczos_2x, load_page,
                     load_teacher, model_runner, teacher_2x, worst_windows, WINDOW)
from smudge import scores

LABELS = {
    "lanczos": "Lanczos (no model)",
    "teacher": "Real-ESRGAN",
    "teacher-ens": "Real-ESRGAN, 8 orientations averaged",
    "anime6b": "Real-ESRGAN x4plus anime (candidate teacher)",
    "c48n8-long": "long (what you saw last)",
    "c48n8e-ctl": "control (long + training-only branches)",
    "c48n8e-b1-mask-last": "B1: fill-only smoothing",
    "c48n8e-b2-grad-last": "B2: fill-only smoothing + edge gradient",
    "c48n8e-b6-lp-last": "B6: fill-only smoothing + mode-seeking error (best without GAN)",
    "c48n8e-c3-g005at5k": "C3: B6 + weak GAN, 5k steps",
    "c48n8e-e3-fine-band12-last": "E3: B6 + static term + stronger fill smoothing",
    "hyb-c48n8e-b64n4-f-hyb-last": "Side branch on E3",
    "hyb-c48n8e-b64n4-final015-last": "Final (shipped as Maki 2x)",
    "hyb-c48n8e-b64n4-m1-sharp02-last": "M1 (Maki 2x now)",
    "hyb-c48n8e-b64n4-n0-more-last": "N0: M1 trained 20k steps more",
    "hyb-c48n8e-b64n4-o3-thin-last": "O3: M1 + thin-line term",
    "hyb-c48n8e-b64n4-o4-gan-last": "O4: O3 + short GAN",
    "hyb-c48n8e-b64n4-n2-hard05-sharp03-last": "N2: M1 + hard crops, sharpness 0.3",
    "c48n8e-c2-g02-ldlat5k": "C2: B6 + stronger GAN with LDL, 5k steps",
    "c48n8": "Round one",
    "c48n8-served": "Round four",
    "c48n8-colour": "Colour run (what you saw)",
    "c48n8-a4": "A4",
    "c48n8-se": "se (A4 + ensemble fill labels)",
    "c48n8-a2f-last": "A2 with flips",
    "c48n8-ens-last": "ens (ensemble labels everywhere)",
    "c48n8-ens": "ens (ensemble labels everywhere)",
}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("models", nargs="+")
    parser.add_argument("--baseline", default="c48n8-colour")
    parser.add_argument("--best", required=True)
    parser.add_argument("--pages", nargs="*", help="reference keys; default all")
    parser.add_argument("--marks-from", help="also show where this model left the most smudges")
    parser.add_argument("--extra", nargs="*", default=[], help="more pages (PNG paths) outside the reference set")
    args = parser.parse_args()
    device = torch.device("cuda")
    wanted = list(args.models)
    for m in (args.baseline, args.best, args.marks_from):
        if m is None:
            continue
        if m not in wanted:
            wanted.insert(1 if wanted and wanted[0] == "lanczos" else 0, m)
    models = list(dict.fromkeys(m for m in wanted if m in PSEUDO or (WORK / f"student-{m}.pt").exists()))
    runners = {m: model_runner(m, device) for m in models}
    teacher = load_teacher(device)
    out = WORK / "show"
    out.mkdir(exist_ok=True)
    entries = json.loads((REFERENCE / "pages.json").read_text())
    if args.pages:
        entries = [e for e in entries if e["key"] in args.pages]
    entries += [{"key": f"extra page {i + 1}", "kind": "extra", "path": p} for i, p in enumerate(args.extra)]
    sections = []
    for entry in entries:
        key = entry["key"]
        if "path" in entry:
            page = load_page(Path(entry["path"]))
            s_rgb = lanczos_2x(page)
            t_rgb = take_colour_from(teacher_2x(teacher, device, page), s_rgb)
        else:
            page = load_page(REFERENCE / "pages" / f"{key}.png")
            s_rgb = lanczos_2x(page)
            t_rgb = take_colour_from(cached_teacher(teacher, device, page, REFERENCE / "teacher" / f"{key}.png"), s_rgb)
        t = torch.from_numpy(luma_u8(t_rgb)).float().to(device)
        flat, _ = flat_mask(t, t)
        pure = fill_mask(t) & flat
        images = {}
        for m, run in runners.items():
            images[m] = s_rgb if run is None else take_colour_from(run(page), s_rgb)
        images["teacher"] = t_rgb

        def excess(m):
            p = torch.from_numpy(luma_u8(images[m])).float().to(device)
            return (band(p).abs() - band(t).abs()).clamp_min(0)

        picks = [(w, f"where the {LABELS.get(args.baseline, args.baseline)} was most blotchy")
                 for w in worst_windows(excess(args.baseline), pure, 2)]
        for w in worst_windows(excess(args.best), pure, 3):
            if all(abs(w[0] - y) >= WINDOW or abs(w[1] - x) >= WINDOW for (y, x), _ in picks):
                picks.append((w, f"where {LABELS.get(args.best, args.best)} is still most blotchy"))
                break
        if args.marks_from:
            p = torch.from_numpy(luma_u8(images[args.marks_from])).float().to(device)
            _, weight = scores(p, t)
            found = 0
            for w in worst_windows(weight, torch.ones_like(weight, dtype=torch.bool), 4):
                if found == 2 or weight[w[0]:w[0] + WINDOW, w[1]:w[1] + WINDOW].sum() <= 0:
                    break
                if all(abs(w[0] - y) >= WINDOW or abs(w[1] - x) >= WINDOW for (y, x), _ in picks):
                    picks.append((w, f"where {LABELS.get(args.marks_from, args.marks_from)} left the most smudges"))
                    found += 1
        rows = []
        for n, ((y, x), why) in enumerate(picks):
            cells = []
            for m in models:
                name = f"{key.replace(' ', '-')}_{n}_{m}.png"
                Image.fromarray(np.ascontiguousarray(images[m][y:y + WINDOW, x:x + WINDOW])).save(out / name)
                cells.append(f'<figure><img src="{name}" alt=""><figcaption>{html.escape(LABELS.get(m, m))}</figcaption></figure>')
            rows.append(f'<h3>{html.escape(why)} <small>at {x // 2}, {y // 2} on the source</small></h3>'
                        f'<div class="row">{"".join(cells)}</div>')
        kind = {"colour": "colour", "extra": "your extra page"}.get(entry["kind"], "black and white")
        sections.append(f'<section><h2>{html.escape(key)} <small>{kind}</small></h2>{"".join(rows)}</section>')
        print(f"{key}: {len(picks)} windows", flush=True)

    table = ""
    rows = []
    for m in models:
        path = OUT / f"{m}.json"
        if not path.exists():
            continue
        s = json.loads(path.read_text())["summary"]
        o, g = s.get("owner", {}), s.get("grey", {})
        def cell(group, k, scale=1.0, digits=3):
            v = group.get(k)
            return "<td>&ndash;</td>" if v is None else f"<td>{v * scale:.{digits}f}</td>"
        rows.append(f"<tr><td>{html.escape(LABELS.get(m, m))}</td>" + cell(o, "marks", 1000) + cell(g, "marks", 1000)
                    + cell(o, "grain") + cell(g, "grain") + cell(o, "sharp_faint") + cell(g, "blobs", 1000, 2)
                    + cell(o, "psnr", digits=2) + "</tr>")
    if rows:
        table = ("<table><thead><tr><th>Model</th><th>Your page: smudges</th><th>Grey pages: smudges</th>"
                 "<th>Your page: grain</th><th>Grey pages: grain</th><th>Your page: faint line sharpness</th>"
                 "<th>Grey pages: blobs</th><th>Your page: PSNR to Real-ESRGAN</th>"
                 f"</tr></thead><tbody>{''.join(rows)}</tbody></table>"
                 "<p class=note>Smudges are marks at least 3 px across that differ from Real-ESRGAN by 20 levels or more, "
                 "per thousand pixels. Grain is pixel static in flat fill (Real-ESRGAN has some too). Faint line sharpness "
                 "is against Real-ESRGAN (1 is as sharp). Blobs are blotches inside fill. Lower is better except sharpness "
                 "and PSNR.</p>")
    page = f"""<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>Upscaler comparison</title>
<style>
:root {{ --bg: #f6f5f2; --fg: #1d1c1a; --muted: #6b6862; --line: #dedbd4; }}
@media (prefers-color-scheme: dark) {{ :root {{ --bg: #141413; --fg: #ecebe7; --muted: #9c9890; --line: #33312d; }} }}
body {{ margin: 0; padding: 24px 16px 64px; background: var(--bg); color: var(--fg); font: 15px/1.45 system-ui, sans-serif; }}
main {{ max-width: 1800px; margin: 0 auto; }}
h1 {{ font-size: 22px; margin: 0 0 8px; }}
h2 {{ font-size: 18px; margin: 32px 0 4px; }}
h3 {{ font-size: 14px; font-weight: 500; color: var(--muted); margin: 14px 0 6px; }}
small {{ color: var(--muted); font-weight: 400; }}
.row {{ display: flex; gap: 8px; overflow-x: auto; padding-bottom: 6px; }}
figure {{ margin: 0; flex: none; }}
img {{ width: 384px; height: 384px; image-rendering: pixelated; display: block; border: 1px solid var(--line); }}
figcaption {{ font-size: 13px; color: var(--muted); margin-top: 4px; max-width: 384px; }}
table {{ border-collapse: collapse; margin: 12px 0; font-variant-numeric: tabular-nums; }}
td, th {{ border-bottom: 1px solid var(--line); padding: 4px 12px 4px 0; text-align: left; }}
.note {{ color: var(--muted); max-width: 900px; }}
</style></head><body><main>
<h1>Upscaler comparison</h1>
<p class=note>Each window is 64 source pixels, shown at 6x with no smoothing so single-pixel differences stay visible.</p>
{table}
{''.join(sections)}
</main></body></html>"""
    (out / "index.html").write_text(page, encoding="utf-8")
    print(f"wrote {out / 'index.html'}")


if __name__ == "__main__":
    main()
