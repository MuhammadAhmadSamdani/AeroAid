using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MissionPlanner.AeroAid.Flight;
using MissionPlanner.AeroAid.Telemetry;
using MissionPlanner.ArduPilot;

namespace MissionPlanner.AeroAid.Tests
{
    [TestClass]
    public class FlightControlTests
    {
        [ClassInitialize]
        public static void Init(TestContext _) => TestEnv.Init();

        private static TelemetrySnapshot Flying(double alt = 10) => new TelemetrySnapshot
        {
            Connected = true,
            LinkAgeSeconds = 0.2,
            Firmware = Firmwares.ArduCopter2,
            IsCopter = true,
            Armed = true,
            Landed = false,
            Mode = "Guided",
            AltRelative = alt,
            SysId = 1,
            CompId = 1
        };

        private static MAVLink.mavlink_set_position_target_local_ned_t Cmd(TelemetrySnapshot t, params MoveDirection[] d) =>
            FlightControlManager.BuildVelocityCommand(t, d, 2.0, 1.0, 30.0, 30.0, 2.0, out _);

        [TestMethod]
        public void VelocityCommand_UsesBodyFrameAndVelocityYawRateMask()
        {
            var m = Cmd(Flying(), MoveDirection.Forward);
            Assert.AreEqual((byte)MAVLink.MAV_FRAME.BODY_OFFSET_NED, m.coordinate_frame);
            Assert.AreEqual((ushort)0x05C7, m.type_mask, "velocity + yaw-rate mask (pos/accel/yaw ignored)");
            Assert.AreEqual(1, m.target_system);
            Assert.AreEqual(1, m.target_component);
        }

        [TestMethod]
        public void VelocityCommand_DirectionsAndSigns()
        {
            var t = Flying();
            Assert.AreEqual(2f, Cmd(t, MoveDirection.Forward).vx, 1e-4);
            Assert.AreEqual(-2f, Cmd(t, MoveDirection.Backward).vx, 1e-4);
            Assert.AreEqual(2f, Cmd(t, MoveDirection.Right).vy, 1e-4);
            Assert.AreEqual(-2f, Cmd(t, MoveDirection.Left).vy, 1e-4);
            Assert.AreEqual(-1f, Cmd(t, MoveDirection.Up).vz, 1e-4, "NED: up is negative z");
            Assert.AreEqual(1f, Cmd(t, MoveDirection.Down).vz, 1e-4);
            Assert.AreEqual(30 * Math.PI / 180, Cmd(t, MoveDirection.YawRight).yaw_rate, 1e-4, "clockwise positive, rad/s");
            Assert.AreEqual(-30 * Math.PI / 180, Cmd(t, MoveDirection.YawLeft).yaw_rate, 1e-4);
        }

        [TestMethod]
        public void VelocityCommand_NoDirections_IsZeroHold()
        {
            var m = Cmd(Flying());
            Assert.AreEqual(0f, m.vx);
            Assert.AreEqual(0f, m.vy);
            Assert.AreEqual(0f, m.vz);
            Assert.AreEqual(0f, m.yaw_rate);
        }

        [TestMethod]
        public void VelocityCommand_DiagonalLimitedToMaxHorizontalSpeed()
        {
            var m = Cmd(Flying(), MoveDirection.Forward, MoveDirection.Right);
            Assert.AreEqual(2.0, Math.Sqrt(m.vx * m.vx + m.vy * m.vy), 1e-4);
        }

        [TestMethod]
        public void VelocityCommand_ClimbBlockedAtMaxAltitude_DescentBlockedAtMinAltitude()
        {
            var up = FlightControlManager.BuildVelocityCommand(Flying(30), new[] { MoveDirection.Up }, 2, 1, 30, 30, 2, out var note1);
            Assert.AreEqual(0f, up.vz);
            StringAssert.Contains(note1, "Climb blocked");
            var down = FlightControlManager.BuildVelocityCommand(Flying(1.5), new[] { MoveDirection.Down }, 2, 1, 30, 30, 2, out var note2);
            Assert.AreEqual(0f, down.vz);
            StringAssert.Contains(note2, "Descent blocked");
            // horizontal movement is still allowed at the envelope
            var fwd = FlightControlManager.BuildVelocityCommand(Flying(30), new[] { MoveDirection.Forward, MoveDirection.Up }, 2, 1, 30, 30, 2, out _);
            Assert.AreEqual(2f, fwd.vx, 1e-4);
        }

        [TestMethod]
        public void SettingsHardCapsCannotBeExceeded()
        {
            AeroSettings.MaxHorizontalSpeed = 50;
            AeroSettings.MaxVerticalSpeed = 50;
            AeroSettings.MaxYawRate = 999;
            AeroSettings.MaxAltitude = 10000;
            Assert.AreEqual(AeroSettings.HardMaxHorizontalSpeed, AeroSettings.MaxHorizontalSpeed);
            Assert.AreEqual(AeroSettings.HardMaxVerticalSpeed, AeroSettings.MaxVerticalSpeed);
            Assert.AreEqual(AeroSettings.HardMaxYawRate, AeroSettings.MaxYawRate);
            Assert.AreEqual(AeroSettings.HardMaxAltitude, AeroSettings.MaxAltitude);
            AeroSettings.MaxHorizontalSpeed = 2;
            AeroSettings.MaxVerticalSpeed = 1;
            AeroSettings.MaxYawRate = 30;
            AeroSettings.MaxAltitude = 30;
        }

        [TestMethod]
        public void CanMove_RequiresLinkCopterArmedGuidedAirborneNoFailsafe()
        {
            Assert.IsTrue(FlightControlManager.CanMove(Flying(), out _));
            void Refused(Action<TelemetrySnapshot> change, string why)
            {
                var t = Flying();
                change(t);
                Assert.IsFalse(FlightControlManager.CanMove(t, out var reason), why);
                Assert.IsFalse(string.IsNullOrEmpty(reason), "a reason must be shown: " + why);
            }

            Refused(t => t.Connected = false, "disconnected");
            Refused(t => t.LinkAgeSeconds = 5, "no heartbeat");
            Refused(t => { t.IsCopter = false; t.Firmware = Firmwares.ArduPlane; }, "plane");
            Refused(t => t.Armed = false, "disarmed");
            Refused(t => t.Mode = "Loiter", "not guided");
            Refused(t => t.Failsafe = true, "failsafe");
            Refused(t => t.Landed = true, "on the ground");
        }

        [TestMethod]
        public void BeginMove_WithoutVehicle_IsRefused()
        {
            Assert.IsFalse(FlightControlManager.Instance.BeginMove(MoveDirection.Forward));
            Assert.IsFalse(FlightControlManager.Instance.IsMoving);
        }

        [TestMethod]
        public void ModeSafetyClassification()
        {
            Assert.IsTrue(FlightControlManager.IsGcsSafeHoldMode("Guided"));
            Assert.IsTrue(FlightControlManager.IsGcsSafeHoldMode("BRAKE"));
            foreach (var m in new[] { "Loiter", "AltHold", "PosHold", "Stabilize", "Auto", "RTL", "Land" })
                Assert.IsFalse(FlightControlManager.IsGcsSafeHoldMode(m), m);
            foreach (var m in new[] { "Loiter", "AltHold", "ALT_HOLD", "PosHold", "Stabilize", "Acro", "Sport" })
                Assert.IsTrue(FlightControlManager.RequiresRcInput(m), m);
            foreach (var m in new[] { "Guided", "Auto", "RTL", "Land", "Brake" })
                Assert.IsFalse(FlightControlManager.RequiresRcInput(m), m);
        }

        [TestMethod]
        public void AvailableModes_ComeFromFirmwareModeList()
        {
            var modes = FlightControlManager.AvailableModes(Flying());
            foreach (var m in new[] { "Stabilize", "AltHold", "Loiter", "Guided", "Auto", "RTL", "Land" })
                Assert.IsTrue(modes.Any(x => string.Equals(x, m, StringComparison.OrdinalIgnoreCase)), m);
            Assert.AreEqual(0, FlightControlManager.AvailableModes(new TelemetrySnapshot()).Count, "no modes while disconnected");
        }
    }
}
