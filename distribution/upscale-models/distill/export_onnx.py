"""
Export a student the way Maki runs it: ECB branches folded (`collapse`), plain c48n8-layout weights, ONNX
opset 17 with dynamic height and width. Then three checks on the CPU (RESEARCH-PLAN step E):
- the ONNX output matches the folded torch model on a random tile;
- one-thread ONNX Runtime time per 120 px context tile (96 + 2 x 12), against a reference model;
- `KeepsFlat`: for every grey value, a flat tile comes back as exactly that value, which is what Maki
  needs before it may skip inference on one-value tiles for this model.

    python export_onnx.py c48n8e-b6-lp-last --name c48n8-b6 --against student-c48n8-long.onnx
"""

import argparse
import time

import numpy as np
import onnxruntime as ort
import torch

from common import WORK, collapse, load_student, plain_spec, split_name, student


def session(path):
    options = ort.SessionOptions()
    options.intra_op_num_threads = 1
    options.inter_op_num_threads = 1
    return ort.InferenceSession(str(path), options, providers=["CPUExecutionProvider"])


def run(s, x: np.ndarray) -> np.ndarray:
    return s.run(None, {s.get_inputs()[0].name: x})[0]


def to_byte(y: np.ndarray) -> np.ndarray:
    return np.clip(np.floor(y * 255 + 0.5), 0, 255).astype(np.uint8)


def timing(s, runs: int = 40) -> float:
    x = np.random.rand(1, 3, 120, 120).astype(np.float32)
    for _ in range(5):
        run(s, x)
    times = []
    for _ in range(runs):
        t = time.perf_counter()
        run(s, x)
        times.append(time.perf_counter() - t)
    return float(np.median(times)) * 1000


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("checkpoint", help="name under WORK without student- and .pt, e.g. c48n8e-b6-lp-last")
    parser.add_argument("--name", required=True, help="output name, written to WORK/onnx/student-NAME.onnx")
    parser.add_argument("--against", help="an ONNX file under WORK/onnx to time beside it")
    args = parser.parse_args()
    torch.set_num_threads(2)

    spec, _ = split_name(args.checkpoint)
    model = student(spec)
    load_student(model, torch.load(WORK / f"student-{args.checkpoint}.pt", map_location="cpu", weights_only=True))
    folded = collapse(model.eval())
    plain = student(plain_spec(spec))
    plain.load_state_dict(folded.state_dict())
    plain.eval()
    path = WORK / "onnx" / f"student-{args.name}.onnx"
    path.parent.mkdir(exist_ok=True)
    torch.onnx.export(plain, torch.rand(1, 3, 64, 64), str(path), input_names=["input"], output_names=["output"],
                      opset_version=17, dynamo=False,
                      dynamic_axes={"input": {2: "height", 3: "width"}, "output": {2: "out_height", 3: "out_width"}})
    print(f"wrote {path} ({path.stat().st_size / 1024:.0f} KB, {plain_spec(spec)} layout)")

    s = session(path)
    x = np.random.rand(1, 3, 120, 120).astype(np.float32)
    with torch.no_grad():
        ref = model(torch.from_numpy(x)).numpy()
    print(f"ONNX against the unfolded torch model: max |diff| {np.abs(run(s, x) - ref).max():.2e}")

    print(f"one thread, 120 px tile: {timing(s):.1f} ms", end="")
    if args.against:
        print(f"; {args.against}: {timing(session(WORK / 'onnx' / args.against)):.1f} ms", end="")
    print()

    bad = []
    for v in range(256):
        tile = np.full((1, 3, 120, 120), v / 255, np.float32)
        out = to_byte(run(s, tile))
        if not (out == v).all():
            bad.append((v, int(out.min()), int(out.max())))
    if bad:
        shown = ", ".join(f"{v} -> {lo}..{hi}" for v, lo, hi in bad[:12])
        print(f"KeepsFlat: {len(bad)} of 256 grey values drift ({shown}{', ...' if len(bad) > 12 else ''})")
    else:
        print("KeepsFlat: every grey value comes back exact")


if __name__ == "__main__":
    main()
