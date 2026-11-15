# AeroAid test fixtures

The .onnx files here are **test fixtures, not detectors**: ONNX graphs with the Ultralytics YOLOv8 I/O layout whose output is a fixed tensor (see the generator scripts). They verify output decoding, letterbox mapping, NMS, category mapping and the alert pipeline. They must never be shipped or used as real models.
