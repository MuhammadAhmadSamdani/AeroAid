using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using MissionPlanner.AeroAid.Branding;
using MissionPlanner.AeroAid.Input;
using MissionPlanner.AeroAid.UI;

namespace MissionPlanner.AeroAid.Views
{
    /// <summary>"Controls &amp; Keyboard Shortcuts": reference and editor for all keyboard controls.</summary>
    public class ShortcutsView : AeroScreen
    {
        private readonly FlowLayoutPanel columns;
        private readonly Label availability = AeroDraw.Body("", 10f);
        private readonly Timer timer = new Timer { Interval = 500 };

        public ShortcutsView() : base("Controls & Keyboard Shortcuts", "Click a key to change it. Keys only act on the operations screens and never while typing.")
        {
            var p = BrandingManager.Palette;
            var restore = AeroDraw.Button("Restore defaults", AeroIcons.Refresh, AeroButtonStyle.Secondary, 180);
            restore.Click += (s, e) =>
            {
                if (CustomMessageBox.Show("Restore all default keyboard shortcuts?", BrandingManager.AppName, MessageBoxButtons.YesNo) == (int)DialogResult.Yes)
                {
                    ShortcutMap.RestoreDefaults();
                    Build();
                }
            };
            HeaderTools.Controls.Add(restore);

            availability.Dock = DockStyle.Top;
            availability.Padding = new Padding(4, 0, 0, 10);
            columns = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, BackColor = p.Background };
            var notes = new AeroCard { Title = "Safety behaviour", Glyph = AeroIcons.About, Dock = DockStyle.Top, Height = 210 };
            var nl = AeroDraw.Caption(
                "• Movement keys work while held (key-down / key-up). Releasing a key stops that movement; Space stops everything.\n" +
                "• Drone movement requires ArduCopter in GUIDED, armed and flying, with a live link — otherwise keys are refused.\n" +
                "• If the window loses focus (Alt-Tab, a dialog opens) all held movement stops immediately.\n" +
                "• Keys are ignored while typing in text fields and on non-operations screens. Ctrl/Alt shortcuts are left to Mission Planner.\n" +
                "• RTL, Land and RC-dependent modes (Stabilize, AltHold, Loiter) ask for confirmation while armed.\n" +
                "• Camera keys control the camera only and never move the drone. Drone keys can be disabled in Settings › Flight controls.", 9.5f);
            nl.MaximumSize = new Size(1100, 0);
            nl.Dock = DockStyle.Fill;
            notes.Controls.Add(nl);

            Body.Controls.Add(notes);
            Body.Controls.Add(columns);
            Body.Controls.Add(availability);
            timer.Tick += (s, e) => UpdateAvailability();
            Build();
        }

        protected override void OnScreenActivated()
        {
            ShortcutMap.Load();
            Build();
            timer.Start();
        }

        protected override void OnScreenDeactivated() => timer.Stop();

        private void UpdateAvailability()
        {
            availability.Text = "Keys are intentionally inactive on this screen; they work on Dashboard, Flight Control, Live Camera, " +
                                "Camera Control and AI Detection." +
                                (AeroSettings.KeyboardFlightEnabled ? "" : "  Drone keys are currently DISABLED in Settings › Flight controls.");
            availability.ForeColor = AeroSettings.KeyboardFlightEnabled ? BrandingManager.Palette.Muted : BrandingManager.Palette.StatusWarn;
        }

        private void Build()
        {
            columns.SuspendLayout();
            foreach (Control c in columns.Controls.Cast<Control>().ToList()) c.Dispose();
            columns.Controls.Clear();
            columns.Controls.Add(Category("Drone movement", AeroIcons.FlightControl, ShortcutCategory.DroneMovement));
            columns.Controls.Add(Category("Flight modes & actions", AeroIcons.Takeoff, ShortcutCategory.FlightModes));
            columns.Controls.Add(Category("Camera (PTZ)", AeroIcons.CameraControl, ShortcutCategory.Camera));
            columns.ResumeLayout();
            UpdateAvailability();
        }

        private Control Category(string title, string glyph, ShortcutCategory cat)
        {
            var p = BrandingManager.Palette;
            var actions = ShortcutMap.Definitions.Where(d => d.Value.cat == cat).ToList();
            var card = new AeroCard { Title = title, Glyph = glyph, Width = 370, Height = 60 + actions.Count * 44 };
            var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, BackColor = p.Surface };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
            foreach (var d in actions)
            {
                var action = d.Key;
                var key = ShortcutMap.KeyFor(action);
                var keyBtn = new AeroButton
                {
                    Text = ShortcutMap.KeyText(key),
                    ButtonStyle = key == d.Value.key ? AeroButtonStyle.Secondary : AeroButtonStyle.Primary,
                    Size = new Size(80, 34),
                    Margin = new Padding(0, 3, 6, 3),
                    Font = AeroDraw.UiFont(10f, FontStyle.Bold)
                };
                keyBtn.Click += (s, e) => Rebind(action);
                table.Controls.Add(keyBtn);
                var label = AeroDraw.Body(d.Value.label, 10f);
                label.Anchor = AnchorStyles.Left;
                table.Controls.Add(label);
                var def = AeroDraw.Caption("default " + ShortcutMap.KeyText(d.Value.key), 8f);
                def.Anchor = AnchorStyles.Left;
                table.Controls.Add(def);
            }

            card.Controls.Add(table);
            return card;
        }

        private void Rebind(ShortcutAction action)
        {
            using (var dlg = new KeyCaptureDialog(ShortcutMap.Definitions[action].label))
            {
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
                var error = ShortcutMap.TryAssign(action, dlg.Captured);
                if (error != null)
                {
                    CustomMessageBox.Show(error, BrandingManager.AppName);
                    return;
                }

                ShortcutMap.Save();
                Build();
            }
        }

        private sealed class KeyCaptureDialog : Form
        {
            public Keys Captured { get; private set; }

            public KeyCaptureDialog(string action)
            {
                var p = BrandingManager.Palette;
                Text = BrandingManager.AppName + " - assign key";
                FormBorderStyle = FormBorderStyle.FixedDialog;
                StartPosition = FormStartPosition.CenterParent;
                MinimizeBox = MaximizeBox = false;
                ShowInTaskbar = false;
                Size = new Size(420, 170);
                BackColor = p.Surface;
                KeyPreview = true;
                var l = new Label
                {
                    Text = "Press the new key for \"" + action + "\"\n(Esc to cancel)",
                    ForeColor = p.Text,
                    Font = AeroDraw.UiFont(11f),
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleCenter,
                    UseMnemonic = false
                };
                Controls.Add(l);
            }

            protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
            {
                var key = keyData & Keys.KeyCode;
                if (key == Keys.Escape)
                {
                    DialogResult = DialogResult.Cancel;
                    return true;
                }

                if (key == Keys.ShiftKey || key == Keys.ControlKey || key == Keys.Menu)
                    return true;
                Captured = key;
                DialogResult = DialogResult.OK;
                return true;
            }
        }
    }
}
