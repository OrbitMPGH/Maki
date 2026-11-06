"""
One-thread ONNX Runtime cost of candidate shapes, random weights, median of 40 runs: per 120 px context tile
(Maki's 96 + 2 x 12) and per 216 px tile (192 + 2 x 12), so the cost of a bigger model and the saving from
bigger tiles sit in one table. Ratios against the in-run c48n8 are what to read.

    python bench3.py c48n8 hyb-c48n8-b64n4 hyb-c64n8-b64n4 hyb-c64_4_48n8-b64n4
"""

import argparse
import time

import numpy as np
import onnxruntime as ort
import torch

from common import WORK, student


def export(spec: str, path) -> int:
    model = student(spec).eval()
    torch.onnx.export(model, torch.rand(1, 3, 64, 64), str(path), input_names=["input"], output_names=["output"],
                      opset_version=17, dynamo=False,
                      dynamic_axes={"input": {2: "h", 3: "w"}, "output": {2: "oh", 3: "ow"}})
    return sum(p.numel() for p in model.parameters())


def timing(path, size: int, runs: int = 40) -> float:
    options = ort.SessionOptions()
    options.intra_op_num_threads = 1
    options.inter_op_num_threads = 1
    s = ort.InferenceSession(str(path), options, providers=["CPUExecutionProvider"])
    x = np.random.rand(1, 3, size, size).astype(np.float32)
    name = s.get_inputs()[0].name
    for _ in range(5):
        s.run(None, {name: x})
    times = []
    for _ in range(runs):
        t = time.perf_counter()
        s.run(None, {name: x})
        times.append(time.perf_counter() - t)
    return float(np.median(times)) * 1000


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("specs", nargs="+")
    args = parser.parse_args()
    out = WORK / "probe3" / "onnx"
    out.mkdir(parents=True, exist_ok=True)
    base = None
    print(f"{'spec':28} {'params':>7} {'ms/120':>7} {'x':>5} {'ms/216':>7} {'x':>5} {'216 vs 120 per output px':>24}")
    for spec in args.specs:
        path = out / f"{spec}.onnx"
        params = export(spec, path)
        a, b = timing(path, 120), timing(path, 216)
        if base is None:
            base = (a, b)
        print(f"{spec:28} {params / 1000:6.0f}k {a:7.1f} {a / base[0]:5.2f} {b:7.1f} {b / base[1]:5.2f} "
              f"{(b / 192 ** 2) / (a / 96 ** 2):24.2f}", flush=True)


if __name__ == "__main__":
    main()
