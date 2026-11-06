"""
Grades students on whole held-out pages against what Maki serves from Real-ESRGAN, exports each to
ONNX, and writes side-by-side crops. Every output goes through Maki's colour pass
(`UpscalePixels.TakeColourFrom`: the model's luma over the Lanczos-resized source), so the images are
what a reader would see.

    python evaluate.py c32n8 c32n12 --span path/to/span-s-2x.onnx
"""

import argparse
import math
from pathlib import Path

import numpy as np
import onnxruntime as ort
import torch
from PIL import Image

from common import WORK, halve, load_teacher, student, to_byte


def luma_u8(rgb: np.ndarray) -> np.ndarray:
    r, g, b = (rgb[..., i].astype(np.int32) for i in range(3))
    return (299 * r + 587 * g + 114 * b + 500) // 1000


def take_colour_from(model_rgb: np.ndarray, source_2x: np.ndarray) -> np.ndarray:
    shift = luma_u8(model_rgb) - luma_u8(source_2x)
    return np.clip(source_2x.astype(np.int32) + shift[..., None], 0, 255).astype(np.uint8)


def psnr(a: np.ndarray, b: np.ndarray) -> float:
    mse = ((a.astype(np.float64) - b.astype(np.float64)) ** 2).mean()
    return 10 * math.log10(255 ** 2 / max(mse, 1e-10))


def run_torch(model, page: np.ndarray, device) -> np.ndarray:
    with torch.no_grad():
        x = torch.from_numpy(page).to(device).permute(2, 0, 1)[None].float() / 255
        return to_byte(model(x))[0].permute(1, 2, 0).cpu().numpy()


def run_span(session, page: np.ndarray) -> np.ndarray:
    x = (page.astype(np.float32) / 255).transpose(2, 0, 1)[None].astype(np.float16)
    y = session.run(None, {session.get_inputs()[0].name: x})[0][0].astype(np.float32)
    return np.clip(np.floor(y * 255 + 0.5), 0, 255).astype(np.uint8).transpose(1, 2, 0)


def export(model, spec: str) -> Path:
    path = WORK / "onnx" / f"student-{spec}.onnx"
    path.parent.mkdir(exist_ok=True)
    cpu = student(spec.split("-")[0])
    cpu.load_state_dict(model.state_dict())
    torch.onnx.export(cpu.eval(), torch.rand(1, 3, 64, 64), str(path), input_names=["input"], output_names=["output"],
                      opset_version=17, dynamo=False,
                      dynamic_axes={"input": {2: "height", 3: "width"}, "output": {2: "out_height", 3: "out_width"}})
    return path


def busiest_window(reference: np.ndarray, plain: np.ndarray, size: int) -> tuple[int, int]:
    """Where the reference differs most from plain Lanczos: the region enhancement visibly changes."""
    diff = np.abs(luma_u8(reference) - luma_u8(plain)).astype(np.float64)
    h, w = diff.shape
    step = size // 2
    best, at = -1.0, (0, 0)
    for y in range(0, max(1, h - size), step):
        for x in range(0, max(1, w - size), step):
            score = diff[y:y + size, x:x + size].mean()
            if score > best:
                best, at = score, (y, x)
    return at


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("specs", nargs="+")
    parser.add_argument("--span", help="SPAN-S ONNX file, shown and scored for reference")
    parser.add_argument("--crops", type=int, default=6)
    parser.add_argument("--crop-size", type=int, default=360)
    args = parser.parse_args()

    device = torch.device("cuda")
    teacher = load_teacher(device)
    students = {}
    for spec in args.specs:
        model = student(spec.split("-")[0])
        model.load_state_dict(torch.load(WORK / f"student-{spec}.pt", map_location="cpu", weights_only=True))
        students[spec] = model.eval().to(device)
        print(f"exported {export(model, spec)}")
    span = ort.InferenceSession(args.span, providers=["CPUExecutionProvider"]) if args.span else None

    pages = sorted((WORK / "data" / "val_pages").glob("*.png"))
    names = ["lanczos"] + (["span-s"] if span else []) + [f"student {s}" for s in args.specs]
    scores = {name: [] for name in names}
    crops_dir = WORK / "compare"
    crops_dir.mkdir(exist_ok=True)
    for index, path in enumerate(pages):
        page = np.asarray(Image.open(path).convert("RGB"))
        h, w = page.shape[:2]
        source_2x = np.asarray(Image.fromarray(page).resize((w * 2, h * 2), Image.LANCZOS))
        reference = take_colour_from(halve(run_torch(teacher, page, device)), source_2x)
        outputs = {"lanczos": source_2x}
        if span:
            outputs["span-s"] = take_colour_from(run_span(span, page), source_2x)
        for spec, model in students.items():
            outputs[f"student {spec}"] = take_colour_from(run_torch(model, page, device), source_2x)
        for name in names:
            scores[name].append(psnr(luma_u8(outputs[name]), luma_u8(reference)))
        if index < args.crops:
            y, x = busiest_window(reference, source_2x, args.crop_size)
            tiles = [("real-esrgan", reference)] + [(n, outputs[n]) for n in names]
            for label, image in tiles:
                Image.fromarray(image[y:y + args.crop_size, x:x + args.crop_size]).save(
                    crops_dir / f"{index:02}_{label.replace(' ', '-')}.png")

    print(f"\n{len(pages)} held-out pages, luma PSNR against what Maki serves from Real-ESRGAN (higher is closer):")
    for name in names:
        values = np.array(scores[name])
        print(f"  {name:16} mean {values.mean():6.2f} dB   worst {values.min():6.2f} dB")


if __name__ == "__main__":
    main()
