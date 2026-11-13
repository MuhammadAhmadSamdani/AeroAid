using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using MissionPlanner.AeroAid.Branding;
using MissionPlanner.AeroAid.Camera;
using MissionPlanner.AeroAid.UI;
using MissionPlanner.AeroAid.Vision;

namespace MissionPlanner.AeroAid.Views
{
    /// <summary>Live video with fire/smoke overlays, detection status, confidence and alert history.</summary>
    public class FireDetectionView : AeroScreen
    {
        private readonly VideoView video = new VideoView { Dock = DockStyle.Fill };
        private readonly Label banner;
        private readonly Label stateLabel = AeroDraw.Body("", 10.5f);
        private readonly Label modelLabel = AeroDraw.Caption("", 8.5f);
        private readonly Label perfLabel = AeroDraw.Caption("", 8.5f);
        private readonly LevelMeter fireMeter = new LevelMeter { Width = 280, Height = 10 };
        private readonly LevelMeter smokeMeter = new LevelMeter { Width = 280, Height = 10 };
        private readonly Label fireValue = AeroDraw.Caption("Fire: --", 9f);
        private readonly Label smokeValue = AeroDraw.Caption("Smoke: --", 9f);
        private readonly ListView historyList;
        private readonly AeroButton toggle;
        private readonly TrackBar threshold;
        private readonly Label thresholdLabel = AeroDraw.Caption("", 9f);
        private readonly Timer ui = new Timer { Interval = 250 };
        private int historyCount = -1;

        public FireDetectionView() : base("Fire Detection", "AI analysis of the live camera feed for visible fire and smoke")
        {
            var p = BrandingManager.Palette;
            toggle = AeroDraw.Button("Disable detection", AeroIcons.Power, AeroButtonStyle.Secondary, 180);
            toggle.Click += (s, e) =>
            {
                FireSettings.Enabled = !FireSettings.Enabled;
                AeroSettings.Save();
                FireDetectionManager.Instance.ApplySettings();
            };
            var load = AeroDraw.Button("Load model...", AeroIcons.Folder, AeroButtonStyle.Secondary, 160);
            load.Click += (s, e) => BrowseModel();
            var folder = AeroDraw.Button("Detections folder", AeroIcons.Folder, AeroButtonStyle.Ghost, 180);
            folder.Click += (s, e) =>
            {
                Directory.CreateDirectory(FireSettings.DetectionsDirectory);
                Process.Start(new ProcessStartInfo(FireSettings.DetectionsDirectory) { UseShellExecute = true });
            };
            var stream = AeroDraw.Button("Start / stop video", AeroIcons.Play, AeroButtonStyle.Primary, 180);
            stream.Click += (s, e) =>
            {
                var st = VideoStreamManager.Instance.State;
                if (st == StreamState.Stopped || st == StreamState.Error) CameraUi.StartStream();
                else VideoStreamManager.Instance.Stop();
            };
            HeaderTools.Controls.Add(stream);
            HeaderTools.Controls.Add(toggle);
            HeaderTools.Controls.Add(load);
            HeaderTools.Controls.Add(folder);

            banner = new Label
            {
                Dock = DockStyle.Top,
                Height = 0,
                ForeColor = Color.White,
                BackColor = p.StatusCritical,
                Font = AeroDraw.UiFont(14f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter
            };

            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, BackColor = p.Background };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 380));
            var videoCard = new AeroCard { Dock = DockStyle.Fill, Padding = new Padding(8) };
            videoCard.Controls.Add(video);
            grid.Controls.Add(videoCard, 0, 0);

            var side = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = p.Background };

            var status = new AeroCard { Title = "Detection status", Glyph = AeroIcons.Fire, Width = 360, Height = 330 };
            var sf = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = p.Surface };
            stateLabel.MaximumSize = new Size(320, 0);
            modelLabel.MaximumSize = new Size(320, 0);
            fireMeter.BarColor = p.StatusCritical;
            smokeMeter.BarColor = Color.FromArgb(0xC8, 0xC8, 0xC8);
            threshold = new TrackBar { Minimum = 10, Maximum = 95, TickFrequency = 5, Width = 300, BackColor = p.Surface };
            threshold.ValueChanged += (s, e) =>
            {
                thresholdLabel.Text = $"Confidence threshold: {threshold.Value}%";
                FireSettings.ConfidenceThreshold = threshold.Value / 100.0;
            };
            threshold.MouseUp += (s, e) => AeroSettings.Save();
            sf.Controls.AddRange(new Control[] { stateLabel, modelLabel, perfLabel, fireValue, fireMeter, smokeValue, smokeMeter, thresholdLabel, threshold });
            status.Controls.Add(sf);
            side.Controls.Add(status);

            var hist = new AeroCard { Title = "Detection history", Glyph = AeroIcons.Telemetry, Width = 360, Height = 330 };
            historyList = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                BackColor = p.Surface,
                ForeColor = p.Text,
                BorderStyle = BorderStyle.None,
                Font = AeroDraw.UiFont(9f),
                HeaderStyle = ColumnHeaderStyle.Nonclickable
            };
            historyList.Columns.Add("Time", 80);
            historyList.Columns.Add("Type", 95);
            historyList.Columns.Add("Conf.", 55);
            historyList.Columns.Add("Duration", 70);
            historyList.DoubleClick += (s, e) =>
            {
                if (historyList.SelectedItems.Count == 0) return;
                var path = historyList.SelectedItems[0].Tag as string;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            };
            hist.Controls.Add(historyList);
            var hint = AeroDraw.Caption("Double-click an entry to open its screenshot.", 8.5f);
            hint.Dock = DockStyle.Bottom;
            hist.Controls.Add(hint);
            side.Controls.Add(hist);

            grid.Controls.Add(side, 1, 0);
            Body.AutoScroll = false;
            Body.Controls.Add(grid);
            Body.Controls.Add(banner);
            ui.Tick += (s, e) => Refresh2();
        }

        private void BrowseModel()
        {
            using (var dlg = new OpenFileDialog { Filter = "ONNX model (*.onnx)|*.onnx", Title = "Select a YOLO fire/smoke ONNX model" })
            {
                try
                {
                    var dir = Path.GetDirectoryName(FireSettings.ModelPath);
                    if (Directory.Exists(dir)) dlg.InitialDirectory = dir;
                }
                catch (Exception)
                {
                }

                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                FireSettings.ModelPath = dlg.FileName;
                FireSettings.Enabled = true;
                AeroSettings.Save();
                FireDetectionManager.Instance.ApplySettings();
            }
        }

        protected override void OnScreenActivated()
        {
            threshold.Value = (int)Math.Round(FireSettings.ConfidenceThreshold * 100);
            thresholdLabel.Text = $"Confidence threshold: {threshold.Value}%";
            historyCount = -1;
            ui.Start();
            Refresh2();
        }

        protected override void OnScreenDeactivated() => ui.Stop();

        private void Refresh2()
        {
            var p = BrandingManager.Palette;
            var m = FireDetectionManager.Instance;
            toggle.Text = FireSettings.Enabled ? "Disable detection" : "Enable detection";
            toggle.ButtonStyle = FireSettings.Enabled ? AeroButtonStyle.Secondary : AeroButtonStyle.Primary;
            stateLabel.Text = "● " + m.State + " — " + m.StatusMessage;
            stateLabel.ForeColor = m.State == FireDetectorState.Running ? p.StatusOk
                : m.State == FireDetectorState.ModelError ? p.StatusCritical
                : m.State == FireDetectorState.NoModel ? p.StatusWarn : p.StatusIdle;
            modelLabel.Text = string.IsNullOrEmpty(m.ModelDescription) ? "Model: " + FireSettings.ModelPath : "Model: " + m.ModelDescription;
            perfLabel.Text = m.State == FireDetectorState.Running ? $"Inference {m.InferenceMs:0} ms/frame • analysing up to {FireSettings.AnalysisFps:0.#} fps" : "";

            var dets = m.LatestDetections;
            var fire = dets.Where(d => d.Kind == DetectionKind.Fire).Select(d => d.Confidence).DefaultIfEmpty(0).Max();
            var smoke = dets.Where(d => d.Kind == DetectionKind.Smoke).Select(d => d.Confidence).DefaultIfEmpty(0).Max();
            var running = m.State == FireDetectorState.Running;
            fireMeter.Value = fire;
            smokeMeter.Value = smoke;
            fireValue.Text = running ? $"Fire: {(fire > 0 ? fire.ToString("P0") : "not detected")}" : "Fire: --";
            smokeValue.Text = running ? $"Smoke: {(smoke > 0 ? smoke.ToString("P0") : "not detected")}" : "Smoke: --";

            var alert = m.CurrentAlert;
            banner.Height = alert != null ? 46 : 0;
            if (alert != null)
                banner.Text = $"⚠  {alert.KindText.ToUpperInvariant()} DETECTED  •  since {alert.Start:HH:mm:ss}  •  max {alert.MaxConfidence:P0}";

            var history = m.History.Take(200).ToList();
            if (history.Count != historyCount)
            {
                // rebuild only when events are added (avoids flicker)
                historyCount = history.Count;
                historyList.BeginUpdate();
                historyList.Items.Clear();
                foreach (var ev in history)
                    historyList.Items.Add(new ListViewItem(new[] { "", "", "", "" }) { Tag = ev.ScreenshotPath });
                historyList.EndUpdate();
            }

            // refresh texts in place (type/confidence/duration change while an alert is ongoing)
            for (var i = 0; i < history.Count && i < historyList.Items.Count; i++)
            {
                var ev = history[i];
                var item = historyList.Items[i];
                SetText(item, 0, ev.Start.ToString("HH:mm:ss"));
                SetText(item, 1, ev.KindText);
                SetText(item, 2, ev.MaxConfidence.ToString("P0"));
                SetText(item, 3, ev.End == null ? "ongoing" : ev.Duration.TotalSeconds.ToString("0") + " s");
                item.Tag = ev.ScreenshotPath;
                var color = ev.End == null ? p.StatusCritical : p.Text;
                if (item.ForeColor != color) item.ForeColor = color;
            }
        }

        private static void SetText(ListViewItem item, int index, string text)
        {
            if (item.SubItems[index].Text != text)
                item.SubItems[index].Text = text;
        }
    }
}
