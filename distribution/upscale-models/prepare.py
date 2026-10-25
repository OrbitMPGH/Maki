#!/usr/bin/env python3
"""
Builds the upscale model release folder for Maki's server-side Page Enhance.

Requirements (Python 3, nothing else): torch, onnx, onnxruntime, numpy.
    python -c "import torch, onnx, onnxruntime, numpy"

What it does:
  1. Downloads the three upstream files into --work and checks their sha256 against the pinned
     values below. A mismatch stops the run before anything is built.
  2. Extracts SPAN-S from the MangaJaNai zip and names it span-s-2x.onnx.
  3. Rebuilds SRVGGNetCompact, loads realesr-animevideov3.pth with weights_only=True, exports
     realesrgan-av3-4x.onnx (opset 17, fp32, dynamic H and W) and compares ONNX Runtime with
     PyTorch on a 128x128 random tile (max abs difference must stay under 1e-3).
  4. Copies the waifu2x swin_unet model as waifu2x-art-2x.onnx.
  5. Writes manifest.json (sha256 and size of each file) and LICENSES.txt into --out.

Upload the result with distribution/publish-upscale-models.ps1.

Usage:
    python distribution/upscale-models/prepare.py --work <download folder> --out <output folder>
"""

import argparse
import hashlib
import json
import shutil
import sys
import urllib.request
import zipfile
from pathlib import Path

import numpy as np
import onnxruntime as ort
import torch
import torch.nn as nn

HERE = Path(__file__).resolve().parent

SPAN_ZIP = {
    "name": "IllustrationJaNai_V3denoise_onnx.zip",
    "url": "https://github.com/the-database/MangaJaNai/releases/download/3.0.0/IllustrationJaNai_V3denoise_onnx.zip",
    "sha256": "86d393d5c55a7ac0df986cd42e79a0351ecf89b370752f79d4fd0a9cf3f2aa58",
}
SPAN_MEMBER = {
    "name": "2x_IllustrationJaNai_V3denoise_SPAN_S_30k_fp16_1x3xHxW_dyn-HW_strong_fp16_op23.onnx",
    "sha256": "cd0344ebae36281206827954b730e0f944ce961dbbe059a41a083014f3e7c03a",
}
WAIFU2X = {
    "name": "noise1_scale2x.onnx",
    "url": "https://unlimited.waifu2x.net/models/swin_unet/art/noise1_scale2x.onnx",
    "sha256": "ac5d366c5ca73c58c40a0ecd7199298521867c9d374a27d57a4bd67a241c4885",
}
REALESRGAN = {
    "name": "realesr-animevideov3.pth",
    "url": "https://github.com/xinntao/Real-ESRGAN/releases/download/v0.2.5.0/realesr-animevideov3.pth",
    "sha256": "b8a8376811077954d82ca3fcf476f1ac3da3e8a68a4f4d71363008000a18b75d",
}

MODELS = [
    {
        "id": "span-s",
        "file": "span-s-2x.onnx",
        "license": "CC BY-NC 4.0",
        "author": "the-database (IllustrationJaNai V3)",
        "source": "https://github.com/the-database/MangaJaNai/releases/tag/3.0.0",
    },
    {
        "id": "realesrgan-av3",
        "file": "realesrgan-av3-4x.onnx",
        "license": "BSD-3-Clause",
        "author": "Xintao Wang et al. (Real-ESRGAN animevideov3)",
        "source": "https://github.com/xinntao/Real-ESRGAN/releases/tag/v0.2.5.0",
    },
    {
        "id": "waifu2x-art",
        "file": "waifu2x-art-2x.onnx",
        "license": "MIT (code); the weights carry no licence beyond the repository's MIT",
        "author": "nagadomi (waifu2x, nunif)",
        "source": "https://github.com/nagadomi/nunif",
    },
]


def sha256_of(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def fail(message: str) -> None:
    print(f"error: {message}", file=sys.stderr)
    sys.exit(1)


def fetch(entry: dict, work: Path) -> Path:
    path = work / entry["name"]
    if not path.exists():
        print(f"downloading {entry['url']}")
        request = urllib.request.Request(entry["url"], headers={"User-Agent": "Maki-prepare-upscale-models"})
        with urllib.request.urlopen(request) as response, path.with_suffix(path.suffix + ".part").open("wb") as out:
            shutil.copyfileobj(response, out)
        path.with_suffix(path.suffix + ".part").replace(path)
    actual = sha256_of(path)
    if actual != entry["sha256"]:
        fail(f"{path.name}: sha256 {actual} does not match the pinned {entry['sha256']}")
    print(f"verified {path.name}")
    return path


class Compact(nn.Module):
    """SRVGGNetCompact: conv/PReLU stack, pixel shuffle, plus a nearest-upsampled copy of the input."""

    def __init__(self, num_conv: int, scale: int):
        super().__init__()
        self.scale = scale
        layers = [nn.Conv2d(3, 64, 3, 1, 1), nn.PReLU(64)]
        for _ in range(num_conv):
            layers += [nn.Conv2d(64, 64, 3, 1, 1), nn.PReLU(64)]
        layers += [nn.Conv2d(64, 3 * scale * scale, 3, 1, 1), nn.PixelShuffle(scale)]
        self.body = nn.ModuleList(layers)

    def forward(self, x):
        out = x
        for layer in self.body:
            out = layer(out)
        return out + nn.functional.interpolate(x, scale_factor=self.scale, mode="nearest")


def export_realesrgan(pth: Path, target: Path) -> float:
    state = torch.load(pth, map_location="cpu", weights_only=True)
    print("state dict top-level keys:", list(state.keys())[:4])
    state = state.get("params", state)
    convs = sum(1 for k in state if k.endswith(".weight") and state[k].dim() == 4)
    model = Compact(convs - 2, 4)
    model.load_state_dict(state)
    model.eval()
    print(f"rebuilt SRVGGNetCompact: {convs - 2} body convs, scale 4")

    sample = torch.rand(1, 3, 64, 64)
    torch.onnx.export(
        model,
        (sample,),
        str(target),
        input_names=["input"],
        output_names=["output"],
        dynamic_axes={"input": {2: "height", 3: "width"}, "output": {2: "out_height", 3: "out_width"}},
        opset_version=17,
        dynamo=False,
    )

    options = ort.SessionOptions()
    session = ort.InferenceSession(str(target), options, providers=["CPUExecutionProvider"])
    name = session.get_inputs()[0].name
    if name != "input":
        fail(f"exported input is named {name}, expected input")
    if session.get_inputs()[0].type != "tensor(float)":
        fail(f"exported input type is {session.get_inputs()[0].type}, expected tensor(float)")

    rng = np.random.default_rng(1234)
    tile = rng.random((1, 3, 128, 128), dtype=np.float32)
    with torch.no_grad():
        expected = model(torch.from_numpy(tile)).numpy()
    actual = session.run(None, {"input": tile})[0]
    if actual.shape != expected.shape:
        fail(f"ONNX output shape {actual.shape} differs from PyTorch {expected.shape}")
    diff = float(np.abs(actual - expected).max())
    print(f"ORT vs PyTorch on a 128x128 tile: max abs diff {diff:.3e}")
    if diff >= 1e-3:
        fail(f"max abs difference {diff} is not under 1e-3")
    return diff


def licenses_text() -> str:
    def read(name: str) -> str:
        return (HERE / "licenses" / name).read_text(encoding="utf-8").strip()

    sections = [
        "Maki upscale models: licences and attribution",
        "=" * 70,
        "These model files are separate from Maki's code (GPL-3.0). Each keeps the licence of its\n"
        "own author, listed below. The SPAN-S file is licensed for non-commercial use only, and\n"
        "that term binds anyone who receives the file.",
        "",
        "1. span-s-2x.onnx",
        "   IllustrationJaNai V3 denoise, SPAN-S 2x, by the-database (MangaJaNai).",
        "   Source: https://github.com/the-database/MangaJaNai/releases/tag/3.0.0",
        "   Licence: Creative Commons Attribution-NonCommercial 4.0 International (full text below).",
        "",
        "2. realesrgan-av3-4x.onnx",
        "   Real-ESRGAN realesr-animevideov3 (SRVGGNetCompact, 4x), by Xintao Wang et al.",
        "   Converted to ONNX (opset 17, fp32, dynamic height and width) without changing the weights.",
        "   Source: https://github.com/xinntao/Real-ESRGAN/releases/tag/v0.2.5.0",
        "   Licence: BSD 3-Clause (full text below).",
        "",
        "3. waifu2x-art-2x.onnx",
        "   waifu2x swin_unet art noise1 2x, by nagadomi, from https://unlimited.waifu2x.net/",
        "   Source: https://github.com/nagadomi/nunif",
        "   Licence: MIT (full text below). The repository's MIT licence covers the code. The model",
        "   weights carry no licence of their own beyond that, and are included here as published.",
        "",
        "-" * 70,
        "Real-ESRGAN: BSD 3-Clause",
        "-" * 70,
        read("BSD-3-Clause-Real-ESRGAN.txt"),
        "",
        "-" * 70,
        "waifu2x (nunif): MIT",
        "-" * 70,
        read("MIT-waifu2x.txt"),
        "",
        "-" * 70,
        "SPAN-S (IllustrationJaNai V3): Creative Commons Attribution-NonCommercial 4.0 International",
        "-" * 70,
        read("CC-BY-NC-4.0.txt"),
        "",
    ]
    return "\n".join(sections)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--work", required=True, type=Path, help="folder for the upstream downloads")
    parser.add_argument("--out", required=True, type=Path, help="folder that receives the release files")
    args = parser.parse_args()

    work: Path = args.work
    out: Path = args.out
    work.mkdir(parents=True, exist_ok=True)
    out.mkdir(parents=True, exist_ok=True)

    zip_path = fetch(SPAN_ZIP, work)
    w2x_path = fetch(WAIFU2X, work)
    pth_path = fetch(REALESRGAN, work)

    span_out = out / "span-s-2x.onnx"
    with zipfile.ZipFile(zip_path) as archive:
        members = [n for n in archive.namelist() if n.endswith(SPAN_MEMBER["name"])]
        if len(members) != 1:
            fail(f"expected one {SPAN_MEMBER['name']} in the zip, found {len(members)}")
        with archive.open(members[0]) as src, span_out.open("wb") as dst:
            shutil.copyfileobj(src, dst)
    if sha256_of(span_out) != SPAN_MEMBER["sha256"]:
        fail(f"extracted SPAN-S sha256 {sha256_of(span_out)} does not match the pinned {SPAN_MEMBER['sha256']}")
    print("extracted span-s-2x.onnx")

    export_realesrgan(pth_path, out / "realesrgan-av3-4x.onnx")

    shutil.copyfile(w2x_path, out / "waifu2x-art-2x.onnx")

    manifest = {"version": 1, "models": []}
    for model in MODELS:
        file = out / model["file"]
        manifest["models"].append(
            {
                "id": model["id"],
                "file": model["file"],
                "sha256": sha256_of(file),
                "bytes": file.stat().st_size,
                "license": model["license"],
                "author": model["author"],
                "source": model["source"],
            }
        )
    (out / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    (out / "LICENSES.txt").write_text(licenses_text(), encoding="utf-8")

    for entry in manifest["models"]:
        print(f"{entry['file']}: {entry['bytes']} bytes, sha256 {entry['sha256']}")
    print(f"wrote manifest.json and LICENSES.txt to {out}")


if __name__ == "__main__":
    main()
