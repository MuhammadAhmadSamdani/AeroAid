using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using MissionPlanner.AeroAid.Telemetry;
using MissionPlanner.AeroAid.UI;
using MissionPlanner.ArduPilot;

namespace MissionPlanner.AeroAid.Flight
{
    public enum MoveDirection
    {
        Forward,
        Backward,
        Left,
        Right,
        Up,
        Down,
        YawLeft,
        YawRight
    }

    /// <summary>
    /// Button/keyboard flight control built on the existing MAVLink link (MainV2.comPort).
    ///
    /// Movement uses SET_POSITION_TARGET_LOCAL_NED in MAV_FRAME_BODY_OFFSET_NED with a
    /// velocity + yaw-rate type mask (ArduCopter GUIDED mode). Safety rules:
    ///  - only ArduCopter, only GUIDED, only armed, only with a live heartbeat;
    ///  - speeds are bounded by AeroSettings (which are themselves clamped to hard caps);
    ///  - climbing stops at the configured max altitude, descending stops at the min altitude (use LAND);
    ///  - commands are streamed at 5 Hz ONLY while a control is held; on release a zero-velocity
    ///    (hold position) command is sent; ArduCopter also stops by itself if updates cease;
    ///  - any failed precondition immediately releases all held controls.
    /// No ArduPilot failsafe, fence or arming check is bypassed: arming is never forced.
    /// </summary>
    public sealed class FlightControlManager
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        public static FlightControlManager Instance { get; } = new FlightControlManager();

        // velocity + yaw rate: ignore position (bits 0-2), acceleration (6-8) and yaw angle (10)
        private const ushort TypeMaskVelocityYawRate = 0x05C7;
        private const int StreamIntervalMs = 200;
        private const int StopRepeatCount = 3;

        private readonly object sync = new object();
        private readonly HashSet<MoveDirection> held = new HashSet<MoveDirection>();
        private readonly System.Threading.Timer streamTimer;
        private int stopRepeatsRemaining;
        private int commandInFlight;

        /// <summary>User-facing feedback (message, level). Raised on arbitrary threads.</summary>
        public event Action<string, StatusLevel> Feedback;

        /// <summary>Raised when the set of held directions changes. Arbitrary thread.</summary>
        public event Action MovementChanged;

        private FlightControlManager()
        {
            streamTimer = new System.Threading.Timer(_ => StreamTick(), null, Timeout.Infinite, Timeout.Infinite);
        }

        public IReadOnlyCollection<MoveDirection> HeldDirections
        {
            get
            {
                lock (sync) return held.ToArray();
            }
        }

        public bool IsMoving
        {
            get
            {
                lock (sync) return held.Count > 0;
            }
        }

        private void Report(string message, StatusLevel level)
        {
            if (level == StatusLevel.Critical) log.Warn(message);
            else log.Info(message);
            try
            {
                Feedback?.Invoke(message, level);
            }
            catch (Exception ex)
            {
                log.Error("Feedback handler failed", ex);
            }
        }

        // ------------------------------------------------------------------ movement

        /// <summary>Returns true when movement commands may be sent; otherwise a user-readable reason.</summary>
        public static bool CanMove(TelemetrySnapshot t, out string reason)
        {
            if (!t.Connected) { reason = "Not connected to a vehicle."; return false; }
            if (!t.HasHeartbeat) { reason = "No heartbeat from the vehicle."; return false; }
            if (!t.IsCopter) { reason = "Button movement control supports ArduCopter only (connected: " + t.Firmware + ")."; return false; }
            if (!t.Armed) { reason = "Vehicle is disarmed. Arm and take off first."; return false; }
            if (!string.Equals(t.Mode, "Guided", StringComparison.OrdinalIgnoreCase)) { reason = "Switch to GUIDED mode to use movement controls (current: " + t.Mode + ")."; return false; }
            if (t.Failsafe) { reason = "Vehicle failsafe is active - movement disabled."; return false; }
            if (t.Landed) { reason = "Vehicle is on the ground - use Take off first."; return false; }
            reason = "";
            return true;
        }

        /// <summary>Starts moving in a direction while the control is held. Returns false if refused.</summary>
        public bool BeginMove(MoveDirection direction)
        {
            var t = TelemetryManager.Capture();
            if (!CanMove(t, out var reason))
            {
                Report(reason, StatusLevel.Warning);
                StopAll();
                return false;
            }

            bool changed;
            lock (sync)
            {
                changed = held.Add(direction);
                stopRepeatsRemaining = StopRepeatCount;
            }

            if (changed)
            {
                SendCurrentVelocity(t);
                streamTimer.Change(StreamIntervalMs, StreamIntervalMs);
                RaiseMovementChanged();
            }

            return true;
        }

        /// <summary>Releases a direction. When nothing is held a stop (zero velocity) is sent.</summary>
        public void EndMove(MoveDirection direction)
        {
            bool changed;
            lock (sync)
            {
                changed = held.Remove(direction);
            }

            if (!changed)
                return;
            // send the new (possibly zero) velocity immediately; the timer repeats the stop a few times
            SendCurrentVelocity(TelemetryManager.Capture());
            RaiseMovementChanged();
        }

        /// <summary>Releases every held control and commands the vehicle to hold position (if possible).</summary>
        public void StopAll()
        {
            bool hadMovement;
            lock (sync)
            {
                hadMovement = held.Count > 0;
                held.Clear();
            }

            var t = TelemetryManager.Capture();
            if (hadMovement || stopRepeatsRemaining > 0)
            {
                lock (sync) stopRepeatsRemaining = StopRepeatCount;
                if (t.Connected && t.IsCopter && t.Armed && string.Equals(t.Mode, "Guided", StringComparison.OrdinalIgnoreCase))
                {
                    SendVelocity(t, 0, 0, 0, 0);
                    streamTimer.Change(StreamIntervalMs, StreamIntervalMs);
                }
            }

            if (hadMovement)
                RaiseMovementChanged();
        }

        private void RaiseMovementChanged()
        {
            try
            {
                MovementChanged?.Invoke();
            }
            catch (Exception ex)
            {
                log.Error("MovementChanged handler failed", ex);
            }
        }

        private void StreamTick()
        {
            try
            {
                var t = TelemetryManager.Capture();
                bool any;
                lock (sync)
                {
                    any = held.Count > 0;
                    if (!any)
                    {
                        if (stopRepeatsRemaining <= 0)
                        {
                            streamTimer.Change(Timeout.Infinite, Timeout.Infinite);
                            return;
                        }

                        stopRepeatsRemaining--;
                    }
                }

                if (any && !CanMove(t, out var reason))
                {
                    lock (sync) held.Clear();
                    Report("Movement stopped: " + reason, StatusLevel.Warning);
                    RaiseMovementChanged();
                    if (t.Connected && t.IsCopter && t.Armed && string.Equals(t.Mode, "Guided", StringComparison.OrdinalIgnoreCase))
                        SendVelocity(t, 0, 0, 0, 0);
                    return;
                }

                SendCurrentVelocity(t);
            }
            catch (Exception ex)
            {
                log.Error("Velocity stream tick failed", ex);
            }
        }

        private void SendCurrentVelocity(TelemetrySnapshot t)
        {
            MoveDirection[] dirs;
            lock (sync) dirs = held.ToArray();

            if (!t.Connected || !t.IsCopter || !string.Equals(t.Mode, "Guided", StringComparison.OrdinalIgnoreCase))
                return;

            var msg = BuildVelocityCommand(t, dirs, AeroSettings.MaxHorizontalSpeed, AeroSettings.MaxVerticalSpeed,
                AeroSettings.MaxYawRate, AeroSettings.MaxAltitude, AeroSettings.MinAltitude, out var limitNote);
            if (limitNote != null)
                Report(limitNote, StatusLevel.Warning);
            Send(t, msg);
        }

        /// <summary>
        /// Builds the SET_POSITION_TARGET_LOCAL_NED velocity command for the held directions (pure function).
        /// Body frame (MAV_FRAME_BODY_OFFSET_NED): +vx forward, +vy right, +vz DOWN, +yaw_rate clockwise (rad/s).
        /// Combined horizontal speed is limited to <paramref name="maxH"/>; climbing stops at
        /// <paramref name="maxAlt"/> and descending at <paramref name="minAlt"/> (relative altitude).
        /// </summary>
        public static MAVLink.mavlink_set_position_target_local_ned_t BuildVelocityCommand(TelemetrySnapshot t,
            IEnumerable<MoveDirection> dirs, double maxH, double maxV, double maxYawDeg, double maxAlt, double minAlt,
            out string limitNote)
        {
            limitNote = null;
            double vx = 0, vy = 0, vz = 0, yawRate = 0;
            foreach (var d in dirs ?? Enumerable.Empty<MoveDirection>())
            {
                switch (d)
                {
                    case MoveDirection.Forward: vx += maxH; break;
                    case MoveDirection.Backward: vx -= maxH; break;
                    case MoveDirection.Right: vy += maxH; break;
                    case MoveDirection.Left: vy -= maxH; break;
                    case MoveDirection.Up: vz -= maxV; break; // NED: negative z is up
                    case MoveDirection.Down: vz += maxV; break;
                    case MoveDirection.YawRight: yawRate += maxYawDeg; break;
                    case MoveDirection.YawLeft: yawRate -= maxYawDeg; break;
                }
            }

            // limit combined horizontal speed (e.g. forward + right) to the configured maximum
            var hs = Math.Sqrt(vx * vx + vy * vy);
            if (hs > maxH && hs > 0)
            {
                vx *= maxH / hs;
                vy *= maxH / hs;
            }

            // altitude envelope
            if (vz < 0 && t.AltRelative >= maxAlt)
            {
                vz = 0;
                limitNote = $"Climb blocked: altitude limit {maxAlt:0} m reached.";
            }

            if (vz > 0 && t.AltRelative <= minAlt)
            {
                vz = 0;
                limitNote = $"Descent blocked below {minAlt:0.#} m - use LAND to land.";
            }

            return new MAVLink.mavlink_set_position_target_local_ned_t
            {
                time_boot_ms = 0,
                target_system = (byte)t.SysId,
                target_component = t.CompId,
                coordinate_frame = (byte)MAVLink.MAV_FRAME.BODY_OFFSET_NED,
                type_mask = TypeMaskVelocityYawRate,
                vx = (float)vx,
                vy = (float)vy,
                vz = (float)vz,
                yaw_rate = (float)(yawRate * Math.PI / 180.0)
            };
        }

        /// <summary>Velocity + yaw-rate type mask (position, acceleration and yaw angle ignored).</summary>
        public const ushort VelocityTypeMask = TypeMaskVelocityYawRate;

        private static void SendVelocity(TelemetrySnapshot t, double vx, double vy, double vz, double yawRateRad)
        {
            var msg = BuildVelocityCommand(t, new MoveDirection[0], 0, 0, 0, double.MaxValue, double.MinValue, out _);
            msg.vx = (float)vx;
            msg.vy = (float)vy;
            msg.vz = (float)vz;
            msg.yaw_rate = (float)yawRateRad;
            Send(t, msg);
        }

        private static void Send(TelemetrySnapshot t, MAVLink.mavlink_set_position_target_local_ned_t msg)
        {
            try
            {
                MainV2.comPort.sendPacket(msg, t.SysId, t.CompId);
            }
            catch (Exception ex)
            {
                log.Error("Failed to send velocity command", ex);
            }
        }
        // ------------------------------------------------------------------ modes

        /// <summary>Flight modes reported for the connected vehicle's firmware.</summary>
        public static List<string> AvailableModes(TelemetrySnapshot t)
        {
            if (!t.Connected)
                return new List<string>();
            try
            {
                return MissionPlanner.ArduPilot.Common.getModesList(t.Firmware).Select(kv => kv.Value).Where(v => !string.IsNullOrEmpty(v)).ToList();
            }
            catch (Exception ex)
            {
                log.Error("getModesList failed", ex);
                return new List<string>();
            }
        }

        /// <summary>
        /// Modes that hold the vehicle without any pilot (RC) input. Only these may be selected while armed
        /// without a confirmation dialog.
        /// </summary>
        public static bool IsGcsSafeHoldMode(string mode)
        {
            switch ((mode ?? "").ToUpperInvariant())
            {
                case "GUIDED":
                case "BRAKE":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// ArduCopter modes whose throttle/attitude come from the RC transmitter. Without RC input
        /// (GCS-only operation) these can make the vehicle descend or drift - a warning is shown.
        /// </summary>
        public static bool RequiresRcInput(string mode)
        {
            switch ((mode ?? "").ToUpperInvariant().Replace(" ", "_"))
            {
                case "STABILIZE":
                case "ACRO":
                case "ALT_HOLD":
                case "ALTHOLD":
                case "LOITER":
                case "POSHOLD":
                case "SPORT":
                case "DRIFT":
                case "FLOWHOLD":
                case "ZIGZAG":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Holds the current position without RC input: in GUIDED a zero-velocity target is sent,
        /// otherwise the vehicle is switched to GUIDED (which holds position on entry).
        /// </summary>
        public Task<bool> HoldPositionAsync()
        {
            var t = TelemetryManager.Capture();
            if (t.Connected && t.IsCopter && string.Equals(t.Mode, "Guided", StringComparison.OrdinalIgnoreCase))
            {
                StopAll();
                Report("Holding position (GUIDED, zero velocity)", StatusLevel.Ok);
                return Task.FromResult(true);
            }

            return SetModeAsync("Guided");
        }

        /// <summary>Changes mode via the existing MAVLink setMode and waits for the vehicle to report it.</summary>
        public Task<bool> SetModeAsync(string mode)
        {
            var t = TelemetryManager.Capture();
            if (!t.HasHeartbeat)
            {
                Report("Cannot change mode: no vehicle connected.", StatusLevel.Critical);
                return Task.FromResult(false);
            }

            if (!AvailableModes(t).Any(m => string.Equals(m, mode, StringComparison.OrdinalIgnoreCase)))
            {
                Report($"Mode {mode} is not available on {t.Firmware}.", StatusLevel.Warning);
                return Task.FromResult(false);
            }

            StopAll();
            return RunCommand("Mode " + mode, async () =>
            {
                MainV2.comPort.setMode(t.SysId, t.CompId, mode);
                for (var i = 0; i < 30; i++)
                {
                    if (string.Equals(MainV2.comPort.MAV.cs.mode, mode, StringComparison.OrdinalIgnoreCase))
                        return true;
                    await Task.Delay(100).ConfigureAwait(false);
                }

                return false;
            }, "Mode changed to " + mode.ToUpperInvariant(),
                "Vehicle did not confirm mode " + mode.ToUpperInvariant() + ". " + LastVehicleMessage());
        }

        // ------------------------------------------------------------------ arming

        /// <summary>Arms through the normal ArduPilot pre-arm checks (never forced).</summary>
        public Task<bool> ArmAsync()
        {
            var t = TelemetryManager.Capture();
            if (!t.HasHeartbeat)
            {
                Report("Cannot arm: no vehicle connected.", StatusLevel.Critical);
                return Task.FromResult(false);
            }

            return RunCommand("Arm", async () =>
            {
                var ack = await MainV2.comPort.doARMAsync(t.SysId, t.CompId, true).ConfigureAwait(false);
                for (var i = 0; i < 30 && !MainV2.comPort.MAV.cs.armed; i++)
                    await Task.Delay(100).ConfigureAwait(false);
                return ack && MainV2.comPort.MAV.cs.armed;
            }, "Vehicle ARMED", "Arming refused by the vehicle. " + LastVehicleMessage());
        }

        /// <summary>Disarms (never forced - ArduPilot refuses to disarm a flying copter).</summary>
        public Task<bool> DisarmAsync()
        {
            var t = TelemetryManager.Capture();
            if (!t.HasHeartbeat)
            {
                Report("Cannot disarm: no vehicle connected.", StatusLevel.Critical);
                return Task.FromResult(false);
            }

            StopAll();
            return RunCommand("Disarm", async () =>
            {
                var ack = await MainV2.comPort.doARMAsync(t.SysId, t.CompId, false).ConfigureAwait(false);
                for (var i = 0; i < 30 && MainV2.comPort.MAV.cs.armed; i++)
                    await Task.Delay(100).ConfigureAwait(false);
                return ack && !MainV2.comPort.MAV.cs.armed;
            }, "Vehicle DISARMED", "Disarm refused by the vehicle (it will not disarm while flying - land first). " + LastVehicleMessage());
        }

        // ------------------------------------------------------------------ takeoff / land / rtl

        /// <summary>
        /// GUIDED takeoff to <paramref name="altitude"/> metres. The vehicle must already be ARMED
        /// (arming is a separate, explicitly confirmed step).
        /// </summary>
        public Task<bool> TakeoffAsync(double altitude)
        {
            var t = TelemetryManager.Capture();
            if (!t.HasHeartbeat)
            {
                Report("Cannot take off: no vehicle connected.", StatusLevel.Critical);
                return Task.FromResult(false);
            }

            if (!t.IsCopter)
            {
                Report("Takeoff command supports ArduCopter only.", StatusLevel.Warning);
                return Task.FromResult(false);
            }

            if (!t.Armed)
            {
                Report("Cannot take off: vehicle is not armed.", StatusLevel.Warning);
                return Task.FromResult(false);
            }

            altitude = Math.Max(1, Math.Min(AeroSettings.MaxAltitude, altitude));
            var alt = (float)altitude;
            return RunCommand("Takeoff", async () =>
            {
                if (!string.Equals(MainV2.comPort.MAV.cs.mode, "Guided", StringComparison.OrdinalIgnoreCase))
                {
                    MainV2.comPort.setMode(t.SysId, t.CompId, "GUIDED");
                    for (var i = 0; i < 30 && !string.Equals(MainV2.comPort.MAV.cs.mode, "Guided", StringComparison.OrdinalIgnoreCase); i++)
                        await Task.Delay(100).ConfigureAwait(false);
                    if (!string.Equals(MainV2.comPort.MAV.cs.mode, "Guided", StringComparison.OrdinalIgnoreCase))
                        return false;
                }

                return await MainV2.comPort.doCommandAsync(t.SysId, t.CompId, MAVLink.MAV_CMD.TAKEOFF,
                    0, 0, 0, 0, 0, 0, alt).ConfigureAwait(false);
            }, $"Takeoff to {altitude:0.#} m accepted", "Takeoff rejected by the vehicle. " + LastVehicleMessage());
        }

        public Task<bool> LandAsync() => SetModeAsync("Land");

        public Task<bool> ReturnToLaunchAsync() => SetModeAsync("RTL");

        // ------------------------------------------------------------------ helpers

        public bool CommandInFlight => Volatile.Read(ref commandInFlight) > 0;

        private async Task<bool> RunCommand(string name, Func<Task<bool>> action, string okText, string failText)
        {
            Interlocked.Increment(ref commandInFlight);
            Report(name + "...", StatusLevel.Unavailable);
            try
            {
                var ok = await Task.Run(action).ConfigureAwait(false);
                Report(ok ? okText : failText, ok ? StatusLevel.Ok : StatusLevel.Critical);
                return ok;
            }
            catch (Exception ex)
            {
                log.Error(name + " failed", ex);
                Report(name + " failed: " + ex.Message, StatusLevel.Critical);
                return false;
            }
            finally
            {
                Interlocked.Decrement(ref commandInFlight);
            }
        }

        private static string LastVehicleMessage()
        {
            try
            {
                var m = MainV2.comPort.MAV.cs.messageHigh;
                return string.IsNullOrEmpty(m) ? "" : "Vehicle says: " + m;
            }
            catch (Exception)
            {
                return "";
            }
        }
    }
}
