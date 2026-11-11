#!/usr/bin/env python3
"""
Builds the bundled src/Maki.Api/UpscaleModels/maki-2x.onnx from the uncropped export (the
upscale-models-v1 release file, sha256 68be9942...).

Maki runs each tile with a 12 px context pad and keeps only the interior. The first four convs and the
half-resolution branch need the whole context, but the last six convs each feed pixels one step further
in, so they run unpadded on a window that shrinks to the interior. Slices on the trunk, the branch and
the skip input make the model return only the interior, 2 * (H - 2 * pad) per side. The sums that reach
the kept pixels are unchanged, so ONNX Runtime gives bit-identical values. It is about 15% fewer
multiply-adds.

The file records the crop as the metadata prop maki.context_crop. OnnxPageUpscaler reads it and stitches
the output as already cropped by that many source px per side, so the pad is now baked into the file:
changing UpscaleTiling.ContextPad means rebuilding this file with the new --pad.

The upscale-models-v1 release keeps the uncropped file: versions before this one cannot run this file.

Needs onnx, onnxruntime and numpy.
    python distribution/upscale-models/crop_maki_2x.py <uncropped maki-2x.onnx> src/Maki.Api/UpscaleModels/maki-2x.onnx
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
SOURCE_SHA256 = "68be994232d22192baaff67a1d52391a88390da6b5bfdfdabe89863d3d635dc8"
TAIL = ["/body.8/Conv", "/body.10/Conv", "/body.12/Conv", "/body.14/Conv", "/body.16/Conv", "/body.18/Conv"]


def fail(message: str) -> None:
    print(f"error: {message}", file=sys.stderr)
    sys.exit(1)


def node(graph, name):
    found = [n for n in graph.node if n.name == name]
    if len(found) != 1:
        fail(f"expected one node {name}, found {len(found)}: not the maki-2x graph this script knows")
    return found[0]


def slice_before(graph, name, tensor, consumer, crop):
    for suffix, values in (("starts", [crop, crop]), ("ends", [-crop, -crop]), ("axes", [2, 3])):
        graph.initializer.append(numpy_helper.from_array(np.asarray(values, dtype=np.int64), f"{name}_{suffix}"))
    target = node(graph, consumer)
    if tensor not in target.input:
        fail(f"{consumer} does not read {tensor}")
    out = f"{name}_out"
    for i, value in enumerate(target.input):
        if value == tensor:
            target.input[i] = out
    index = list(graph.node).index(target)
    graph.node.insert(index, helper.make_node(
        "Slice", [tensor, f"{name}_starts", f"{name}_ends", f"{name}_axes"], [out], name=name))


def crop(model, pad):
    graph = model.graph
    for name in TAIL:
        conv = node(graph, name)
        pads = next(a for a in conv.attribute if a.name == "pads")
        if list(pads.ints) != [1, 1, 1, 1]:
            fail(f"{name} has pads {list(pads.ints)}, expected 1")
        del pads.ints[:]
        pads.ints.extend([0, 0, 0, 0])

    slice_before(graph, "crop_trunk", "/body.7/LeakyRelu_output_0", "/body.8/Conv", pad - len(TAIL))
    slice_before(graph, "crop_branch", "/branch/branch.10/Resize_output_0", "/Add", pad - len(TAIL) + 1)
    slice_before(graph, "crop_skip", "input", "/Resize", pad)

    del graph.value_info[:]
    dims = graph.output[0].type.tensor_type.shape.dim
    dims[2].dim_param, dims[3].dim_param = "out_height", "out_width"
    helper.set_model_props(model, {CROP_KEY: str(pad)})
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
    print(f"checked: the cropped output equals the full model's interior bit for bit on 3 tiles")


def sha256_of(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("source", type=Path, help="the uncropped maki-2x.onnx")
    parser.add_argument("target", type=Path, help="where the cropped model is written")
    parser.add_argument("--pad", type=int, default=12, help="UpscaleTiling.ContextPad (default 12)")
    parser.add_argument("--any-source", action="store_true", help="skip the source sha256 check")
    args = parser.parse_args()

    if args.pad < len(TAIL):
        fail(f"--pad must be at least {len(TAIL)}, one per unpadded conv")
    digest = sha256_of(args.source)
    if digest != SOURCE_SHA256 and not args.any_source:
        fail(f"{args.source} has sha256 {digest}, not the released maki-2x; pass --any-source for new weights")
    model = onnx.load(str(args.source))
    if any(p.key == CROP_KEY for p in model.metadata_props):
        fail(f"{args.source} is already cropped")

    crop(model, args.pad)
    onnx.save(model, str(args.target))
    verify(args.source, args.target, args.pad)
    print(f"wrote {args.target}: {args.target.stat().st_size} bytes, sha256 {sha256_of(args.target)}")


if __name__ == "__main__":
    main()
