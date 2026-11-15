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
using MissionPlanner.AeroAid.Camera;

namespace MissionPlanner.AeroAid.Vision
{
    public enum AiEngineState
    {
        Disabled,
        NoModel,
        ModelError,
        WaitingForVideo,
        Running
    }

    public enum DetectionStatus
    {
        /// <summary>Currently detected, not yet acknowledged by the operator.</summary>
        Active,
        /// <summary>Currently detected, acknowledged by the operator.</summary>
        Acknowledged,
        /// <summary>No longer detected (episode ended).</summary>
        Cleared
    }

    /// <summary>One alert episode of one category: from confirmed detection until it has been clear for a while.</summary>
    public sealed class DetectionEvent
    {
        public int Id;
        public DetectionCategory Category;
        public DateTime Start;
        public DateTime? End;
        public float MaxConfidence;
        public int FramesSeen;
        public string ScreenshotPath;
        public string CameraSource = "";
        public string ModelName = "";
        public string Labels = "";
        public bool Acknowledged;

        public DetectionStatus Status => End != null ? DetectionStatus.Cleared : Acknowledged ? DetectionStatus.Acknowledged : DetectionStatus.Active;
        public TimeSpan Duration => (End ?? DateTime.Now) - Start;
    }

    /// <summary>A configured model and its load result.</summary>
    public sealed class LoadedModel
    {
        public ModelConfig Config;
        public YoloOnnxDetector Detector;
        public string Status = "";
        public bool Ok => Detector != null;
    }

    /// <summary>
    /// Multi-model, multi-category AI detection on the shared live video. Runs in a background task (never on
    /// the UI thread). Only categories covered by a successfully loaded model can ever be reported - there are no
    /// heuristics or simulated results.
    /// </summary>
    public sealed class AiDetectionManager
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        public static AiDetectionManager Instance { get; } = new AiDetectionManager();

        private sealed class Tracker
        {
            public int Consecutive;
            public DateTime LastSeen;
            public DetectionEvent Current;
        }

        private readonly object sync = new object();
        private readonly List<DetectionEvent> history = new List<DetectionEvent>();
        private readonly Dictionary<string, Tracker> trackers = new Dictionary<string, Tracker>();
        private CancellationTokenSource cts;
        private Task loop;
        private List<LoadedModel> models = new List<LoadedModel>();
        private IReadOnlyList<Detection> latest = new Detection[0];
        private DateTime latestTimeUtc = DateTime.MinValue;
        private int nextId = 1;

        public AiEngineState State { get; private set; } = AiEngineState.Disabled;
        public string StatusMessage { get; private set; } = "AI detection disabled.";
        public double InferenceMs { get; private set; }

        public event Action StatusChanged;
        public event Action<DetectionEvent> AlertRaised;
        public event Action<DetectionEvent> AlertCleared;

        private AiDetectionManager()
        {
            VideoView.Overlay += PaintOverlay;
        }

        // ------------------------------------------------------------------ queries (any thread)

        public IReadOnlyList<LoadedModel> Models
        {
            get
            {
                lock (sync) return models.ToList();
            }
        }

        /// <summary>True when at least one loaded model can detect this category.</summary>
        public bool IsOperational(string categoryId) =>
            Models.Any(m => m.Ok && m.Config.Enabled && m.Detector.Categories.Any(c => c.Id == categoryId));

        public string ModelsCovering(string categoryId) =>
            string.Join(", ", Models.Where(m => m.Ok && m.Detector.Categories.Any(c => c.Id == categoryId)).Select(m => m.Detector.ModelName));

        /// <summary>Latest detections of enabled categories (empty when stale).</summary>
        public IReadOnlyList<Detection> LatestDetections =>
            (DateTime.UtcNow - latestTimeUtc).TotalSeconds < 2 ? latest : new Detection[0];

        public List<DetectionEvent> History
        {
            get
            {
                lock (sync) return history.ToList();
            }
        }

        public List<DetectionEvent> ActiveAlerts
        {
            get
            {
                lock (sync) return trackers.Values.Where(t => t.Current != null).Select(t => t.Current).OrderBy(e => e.Start).ToList();
            }
        }

        public bool AlertActive => ActiveAlerts.Count > 0;
        public bool UnacknowledgedAlert => ActiveAlerts.Any(e => !e.Acknowledged);

        public int SessionCount(string categoryId)
        {
            lock (sync) return history.Count(e => e.Category.Id == categoryId);
        }

        public void AcknowledgeAll()
        {
            lock (sync)
                foreach (var e in trackers.Values.Where(t => t.Current != null))
                    e.Current.Acknowledged = true;
            Raise(StatusChanged);
        }

        public void Acknowledge(DetectionEvent ev)
        {
            if (ev == null) return;
            ev.Acknowledged = true;
            Raise(StatusChanged);
        }

        public static string CameraSource()
        {
            if (!string.IsNullOrEmpty(CameraSettings.CustomPipeline)) return "Custom video pipeline";
            var host = CameraSettings.EffectiveHost;
            var model = CameraSettings.Model;
            return string.IsNullOrEmpty(host) ? model : $"{model} @ {host}";
        }

        // ------------------------------------------------------------------ lifecycle

        private void SetStatus(AiEngineState s, string message)
        {
            if (State == s && StatusMessage == message) return;
            State = s;
            StatusMessage = message;
            log.Info("AI detection: " + s + " - " + message);
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
                log.Error("AI detection event handler failed", ex);
            }
        }

        /// <summary>(Re)starts or stops the engine according to the current settings.</summary>
        public void ApplySettings()
        {
            Stop();
            DetectionCategories.Load();
            if (!AiSettings.Enabled)
            {
                SetStatus(AiEngineState.Disabled, "AI detection disabled.");
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
            List<LoadedModel> old;
            lock (sync)
            {
                old = models;
                models = new List<LoadedModel>();
            }

            foreach (var m in old) m.Detector?.Dispose();
            latest = new Detection[0];
            EndAllAlerts();
        }

        private void LoadModels()
        {
            var loaded = new List<LoadedModel>();
            foreach (var cfg in AiSettings.Models)
            {
                var lm = new LoadedModel { Config = cfg };
                if (!cfg.Enabled)
                {
                    lm.Status = "Disabled";
                }
                else if (!File.Exists(cfg.Path))
                {
                    lm.Status = "File not found";
                }
                else
                {
                    try
                    {
                        lm.Detector = new YoloOnnxDetector(cfg.Path);
                        var cats = lm.Detector.Categories;
                        lm.Status = cats.Count == 0
                            ? "Loaded, but no class maps to a detection category (see labels mapping)"
                            : "Loaded • " + string.Join(", ", cats.Select(c => c.Name));
                    }
                    catch (Exception ex)
                    {
                        log.Error("Loading model " + cfg.Path + " failed", ex);
                        lm.Status = "Load error: " + ex.GetBaseException().Message;
                    }
                }

                loaded.Add(lm);
            }

            lock (sync) models = loaded;

            var ok = loaded.Count(m => m.Ok);
            if (ok == 0)
            {
                var anyError = loaded.Any(m => m.Status.StartsWith("Load error"));
                SetStatus(anyError ? AiEngineState.ModelError : AiEngineState.NoModel,
                    anyError ? "No model could be loaded. " + loaded.First(m => m.Status.StartsWith("Load error")).Status
                        : "No AI model available. Add a trained ONNX model (Settings › AI detection). Expected default: " + AiSettings.DefaultFireModelPath);
                return;
            }

            SetStatus(AiEngineState.WaitingForVideo, $"{ok} model(s) loaded. Waiting for live video (start the camera stream).");
        }

        private void Run(CancellationToken ct)
        {
            LoadModels();
            if (Models.All(m => !m.Ok))
                return;

            long lastId = -1;
            var sw = new Stopwatch();
            while (!ct.IsCancellationRequested)
            {
                var period = TimeSpan.FromSeconds(1.0 / AiSettings.AnalysisFps);
                var cycleStart = DateTime.UtcNow;
                Bitmap frame = null;
                try
                {
                    frame = VideoStreamManager.Instance.GetFrameCopy(out var id);
                    if (frame == null || id == lastId)
                    {
                        if (frame == null)
                            SetStatus(AiEngineState.WaitingForVideo, $"{Models.Count(m => m.Ok)} model(s) loaded. Waiting for live video (start the camera stream).");
                        frame?.Dispose();
                        frame = null;
                        if (ct.WaitHandle.WaitOne(150)) break;
                        continue;
                    }

                    lastId = id;
                    sw.Restart();
                    var disabled = AiSettings.DisabledCategories;
                    var all = new List<Detection>();
                    foreach (var m in Models.Where(x => x.Ok))
                    {
                        var threshold = m.Config.Threshold > 0 ? m.Config.Threshold : AiSettings.ConfidenceThreshold;
                        all.AddRange(m.Detector.Detect(frame, (float)threshold));
                    }

                    sw.Stop();
                    InferenceMs = sw.Elapsed.TotalMilliseconds;
                    // only emergency categories that the operator has not filtered out
                    var relevant = all.Where(d => d.Category != null && !disabled.Contains(d.Category.Id)).ToList();
                    latest = relevant;
                    latestTimeUtc = DateTime.UtcNow;
                    SetStatus(AiEngineState.Running, "Analysing live video.");
                    UpdateTrackers(relevant, frame, disabled);
                }
                catch (Exception ex)
                {
                    log.Error("AI detection cycle failed", ex);
                    SetStatus(AiEngineState.ModelError, "Detection error: " + ex.GetBaseException().Message);
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

        // ------------------------------------------------------------------ alert episodes (per category)

        private void UpdateTrackers(List<Detection> relevant, Bitmap frame, HashSet<string> disabled)
        {
            var now = DateTime.Now;
            var byCat = relevant.GroupBy(d => d.Category.Id).ToDictionary(g => g.Key, g => g.ToList());
            var raised = new List<DetectionEvent>();
            var cleared = new List<DetectionEvent>();
            lock (sync)
            {
                foreach (var cat in DetectionCategories.All)
                {
                    if (!trackers.TryGetValue(cat.Id, out var t))
                        trackers[cat.Id] = t = new Tracker();

                    if (byCat.TryGetValue(cat.Id, out var dets))
                    {
                        t.Consecutive++;
                        t.LastSeen = now;
                        if (t.Current == null && t.Consecutive >= AiSettings.ConfirmFrames)
                        {
                            t.Current = new DetectionEvent
                            {
                                Id = nextId++,
                                Category = cat,
                                Start = now,
                                MaxConfidence = dets.Max(d => d.Confidence),
                                FramesSeen = 1,
                                CameraSource = CameraSource(),
                                ModelName = string.Join(", ", dets.Select(d => d.ModelName).Distinct()),
                                Labels = string.Join(", ", dets.Select(d => d.Label).Distinct())
                            };
                            history.Insert(0, t.Current);
                            while (history.Count > 1000) history.RemoveAt(history.Count - 1);
                            raised.Add(t.Current);
                        }
                        else if (t.Current != null)
                        {
                            t.Current.FramesSeen++;
                            t.Current.MaxConfidence = Math.Max(t.Current.MaxConfidence, dets.Max(d => d.Confidence));
                        }
                    }
                    else
                    {
                        t.Consecutive = 0;
                        // a filtered-out category ends immediately; others after the clear time
                        if (t.Current != null && (disabled.Contains(cat.Id) || (now - t.LastSeen).TotalSeconds >= AiSettings.ClearSeconds))
                        {
                            t.Current.End = now;
                            cleared.Add(t.Current);
                            t.Current = null;
                        }
                    }
                }
            }

            foreach (var ev in raised)
            {
                if (AiSettings.SaveScreenshots)
                    ev.ScreenshotPath = SaveScreenshot(frame, byCat[ev.Category.Id], ev);
                AppendCsv(ev, "ALERT");
                log.Warn($"AI DETECTION ALERT: {ev.Category.Name} {ev.MaxConfidence:P0} ({ev.ModelName})");
                try
                {
                    AlertRaised?.Invoke(ev);
                }
                catch (Exception ex)
                {
                    log.Error("AlertRaised handler failed", ex);
                }
            }

            foreach (var ev in cleared)
                Cleared(ev);
            if (raised.Count > 0 || cleared.Count > 0)
                Raise(StatusChanged);
        }

        private void Cleared(DetectionEvent ev)
        {
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

        private void EndAllAlerts()
        {
            List<DetectionEvent> ended;
            lock (sync)
            {
                ended = trackers.Values.Where(t => t.Current != null).Select(t => t.Current).ToList();
                foreach (var t in trackers.Values)
                {
                    if (t.Current != null) t.Current.End = DateTime.Now;
                    t.Current = null;
                    t.Consecutive = 0;
                }
            }

            foreach (var ev in ended) Cleared(ev);
            if (ended.Count > 0) Raise(StatusChanged);
        }

        // ------------------------------------------------------------------ evidence

        private static string SaveScreenshot(Bitmap frame, IEnumerable<Detection> dets, DetectionEvent ev)
        {
            try
            {
                Directory.CreateDirectory(AiSettings.DetectionsDirectory);
                var path = Path.Combine(AiSettings.DetectionsDirectory,
                    $"{ev.Category.Id}_{ev.Start:yyyyMMdd_HHmmss_fff}.png");
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
                Directory.CreateDirectory(AiSettings.DetectionsDirectory);
                var path = Path.Combine(AiSettings.DetectionsDirectory, "detections.csv");
                if (!File.Exists(path))
                    File.AppendAllText(path, "timestamp,event,category,max_confidence,duration_s,camera,model,labels,screenshot\r\n");
                string Q(string s) => "\"" + (s ?? "").Replace("\"", "'") + "\"";
                File.AppendAllText(path, string.Format(CultureInfo.InvariantCulture,
                    "{0:yyyy-MM-dd HH:mm:ss},{1},{2},{3:0.000},{4:0.0},{5},{6},{7},{8}\r\n",
                    DateTime.Now, what, ev.Category.Id, ev.MaxConfidence, ev.Duration.TotalSeconds,
                    Q(ev.CameraSource), Q(ev.ModelName), Q(ev.Labels), Q(ev.ScreenshotPath)));
            }
            catch (Exception ex)
            {
                log.Error("Writing detections.csv failed", ex);
            }
        }

        // ------------------------------------------------------------------ overlay

        private void PaintOverlay(Graphics g, Rectangle imageRect)
        {
            var dets = LatestDetections;
            if (dets.Count == 0) return;
            DrawDetections(g, imageRect, dets, 2f);
        }

        public static void DrawDetections(Graphics g, Rectangle area, IEnumerable<Detection> dets, float penWidth)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var font = new Font("Segoe UI Semibold", Math.Max(9f, penWidth * 5f), GraphicsUnit.Pixel))
            {
                foreach (var d in dets)
                {
                    var color = d.Category?.Color ?? Color.Gray;
                    var r = new RectangleF(area.X + d.Box.X * area.Width, area.Y + d.Box.Y * area.Height,
                        d.Box.Width * area.Width, d.Box.Height * area.Height);
                    using (var pen = new Pen(color, penWidth))
                        g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
                    var text = $"{(d.Category?.Name ?? d.Label).ToUpperInvariant()} {d.Confidence:P0}";
                    var sz = g.MeasureString(text, font);
                    var lr = new RectangleF(r.X, Math.Max(area.Y, r.Y - sz.Height - 2), sz.Width + 6, sz.Height + 2);
                    using (var bg = new SolidBrush(color))
                        g.FillRectangle(bg, lr);
                    var dark = color.GetBrightness() > 0.6f;
                    using (var tb = new SolidBrush(dark ? Color.Black : Color.White))
                        g.DrawString(text, font, tb, lr.X + 3, lr.Y + 1);
                }
            }
        }
    }
}
