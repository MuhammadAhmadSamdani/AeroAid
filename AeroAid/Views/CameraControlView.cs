using System;
using System.Drawing;
using System.Windows.Forms;
using MissionPlanner.AeroAid.Branding;
using MissionPlanner.AeroAid.Camera;
using MissionPlanner.AeroAid.UI;

namespace MissionPlanner.AeroAid.Views
{
    /// <summary>Camera pan/tilt/zoom (ONVIF) with live preview and detected camera capabilities.</summary>
    public class CameraControlView : AeroScreen
    {
        private readonly VideoView preview = new VideoView { Dock = DockStyle.Fill };
        private readonly PtzPad pad = new PtzPad();
        private readonly Label ptzMessage = AeroDraw.Caption("", 9f);
        private readonly Label capsText = AeroDraw.Body("", 9.5f);
        private readonly TrackBar speed;
        private readonly ListBox log;
        private readonly AeroButton detect;

        public CameraControlView() : base("Camera Control", "Pan / tilt / zoom via ONVIF — independent of drone movement")
        {
            var p = BrandingManager.Palette;
            detect = AeroDraw.Button("Detect capabilities", AeroIcons.Refresh, AeroButtonStyle.Primary, 200);
            detect.Click += async (s, e) =>
            {
                detect.Enabled = false;
                try
                {
                    await CameraConnectionManager.ProbeAsync();
                }
                finally
                {
                    detect.Enabled = true;
                }
            };
            var stream = AeroDraw.Button("Start / stop video", AeroIcons.Play, AeroButtonStyle.Secondary, 180);
            stream.Click += (s, e) =>
            {
                var st = VideoStreamManager.Instance.State;
                if (st == StreamState.Stopped || st == StreamState.Error) CameraUi.StartStream();
                else VideoStreamManager.Instance.Stop();
            };
            HeaderTools.Controls.Add(detect);
            HeaderTools.Controls.Add(stream);

            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, BackColor = p.Background };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 470));
            var videoCard = new AeroCard { Dock = DockStyle.Fill, Padding = new Padding(8) };
            videoCard.Controls.Add(preview);
            grid.Controls.Add(videoCard, 0, 0);

            var side = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = p.Background };

            var ptzCard = new AeroCard { Title = "Pan / tilt / zoom", Glyph = AeroIcons.CameraControl, Width = 450, Height = 380 };
            var pflow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = p.Surface };
            pflow.Controls.Add(pad);
            speed = new TrackBar { Minimum = 1, Maximum = 10, TickFrequency = 1, Width = 400, BackColor = p.Surface };
            speed.ValueChanged += (s, e) =>
            {
                CameraSettings.PtzSpeed = speed.Value / 10.0;
                AeroSettings.Save();
            };
            pflow.Controls.Add(AeroDraw.Caption("PTZ speed", 9f));
            pflow.Controls.Add(speed);
            ptzMessage.MaximumSize = new Size(420, 0);
            pflow.Controls.Add(ptzMessage);
            ptzCard.Controls.Add(pflow);
            side.Controls.Add(ptzCard);

            var capsCard = new AeroCard { Title = "Detected capabilities", Glyph = AeroIcons.About, Width = 450, Height = 260 };
            capsText.MaximumSize = new Size(420, 0);
            capsText.Dock = DockStyle.Fill;
            capsCard.Controls.Add(capsText);
            side.Controls.Add(capsCard);

            var audioCard = new AeroCard { Title = "Audio channels", Glyph = AeroIcons.Mic, Width = 450, Height = 200 };
            var audio = AeroDraw.Caption(
                "• Voice-command microphone: the PC microphone used for voice takeoff (Settings › Voice).\n" +
                "• Camera audio input: the camera's microphone, played from its RTSP stream (Live Camera › options).\n" +
                "• Camera two-way audio (talk to the camera): NOT implemented. It requires an ONVIF Profile-T audio " +
                "back-channel, which has not been confirmed for the K2EP; Imou's own app uses its cloud protocol.", 9f);
            audio.MaximumSize = new Size(420, 0);
            audioCard.Controls.Add(audio);
            side.Controls.Add(audioCard);

            var logCard = new AeroCard { Title = "Camera log", Glyph = AeroIcons.Telemetry, Width = 450, Height = 160 };
            log = new ListBox { Dock = DockStyle.Fill, BackColor = p.Surface, ForeColor = p.Text, BorderStyle = BorderStyle.None, Font = AeroDraw.UiFont(8.5f) };
            logCard.Controls.Add(log);
            side.Controls.Add(logCard);

            grid.Controls.Add(side, 1, 0);
            Body.AutoScroll = false;
            Body.Controls.Add(grid);
        }

        protected override void OnScreenActivated()
        {
            speed.Value = Math.Max(1, Math.Min(10, (int)Math.Round(CameraSettings.PtzSpeed * 10)));
            pad.SetHint(PtzDirection.Up, Input.ShortcutMap.KeyText(Input.ShortcutMap.KeyFor(Input.ShortcutAction.CameraUp)));
            pad.SetHint(PtzDirection.Down, Input.ShortcutMap.KeyText(Input.ShortcutMap.KeyFor(Input.ShortcutAction.CameraDown)));
            pad.SetHint(PtzDirection.Left, Input.ShortcutMap.KeyText(Input.ShortcutMap.KeyFor(Input.ShortcutAction.CameraLeft)));
            pad.SetHint(PtzDirection.Right, Input.ShortcutMap.KeyText(Input.ShortcutMap.KeyFor(Input.ShortcutAction.CameraRight)));
            pad.SetHint(PtzDirection.ZoomIn, Input.ShortcutMap.KeyText(Input.ShortcutMap.KeyFor(Input.ShortcutAction.ZoomIn)));
            pad.SetHint(PtzDirection.ZoomOut, Input.ShortcutMap.KeyText(Input.ShortcutMap.KeyFor(Input.ShortcutAction.ZoomOut)));
            pad.SetCenterHint(Input.ShortcutMap.KeyText(Input.ShortcutMap.KeyFor(Input.ShortcutAction.CameraCenter)));
            CameraConnectionManager.CapabilitiesChanged += OnCaps;
            CameraPtzManager.Instance.Feedback += OnFeedback;
            OnCaps();
            if (!CameraConnectionManager.Capabilities.Tested && CameraSettings.IsConfigured && !CameraConnectionManager.Probing)
                _ = CameraConnectionManager.ProbeAsync();
        }

        protected override void OnScreenDeactivated()
        {
            CameraConnectionManager.CapabilitiesChanged -= OnCaps;
            CameraPtzManager.Instance.Feedback -= OnFeedback;
            CameraPtzManager.Instance.StopAll();
        }

        private void OnFeedback(string message, StatusLevel level)
        {
            AeroDraw.SafeInvoke(this, () =>
            {
                log.Items.Insert(0, DateTime.Now.ToString("HH:mm:ss") + "  " + message);
                while (log.Items.Count > 100) log.Items.RemoveAt(log.Items.Count - 1);
            });
        }

        private void OnCaps()
        {
            AeroDraw.SafeInvoke(this, () =>
            {
                var c = CameraConnectionManager.Capabilities;
                var p = BrandingManager.Palette;
                if (CameraConnectionManager.Probing)
                {
                    ptzMessage.Text = "Detecting camera capabilities...";
                    ptzMessage.ForeColor = p.StatusWarn;
                    return;
                }

                if (!CameraSettings.IsConfigured)
                {
                    ptzMessage.Text = "Camera not configured (Settings › Camera).";
                    ptzMessage.ForeColor = p.Muted;
                    capsText.Text = "No camera configured.";
                    return;
                }

                ptzMessage.Text = c.Tested ? c.PtzMessage : "Press \"Detect capabilities\" to check PTZ support.";
                ptzMessage.ForeColor = c.PtzAvailable ? p.StatusOk : c.Tested ? p.StatusWarn : p.Muted;
                capsText.Text = !c.Tested
                    ? "Not tested yet."
                    : $"RTSP: {c.Rtsp.Message}\n\nONVIF: {c.OnvifMessage}\n\nPTZ: {c.PtzMessage}" +
                      (string.IsNullOrEmpty(c.DeviceModel) ? "" : $"\n\nDevice: {c.Manufacturer} {c.DeviceModel} (fw {c.Firmware})") +
                      (string.IsNullOrEmpty(c.OnvifStreamUri) ? "" : $"\n\nONVIF stream URI: {c.OnvifStreamUri}") +
                      $"\n\nTested {c.ProbedAt:HH:mm:ss}";
            });
        }
    }
}
