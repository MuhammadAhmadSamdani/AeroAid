using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using log4net;
using Newtonsoft.Json;

namespace MissionPlanner.AeroAid.Input
{
    public enum ShortcutCategory
    {
        DroneMovement,
        FlightModes,
        Camera
    }

    public enum ShortcutAction
    {
        Forward, Backward, Left, Right, Up, Down, YawLeft, YawRight, Stop,
        ModeStabilize, ModeAltHold, ModeLoiter, ModeGuided, ModeAuto, ModeRtl, ModeLand,
        CameraUp, CameraDown, CameraLeft, CameraRight, ZoomIn, ZoomOut, CameraCenter
    }

    /// <summary>Configurable keyboard shortcuts (persisted as JSON in the settings file).</summary>
    public static class ShortcutMap
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private const string SettingKey = "keys.map";
        private static Dictionary<ShortcutAction, Keys> map;

        public static event Action Changed;

        public static readonly Dictionary<ShortcutAction, (string label, ShortcutCategory cat, Keys key)> Definitions =
            new Dictionary<ShortcutAction, (string, ShortcutCategory, Keys)>
            {
                { ShortcutAction.Forward, ("Forward", ShortcutCategory.DroneMovement, Keys.W) },
                { ShortcutAction.Backward, ("Backward", ShortcutCategory.DroneMovement, Keys.S) },
                { ShortcutAction.Left, ("Left", ShortcutCategory.DroneMovement, Keys.A) },
                { ShortcutAction.Right, ("Right", ShortcutCategory.DroneMovement, Keys.D) },
                { ShortcutAction.Up, ("Up (climb)", ShortcutCategory.DroneMovement, Keys.R) },
                { ShortcutAction.Down, ("Down (descend)", ShortcutCategory.DroneMovement, Keys.F) },
                { ShortcutAction.YawLeft, ("Rotate left", ShortcutCategory.DroneMovement, Keys.Q) },
                { ShortcutAction.YawRight, ("Rotate right", ShortcutCategory.DroneMovement, Keys.E) },
                { ShortcutAction.Stop, ("Stop / neutral", ShortcutCategory.DroneMovement, Keys.Space) },
                { ShortcutAction.ModeStabilize, ("Mode: Stabilize", ShortcutCategory.FlightModes, Keys.D1) },
                { ShortcutAction.ModeAltHold, ("Mode: AltHold", ShortcutCategory.FlightModes, Keys.D2) },
                { ShortcutAction.ModeLoiter, ("Mode: Loiter", ShortcutCategory.FlightModes, Keys.D3) },
                { ShortcutAction.ModeGuided, ("Mode: Guided", ShortcutCategory.FlightModes, Keys.D4) },
                { ShortcutAction.ModeAuto, ("Mode: Auto", ShortcutCategory.FlightModes, Keys.D5) },
                { ShortcutAction.ModeRtl, ("RTL (return to launch)", ShortcutCategory.FlightModes, Keys.D6) },
                { ShortcutAction.ModeLand, ("Land", ShortcutCategory.FlightModes, Keys.D7) },
                { ShortcutAction.CameraUp, ("Camera up", ShortcutCategory.Camera, Keys.Up) },
                { ShortcutAction.CameraDown, ("Camera down", ShortcutCategory.Camera, Keys.Down) },
                { ShortcutAction.CameraLeft, ("Camera left", ShortcutCategory.Camera, Keys.Left) },
                { ShortcutAction.CameraRight, ("Camera right", ShortcutCategory.Camera, Keys.Right) },
                { ShortcutAction.ZoomIn, ("Zoom in", ShortcutCategory.Camera, Keys.Oemplus) },
                { ShortcutAction.ZoomOut, ("Zoom out", ShortcutCategory.Camera, Keys.OemMinus) },
                { ShortcutAction.CameraCenter, ("Camera center / reset", ShortcutCategory.Camera, Keys.C) },
            };

        /// <summary>Keys that cannot be bound (Mission Planner shortcuts, focus/dialog keys).</summary>
        public static readonly HashSet<Keys> Reserved = new HashSet<Keys>
        {
            Keys.F2, Keys.F3, Keys.F4, Keys.F5, Keys.F12, Keys.Tab, Keys.Enter, Keys.Escape, Keys.ShiftKey,
            Keys.ControlKey, Keys.Menu, Keys.LWin, Keys.RWin, Keys.Apps, Keys.Back, Keys.Delete
        };

        private static Dictionary<ShortcutAction, Keys> Map
        {
            get
            {
                if (map == null) Load();
                return map;
            }
        }

        public static void Load()
        {
            map = Definitions.ToDictionary(kv => kv.Key, kv => kv.Value.key);
            var json = AeroSettings.GetString(SettingKey, "");
            if (string.IsNullOrEmpty(json)) return;
            try
            {
                var stored = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
                foreach (var kv in stored)
                {
                    if (Enum.TryParse(kv.Key, out ShortcutAction a) && Enum.TryParse(kv.Value, out Keys k) && !Reserved.Contains(k))
                        map[a] = k;
                }
            }
            catch (Exception ex)
            {
                log.Error("Invalid keyboard shortcut settings - using defaults", ex);
            }
        }

        public static Keys KeyFor(ShortcutAction a) => Map.TryGetValue(a, out var k) ? k : Keys.None;

        /// <summary>Normalises equivalent keys (numpad +/- and digits).</summary>
        public static Keys Normalize(Keys k)
        {
            switch (k)
            {
                case Keys.Add: return Keys.Oemplus;
                case Keys.Subtract: return Keys.OemMinus;
                default:
                    if (k >= Keys.NumPad0 && k <= Keys.NumPad9) return Keys.D0 + (k - Keys.NumPad0);
                    return k;
            }
        }

        public static ShortcutAction? ActionFor(Keys key)
        {
            key = Normalize(key);
            foreach (var kv in Map)
                if (Normalize(kv.Value) == key)
                    return kv.Key;
            return null;
        }

        /// <summary>Assigns a key. Returns an error message (conflict/reserved) or null on success.</summary>
        public static string TryAssign(ShortcutAction a, Keys key)
        {
            key = Normalize(key);
            if (Reserved.Contains(key))
                return KeyText(key) + " is reserved and cannot be used.";
            var other = Map.FirstOrDefault(kv => kv.Key != a && Normalize(kv.Value) == key);
            if (Map.Any(kv => kv.Key != a && Normalize(kv.Value) == key))
                return KeyText(key) + " is already used by \"" + Definitions[other.Key].label + "\".";
            Map[a] = key;
            return null;
        }

        public static void Save()
        {
            var stored = Map.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value.ToString());
            AeroSettings.SetString(SettingKey, JsonConvert.SerializeObject(stored));
            AeroSettings.Save();
            Changed?.Invoke();
        }

        public static void RestoreDefaults()
        {
            map = Definitions.ToDictionary(kv => kv.Key, kv => kv.Value.key);
            AeroSettings.SetString(SettingKey, "");
            AeroSettings.Save();
            Changed?.Invoke();
        }

        public static string KeyText(Keys k)
        {
            switch (k)
            {
                case Keys.None: return "—";
                case Keys.Space: return "Space";
                case Keys.Oemplus: return "+";
                case Keys.OemMinus: return "−";
                case Keys.Up: return "↑";
                case Keys.Down: return "↓";
                case Keys.Left: return "←";
                case Keys.Right: return "→";
                default:
                    if (k >= Keys.D0 && k <= Keys.D9) return ((int)(k - Keys.D0)).ToString();
                    return k.ToString();
            }
        }
    }
}
