"""
Builds the distillation set: random low-res crops of library pages and what Maki serves for them from
Real-ESRGAN (4x, rounded, Lanczos-halved to 2x). Walks library folders (one folder per series,
archives inside) read-only. Everything lands under X:/MakiModel/data, which is
git-ignored: the crops are pages of the owner's library and must never be committed or published.

Series, not pages, are split between train and validation, so a validation page is never a
neighbour of a training one from the same series. A series present in two libraries is used once.

    python make_dataset.py --library //192.168.1.10/media/library/manga --library X:/meenge
"""

import argparse
import hashlib
import io
import random
import re
import zipfile
from pathlib import Path

import numpy as np
import torch
from PIL import Image

from common import WORK, load_teacher, teacher_2x
from sets import RowWriter

IMAGE_EXTENSIONS = (".jpg", ".jpeg", ".png", ".webp")


def series_key(folder: str) -> str:
    """`SSS-Class Revival Hunter (Digital) (1r0n)` and `SSS-Class Revival Hunter` are one series: release tags
    in brackets are dropped, so the same work never lands in both train and validation. A bare year such as
    `(2020)` stays, because the library's own folders carry one and the split was first drawn with it."""
    return re.sub(r"\s*[\(\[](?!\d{4}[\)\]])[^\)\]]*[\)\]]", "", folder).strip().casefold()


def is_validation(series: str, share: float) -> bool:
    digest = hashlib.sha1(f"maki-distill-{series_key(series)}".encode()).digest()
    return digest[0] / 256 < share


def open_rgb(data: bytes) -> np.ndarray:
    image = Image.open(io.BytesIO(data))
    image.load()
    if image.mode in ("RGBA", "LA") or (image.mode == "P" and "transparency" in image.info):
        rgba = image.convert("RGBA")
        white = Image.new("RGBA", rgba.size, (255, 255, 255, 255))
        image = Image.alpha_composite(white, rgba)
    return np.asarray(image.convert("RGB"))


def library_series(libraries: list[str]) -> dict[str, list[Path]]:
    by_series: dict[str, list[Path]] = {}
    for library in libraries:
        for folder in sorted(Path(library).iterdir()):
            if not folder.is_dir() or folder.name.casefold() in by_series:
                continue
            files = [f for f in folder.rglob("*") if f.suffix.lower() in (".cbz", ".zip")]
            if files:
                by_series[folder.name.casefold()] = files
    return by_series


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--library", action="append", required=True, help="a folder of series folders; repeatable")
    parser.add_argument("--chapters-per-series", type=int, default=5)
    parser.add_argument("--pages-per-chapter", type=int, default=4)
    parser.add_argument("--crops-per-page", type=int, default=4)
    parser.add_argument("--size", type=int, default=160)
    parser.add_argument("--val-share", type=float, default=0.1)
    parser.add_argument("--val-pages", type=int, default=40, help="whole held-out pages kept for the page-level eval")
    parser.add_argument("--seed", type=int, default=1)
    parser.add_argument("--prefix", default="", help="output file prefix, so a set can be added beside another")
    parser.add_argument("--colour-only", action="store_true", help="skip black-and-white pages")
    parser.add_argument("--min-std", type=float, default=2.0,
                        help="drop crops flatter than this; keep it low so blotchy flat fills are not thrown out")
    parser.add_argument("--max-crops", type=int, default=0,
                        help="write straight to disk as {prefix}{split}_lr.npy/_hr.npy up to this many crops per split, "
                             "instead of collecting a .npz in memory")
    args = parser.parse_args()

    random.seed(args.seed)
    rng = np.random.default_rng(args.seed)
    device = torch.device("cuda")
    teacher = load_teacher(device)
    out = WORK / "data"
    pages_dir = out / (f"val_pages_{args.prefix.rstrip('_')}" if args.prefix else "val_pages")
    pages_dir.mkdir(parents=True, exist_ok=True)

    by_series = library_series(args.library)
    print(f"{len(by_series)} series", flush=True)
    sets = {"train": ([], []), "val": ([], [])}
    if args.max_crops:
        # Pre-sized files on disk: memory stays at one page however many crops are taken.
        sets = {split: tuple(RowWriter(out / f"{args.prefix}{split}_{key}.npy",
                                       (args.max_crops if split == "train" else max(1, args.max_crops // 8),
                                        args.size * scale, args.size * scale, 3))
                             for key, scale in (("lr", 1), ("hr", 2)))
                for split in ("train", "val")}
    counts = {"train": 0, "val": 0}
    val_pages = 0
    for done, (name, files) in enumerate(sorted(by_series.items()), 1):
        split = "val" if is_validation(name, args.val_share) else "train"
        for archive_path in random.sample(files, min(args.chapters_per_series, len(files))):
            try:
                archive = zipfile.ZipFile(archive_path)
            except (zipfile.BadZipFile, OSError):
                continue
            members = sorted(n for n in archive.namelist() if n.lower().endswith(IMAGE_EXTENSIONS) and "__MACOSX" not in n)
            for member in random.sample(members, min(args.pages_per_chapter, len(members))):
                try:
                    page = open_rgb(archive.read(member))
                except Exception:
                    continue
                h, w = page.shape[:2]
                if min(h, w) < args.size + 8 or h * w > 16_000_000:
                    continue
                if args.colour_only:
                    sample = page[::7, ::7].astype(np.int16)
                    if ((sample.max(-1) - sample.min(-1)) > 16).mean() < 0.05:
                        continue
                if args.max_crops and counts[split] >= len(sets[split][0]):
                    continue
                if split == "val" and val_pages < args.val_pages and h * w <= 4_000_000:
                    Image.fromarray(page).save(pages_dir / f"{val_pages:03}.png")
                    val_pages += 1
                for _ in range(args.crops_per_page):
                    y = int(rng.integers(0, h - args.size + 1))
                    x = int(rng.integers(0, w - args.size + 1))
                    crop = page[y:y + args.size, x:x + args.size]
                    if crop.std() < args.min_std:
                        continue
                    target = teacher_2x(teacher, torch.from_numpy(np.ascontiguousarray(crop)).to(device))
                    if args.max_crops:
                        if counts[split] >= len(sets[split][0]):
                            break
                        sets[split][0][counts[split]] = crop
                        sets[split][1][counts[split]] = target
                    else:
                        sets[split][0].append(crop)
                        sets[split][1].append(target)
                    counts[split] += 1
        if done % 10 == 0:
            print(f"{done} series, {counts['train']} train and {counts['val']} val crops", flush=True)
        if args.max_crops and all(counts[s] >= len(sets[s][0]) for s in sets):
            break

    for split, (lr, hr) in sets.items():
        if args.max_crops:
            lr.close(); hr.close()
            np.save(out / f"{args.prefix}{split}_count.npy", np.array(counts[split]))
        elif lr:
            np.savez(out / f"{args.prefix}{split}.npz", lr=np.stack(lr), hr=np.stack(hr))
        print(f"{split}: {counts[split]} crops")
    print(f"{val_pages} whole validation pages in {pages_dir}")


if __name__ == "__main__":
    main()
