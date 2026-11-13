using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using MissionPlanner.AeroAid.Branding;
using MissionPlanner.AeroAid.UI;

namespace MissionPlanner.AeroAid.Camera
{
    /// <summary>
    /// Displays the shared live video stream (letter-boxed) with optional overlays and a status message
    /// when no video is available. Double-click toggles fullscreen.
    /// </summary>
    public class VideoView : Control
    {
        private readonly Timer refresh = new Timer { Interval = 33 };
        private long lastFrameId = -1;

        /// <summary>Overlay painters (e.g. fire detection boxes): (graphics, image rectangle on screen).</summary>
        public static event Action<Graphics, Rectangle> Overlay;

        public VideoView()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Black;
            refresh.Tick += (s, e) =>
            {
                var id = VideoStreamManager.Instance.FrameId;
                if (id != lastFrameId || ShowOverlays)
                {
                    lastFrameId = id;
                    Invalidate();
                }
            };
        }

        public bool ShowOverlays { get; set; } = true;
        public bool ShowInfo { get; set; } = true;
        public bool AllowFullscreen { get; set; } = true;

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            refresh.Enabled = Visible;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            refresh.Enabled = Visible;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                refresh.Stop();
                refresh.Dispose();
            }

            base.Dispose(disposing);
        }

        protected override void OnDoubleClick(EventArgs e)
        {
            base.OnDoubleClick(e);
            if (AllowFullscreen)
                FullscreenVideoForm.Toggle(FindForm());
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            var vs = VideoStreamManager.Instance;
            var imgRect = vs.DrawLatest(g, ClientRectangle);
            var p = BrandingManager.Palette;

            if (imgRect.IsEmpty)
            {
                var msg = vs.State == StreamState.Stopped ? "Camera stream stopped" : vs.StatusMessage;
                if (!CameraSettings.IsConfigured)
                    msg = "Camera not configured — Settings › Camera";
                using (var iconFont = AeroIcons.CreateFont(Math.Min(48f, Height / 5f)))
                using (var ib = new SolidBrush(Color.FromArgb(70, p.Text)))
                {
                    if (iconFont != null)
                        g.DrawString(AeroIcons.Camera, iconFont, ib, ClientRectangle,
                            new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
                }

                using (var f = AeroDraw.UiFont(Math.Max(8f, Math.Min(11f, Width / 45f))))
                using (var b = new SolidBrush(vs.State == StreamState.Error ? p.StatusCritical : p.Muted))
                {
                    var r = new Rectangle(10, Height / 2 + Height / 8, Width - 20, Height / 2 - Height / 8 - 6);
                    g.DrawString(msg, f, b, r, new StringFormat { Alignment = StringAlignment.Center });
                }

                return;
            }

            if (ShowOverlays)
            {
                try
                {
                    Overlay?.Invoke(g, imgRect);
                }
                catch (Exception ex)
                {
                    log4net.LogManager.GetLogger(typeof(VideoView)).Error("Overlay failed", ex);
                }
            }

            if (ShowInfo)
            {
                var text = $"● LIVE  {vs.FrameSize.Width}x{vs.FrameSize.Height}  {vs.Fps:0} fps";
                using (var f = AeroDraw.UiFont(8.5f, FontStyle.Bold))
                {
                    var sz = g.MeasureString(text, f);
                    var r = new RectangleF(imgRect.X + 8, imgRect.Y + 8, sz.Width + 12, sz.Height + 6);
                    using (var bg = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
                        g.FillRectangle(bg, r);
                    using (var tb = new SolidBrush(vs.State == StreamState.Playing ? p.StatusOk : p.StatusWarn))
                        g.DrawString(text, f, tb, r.X + 6, r.Y + 3);
                }
            }
        }
    }

    /// <summary>Borderless fullscreen live video. Esc or double-click closes.</summary>
    public class FullscreenVideoForm : Form
    {
        private static FullscreenVideoForm current;

        public static void Toggle(Form owner)
        {
            if (current != null && !current.IsDisposed)
            {
                current.Close();
                return;
            }

            current = new FullscreenVideoForm();
            current.Show(owner);
        }

        private FullscreenVideoForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
            BackColor = Color.Black;
            KeyPreview = true;
            ShowInTaskbar = false;
            Text = BrandingManager.AppName + " - Live camera";
            var view = new VideoView { Dock = DockStyle.Fill, AllowFullscreen = false };
            view.DoubleClick += (s, e) => Close();
            Controls.Add(view);
            var hint = new Label
            {
                Text = "Esc / double-click to exit fullscreen",
                ForeColor = BrandingManager.Palette.Muted,
                BackColor = Color.Black,
                AutoSize = true,
                Location = new Point(12, 12)
            };
            Controls.Add(hint);
            hint.BringToFront();
            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) Close();
            };
            FormClosed += (s, e) => current = null;
        }
    }
}
