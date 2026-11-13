using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using log4net;
using MissionPlanner.AeroAid.UI;
using MissionPlanner.ArduPilot;

namespace MissionPlanner.AeroAid.Telemetry
{
    /// <summary>Immutable copy of the live vehicle state taken from MainV2.comPort.MAV.cs.</summary>
    public sealed class TelemetrySnapshot
    {
        public bool Connected;
        public double LinkAgeSeconds = double.PositiveInfinity;
        public int LinkQualityPercent = -1;
        public uint SysId;
        public byte CompId;
        public string PortName = "";
        public int Baud;
        public string VehicleType = "Unknown";
        public Firmwares Firmware;
        public bool IsCopter;
        public bool IsPlane;
        public string Mode = "";
        public bool Armed;
        public bool Landed;
        public bool Failsafe;
        public int BatteryPercent = -1;
        public double BatteryVoltage;
        public double BatteryCurrent;
        public int GpsFix;
        public int Satellites;
        public double Hdop;
        public double Lat;
        public double Lng;
        public double AltRelative;
        public double GroundSpeed;
        public double AirSpeed;
        public double Heading;
        public double Roll;
        public double Pitch;
        public double Yaw;
        public double ClimbRate;
        public double DistToHome;
        public double EkfVariance;
        /// <summary>null = vehicle does not report a pre-arm health bit.</summary>
        public bool? PrearmOk;
        public string LastHighMessage = "";
        public IReadOnlyList<(DateTime time, string message)> Messages = Array.Empty<(DateTime, string)>();
        public string FirmwareVersion = "";

        public bool HasHeartbeat => Connected && LinkAgeSeconds < 3.0;
        public bool HasPosition => GpsFix >= 2 && (Math.Abs(Lat) > 1e-7 || Math.Abs(Lng) > 1e-7);

        // ---------- status grading (green / yellow / red / grey) ----------
        public StatusLevel ConnectionLevel =>
            !Connected ? StatusLevel.Unavailable
            : LinkAgeSeconds > 3 ? StatusLevel.Critical
            : LinkAgeSeconds > 1.5 ? StatusLevel.Warning
            : StatusLevel.Ok;

        public string ConnectionText =>
            !Connected ? "Disconnected"
            : LinkAgeSeconds > 3 ? "No heartbeat"
            : LinkAgeSeconds > 1.5 ? "Link degraded"
            : "Connected";

        public StatusLevel GpsLevel =>
            !HasHeartbeat ? StatusLevel.Unavailable
            : GpsFix < 2 ? StatusLevel.Critical
            : GpsFix == 2 || Satellites < 6 ? StatusLevel.Warning
            : StatusLevel.Ok;

        public string GpsText
        {
            get
            {
                if (!HasHeartbeat) return "--";
                switch (GpsFix)
                {
                    case 0: return "No GPS";
                    case 1: return "No fix";
                    case 2: return "2D fix";
                    case 3: return "3D fix";
                    case 4: return "DGPS";
                    case 5: return "RTK float";
                    case 6: return "RTK fixed";
                    default: return "Fix " + GpsFix;
                }
            }
        }

        public StatusLevel BatteryLevel =>
            !HasHeartbeat || (BatteryPercent < 0 && BatteryVoltage <= 0) ? StatusLevel.Unavailable
            : BatteryPercent < 0 ? StatusLevel.Warning // voltage only - capacity unknown
            : BatteryPercent < 20 ? StatusLevel.Critical
            : BatteryPercent < 35 ? StatusLevel.Warning
            : StatusLevel.Ok;

        public StatusLevel EkfLevel =>
            !HasHeartbeat ? StatusLevel.Unavailable
            : EkfVariance > 0.8 ? StatusLevel.Critical
            : EkfVariance > 0.5 ? StatusLevel.Warning
            : StatusLevel.Ok;

        public StatusLevel ArmedLevel =>
            !HasHeartbeat ? StatusLevel.Unavailable : Armed ? StatusLevel.Warning : StatusLevel.Ok;

        public StatusLevel ModeLevel =>
            !HasHeartbeat ? StatusLevel.Unavailable : Failsafe ? StatusLevel.Critical : StatusLevel.Ok;

        public StatusLevel PrearmLevel =>
            !HasHeartbeat || PrearmOk == null ? StatusLevel.Unavailable
            : PrearmOk.Value ? StatusLevel.Ok : StatusLevel.Critical;
    }

    /// <summary>
    /// Polls the existing Mission Planner telemetry model (no second MAVLink connection) on the UI thread
    /// at 5 Hz while at least one subscriber is listening.
    /// </summary>
    public static class TelemetryManager
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly Timer timer = new Timer { Interval = 200 };
        private static event Action<TelemetrySnapshot> updated;
        private static int subscribers;

        public static TelemetrySnapshot Latest { get; private set; } = new TelemetrySnapshot();

        static TelemetryManager()
        {
            timer.Tick += (s, e) => Poll();
        }

        /// <summary>Subscribe on the UI thread; delivers snapshots on the UI thread.</summary>
        public static event Action<TelemetrySnapshot> Updated
        {
            add
            {
                updated += value;
                subscribers++;
                if (!timer.Enabled)
                    timer.Start();
                Poll();
            }
            remove
            {
                updated -= value;
                subscribers = Math.Max(0, subscribers - 1);
                if (subscribers == 0)
                    timer.Stop();
            }
        }

        /// <summary>Take a fresh snapshot now (any thread).</summary>
        public static TelemetrySnapshot Capture()
        {
            var t = new TelemetrySnapshot();
            try
            {
                var port = MainV2.comPort;
                if (port == null)
                    return t;
                var mav = port.MAV;
                var cs = mav.cs;
                t.Connected = port.BaseStream != null && port.BaseStream.IsOpen;
                t.PortName = MainV2.comPortName ?? "";
                t.Baud = MainV2.comPortBaud;
                t.SysId = mav.sysid;
                t.CompId = mav.compid;
                // MAVState.lastvalidpacket is stored in UTC
                if (t.Connected && mav.lastvalidpacket != DateTime.MinValue)
                    t.LinkAgeSeconds = Math.Max(0, (DateTime.UtcNow - mav.lastvalidpacket).TotalSeconds);
                t.LinkQualityPercent = t.Connected ? cs.linkqualitygcs : -1;
                t.Firmware = cs.firmware;
                t.IsCopter = cs.firmware == Firmwares.ArduCopter2;
                t.IsPlane = cs.firmware == Firmwares.ArduPlane;
                t.VehicleType = FormatVehicleType(mav.aptype.ToString(), cs.firmware);
                t.FirmwareVersion = mav.VersionString ?? "";
                t.Mode = cs.mode ?? "";
                t.Armed = cs.armed;
                t.Landed = cs.landed;
                t.Failsafe = cs.failsafe;
                t.BatteryPercent = cs.battery_remaining > 0 && cs.battery_remaining <= 100 ? cs.battery_remaining : -1;
                t.BatteryVoltage = cs.battery_voltage;
                t.BatteryCurrent = cs.current;
                t.GpsFix = (int)cs.gpsstatus;
                t.Satellites = (int)cs.satcount;
                t.Hdop = cs.gpshdop;
                t.Lat = cs.lat;
                t.Lng = cs.lng;
                t.AltRelative = cs.alt;
                t.GroundSpeed = cs.groundspeed;
                t.AirSpeed = cs.airspeed;
                t.Heading = (cs.yaw % 360 + 360) % 360;
                t.Roll = cs.roll;
                t.Pitch = cs.pitch;
                t.Yaw = cs.yaw;
                t.ClimbRate = cs.climbrate;
                t.DistToHome = cs.DistToHome;
                t.EkfVariance = cs.ekfstatus;
                if (cs.sensors_present.seen && cs.sensors_present.prearm)
                    t.PrearmOk = cs.sensors_health.prearm;
                t.LastHighMessage = cs.messageHigh ?? "";
                try
                {
                    t.Messages = cs.messages.ToArray().Reverse().Take(200).ToArray();
                }
                catch (Exception)
                {
                    // list is appended by the MAVLink reader thread; a torn read is retried on the next poll
                }
            }
            catch (Exception ex)
            {
                log.Debug("Telemetry capture failed", ex);
            }

            return t;
        }

        private static string FormatVehicleType(string aptype, Firmwares fw)
        {
            if (string.IsNullOrEmpty(aptype) || aptype == "GENERIC")
                return fw.ToString();
            var s = aptype.Replace('_', ' ').ToLowerInvariant();
            return char.ToUpperInvariant(s[0]) + s.Substring(1);
        }

        private static void Poll()
        {
            Latest = Capture();
            try
            {
                updated?.Invoke(Latest);
            }
            catch (Exception ex)
            {
                log.Error("Telemetry subscriber failed", ex);
            }
        }
    }
}
