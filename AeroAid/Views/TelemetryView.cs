using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using MissionPlanner.AeroAid.Branding;
using MissionPlanner.AeroAid.Telemetry;
using MissionPlanner.AeroAid.UI;

namespace MissionPlanner.AeroAid.Views
{
    /// <summary>Connection / MAVLink link health, GPS, battery, attitude and vehicle system messages.</summary>
    public class TelemetryView : AeroScreen
    {
        private readonly StatusTile conn = new StatusTile { Caption = "Connection" };
        private readonly StatusTile heartbeat = new StatusTile { Caption = "Last packet", Unit = "s ago" };
        private readonly StatusTile quality = new StatusTile { Caption = "Link quality", Unit = "%" };
        private readonly StatusTile port = new StatusTile { Caption = "Port" };
        private readonly StatusTile ids = new StatusTile { Caption = "Sys / Comp ID", ShowDot = false };
        private readonly StatusTile fw = new StatusTile { Caption = "Firmware", ShowDot = false };

        private readonly StatusTile mode = new StatusTile { Caption = "Flight mode" };
        private readonly StatusTile armed = new StatusTile { Caption = "Motors" };
        private readonly StatusTile prearm = new StatusTile { Caption = "Pre-arm checks" };
        private readonly StatusTile ekf = new StatusTile { Caption = "EKF variance" };
        private readonly StatusTile failsafe = new StatusTile { Caption = "Failsafe" };

        private readonly StatusTile gps = new StatusTile { Caption = "GPS fix" };
        private readonly StatusTile sats = new StatusTile { Caption = "Satellites" };
        private readonly StatusTile hdop = new StatusTile { Caption = "HDOP", ShowDot = false };
        private readonly StatusTile batt = new StatusTile { Caption = "Battery" };
        private readonly StatusTile volt = new StatusTile { Caption = "Voltage", Unit = "V", ShowDot = false };
        private readonly StatusTile amps = new StatusTile { Caption = "Current", Unit = "A", ShowDot = false };

        private readonly ListView messages;
        private readonly Label errorBanner;
        private readonly AeroButton connectBtn;
        private DateTime lastMessageTime = DateTime.MinValue;

        public TelemetryView() : base("Telemetry", "MAVLink link health and vehicle messages (Serial / USB / telemetry radio / UDP / TCP)")
        {
            var p = BrandingManager.Palette;
            connectBtn = AeroDraw.Button("Connect", AeroIcons.Plug, AeroButtonStyle.Primary, 150);
            connectBtn.Click += (s, e) => MainV2.instance.AeroNavigate("Connect");
            var hud = AeroDraw.Button("Full Flight Data (HUD)", AeroIcons.Hud, AeroButtonStyle.Secondary, 210);
            hud.Click += (s, e) => MainV2.instance.AeroNavigate("FlightData");
            HeaderTools.Controls.Add(connectBtn);
            HeaderTools.Controls.Add(hud);

            errorBanner = new Label
            {
                Dock = DockStyle.Top,
                Height = 0,
                ForeColor = p.Text,
                BackColor = Color.FromArgb(0x4A, 0x1E, 0x1C),
                Font = AeroDraw.UiFont(10f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(12, 0, 0, 0)
            };

            Body.Controls.Add(MessagesCard(out messages));
            Body.Controls.Add(Row("Power & position", gps, sats, hdop, batt, volt, amps));
            Body.Controls.Add(Row("Vehicle state", mode, armed, prearm, ekf, failsafe));
            Body.Controls.Add(Row("Link", conn, heartbeat, quality, port, ids, fw));
            Body.Controls.Add(errorBanner);
        }

        private Control Row(string title, params StatusTile[] tiles)
        {
            var card = new AeroCard { Title = title, Dock = DockStyle.Top, Height = 124 };
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = BrandingManager.Palette.Surface };
            foreach (var t in tiles)
            {
                t.Size = new Size(170, 60);
                t.BackColor = BrandingManager.Palette.SurfaceAlt;
                flow.Controls.Add(t);
            }

            card.Controls.Add(flow);
            var wrap = new Panel { Dock = DockStyle.Top, Height = 132, BackColor = BrandingManager.Palette.Background, Padding = new Padding(0, 0, 0, 8) };
            card.Dock = DockStyle.Fill;
            wrap.Controls.Add(card);
            return wrap;
        }

        private Control MessagesCard(out ListView list)
        {
            var p = BrandingManager.Palette;
            var card = new AeroCard { Title = "System messages (from vehicle)", Glyph = AeroIcons.Telemetry, Dock = DockStyle.Top, Height = 300 };
            list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                BackColor = p.Surface,
                ForeColor = p.Text,
                BorderStyle = BorderStyle.None,
                Font = AeroDraw.UiFont(9f)
            };
            list.Columns.Add("Time", 90);
            list.Columns.Add("Message", 900);
            card.Controls.Add(list);
            return card;
        }

        protected override void OnScreenActivated()
        {
            lastMessageTime = DateTime.MinValue;
            messages.Items.Clear();
            TelemetryManager.Updated += OnTelemetry;
        }

        protected override void OnScreenDeactivated()
        {
            TelemetryManager.Updated -= OnTelemetry;
        }

        private void OnTelemetry(TelemetrySnapshot t)
        {
            var hb = t.HasHeartbeat;
            var ok = hb ? StatusLevel.Ok : StatusLevel.Unavailable;
            connectBtn.Text = t.Connected ? "Disconnect" : "Connect";
            connectBtn.ButtonStyle = t.Connected ? AeroButtonStyle.Secondary : AeroButtonStyle.Primary;

            conn.SetValue(t.ConnectionText, t.ConnectionLevel);
            heartbeat.SetValue(t.Connected && !double.IsInfinity(t.LinkAgeSeconds) ? t.LinkAgeSeconds.ToString("0.0") : "--", t.ConnectionLevel);
            quality.SetValue(t.Connected && t.LinkQualityPercent >= 0 ? t.LinkQualityPercent.ToString() : "--",
                !t.Connected ? StatusLevel.Unavailable : t.LinkQualityPercent < 50 ? StatusLevel.Critical : t.LinkQualityPercent < 85 ? StatusLevel.Warning : StatusLevel.Ok);
            port.SetValue(t.Connected ? $"{t.PortName} {(t.Baud > 0 && t.PortName.StartsWith("COM") ? t.Baud.ToString() : "")}".Trim() : "--", t.Connected ? StatusLevel.Ok : StatusLevel.Unavailable);
            ids.SetValue(hb ? $"{t.SysId} / {t.CompId}" : "--", ok);
            fw.SetValue(hb ? (string.IsNullOrEmpty(t.FirmwareVersion) ? t.Firmware.ToString() : t.FirmwareVersion) : "--", ok);

            mode.SetValue(hb ? t.Mode.ToUpperInvariant() : "--", t.ModeLevel);
            armed.SetValue(hb ? (t.Armed ? "ARMED" : "Disarmed") : "--", t.ArmedLevel);
            prearm.SetValue(!hb || t.PrearmOk == null ? "Not reported" : t.PrearmOk.Value ? "Passing" : "Failing", t.PrearmLevel);
            ekf.SetValue(hb ? t.EkfVariance.ToString("0.00") : "--", t.EkfLevel);
            failsafe.SetValue(hb ? (t.Failsafe ? "ACTIVE" : "None") : "--", !hb ? StatusLevel.Unavailable : t.Failsafe ? StatusLevel.Critical : StatusLevel.Ok);

            gps.SetValue(t.GpsText, t.GpsLevel);
            sats.SetValue(hb ? t.Satellites.ToString() : "--", t.GpsLevel);
            hdop.SetValue(hb ? t.Hdop.ToString("0.0") : "--", ok);
            batt.SetValue(hb && t.BatteryPercent >= 0 ? t.BatteryPercent + " %" : "--", t.BatteryLevel);
            volt.SetValue(hb && t.BatteryVoltage > 0 ? t.BatteryVoltage.ToString("0.00") : "--", ok);
            amps.SetValue(hb ? t.BatteryCurrent.ToString("0.0") : "--", ok);

            // error banner: failsafe / no heartbeat / failing pre-arm
            string error = null;
            if (t.Connected && !hb) error = "No heartbeat received for " + (double.IsInfinity(t.LinkAgeSeconds) ? "a while" : t.LinkAgeSeconds.ToString("0") + " s") + " - check the link.";
            else if (hb && t.Failsafe) error = "FAILSAFE ACTIVE: " + t.LastHighMessage;
            else if (hb && t.PrearmOk == false && !t.Armed) error = "Pre-arm check failing: " + t.LastHighMessage;
            errorBanner.Text = error ?? "";
            errorBanner.Height = error == null ? 0 : 34;

            // append new vehicle messages (newest first)
            var fresh = t.Messages.Where(m => m.time > lastMessageTime).OrderBy(m => m.time).ToList();
            if (fresh.Count > 0)
            {
                messages.BeginUpdate();
                foreach (var m in fresh)
                {
                    var item = new ListViewItem(new[] { m.time.ToString("HH:mm:ss"), m.message });
                    var lower = (m.message ?? "").ToLowerInvariant();
                    if (lower.Contains("prearm") || lower.Contains("fail") || lower.Contains("error") || lower.Contains("crash"))
                        item.ForeColor = BrandingManager.Palette.StatusCritical;
                    messages.Items.Insert(0, item);
                }

                while (messages.Items.Count > 300)
                    messages.Items.RemoveAt(messages.Items.Count - 1);
                messages.EndUpdate();
                lastMessageTime = fresh.Last().time;
            }
        }
    }
}
