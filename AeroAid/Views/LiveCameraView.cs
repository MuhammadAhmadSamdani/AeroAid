using System;
using System.Drawing;
using System.Windows.Forms;
using MissionPlanner.AeroAid.Branding;
using MissionPlanner.AeroAid.Camera;
using MissionPlanner.AeroAid.UI;

namespace MissionPlanner.AeroAid.Views
{
    /// <summary>Live camera stream with start/stop, quality, fullscreen, snapshot and stream status.</summary>
    public class LiveCameraView : AeroScreen
    {
        private readonly VideoView video = new VideoView { Dock = DockStyle.Fill };
        private readonly AeroButton startStop;
        private readonly ComboBox quality = AeroDraw.Combo();
        private readonly Label stateLabel = AeroDraw.Body("", 10.5f);
        private readonly Label detailLabel = AeroDraw.Caption("", 9f);
        private readonly CheckBox cameraAudio = AeroDraw.Check("Play camera microphone audio", false);

        public LiveCameraView() : base("Live Camera", "Real-time RTSP video from the IMOU camera (double-click video for fullscreen)")
        {
            var p = BrandingManager.Palette;
            startStop = AeroDraw.Button("Start stream", AeroIcons.Play, AeroButtonStyle.Primary, 160);
            startStop.Click += (s, e) =>
            {
                if (VideoStreamManager.Instance.State == StreamState.Stopped || VideoStreamManager.Instance.State == StreamState.Error)
                    CameraUi.StartStream();
                else
                    VideoStreamManager.Instance.Stop();
            };
            var full = AeroDraw.Button("Fullscreen", AeroIcons.Fullscreen, AeroButtonStyle.Secondary, 140);
            full.Click += (s, e) => FullscreenVideoForm.Toggle(FindForm());
            var snap = AeroDraw.Button("Snapshot", AeroIcons.Snapshot, AeroButtonStyle.Secondary, 130);
            snap.Click += (s, e) =>
            {
                var path = CameraUi.SaveSnapshot();
                if (path != null)
                    detailLabel.Text = "Snapshot saved: " + path;
            };
            HeaderTools.Controls.Add(startStop);
            HeaderTools.Controls.Add(full);
            HeaderTools.Controls.Add(snap);

            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, BackColor = p.Background };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 340));

            var videoCard = new AeroCard { Dock = DockStyle.Fill, Padding = new Padding(8) };
            videoCard.Controls.Add(video);
            grid.Controls.Add(videoCard, 0, 0);

            var side = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = p.Background };

            var status = new AeroCard { Title = "Stream status", Glyph = AeroIcons.Camera, Width = 320, Height = 210 };
            var sflow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = p.Surface };
            stateLabel.MaximumSize = new Size(290, 0);
            detailLabel.MaximumSize = new Size(290, 0);
            sflow.Controls.Add(stateLabel);
            sflow.Controls.Add(detailLabel);
            status.Controls.Add(sflow);
            side.Controls.Add(status);

            var opts = new AeroCard { Title = "Stream options", Glyph = AeroIcons.Settings, Width = 320, Height = 230 };
            var oflow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = p.Surface };
            quality.Items.AddRange(new object[] { "Main stream (full quality)", "Sub stream (low latency / bandwidth)" });
            quality.Width = 280;
            quality.SelectedIndexChanged += (s, e) =>
            {
                var sub = quality.SelectedIndex == 1;
                if (sub == CameraSettings.UseSubStream) return;
                CameraSettings.UseSubStream = sub;
                AeroSettings.Save();
                VideoStreamManager.Instance.ApplySettings();
            };
            cameraAudio.CheckedChanged += (s, e) =>
            {
                if (cameraAudio.Checked == CameraSettings.PlayCameraAudio) return;
                CameraSettings.PlayCameraAudio = cameraAudio.Checked;
                AeroSettings.Save();
                VideoStreamManager.Instance.ApplySettings();
            };
            oflow.Controls.Add(AeroDraw.Caption("Video quality", 9f));
            oflow.Controls.Add(quality);
            oflow.Controls.Add(cameraAudio);
            var audioNote = AeroDraw.Caption("Plays the camera's own microphone (if its RTSP stream has an audio track). " +
                                             "Separate from the AeroAid voice-command microphone.", 8.5f);
            audioNote.MaximumSize = new Size(290, 0);
            oflow.Controls.Add(audioNote);
            opts.Controls.Add(oflow);
            side.Controls.Add(opts);

            var nav = new AeroCard { Title = "Camera", Glyph = AeroIcons.CameraControl, Width = 320, Height = 150 };
            var nflow = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = p.Surface };
            var ptz = AeroDraw.Button("Camera Control (PTZ)", AeroIcons.CameraControl, AeroButtonStyle.Secondary, 280);
            ptz.Click += (s, e) => MainV2.instance.AeroNavigate(AeroShell.ScreenCameraControl);
            var settings = AeroDraw.Button("Camera settings", AeroIcons.Settings, AeroButtonStyle.Ghost, 280);
            settings.Click += (s, e) => MainV2.instance.AeroNavigate(AeroShell.ScreenSettings);
            nflow.Controls.Add(ptz);
            nflow.Controls.Add(settings);
            nav.Controls.Add(nflow);
            side.Controls.Add(nav);

            grid.Controls.Add(side, 1, 0);
            Body.AutoScroll = false;
            Body.Controls.Add(grid);
        }

        protected override void OnScreenActivated()
        {
            quality.SelectedIndex = CameraSettings.UseSubStream ? 1 : 0;
            cameraAudio.Checked = CameraSettings.PlayCameraAudio;
            VideoStreamManager.Instance.StateChanged += OnState;
            OnState(VideoStreamManager.Instance.State, VideoStreamManager.Instance.StatusMessage);
        }

        protected override void OnScreenDeactivated()
        {
            VideoStreamManager.Instance.StateChanged -= OnState;
        }

        private void OnState(StreamState state, string message)
        {
            AeroDraw.SafeInvoke(this, () =>
            {
                var p = BrandingManager.Palette;
                var running = state == StreamState.Playing || state == StreamState.Starting || state == StreamState.Reconnecting;
                startStop.Text = running ? "Stop stream" : "Start stream";
                startStop.Glyph = running ? AeroIcons.Stop : AeroIcons.Play;
                startStop.ButtonStyle = running ? AeroButtonStyle.Secondary : AeroButtonStyle.Primary;
                stateLabel.Text = "● " + state;
                stateLabel.ForeColor = state == StreamState.Playing ? p.StatusOk
                    : state == StreamState.Error ? p.StatusCritical
                    : state == StreamState.Stopped ? p.StatusIdle : p.StatusWarn;
                var url = VideoStreamManager.Instance.DisplayUrl;
                detailLabel.Text = message + (string.IsNullOrEmpty(url) ? "" : "\n\nSource: " + url) +
                                   (CameraSettings.IsConfigured ? "" : "\n\nConfigure the camera in Settings › Camera.");
            });
        }
    }
}
