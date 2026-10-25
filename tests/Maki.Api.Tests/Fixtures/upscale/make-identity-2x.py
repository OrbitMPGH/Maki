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

# The same upsample with float16 input and output, the way SPAN-S takes its tensors.
to_float = helper.make_node("Cast", inputs=["input"], outputs=["input32"], to=TensorProto.FLOAT)
resize32 = helper.make_node(
    "Resize",
    inputs=["input32", "", "scales"],
    outputs=["output32"],
    mode="nearest",
    coordinate_transformation_mode="asymmetric",
    nearest_mode="floor",
)
to_half = helper.make_node("Cast", inputs=["output32"], outputs=["output"], to=TensorProto.FLOAT16)
graph16 = helper.make_graph(
    [to_float, resize32, to_half],
    "identity_2x_fp16",
    [helper.make_tensor_value_info("input", TensorProto.FLOAT16, [1, 3, "height", "width"])],
    [helper.make_tensor_value_info("output", TensorProto.FLOAT16, [1, 3, "out_height", "out_width"])],
    initializer=[scales],
)
model16 = helper.make_model(graph16, opset_imports=[helper.make_opsetid("", 13)], ir_version=8)
onnx.checker.check_model(model16)
onnx.save(model16, str(here / "identity-2x-fp16.onnx"))
print("wrote", here / "identity-2x-fp16.onnx")
