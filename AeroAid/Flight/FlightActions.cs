using System;
using System.Threading.Tasks;
using System.Windows.Forms;
using MissionPlanner.AeroAid.Branding;
using MissionPlanner.AeroAid.Telemetry;

namespace MissionPlanner.AeroAid.Flight
{
    /// <summary>
    /// UI-thread entry points for flight actions. Every potentially dangerous action requires an
    /// explicit confirmation dialog. Movement is always stopped before a dialog is shown.
    /// </summary>
    public static class FlightActions
    {
        private static FlightControlManager Fc => FlightControlManager.Instance;

        public static bool Confirm(string title, string text)
        {
            Fc.StopAll();
            var result = CustomMessageBox.Show(text, BrandingManager.AppName + " - " + title, MessageBoxButtons.YesNo,
                CustomMessageBox.MessageBoxIcon.Warning);
            return result == (int)DialogResult.Yes;
        }

        private static bool RequireLink(TelemetrySnapshot t, string action)
        {
            if (t.HasHeartbeat)
                return true;
            CustomMessageBox.Show("Cannot " + action + ": no vehicle is connected (or no heartbeat).", BrandingManager.AppName);
            return false;
        }

        public static async Task<bool> ArmWithConfirmation()
        {
            var t = TelemetryManager.Capture();
            if (!RequireLink(t, "arm")) return false;
            if (t.Armed)
            {
                CustomMessageBox.Show("The vehicle is already armed.", BrandingManager.AppName);
                return true;
            }

            if (!Confirm("Arm motors",
                    "ARM the motors?\n\nPropellers may start spinning. Make sure people and objects are clear of the vehicle.\n" +
                    "ArduPilot pre-arm checks apply (arming is never forced)."))
                return false;
            return await Fc.ArmAsync();
        }

        public static async Task<bool> DisarmWithConfirmation()
        {
            var t = TelemetryManager.Capture();
            if (!RequireLink(t, "disarm")) return false;
            if (!t.Armed)
            {
                CustomMessageBox.Show("The vehicle is already disarmed.", BrandingManager.AppName);
                return true;
            }

            var airborne = !t.Landed && t.AltRelative > 1.0;
            var text = "DISARM the motors?" + (airborne
                ? $"\n\nWARNING: the vehicle appears to be airborne ({t.AltRelative:0.0} m). ArduPilot normally refuses to " +
                  "disarm a flying copter; land first."
                : "");
            if (!Confirm("Disarm motors", text))
                return false;
            return await Fc.DisarmAsync();
        }

        public static async Task<bool> TakeoffWithConfirmation(double altitude)
        {
            var t = TelemetryManager.Capture();
            if (!RequireLink(t, "take off")) return false;
            if (!t.Armed)
            {
                CustomMessageBox.Show("Arm the vehicle first (Arm button), then take off.", BrandingManager.AppName);
                return false;
            }

            altitude = Math.Max(1, Math.Min(AeroSettings.MaxAltitude, altitude));
            if (!Confirm("Take off", $"Take off to {altitude:0.#} m in GUIDED mode?\n\nMake sure the area above the vehicle is clear."))
                return false;
            return await Fc.TakeoffAsync(altitude);
        }

        public static async Task<bool> LandWithConfirmation()
        {
            var t = TelemetryManager.Capture();
            if (!RequireLink(t, "land")) return false;
            if (t.Armed && !Confirm("Land", "Switch to LAND mode and land at the current position?"))
                return false;
            return await Fc.LandAsync();
        }

        public static async Task<bool> RtlWithConfirmation()
        {
            var t = TelemetryManager.Capture();
            if (!RequireLink(t, "return to launch")) return false;
            if (t.Armed && !Confirm("Return to launch",
                    $"Switch to RTL and return to the launch point?\nCurrent distance to home: {t.DistToHome:0} m."))
                return false;
            return await Fc.ReturnToLaunchAsync();
        }

        /// <summary>
        /// Mode change. While armed a confirmation is required for every mode except GUIDED/BRAKE
        /// (which hold position without RC input); RC-dependent modes always show an explicit warning.
        /// </summary>
        public static async Task<bool> SetModeWithConfirmation(string mode)
        {
            var t = TelemetryManager.Capture();
            if (!RequireLink(t, "change mode")) return false;
            if (string.Equals(mode, "Land", StringComparison.OrdinalIgnoreCase))
                return await LandWithConfirmation();
            if (string.Equals(mode, "RTL", StringComparison.OrdinalIgnoreCase))
                return await RtlWithConfirmation();

            var rcWarning = t.IsCopter && FlightControlManager.RequiresRcInput(mode)
                ? $"\n\nWARNING: {mode.ToUpperInvariant()} takes throttle/attitude from the RC transmitter. Without an RC " +
                  "transmitter (ground-station-only operation) the vehicle may descend or drift. Use GUIDED or BRAKE to hold position."
                : "";
            var needsConfirm = t.Armed && (rcWarning.Length > 0 ||
                                           (AeroSettings.ConfirmModeChangeWhenArmed && !FlightControlManager.IsGcsSafeHoldMode(mode)));
            if (needsConfirm && !Confirm("Change flight mode",
                    $"Change flight mode from {t.Mode.ToUpperInvariant()} to {mode.ToUpperInvariant()} while armed?{rcWarning}"))
                return false;
            return await Fc.SetModeAsync(mode);
        }

        /// <summary>Hold position without RC input (GUIDED zero velocity). No confirmation needed.</summary>
        public static async Task<bool> HoldPosition()
        {
            var t = TelemetryManager.Capture();
            if (!RequireLink(t, "hold position")) return false;
            if (!t.IsCopter)
            {
                CustomMessageBox.Show("Hold position is available for ArduCopter only.", BrandingManager.AppName);
                return false;
            }

            return await Fc.HoldPositionAsync();
        }
    }
}
