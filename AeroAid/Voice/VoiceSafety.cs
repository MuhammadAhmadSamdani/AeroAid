using System;
using System.Collections.Generic;
using MissionPlanner.AeroAid.Telemetry;
using MissionPlanner.AeroAid.UI;

namespace MissionPlanner.AeroAid.Voice
{
    /// <summary>
    /// Vehicle-state validation for voice commands (pure functions on a telemetry snapshot).
    /// These checks run BEFORE the confirmation dialog and again right before sending. They add to, and never
    /// replace, ArduPilot's own pre-arm checks, failsafes and arming rules: arming is never forced.
    /// </summary>
    public static class VoiceSafety
    {
        /// <summary>Copter modes from which a voice takeoff may start (the takeoff switches to GUIDED).</summary>
        public static readonly string[] TakeoffModes = { "GUIDED", "STABILIZE", "ALT_HOLD", "ALTHOLD", "LOITER", "POSHOLD" };

        /// <summary>Modes in which the vehicle follows a mission / lands / returns: voice ARM and TAKEOFF are refused.</summary>
        public static readonly string[] AutonomousModes = { "AUTO", "RTL", "SMART_RTL", "SMARTRTL", "LAND", "FOLLOW", "AUTOROTATE", "THROW" };

        /// <summary>Copter modes that need a position estimate (GPS + EKF) to arm.</summary>
        public static readonly string[] PositionModes = { "GUIDED", "LOITER", "POSHOLD", "AUTO", "RTL", "SMART_RTL", "SMARTRTL", "CIRCLE", "DRIFT", "FOLLOW", "ZIGZAG" };

        private static string ModeKey(string mode) => (mode ?? "").Trim().ToUpperInvariant().Replace(' ', '_');

        private static bool In(string mode, string[] list) => Array.IndexOf(list, ModeKey(mode)) >= 0;

        /// <summary>
        /// True if the vehicle may be in the air. Uses EXTENDED_SYS_STATE when reported and, independently, altitude
        /// and climb rate - any one of them is enough. (cs.landed cannot be used: it is false whenever the vehicle is
        /// armed, even on the ground.)
        /// </summary>
        public static bool IsAirborne(TelemetrySnapshot t)
        {
            if (t.LandedState == 2 || t.LandedState == 3 || t.LandedState == 4)
                return true;
            return t.Armed && (t.AltRelative > 1.0 || Math.Abs(t.ClimbRate) > 0.5);
        }

        public static string LandedStateText(int s)
        {
            switch (s)
            {
                case 1: return "on ground";
                case 2: return "in air";
                case 3: return "taking off";
                case 4: return "landing";
                default: return "not reported";
            }
        }

        /// <summary>Takeoff altitude that will be commanded: the configured value, limited to [1, max altitude].</summary>
        public static double TakeoffAltitude() =>
            Math.Max(1, Math.Min(AeroSettings.TakeoffAltitude, AeroSettings.MaxAltitude));

        /// <summary>
        /// Validates <paramref name="command"/> against the vehicle state. Returns the failures (empty = allowed);
        /// passed checks are added to <paramref name="passed"/>.
        /// </summary>
        public static List<string> Check(VoiceCommand command, TelemetrySnapshot t, List<string> passed)
        {
            var fail = new List<string>();
            passed = passed ?? new List<string>();

            void Rule(bool ok, string good, string bad)
            {
                if (ok) { if (!string.IsNullOrEmpty(good)) passed.Add(good); }
                else fail.Add(bad);
            }

            if (t == null || !t.HasHeartbeat)
            {
                fail.Add(t != null && t.Connected ? "No heartbeat from the vehicle (link lost)." : "Vehicle not connected.");
                return fail;
            }

            passed.Add("Vehicle connected (heartbeat)");
            switch (command)
            {
                case VoiceCommand.Arm:
                    CheckArm(t, Rule);
                    break;
                case VoiceCommand.Disarm:
                    CheckDisarm(t, Rule);
                    break;
                case VoiceCommand.Takeoff:
                    CheckTakeoff(t, Rule);
                    break;
                case VoiceCommand.Stop:
                    CheckModeCommand(t, Rule, "STOP", new[] { "BRAKE" }, true);
                    break;
                case VoiceCommand.Hold:
                    CheckModeCommand(t, Rule, "HOLD", null, true);
                    break;
                case VoiceCommand.Rtl:
                    CheckModeCommand(t, Rule, "RTL", new[] { "RTL" }, false);
                    break;
                case VoiceCommand.Land:
                    CheckModeCommand(t, Rule, "LAND", new[] { "LAND" }, false);
                    break;
                default:
                    fail.Add("No command.");
                    break;
            }

            return fail;
        }

        private static void CheckArm(TelemetrySnapshot t, Action<bool, string, string> rule)
        {
            rule(!t.Armed, "Vehicle is disarmed", "Vehicle is already armed.");
            rule(!IsAirborne(t), "Vehicle on the ground", $"Vehicle appears to be airborne ({t.AltRelative:0.0} m, {LandedStateText(t.LandedState)}).");
            rule(!In(t.Mode, AutonomousModes), "Mode " + t.Mode.ToUpperInvariant(),
                $"Voice arming is not allowed in {t.Mode.ToUpperInvariant()} mode - switch to GUIDED (or a manual mode) first.");
            CommonReadiness(t, rule, !t.IsCopter || In(t.Mode, PositionModes));
        }

        private static void CheckDisarm(TelemetrySnapshot t, Action<bool, string, string> rule)
        {
            rule(t.Armed, "Vehicle is armed", "Vehicle is already disarmed.");
            // NEVER disarm a flying vehicle by voice, whatever ArduPilot would do
            rule(!IsAirborne(t), $"Vehicle on the ground ({t.AltRelative:0.0} m, {LandedStateText(t.LandedState)})",
                $"Vehicle appears to be AIRBORNE ({t.AltRelative:0.0} m, climb {t.ClimbRate:0.0} m/s, {LandedStateText(t.LandedState)}). " +
                "Voice disarm is refused in the air - land first (LAND / RTL).");
        }

        private static void CheckTakeoff(TelemetrySnapshot t, Action<bool, string, string> rule)
        {
            rule(t.IsCopter, "ArduCopter connected", "Voice takeoff supports ArduCopter only (connected: " + t.Firmware + ").");
            rule(t.Armed, "Vehicle is armed", "Vehicle is not armed - say \"arm\" first, then \"take off\".");
            rule(!IsAirborne(t), "Vehicle on the ground", $"Vehicle already appears to be flying ({t.AltRelative:0.0} m, {LandedStateText(t.LandedState)}).");
            rule(In(t.Mode, TakeoffModes),
                string.Equals(ModeKey(t.Mode), "GUIDED") ? "Mode GUIDED" : "Mode " + t.Mode.ToUpperInvariant() + " (will switch to GUIDED)",
                $"Takeoff is not allowed from {t.Mode.ToUpperInvariant()} mode - use GUIDED, LOITER, ALT_HOLD, POSHOLD or STABILIZE.");
            var alt = TakeoffAltitude();
            rule(AeroSettings.TakeoffAltitude >= 1 && AeroSettings.TakeoffAltitude <= AeroSettings.MaxAltitude,
                $"Takeoff altitude {alt:0.#} m (limit {AeroSettings.MaxAltitude:0} m)",
                $"Configured takeoff altitude {AeroSettings.TakeoffAltitude:0.#} m is outside 1..{AeroSettings.MaxAltitude:0} m (Settings › Flight).");
            CommonReadiness(t, rule, true);
        }

        /// <summary>
        /// STOP / HOLD / RTL / LAND: only meaningful for an ARMED vehicle (a disarmed vehicle is on the ground and
        /// stopped already); refused when the vehicle is already in the target mode (no duplicate command).
        /// Failsafes are not checked here on purpose: these commands move the vehicle towards a safer state, and
        /// ArduPilot itself decides whether the mode change is allowed (e.g. RTL without GPS is refused by the vehicle).
        /// </summary>
        private static void CheckModeCommand(TelemetrySnapshot t, Action<bool, string, string> rule, string name,
            string[] alreadyModes, bool copterOnly)
        {
            if (copterOnly)
                rule(t.IsCopter, "ArduCopter connected", $"Voice {name} supports ArduCopter only (connected: {t.Firmware}).");
            rule(t.Armed, "Vehicle is armed", $"Vehicle is disarmed - {name} has nothing to do.");
            if (alreadyModes != null)
                rule(!In(t.Mode, alreadyModes), "Mode " + t.Mode.ToUpperInvariant(),
                    $"Vehicle is already in {t.Mode.ToUpperInvariant()} mode - {name} not sent again.");
            // HOLD in GUIDED is allowed again: it re-sends a zero-velocity target, which stops any movement
        }

        private static void CommonReadiness(TelemetrySnapshot t, Action<bool, string, string> rule, bool needPosition)
        {
            if (needPosition)
            {
                rule(t.GpsFix >= 3, "GPS 3D fix", "GPS has no 3D fix (" + t.GpsText + ").");
                rule(t.Satellites >= AeroSettings.MinSatellites, $"{t.Satellites} satellites",
                    $"Only {t.Satellites} satellites (minimum {AeroSettings.MinSatellites}).");
                rule(t.EkfLevel == StatusLevel.Ok, $"EKF healthy ({t.EkfVariance:0.00})", $"EKF not healthy (variance {t.EkfVariance:0.00}).");
            }

            if (t.PrearmOk == null)
                rule(true, "Pre-arm status not reported (ArduPilot still enforces its pre-arm checks)", "");
            else
                rule(t.PrearmOk.Value, "Pre-arm checks passing",
                    "Pre-arm checks failing: " + (string.IsNullOrEmpty(t.LastHighMessage) ? "see Telemetry" : t.LastHighMessage));
            rule(t.BatteryPercent >= 0, "", "Battery percentage unknown - cannot verify the battery state.");
            if (t.BatteryPercent >= 0)
                rule(t.BatteryPercent >= AeroSettings.MinBatteryPercent, $"Battery {t.BatteryPercent}% ({t.BatteryVoltage:0.0} V)",
                    $"Battery {t.BatteryPercent}% is below the minimum {AeroSettings.MinBatteryPercent}%.");
            rule(!t.Failsafe, "No failsafe active", "A failsafe is active.");
        }
    }
}
