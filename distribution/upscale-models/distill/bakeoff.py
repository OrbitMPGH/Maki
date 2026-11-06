"""
Runs candidate teachers on the same held-out pages and writes side-by-side crops, so the owner can
pick the look a student should learn. Every output goes through Maki's colour pass, so the crops are
what a reader would see; 4x models are halved with Lanczos like Maki's Plain4xThenHalve.

Weights are loaded with spandrel (safetensors or weights-only pickles) and onnxruntime; nothing
downloaded is ever imported or executed. Candidates and their licences:

  real-esrgan      Real-ESRGAN animevideov3, BSD-3-Clause (what Maki serves today, the round-one teacher)
  span-s           IllustrationJaNai V3 SPAN-S, CC BY-NC 4.0 (what Maki ships as its other model)
  mangajanai       MangaJaNai V1 2x ESRGAN, picked by page height, CC BY-NC 4.0 (black-and-white manga)
  ijanai-hat       IllustrationJaNai V3 detail HAT-L 4x, CC BY-NC 4.0
  ijanai-dat2      IllustrationJaNai V3 detail DAT2 4x, CC BY-NC 4.0
  waifu2x          waifu2x swin_unet art_scan noise1 2x, MIT

    python bakeoff.py --pages 8
"""

import argparse
import json
import re
from pathlib import Path

import numpy as np
import onnxruntime as ort
import spandrel
import torch
from PIL import Image, ImageFilter

from common import MAIN, WORK, halve, load_teacher, student, to_byte
from evaluate import busiest_window, luma_u8, take_colour_from

TEACHERS = WORK / "teachers"
SPAN_ONNX = MAIN / ".simulated" / "models" / "upscale" / "span-s-2x.onnx"


def reflect(page: np.ndarray, y0: int, y1: int, x0: int, x1: int) -> np.ndarray:
    h, w = page.shape[:2]

    def idx(a, b, n):
        i = np.arange(a, b)
        period = 2 * (n - 1)
        i = np.mod(i, period)
        return np.where(i < n, i, period - i)

    return page[idx(y0, y1, h)][:, idx(x0, x1, w)]


def tiled(run, page: np.ndarray, scale: int, tile: int, pad: int) -> np.ndarray:
    """`run` maps an HWC uint8 context tile to its HWC uint8 output at `scale`."""
    h, w = page.shape[:2]
    out = np.zeros((h * scale, w * scale, 3), np.uint8)
    for y in range(0, h, tile):
        for x in range(0, w, tile):
            th, tw = min(tile, h - y), min(tile, w - x)
            # Every tile has the full shape, reflected past the page edge: U-Nets need aligned sizes.
            context = reflect(page, y - pad, y + tile + pad, x - pad, x + tile + pad)
            result = run(context)
            out[y * scale:(y + th) * scale, x * scale:(x + tw) * scale] = \
                result[pad * scale:(pad + th) * scale, pad * scale:(pad + tw) * scale]
    return out


def torch_runner(model, device, dtype):
    def run(context):
        with torch.no_grad(), torch.autocast("cuda", dtype=dtype, enabled=dtype != torch.float32):
            x = torch.from_numpy(np.ascontiguousarray(context)).to(device).permute(2, 0, 1)[None].float() / 255
            return to_byte(model(x).float())[0].permute(1, 2, 0).cpu().numpy()
    return run


def onnx_runner(session, half: bool, trim: int = 0):
    def run(context):
        if trim:
            context = np.pad(context, ((trim, trim), (trim, trim), (0, 0)), mode="reflect")
        x = (context.astype(np.float32) / 255).transpose(2, 0, 1)[None]
        y = session.run(None, {session.get_inputs()[0].name: x.astype(np.float16 if half else np.float32)})[0][0]
        return np.clip(np.floor(y.astype(np.float32) * 255 + 0.5), 0, 255).astype(np.uint8).transpose(1, 2, 0)
    return run


def mangajanai_for(height: int) -> Path:
    files = sorted((TEACHERS / "MangaJaNai_V1_ModelsOnly").glob("2x_MangaJaNai_*p_V1_*.pth"))
    by_height = {int(re.search(r"_(\d+)p_", f.name).group(1)): f for f in files}
    return by_height[min(by_height, key=lambda h: abs(h - height))]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--pages", type=int, default=8)
    parser.add_argument("--crop-size", type=int, default=360)
    parser.add_argument("--only", nargs="*", help="teacher names to run")
    parser.add_argument("--also", nargs="*", default=[], help="extra page images to include, e.g. a known bad source")
    args = parser.parse_args()

    device = torch.device("cuda")
    loader = spandrel.ModelLoader()

    def spandrel_model(path):
        return loader.load_from_file(str(path)).model.eval().to(device)

    teachers = {
        "real-esrgan": lambda page: halve(tiled(torch_runner(load_teacher(device), device, torch.float32), page, 4, 192, 24)),
        "span-s": lambda page: tiled(onnx_runner(ort.InferenceSession(str(SPAN_ONNX), providers=["CPUExecutionProvider"]), True), page, 2, 96, 12),
        "mangajanai": lambda page: tiled(torch_runner(spandrel_model(mangajanai_for(page.shape[0])), device, torch.float16), page, 2, 256, 24),
        "ijanai-hat": lambda page: halve(tiled(torch_runner(spandrel_model(next((TEACHERS / "IllustrationJaNai_V3detail").glob("*HAT_L*"))), device, torch.bfloat16), page, 4, 128, 24)),
        "ijanai-dat2": lambda page: halve(tiled(torch_runner(spandrel_model(next((TEACHERS / "IllustrationJaNai_V3detail").glob("*DAT2*"))), device, torch.bfloat16), page, 4, 128, 24)),
        "ijanai-dat2-denoise": lambda page: halve(tiled(torch_runner(spandrel_model(next((TEACHERS / "IllustrationJaNai_V3denoise").glob("*DAT2*"))), device, torch.bfloat16), page, 4, 128, 24)),
        "waifu2x": lambda page: tiled(onnx_runner(ort.InferenceSession(str(TEACHERS / "waifu2x_onnx_20250502/onnx_models/swin_unet/art_scan/noise1_scale2x.onnx"), providers=["CPUExecutionProvider"]), False, 8), page, 2, 112, 16),
    }
    for checkpoint in sorted(WORK.glob("student-*.pt")):
        spec = checkpoint.stem.removeprefix("student-")

        def student_run(page, checkpoint=checkpoint, spec=spec):
            model = student(spec.split("-")[0])
            model.load_state_dict(torch.load(checkpoint, map_location="cpu", weights_only=True))
            return tiled(torch_runner(model.eval().to(device), device, torch.float32), page, 2, 96, 12)

        teachers[f"student-{spec}"] = student_run
    names = args.only or list(teachers)
    out = WORK / "bakeoff"
    out.mkdir(exist_ok=True)
    pages = sorted((WORK / "data" / "val_pages").glob("*.png"))[:args.pages] + [Path(p) for p in args.also]
    index = []
    index_file = out / "index.json"
    index_on_disk = json.loads(index_file.read_text()) if index_file.exists() else []
    for number, path in enumerate(pages):
        page = np.asarray(Image.open(path).convert("RGB"))
        h, w = page.shape[:2]
        source_2x = np.asarray(Image.fromarray(page).resize((w * 2, h * 2), Image.LANCZOS))
        results = {"lanczos": source_2x}
        for name in names:
            print(f"page {number} ({w}x{h}): {name}", flush=True)
            results[name] = take_colour_from(teachers[name](page), source_2x)
        # Anchored on the source's own detail, so runs with different teachers crop the same region.
        blurred = np.asarray(Image.fromarray(source_2x).filter(ImageFilter.BoxBlur(3)))
        y, x = busiest_window(source_2x, blurred, args.crop_size)
        files = {}
        for name, image in results.items():
            file = f"{number:02}_{name}.png"
            Image.fromarray(image[y:y + args.crop_size, x:x + args.crop_size]).save(out / file)
            files[name] = file
        previous = next((e for e in index_on_disk if e["page"] == path.name), None)
        if previous:
            files = {**previous["crops"], **files}
        index.append({"page": path.name, "size": [w, h], "crops": files,
                      "grey": bool((page[..., 0] == page[..., 1]).all() and (page[..., 1] == page[..., 2]).all())})
    index_file.write_text(json.dumps(index, indent=1))
    print(f"wrote {len(index)} pages to {out}")


if __name__ == "__main__":
    main()
