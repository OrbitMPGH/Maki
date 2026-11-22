#!/usr/bin/env python3
"""
Builds the bundled src/Maki.Api/UpscaleModels/maki-2x-hq.onnx from the uncropped export (the file
distill/export_specialists.py wrote for run col48b, sha256 1457823...).

Maki 2x HQ is a plain chain of eighteen padded 3x3 convs, a DepthToSpace and a nearest 2x Resize of the
input added at the end. Maki runs each tile with a 12 px context pad and keeps only the interior. A kept
pixel sits 12 px in, so the zero padding of the last twelve convs never reaches it: those convs run
unpadded and the window shrinks by one px per side at each, ending on the interior. A Slice on the
residual input crops the Resize branch to the same window. The sums that reach the kept pixels are
unchanged, so ONNX Runtime gives bit-identical values.

The file records the crop as the metadata prop maki.context_crop, read by OnnxPageUpscaler, so the pad
is baked into the file: changing UpscaleTiling.ContextPad means rebuilding this file with the new --pad.

Needs onnx, onnxruntime and numpy.
    python distribution/upscale-models/crop_maki_2x_hq.py <uncropped maki-2x-hq.onnx> src/Maki.Api/UpscaleModels/maki-2x-hq.onnx
"""

import argparse
import hashlib
import sys
from pathlib import Path

import numpy as np
import onnx
import onnxruntime as ort
from onnx import helper, numpy_helper

CROP_KEY = "maki.context_crop"
SOURCE_SHA256 = "1457823558c8288057483dfc6723de6c223627c9e7e01c0145d0a197e72a0427"
CONVS = [f"/body.{2 * i}/Conv" for i in range(18)]


def fail(message: str) -> None:
    print(f"error: {message}", file=sys.stderr)
    sys.exit(1)


def node(graph, name):
    found = [n for n in graph.node if n.name == name]
    if len(found) != 1:
        fail(f"expected one node {name}, found {len(found)}: not the maki-2x-hq graph this script knows")
    return found[0]


def check_graph(graph):
    convs = [n.name for n in graph.node if n.op_type == "Conv"]
    if convs != CONVS:
        fail(f"convs {convs} are not the maki-2x-hq chain")
    for name in CONVS:
        attrs = {a.name: helper.get_attribute_value(a) for a in node(graph, name).attribute}
        if attrs.get("kernel_shape") != [3, 3] or attrs.get("strides") != [1, 1] or attrs.get("pads") != [1, 1, 1, 1]:
            fail(f"{name} is not a padded 3x3 stride 1 conv")
    resize = node(graph, "/Resize")
    attrs = {a.name: helper.get_attribute_value(a) for a in resize.attribute}
    if resize.input[0] != "input" or attrs.get("mode") != b"nearest":
        fail("/Resize is not a nearest resize of the input")


def crop(model, pad):
    graph = model.graph
    check_graph(graph)
    for name in CONVS[-pad:]:
        pads = next(a for a in node(graph, name).attribute if a.name == "pads")
        del pads.ints[:]
        pads.ints.extend([0, 0, 0, 0])

    resize = node(graph, "/Resize")
    for suffix, values in (("starts", [pad, pad]), ("ends", [-pad, -pad]), ("axes", [2, 3])):
        graph.initializer.append(numpy_helper.from_array(np.asarray(values, dtype=np.int64), f"crop_skip_{suffix}"))
    resize.input[0] = "crop_skip_out"
    graph.node.insert(list(graph.node).index(resize), helper.make_node(
        "Slice", ["input", "crop_skip_starts", "crop_skip_ends", "crop_skip_axes"], ["crop_skip_out"], name="crop_skip"))

    del graph.value_info[:]
    props = {p.key: p.value for p in model.metadata_props}
    props[CROP_KEY] = str(pad)
    helper.set_model_props(model, props)
    onnx.checker.check_model(model)


def session(path):
    options = ort.SessionOptions()
    options.intra_op_num_threads = 1
    return ort.InferenceSession(str(path), options, providers=["CPUExecutionProvider"])


def verify(source, target, pad):
    full, cropped = session(source), session(target)
    if cropped.get_modelmeta().custom_metadata_map.get(CROP_KEY) != str(pad):
        fail(f"{target} does not carry {CROP_KEY} = {pad}")
    rng = np.random.default_rng(1234)
    for height, width in ((120, 120), (116, 124), (128, 108)):
        x = rng.random((1, 3, height, width), dtype=np.float32)
        x[:, :, : height // 2, : width // 3] = 0.03
        want = full.run(None, {"input": x})[0][:, :, 2 * pad:-2 * pad, 2 * pad:-2 * pad]
        got = cropped.run(None, {"input": x})[0]
        if got.shape != want.shape or not np.array_equal(got, want):
            fail(f"{height}x{width}: cropped output {got.shape} is not the full model's interior {want.shape}")
    print("checked: the cropped output equals the full model's interior bit for bit on 3 tiles")


def sha256_of(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("source", type=Path, help="the uncropped maki-2x-hq.onnx")
    parser.add_argument("target", type=Path, help="where the cropped model is written")
    parser.add_argument("--pad", type=int, default=12, help="UpscaleTiling.ContextPad (default 12)")
    parser.add_argument("--any-source", action="store_true", help="skip the source sha256 check")
    args = parser.parse_args()

    if not 1 <= args.pad <= len(CONVS):
        fail(f"--pad must be 1 to {len(CONVS)}, one unpadded conv per px")
    digest = sha256_of(args.source)
    if digest != SOURCE_SHA256 and not args.any_source:
        fail(f"{args.source} has sha256 {digest}, not the uncropped maki-2x-hq; pass --any-source for new weights")
    model = onnx.load(str(args.source))
    if any(p.key == CROP_KEY for p in model.metadata_props):
        fail(f"{args.source} is already cropped")

    crop(model, args.pad)
    onnx.save(model, str(args.target))
    verify(args.source, args.target, args.pad)
    print(f"wrote {args.target}: {args.target.stat().st_size} bytes, sha256 {sha256_of(args.target)}")


if __name__ == "__main__":
    main()
