"""
Writes identity-2x.onnx: a nearest-neighbour 2x upsample that stands in for a real upscale model in
tests. Input "input" float32 [1,3,H,W] with dynamic H and W, output "output" [1,3,2H,2W].

Needs the onnx package: python make-identity-2x.py
"""

from pathlib import Path

import numpy as np
import onnx
from onnx import TensorProto, helper, numpy_helper

here = Path(__file__).resolve().parent

scales = numpy_helper.from_array(np.array([1, 1, 2, 2], dtype=np.float32), name="scales")
resize = helper.make_node(
    "Resize",
    inputs=["input", "", "scales"],
    outputs=["output"],
    mode="nearest",
    coordinate_transformation_mode="asymmetric",
    nearest_mode="floor",
)
graph = helper.make_graph(
    [resize],
    "identity_2x",
    [helper.make_tensor_value_info("input", TensorProto.FLOAT, [1, 3, "height", "width"])],
    [helper.make_tensor_value_info("output", TensorProto.FLOAT, [1, 3, "out_height", "out_width"])],
    initializer=[scales],
)
model = helper.make_model(graph, opset_imports=[helper.make_opsetid("", 13)], ir_version=8)
onnx.checker.check_model(model)
onnx.save(model, str(here / "identity-2x.onnx"))
print("wrote", here / "identity-2x.onnx")
