using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using MissionPlanner.AeroAid.Branding;

namespace MissionPlanner.AeroAid.UI
{
    /// <summary>Status levels: Green = OK, Yellow = Warning, Red = Critical, Grey = Unavailable.</summary>
    public enum StatusLevel
    {
        Unavailable,
        Ok,
        Warning,
        Critical
    }

    public static class StatusColors
    {
        public static Color For(StatusLevel level)
        {
            var p = BrandingManager.Palette;
            switch (level)
            {
                case StatusLevel.Ok: return p.StatusOk;
                case StatusLevel.Warning: return p.StatusWarn;
                case StatusLevel.Critical: return p.StatusCritical;
                default: return p.StatusIdle;
            }
        }
    }

    /// <summary>
    /// Compact tile: coloured status dot (pulsing when critical), caption and value.
    /// </summary>
    public class StatusTile : Control
    {
        private StatusLevel level = StatusLevel.Unavailable;
        private string caption = "";
        private string value = "--";
        private string unit = "";
        private static readonly Timer pulseTimer = new Timer { Interval = 60 };
        private static float pulsePhase;
        private static event EventHandler Pulse;

        static StatusTile()
        {
            pulseTimer.Tick += (s, e) =>
            {
                pulsePhase = (pulsePhase + 0.08f) % (float)(Math.PI * 2);
                Pulse?.Invoke(null, EventArgs.Empty);
            };
            pulseTimer.Start();
        }

        public StatusTile()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Size = new Size(170, 64);
            Margin = new Padding(5);
            BackColor = BrandingManager.Palette.SurfaceAlt;
            Pulse += OnPulse;
        }

        private void OnPulse(object sender, EventArgs e)
        {
            if (level == StatusLevel.Critical && Visible && IsHandleCreated)
                Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            Pulse -= OnPulse;
            base.Dispose(disposing);
        }

        public string Caption
        {
            get => caption;
            set { caption = value ?? ""; Invalidate(); }
        }

        public string Unit
        {
            get => unit;
            set { unit = value ?? ""; Invalidate(); }
        }

        public bool ShowDot { get; set; } = true;

        public void SetValue(string text, StatusLevel lvl)
        {
            if (text == value && lvl == level)
                return;
            value = text ?? "--";
            level = lvl;
            Invalidate();
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using (var b = new SolidBrush(Parent?.BackColor ?? BrandingManager.Palette.Surface))
                e.Graphics.FillRectangle(b, ClientRectangle);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            var p = BrandingManager.Palette;
            var color = StatusColors.For(level);

            using (var path = AeroDraw.RoundedRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), BrandingManager.CornerRadius))
            {
                using (var b = new SolidBrush(BackColor))
                    g.FillPath(b, path);
                using (var pen = new Pen(level == StatusLevel.Critical ? color : Color.FromArgb(0x33, 0x33, 0x33)))
                    g.DrawPath(pen, path);
            }

            float x = 12;
            if (ShowDot)
            {
                var alpha = level == StatusLevel.Critical ? (int)(150 + 105 * Math.Sin(pulsePhase)) : 255;
                using (var glow = new SolidBrush(Color.FromArgb(Math.Max(0, Math.Min(255, alpha / 4)), color)))
                    g.FillEllipse(glow, x - 3, 13, 16, 16);
                using (var dot = new SolidBrush(Color.FromArgb(Math.Max(60, Math.Min(255, alpha)), color)))
                    g.FillEllipse(dot, x, 16, 10, 10);
                x += 18;
            }

            using (var cf = AeroDraw.UiFont(8.5f))
            using (var cb = new SolidBrush(p.Muted))
                g.DrawString(caption.ToUpperInvariant(), cf, cb, x, 12);

            var text = string.IsNullOrEmpty(unit) ? value : value + " " + unit;
            var available = Width - 20f;
            // shrink the value font until it fits the tile (keeps long values like "Disconnected" readable)
            var size = Height > 60 ? 14f : 12f;
            Font vf = AeroDraw.UiFont(size, FontStyle.Bold);
            while (size > 8f && g.MeasureString(text, vf).Width > available)
            {
                vf.Dispose();
                size -= 0.5f;
                vf = AeroDraw.UiFont(size, FontStyle.Bold);
            }

            using (vf)
            using (var vb = new SolidBrush(level == StatusLevel.Unavailable ? p.Muted : p.Text))
            {
                g.DrawString(text, vf, vb, new RectangleF(10, Height / 2f - 4, Width - 14, Height / 2f + 2),
                    new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap });
            }
        }
    }

    /// <summary>Simple horizontal level/value meter (battery, microphone input, confidence).</summary>
    public class LevelMeter : Control
    {
        private float value;

        public LevelMeter()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Size = new Size(200, 10);
            BarColor = BrandingManager.Palette.Accent;
        }

        public Color BarColor { get; set; }

        /// <summary>0..1</summary>
        public float Value
        {
            get => value;
            set
            {
                var v = Math.Max(0, Math.Min(1, value));
                if (Math.Abs(v - this.value) < 0.002f) return;
                this.value = v;
                Invalidate();
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using (var b = new SolidBrush(Parent?.BackColor ?? BrandingManager.Palette.Surface))
                e.Graphics.FillRectangle(b, ClientRectangle);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0, 0, Width - 1, Height - 1);
            using (var track = AeroDraw.RoundedRect(r, Height / 2f))
            using (var tb = new SolidBrush(Color.FromArgb(0x30, 0x30, 0x30)))
                g.FillPath(tb, track);
            if (value > 0.001f)
            {
                var fr = new RectangleF(0, 0, Math.Max(Height, (Width - 1) * value), Height - 1);
                using (var fill = AeroDraw.RoundedRect(fr, Height / 2f))
                using (var fb = new SolidBrush(BarColor))
                    g.FillPath(fb, fill);
            }
        }
    }
}
