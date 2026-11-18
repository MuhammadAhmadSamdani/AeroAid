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
    /// Everything is read from Assets/Branding/branding.json and the logo it names - Assets/Branding/aeroaid.png -
    /// (next to the executable). That one file is the only logo source: the sidebar, About page, splash screen,
    /// window/taskbar icons, the main-menu logo button and the exe icon (make-icon.ps1) are all derived from it.
    /// </summary>
    public static class BrandingManager
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private static readonly object sync = new object();
        private static readonly Dictionary<int, Bitmap> scaledLogoCache = new Dictionary<int, Bitmap>();
        private static readonly Dictionary<long, Bitmap> fittedLogoCache = new Dictionary<long, Bitmap>();
        private static Icon icon;

        /// <summary>The central logo file name (in <see cref="BrandingDirectory"/>), unless branding.json names another.</summary>
        public const string DefaultLogoFile = "aeroaid.png";
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

        /// <summary>
        /// Forgets the loaded branding so the next access re-reads branding.json and the logo (e.g. after the logo
        /// file was replaced). Bitmaps handed out earlier stay valid; they are simply no longer cached.
        /// </summary>
        public static void Reload()
        {
            lock (sync)
            {
                loaded = false;
                logo = null;
                icon = null;
                scaledLogoCache.Clear();
                fittedLogoCache.Clear();
            }

            Load();
        }

        public static void Load()
        {
            lock (sync)
            {
                if (loaded)
                    return;
                loaded = true;

                var configPath = Path.Combine(BrandingDirectory, "branding.json");
                string logoFile = DefaultLogoFile;
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
                        using (var full = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppArgb))
                        {
                            using (var g = Graphics.FromImage(full))
                                g.DrawImage(img, 0, 0, img.Width, img.Height);
                            // remove only the fully transparent margin around the artwork (not the artwork itself),
                            // so every location can size the visible logo exactly
                            logo = full.Clone(OpaqueBounds(full), PixelFormat.Format32bppArgb);
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

        /// <summary>Bounding box of the pixels that are not (almost) fully transparent, with a small safety margin.</summary>
        public static Rectangle OpaqueBounds(Bitmap bmp)
        {
            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var bytes = new byte[data.Stride * data.Height];
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                int minX = bmp.Width, minY = bmp.Height, maxX = -1, maxY = -1;
                for (var y = 0; y < bmp.Height; y++)
                {
                    var row = y * data.Stride;
                    for (var x = 0; x < bmp.Width; x++)
                    {
                        if (bytes[row + x * 4 + 3] <= 8) continue; // alpha
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }

                if (maxX < 0) return new Rectangle(0, 0, bmp.Width, bmp.Height);
                // keep a 1 % margin so anti-aliased edges are untouched
                var pad = Math.Max(2, Math.Max(bmp.Width, bmp.Height) / 100);
                var r = Rectangle.FromLTRB(minX - pad, minY - pad, maxX + 1 + pad, maxY + 1 + pad);
                r.Intersect(new Rectangle(0, 0, bmp.Width, bmp.Height));
                return r;
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }

        /// <summary>Aspect ratio (width / height) of the visible logo, or 1 without a logo.</summary>
        public static float LogoAspect
        {
            get
            {
                Load();
                return logo == null ? 1f : (float)logo.Width / logo.Height;
            }
        }

        /// <summary>
        /// Logo scaled to fit inside <paramref name="maxWidth"/> x <paramref name="maxHeight"/> pixels, preserving the
        /// aspect ratio (never stretched or cropped) and transparency. The bitmap has exactly the fitted size.
        /// Cached; callers must NOT dispose the result.
        /// </summary>
        public static Bitmap GetLogo(int maxWidth, int maxHeight)
        {
            Load();
            if (logo == null || maxWidth <= 0 || maxHeight <= 0)
                return null;
            lock (sync)
            {
                var key = ((long)maxWidth << 32) | (uint)maxHeight;
                if (fittedLogoCache.TryGetValue(key, out var cached))
                    return cached;
                var scale = Math.Min((double)maxWidth / logo.Width, (double)maxHeight / logo.Height);
                var w = Math.Max(1, (int)Math.Round(logo.Width * scale));
                var h = Math.Max(1, (int)Math.Round(logo.Height * scale));
                var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.CompositingQuality = CompositingQuality.HighQuality;
                    using (var attrs = new ImageAttributes())
                    {
                        attrs.SetWrapMode(WrapMode.TileFlipXY); // no dark halo at the edges
                        g.DrawImage(logo, new Rectangle(0, 0, w, h), 0, 0, logo.Width, logo.Height, GraphicsUnit.Pixel, attrs);
                    }
                }

                fittedLogoCache[key] = bmp;
                return bmp;
            }
        }

        /// <summary>
        /// Square icon image (window / taskbar icon): the wide AEROAID wordmark centred, aspect preserved, on a rounded
        /// tile in the brand background colour. The tile is needed because the white "AERO" lettering would be
        /// invisible on a light taskbar if the icon were transparent.
        /// </summary>
        public static Bitmap GetIconBitmap(int size = 128)
        {
            Load();
            if (logo == null) return null;
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                using (var path = RoundedSquare(size, Math.Max(2, size / 6)))
                using (var bg = new SolidBrush(Palette.Background))
                    g.FillPath(bg, path);
                var inner = (int)(size * 0.9);
                var fitted = GetLogo(inner, inner);
                g.DrawImage(fitted, (size - fitted.Width) / 2, (size - fitted.Height) / 2, fitted.Width, fitted.Height);
            }

            return bmp;
        }

        private static GraphicsPath RoundedSquare(int size, int r)
        {
            var p = new GraphicsPath();
            var d = r * 2;
            p.AddArc(0, 0, d, d, 180, 90);
            p.AddArc(size - d - 1, 0, d, d, 270, 90);
            p.AddArc(size - d - 1, size - d - 1, d, d, 0, 90);
            p.AddArc(0, size - d - 1, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>Window icon built from the logo (shared instance - do not dispose), or null without a logo.</summary>
        public static Icon AppIcon
        {
            get
            {
                lock (sync)
                    if (icon != null) return icon;
                var bmp = GetIconBitmap(64);
                if (bmp == null) return null;
                var i = Icon.FromHandle(bmp.GetHicon());
                lock (sync) icon = icon ?? i;
                return icon;
            }
        }


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

                // the AEROAID wordmark already carries the name: centre it, then tagline, version and attribution
                var logoImg = GetLogo(width - 120, (int)(height * 0.42));
                var logoY = (int)(height * 0.14);
                if (logoImg != null)
                    g.DrawImage(logoImg, (width - logoImg.Width) / 2, logoY, logoImg.Width, logoImg.Height);
                var textY = logoY + (logoImg?.Height ?? 60) + 18;
                using (var tagFont = new Font("Segoe UI", 18f, GraphicsUnit.Pixel))
                using (var smallFont = new Font("Segoe UI", 12f, GraphicsUnit.Pixel))
                using (var textBrush = new SolidBrush(p.Text))
                using (var mutedBrush = new SolidBrush(p.Muted))
                using (var centre = new StringFormat { Alignment = StringAlignment.Center })
                {
                    if (logoImg == null)
                        using (var titleFont = new Font("Segoe UI Semibold", 34f, FontStyle.Bold, GraphicsUnit.Pixel))
                        using (var accentBrush = new SolidBrush(p.Accent))
                            g.DrawString(AppName, titleFont, accentBrush, new RectangleF(0, logoY, width, 50), centre);
                    g.DrawString(Tagline, tagFont, textBrush, new RectangleF(0, textY, width, 26), centre);
                    g.DrawString("Version " + Version, smallFont, mutedBrush, new RectangleF(0, textY + 30, width, 20), centre);
                    // Required attribution: AeroAid is a derivative of Mission Planner (GPLv3) for ArduPilot.
                    g.DrawString("Based on Mission Planner by Michael Oborne (GPLv3) • ArduPilot / MAVLink",
                        smallFont, mutedBrush, new RectangleF(0, height - 30, width, 20), centre);
                }
            }

            return bmp;
        }
    }
}
