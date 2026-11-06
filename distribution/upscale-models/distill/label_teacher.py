"""
Labels the library crops with a second teacher, so training can blend teachers without rebuilding the
set. A 4x teacher is rounded to bytes and Lanczos-halved like Maki's Plain4xThenHalve.

    python label_teacher.py dat2
"""

import argparse

import numpy as np
import spandrel
import torch

from common import WORK, halve, to_byte

TEACHERS = {
    "dat2": "IllustrationJaNai_V3detail/4x_IllustrationJaNai_V3detail_DAT2_*.safetensors",
}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("teacher", choices=sorted(TEACHERS))
    parser.add_argument("--batch", type=int, default=8)
    parser.add_argument("--prefix", default="")
    args = parser.parse_args()

    device = torch.device("cuda")
    path = next((WORK / "teachers").glob(TEACHERS[args.teacher]))
    descriptor = spandrel.ModelLoader().load_from_file(str(path))
    model = descriptor.model.eval().to(device)
    scale = descriptor.scale
    for split in ("train", "val"):
        source = WORK / "data" / f"{args.prefix}{split}.npz"
        if not source.exists():
            continue
        lr = np.load(source)["lr"]
        out = []
        for i in range(0, len(lr), args.batch):
            x = torch.from_numpy(lr[i:i + args.batch]).to(device).permute(0, 3, 1, 2).float() / 255
            with torch.no_grad(), torch.autocast("cuda", dtype=torch.bfloat16):
                y = to_byte(model(x).float()).permute(0, 2, 3, 1).cpu().numpy()
            out.extend(halve(t) if scale == 4 else t for t in y)
            if i % (args.batch * 100) == 0:
                print(f"{split}: {i}/{len(lr)}", flush=True)
        np.savez(WORK / "data" / f"{args.prefix}{split}_{args.teacher}.npz", hr=np.stack(out))
        print(f"{split}: {len(out)} crops labelled by {args.teacher}")


if __name__ == "__main__":
    main()
