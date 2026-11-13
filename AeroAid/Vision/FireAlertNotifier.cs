using System;
using System.Drawing;
using System.Media;
using System.Windows.Forms;
using MissionPlanner.AeroAid.Branding;
using MissionPlanner.AeroAid.UI;

namespace MissionPlanner.AeroAid.Vision
{
    /// <summary>
    /// Shows a non-modal alert toast (bottom-right of the main window) and plays a sound once per alert
    /// episode. Never steals keyboard focus (flight controls must keep working).
    /// </summary>
    public static class FireAlertNotifier
    {
        private static Form owner;

        public static void Install(Form mainForm)
        {
            owner = mainForm;
            FireDetectionManager.Instance.AlertRaised += ev => AeroDraw.SafeInvoke(owner, () => Show(ev));
        }

        private static void Show(DetectionEvent ev)
        {
            if (FireSettings.PlaySound)
            {
                try
                {
                    SystemSounds.Exclamation.Play();
                }
                catch (Exception)
                {
                    // sound is best-effort
                }
            }

            new AlertToast(ev).ShowNoActivate(owner);
        }

        private sealed class AlertToast : Form
        {
            private readonly Timer life = new Timer { Interval = 10000 };

            protected override bool ShowWithoutActivation => true;

            protected override CreateParams CreateParams
            {
                get
                {
                    var cp = base.CreateParams;
                    cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
                    return cp;
                }
            }

            public AlertToast(DetectionEvent ev)
            {
                var p = BrandingManager.Palette;
                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.Manual;
                ShowInTaskbar = false;
                BackColor = Color.FromArgb(0x4A, 0x14, 0x12);
                Size = new Size(380, 110);
                Padding = new Padding(14);
                var title = new Label
                {
                    Text = "⚠  " + ev.KindText.ToUpperInvariant() + " DETECTED",
                    ForeColor = Color.White,
                    Font = AeroDraw.UiFont(13f, FontStyle.Bold),
                    Dock = DockStyle.Top,
                    Height = 34
                };
                var detail = new Label
                {
                    Text = $"{ev.Start:HH:mm:ss}  •  confidence {ev.MaxConfidence:P0}\nOpen Fire Detection for details.",
                    ForeColor = p.Text,
                    Font = AeroDraw.UiFont(9.5f),
                    Dock = DockStyle.Fill
                };
                Controls.Add(detail);
                Controls.Add(title);
                Paint += (s, e) =>
                {
                    using (var pen = new Pen(p.StatusCritical, 3))
                        e.Graphics.DrawRectangle(pen, 1, 1, Width - 3, Height - 3);
                };
                Click += (s, e) => Close();
                title.Click += (s, e) => Close();
                detail.Click += (s, e) =>
                {
                    Close();
                    MainV2.instance?.AeroNavigate(AeroShell.ScreenFireDetection);
                };
                life.Tick += (s, e) => Close();
                FormClosed += (s, e) => life.Dispose();
            }

            public void ShowNoActivate(Form o)
            {
                if (o != null)
                {
                    var b = o.Bounds;
                    Location = new Point(b.Right - Width - 24, b.Bottom - Height - 48);
                    Owner = o;
                }

                Show();
                life.Start();
            }
        }
    }
}
