"""
TEST FIXTURE ONLY - NOT A DETECTOR. YOLOv8-layout ONNX graph ([1,3,640,640] -> [1,7,8400]) with a fixed output:
classes {0: 'casualty', 1: 'flooded_area', 2: 'car'} - verifies multi-model loading, keyword category mapping
(casualty -> injured_person, flooded_area -> flood) and that non-emergency classes ('car') are ignored.
"""
import sys
import numpy as np
import onnx
from onnx import helper, TensorProto, numpy_helper

out = np.zeros((1, 7, 8400), dtype=np.float32)
def box(i, cx, cy, w, h, scores):
    out[0, :4, i] = [cx, cy, w, h]
    out[0, 4:, i] = scores
box(5, 480, 420, 90, 60, [0.82, 0.0, 0.0])    # casualty -> injured person
box(6, 160, 440, 220, 90, [0.0, 0.76, 0.0])   # flooded_area -> flood
box(7, 560, 250, 70, 40, [0.0, 0.0, 0.95])    # car -> not an emergency category, must be ignored

const = numpy_helper.from_array(out, name="fixed")
zero = numpy_helper.from_array(np.array([0.0], dtype=np.float32), name="zero")
shape = numpy_helper.from_array(np.array([1, 1, 1], dtype=np.int64), name="shape")
nodes = [
    helper.make_node("ReduceMean", ["images"], ["m"], keepdims=1),
    helper.make_node("Mul", ["m", "zero"], ["z"]),
    helper.make_node("Reshape", ["z", "shape"], ["z1"]),
    helper.make_node("Add", ["fixed", "z1"], ["output0"]),
]
graph = helper.make_graph(nodes, "aeroaid_test_fixture_2",
                          [helper.make_tensor_value_info("images", TensorProto.FLOAT, [1, 3, 640, 640])],
                          [helper.make_tensor_value_info("output0", TensorProto.FLOAT, [1, 7, 8400])],
                          initializer=[const, zero, shape])
model = helper.make_model(graph, opset_imports=[helper.make_opsetid("", 13)])
model.ir_version = 8
helper.set_model_props(model, {"names": "{0: 'casualty', 1: 'flooded_area', 2: 'car'}", "description": "AeroAid TEST FIXTURE - not a detector"})
onnx.checker.check_model(model)
onnx.save(model, sys.argv[1])
print("saved", sys.argv[1])
