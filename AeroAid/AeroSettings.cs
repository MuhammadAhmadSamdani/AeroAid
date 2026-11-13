using System;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using log4net;
using MissionPlanner.AeroAid.Branding;
using MissionPlanner.Utilities;

namespace MissionPlanner.AeroAid
{
    /// <summary>
    /// Typed, persistent AeroAid preferences stored in the existing Mission Planner settings file
    /// (Settings.Instance) under the "aeroaid." prefix. Secrets are encrypted with Windows DPAPI
    /// (current-user scope) and are never written in plain text.
    /// </summary>
    public static class AeroSettings
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private const string Prefix = "aeroaid.";
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AeroAid.GCS.v1");

        public static event Action Changed;

        public static void RaiseChanged() => Changed?.Invoke();

        // ---------------- primitives ----------------
        public static string GetString(string key, string def = "")
        {
            var v = Settings.Instance[Prefix + key];
            return v ?? def;
        }

        public static void SetString(string key, string value) => Settings.Instance[Prefix + key] = value ?? "";

        public static bool GetBool(string key, bool def)
        {
            var v = Settings.Instance[Prefix + key];
            return bool.TryParse(v, out var b) ? b : def;
        }

        public static void SetBool(string key, bool value) => Settings.Instance[Prefix + key] = value.ToString();

        public static double GetDouble(string key, double def, double min, double max)
        {
            var v = Settings.Instance[Prefix + key];
            if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || double.IsNaN(d))
                d = def;
            return Math.Max(min, Math.Min(max, d));
        }

        public static void SetDouble(string key, double value) =>
            Settings.Instance[Prefix + key] = value.ToString(CultureInfo.InvariantCulture);

        public static int GetInt(string key, int def, int min, int max)
        {
            var v = Settings.Instance[Prefix + key];
            if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                i = def;
            return Math.Max(min, Math.Min(max, i));
        }

        public static void SetInt(string key, int value) =>
            Settings.Instance[Prefix + key] = value.ToString(CultureInfo.InvariantCulture);

        /// <summary>Stores a secret encrypted with DPAPI (CurrentUser). Empty clears it.</summary>
        public static void SetSecret(string key, string secret)
        {
            if (string.IsNullOrEmpty(secret))
            {
                Settings.Instance[Prefix + key] = "";
                return;
            }

            var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy,
                DataProtectionScope.CurrentUser);
            Settings.Instance[Prefix + key] = "dpapi:" + Convert.ToBase64String(protectedBytes);
        }

        public static string GetSecret(string key)
        {
            var v = Settings.Instance[Prefix + key];
            if (string.IsNullOrEmpty(v) || !v.StartsWith("dpapi:", StringComparison.Ordinal))
                return "";
            try
            {
                var bytes = Convert.FromBase64String(v.Substring(6));
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser));
            }
            catch (Exception ex)
            {
                // e.g. settings copied from another Windows user/machine - cannot be decrypted
                log.Warn("Stored secret '" + key + "' could not be decrypted; it must be re-entered. " + ex.Message);
                return "";
            }
        }

        public static bool HasSecret(string key) => !string.IsNullOrEmpty(GetSecret(key));

        public static void Save()
        {
            try
            {
                Settings.Instance.Save();
            }
            catch (Exception ex)
            {
                log.Error("Failed to save settings", ex);
                throw;
            }

            RaiseChanged();
        }

        // ---------------- Appearance ----------------
        public static bool Animations
        {
            get => GetBool("ui.animations", BrandingManager.AnimationsDefault);
            set => SetBool("ui.animations", value);
        }

        public static bool UseSidebar
        {
            get => GetBool("ui.sidebar", true);
            set => SetBool("ui.sidebar", value);
        }

        // ---------------- Flight control limits (hard caps enforced here) ----------------
        public const double HardMaxHorizontalSpeed = 5.0; // m/s
        public const double HardMaxVerticalSpeed = 2.5; // m/s
        public const double HardMaxYawRate = 90.0; // deg/s
        public const double HardMaxAltitude = 120.0; // m (typical regulatory ceiling)

        public static double MaxHorizontalSpeed
        {
            get => GetDouble("flight.max_h_speed", 2.0, 0.1, HardMaxHorizontalSpeed);
            set => SetDouble("flight.max_h_speed", Math.Max(0.1, Math.Min(HardMaxHorizontalSpeed, value)));
        }

        public static double MaxVerticalSpeed
        {
            get => GetDouble("flight.max_v_speed", 1.0, 0.1, HardMaxVerticalSpeed);
            set => SetDouble("flight.max_v_speed", Math.Max(0.1, Math.Min(HardMaxVerticalSpeed, value)));
        }

        public static double MaxYawRate
        {
            get => GetDouble("flight.max_yaw_rate", 30.0, 5, HardMaxYawRate);
            set => SetDouble("flight.max_yaw_rate", Math.Max(5, Math.Min(HardMaxYawRate, value)));
        }

        /// <summary>Climb commands are blocked at/above this relative altitude.</summary>
        public static double MaxAltitude
        {
            get => GetDouble("flight.max_alt", 30.0, 2, HardMaxAltitude);
            set => SetDouble("flight.max_alt", Math.Max(2, Math.Min(HardMaxAltitude, value)));
        }

        /// <summary>Descend commands are blocked below this relative altitude (use Land instead).</summary>
        public static double MinAltitude
        {
            get => GetDouble("flight.min_alt", 2.0, 0.5, 20);
            set => SetDouble("flight.min_alt", Math.Max(0.5, Math.Min(20, value)));
        }

        public static double TakeoffAltitude
        {
            get => GetDouble("flight.takeoff_alt", 5.0, 1, 50);
            set => SetDouble("flight.takeoff_alt", Math.Max(1, Math.Min(50, value)));
        }

        public static bool KeyboardFlightEnabled
        {
            get => GetBool("flight.keyboard", true);
            set => SetBool("flight.keyboard", value);
        }

        public static bool ConfirmModeChangeWhenArmed
        {
            get => GetBool("flight.confirm_modes", true);
            set => SetBool("flight.confirm_modes", value);
        }

        // ---------------- Pre-flight readiness (voice takeoff) ----------------
        public static int MinSatellites
        {
            get => GetInt("preflight.min_sats", 6, 4, 20);
            set => SetInt("preflight.min_sats", value);
        }

        public static int MinBatteryPercent
        {
            get => GetInt("preflight.min_batt", 30, 10, 90);
            set => SetInt("preflight.min_batt", value);
        }
    }
}
