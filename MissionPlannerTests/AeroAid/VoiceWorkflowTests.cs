using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MissionPlanner.AeroAid.Audio;
using MissionPlanner.AeroAid.Telemetry;
using MissionPlanner.AeroAid.Voice;
using MissionPlanner.ArduPilot;

namespace MissionPlanner.AeroAid.Tests
{
    /// <summary>Voice-takeoff readiness validation. No commands are sent.</summary>
    [TestClass]
    public class VoiceWorkflowTests
    {
        [ClassInitialize]
        public static void Init(TestContext _)
        {
            TestEnv.Init();
            AeroSettings.MinSatellites = 6;
            AeroSettings.MinBatteryPercent = 30;
        }

        private static TelemetrySnapshot Ready() => new TelemetrySnapshot
        {
            Connected = true,
            LinkAgeSeconds = 0.2,
            Firmware = Firmwares.ArduCopter2,
            IsCopter = true,
            Armed = false,
            Landed = true,
            Mode = "Loiter",
            GpsFix = 3,
            Satellites = 12,
            EkfVariance = 0.05,
            PrearmOk = true,
            BatteryPercent = 90,
            BatteryVoltage = 12.4
        };

        private static List<string> Fails(Action<TelemetrySnapshot> change)
        {
            var t = Ready();
            change?.Invoke(t);
            return VoiceTakeoffWorkflow.Check(t, new List<string>());
        }

        [TestMethod]
        public void ReadyVehiclePassesAllChecks()
        {
            var passed = new List<string>();
            Assert.AreEqual(0, VoiceTakeoffWorkflow.Check(Ready(), passed).Count);
            Assert.IsTrue(passed.Count >= 7);
        }

        [TestMethod]
        public void EachUnsafeConditionBlocksTakeoff()
        {
            Assert.AreEqual(1, Fails(t => t.Connected = false).Count, "disconnected");
            Assert.IsTrue(Fails(t => t.LinkAgeSeconds = 10).Any(f => f.Contains("heartbeat")));
            Assert.IsTrue(Fails(t => { t.IsCopter = false; t.Firmware = Firmwares.ArduPlane; }).Any(f => f.Contains("ArduCopter")));
            Assert.IsTrue(Fails(t => { t.Armed = true; t.Landed = false; t.AltRelative = 12; }).Any(f => f.Contains("flying")));
            Assert.IsTrue(Fails(t => t.GpsFix = 2).Any(f => f.Contains("3D fix")));
            Assert.IsTrue(Fails(t => t.Satellites = 4).Any(f => f.Contains("satellites")));
            Assert.IsTrue(Fails(t => t.EkfVariance = 0.9).Any(f => f.Contains("EKF")));
            Assert.IsTrue(Fails(t => t.PrearmOk = false).Any(f => f.Contains("Pre-arm")));
            Assert.IsTrue(Fails(t => t.BatteryPercent = -1).Any(f => f.Contains("unknown")));
            Assert.IsTrue(Fails(t => t.BatteryPercent = 20).Any(f => f.Contains("below the minimum")));
            Assert.IsTrue(Fails(t => t.Failsafe = true).Any(f => f.Contains("failsafe")));
        }

        [TestMethod]
        public void PrearmNotReportedIsPassedToArduPilotChecks()
        {
            var passed = new List<string>();
            var t = Ready();
            t.PrearmOk = null;
            Assert.AreEqual(0, VoiceTakeoffWorkflow.Check(t, passed).Count);
            Assert.IsTrue(passed.Any(p => p.Contains("ArduPilot will still enforce")));
        }

        [TestMethod]
        public void GrammarContainsOnlyTakeoffPhrases()
        {
            CollectionAssert.Contains(VoiceCommandEngine.TakeoffPhrases, "take off");
            CollectionAssert.Contains(VoiceCommandEngine.TakeoffPhrases, "drone take off");
            CollectionAssert.Contains(VoiceCommandEngine.TakeoffPhrases, "initiate takeoff");
            Assert.IsTrue(VoiceCommandEngine.TakeoffPhrases.All(p => p.Contains("take")), "no unrelated commands in the grammar");
        }

        [TestMethod]
        public void VoiceSettingsAreBounded()
        {
            VoiceSettings.MinConfidence = 0.01;
            Assert.AreEqual(0.3, VoiceSettings.MinConfidence, 1e-9, "confidence threshold cannot be disabled");
            VoiceSettings.MinConfidence = 0.75;
            VoiceSettings.CooldownSeconds = 0;
            Assert.AreEqual(5, VoiceSettings.CooldownSeconds, "repeat cool-down has a minimum");
            VoiceSettings.CooldownSeconds = 30;
        }

        [TestMethod]
        public void MicrophoneEnumerationIncludesSystemDefault()
        {
            var devices = WaveInCapture.Devices();
            Assert.AreEqual(-1, devices[0].id);
            Assert.IsTrue(devices.Count >= 1);
        }

        [TestMethod]
        public void MissingSelectedMicrophoneIsReportedNotSilentlyReplaced()
        {
            MicrophoneManager.SelectedDeviceName = "Device that does not exist 123";
            try
            {
                Assert.IsFalse(MicrophoneManager.Instance.TurnOn());
                Assert.IsTrue(MicrophoneManager.Instance.HasError);
                StringAssert.Contains(MicrophoneManager.Instance.Status, "not found");
            }
            finally
            {
                MicrophoneManager.SelectedDeviceName = "";
            }
        }
    }
}
