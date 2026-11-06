"""
Crop sets on disk as plain .npy files ({name}_lr.npy, {name}_hr.npy, optional {name}_count.npy), so they
can be memory-mapped and moved to the GPU a chunk at a time. A .npz is a zip: reading one array pulls
all of it into memory, which for a served set is 5 GB.

    python sets.py convert train val served_train served_val
"""

import sys
import zipfile

import numpy as np
import torch

from common import WORK

DATA = WORK / "data"
CHUNK = 1024


class DiskArray:
    """Rows of an .npy file read on demand with ordinary file reads. A memory map would do the same job,
    but on Windows every page it touches stays in the process's working set; reads go through the file
    cache, which the system gives back as soon as anything else wants the memory."""

    def __init__(self, path, rows: int | None = None):
        self.path = path
        with open(path, "rb") as f:
            version = np.lib.format.read_magic(f)
            read = np.lib.format.read_array_header_1_0 if version == (1, 0) else np.lib.format.read_array_header_2_0
            shape, fortran, self.dtype = read(f)
            self.offset = f.tell()
        assert not fortran
        self.shape = (min(shape[0], rows) if rows is not None else shape[0],) + tuple(shape[1:])
        self.row_bytes = int(np.prod(self.shape[1:])) * self.dtype.itemsize

    def __len__(self):
        return self.shape[0]

    def __getitem__(self, index):
        if isinstance(index, slice):
            start, stop, step = index.indices(len(self))
            assert step == 1
            count = max(0, stop - start)
            with open(self.path, "rb") as f:
                f.seek(self.offset + start * self.row_bytes)
                data = np.frombuffer(f.read(count * self.row_bytes), self.dtype)
            return data.reshape((count,) + self.shape[1:])
        return self[index:index + 1][0]


class RowWriter:
    """Writes rows into a pre-sized .npy file with ordinary file writes, for the same reason DiskArray
    reads that way: a writable memory map keeps every page it has written in the process's memory."""

    def __init__(self, path, shape: tuple, dtype=np.uint8):
        np.lib.format.open_memmap(path, mode="w+", dtype=dtype, shape=shape).flush()
        self.reader = DiskArray(path)
        self.file = open(path, "r+b")

    def __len__(self):
        return len(self.reader)

    def __setitem__(self, index, rows):
        rows = np.ascontiguousarray(rows, dtype=self.reader.dtype)
        start = index.start if isinstance(index, slice) else index
        self.file.seek(self.reader.offset + start * self.reader.row_bytes)
        self.file.write(rows.tobytes())

    def flush(self):
        self.file.flush()

    def close(self):
        self.file.close()


def open_set(name: str) -> tuple[DiskArray, DiskArray]:
    """(lr, hr) read on demand, trimmed to the count a capped builder wrote."""
    count_file = DATA / f"{name}_count.npy"
    n = int(np.load(count_file)) if count_file.exists() else None
    return DiskArray(DATA / f"{name}_lr.npy", n), DiskArray(DATA / f"{name}_hr.npy", n)


def to_gpu(array: np.ndarray, device, luma_only: bool = False, trim: int = 0) -> torch.Tensor:
    """NHWC uint8 on disk → NCHW uint8 on the GPU, one chunk at a time. With `luma_only` the result is
    N1HW: Maki keeps only the model's luma (BT.601, as `common.luma`), so targets need nothing else.
    `trim` drops that many pixels from every edge."""
    n, h, w, _ = array.shape
    h, w = h - 2 * trim, w - 2 * trim
    out = torch.empty((n, 1 if luma_only else 3, h, w), dtype=torch.uint8, device=device)
    weights = torch.tensor([0.299, 0.587, 0.114], device=device)
    for i in range(0, n, CHUNK):
        chunk = torch.from_numpy(np.ascontiguousarray(array[i:i + CHUNK])).to(device)
        if trim:
            chunk = chunk[:, trim:trim + h, trim:trim + w]
        if luma_only:
            out[i:i + CHUNK, 0] = (chunk.float() @ weights).round().clamp(0, 255).byte()
        else:
            out[i:i + CHUNK] = chunk.permute(0, 3, 1, 2)
    return out


def convert(name: str) -> None:
    """{name}.npz → {name}_lr.npy and {name}_hr.npy, streamed out of the zip without loading an array."""
    with zipfile.ZipFile(DATA / f"{name}.npz") as z:
        for key in ("lr", "hr"):
            with z.open(f"{key}.npy") as member:
                version = np.lib.format.read_magic(member)
                read = np.lib.format.read_array_header_1_0 if version == (1, 0) else np.lib.format.read_array_header_2_0
                shape, fortran, dtype = read(member)
                assert not fortran
                target = np.lib.format.open_memmap(DATA / f"{name}_{key}.npy", mode="w+", dtype=dtype, shape=shape)
                flat = target.reshape(-1).view(np.uint8)
                done = 0
                while done < flat.size:
                    block = member.read(64 << 20)
                    if not block:
                        break
                    flat[done:done + len(block)] = np.frombuffer(block, np.uint8)
                    done += len(block)
                assert done == flat.size, f"{name}/{key}: {done} of {flat.size} bytes"
                target.flush()
                del target, flat
        print(f"{name}: converted", flush=True)


if __name__ == "__main__":
    if sys.argv[1] == "convert":
        for n in sys.argv[2:]:
            convert(n)
