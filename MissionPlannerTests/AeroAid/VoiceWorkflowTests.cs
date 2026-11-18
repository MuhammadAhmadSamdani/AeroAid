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
    /// <summary>Vehicle-state validation of voice ARM / DISARM / TAKEOFF. No commands are sent.</summary>
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

        /// <summary>A healthy, disarmed copter on the ground.</summary>
        internal static TelemetrySnapshot OnGround() => new TelemetrySnapshot
        {
            Connected = true,
            LinkAgeSeconds = 0.2,
            Firmware = Firmwares.ArduCopter2,
            IsCopter = true,
            Armed = false,
            Landed = true,
            LandedState = 1,
            Mode = "Guided",
            GpsFix = 3,
            Satellites = 12,
            EkfVariance = 0.05,
            PrearmOk = true,
            BatteryPercent = 90,
            BatteryVoltage = 12.4
        };

        /// <summary>Armed on the ground (cs.landed is false once armed - like the real telemetry).</summary>
        internal static TelemetrySnapshot ArmedOnGround()
        {
            var t = OnGround();
            t.Armed = true;
            t.Landed = false;
            return t;
        }

        internal static TelemetrySnapshot Flying()
        {
            var t = ArmedOnGround();
            t.LandedState = 2;
            t.AltRelative = 15;
            return t;
        }

        private static List<string> Fails(VoiceCommand c, Func<TelemetrySnapshot> make, Action<TelemetrySnapshot> change = null)
        {
            var t = make();
            change?.Invoke(t);
            return VoiceSafety.Check(c, t, new List<string>());
        }

        [TestMethod]
        public void ReadyVehiclePassesEachCommandsChecks()
        {
            var passed = new List<string>();
            Assert.AreEqual(0, VoiceSafety.Check(VoiceCommand.Arm, OnGround(), passed).Count, "arm");
            Assert.IsTrue(passed.Count >= 7);
            Assert.AreEqual(0, VoiceSafety.Check(VoiceCommand.Takeoff, ArmedOnGround(), new List<string>()).Count, "takeoff");
            Assert.AreEqual(0, VoiceSafety.Check(VoiceCommand.Disarm, ArmedOnGround(), new List<string>()).Count, "disarm");
        }

        [TestMethod]
        public void NothingIsAllowedWhileDisconnected()
        {
            foreach (var c in new[] { VoiceCommand.Arm, VoiceCommand.Disarm, VoiceCommand.Takeoff })
            {
                var f = Fails(c, ArmedOnGround, t => t.Connected = false);
                Assert.AreEqual(1, f.Count, c.ToString());
                StringAssert.Contains(f[0], "not connected");
                StringAssert.Contains(Fails(c, ArmedOnGround, t => t.LinkAgeSeconds = 10)[0], "heartbeat");
            }
        }

        [TestMethod]
        public void ArmChecks()
        {
            Assert.IsTrue(Fails(VoiceCommand.Arm, ArmedOnGround).Any(f => f.Contains("already armed")));
            Assert.IsTrue(Fails(VoiceCommand.Arm, OnGround, t => t.PrearmOk = false).Any(f => f.Contains("Pre-arm")));
            Assert.IsTrue(Fails(VoiceCommand.Arm, OnGround, t => t.Failsafe = true).Any(f => f.Contains("failsafe")));
            Assert.IsTrue(Fails(VoiceCommand.Arm, OnGround, t => t.BatteryPercent = 10).Any(f => f.Contains("below the minimum")));
            Assert.IsTrue(Fails(VoiceCommand.Arm, OnGround, t => t.BatteryPercent = -1).Any(f => f.Contains("unknown")));
            Assert.IsTrue(Fails(VoiceCommand.Arm, OnGround, t => t.Mode = "Auto").Any(f => f.Contains("AUTO")));
            Assert.IsTrue(Fails(VoiceCommand.Arm, OnGround, t => t.Mode = "RTL").Any(f => f.Contains("RTL")));
            // GUIDED needs a position estimate...
            Assert.IsTrue(Fails(VoiceCommand.Arm, OnGround, t => t.GpsFix = 1).Any(f => f.Contains("3D fix")));
            Assert.IsTrue(Fails(VoiceCommand.Arm, OnGround, t => t.EkfVariance = 0.9).Any(f => f.Contains("EKF")));
            // ...STABILIZE does not (ArduPilot still applies its own checks)
            Assert.AreEqual(0, Fails(VoiceCommand.Arm, OnGround, t => { t.Mode = "Stabilize"; t.GpsFix = 1; }).Count);
        }

        [TestMethod]
        public void DisarmChecksNeverAllowAnAirborneVehicle()
        {
            Assert.IsTrue(Fails(VoiceCommand.Disarm, OnGround).Any(f => f.Contains("already disarmed")));
            Assert.IsTrue(Fails(VoiceCommand.Disarm, Flying).Any(f => f.Contains("AIRBORNE")));
            // each airborne indicator on its own is enough
            Assert.IsTrue(Fails(VoiceCommand.Disarm, ArmedOnGround, t => t.LandedState = 2).Any(f => f.Contains("AIRBORNE")), "landed_state in air");
            Assert.IsTrue(Fails(VoiceCommand.Disarm, ArmedOnGround, t => t.LandedState = 3).Any(f => f.Contains("AIRBORNE")), "taking off");
            Assert.IsTrue(Fails(VoiceCommand.Disarm, ArmedOnGround, t => t.LandedState = 4).Any(f => f.Contains("AIRBORNE")), "landing");
            Assert.IsTrue(Fails(VoiceCommand.Disarm, ArmedOnGround, t => { t.LandedState = 0; t.AltRelative = 3; }).Any(f => f.Contains("AIRBORNE")), "altitude");
            Assert.IsTrue(Fails(VoiceCommand.Disarm, ArmedOnGround, t => { t.LandedState = 1; t.ClimbRate = -1.2; }).Any(f => f.Contains("AIRBORNE")), "descending");
            // a failing pre-arm / low battery must NOT prevent disarming on the ground
            Assert.AreEqual(0, Fails(VoiceCommand.Disarm, ArmedOnGround, t => { t.PrearmOk = false; t.BatteryPercent = 5; t.GpsFix = 0; }).Count);
        }

        [TestMethod]
        public void TakeoffChecks()
        {
            Assert.IsTrue(Fails(VoiceCommand.Takeoff, OnGround).Any(f => f.Contains("not armed")), "must be armed first");
            Assert.IsTrue(Fails(VoiceCommand.Takeoff, Flying).Any(f => f.Contains("flying")));
            Assert.IsTrue(Fails(VoiceCommand.Takeoff, ArmedOnGround, t => { t.IsCopter = false; t.Firmware = Firmwares.ArduPlane; }).Any(f => f.Contains("ArduCopter")));
            Assert.IsTrue(Fails(VoiceCommand.Takeoff, ArmedOnGround, t => t.Mode = "Auto").Any(f => f.Contains("not allowed")));
            Assert.IsTrue(Fails(VoiceCommand.Takeoff, ArmedOnGround, t => t.Mode = "Land").Any(f => f.Contains("not allowed")));
            Assert.IsTrue(Fails(VoiceCommand.Takeoff, ArmedOnGround, t => t.GpsFix = 2).Any(f => f.Contains("3D fix")));
            Assert.IsTrue(Fails(VoiceCommand.Takeoff, ArmedOnGround, t => t.Satellites = 4).Any(f => f.Contains("satellites")));
            Assert.IsTrue(Fails(VoiceCommand.Takeoff, ArmedOnGround, t => t.EkfVariance = 0.9).Any(f => f.Contains("EKF")));
            Assert.IsTrue(Fails(VoiceCommand.Takeoff, ArmedOnGround, t => t.PrearmOk = false).Any(f => f.Contains("Pre-arm")));
            Assert.IsTrue(Fails(VoiceCommand.Takeoff, ArmedOnGround, t => t.BatteryPercent = 20).Any(f => f.Contains("below the minimum")));
            Assert.IsTrue(Fails(VoiceCommand.Takeoff, ArmedOnGround, t => t.Failsafe = true).Any(f => f.Contains("failsafe")));
            // LOITER is fine: the takeoff switches to GUIDED
            var passed = new List<string>();
            var t2 = ArmedOnGround();
            t2.Mode = "Loiter";
            Assert.AreEqual(0, VoiceSafety.Check(VoiceCommand.Takeoff, t2, passed).Count);
            Assert.IsTrue(passed.Any(p => p.Contains("will switch to GUIDED")));
        }

        [TestMethod]
        public void TakeoffAltitudeIsLimited()
        {
            var alt = AeroSettings.TakeoffAltitude;
            try
            {
                AeroSettings.TakeoffAltitude = 5;
                var passed = new List<string>();
                VoiceSafety.Check(VoiceCommand.Takeoff, ArmedOnGround(), passed);
                Assert.IsTrue(passed.Any(p => p.StartsWith("Takeoff altitude 5 m")));
                Assert.IsTrue(VoiceSafety.TakeoffAltitude() >= 1 && VoiceSafety.TakeoffAltitude() <= AeroSettings.MaxAltitude);
            }
            finally
            {
                AeroSettings.TakeoffAltitude = alt;
            }
        }

        [TestMethod]
        public void PrearmNotReportedIsPassedToArduPilotChecks()
        {
            var passed = new List<string>();
            var t = ArmedOnGround();
            t.PrearmOk = null;
            Assert.AreEqual(0, VoiceSafety.Check(VoiceCommand.Takeoff, t, passed).Count);
            Assert.IsTrue(passed.Any(p => p.Contains("ArduPilot still enforces")));
        }

        [TestMethod]
        public void VoiceSettingsAreBounded()
        {
            VoiceSettings.MinConfidence = 0.01;
            Assert.AreEqual(0.3, VoiceSettings.MinConfidence, 1e-9, "confidence threshold cannot be disabled");
            VoiceSettings.MinConfidence = 0.65;
            VoiceSettings.CooldownSeconds = 0;
            Assert.AreEqual(5, VoiceSettings.CooldownSeconds, "command cool-down has a minimum");
            VoiceSettings.CooldownSeconds = 10;
            VoiceSettings.IdleTimeoutSeconds = -5;
            Assert.AreEqual(0, VoiceSettings.IdleTimeoutSeconds, "0 = continuous listening");
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
