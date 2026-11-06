"""
The crop bank our own model trains on: clean high-resolution crops from the volume releases, each beside
a target with its screentone flattened (descreen.py). Training damages the original into an input
(degrade.py) and asks for the flattened target, so tone has one answer however the source aliased it.

Pages are scaled to 1400-2200 px wide first, the 2x of a typical reading source, so the model learns
detail at the size it will produce it. Webtoon slices are narrower than that already and stay as they are.

    python make_hr.py --pages 6000 --crops-per-page 5
"""

import argparse
import collections
import concurrent.futures
import itertools
import random
import zipfile
import zlib
from pathlib import Path

import numpy as np
import torch
from PIL import Image

from common import WORK
from descreen import descreen
from sets import RowWriter

IMAGE = (".jpg", ".jpeg", ".png", ".webp")
SIZE = 256
CONTEXT = 64


def pages_of(root: Path) -> dict[str, list[tuple[Path, str]]]:
    series = {}
    for folder in sorted(p for p in root.iterdir() if p.is_dir()):
        found = []
        for cbz in sorted(folder.rglob("*.cbz")):
            try:
                with zipfile.ZipFile(cbz) as z:
                    found += [(cbz, n) for n in sorted(z.namelist()) if n.lower().endswith(IMAGE)]
            except zipfile.BadZipFile:
                continue
        if found:
            series[folder.name] = found
    return series


def load(item: tuple[Path, str], rng: random.Random) -> np.ndarray | None:
    cbz, name = item
    with zipfile.ZipFile(cbz) as z, z.open(name) as fh:
        page = Image.open(fh).convert("RGB")
    w, h = page.size
    if w > 2200 or (w > 1400 and rng.random() < 0.5):
        target = rng.randint(1400, min(w, 2200))
        page = page.resize((target, round(h * target / w)), Image.Resampling.LANCZOS)
    if min(page.size) < SIZE:
        return None
    return np.asarray(page)


def pick_crops(page: np.ndarray, count: int, rng: random.Random) -> list[tuple[int, int]]:
    """Crops with something in them: blank margins and flat fills teach nothing."""
    grey = page.mean(axis=2)
    h, w = grey.shape
    picks = []
    for _ in range(count * 8):
        y, x = rng.randrange(h - SIZE + 1), rng.randrange(w - SIZE + 1)
        if grey[y:y + SIZE:4, x:x + SIZE:4].std() > 18:
            picks.append((y, x))
            if len(picks) == count:
                break
    return picks


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--volumes", default="X:/MangaVolumes")
    parser.add_argument("--pages", type=int, default=6000)
    parser.add_argument("--crops-per-page", type=int, default=5)
    parser.add_argument("--val-share", type=float, default=0.04)
    parser.add_argument("--seed", type=int, default=7)
    args = parser.parse_args()
    rng = random.Random(args.seed)
    device = torch.device("cuda")

    series = pages_of(Path(args.volumes))
    per = args.pages // len(series)
    chosen = []
    for name, items in series.items():
        chosen += [(name, it) for it in rng.sample(items, min(per, len(items)))]
    rng.shuffle(chosen)
    print(f"{len(series)} series, {len(chosen)} pages", flush=True)

    out = WORK / "data"
    capacity = len(chosen) * args.crops_per_page
    banks = {}
    for split, size in (("train", capacity), ("val", int(capacity * args.val_share * 3))):
        banks[split] = {
            key: RowWriter(out / f"own_{split}_{key}.npy", (size, SIZE, SIZE, 3))
            for key in ("source", "target")
        }
    counts = {"train": 0, "val": 0}
    origins = {"train": [], "val": []}

    weights = torch.tensor([0.299, 0.587, 0.114], device=device)

    def job(pair):
        return pair[0], pair[1][0], load(pair[1], random.Random(zlib.crc32(pair[1][1].encode())))

    # A few pages decoded ahead, never the whole list: a decoded page is 10 to 50 MB.
    with concurrent.futures.ThreadPoolExecutor(4) as pool:
        queue = collections.deque()
        todo = iter(chosen)
        for pair in itertools.islice(todo, 6):
            queue.append(pool.submit(job, pair))
        index = 0
        while queue:
            name, cbz, page = queue.popleft().result()
            nxt = next(todo, None)
            if nxt is not None:
                queue.append(pool.submit(job, nxt))
            index += 1
            if page is None:
                continue
            # Whole volumes go to one split, so validation is genuinely unseen pages.
            split = "val" if zlib.crc32(cbz.name.encode()) % 1000 < args.val_share * 1000 else "train"
            if counts[split] + args.crops_per_page > len(banks[split]["source"]):
                continue
            h, w = page.shape[:2]
            for y, x in pick_crops(page, args.crops_per_page, rng):
                # Descreen the crop with real context around it rather than the whole page.
                y0, x0 = max(0, y - CONTEXT), max(0, x - CONTEXT)
                y1, x1 = min(h, y + SIZE + CONTEXT), min(w, x + SIZE + CONTEXT)
                rgb = torch.from_numpy(page[y0:y1, x0:x1]).to(device).float()
                luma = rgb @ weights
                flat = descreen(luma)
                region = (rgb + (flat - luma)[..., None]).round().clamp(0, 255).byte().cpu().numpy()
                k = counts[split]
                banks[split]["source"][k] = page[y:y + SIZE, x:x + SIZE][None]
                banks[split]["target"][k] = region[y - y0:y - y0 + SIZE, x - x0:x - x0 + SIZE][None]
                origins[split].append(name)
                counts[split] += 1
            if index % 250 == 0:
                print(f"{index}/{len(chosen)} pages, {counts['train']} train and {counts['val']} val crops", flush=True)

    for split in ("train", "val"):
        for key, bank in banks[split].items():
            bank.close()
        np.save(out / f"own_{split}_series.npy", np.array(origins[split]))
        np.save(out / f"own_{split}_count.npy", np.array(counts[split]))
    print(f"done: {counts['train']} train and {counts['val']} val crops (banks sized {capacity}, use the count)", flush=True)


if __name__ == "__main__":
    main()
