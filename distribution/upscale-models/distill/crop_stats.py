"""
Per-crop numbers that let a trainer sample flat-fill crops on purpose: input luma std and mean, and the
share of the teacher target inside measure.py's flat and fill masks. Writes {name}_stats.npy with one
row of (std, mean, flat, fill) per crop. A damaged set (`*_served_*`) uses its source set's rows.

    python crop_stats.py col2_train col2_val col_train col_val
"""

import sys

import numpy as np
import torch

from measure import fill_mask, flat_mask
from sets import DATA, open_set

FILL = 0.5


def compute(name: str) -> np.ndarray:
    device = torch.device("cuda")
    weights = torch.tensor([0.299, 0.587, 0.114], device=device)
    lr, hr = open_set(name)
    rows = []
    for i in range(0, len(lr), 128):
        x = torch.from_numpy(np.ascontiguousarray(lr[i:i + 128])).to(device).float() @ weights
        y = torch.from_numpy(np.ascontiguousarray(hr[i:i + 128])).to(device).float() @ weights
        for a, b in zip(x, y):
            rows.append((a.std().item(), a.mean().item(), flat_mask(b, b)[0].float().mean().item(),
                         fill_mask(b).float().mean().item()))
    stats = np.array(rows, np.float32)
    np.save(DATA / f"{name}_stats.npy", stats)
    return stats


def source_of(name: str) -> str:
    return "train" if name == "served_train" else "val" if name == "served_val" else name.replace("_served_", "_")


def stats(name: str, rows: int) -> np.ndarray:
    """Rows for a set of `rows` crops; a damaged set's row k is a copy of source row k % len(source)."""
    path = DATA / f"{name}_stats.npy"
    if not path.exists():
        path = DATA / f"{source_of(name)}_stats.npy"
    table = np.load(path)
    return table[np.arange(rows) % len(table)]


def is_fill(table: np.ndarray) -> np.ndarray:
    return table[:, 3] >= FILL


if __name__ == "__main__":
    for set_name in sys.argv[1:]:
        s = compute(set_name)
        print(f"{set_name}: {len(s)} crops, {is_fill(s).sum()} mostly fill", flush=True)
