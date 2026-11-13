using System;
using System.Drawing;
using Newtonsoft.Json.Linq;

namespace MissionPlanner.AeroAid.Branding
{
    /// <summary>Immutable colour palette for the AeroAid UI (loaded from branding.json).</summary>
    public sealed class AeroPalette
    {
        public Color Accent { get; private set; }
        public Color Background { get; private set; }
        public Color Surface { get; private set; }
        public Color SurfaceAlt { get; private set; }
        public Color Text { get; private set; }
        public Color Muted { get; private set; }
        public Color StatusOk { get; private set; }
        public Color StatusWarn { get; private set; }
        public Color StatusCritical { get; private set; }
        public Color StatusIdle { get; private set; }

        /// <summary>Slightly lighter accent used for hover states.</summary>
        public Color AccentHover => Blend(Accent, Color.White, 0.15f);

        /// <summary>Darker accent used for pressed states.</summary>
        public Color AccentPressed => Blend(Accent, Color.Black, 0.18f);

        public static AeroPalette Default { get; } = new AeroPalette
        {
            Accent = ColorTranslator.FromHtml("#D6A928"),
            Background = ColorTranslator.FromHtml("#111111"),
            Surface = ColorTranslator.FromHtml("#1B1B1B"),
            SurfaceAlt = ColorTranslator.FromHtml("#252525"),
            Text = ColorTranslator.FromHtml("#F5F5F5"),
            Muted = ColorTranslator.FromHtml("#999999"),
            StatusOk = ColorTranslator.FromHtml("#3FB950"),
            StatusWarn = ColorTranslator.FromHtml("#D6A928"),
            StatusCritical = ColorTranslator.FromHtml("#E5534B"),
            StatusIdle = ColorTranslator.FromHtml("#6E6E6E"),
        };

        public static AeroPalette FromJson(JObject colors)
        {
            var d = Default;
            return new AeroPalette
            {
                Accent = Parse(colors, "accent", d.Accent),
                Background = Parse(colors, "background", d.Background),
                Surface = Parse(colors, "surface", d.Surface),
                SurfaceAlt = Parse(colors, "surfaceAlt", d.SurfaceAlt),
                Text = Parse(colors, "text", d.Text),
                Muted = Parse(colors, "muted", d.Muted),
                StatusOk = Parse(colors, "statusOk", d.StatusOk),
                StatusWarn = Parse(colors, "statusWarn", d.StatusWarn),
                StatusCritical = Parse(colors, "statusCritical", d.StatusCritical),
                StatusIdle = Parse(colors, "statusIdle", d.StatusIdle),
            };
        }

        private static Color Parse(JObject o, string key, Color fallback)
        {
            var s = (string)o[key];
            if (string.IsNullOrWhiteSpace(s))
                return fallback;
            try
            {
                return ColorTranslator.FromHtml(s.Trim());
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        public static Color Blend(Color a, Color b, float t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb(
                (int)(a.A + (b.A - a.A) * t),
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }
    }
}
