using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using log4net;
using MissionPlanner.Utilities;
using Newtonsoft.Json.Linq;

namespace MissionPlanner.AeroAid.Branding
{
    /// <summary>
    /// Single source of truth for application identity: name, version, logo and palette.
    /// Everything is read from Assets/Branding/branding.json and Assets/Branding/logo.png
    /// (next to the executable). Replacing logo.png rebrands every logo location.
    /// </summary>
    public static class BrandingManager
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private static readonly object sync = new object();
        private static readonly Dictionary<int, Bitmap> scaledLogoCache = new Dictionary<int, Bitmap>();
        private static Bitmap logo;
        private static bool loaded;

        public static string AppName { get; private set; } = "AeroAid";
        public static string Tagline { get; private set; } = "Drone Ground Control Station";
        public static string Version { get; private set; } = "1.0.0";
        public static string Company { get; private set; } = "AeroAid";
        public static int CornerRadius { get; private set; } = 10;
        public static bool AnimationsDefault { get; private set; } = true;
        public static AeroPalette Palette { get; private set; } = AeroPalette.Default;

        /// <summary>Base Mission Planner version this build is derived from (kept for attribution/support).</summary>
        public static string BaseVersion => System.Windows.Forms.Application.ProductVersion;

        public static string BrandingDirectory =>
            Path.Combine(Settings.GetRunningDirectory(), "Assets", "Branding");

        public static string LogoPath { get; private set; }

        /// <summary>Full logo at native resolution, or null if the logo file is missing/invalid.</summary>
        public static Bitmap Logo
        {
            get
            {
                Load();
                return logo;
            }
        }

        public static void Load()
        {
            lock (sync)
            {
                if (loaded)
                    return;
                loaded = true;

                var configPath = Path.Combine(BrandingDirectory, "branding.json");
                string logoFile = "logo.png";
                try
                {
                    if (File.Exists(configPath))
                    {
                        var json = JObject.Parse(File.ReadAllText(configPath));
                        AppName = (string)json["appName"] ?? AppName;
                        Tagline = (string)json["tagline"] ?? Tagline;
                        Version = (string)json["version"] ?? Version;
                        Company = (string)json["company"] ?? Company;
                        logoFile = (string)json["logoFile"] ?? logoFile;
                        CornerRadius = Math.Max(0, Math.Min(24, (int?)json["cornerRadius"] ?? CornerRadius));
                        AnimationsDefault = (bool?)json["animations"] ?? AnimationsDefault;
                        if (json["colors"] is JObject colors)
                            Palette = AeroPalette.FromJson(colors);
                    }
                    else
                    {
                        log.Warn("Branding config not found at " + configPath + " - using built-in defaults");
                    }
                }
                catch (Exception ex)
                {
                    log.Error("Failed to read branding config " + configPath + " - using built-in defaults", ex);
                }

                LogoPath = Path.Combine(BrandingDirectory, logoFile);
                try
                {
                    if (File.Exists(LogoPath))
                    {
                        // read into memory so the file is not locked and can be replaced while running
                        var bytes = File.ReadAllBytes(LogoPath);
                        using (var ms = new MemoryStream(bytes))
                        using (var img = Image.FromStream(ms))
                        {
                            logo = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppArgb);
                            using (var g = Graphics.FromImage(logo))
                                g.DrawImage(img, 0, 0, img.Width, img.Height);
                        }
                    }
                    else
                    {
                        log.Warn("Logo not found at " + LogoPath);
                    }
                }
                catch (Exception ex)
                {
                    log.Error("Failed to load logo " + LogoPath, ex);
                    logo = null;
                }
            }
        }

        /// <summary>
        /// Logo scaled to fit a square of <paramref name="size"/> pixels, preserving aspect ratio
        /// and transparency. Cached; callers must NOT dispose the result.
        /// </summary>
        public static Bitmap GetLogo(int size)
        {
            Load();
            if (logo == null || size <= 0)
                return null;
            lock (sync)
            {
                if (scaledLogoCache.TryGetValue(size, out var cached))
                    return cached;

                var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    var scale = Math.Min((float)size / logo.Width, (float)size / logo.Height);
                    var w = logo.Width * scale;
                    var h = logo.Height * scale;
                    g.DrawImage(logo, (size - w) / 2f, (size - h) / 2f, w, h);
                }

                scaledLogoCache[size] = bmp;
                return bmp;
            }
        }

        /// <summary>128px bitmap suitable for Program.IconFile (window/taskbar icons).</summary>
        public static Bitmap GetIconBitmap() => GetLogo(128);

        /// <summary>Renders the branded splash background (logo, name, tagline, version, attribution).</summary>
        public static Bitmap CreateSplashImage(int width, int height)
        {
            Load();
            var p = Palette;
            var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                using (var bg = new LinearGradientBrush(new Rectangle(0, 0, width, height), p.Background, p.Surface,
                           LinearGradientMode.ForwardDiagonal))
                    g.FillRectangle(bg, 0, 0, width, height);

                // accent bar
                using (var accent = new SolidBrush(p.Accent))
                    g.FillRectangle(accent, 0, height - 6, width, 6);

                var logoSize = Math.Min(150, height - 120);
                var logoImg = GetLogo(logoSize);
                if (logoImg != null)
                    g.DrawImage(logoImg, 40, (height - logoSize) / 2 - 10, logoSize, logoSize);

                var textX = 40 + logoSize + 30;
                using (var titleFont = new Font("Segoe UI Semibold", 34f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var tagFont = new Font("Segoe UI", 16f, GraphicsUnit.Pixel))
                using (var smallFont = new Font("Segoe UI", 12f, GraphicsUnit.Pixel))
                using (var textBrush = new SolidBrush(p.Text))
                using (var accentBrush = new SolidBrush(p.Accent))
                using (var mutedBrush = new SolidBrush(p.Muted))
                {
                    g.DrawString(AppName, titleFont, accentBrush, textX, height / 2f - 70);
                    g.DrawString(Tagline, tagFont, textBrush, textX + 2, height / 2f - 20);
                    g.DrawString("Version " + Version, smallFont, mutedBrush, textX + 2, height / 2f + 10);
                    // Required attribution: AeroAid is a derivative of Mission Planner (GPLv3) for ArduPilot.
                    g.DrawString("Based on Mission Planner by Michael Oborne (GPLv3) • ArduPilot / MAVLink",
                        smallFont, mutedBrush, 40, height - 30);
                }
            }

            return bmp;
        }
    }
}
