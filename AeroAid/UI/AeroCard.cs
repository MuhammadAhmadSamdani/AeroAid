using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using MissionPlanner.AeroAid.Branding;

namespace MissionPlanner.AeroAid.UI
{
    /// <summary>Rounded dark card with an optional header title and accent glyph.</summary>
    public class AeroCard : Panel
    {
        private string title = "";
        private string glyph = "";
        private Color? accentOverride;

        public AeroCard()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = BrandingManager.Palette.Surface;
            ForeColor = BrandingManager.Palette.Text;
            Margin = new Padding(6);
            UpdatePadding();
        }

        [Category("AeroAid")]
        public string Title
        {
            get => title;
            set { title = value ?? ""; UpdatePadding(); Invalidate(); }
        }

        [Category("AeroAid")]
        public string Glyph
        {
            get => glyph;
            set { glyph = value ?? ""; Invalidate(); }
        }

        /// <summary>Overrides the header/border accent colour (e.g. red during an alert).</summary>
        public Color? AccentOverride
        {
            get => accentOverride;
            set { accentOverride = value; Invalidate(); }
        }

        public int CornerRadius { get; set; } = BrandingManager.CornerRadius + 2;

        private void UpdatePadding()
        {
            Padding = new Padding(14, string.IsNullOrEmpty(title) ? 12 : 42, 14, 12);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var parent = AeroDraw.OpaqueBackColor(this);
            using (var b = new SolidBrush(parent))
                e.Graphics.FillRectangle(b, ClientRectangle);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            var p = BrandingManager.Palette;
            var rect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var path = AeroDraw.RoundedRect(rect, CornerRadius))
            {
                using (var b = new SolidBrush(BackColor))
                    g.FillPath(b, path);
                var borderColor = accentOverride ?? Color.FromArgb(0x2E, 0x2E, 0x2E);
                using (var pen = new Pen(borderColor, accentOverride.HasValue ? 2f : 1f))
                    g.DrawPath(pen, path);
            }

            if (!string.IsNullOrEmpty(title))
            {
                var accent = accentOverride ?? p.Accent;
                float x = 14;
                if (!string.IsNullOrEmpty(glyph))
                {
                    using (var iconFont = AeroIcons.CreateFont(15f))
                        if (iconFont != null)
                        {
                            using (var ib = new SolidBrush(accent))
                                g.DrawString(glyph, iconFont, ib, x, 13);
                            x += 24;
                        }
                }

                using (var f = AeroDraw.UiFont(10.5f, FontStyle.Bold))
                using (var tb = new SolidBrush(p.Text))
                    g.DrawString(title, f, tb, x, 11);

                using (var pen = new Pen(Color.FromArgb(0x2A, 0x2A, 0x2A)))
                    g.DrawLine(pen, 12, 34, Width - 12, 34);
            }

            base.OnPaint(e);
        }
    }
}
