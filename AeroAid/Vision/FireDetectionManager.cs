using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using MissionPlanner.AeroAid.Branding;
using MissionPlanner.AeroAid.Camera;
using MissionPlanner.Utilities;

namespace MissionPlanner.AeroAid.Vision
{
    public enum FireDetectorState
    {
        Disabled,
        NoModel,
        ModelError,
        WaitingForVideo,
        Running
    }

    /// <summary>One alert episode: from the first confirmed fire/smoke detection until it has been clear for a while.</summary>
    public sealed class DetectionEvent
    {
        public DateTime Start;
        public DateTime? End;
        public bool Fire;
        public bool Smoke;
        public float MaxConfidence;
        public string ScreenshotPath;

        public string KindText => Fire && Smoke ? "Fire + smoke" : Fire ? "Fire" : "Smoke";
        public TimeSpan Duration => (End ?? DateTime.Now) - Start;
    }

    /// <summary>Fire-detection preferences (AeroSettings "fire.*").</summary>
    public static class FireSettings
    {
        public static string DefaultModelPath =>
            Path.Combine(Settings.GetUserDataDirectory(), "AeroAid", "models", "fire_smoke.onnx");

        public static string DetectionsDirectory =>
            Path.Combine(Settings.GetUserDataDirectory(), "AeroAid", "detections");

        public static bool Enabled
        {
            get => AeroSettings.GetBool("fire.enabled", true);
            set => AeroSettings.SetBool("fire.enabled", value);
        }

        public static string ModelPath
        {
            get
            {
                var p = AeroSettings.GetString("fire.model", "");
                return string.IsNullOrWhiteSpace(p) ? DefaultModelPath : p;
            }
            set => AeroSettings.SetString("fire.model", value ?? "");
        }

        public static double ConfidenceThreshold
        {
            get => AeroSettings.GetDouble("fire.threshold", 0.5, 0.1, 0.95);
            set => AeroSettings.SetDouble("fire.threshold", value);
        }

        public static double AnalysisFps
        {
            get => AeroSettings.GetDouble("fire.fps", 3, 0.5, 15);
            set => AeroSettings.SetDouble("fire.fps", value);
        }

        /// <summary>Consecutive analysed frames with fire/smoke required before an alert is raised.</summary>
        public static int ConfirmFrames
        {
            get => AeroSettings.GetInt("fire.confirm_frames", 2, 1, 10);
            set => AeroSettings.SetInt("fire.confirm_frames", value);
        }

        /// <summary>Seconds without detections before an alert episode ends (prevents repeated alerts).</summary>
        public static int ClearSeconds
        {
            get => AeroSettings.GetInt("fire.clear_seconds", 10, 2, 120);
            set => AeroSettings.SetInt("fire.clear_seconds", value);
        }

        public static bool SaveScreenshots
        {
            get => AeroSettings.GetBool("fire.screenshots", true);
            set => AeroSettings.SetBool("fire.screenshots", value);
        }

        public static bool PlaySound
        {
            get => AeroSettings.GetBool("fire.sound", true);
            set => AeroSettings.SetBool("fire.sound", value);
        }
    }

    /// <summary>
    /// Runs the fire/smoke model on the shared live video in a background task (never on the UI thread).
    /// Without a trained model file nothing is detected - no colour heuristics are used.
    /// </summary>
    public sealed class FireDetectionManager
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        public static FireDetectionManager Instance { get; } = new FireDetectionManager();

        private readonly object sync = new object();
        private readonly List<DetectionEvent> history = new List<DetectionEvent>();
        private CancellationTokenSource cts;
        private Task loop;
        private YoloOnnxDetector detector;
        private IReadOnlyList<Detection> latest = new Detection[0];
        private DateTime latestTimeUtc = DateTime.MinValue;

        public FireDetectorState State { get; private set; } = FireDetectorState.Disabled;
        public string StatusMessage { get; private set; } = "Fire detection disabled.";
        public string ModelDescription { get; private set; } = "";
        public double InferenceMs { get; private set; }
        public DetectionEvent CurrentAlert { get; private set; }
        public bool AlertActive => CurrentAlert != null;

        public event Action StatusChanged;
        public event Action DetectionsUpdated;
        public event Action<DetectionEvent> AlertRaised;
        public event Action<DetectionEvent> AlertCleared;

        private FireDetectionManager()
        {
            VideoView.Overlay += PaintOverlay;
        }

        public IReadOnlyList<Detection> LatestDetections
        {
            get
            {
                // stale detections are not drawn
                return (DateTime.UtcNow - latestTimeUtc).TotalSeconds < 2 ? latest : new Detection[0];
            }
        }

        public List<DetectionEvent> History
        {
            get
            {
                lock (sync) return history.ToList();
            }
        }

        private void SetStatus(FireDetectorState s, string message)
        {
            if (State == s && StatusMessage == message) return;
            State = s;
            StatusMessage = message;
            log.Info("Fire detection: " + s + " - " + message);
            Raise(StatusChanged);
        }

        private static void Raise(Action a)
        {
            try
            {
                a?.Invoke();
            }
            catch (Exception ex)
            {
                log.Error("Fire detection event handler failed", ex);
            }
        }

        /// <summary>(Re)starts or stops the detector according to the current settings.</summary>
        public void ApplySettings()
        {
            Stop();
            if (!FireSettings.Enabled)
            {
                SetStatus(FireDetectorState.Disabled, "Fire detection disabled (Settings › Fire detection).");
                return;
            }

            cts = new CancellationTokenSource();
            var token = cts.Token;
            loop = Task.Run(() => Run(token), token);
        }

        public void Stop()
        {
            try
            {
                cts?.Cancel();
                loop?.Wait(3000);
            }
            catch (AggregateException)
            {
                // cancellation
            }

            cts?.Dispose();
            cts = null;
            loop = null;
            detector?.Dispose();
            detector = null;
            latest = new Detection[0];
            if (CurrentAlert != null)
                EndAlert();
        }

        private void LoadModel()
        {
            var path = FireSettings.ModelPath;
            if (!File.Exists(path))
            {
                ModelDescription = "";
                SetStatus(FireDetectorState.NoModel,
                    "No fire/smoke model found at " + path + ". Fire detection needs a trained YOLO model in ONNX format " +
                    "(see README_CUSTOM_GCS.md › Fire detection model setup).");
                return;
            }

            try
            {
                detector = new YoloOnnxDetector(path);
                ModelDescription = detector.Description;
                if (!detector.ClassNames.Any(n => YoloOnnxDetector.Classify(n) != DetectionKind.Other))
                    log.Warn("Model has no class named fire/flame/smoke: " + string.Join(",", detector.ClassNames));
                SetStatus(FireDetectorState.WaitingForVideo, "Model loaded. Waiting for live video (start the camera stream).");
            }
            catch (Exception ex)
            {
                log.Error("Loading fire model failed", ex);
                detector = null;
                SetStatus(FireDetectorState.ModelError, "Could not load the model: " + ex.GetBaseException().Message);
            }
        }

        private void Run(CancellationToken ct)
        {
            LoadModel();
            if (detector == null)
                return;

            long lastId = -1;
            var consecutive = 0;
            var lastSeen = DateTime.MinValue;
            var sw = new Stopwatch();
            while (!ct.IsCancellationRequested)
            {
                var period = TimeSpan.FromSeconds(1.0 / FireSettings.AnalysisFps);
                var cycleStart = DateTime.UtcNow;
                Bitmap frame = null;
                try
                {
                    frame = VideoStreamManager.Instance.GetFrameCopy(out var id);
                    if (frame == null || id == lastId)
                    {
                        if (frame == null)
                            SetStatus(FireDetectorState.WaitingForVideo, "Model loaded. Waiting for live video (start the camera stream).");
                        frame?.Dispose();
                        frame = null;
                        if (ct.WaitHandle.WaitOne(150)) break;
                        continue;
                    }

                    lastId = id;
                    sw.Restart();
                    var dets = detector.Detect(frame, (float)FireSettings.ConfidenceThreshold);
                    sw.Stop();
                    InferenceMs = sw.Elapsed.TotalMilliseconds;
                    latest = dets;
                    latestTimeUtc = DateTime.UtcNow;
                    SetStatus(FireDetectorState.Running, "Analysing live video.");
                    Raise(DetectionsUpdated);

                    var relevant = dets.Where(d => d.Kind != DetectionKind.Other).ToList();
                    var now = DateTime.Now;
                    if (relevant.Count > 0)
                    {
                        consecutive++;
                        lastSeen = now;
                        if (CurrentAlert == null && consecutive >= FireSettings.ConfirmFrames)
                            BeginAlert(relevant, frame);
                        else if (CurrentAlert != null)
                        {
                            CurrentAlert.MaxConfidence = Math.Max(CurrentAlert.MaxConfidence, relevant.Max(d => d.Confidence));
                            CurrentAlert.Fire |= relevant.Any(d => d.Kind == DetectionKind.Fire);
                            CurrentAlert.Smoke |= relevant.Any(d => d.Kind == DetectionKind.Smoke);
                        }
                    }
                    else
                    {
                        consecutive = 0;
                        if (CurrentAlert != null && (now - lastSeen).TotalSeconds >= FireSettings.ClearSeconds)
                            EndAlert();
                    }
                }
                catch (Exception ex)
                {
                    log.Error("Fire detection cycle failed", ex);
                    SetStatus(FireDetectorState.ModelError, "Detection error: " + ex.GetBaseException().Message);
                    if (ct.WaitHandle.WaitOne(2000)) break;
                }
                finally
                {
                    frame?.Dispose();
                }

                var wait = period - (DateTime.UtcNow - cycleStart);
                if (wait > TimeSpan.Zero && ct.WaitHandle.WaitOne(wait))
                    break;
            }
        }

        private void BeginAlert(List<Detection> relevant, Bitmap frame)
        {
            var ev = new DetectionEvent
            {
                Start = DateTime.Now,
                Fire = relevant.Any(d => d.Kind == DetectionKind.Fire),
                Smoke = relevant.Any(d => d.Kind == DetectionKind.Smoke),
                MaxConfidence = relevant.Max(d => d.Confidence)
            };
            if (FireSettings.SaveScreenshots)
                ev.ScreenshotPath = SaveScreenshot(frame, relevant, ev.Start);
            lock (sync)
            {
                history.Insert(0, ev);
                while (history.Count > 500) history.RemoveAt(history.Count - 1);
            }

            CurrentAlert = ev;
            AppendCsv(ev, "ALERT");
            log.Warn($"FIRE DETECTION ALERT: {ev.KindText} {ev.MaxConfidence:P0}");
            try
            {
                AlertRaised?.Invoke(ev);
            }
            catch (Exception ex)
            {
                log.Error("AlertRaised handler failed", ex);
            }
        }

        private void EndAlert()
        {
            var ev = CurrentAlert;
            if (ev == null) return;
            ev.End = DateTime.Now;
            CurrentAlert = null;
            AppendCsv(ev, "CLEARED");
            try
            {
                AlertCleared?.Invoke(ev);
            }
            catch (Exception ex)
            {
                log.Error("AlertCleared handler failed", ex);
            }
        }

        private static string SaveScreenshot(Bitmap frame, IEnumerable<Detection> dets, DateTime when)
        {
            try
            {
                Directory.CreateDirectory(FireSettings.DetectionsDirectory);
                var path = Path.Combine(FireSettings.DetectionsDirectory, "detection_" + when.ToString("yyyyMMdd_HHmmss_fff") + ".png");
                using (var copy = new Bitmap(frame))
                {
                    using (var g = Graphics.FromImage(copy))
                        DrawDetections(g, new Rectangle(0, 0, copy.Width, copy.Height), dets, Math.Max(2f, copy.Width / 400f));
                    copy.Save(path, ImageFormat.Png);
                }

                return path;
            }
            catch (Exception ex)
            {
                log.Error("Saving detection screenshot failed", ex);
                return null;
            }
        }

        private static void AppendCsv(DetectionEvent ev, string what)
        {
            try
            {
                Directory.CreateDirectory(FireSettings.DetectionsDirectory);
                var path = Path.Combine(FireSettings.DetectionsDirectory, "detections.csv");
                if (!File.Exists(path))
                    File.AppendAllText(path, "timestamp,event,type,max_confidence,duration_s,screenshot\r\n");
                File.AppendAllText(path, string.Format(CultureInfo.InvariantCulture, "{0:yyyy-MM-dd HH:mm:ss},{1},{2},{3:0.000},{4:0.0},{5}\r\n",
                    DateTime.Now, what, ev.KindText, ev.MaxConfidence, ev.Duration.TotalSeconds, ev.ScreenshotPath ?? ""));
            }
            catch (Exception ex)
            {
                log.Error("Writing detections.csv failed", ex);
            }
        }

        private void PaintOverlay(Graphics g, Rectangle imageRect)
        {
            var dets = LatestDetections;
            if (dets.Count == 0) return;
            DrawDetections(g, imageRect, dets, 2f);
        }

        public static void DrawDetections(Graphics g, Rectangle area, IEnumerable<Detection> dets, float penWidth)
        {
            var p = BrandingManager.Palette;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var font = new Font("Segoe UI Semibold", Math.Max(9f, penWidth * 5f), GraphicsUnit.Pixel))
            {
                foreach (var d in dets)
                {
                    var color = d.Kind == DetectionKind.Fire ? p.StatusCritical : d.Kind == DetectionKind.Smoke ? Color.FromArgb(0xC8, 0xC8, 0xC8) : p.Muted;
                    var r = new RectangleF(area.X + d.Box.X * area.Width, area.Y + d.Box.Y * area.Height,
                        d.Box.Width * area.Width, d.Box.Height * area.Height);
                    using (var pen = new Pen(color, penWidth))
                        g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
                    var text = $"{d.Label.ToUpperInvariant()} {d.Confidence:P0}";
                    var sz = g.MeasureString(text, font);
                    var lr = new RectangleF(r.X, Math.Max(area.Y, r.Y - sz.Height - 2), sz.Width + 6, sz.Height + 2);
                    using (var bg = new SolidBrush(color))
                        g.FillRectangle(bg, lr);
                    using (var tb = new SolidBrush(d.Kind == DetectionKind.Fire ? Color.White : Color.Black))
                        g.DrawString(text, font, tb, lr.X + 3, lr.Y + 1);
                }
            }
        }
    }
}
