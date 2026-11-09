"""
Labels every clean volume crop with what a teacher makes of a clean half-size copy of it. Training
then shows the student a damaged copy of the same crop and asks for that result, so it learns to give
the teacher's best output from a bad source. One teacher per label set: averaging two teachers that
disagree (Real-ESRGAN flattens screentone, DAT2 keeps it) taught round two a faint grid.

    python label_clean.py realesrgan
    python label_clean.py dat2
"""

import argparse

import numpy as np
import spandrel
import torch
from PIL import Image

from common import WORK, halve, load_teacher, to_byte

DAT2 = "IllustrationJaNai_V3detail/4x_IllustrationJaNai_V3detail_DAT2_*.safetensors"


def clean_half(targets: np.ndarray) -> np.ndarray:
    return np.stack([np.asarray(Image.fromarray(t).resize((t.shape[1] // 2, t.shape[0] // 2), Image.Resampling.LANCZOS))
                     for t in targets])


def load(teacher: str, device):
    if teacher == "realesrgan":
        return load_teacher(device), 4, torch.float32
    model = spandrel.ModelLoader().load_from_file(str(next((WORK / "teachers").glob(DAT2))))
    return model.model.eval().to(device), model.scale, torch.bfloat16


def label(model, scale, dtype, inputs: np.ndarray, device, batch: int = 8) -> np.ndarray:
    out = []
    for i in range(0, len(inputs), batch):
        x = torch.from_numpy(inputs[i:i + batch]).to(device).permute(0, 3, 1, 2).float() / 255
        with torch.no_grad(), torch.autocast("cuda", dtype=dtype, enabled=dtype != torch.float32):
            y = to_byte(model(x).float()).permute(0, 2, 3, 1).cpu().numpy()
        out.extend(halve(t) if scale == 4 else t for t in y)
    return np.stack(out)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("teacher", choices=["realesrgan", "dat2"])
    args = parser.parse_args()
    device = torch.device("cuda")
    model, scale, dtype = load(args.teacher, device)
    data = WORK / "data"
    for prefix in ("", "vol_"):
        for split in ("train", "val"):
            source = data / f"{prefix}gt_{split}.npz"
            if not source.exists():
                continue
            # The builder wrote each clean target once per damaged variant, consecutively.
            targets = np.ascontiguousarray(np.load(source)["hr"][::2])
            labels = label(model, scale, dtype, clean_half(targets), device)
            np.savez(data / f"{prefix}gt_{split}_clean_{args.teacher}.npz", target=labels)
            print(f"{prefix}gt_{split}: {len(labels)} clean crops labelled by {args.teacher}", flush=True)


if __name__ == "__main__":
    main()
