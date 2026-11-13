using System;
using System.Drawing;
using System.Drawing.Text;
using System.Linq;

namespace MissionPlanner.AeroAid.UI
{
    /// <summary>
    /// Vector icon glyphs from the Windows system icon font (Segoe Fluent Icons on Windows 11,
    /// Segoe MDL2 Assets on Windows 10). No bitmap assets are required.
    /// </summary>
    public static class AeroIcons
    {
        public const string Dashboard = ""; // Home
        public const string FlightControl = ""; // Game controller
        public const string Mission = ""; // Map pin
        public const string Camera = ""; // Video
        public const string Fire = ""; // Warning
        public const string Telemetry = ""; // Diagnostic
        public const string CameraControl = ""; // Camera
        public const string Settings = ""; // Gear
        public const string Keyboard = ""; // Keyboard
        public const string About = ""; // Info
        public const string Hud = ""; // World
        public const string Setup = ""; // Repair
        public const string Tuning = ""; // Equalizer
        public const string Simulation = ""; // TV monitor
        public const string Help = ""; // Help

        public const string Up = "";
        public const string Down = "";
        public const string Left = "";
        public const string Right = "";
        public const string ArrowUp = "";
        public const string ArrowDown = "";
        public const string RotateLeft = ""; // Undo
        public const string RotateRight = ""; // Redo
        public const string Stop = "";
        public const string Home = "";
        public const string ZoomIn = "";
        public const string ZoomOut = "";
        public const string Center = ""; // Target-ish (location)
        public const string Play = "";
        public const string Fullscreen = "";
        public const string Mic = "";
        public const string MicOff = "";
        public const string Power = "";
        public const string Lock = "";
        public const string Unlock = "";
        public const string Takeoff = ""; // Airplane
        public const string Land = ""; // Download arrow
        public const string Link = "";
        public const string Save = "";
        public const string Refresh = "";
        public const string Folder = "";
        public const string Snapshot = "";
        public const string Check = "";
        public const string Plug = "";

        private static string fontName;

        /// <summary>Name of the installed icon font, or null if none is available.</summary>
        public static string FontName
        {
            get
            {
                if (fontName != null)
                    return fontName.Length == 0 ? null : fontName;
                try
                {
                    using (var fonts = new InstalledFontCollection())
                    {
                        var names = fonts.Families.Select(f => f.Name).ToList();
                        fontName = names.Contains("Segoe Fluent Icons") ? "Segoe Fluent Icons"
                            : names.Contains("Segoe MDL2 Assets") ? "Segoe MDL2 Assets"
                            : "";
                    }
                }
                catch (Exception)
                {
                    fontName = "";
                }

                return fontName.Length == 0 ? null : fontName;
            }
        }

        public static Font CreateFont(float sizePx)
        {
            var name = FontName;
            return name == null ? null : new Font(name, sizePx, FontStyle.Regular, GraphicsUnit.Pixel);
        }
    }
}
