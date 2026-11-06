"""
Self-ensemble labels for the fill crops of a set (PLAN.md, held back until the owner still sees blotches
after A2): the teacher's 2x output averaged over the 8 flips and rotations of the input. A blotch the
teacher leaves in one orientation and not another is averaged down; a real edge is the same in all 8.

    python make_ensemble.py col2_train col2_served_train col_train col_served_train

Writes {name}_se_lr.npy / _hr.npy holding only that set's fill crops (crop_stats.py), in their order.
With --all, every crop is labelled instead, into {name}_se8 (a full replacement for the set, with a copy
of its stats so `@fill` and `@rest` views still work):

    python make_ensemble.py --all col2_train col2_served_train
"""

import shutil
import sys

import numpy as np
import torch

from common import halve, load_teacher, to_byte
from crop_stats import is_fill, stats
from sets import DATA, RowWriter, open_set

BATCH = 16


def ensemble(teacher, crops: np.ndarray, device) -> np.ndarray:
    x = torch.from_numpy(crops).to(device).permute(0, 3, 1, 2).float() / 255
    total = None
    for k in range(8):
        v = x.flip(3) if k & 4 else x
        v = v.rot90(k & 3, (2, 3))
        with torch.no_grad():
            y = to_byte(teacher(v)).permute(0, 2, 3, 1).cpu().numpy()
        # Halve each orientation the way Maki would, then turn it back.
        y = torch.from_numpy(np.stack([halve(t) for t in y])).permute(0, 3, 1, 2).float()
        y = y.rot90(-(k & 3), (2, 3))
        y = y.flip(3) if k & 4 else y
        total = y if total is None else total + y
    return (total / 8).round().clamp(0, 255).byte().permute(0, 2, 3, 1).numpy()


def main():
    device = torch.device("cuda")
    teacher = load_teacher(device)
    every = "--all" in sys.argv
    for name in [a for a in sys.argv[1:] if a != "--all"]:
        lr, _ = open_set(name)
        rows = np.arange(len(lr)) if every else np.flatnonzero(is_fill(stats(name, len(lr))))
        out_name = f"{name}_se8" if every else f"{name}_se"
        if every and (DATA / f"{name}_stats.npy").exists():
            shutil.copyfile(DATA / f"{name}_stats.npy", DATA / f"{out_name}_stats.npy")
        out_lr = RowWriter(DATA / f"{out_name}_lr.npy", (len(rows),) + lr.shape[1:])
        out_hr = RowWriter(DATA / f"{out_name}_hr.npy", (len(rows), lr.shape[1] * 2, lr.shape[2] * 2, 3))
        for k in range(0, len(rows), BATCH):
            chunk = np.stack([lr[int(r)] for r in rows[k:k + BATCH]])
            out_lr[k:k + len(chunk)] = chunk
            out_hr[k:k + len(chunk)] = ensemble(teacher, chunk, device)
            if k % (BATCH * 100) == 0:
                print(f"{out_name}: {k}/{len(rows)}", flush=True)
        out_lr.close(); out_hr.close()
        print(f"{out_name}: {len(rows)} crops labelled by the 8-way ensemble", flush=True)


if __name__ == "__main__":
    main()
