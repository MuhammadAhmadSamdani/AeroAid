using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using MissionPlanner.AeroAid.Branding;
using MissionPlanner.AeroAid.UI;

namespace MissionPlanner.AeroAid.Views
{
    /// <summary>About screen: branding, versions and the required open-source attribution/licence notice.</summary>
    public class AboutView : AeroScreen
    {
        public AboutView() : base("About " + BrandingManager.AppName, BrandingManager.Tagline)
        {
            var p = BrandingManager.Palette;

            var card = new AeroCard { Dock = DockStyle.Top, Height = 230 };
            var logo = new PictureBox
            {
                Image = BrandingManager.GetLogo(160),
                SizeMode = PictureBoxSizeMode.CenterImage,
                Size = new Size(170, 170),
                Location = new Point(20, 24),
                BackColor = Color.Transparent
            };
            card.Controls.Add(logo);
            var name = AeroDraw.Title(BrandingManager.AppName, 26f);
            name.ForeColor = p.Accent;
            name.Location = new Point(210, 34);
            card.Controls.Add(name);
            var tag = AeroDraw.Body(BrandingManager.Tagline, 12f);
            tag.Location = new Point(214, 84);
            card.Controls.Add(tag);
            var ver = AeroDraw.Caption("Version " + BrandingManager.Version + "   •   Based on Mission Planner " +
                                       BrandingManager.BaseVersion, 10f);
            ver.Location = new Point(214, 116);
            card.Controls.Add(ver);
            var by = AeroDraw.Caption(BrandingManager.Company, 10f);
            by.Location = new Point(214, 140);
            card.Controls.Add(by);

            var license = new AeroCard { Title = "Open-source attribution & licence", Glyph = AeroIcons.About, Dock = DockStyle.Top, Height = 300 };
            var text = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = p.Text,
                Font = AeroDraw.UiFont(9.5f),
                Text =
                    BrandingManager.AppName + " is a customised build of Mission Planner, the ArduPilot ground control station " +
                    "created by Michael Oborne and the ArduPilot community.\n\n" +
                    "Mission Planner is free software licensed under the GNU General Public License v3 (see COPYING.txt " +
                    "distributed with this application). As a derivative work, " + BrandingManager.AppName +
                    " is distributed under the same licence; its complete source code must be made available to " +
                    "anyone who receives the binaries.\n\n" +
                    "Vehicle communication uses the MAVLink protocol and is compatible with ArduPilot firmware. " +
                    "Third-party components (GMap.NET, GStreamer, log4net, Newtonsoft.Json, ONNX Runtime and others) " +
                    "remain under their respective licences.\n\n" +
                    "ArduPilot and Mission Planner are trademarks/projects of their respective owners; " +
                    BrandingManager.AppName + " is not endorsed by them."
            };
            license.Controls.Add(text);

            var links = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 54, BackColor = p.Background, Padding = new Padding(4, 10, 0, 0) };
            links.Controls.Add(LinkButton("Licence (GPLv3)", AeroIcons.About, () => OpenFile("COPYING.txt")));
            links.Controls.Add(LinkButton("ArduPilot.org", AeroIcons.Link, () => OpenUrl("https://ardupilot.org")));
            links.Controls.Add(LinkButton("Mission Planner source", AeroIcons.Link, () => OpenUrl("https://github.com/ArduPilot/MissionPlanner")));

            Body.Controls.Add(links);
            Body.Controls.Add(Spacer());
            Body.Controls.Add(license);
            Body.Controls.Add(Spacer());
            Body.Controls.Add(card);
        }

        private static Control Spacer() => new Panel { Dock = DockStyle.Top, Height = 12, BackColor = Color.Transparent };

        private static AeroButton LinkButton(string text, string glyph, Action a)
        {
            var b = AeroDraw.Button(text, glyph, AeroButtonStyle.Secondary, 200);
            b.Click += (s, e) => a();
            return b;
        }

        private static void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show("Could not open " + url + "\n" + ex.Message, BrandingManager.AppName);
            }
        }

        private static void OpenFile(string name)
        {
            var path = System.IO.Path.Combine(Utilities.Settings.GetRunningDirectory(), name);
            if (!System.IO.File.Exists(path))
            {
                CustomMessageBox.Show(name + " was not found next to the application.\nThe GPLv3 text is available at https://www.gnu.org/licenses/gpl-3.0.html",
                    BrandingManager.AppName);
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show("Could not open " + path + "\n" + ex.Message, BrandingManager.AppName);
            }
        }
    }
}
