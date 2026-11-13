using System;
using System.IO;
using System.Windows.Forms;
using MissionPlanner.AeroAid.UI;
using MissionPlanner.AeroAid.Vision;

namespace MissionPlanner.AeroAid.Views
{
    public partial class SettingsView
    {
        private AeroCard FireSection()
        {
            var card = NewSection("Fire detection", AeroIcons.Fire, out var grid, 470);
            var enabled = AeroDraw.Check("Enable fire / smoke detection on the live video", true);
            var model = AeroDraw.TextBox();
            model.Width = 380;
            var browse = AeroDraw.Button("Browse...", AeroIcons.Folder, AeroButtonStyle.Secondary, 120, 32);
            var modelRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = System.Drawing.Color.Transparent, Margin = new Padding(0) };
            modelRow.Controls.Add(model);
            modelRow.Controls.Add(browse);
            var threshold = AeroDraw.Number(50, 10, 95, 0, 5);
            var fps = AeroDraw.Number(3, 0.5m, 15, 1, 0.5m);
            var confirm = AeroDraw.Number(2, 1, 10, 0, 1);
            var clear = AeroDraw.Number(10, 2, 120, 0, 1);
            var shots = AeroDraw.Check("Save a screenshot for every alert", true);
            var sound = AeroDraw.Check("Play an alert sound", true);

            AeroDraw.AddRow(grid, "Detection", enabled);
            AeroDraw.AddRow(grid, "Model file (.onnx)", modelRow);
            AeroDraw.AddRow(grid, "Confidence threshold", threshold, "%");
            AeroDraw.AddRow(grid, "Analysis rate", fps, "frames per second (CPU load)");
            AeroDraw.AddRow(grid, "Confirm frames", confirm, "consecutive frames before alerting");
            AeroDraw.AddRow(grid, "Alert clear time", clear, "s without detection ends an alert");
            AeroDraw.AddRow(grid, "Screenshots", shots);
            AeroDraw.AddRow(grid, "Sound", sound);
            AeroDraw.AddRow(grid, "", AeroDraw.Caption("Default model location: " + FireSettings.DefaultModelPath, 8.5f));

            browse.Click += (s, e) =>
            {
                using (var dlg = new OpenFileDialog { Filter = "ONNX model (*.onnx)|*.onnx" })
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                        model.Text = dlg.FileName;
                }
            };

            Bind(() =>
            {
                enabled.Checked = FireSettings.Enabled;
                model.Text = FireSettings.ModelPath;
                threshold.Value = (decimal)Math.Round(FireSettings.ConfidenceThreshold * 100);
                fps.Value = (decimal)FireSettings.AnalysisFps;
                confirm.Value = FireSettings.ConfirmFrames;
                clear.Value = FireSettings.ClearSeconds;
                shots.Checked = FireSettings.SaveScreenshots;
                sound.Checked = FireSettings.PlaySound;
            }, () =>
            {
                var changed = enabled.Checked != FireSettings.Enabled || model.Text.Trim() != FireSettings.ModelPath;
                FireSettings.Enabled = enabled.Checked;
                FireSettings.ModelPath = model.Text.Trim() == FireSettings.DefaultModelPath ? "" : model.Text.Trim();
                FireSettings.ConfidenceThreshold = (double)threshold.Value / 100.0;
                FireSettings.AnalysisFps = (double)fps.Value;
                FireSettings.ConfirmFrames = (int)confirm.Value;
                FireSettings.ClearSeconds = (int)clear.Value;
                FireSettings.SaveScreenshots = shots.Checked;
                FireSettings.PlaySound = sound.Checked;
                if (changed)
                    FireDetectionManager.Instance.ApplySettings();
            });
            return card;
        }
    }
}
