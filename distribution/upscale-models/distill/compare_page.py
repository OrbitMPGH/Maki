"""
Writes X:/MakiModel/bakeoff/index.html from bakeoff.py's crops. A local file on purpose:
the crops are pages of the owner's library, so the page is never published anywhere.

    python compare_page.py
"""

import html
import json

from common import WORK

LABELS = {
    "lanczos": ("Lanczos", "no enhancement, the baseline"),
    "real-esrgan": ("Real-ESRGAN", "BSD-3, what Maki serves today"),
    "span-s": ("SPAN-S", "CC BY-NC, Maki's other model"),
    "mangajanai": ("MangaJaNai V1", "CC BY-NC, ESRGAN for black-and-white manga"),
    "ijanai-hat": ("IllustrationJaNai HAT-L", "CC BY-NC, detail"),
    "ijanai-dat2": ("IllustrationJaNai DAT2", "CC BY-NC, detail"),
    "ijanai-dat2-denoise": ("IllustrationJaNai DAT2 denoise", "CC BY-NC"),
    "waifu2x": ("waifu2x swin_unet", "MIT, art_scan noise 1"),
}
ORDER = ["lanczos", "real-esrgan", "mangajanai", "ijanai-hat", "ijanai-dat2", "ijanai-dat2-denoise", "waifu2x", "span-s"]


def label(name: str) -> tuple[str, str]:
    if name.startswith("student-"):
        spec, _, tag = name.removeprefix("student-").partition("-")
        channels, convs = spec[1:].split("n")
        if tag:
            return f"Student {channels}x{convs} ({tag})", "ours, Real-ESRGAN + DAT2 + real volume pages"
        return f"Student {channels}x{convs}", "ours, learned from Real-ESRGAN"
    return LABELS.get(name, (name, ""))


def main():
    out = WORK / "bakeoff"
    index = json.loads((out / "index.json").read_text())
    rows = []
    for entry in index:
        names = sorted(entry["crops"], key=lambda n: (ORDER.index(n) if n in ORDER else 1.5 if n.startswith("student-") else 99, n))
        cells = []
        for name in names:
            title, note = label(name)
            cells.append(
                f'<figure><img src="{html.escape(entry["crops"][name])}" alt="{html.escape(title)}" loading="lazy">'
                f'<figcaption><b>{html.escape(title)}</b><span>{html.escape(note)}</span></figcaption></figure>')
        kind = "black and white" if entry["grey"] else "colour"
        w, h = entry["size"]
        rows.append(f'<section><h2>Page {len(rows) + 1} <small>{w}x{h}, {kind}</small></h2>'
                    f'<div class="grid">{"".join(cells)}</div></section>')
    page = f"""<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>Upscaler comparison</title>
<style>
:root {{ --bg: #f6f5f2; --fg: #1d1c1a; --muted: #6b6862; --card: #ffffff; --line: #dedbd4; }}
@media (prefers-color-scheme: dark) {{ :root {{ --bg: #141413; --fg: #ecebe7; --muted: #9c9890; --card: #1e1d1b; --line: #33312d; }} }}
body {{ margin: 0; padding: 24px 16px 64px; background: var(--bg); color: var(--fg); font: 15px/1.45 system-ui, sans-serif; }}
header {{ max-width: 1400px; margin: 0 auto 8px; }}
h1 {{ font-size: 22px; margin: 0 0 6px; }}
p {{ color: var(--muted); margin: 0 0 12px; max-width: 72ch; }}
label {{ display: inline-flex; gap: 8px; align-items: center; cursor: pointer; }}
section {{ max-width: 1400px; margin: 28px auto 0; }}
h2 {{ font-size: 17px; margin: 0 0 10px; }} h2 small {{ color: var(--muted); font-weight: 400; }}
.grid {{ display: grid; grid-template-columns: repeat(auto-fill, minmax(260px, 1fr)); gap: 12px; }}
figure {{ margin: 0; background: var(--card); border: 1px solid var(--line); border-radius: 10px; overflow: hidden; }}
.frame {{ overflow: hidden; }}
img {{ display: block; width: 100%; height: auto; }}
body.zoom img {{ width: 200%; image-rendering: pixelated; }}
figcaption {{ padding: 8px 10px; display: grid; }} figcaption span {{ color: var(--muted); font-size: 13px; }}
</style></head>
<body>
<header><h1>Upscaler comparison</h1>
<p>Held-out pages from your library, every model run through Maki's colour pass and cropped at the same spot
(the most detailed region of the source). This page and its images stay on this machine.</p>
<label><input type="checkbox" id="zoom"> Zoom 2x (top-left quarter, nearest neighbour)</label></header>
{"".join(rows)}
<script>document.getElementById('zoom').addEventListener('change', e => document.body.classList.toggle('zoom', e.target.checked));</script>
</body></html>"""
    (out / "index.html").write_text(page, encoding="utf-8")
    print(out / "index.html")


if __name__ == "__main__":
    main()
