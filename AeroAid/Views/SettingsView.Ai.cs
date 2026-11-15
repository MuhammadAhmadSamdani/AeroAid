using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using MissionPlanner.AeroAid.Branding;
using MissionPlanner.AeroAid.UI;
using MissionPlanner.AeroAid.Vision;

namespace MissionPlanner.AeroAid.Views
{
    public partial class SettingsView
    {
        private AeroCard AiSection()
        {
            var p = BrandingManager.Palette;
            var card = NewSection("AI detection", AeroIcons.AiDetection, out var grid, 760);
            var enabled = AeroDraw.Check("Enable AI detection on the live video", true);
            var models = new ListView
            {
                View = View.Details,
                CheckBoxes = true,
                FullRowSelect = true,
                Width = 620,
                Height = 150,
                BackColor = p.SurfaceAlt,
                ForeColor = p.Text,
                BorderStyle = BorderStyle.FixedSingle,
                Font = AeroDraw.UiFont(9f),
                HeaderStyle = ColumnHeaderStyle.Nonclickable
            };
            models.Columns.Add("Model file (enabled)", 220);
            models.Columns.Add("Status / categories", 380);
            var add = AeroDraw.Button("Add model...", AeroIcons.Folder, AeroButtonStyle.Secondary, 150, 32);
            var remove = AeroDraw.Button("Remove", AeroIcons.Stop, AeroButtonStyle.Secondary, 120, 32);
            var openFolder = AeroDraw.Button("Models folder", AeroIcons.Folder, AeroButtonStyle.Ghost, 160, 32);
            // fixed layout: an auto-sized FlowLayoutPanel inside the form grid overlapped these buttons
            var modelButtons = new Panel { Size = new Size(620, 40), BackColor = Color.Transparent, Margin = new Padding(0, 6, 0, 4) };
            add.Location = new Point(0, 0);
            remove.Location = new Point(add.Right + 8, 0);
            openFolder.Location = new Point(remove.Right + 8, 0);
            add.Margin = remove.Margin = openFolder.Margin = new Padding(0);
            modelButtons.Controls.AddRange(new Control[] { add, remove, openFolder });
            var modelBox = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 8) };
            modelBox.Controls.Add(models);
            modelBox.Controls.Add(modelButtons);
            var mapping = AeroDraw.Caption(
                "Classes are mapped to categories by name (fire/flame, smoke, injured/casualty/victim, flood, damaged/collapsed/rubble, emergency/accident).\n" +
                "Explicit mapping: put \"<model>.labels.txt\" next to the model with one class per line, e.g. \"person_lying=injured_person\".\n" +
                "Category ids: " + string.Join(", ", DetectionCategories.All.Select(c => c.Id)) +
                ". Extra categories: categories.json in the models folder.", 8.5f);
            mapping.MaximumSize = new Size(620, 0);
            modelBox.Controls.Add(mapping);

            var threshold = AeroDraw.Number(50, 10, 95, 0, 5);
            var fps = AeroDraw.Number(3, 0.5m, 15, 1, 0.5m);
            var confirm = AeroDraw.Number(2, 1, 10, 0, 1);
            var clear = AeroDraw.Number(10, 2, 120, 0, 1);
            var shots = AeroDraw.Check("Save a captured frame for every alert", true);
            var sound = AeroDraw.Check("Play an alert sound", true);

            AeroDraw.AddRow(grid, "AI detection", enabled);
            AeroDraw.AddRow(grid, "Models", modelBox);
            AeroDraw.AddRow(grid, "Confidence threshold", threshold, "% (global default)");
            AeroDraw.AddRow(grid, "Analysis rate", fps, "frames per second (CPU load grows with each model)");
            AeroDraw.AddRow(grid, "Confirm frames", confirm, "consecutive frames before alerting");
            AeroDraw.AddRow(grid, "Alert clear time", clear, "s without detection ends an alert");
            AeroDraw.AddRow(grid, "Evidence", shots);
            AeroDraw.AddRow(grid, "Sound", sound);

            void FillModels(List<ModelConfig> list)
            {
                models.Items.Clear();
                var loaded = AiDetectionManager.Instance.Models;
                foreach (var m in list)
                {
                    var status = loaded.FirstOrDefault(l => string.Equals(l.Config.Path, m.Path, StringComparison.OrdinalIgnoreCase))?.Status
                                 ?? (File.Exists(m.Path) ? "Not loaded yet (save to apply)" : "File not found");
                    models.Items.Add(new ListViewItem(new[] { Path.GetFileName(m.Path), status }) { Checked = m.Enabled, Tag = m.Path, ToolTipText = m.Path });
                }
            }

            List<ModelConfig> ReadModels() =>
                models.Items.Cast<ListViewItem>().Select(i => new ModelConfig { Path = (string)i.Tag, Enabled = i.Checked }).ToList();

            add.Click += (s, e) =>
            {
                using (var dlg = new OpenFileDialog { Filter = "ONNX model (*.onnx)|*.onnx", Multiselect = true })
                {
                    if (Directory.Exists(AiSettings.ModelsDirectory)) dlg.InitialDirectory = AiSettings.ModelsDirectory;
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    var list = ReadModels();
                    foreach (var f in dlg.FileNames)
                        if (!list.Any(m => string.Equals(m.Path, f, StringComparison.OrdinalIgnoreCase)))
                            list.Add(new ModelConfig { Path = f });
                    FillModels(list);
                }
            };
            remove.Click += (s, e) =>
            {
                foreach (ListViewItem i in models.SelectedItems.Cast<ListViewItem>().ToList())
                    models.Items.Remove(i);
            };
            openFolder.Click += (s, e) =>
            {
                Directory.CreateDirectory(AiSettings.ModelsDirectory);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AiSettings.ModelsDirectory) { UseShellExecute = true });
            };

            Bind(() =>
            {
                enabled.Checked = AiSettings.Enabled;
                FillModels(AiSettings.Models);
                threshold.Value = (decimal)Math.Round(AiSettings.ConfidenceThreshold * 100);
                fps.Value = (decimal)AiSettings.AnalysisFps;
                confirm.Value = AiSettings.ConfirmFrames;
                clear.Value = AiSettings.ClearSeconds;
                shots.Checked = AiSettings.SaveScreenshots;
                sound.Checked = AiSettings.PlaySound;
            }, () =>
            {
                AiSettings.Enabled = enabled.Checked;
                AiSettings.Models = ReadModels();
                AiSettings.ConfidenceThreshold = (double)threshold.Value / 100.0;
                AiSettings.AnalysisFps = (double)fps.Value;
                AiSettings.ConfirmFrames = (int)confirm.Value;
                AiSettings.ClearSeconds = (int)clear.Value;
                AiSettings.SaveScreenshots = shots.Checked;
                AiSettings.PlaySound = sound.Checked;
                // reload models / categories so changes take effect immediately
                AiDetectionManager.Instance.ApplySettings();
            });
            return card;
        }
    }
}
