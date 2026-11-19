using System;
using System.Drawing;
using System.Windows.Forms;
using MissionPlanner.AeroAid.Branding;
using MissionPlanner.AeroAid.Camera;
using MissionPlanner.AeroAid.UI;

namespace MissionPlanner.AeroAid.Views
{
    public partial class SettingsView
    {
        private AeroCard CameraSection()
        {
            var card = NewSection("Camera (IMOU / RTSP / ONVIF)", AeroIcons.Camera, out var grid, 760);
            var model = AeroDraw.TextBox();
            var host = AeroDraw.TextBox();
            var rtspPort = AeroDraw.Number(554, 1, 65535, 0, 1);
            var onvifPort = AeroDraw.Number(80, 1, 65535, 0, 1);
            var user = AeroDraw.TextBox();
            var password = AeroDraw.TextBox("", true);
            var path = AeroDraw.TextBox();
            path.Width = 360;
            var urlOverride = AeroDraw.TextBox();
            urlOverride.Width = 360;
            var sub = AeroDraw.Check("Use sub stream (lower resolution, lower latency)", false);
            var latency = AeroDraw.Number(200, 0, 5000, 0, 50);
            var tcp = AeroDraw.Check("RTSP over TCP (more reliable on Wi-Fi)", true);
            var pipeline = AeroDraw.TextBox();
            pipeline.Width = 360;
            var status = AeroDraw.Caption("", 9f);
            status.MaximumSize = new Size(560, 0);

            AeroDraw.AddRow(grid, "Camera model", model);
            AeroDraw.AddRow(grid, "Camera IP address", host, "e.g. 192.168.1.64 (see the Imou Life app / router)");
            AeroDraw.AddRow(grid, "RTSP port", rtspPort, "default 554");
            AeroDraw.AddRow(grid, "ONVIF port", onvifPort, "default 80");
            AeroDraw.AddRow(grid, "Username", user, "usually admin");
            AeroDraw.AddRow(grid, "Password", password, "RTSP/ONVIF password or label safety code — stored encrypted");
            AeroDraw.AddRow(grid, "RTSP path", path, "{subtype} = 0 main / 1 sub");
            AeroDraw.AddRow(grid, "RTSP URL (override)", urlOverride, "optional, without password");
            AeroDraw.AddRow(grid, "Stream", sub);
            AeroDraw.AddRow(grid, "Latency buffer", latency, "ms");
            AeroDraw.AddRow(grid, "Transport", tcp);
            AeroDraw.AddRow(grid, "Custom pipeline", pipeline, "advanced, needs GStreamer; must end with appsink name=outsink");

            var test = AeroDraw.Button("Save & test connection", AeroIcons.Plug, AeroButtonStyle.Primary, 230);
            var clearPw = AeroDraw.Button("Forget password", AeroIcons.Lock, AeroButtonStyle.Secondary, 170);
            var buttons = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 8, 0, 0) };
            buttons.Controls.Add(test);
            buttons.Controls.Add(clearPw);
            AeroDraw.AddRow(grid, "", buttons);
            AeroDraw.AddRow(grid, "Connection status", status);

            void Save()
            {
                CameraSettings.Model = model.Text;
                CameraSettings.Host = host.Text;
                CameraSettings.RtspPort = (int)rtspPort.Value;
                CameraSettings.OnvifPort = (int)onvifPort.Value;
                CameraSettings.Username = user.Text;
                if (password.Text.Length > 0)
                    CameraSettings.Password = password.Text; // empty field = keep stored password
                CameraSettings.RtspPath = path.Text;
                CameraSettings.RtspUrlOverride = CameraSettings.StripCredentials(urlOverride.Text);
                CameraSettings.UseSubStream = sub.Checked;
                CameraSettings.LatencyMs = (int)latency.Value;
                CameraSettings.UseTcp = tcp.Checked;
                CameraSettings.CustomPipeline = pipeline.Text;
                password.Text = "";
                password.Tag = null;
                password.PlaceholderSet(CameraSettings.HasPassword);
            }

            Bind(() =>
            {
                model.Text = CameraSettings.Model;
                host.Text = CameraSettings.Host;
                rtspPort.Value = CameraSettings.RtspPort;
                onvifPort.Value = CameraSettings.OnvifPort;
                user.Text = CameraSettings.Username;
                password.Text = "";
                password.PlaceholderSet(CameraSettings.HasPassword);
                path.Text = CameraSettings.RtspPath;
                urlOverride.Text = CameraSettings.RtspUrlOverride;
                sub.Checked = CameraSettings.UseSubStream;
                latency.Value = CameraSettings.LatencyMs;
                tcp.Checked = CameraSettings.UseTcp;
                pipeline.Text = CameraSettings.CustomPipeline;
                var c = CameraConnectionManager.Capabilities;
                status.Text = c.Tested ? CameraConnectionManager.StatusSummary(c) : "Not tested.";
            }, Save);

            clearPw.Click += (s, e) =>
            {
                CameraSettings.Password = "";
                AeroSettings.Save();
                password.PlaceholderSet(false);
                status.Text = "Stored camera password removed.";
            };

            test.Click += async (s, e) =>
            {
                test.Enabled = false;
                try
                {
                    Save();
                    AeroSettings.Save();
                    status.ForeColor = BrandingManager.Palette.StatusWarn;
                    status.Text = "Testing " + CameraSettings.BuildRtspUrl() + " ...";
                    var c = await CameraConnectionManager.ProbeAsync();
                    status.ForeColor = c.Rtsp.Status == RtspProbeStatus.Ok ? BrandingManager.Palette.StatusOk : BrandingManager.Palette.StatusCritical;
                    status.Text = CameraConnectionManager.StatusSummary(c);
                    if (VideoStreamManager.Instance.State != StreamState.Stopped)
                        VideoStreamManager.Instance.ApplySettings();
                }
                catch (Exception ex)
                {
                    status.ForeColor = BrandingManager.Palette.StatusCritical;
                    status.Text = "Test failed: " + ex.Message;
                }
                finally
                {
                    test.Enabled = true;
                }
            };
            return card;
        }
    }

    internal static class PasswordBoxExtensions
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        /// <summary>Shows a cue banner indicating whether a password is stored (the password itself is never shown).</summary>
        public static void PlaceholderSet(this TextBox box, bool stored)
        {
            const int EM_SETCUEBANNER = 0x1501;
            void Apply() => SendMessage(box.Handle, EM_SETCUEBANNER, (IntPtr)1,
                stored ? "•••••• (stored — type to replace)" : "not set");
            if (box.IsHandleCreated) Apply();
            else box.HandleCreated += (s, e) => Apply();
        }
    }
}
