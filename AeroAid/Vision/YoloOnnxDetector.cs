using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace MissionPlanner.AeroAid.Vision
{
    public enum DetectionKind
    {
        Fire,
        Smoke,
        Other
    }

    /// <summary>One detected object; box coordinates are normalised to the source frame (0..1).</summary>
    public sealed class Detection
    {
        public DetectionKind Kind;
        public string Label = "";
        public float Confidence;
        public RectangleF Box;
    }

    /// <summary>
    /// Runs a YOLO object-detection model exported to ONNX (Ultralytics YOLOv5/v8/v11 or an end-to-end
    /// export with NMS) on video frames. Classes whose name contains "fire"/"flame" are treated as fire and
    /// "smoke" as smoke. Class names come from the model metadata ("names") or a labels.txt file next to the model.
    /// </summary>
    public sealed class YoloOnnxDetector : IDisposable
    {
        private readonly InferenceSession session;
        private readonly string inputName;
        private readonly int inputW;
        private readonly int inputH;
        private readonly string[] classNames;

        public string ModelPath { get; }
        public IReadOnlyList<string> ClassNames => classNames;
        public string Description { get; }

        public YoloOnnxDetector(string modelPath)
        {
            ModelPath = modelPath;
            var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
            options.IntraOpNumThreads = Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2));
            session = new InferenceSession(modelPath, options);

            var input = session.InputMetadata.First();
            inputName = input.Key;
            var dims = input.Value.Dimensions;
            if (dims.Length != 4 || dims[1] != 3)
                throw new InvalidDataException($"Unsupported model input shape [{string.Join(",", dims)}]; expected [1,3,H,W].");
            inputH = dims[2] > 0 ? dims[2] : 640;
            inputW = dims[3] > 0 ? dims[3] : 640;

            classNames = LoadClassNames(modelPath, session);
            Description = $"{Path.GetFileName(modelPath)} • input {inputW}x{inputH} • classes: {string.Join(", ", classNames)}";
        }

        public void Dispose() => session.Dispose();

        private static string[] LoadClassNames(string modelPath, InferenceSession s)
        {
            var labels = Path.Combine(Path.GetDirectoryName(modelPath) ?? "", "labels.txt");
            if (File.Exists(labels))
            {
                var lines = File.ReadAllLines(labels).Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
                if (lines.Length > 0) return lines;
            }

            try
            {
                if (s.ModelMetadata.CustomMetadataMap.TryGetValue("names", out var names))
                {
                    // Ultralytics format: {0: 'fire', 1: 'smoke'}
                    var matches = Regex.Matches(names, @"(\d+)\s*:\s*['""]([^'""]*)['""]");
                    var dict = matches.Cast<Match>().ToDictionary(m => int.Parse(m.Groups[1].Value), m => m.Groups[2].Value);
                    if (dict.Count > 0)
                        return Enumerable.Range(0, dict.Keys.Max() + 1).Select(i => dict.TryGetValue(i, out var n) ? n : "class" + i).ToArray();
                }
            }
            catch (Exception)
            {
                // metadata is optional
            }

            return new string[0];
        }

        public static DetectionKind Classify(string label)
        {
            var l = (label ?? "").ToLowerInvariant();
            if (l.Contains("smoke")) return DetectionKind.Smoke;
            if (l.Contains("fire") || l.Contains("flame")) return DetectionKind.Fire;
            return DetectionKind.Other;
        }

        private string NameOf(int cls) => cls >= 0 && cls < classNames.Length ? classNames[cls] : "class" + cls;

        /// <summary>Detects objects in <paramref name="frame"/> (any size). Thread: call from one worker thread.</summary>
        public List<Detection> Detect(Bitmap frame, float confThreshold, float iouThreshold = 0.45f)
        {
            // letterbox into the model input
            var scale = Math.Min((float)inputW / frame.Width, (float)inputH / frame.Height);
            var nw = (int)Math.Round(frame.Width * scale);
            var nh = (int)Math.Round(frame.Height * scale);
            var padX = (inputW - nw) / 2;
            var padY = (inputH - nh) / 2;

            var tensor = new DenseTensor<float>(new[] { 1, 3, inputH, inputW });
            using (var lb = new Bitmap(inputW, inputH, PixelFormat.Format24bppRgb))
            {
                using (var g = Graphics.FromImage(lb))
                {
                    g.Clear(Color.FromArgb(114, 114, 114));
                    g.InterpolationMode = InterpolationMode.Bilinear;
                    g.DrawImage(frame, padX, padY, nw, nh);
                }

                var bd = lb.LockBits(new Rectangle(0, 0, inputW, inputH), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                try
                {
                    var row = new byte[bd.Stride];
                    var buf = tensor.Buffer.Span;
                    var plane = inputW * inputH;
                    for (var y = 0; y < inputH; y++)
                    {
                        Marshal.Copy(bd.Scan0 + y * bd.Stride, row, 0, bd.Stride);
                        var o = y * inputW;
                        for (var x = 0; x < inputW; x++)
                        {
                            var i = x * 3; // BGR in memory
                            buf[o + x] = row[i + 2] / 255f; // R
                            buf[plane + o + x] = row[i + 1] / 255f; // G
                            buf[2 * plane + o + x] = row[i] / 255f; // B
                        }
                    }
                }
                finally
                {
                    lb.UnlockBits(bd);
                }
            }

            var candidates = new List<Detection>();
            using (var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, tensor) }))
            {
                var output = results.First().AsTensor<float>();
                var d = output.Dimensions.ToArray();
                if (d.Length == 3)
                {
                    // end-to-end exports (NMS inside the model) emit at most ~300 rows of [x1,y1,x2,y2,score,class]
                    if (d[2] == 6 && d[1] <= 300)
                        DecodeEndToEnd(output, d[1], confThreshold, candidates);
                    else if (d[1] < d[2])
                        DecodeV8(output, d[1], d[2], confThreshold, candidates); // [1, 4+nc, N]
                    else
                        DecodeV5(output, d[1], d[2], confThreshold, candidates); // [1, N, 5+nc]
                }
                else
                {
                    throw new InvalidDataException($"Unsupported model output shape [{string.Join(",", d)}].");
                }
            }

            var kept = Nms(candidates, iouThreshold);
            // map from model input space back to normalised frame coordinates
            foreach (var det in kept)
            {
                var b = det.Box;
                var x1 = (b.Left - padX) / scale / frame.Width;
                var y1 = (b.Top - padY) / scale / frame.Height;
                var x2 = (b.Right - padX) / scale / frame.Width;
                var y2 = (b.Bottom - padY) / scale / frame.Height;
                x1 = Clamp01(x1); y1 = Clamp01(y1); x2 = Clamp01(x2); y2 = Clamp01(y2);
                det.Box = RectangleF.FromLTRB(x1, y1, x2, y2);
            }

            return kept;
        }

        private static float Clamp01(float v) => Math.Max(0, Math.Min(1, v));

        private void Add(List<Detection> list, int cls, float conf, float cx, float cy, float w, float h)
        {
            var label = NameOf(cls);
            list.Add(new Detection
            {
                Kind = Classify(label),
                Label = label,
                Confidence = conf,
                Box = new RectangleF(cx - w / 2, cy - h / 2, w, h)
            });
        }

        private void DecodeV8(Tensor<float> t, int channels, int n, float conf, List<Detection> list)
        {
            var nc = channels - 4;
            for (var i = 0; i < n; i++)
            {
                var best = -1;
                var bestScore = 0f;
                for (var c = 0; c < nc; c++)
                {
                    var s = t[0, 4 + c, i];
                    if (s > bestScore) { bestScore = s; best = c; }
                }

                if (best >= 0 && bestScore >= conf)
                    Add(list, best, bestScore, t[0, 0, i], t[0, 1, i], t[0, 2, i], t[0, 3, i]);
            }
        }

        private void DecodeV5(Tensor<float> t, int n, int channels, float conf, List<Detection> list)
        {
            var nc = channels - 5;
            for (var i = 0; i < n; i++)
            {
                var obj = t[0, i, 4];
                if (obj < conf) continue;
                var best = -1;
                var bestScore = 0f;
                for (var c = 0; c < nc; c++)
                {
                    var s = t[0, i, 5 + c] * obj;
                    if (s > bestScore) { bestScore = s; best = c; }
                }

                if (best >= 0 && bestScore >= conf)
                    Add(list, best, bestScore, t[0, i, 0], t[0, i, 1], t[0, i, 2], t[0, i, 3]);
            }
        }

        private void DecodeEndToEnd(Tensor<float> t, int n, float conf, List<Detection> list)
        {
            // [x1, y1, x2, y2, score, class]
            for (var i = 0; i < n; i++)
            {
                var score = t[0, i, 4];
                if (score < conf) continue;
                var x1 = t[0, i, 0];
                var y1 = t[0, i, 1];
                var x2 = t[0, i, 2];
                var y2 = t[0, i, 3];
                Add(list, (int)t[0, i, 5], score, (x1 + x2) / 2, (y1 + y2) / 2, x2 - x1, y2 - y1);
            }
        }

        private static List<Detection> Nms(List<Detection> dets, float iou)
        {
            var result = new List<Detection>();
            foreach (var group in dets.GroupBy(d => d.Label))
            {
                var sorted = group.OrderByDescending(d => d.Confidence).ToList();
                while (sorted.Count > 0)
                {
                    var top = sorted[0];
                    result.Add(top);
                    sorted.RemoveAt(0);
                    sorted.RemoveAll(d => IoU(top.Box, d.Box) > iou);
                }
            }

            return result;
        }

        private static float IoU(RectangleF a, RectangleF b)
        {
            var inter = RectangleF.Intersect(a, b);
            if (inter.IsEmpty) return 0;
            var i = inter.Width * inter.Height;
            var u = a.Width * a.Height + b.Width * b.Height - i;
            return u <= 0 ? 0 : i / u;
        }
    }
}
