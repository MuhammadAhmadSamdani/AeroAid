using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using MissionPlanner.AeroAid.Branding;
using MissionPlanner.AeroAid.Camera;
using MissionPlanner.AeroAid.UI;
using MissionPlanner.AeroAid.Vision;

namespace MissionPlanner.AeroAid.Views
{
    /// <summary>
    /// AI Detection: multi-category AI surveillance / disaster-response monitoring on the live camera feed
    /// (fire, injured person, flood, damaged building, smoke, other emergencies). Categories, filters and
    /// status rows are generated from <see cref="DetectionCategories"/>, so new categories need no UI changes.
    /// </summary>
    public class AiDetectionView : AeroScreen
    {
        private readonly VideoView video = new VideoView { Dock = DockStyle.Fill };
        private readonly Label banner;
        private readonly StatusTile tEngine = new StatusTile { Caption = "AI engine" };
        private readonly StatusTile tModels = new StatusTile { Caption = "Models" };
        private readonly StatusTile tCamera = new StatusTile { Caption = "Camera" };
        private readonly StatusTile tLive = new StatusTile { Caption = "Live detections" };
        private readonly StatusTile tAlerts = new StatusTile { Caption = "Active alerts" };
        private readonly StatusTile tInference = new StatusTile { Caption = "Inference", ShowDot = false };
        private readonly FlowLayoutPanel chips;
        private readonly FlowLayoutPanel categoryList;
        private readonly Label engineMessage = AeroDraw.Caption("", 9f);
        private readonly ListView historyList;
        private readonly ImageList thumbs = new ImageList { ImageSize = new Size(96, 54), ColorDepth = ColorDepth.Depth32Bit };
        private readonly PictureBox detailImage;
        private readonly Label detailText = AeroDraw.Body("", 9.5f);
        private readonly AeroButton ackOne, openImage, toggle;
        private readonly Timer ui = new Timer { Interval = 250 };
        private readonly Dictionary<int, int> thumbIndex = new Dictionary<int, int>();
        private List<DetectionEvent> shown = new List<DetectionEvent>();
        private int historyCount = -1;
        private string chipSignature = "";

        public AiDetectionView() : base("AI Detection", "Multi-category AI surveillance of the live camera feed for disaster response")
        {
            var p = BrandingManager.Palette;

            // ---- header tools
            var stream = AeroDraw.Button("Start / stop video", AeroIcons.Play, AeroButtonStyle.Primary, 180);
            stream.Click += (s, e) =>
            {
                var st = VideoStreamManager.Instance.State;
                if (st == StreamState.Stopped || st == StreamState.Error) CameraUi.StartStream();
                else VideoStreamManager.Instance.Stop();
            };
            toggle = AeroDraw.Button("Disable AI", AeroIcons.Power, AeroButtonStyle.Secondary, 150);
            toggle.Click += (s, e) =>
            {
                AiSettings.Enabled = !AiSettings.Enabled;
                AeroSettings.Save();
                AiDetectionManager.Instance.ApplySettings();
            };
            var ackAll = AeroDraw.Button("Acknowledge all", AeroIcons.Check, AeroButtonStyle.Secondary, 170);
            ackAll.Click += (s, e) => AiDetectionManager.Instance.AcknowledgeAll();
            var folder = AeroDraw.Button("Detections folder", AeroIcons.Folder, AeroButtonStyle.Ghost, 180);
            folder.Click += (s, e) =>
            {
                Directory.CreateDirectory(AiSettings.DetectionsDirectory);
                Process.Start(new ProcessStartInfo(AiSettings.DetectionsDirectory) { UseShellExecute = true });
            };
            HeaderTools.Controls.Add(stream);
            HeaderTools.Controls.Add(toggle);
            HeaderTools.Controls.Add(ackAll);
            HeaderTools.Controls.Add(folder);

            // ---- alert banner
            banner = new Label
            {
                Dock = DockStyle.Top,
                Height = 0,
                ForeColor = Color.White,
                BackColor = p.StatusCritical,
                Font = AeroDraw.UiFont(13f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter,
                UseMnemonic = false
            };

            // ---- status tiles
            var tiles = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, BackColor = p.Background, Padding = new Padding(0, 4, 0, 2) };
            foreach (var t in new[] { tEngine, tModels, tCamera, tLive, tAlerts, tInference })
            {
                t.Size = new Size(176, 62);
                tiles.Controls.Add(t);
            }

            // ---- filter chips
            chips = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, BackColor = p.Background, Padding = new Padding(2, 4, 0, 6) };

            // ---- video + categories
            var grid = new TableLayoutPanel { Dock = DockStyle.Top, Height = 540, ColumnCount = 2, BackColor = p.Background };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 410));
            var videoCard = new AeroCard { Title = "AI Detection Panel — live feed", Glyph = AeroIcons.Camera, Dock = DockStyle.Fill, Padding = new Padding(8, 42, 8, 8) };
            videoCard.Controls.Add(video);
            grid.Controls.Add(videoCard, 0, 0);

            var catCard = new AeroCard { Title = "AI Detection Status", Glyph = AeroIcons.Telemetry, Dock = DockStyle.Fill };
            categoryList = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = p.Surface };
            engineMessage.Dock = DockStyle.Bottom;
            engineMessage.MaximumSize = new Size(380, 0);
            engineMessage.AutoSize = true;
            catCard.Controls.Add(categoryList);
            catCard.Controls.Add(engineMessage);
            grid.Controls.Add(catCard, 1, 0);

            // ---- history + details
            var histGrid = new TableLayoutPanel { Dock = DockStyle.Top, Height = 380, ColumnCount = 2, BackColor = p.Background };
            histGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            histGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 410));
            var histCard = new AeroCard { Title = "Detection History", Glyph = AeroIcons.Telemetry, Dock = DockStyle.Fill };
            historyList = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                BackColor = p.Surface,
                ForeColor = p.Text,
                BorderStyle = BorderStyle.None,
                Font = AeroDraw.UiFont(9.5f),
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                SmallImageList = thumbs
            };
            historyList.Columns.Add("Time", 230);
            historyList.Columns.Add("Category", 150);
            historyList.Columns.Add("Confidence", 90);
            historyList.Columns.Add("Status", 110);
            historyList.Columns.Add("Duration", 80);
            historyList.Columns.Add("Camera source", 220);
            historyList.SelectedIndexChanged += (s, e) => ShowDetail();
            historyList.DoubleClick += (s, e) => OpenSelectedImage();
            histCard.Controls.Add(historyList);
            histGrid.Controls.Add(histCard, 0, 0);

            var detailCard = new AeroCard { Title = "Detection details", Glyph = AeroIcons.About, Dock = DockStyle.Fill };
            var df = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = p.Surface, AutoScroll = true };
            detailImage = new PictureBox { Size = new Size(372, 170), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black, Margin = new Padding(0, 0, 0, 6) };
            detailImage.DoubleClick += (s, e) => OpenSelectedImage();
            detailText.MaximumSize = new Size(372, 0);
            var dbuttons = new FlowLayoutPanel { AutoSize = true, BackColor = p.Surface, Margin = new Padding(0, 6, 0, 0) };
            ackOne = AeroDraw.Button("Acknowledge", AeroIcons.Check, AeroButtonStyle.Primary, 160, 34);
            ackOne.Click += (s, e) => AiDetectionManager.Instance.Acknowledge(Selected());
            openImage = AeroDraw.Button("Open image", AeroIcons.Snapshot, AeroButtonStyle.Secondary, 150, 34);
            openImage.Click += (s, e) => OpenSelectedImage();
            dbuttons.Controls.Add(ackOne);
            dbuttons.Controls.Add(openImage);
            df.Controls.Add(detailImage);
            df.Controls.Add(detailText);
            df.Controls.Add(dbuttons);
            detailCard.Controls.Add(df);
            histGrid.Controls.Add(detailCard, 1, 0);

            Body.Controls.Add(histGrid);
            Body.Controls.Add(grid);
            Body.Controls.Add(chips);
            Body.Controls.Add(tiles);
            Body.Controls.Add(banner);

            ui.Tick += (s, e) => RefreshUi();
            ShowDetail();
        }

        protected override void OnScreenActivated()
        {
            chipSignature = "";
            historyCount = -1;
            ui.Start();
            RefreshUi();
        }

        protected override void OnScreenDeactivated() => ui.Stop();

        // ------------------------------------------------------------------ filters

        private void BuildChips()
        {
            var disabled = AiSettings.DisabledCategories;
            var sig = AiDetectionManager.Instance.State + "|" + string.Join(",", DetectionCategories.All.Select(c => c.Id + (disabled.Contains(c.Id) ? "-" : "+") +
                                                                          (AiDetectionManager.Instance.IsOperational(c.Id) ? "o" : "")));
            if (sig == chipSignature) return;
            chipSignature = sig;
            chips.SuspendLayout();
            foreach (Control c in chips.Controls.Cast<Control>().ToList()) c.Dispose();
            chips.Controls.Clear();
            chips.Controls.Add(new Label { Text = "FILTERS", ForeColor = BrandingManager.Palette.Muted, Font = AeroDraw.UiFont(8f, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 12, 10, 0) });

            var all = Chip("All detections", AeroIcons.Check, disabled.Count == 0, null);
            all.Click += (s, e) =>
            {
                AiSettings.DisabledCategories = new HashSet<string>();
                AeroSettings.Save();
            };
            chips.Controls.Add(all);
            foreach (var cat in DetectionCategories.All)
            {
                var category = cat;
                var noModel = AiSettings.Enabled && AiDetectionManager.Instance.State != AiEngineState.Disabled &&
                              !AiDetectionManager.Instance.IsOperational(cat.Id);
                var b = Chip(noModel ? cat.Name + " • no model" : cat.Name, cat.Glyph, !disabled.Contains(cat.Id), cat.Color);
                b.Click += (s, e) =>
                {
                    var set = AiSettings.DisabledCategories;
                    if (!set.Remove(category.Id)) set.Add(category.Id);
                    AiSettings.DisabledCategories = set;
                    AeroSettings.Save();
                };
                chips.Controls.Add(b);
            }

            chips.ResumeLayout();
        }

        private static AeroButton Chip(string text, string glyph, bool active, Color? color)
        {
            var b = new AeroButton
            {
                Text = text,
                Glyph = glyph,
                ButtonStyle = AeroButtonStyle.Secondary,
                IsActive = active,
                GlyphSize = 13f,
                Font = AeroDraw.UiFont(9f, FontStyle.Bold),
                Height = 34,
                CornerRadius = 17,
                Margin = new Padding(0, 0, 8, 0)
            };
            using (var g = b.CreateGraphics())
                b.Width = (int)g.MeasureString(text, b.Font).Width + 60;
            if (!active)
                b.AlertColor = BrandingManager.Palette.Muted;
            else if (color.HasValue)
                b.AlertColor = color;
            return b;
        }

        // ------------------------------------------------------------------ refresh

        private void RefreshUi()
        {
            var p = BrandingManager.Palette;
            var m = AiDetectionManager.Instance;
            var models = m.Models;
            var vs = VideoStreamManager.Instance;

            toggle.Text = AiSettings.Enabled ? "Disable AI" : "Enable AI";
            toggle.ButtonStyle = AiSettings.Enabled ? AeroButtonStyle.Secondary : AeroButtonStyle.Primary;

            tEngine.SetValue(m.State.ToString(), m.State == AiEngineState.Running ? StatusLevel.Ok
                : m.State == AiEngineState.ModelError ? StatusLevel.Critical
                : m.State == AiEngineState.Disabled ? StatusLevel.Unavailable : StatusLevel.Warning);
            var ok = models.Count(x => x.Ok);
            tModels.SetValue(models.Count == 0 ? "None" : $"{ok} of {models.Count} loaded",
                ok == 0 ? StatusLevel.Critical : ok < models.Count(x => x.Config.Enabled) ? StatusLevel.Warning : StatusLevel.Ok);
            tCamera.SetValue(vs.State == StreamState.Playing ? "Live" : vs.State.ToString(),
                vs.State == StreamState.Playing ? StatusLevel.Ok : vs.State == StreamState.Error ? StatusLevel.Critical
                : vs.State == StreamState.Stopped ? StatusLevel.Unavailable : StatusLevel.Warning);
            var live = m.LatestDetections;
            tLive.SetValue(m.State == AiEngineState.Running ? live.Count.ToString() : "--",
                m.State != AiEngineState.Running ? StatusLevel.Unavailable : live.Count > 0 ? StatusLevel.Warning : StatusLevel.Ok);
            var alerts = m.ActiveAlerts;
            tAlerts.SetValue(alerts.Count.ToString(), alerts.Count == 0 ? StatusLevel.Ok : alerts.Any(a => !a.Acknowledged) ? StatusLevel.Critical : StatusLevel.Warning);
            tInference.SetValue(m.State == AiEngineState.Running ? $"{m.InferenceMs:0} ms" : "--", StatusLevel.Unavailable);
            engineMessage.Text = m.StatusMessage;
            engineMessage.ForeColor = m.State == AiEngineState.Running ? p.Muted : m.State == AiEngineState.ModelError ? p.StatusCritical : p.StatusWarn;

            // banner
            if (alerts.Count == 0)
            {
                banner.Height = 0;
            }
            else
            {
                var unack = alerts.Any(a => !a.Acknowledged);
                banner.BackColor = unack ? p.StatusCritical : AeroPalette.Blend(p.Background, p.Accent, 0.55f);
                banner.Text = "⚠  " + string.Join("   •   ", alerts.Select(a => a.Category.AlertTitle.ToUpperInvariant() +
                                                                                         $" ({a.MaxConfidence:P0}, since {a.Start:HH:mm:ss})")) +
                              (unack ? "" : "   — acknowledged");
                // grow with the number of wrapped lines
                var needed = TextRenderer.MeasureText(banner.Text, banner.Font, new Size(Math.Max(200, banner.Width - 20), 0),
                    TextFormatFlags.WordBreak).Height + 16;
                banner.Height = Math.Max(44, needed);
            }

            BuildChips();
            UpdateCategoryRows(live, alerts);
            UpdateHistory();
        }

        private void UpdateCategoryRows(IReadOnlyList<Detection> live, List<DetectionEvent> alerts)
        {
            var cats = DetectionCategories.All;
            if (categoryList.Controls.Count != cats.Count)
            {
                categoryList.Controls.Clear();
                foreach (var c in cats)
                    categoryList.Controls.Add(new CategoryRow(c));
            }

            var m = AiDetectionManager.Instance;
            var disabled = AiSettings.DisabledCategories;
            foreach (CategoryRow row in categoryList.Controls)
            {
                var id = row.Category.Id;
                var conf = live.Where(d => d.Category.Id == id).Select(d => d.Confidence).DefaultIfEmpty(0).Max();
                var operational = m.IsOperational(id);
                string status;
                if (m.State == AiEngineState.Disabled) status = "AI detection is disabled";
                else if (!operational) status = "Not available — no model loaded for this category";
                else if (disabled.Contains(id)) status = "Filtered out (disabled)";
                else if (m.State != AiEngineState.Running) status = "Ready • " + m.ModelsCovering(id);
                else status = conf > 0 ? "DETECTED" : "Monitoring • " + m.ModelsCovering(id);
                row.SetState(status, conf, operational && !disabled.Contains(id), alerts.Any(a => a.Category.Id == id), m.SessionCount(id));
            }
        }

        // ------------------------------------------------------------------ history

        private void UpdateHistory()
        {
            var history = AiDetectionManager.Instance.History.Take(300).ToList();
            if (history.Count != historyCount)
            {
                historyCount = history.Count;
                var selectedId = Selected()?.Id;
                historyList.BeginUpdate();
                historyList.Items.Clear();
                foreach (var ev in history)
                    historyList.Items.Add(new ListViewItem(new[] { "", "", "", "", "", "" }) { ImageIndex = Thumb(ev) });
                historyList.EndUpdate();
                shown = history;
                if (selectedId.HasValue)
                {
                    var idx = shown.FindIndex(e => e.Id == selectedId.Value);
                    if (idx >= 0) historyList.Items[idx].Selected = true;
                }
                else if (historyList.Items.Count > 0)
                {
                    historyList.Items[0].Selected = true;
                }
            }

            var p = BrandingManager.Palette;
            for (var i = 0; i < shown.Count && i < historyList.Items.Count; i++)
            {
                var ev = shown[i];
                var item = historyList.Items[i];
                Set(item, 0, ev.Start.ToString("dd MMM HH:mm:ss"));
                Set(item, 1, ev.Category.Name);
                Set(item, 2, ev.MaxConfidence.ToString("P0"));
                Set(item, 3, ev.Status.ToString());
                Set(item, 4, ev.Duration.TotalSeconds.ToString("0") + " s");
                Set(item, 5, ev.CameraSource);
                var color = ev.Status == DetectionStatus.Active ? p.StatusCritical : ev.Status == DetectionStatus.Acknowledged ? p.StatusWarn : p.Text;
                if (item.ForeColor != color) item.ForeColor = color;
                if (item.ImageIndex < 0 && ev.ScreenshotPath != null) item.ImageIndex = Thumb(ev);
            }

            ShowDetail();
        }

        private static void Set(ListViewItem item, int i, string text)
        {
            if (item.SubItems[i].Text != text) item.SubItems[i].Text = text;
        }

        private int Thumb(DetectionEvent ev)
        {
            if (thumbIndex.TryGetValue(ev.Id, out var idx)) return idx;
            if (string.IsNullOrEmpty(ev.ScreenshotPath) || !File.Exists(ev.ScreenshotPath)) return -1;
            try
            {
                using (var fs = new FileStream(ev.ScreenshotPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var img = Image.FromStream(fs))
                {
                    var t = new Bitmap(96, 54);
                    using (var g = Graphics.FromImage(t))
                    {
                        g.Clear(Color.Black);
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.DrawImage(img, VideoStreamManager.Fit(img.Size, new Rectangle(0, 0, 96, 54)));
                        using (var pen = new Pen(ev.Category.Color, 3))
                            g.DrawRectangle(pen, 1, 1, 93, 51);
                    }

                    thumbs.Images.Add(t);
                    idx = thumbs.Images.Count - 1;
                    thumbIndex[ev.Id] = idx;
                    return idx;
                }
            }
            catch (Exception)
            {
                return -1;
            }
        }

        private DetectionEvent Selected()
        {
            if (historyList.SelectedIndices.Count == 0) return null;
            var i = historyList.SelectedIndices[0];
            return i < shown.Count ? shown[i] : null;
        }

        private string detailForPath;

        private void ShowDetail()
        {
            var ev = Selected();
            ackOne.Enabled = ev != null && ev.Status == DetectionStatus.Active;
            openImage.Enabled = ev?.ScreenshotPath != null && File.Exists(ev.ScreenshotPath);
            if (ev == null)
            {
                detailText.Text = "No detections yet. Detected events of the enabled categories appear here with a captured frame.";
                if (detailImage.Image != null) { detailImage.Image.Dispose(); detailImage.Image = null; detailForPath = null; }
                return;
            }

            detailText.Text = $"Category:  {ev.Category.Name}\nConfidence:  {ev.MaxConfidence:P1} (max)\n" +
                              $"Detected:  {ev.Start:yyyy-MM-dd HH:mm:ss}\nStatus:  {ev.Status}" +
                              (ev.End != null ? $" at {ev.End:HH:mm:ss}" : "") + $"  •  {ev.Duration.TotalSeconds:0} s, {ev.FramesSeen} frames\n" +
                              $"Camera source:  {ev.CameraSource}\nModel:  {ev.ModelName}\nClasses:  {ev.Labels}";
            detailText.ForeColor = ev.Status == DetectionStatus.Active ? BrandingManager.Palette.StatusCritical : BrandingManager.Palette.Text;
            if (detailForPath != ev.ScreenshotPath)
            {
                detailForPath = ev.ScreenshotPath;
                detailImage.Image?.Dispose();
                detailImage.Image = null;
                if (ev.ScreenshotPath != null && File.Exists(ev.ScreenshotPath))
                {
                    try
                    {
                        using (var fs = new FileStream(ev.ScreenshotPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var img = Image.FromStream(fs))
                            detailImage.Image = new Bitmap(img);
                    }
                    catch (Exception)
                    {
                        // screenshot unreadable - details still shown
                    }
                }
            }
        }

        private void OpenSelectedImage()
        {
            var path = Selected()?.ScreenshotPath;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }

        /// <summary>Status row for one category (colour, icon, name, status, live confidence, session count).</summary>
        private sealed class CategoryRow : Control
        {
            public DetectionCategory Category { get; }
            private string status = "";
            private float confidence;
            private bool enabled;
            private bool alert;
            private int count;

            public CategoryRow(DetectionCategory c)
            {
                Category = c;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                Size = new Size(352, 62);
                Margin = new Padding(0, 0, 0, 6);
            }

            public void SetState(string s, float conf, bool en, bool al, int cnt)
            {
                if (s == status && Math.Abs(conf - confidence) < 0.005f && en == enabled && al == alert && cnt == count) return;
                status = s;
                confidence = conf;
                enabled = en;
                alert = al;
                count = cnt;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                var p = BrandingManager.Palette;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                g.Clear(Parent?.BackColor ?? p.Surface);
                var c = enabled ? Category.Color : Color.FromArgb(0x55, 0x55, 0x55);
                using (var path = AeroDraw.RoundedRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 8))
                {
                    using (var b = new SolidBrush(alert ? AeroPalette.Blend(p.SurfaceAlt, Category.Color, 0.25f) : p.SurfaceAlt))
                        g.FillPath(b, path);
                    using (var pen = new Pen(alert ? Category.Color : Color.FromArgb(0x33, 0x33, 0x33), alert ? 2f : 1f))
                        g.DrawPath(pen, path);
                }

                using (var bar = new SolidBrush(c))
                    g.FillRectangle(bar, 1, 8, 4, Height - 16);
                using (var iconFont = AeroIcons.CreateFont(18f))
                    if (iconFont != null)
                        using (var ib = new SolidBrush(c))
                            g.DrawString(Category.Glyph, iconFont, ib, 12, 18);
                using (var nf = AeroDraw.UiFont(10f, FontStyle.Bold))
                using (var nb = new SolidBrush(enabled ? p.Text : p.Muted))
                    g.DrawString(Category.Name, nf, nb, 42, 7);
                using (var cf = AeroDraw.UiFont(8f))
                using (var cb = new SolidBrush(p.Muted))
                {
                    var countText = count > 0 ? count + (count == 1 ? " event" : " events") : "";
                    var sz = g.MeasureString(countText, cf);
                    g.DrawString(countText, cf, cb, Width - sz.Width - 10, 9);
                }

                using (var sf = AeroDraw.UiFont(8.5f))
                using (var sb = new SolidBrush(alert ? Category.Color : status == "DETECTED" ? Category.Color : p.Muted))
                    g.DrawString(status, sf, sb, new RectangleF(42, 27, Width - 54, 16),
                        new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap });

                // live confidence meter
                var track = new RectangleF(42, Height - 14, Width - 100, 6);
                using (var tb = new SolidBrush(Color.FromArgb(0x30, 0x30, 0x30)))
                    g.FillRectangle(tb, track);
                if (confidence > 0)
                    using (var fb = new SolidBrush(c))
                        g.FillRectangle(fb, track.X, track.Y, track.Width * confidence, track.Height);
                using (var pf = AeroDraw.UiFont(8f, FontStyle.Bold))
                using (var pb = new SolidBrush(confidence > 0 ? p.Text : p.Muted))
                    g.DrawString(confidence > 0 ? confidence.ToString("P0") : "—", pf, pb, Width - 52, Height - 19);
            }
        }
    }
}
