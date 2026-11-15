using System;
using System.Collections.Generic;
using System.Drawing;
using System.Media;
using System.Windows.Forms;
using MissionPlanner.AeroAid.Branding;
using MissionPlanner.AeroAid.UI;

namespace MissionPlanner.AeroAid.Vision
{
    /// <summary>
    /// Non-modal alert toasts (one per alert episode, stacked bottom-right of the main window) plus a sound.
    /// Toasts never take keyboard focus, so flight controls keep working.
    /// </summary>
    public static class DetectionAlertNotifier
    {
        private static Form owner;
        private static readonly List<Form> open = new List<Form>();

        public static void Install(Form mainForm)
        {
            owner = mainForm;
            AiDetectionManager.Instance.AlertRaised += ev => AeroDraw.SafeInvoke(owner, () => Show(ev));
        }

        private static void Show(DetectionEvent ev)
        {
            if (AiSettings.PlaySound)
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

            var toast = new AlertToast(ev);
            toast.FormClosed += (s, e) => open.Remove(toast);
            open.Add(toast);
            toast.ShowNoActivate(owner, open.Count - 1);
        }

        private sealed class AlertToast : Form
        {
            private readonly Timer life = new Timer { Interval = 12000 };

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
                var c = ev.Category.Color;
                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.Manual;
                ShowInTaskbar = false;
                BackColor = AeroPalette.Blend(p.Background, c, 0.22f);
                Size = new Size(400, 112);
                Padding = new Padding(16, 12, 12, 10);
                var title = new Label
                {
                    Text = "⚠  " + ev.Category.AlertTitle.ToUpperInvariant(),
                    ForeColor = Color.White,
                    Font = AeroDraw.UiFont(13f, FontStyle.Bold),
                    Dock = DockStyle.Top,
                    Height = 32,
                    UseMnemonic = false
                };
                var detail = new Label
                {
                    Text = $"{ev.Start:HH:mm:ss}  •  confidence {ev.MaxConfidence:P0}  •  {ev.CameraSource}\nClick to open AI Detection.",
                    ForeColor = p.Text,
                    Font = AeroDraw.UiFont(9.5f),
                    Dock = DockStyle.Fill,
                    UseMnemonic = false
                };
                Controls.Add(detail);
                Controls.Add(title);
                Paint += (s, e) =>
                {
                    using (var bar = new SolidBrush(c))
                        e.Graphics.FillRectangle(bar, 0, 0, 6, Height);
                    using (var pen = new Pen(c, 2))
                        e.Graphics.DrawRectangle(pen, 1, 1, Width - 3, Height - 3);
                };
                EventHandler open = (s, e) =>
                {
                    Close();
                    MainV2.instance?.AeroNavigate(AeroShell.ScreenAiDetection);
                };
                Click += open;
                title.Click += open;
                detail.Click += open;
                life.Tick += (s, e) => Close();
                FormClosed += (s, e) => life.Dispose();
            }

            public void ShowNoActivate(Form o, int index)
            {
                if (o != null)
                {
                    var b = o.Bounds;
                    Location = new Point(b.Right - Width - 24, b.Bottom - (Height + 10) * (index + 1) - 40);
                    Owner = o;
                }

                Show();
                life.Start();
            }
        }
    }
}
