using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using MissionPlanner.AeroAid.Branding;
using MissionPlanner.Utilities;

namespace MissionPlanner.AeroAid.UI
{
    /// <summary>Left navigation bar: logo, primary sections, advanced (stock Mission Planner) screens.</summary>
    [PreventTheming]
    public class Sidebar : Panel
    {
        public sealed class NavItem
        {
            public string ScreenName;
            public string Label;
            public string Glyph;
            public Action Navigate;
            public AeroButton Button;
            public Func<bool> AlertState;
        }

        private readonly List<NavItem> items = new List<NavItem>();
        private readonly FlowLayoutPanel list;
        private readonly Panel brand;
        private readonly Label statusLabel;
        private readonly Timer blinkTimer = new Timer { Interval = 500 };
        private bool blinkOn;

        public Sidebar()
        {
            var p = BrandingManager.Palette;
            DoubleBuffered = true;
            Dock = DockStyle.Left;
            Width = 228;
            BackColor = p.Background;

            brand = new Panel { Dock = DockStyle.Top, Height = 92, BackColor = p.Background };
            brand.Paint += PaintBrand;

            statusLabel = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 40,
                ForeColor = p.Muted,
                BackColor = p.Background,
                Font = AeroDraw.UiFont(8f),
                TextAlign = ContentAlignment.MiddleCenter,
                Text = BrandingManager.AppName + " " + BrandingManager.Version + "\nMission Planner " + BrandingManager.BaseVersion
            };

            list = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                BackColor = p.Background,
                Padding = new Padding(10, 4, 6, 4)
            };

            Controls.Add(list);
            Controls.Add(statusLabel);
            Controls.Add(brand);

            blinkTimer.Tick += (s, e) =>
            {
                blinkOn = !blinkOn;
                foreach (var it in items)
                {
                    if (it.AlertState == null) continue;
                    var alert = SafeAlert(it);
                    it.Button.AlertColor = alert && blinkOn ? p.StatusCritical : (Color?)null;
                }
            };
            blinkTimer.Start();
        }

        private static bool SafeAlert(NavItem it)
        {
            try
            {
                return it.AlertState();
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void PaintBrand(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            var p = BrandingManager.Palette;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            // the AEROAID wordmark (which already contains the name), centred, aspect ratio preserved
            var logo = BrandingManager.GetLogo(brand.Width - 36, 46);
            if (logo != null)
            {
                var y = 14 + (46 - logo.Height) / 2;
                g.DrawImage(logo, (brand.Width - logo.Width) / 2, y, logo.Width, logo.Height);
                using (var f = AeroDraw.UiFont(8f))
                using (var b = new SolidBrush(p.Muted))
                using (var centre = new StringFormat { Alignment = StringAlignment.Center })
                    g.DrawString(BrandingManager.Tagline, f, b, new RectangleF(6, 66, brand.Width - 12, 20), centre);
            }
            else
            {
                using (var f = AeroDraw.UiFont(16f, FontStyle.Bold))
                using (var b = new SolidBrush(p.Accent))
                    g.DrawString(BrandingManager.AppName, f, b, 18, 24);
                using (var f = AeroDraw.UiFont(8f))
                using (var b = new SolidBrush(p.Muted))
                    g.DrawString(BrandingManager.Tagline, f, b, new RectangleF(19, 52, Width - 25, 30));
            }
            using (var pen = new Pen(Color.FromArgb(0x26, 0x26, 0x26)))
                g.DrawLine(pen, 14, brand.Height - 1, Width - 14, brand.Height - 1);
        }

        public void AddSection(string caption)
        {
            var l = new Label
            {
                Text = caption.ToUpperInvariant(),
                ForeColor = Color.FromArgb(0x70, 0x70, 0x70),
                Font = AeroDraw.UiFont(7.5f, FontStyle.Bold),
                AutoSize = false,
                Width = 200,
                Height = 22,
                TextAlign = ContentAlignment.BottomLeft,
                Margin = new Padding(8, 4, 0, 2)
            };
            list.Controls.Add(l);
        }

        public NavItem AddItem(string screenName, string label, string glyph, Action navigate, Func<bool> alertState = null)
        {
            var btn = new AeroButton
            {
                Text = label,
                Glyph = glyph,
                ButtonStyle = AeroButtonStyle.Ghost,
                TextAlignment = ContentAlignment.MiddleLeft,
                Size = new Size(202, 34),
                Margin = new Padding(0, 1, 0, 1),
                Padding = new Padding(14, 4, 8, 4),
                GlyphSize = 15f,
                Font = AeroDraw.UiFont(9.5f, FontStyle.Bold)
            };
            var item = new NavItem { ScreenName = screenName, Label = label, Glyph = glyph, Navigate = navigate, Button = btn, AlertState = alertState };
            btn.Click += (s, e) =>
            {
                try
                {
                    navigate();
                }
                catch (Exception ex)
                {
                    log4net.LogManager.GetLogger(typeof(Sidebar)).Error("Navigation to " + screenName + " failed", ex);
                    CustomMessageBox.Show("Could not open " + label + ":\n" + ex.Message, BrandingManager.AppName);
                }
            };
            items.Add(item);
            list.Controls.Add(btn);
            return item;
        }

        /// <summary>Highlights the item whose screen is currently displayed.</summary>
        public void SetActive(string screenName)
        {
            foreach (var it in items)
                it.Button.IsActive = string.Equals(it.ScreenName, screenName, StringComparison.Ordinal);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Color.FromArgb(0x24, 0x24, 0x24)))
                e.Graphics.DrawLine(pen, Width - 1, 0, Width - 1, Height);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                blinkTimer.Stop();
                blinkTimer.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
