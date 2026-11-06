"""
Damaged copies of a crop set, each labelled with what Real-ESRGAN makes of that damaged copy. A target
the teacher derives from the input itself is always reachable, so the student is never asked for an
average of answers. Works in chunks straight to disk.

    python make_served.py train --copies 2           # writes served_train_lr.npy / _hr.npy
    python make_served.py col_train --copies 1        # writes col_served_train_...
    python make_served.py col2_train --grid-offset    # JPEG blocks at a random 0-7 px offset

Row k of the output is a copy of source row k % len(source).
"""

import argparse
import random

import numpy as np
import torch
from PIL import Image

from common import load_teacher
from degrade import damage
from label_clean import label
from sets import DATA, RowWriter, open_set

CHUNK = 512


def offset_damage(crop: Image.Image, rng: random.Random) -> Image.Image:
    """Damage with the JPEG block grid shifted by 0-7 px: Maki's tiles start anywhere on a page, so
    the student must not learn where block edges fall. Reflect-pad on the top left, damage, crop back."""
    dy, dx = rng.randrange(8), rng.randrange(8)
    if not (dy or dx):
        return damage(crop, rng)
    a = np.pad(np.asarray(crop), ((dy, 0), (dx, 0), (0, 0)), mode="reflect")
    return Image.fromarray(np.asarray(damage(Image.fromarray(a), rng))[dy:, dx:])


def served_name(source: str) -> str:
    prefix, _, split = source.rpartition("_") if source.count("_") else ("", "", source)
    return f"{prefix}_served_{split}" if prefix else f"served_{split}"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("source", help="set name, e.g. train or col_train")
    parser.add_argument("--copies", type=int, default=2)
    parser.add_argument("--seed", type=int, default=101)
    parser.add_argument("--grid-offset", action="store_true", help="JPEG saves at a random 0-7 px block offset")
    args = parser.parse_args()
    device = torch.device("cuda")
    teacher = load_teacher(device)
    lr, _ = open_set(args.source)
    name = served_name(args.source)
    total = len(lr) * args.copies
    out_lr = RowWriter(DATA / f"{name}_lr.npy", (total,) + lr.shape[1:])
    out_hr = RowWriter(DATA / f"{name}_hr.npy", (total, lr.shape[1] * 2, lr.shape[2] * 2, 3))
    rng = random.Random(args.seed + hash(args.source.endswith("val")))
    k = 0
    for _ in range(args.copies):
        for i in range(0, len(lr), CHUNK):
            hurt = offset_damage if args.grid_offset else damage
            inputs = np.stack([np.asarray(hurt(Image.fromarray(c), rng)) for c in lr[i:i + CHUNK]])
            out_lr[k:k + len(inputs)] = inputs
            out_hr[k:k + len(inputs)] = label(teacher, 4, torch.float32, inputs, device, batch=32)
            k += len(inputs)
    out_lr.close(); out_hr.close()
    print(f"{name}: {k} damaged crops labelled", flush=True)


if __name__ == "__main__":
    main()
