using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using log4net;
using MissionPlanner.AeroAid.Branding;
using MissionPlanner.AeroAid.Flight;
using MissionPlanner.AeroAid.Telemetry;
using MissionPlanner.AeroAid.UI;

namespace MissionPlanner.AeroAid.Voice
{
    /// <summary>
    /// Voice-initiated takeoff. A recognised phrase NEVER flies the vehicle by itself:
    ///   1. pre-flight checks (link, ArduCopter, on the ground, GPS/satellites, EKF, pre-arm, battery, failsafe);
    ///   2. MANDATORY arm confirmation dialog, then a normal (never forced) arm and verification;
    ///   3. separate takeoff confirmation dialog, then GUIDED takeoff to the configured altitude.
    /// Re-entry is blocked while a workflow runs and for a cooldown period afterwards.
    /// </summary>
    public static class VoiceTakeoffWorkflow
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static bool running;
        private static DateTime lastFinished = DateTime.MinValue;

        /// <summary>Progress messages for the UI (UI thread).</summary>
        public static event Action<string, StatusLevel> Progress;

        public static bool Running => running;

        private static void Say(string m, StatusLevel l)
        {
            log.Info("Voice takeoff: " + m);
            Progress?.Invoke(m, l);
        }

        /// <summary>Pre-flight checks; returns failures (empty = ready) and fills <paramref name="passed"/>.</summary>
        public static List<string> Check(TelemetrySnapshot t, List<string> passed)
        {
            var fail = new List<string>();
            void Rule(bool ok, string good, string bad)
            {
                if (ok) passed.Add(good);
                else fail.Add(bad);
            }

            if (!t.HasHeartbeat)
            {
                fail.Add("Vehicle not connected (no heartbeat).");
                return fail;
            }

            Rule(t.IsCopter, "ArduCopter connected", "Voice takeoff supports ArduCopter only (connected: " + t.Firmware + ").");
            Rule(!(t.Armed && !t.Landed && t.AltRelative > 1.0), "Vehicle on the ground", "Vehicle already appears to be flying.");
            Rule(t.GpsFix >= 3, "GPS 3D fix", "GPS has no 3D fix (" + t.GpsText + ").");
            Rule(t.Satellites >= AeroSettings.MinSatellites, $"{t.Satellites} satellites", $"Only {t.Satellites} satellites (minimum {AeroSettings.MinSatellites}).");
            Rule(t.EkfLevel == StatusLevel.Ok, $"EKF healthy ({t.EkfVariance:0.00})", $"EKF not healthy (variance {t.EkfVariance:0.00}).");
            if (t.PrearmOk == null)
                passed.Add("Pre-arm status not reported (ArduPilot will still enforce pre-arm checks when arming)");
            else
                Rule(t.PrearmOk.Value, "Pre-arm checks passing", "Pre-arm checks failing: " + (string.IsNullOrEmpty(t.LastHighMessage) ? "see Telemetry" : t.LastHighMessage));
            Rule(t.BatteryPercent >= 0, "", "Battery percentage unknown - cannot verify the battery state.");
            if (t.BatteryPercent >= 0)
                Rule(t.BatteryPercent >= AeroSettings.MinBatteryPercent, $"Battery {t.BatteryPercent}% ({t.BatteryVoltage:0.0} V)",
                    $"Battery {t.BatteryPercent}% is below the minimum {AeroSettings.MinBatteryPercent}%.");
            Rule(!t.Failsafe, "No failsafe active", "A failsafe is active.");
            passed.RemoveAll(string.IsNullOrEmpty);
            return fail;
        }

        /// <summary>Entry point for a recognised takeoff command. Must be called on the UI thread.</summary>
        public static async Task RunAsync(string heard, float confidence)
        {
            if (running)
            {
                Say("Ignored: a voice takeoff is already in progress.", StatusLevel.Warning);
                return;
            }

            var since = (DateTime.Now - lastFinished).TotalSeconds;
            if (since < VoiceSettings.CooldownSeconds)
            {
                Say($"Ignored \"{heard}\": repeated voice command (cool-down {VoiceSettings.CooldownSeconds - since:0} s).", StatusLevel.Warning);
                return;
            }

            running = true;
            try
            {
                Say($"Heard \"{heard}\" ({confidence:P0}). Running pre-flight checks...", StatusLevel.Unavailable);
                FlightControlManager.Instance.StopAll();

                var t = TelemetryManager.Capture();
                var passed = new List<string>();
                var failures = Check(t, passed);
                if (failures.Count > 0)
                {
                    Say("Takeoff blocked: " + string.Join(" ", failures), StatusLevel.Critical);
                    CustomMessageBox.Show("Voice takeoff blocked by pre-flight checks:\n\n• " + string.Join("\n• ", failures),
                        BrandingManager.AppName + " - Voice takeoff");
                    return;
                }

                var alt = Math.Min(AeroSettings.TakeoffAltitude, AeroSettings.MaxAltitude);

                // ---- step 1: mandatory arm confirmation
                Say("Pre-flight checks passed. Waiting for ARM confirmation...", StatusLevel.Warning);
                var armText = "Voice command: \"" + heard + "\"\n\nPre-flight checks:\n✓ " + string.Join("\n✓ ", passed) +
                              "\n\nSTEP 1 of 2 — " + (t.Armed ? "The vehicle is ALREADY ARMED on the ground. Confirm the armed state to continue?"
                                  : "ARM the motors now? Propellers may start spinning — make sure the area is clear.");
                if (!FlightActions.Confirm("Voice takeoff — arm", armText))
                {
                    Say("Voice takeoff cancelled at the arm step.", StatusLevel.Warning);
                    return;
                }

                if (!TelemetryManager.Capture().Armed)
                {
                    if (!string.Equals(TelemetryManager.Capture().Mode, "Guided", StringComparison.OrdinalIgnoreCase) &&
                        !await FlightControlManager.Instance.SetModeAsync("Guided"))
                    {
                        Say("Could not switch to GUIDED - takeoff aborted.", StatusLevel.Critical);
                        return;
                    }

                    if (!await FlightControlManager.Instance.ArmAsync())
                    {
                        var why = TelemetryManager.Capture().LastHighMessage;
                        Say("Arming refused by the vehicle - takeoff aborted. " + why, StatusLevel.Critical);
                        CustomMessageBox.Show("The vehicle refused to arm. Takeoff aborted.\n\n" + why, BrandingManager.AppName);
                        return;
                    }
                }

                Say("Vehicle ARMED. Waiting for TAKEOFF confirmation...", StatusLevel.Warning);

                // ---- step 2: takeoff confirmation
                var go = FlightActions.Confirm("Voice takeoff — take off",
                    $"STEP 2 of 2 — TAKE OFF to {alt:0.#} m in GUIDED mode?\n\n" +
                    "Note: ArduCopter disarms automatically if it is not flown within a few seconds of arming.");
                if (!go)
                {
                    Say("Takeoff cancelled after arming.", StatusLevel.Warning);
                    if (TelemetryManager.Capture().Armed &&
                        FlightActions.Confirm("Disarm", "Takeoff was cancelled. DISARM the motors now (recommended)?"))
                        await FlightControlManager.Instance.DisarmAsync();
                    return;
                }

                if (!TelemetryManager.Capture().Armed)
                {
                    Say("The vehicle disarmed before takeoff (auto-disarm timeout). Say the command again.", StatusLevel.Critical);
                    return;
                }

                var ok = await FlightControlManager.Instance.TakeoffAsync(alt);
                Say(ok ? $"Takeoff to {alt:0.#} m in progress." : "Takeoff command rejected by the vehicle.", ok ? StatusLevel.Ok : StatusLevel.Critical);
            }
            catch (Exception ex)
            {
                log.Error("Voice takeoff workflow failed", ex);
                Say("Voice takeoff failed: " + ex.Message, StatusLevel.Critical);
            }
            finally
            {
                running = false;
                lastFinished = DateTime.Now;
                VoiceCommandEngine.Instance.ResetTimeout();
            }
        }
    }
}
