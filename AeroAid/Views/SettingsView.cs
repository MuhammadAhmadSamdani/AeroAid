using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using MissionPlanner.AeroAid.Branding;
using MissionPlanner.AeroAid.UI;
using MissionPlanner.Utilities;

namespace MissionPlanner.AeroAid.Views
{
    /// <summary>
    /// Persistent AeroAid preferences. Each section is a card; values are written to the Mission Planner
    /// settings file (aeroaid.* keys) when "Save settings" is pressed.
    /// </summary>
    public class SettingsView : AeroScreen
    {
        private readonly List<Action> savers = new List<Action>();
        private readonly List<Action> loaders = new List<Action>();
        private readonly FlowLayoutPanel nav;
        private readonly Panel sections;

        public SettingsView() : base("Settings", "Appearance, flight limits, camera, voice and fire detection preferences")
        {
            var p = BrandingManager.Palette;
            var save = AeroDraw.Button("Save settings", AeroIcons.Save, AeroButtonStyle.Primary, 160);
            save.Click += (s, e) => SaveAll();
            HeaderTools.Controls.Add(save);
            var revert = AeroDraw.Button("Revert", AeroIcons.Refresh, AeroButtonStyle.Secondary, 110);
            revert.Click += (s, e) => LoadAll();
            HeaderTools.Controls.Add(revert);

            nav = new FlowLayoutPanel { Dock = DockStyle.Left, Width = 200, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = p.Background, Padding = new Padding(0, 6, 10, 0) };
            sections = new Panel { Dock = DockStyle.Fill, BackColor = p.Background, AutoScroll = true };
            Body.AutoScroll = false;
            Body.Controls.Add(sections);
            Body.Controls.Add(nav);

            // order on screen = order added (cards dock top, so add in reverse)
            var cards = new List<(string name, string glyph, AeroCard card)>();
            cards.Add(("Appearance", AeroIcons.Settings, AppearanceSection()));
            cards.Add(("Flight controls", AeroIcons.FlightControl, FlightSection()));
            foreach (var extra in ExtraSections)
            {
                try
                {
                    var r = extra(this);
                    if (r.card != null)
                        cards.Add(r);
                }
                catch (Exception ex)
                {
                    log4net.LogManager.GetLogger(typeof(SettingsView)).Error("Settings section failed", ex);
                }
            }

            foreach (var c in Enumerable.Reverse(cards))
            {
                c.card.Dock = DockStyle.Top;
                sections.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 12, BackColor = p.Background });
                sections.Controls.Add(c.card);
            }

            foreach (var c in cards)
            {
                var card = c.card;
                var b = new AeroButton
                {
                    Text = c.name, Glyph = c.glyph, ButtonStyle = AeroButtonStyle.Ghost, TextAlignment = ContentAlignment.MiddleLeft,
                    Size = new Size(186, 36), Margin = new Padding(0, 1, 0, 1), Padding = new Padding(12, 4, 6, 4), GlyphSize = 14f
                };
                b.Click += (s, e) =>
                {
                    sections.ScrollControlIntoView(card);
                    foreach (var other in nav.Controls.OfType<AeroButton>())
                        other.IsActive = other == b;
                };
                nav.Controls.Add(b);
            }

            LoadAll();
        }

        /// <summary>Sections contributed by other modules: (settings view) => (name, glyph, card).</summary>
        internal static List<Func<SettingsView, (string name, string glyph, AeroCard card)>> ExtraSections =
            new List<Func<SettingsView, (string, string, AeroCard)>>();

        /// <summary>Registers load/save callbacks for a section's controls.</summary>
        internal void Bind(Action load, Action save)
        {
            loaders.Add(load);
            savers.Add(save);
        }

        protected override void OnScreenActivated() => LoadAll();

        private void LoadAll()
        {
            foreach (var l in loaders)
            {
                try
                {
                    l();
                }
                catch (Exception ex)
                {
                    log4net.LogManager.GetLogger(typeof(SettingsView)).Error("Settings load failed", ex);
                }
            }
        }

        private void SaveAll()
        {
            try
            {
                foreach (var s in savers)
                    s();
                AeroSettings.Save();
                CustomMessageBox.Show("Settings saved.", BrandingManager.AppName);
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show("Saving settings failed:\n" + ex.Message, BrandingManager.AppName);
            }
        }

        internal static AeroCard NewSection(string title, string glyph, out TableLayoutPanel grid, int height)
        {
            var card = new AeroCard { Title = title, Glyph = glyph, Height = height };
            grid = AeroDraw.FormGrid();
            card.Controls.Add(grid);
            return card;
        }

        private AeroCard AppearanceSection()
        {
            var card = NewSection("Appearance", AeroIcons.Settings, out var grid, 230);
            var theme = AeroDraw.Combo();
            var anim = AeroDraw.Check("Enable UI animations (hover / press effects)", true);
            var sidebar = AeroDraw.Check("Use AeroAid sidebar navigation (restart required)", true);
            var start = AeroDraw.Combo();
            var startScreens = new (string name, string label)[]
            {
                (AeroShell.ScreenDashboard, "Dashboard"), (AeroShell.ScreenFlightControl, "Flight Control"),
                (AeroShell.ScreenTelemetry, "Telemetry"), (AeroShell.ScreenLiveCamera, "Live Camera"),
                (AeroShell.ScreenFireDetection, "Fire Detection"), ("FlightData", "Flight Data (HUD)"),
                ("FlightPlanner", "Mission Planner")
            };
            foreach (var s in startScreens)
                start.Items.Add(s.label);
            AeroDraw.AddRow(grid, "Theme", theme, "Mustard/Black = AeroAid.mpsystheme");
            AeroDraw.AddRow(grid, "Animations", anim);
            AeroDraw.AddRow(grid, "Navigation", sidebar);
            AeroDraw.AddRow(grid, "Start screen", start);
            card.Height = 280;

            Bind(() =>
            {
                ThemeManager.GetThemesList();
                theme.Items.Clear();
                foreach (var n in ThemeManager.ThemeNames ?? new List<string>())
                    theme.Items.Add(n);
                var current = Settings.Instance["theme"] ?? AeroShell.ThemeFile;
                if (!theme.Items.Contains(current))
                    theme.Items.Add(current);
                theme.SelectedItem = current;
                anim.Checked = AeroSettings.Animations;
                sidebar.Checked = AeroSettings.UseSidebar;
                var idx = Array.FindIndex(startScreens, s => s.name == AeroSettings.StartScreen);
                start.SelectedIndex = idx < 0 ? 0 : idx;
            }, () =>
            {
                AeroSettings.Animations = anim.Checked;
                AeroSettings.UseSidebar = sidebar.Checked;
                if (start.SelectedIndex >= 0)
                    AeroSettings.StartScreen = startScreens[start.SelectedIndex].name;
                var selected = theme.SelectedItem as string;
                if (!string.IsNullOrEmpty(selected) && selected != Settings.Instance["theme"])
                {
                    ThemeManager.LoadTheme(selected);
                    ThemeManager.ApplyThemeTo(MainV2.instance);
                }
            });
            return card;
        }

        private AeroCard FlightSection()
        {
            var card = NewSection("Flight controls", AeroIcons.FlightControl, out var grid, 520);
            var hs = AeroDraw.Number(2, 0.1m, (decimal)AeroSettings.HardMaxHorizontalSpeed, 1, 0.1m);
            var vs = AeroDraw.Number(1, 0.1m, (decimal)AeroSettings.HardMaxVerticalSpeed, 1, 0.1m);
            var yaw = AeroDraw.Number(30, 5, (decimal)AeroSettings.HardMaxYawRate, 0, 5);
            var maxAlt = AeroDraw.Number(30, 2, (decimal)AeroSettings.HardMaxAltitude, 0, 1);
            var minAlt = AeroDraw.Number(2, 0.5m, 20, 1, 0.5m);
            var toAlt = AeroDraw.Number(5, 1, 50, 1, 1);
            var sats = AeroDraw.Number(6, 4, 20, 0, 1);
            var batt = AeroDraw.Number(30, 10, 90, 0, 5);
            var confirm = AeroDraw.Check("Confirm flight-mode changes while armed (GUIDED/BRAKE excepted; RC-dependent modes always confirm)", true);
            var keyboard = AeroDraw.Check("Enable keyboard flight control (see Controls & Keyboard Shortcuts)", true);

            AeroDraw.AddRow(grid, "Max horizontal speed", hs, $"m/s (hard cap {AeroSettings.HardMaxHorizontalSpeed})");
            AeroDraw.AddRow(grid, "Max vertical speed", vs, $"m/s (hard cap {AeroSettings.HardMaxVerticalSpeed})");
            AeroDraw.AddRow(grid, "Max yaw rate", yaw, $"°/s (hard cap {AeroSettings.HardMaxYawRate})");
            AeroDraw.AddRow(grid, "Max altitude (climb stop)", maxAlt, "m above home");
            AeroDraw.AddRow(grid, "Min altitude (descent stop)", minAlt, "m - use LAND below this");
            AeroDraw.AddRow(grid, "Default takeoff altitude", toAlt, "m");
            AeroDraw.AddRow(grid, "Min satellites (voice takeoff)", sats);
            AeroDraw.AddRow(grid, "Min battery % (voice takeoff)", batt);
            AeroDraw.AddRow(grid, "Confirmations", confirm);
            AeroDraw.AddRow(grid, "Keyboard", keyboard);

            Bind(() =>
            {
                hs.Value = (decimal)AeroSettings.MaxHorizontalSpeed;
                vs.Value = (decimal)AeroSettings.MaxVerticalSpeed;
                yaw.Value = (decimal)AeroSettings.MaxYawRate;
                maxAlt.Value = (decimal)AeroSettings.MaxAltitude;
                minAlt.Value = (decimal)AeroSettings.MinAltitude;
                toAlt.Value = (decimal)AeroSettings.TakeoffAltitude;
                sats.Value = AeroSettings.MinSatellites;
                batt.Value = AeroSettings.MinBatteryPercent;
                confirm.Checked = AeroSettings.ConfirmModeChangeWhenArmed;
                keyboard.Checked = AeroSettings.KeyboardFlightEnabled;
            }, () =>
            {
                if (minAlt.Value >= maxAlt.Value)
                    throw new InvalidOperationException("Min altitude must be lower than max altitude.");
                AeroSettings.MaxHorizontalSpeed = (double)hs.Value;
                AeroSettings.MaxVerticalSpeed = (double)vs.Value;
                AeroSettings.MaxYawRate = (double)yaw.Value;
                AeroSettings.MaxAltitude = (double)maxAlt.Value;
                AeroSettings.MinAltitude = (double)minAlt.Value;
                AeroSettings.TakeoffAltitude = (double)toAlt.Value;
                AeroSettings.MinSatellites = (int)sats.Value;
                AeroSettings.MinBatteryPercent = (int)batt.Value;
                AeroSettings.ConfirmModeChangeWhenArmed = confirm.Checked;
                AeroSettings.KeyboardFlightEnabled = keyboard.Checked;
            });
            return card;
        }
    }
}
