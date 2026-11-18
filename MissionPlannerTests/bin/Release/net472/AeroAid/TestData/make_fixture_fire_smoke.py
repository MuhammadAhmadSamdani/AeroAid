"""
TEST FIXTURE ONLY - NOT A FIRE DETECTOR.
Builds an ONNX graph with the exact I/O layout of an Ultralytics YOLOv8 export (input 'images' [1,3,640,640],
output 'output0' [1,6,8400] = cx,cy,w,h,score_fire,score_smoke) whose output is a fixed tensor containing
one 'fire' box and one 'smoke' box (plus near-duplicates to exercise NMS). Used to verify AeroAid's
decoding, letterbox mapping, NMS, overlays and alert pipeline end to end.
"""
import sys
import numpy as np
import onnx
from onnx import helper, TensorProto, numpy_helper

out = np.zeros((1, 6, 8400), dtype=np.float32)
def box(i, cx, cy, w, h, fire, smoke):
    out[0, :, i] = [cx, cy, w, h, fire, smoke]
box(10, 320, 320, 200, 150, 0.90, 0.0)   # fire
box(11, 322, 318, 198, 152, 0.85, 0.0)   # overlapping duplicate -> must be removed by NMS
box(20, 100, 220, 80, 60, 0.0, 0.70)     # smoke
box(30, 500, 400, 50, 50, 0.30, 0.0)     # below threshold -> ignored

const = numpy_helper.from_array(out, name="fixed")
zero = numpy_helper.from_array(np.array([0.0], dtype=np.float32), name="zero")
nodes = [
    helper.make_node("ReduceMean", ["images"], ["m"], keepdims=1),
    helper.make_node("Mul", ["m", "zero"], ["z"]),
    helper.make_node("Reshape", ["z", "shape"], ["z1"]),
    helper.make_node("Add", ["fixed", "z1"], ["output0"]),
]
shape = numpy_helper.from_array(np.array([1, 1, 1], dtype=np.int64), name="shape")
graph = helper.make_graph(nodes, "aeroaid_test_fixture",
                          [helper.make_tensor_value_info("images", TensorProto.FLOAT, [1, 3, 640, 640])],
                          [helper.make_tensor_value_info("output0", TensorProto.FLOAT, [1, 6, 8400])],
                          initializer=[const, zero, shape])
model = helper.make_model(graph, opset_imports=[helper.make_opsetid("", 13)])
model.ir_version = 8
helper.set_model_props(model, {"names": "{0: 'fire', 1: 'smoke'}", "description": "AeroAid TEST FIXTURE - not a detector"})
onnx.checker.check_model(model)
onnx.save(model, sys.argv[1])
print("saved", sys.argv[1])
