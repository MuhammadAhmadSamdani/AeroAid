using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MissionPlanner.AeroAid.Camera;
using MissionPlanner.AeroAid.Vision;

namespace MissionPlanner.AeroAid.Tests
{
    /// <summary>
    /// AI detection result processing and alert generation. Uses TEST FIXTURE models (fixed outputs, see
    /// TestData/README.md) - they verify the pipeline, not detection quality.
    /// </summary>
    [TestClass]
    public class AiDetectionTests
    {
        [ClassInitialize]
        public static void Init(TestContext _) => TestEnv.Init();

        [TestMethod]
        public void CategoryKeywordMapping()
        {
            Assert.AreEqual(DetectionCategories.Fire, DetectionCategories.Match("fire")?.Id);
            Assert.AreEqual(DetectionCategories.Fire, DetectionCategories.Match("Flame")?.Id);
            Assert.AreEqual(DetectionCategories.Smoke, DetectionCategories.Match("smoke")?.Id);
            Assert.AreEqual(DetectionCategories.InjuredPerson, DetectionCategories.Match("casualty")?.Id);
            Assert.AreEqual(DetectionCategories.InjuredPerson, DetectionCategories.Match("injured_person")?.Id);
            Assert.AreEqual(DetectionCategories.Flood, DetectionCategories.Match("flooded_area")?.Id);
            Assert.AreEqual(DetectionCategories.DamagedBuilding, DetectionCategories.Match("collapsed_house")?.Id);
            Assert.AreEqual(DetectionCategories.OtherEmergency, DetectionCategories.Match("car_accident")?.Id);
            Assert.IsNull(DetectionCategories.Match("car"), "non-emergency classes are ignored");
            Assert.IsNull(DetectionCategories.Match("person"), "a plain person is not an injured person");
            Assert.IsNull(DetectionCategories.Match(""));
        }

        [TestMethod]
        public void AllRequiredCategoriesExist()
        {
            foreach (var id in new[] { "fire", "injured_person", "flood", "damaged_building", "smoke", "other_emergency" })
                Assert.IsNotNull(DetectionCategories.Get(id), id);
        }

        [TestMethod]
        public void FireSmokeFixture_DecodeNmsThresholdAndLetterbox()
        {
            using (var det = new YoloOnnxDetector(TestEnv.Data("fixture_fire_smoke.onnx")))
            using (var frame = new Bitmap(1280, 720))
            {
                CollectionAssert.AreEquivalent(new[] { "fire", "smoke" }, det.ClassNames.ToArray());
                var dets = det.Detect(frame, 0.5f);
                Assert.AreEqual(2, dets.Count, "duplicate removed by NMS, 0.30 box removed by threshold");
                var fire = dets.Single(d => d.Category.Id == "fire");
                Assert.AreEqual(0.90f, fire.Confidence, 1e-4);
                // 640x640 letterbox of 1280x720: scale 0.5, padY 140 -> (320,320,200,150) maps to:
                Assert.AreEqual(0.34375f, fire.Box.X, 2e-3);
                Assert.AreEqual(0.29167f, fire.Box.Y, 2e-3);
                Assert.AreEqual(0.3125f, fire.Box.Width, 2e-3);
                Assert.AreEqual(0.41667f, fire.Box.Height, 2e-3);
                Assert.AreEqual("fixture_fire_smoke.onnx", fire.ModelName);
                Assert.AreEqual(3, det.Detect(frame, 0.25f).Count, "lower threshold admits the 0.30 box");
            }
        }

        [TestMethod]
        public void MultiCategoryFixture_MapsCategoriesAndIgnoresNonEmergencyClasses()
        {
            using (var det = new YoloOnnxDetector(TestEnv.Data("fixture_casualty_flood_car.onnx")))
            using (var frame = new Bitmap(640, 640))
            {
                CollectionAssert.AreEquivalent(new[] { "injured_person", "flood" }, det.Categories.Select(c => c.Id).ToArray());
                var dets = det.Detect(frame, 0.5f);
                Assert.AreEqual(1, dets.Count(d => d.Category?.Id == "injured_person"));
                Assert.AreEqual(1, dets.Count(d => d.Category?.Id == "flood"));
                Assert.AreEqual(1, dets.Count(d => d.Category == null && d.Label == "car"), "car is returned uncategorised");
            }
        }

        [TestMethod]
        public void ExplicitLabelsFileOverridesKeywordMapping()
        {
            var dir = Path.Combine(TestEnv.Root, "labels_test");
            Directory.CreateDirectory(dir);
            var model = Path.Combine(dir, "m.onnx");
            File.Copy(TestEnv.Data("fixture_casualty_flood_car.onnx"), model, true);
            File.WriteAllLines(Path.Combine(dir, "m.labels.txt"), new[] { "casualty", "flooded_area", "car=other_emergency" });
            using (var det = new YoloOnnxDetector(model))
                CollectionAssert.Contains(det.Categories.Select(c => c.Id).ToList(), "other_emergency");
        }

        [TestMethod]
        public void InvalidModelFileThrows()
        {
            var bad = Path.Combine(TestEnv.Root, "garbage.onnx");
            File.WriteAllText(bad, "this is not an onnx model");
            try
            {
                using (new YoloOnnxDetector(bad)) { }
                Assert.Fail("garbage model loaded");
            }
            catch (Exception ex) when (!(ex is AssertFailedException))
            {
                // expected (OnnxRuntimeException)
            }
        }

        [TestMethod]
        public void LegacyFireSettingsMigrate()
        {
            MissionPlanner.Utilities.Settings.Instance["aeroaid.ai.models"] = "";
            MissionPlanner.Utilities.Settings.Instance["aeroaid.fire.model"] = @"C:\legacy\fire.onnx";
            var m = AiSettings.Models;
            Assert.AreEqual(1, m.Count);
            Assert.AreEqual(@"C:\legacy\fire.onnx", m[0].Path);
            MissionPlanner.Utilities.Settings.Instance["aeroaid.fire.model"] = "";
            MissionPlanner.Utilities.Settings.Instance["aeroaid.ai.models"] = "{ broken json";
            Assert.AreEqual(AiSettings.DefaultFireModelPath, AiSettings.Models.Single().Path, "invalid JSON falls back to default");
        }

        // ---------------------------------------------------------------- engine: model loading, alerts, filters

        private static void InjectFrame(Bitmap frame)
        {
            var vs = VideoStreamManager.Instance;
            var t = typeof(VideoStreamManager);
            var lockObj = t.GetField("frameLock", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(vs);
            lock (lockObj)
                t.GetField("latest", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(vs, frame);
            var idField = t.GetField("frameId", BindingFlags.NonPublic | BindingFlags.Instance);
            idField.SetValue(vs, (long)idField.GetValue(vs) + 1);
        }

        private static bool WaitFor(Func<bool> cond, int ms)
        {
            var end = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < end)
            {
                if (cond()) return true;
                Thread.Sleep(50);
            }

            return cond();
        }

        [TestMethod]
        public void Engine_ReportsMissingAndInvalidModels()
        {
            var bad = Path.Combine(TestEnv.Root, "garbage2.onnx");
            File.WriteAllText(bad, "nope");
            AiSettings.Enabled = true;
            AiSettings.Models = new List<ModelConfig>
            {
                new ModelConfig { Path = Path.Combine(TestEnv.Root, "missing.onnx") },
                new ModelConfig { Path = bad }
            };
            var m = AiDetectionManager.Instance;
            m.ApplySettings();
            try
            {
                Assert.IsTrue(WaitFor(() => m.State == AiEngineState.ModelError || m.State == AiEngineState.NoModel, 10000), m.State.ToString());
                Assert.AreEqual(AiEngineState.ModelError, m.State, m.StatusMessage);
                Assert.AreEqual("File not found", m.Models[0].Status);
                StringAssert.StartsWith(m.Models[1].Status, "Load error");
                Assert.IsFalse(m.IsOperational("fire"));
            }
            finally
            {
                m.Stop();
            }
        }

        [TestMethod]
        public void Engine_OneAlertPerEpisode_FiltersClearAlerts_EvidenceSaved()
        {
            AiSettings.Enabled = true;
            AiSettings.Models = new List<ModelConfig>
            {
                new ModelConfig { Path = TestEnv.Data("fixture_fire_smoke.onnx") },
                new ModelConfig { Path = TestEnv.Data("fixture_casualty_flood_car.onnx") }
            };
            AiSettings.DisabledCategories = new HashSet<string>();
            AiSettings.AnalysisFps = 10;
            AiSettings.ConfirmFrames = 2;
            AiSettings.ClearSeconds = 2;
            AiSettings.SaveScreenshots = true;

            var raised = new List<DetectionEvent>();
            var cleared = new List<DetectionEvent>();
            var m = AiDetectionManager.Instance;
            Action<DetectionEvent> onRaise = e => { lock (raised) raised.Add(e); };
            Action<DetectionEvent> onClear = e => { lock (cleared) cleared.Add(e); };
            m.AlertRaised += onRaise;
            m.AlertCleared += onClear;
            var stop = false;
            var feeder = new Thread(() =>
            {
                while (!stop)
                {
                    InjectFrame(new Bitmap(640, 360));
                    Thread.Sleep(60);
                }
            }) { IsBackground = true };
            m.ApplySettings();
            feeder.Start();
            try
            {
                Assert.IsTrue(WaitFor(() => m.State == AiEngineState.Running, 15000), "engine did not start: " + m.StatusMessage);
                Assert.IsTrue(m.IsOperational("fire") && m.IsOperational("smoke") && m.IsOperational("injured_person") && m.IsOperational("flood"));
                Assert.IsFalse(m.IsOperational("damaged_building"), "no model covers damaged buildings -> not operational");

                Assert.IsTrue(WaitFor(() => { lock (raised) return raised.Count >= 4; }, 10000), "alerts raised: " + raised.Count);
                Thread.Sleep(2500); // many more frames with the same detections
                lock (raised)
                {
                    Assert.AreEqual(4, raised.Count, "exactly one alert per continuously detected category");
                    CollectionAssert.AreEquivalent(new[] { "fire", "smoke", "injured_person", "flood" }, raised.Select(e => e.Category.Id).ToArray());
                    foreach (var e in raised)
                    {
                        Assert.IsTrue(File.Exists(e.ScreenshotPath), "captured frame saved for " + e.Category.Id);
                        Assert.AreEqual(DetectionStatus.Active, e.Status);
                    }
                }

                Assert.IsTrue(m.LatestDetections.All(d => d.Category != null), "uncategorised classes (car) never reach the UI");

                // operator filters out flood -> its alert ends immediately and it is no longer drawn/alerted
                AiSettings.DisabledCategories = new HashSet<string> { "flood" };
                Assert.IsTrue(WaitFor(() => { lock (cleared) return cleared.Any(e => e.Category.Id == "flood"); }, 5000), "flood not cleared");
                Assert.IsTrue(WaitFor(() => m.LatestDetections.Count > 0 && m.LatestDetections.All(d => d.Category.Id != "flood"), 5000));
                Assert.AreEqual(3, m.ActiveAlerts.Count);

                m.AcknowledgeAll();
                Assert.IsTrue(m.ActiveAlerts.All(a => a.Status == DetectionStatus.Acknowledged));
                Assert.IsFalse(m.UnacknowledgedAlert);

                var csv = File.ReadAllText(Path.Combine(AiSettings.DetectionsDirectory, "detections.csv"));
                StringAssert.Contains(csv, ",ALERT,fire,");
                StringAssert.Contains(csv, ",CLEARED,flood,");
            }
            finally
            {
                stop = true;
                feeder.Join(2000);
                m.AlertRaised -= onRaise;
                m.AlertCleared -= onClear;
                m.Stop();
                AiSettings.DisabledCategories = new HashSet<string>();
            }
        }
    }
}
