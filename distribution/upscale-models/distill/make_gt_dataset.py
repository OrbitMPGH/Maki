"""
Builds the ground-truth set: crops of high-quality digital volume pages as the target, and the same
crop shrunk 2x and damaged the way scanlations are as the input. The student then learns to restore
real detail, which no teacher can give it. Reads the libraries read-only; output is git-ignored under
X:/MakiModel/data, like every other crop of the owner's library.

A page wider than a reading source would be is shrunk first (to 1400-2200 px), so screentone sits at
the frequency it has in the pages Maki actually upscales, and the shrunk input lands at chapter size.

    python make_gt_dataset.py --library //192.168.1.10/media/library/manga --library X:/meenge
"""

import argparse
import hashlib
import io
import random
import re
import zipfile
from pathlib import Path

import numpy as np
from PIL import Image, ImageFilter

VOLUME = re.compile(r"\bv(ol(ume)?)?\.?\s?\d+\b", re.I)
CHAPTER = re.compile(r"\bch(apter)?\.?\s?\d", re.I)
YEAR = re.compile(r"\s*\(\d{4}\)\s*$")
IMAGE_EXTENSIONS = (".jpg", ".jpeg", ".png", ".webp")
FILTERS = [Image.Resampling.BOX, Image.Resampling.BILINEAR, Image.Resampling.BICUBIC, Image.Resampling.LANCZOS]


def series_key(name: str) -> str:
    return YEAR.sub("", name).casefold().strip()


def is_validation(key: str, share: float) -> bool:
    return hashlib.sha1(f"maki-gt-{key}".encode()).digest()[0] / 256 < share


def volumes(libraries: list[str], volume_libraries: list[str]) -> dict[str, list[Path]]:
    """Archives named as volumes in `libraries`, and every archive in `volume_libraries`."""
    found: dict[str, list[Path]] = {}
    for library in libraries + volume_libraries:
        every = library in volume_libraries
        for folder in sorted(Path(library).iterdir()):
            if not folder.is_dir():
                continue
            files = [f for f in folder.rglob("*") if f.suffix.lower() in (".cbz", ".zip")
                     and (every or (VOLUME.search(f.stem) and not CHAPTER.search(f.stem)))]
            if files and series_key(folder.name) not in found:
                found[series_key(folder.name)] = files
    return found


def degrade(hr: Image.Image, rng: random.Random) -> Image.Image:
    """One plausible history of a reading source: blur, one or two resizes, compression, sharpening."""
    w, h = hr.size
    image = hr
    if rng.random() < 0.3:
        image = image.filter(ImageFilter.GaussianBlur(rng.uniform(0.3, 0.9)))
    if rng.random() < 0.3:
        step = rng.uniform(0.55, 0.85)
        image = image.resize((round(w * step), round(h * step)), rng.choice(FILTERS))
    image = image.resize((w // 2, h // 2), rng.choice(FILTERS))
    if rng.random() < 0.2:
        image = image.filter(ImageFilter.UnsharpMask(radius=rng.uniform(0.6, 1.5), percent=rng.randint(30, 90), threshold=1))
    roll = rng.random()
    if roll < 0.75:
        buffer = io.BytesIO()
        image.save(buffer, "JPEG", quality=rng.randint(55, 95), subsampling=rng.choice([0, 2]))
        image = Image.open(buffer).convert("RGB")
    elif roll < 0.95:
        buffer = io.BytesIO()
        image.save(buffer, "WEBP", quality=rng.randint(60, 95))
        image = Image.open(buffer).convert("RGB")
    return image


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--library", action="append", default=[], help="volumes are found by file name")
    parser.add_argument("--volume-library", action="append", default=[], help="every archive is a volume")
    parser.add_argument("--colour-boost", type=int, default=3, help="crop multiplier for colour pages")
    parser.add_argument("--prefix", default="", help="output file prefix, so a set can be added beside another")
    parser.add_argument("--volumes-per-series", type=int, default=4)
    parser.add_argument("--pages-per-volume", type=int, default=8)
    parser.add_argument("--crops-per-page", type=int, default=4)
    parser.add_argument("--variants", type=int, default=2, help="damaged inputs per target crop")
    parser.add_argument("--size", type=int, default=320, help="target crop; the input is half")
    parser.add_argument("--min-width", type=int, default=1350)
    parser.add_argument("--val-share", type=float, default=0.1)
    parser.add_argument("--val-series", action="append", default=[],
                        help="hold out every series whose name contains this, instead of hashing")
    parser.add_argument("--seed", type=int, default=2)
    args = parser.parse_args()

    rng = random.Random(args.seed)
    from common import WORK
    out = WORK / "data"
    out.mkdir(parents=True, exist_ok=True)
    found = volumes(args.library, args.volume_library)
    print(f"{len(found)} series with volumes", flush=True)
    sets = {"train": ([], []), "val": ([], [])}
    colour_pages = 0
    for done, (key, files) in enumerate(sorted(found.items()), 1):
        if args.val_series:
            split = "val" if any(v.casefold() in key for v in args.val_series) else "train"
        else:
            split = "val" if is_validation(key, args.val_share) else "train"
        for path in rng.sample(files, min(args.volumes_per_series, len(files))):
            try:
                archive = zipfile.ZipFile(path)
            except (zipfile.BadZipFile, OSError):
                continue
            names = sorted(n for n in archive.namelist() if n.lower().endswith(IMAGE_EXTENSIONS) and "__MACOSX" not in n)
            # Skip the cover and the last pages, which are often ads or credits.
            body = names[2:-4] if len(names) > 12 else names
            for name in rng.sample(body, min(args.pages_per_volume, len(body))):
                try:
                    page = Image.open(io.BytesIO(archive.read(name))).convert("RGB")
                except Exception:
                    continue
                if page.width < args.min_width:
                    continue
                if page.width > 2200:
                    width = rng.randint(1400, 2200)
                    page = page.resize((width, round(page.height * width / page.width)), Image.Resampling.LANCZOS)
                pixels = np.asarray(page.resize((page.width // 8, page.height // 8)), dtype=np.int16)
                spread = pixels.max(2) - pixels.min(2)
                colour = (spread > 16).mean() > 0.002 or spread.mean() > 6
                boost = args.colour_boost if colour else 1
                colour_pages += colour
                for _ in range(args.crops_per_page * boost):
                    x = rng.randrange(0, page.width - args.size)
                    y = rng.randrange(0, page.height - args.size)
                    hr = page.crop((x, y, x + args.size, y + args.size))
                    if np.asarray(hr).std() < 4:
                        continue
                    for _ in range(args.variants):
                        sets[split][0].append(np.asarray(degrade(hr, rng)))
                        sets[split][1].append(np.asarray(hr))
        print(f"{done}/{len(found)} series, {len(sets['train'][0])} train and {len(sets['val'][0])} val pairs", flush=True)
    for split, (lr, hr) in sets.items():
        if not lr:
            print(f"gt {split}: no pairs, nothing written")
            continue
        np.savez(out / f"{args.prefix}gt_{split}.npz", lr=np.stack(lr), hr=np.stack(hr))
        print(f"gt {split}: {len(lr)} pairs")
    print(f"{colour_pages} colour pages")


if __name__ == "__main__":
    main()
