using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using MissionPlanner.AeroAid.Branding;
using MissionPlanner.AeroAid.Flight;
using MissionPlanner.AeroAid.Telemetry;
using MissionPlanner.AeroAid.UI;

namespace MissionPlanner.AeroAid.Views
{
    /// <summary>Main dashboard: live status, map, flight data, quick controls and module status cards.</summary>
    public class DashboardView : AeroScreen
    {
        private readonly StatusTile tConn = new StatusTile { Caption = "Connection" };
        private readonly StatusTile tVehicle = new StatusTile { Caption = "Vehicle" };
        private readonly StatusTile tMode = new StatusTile { Caption = "Flight mode" };
        private readonly StatusTile tArmed = new StatusTile { Caption = "Motors" };
        private readonly StatusTile tBatt = new StatusTile { Caption = "Battery" };
        private readonly StatusTile tVolt = new StatusTile { Caption = "Voltage", Unit = "V" };
        private readonly StatusTile tGps = new StatusTile { Caption = "GPS" };
        private readonly StatusTile tSats = new StatusTile { Caption = "Satellites" };
        private readonly StatusTile tEkf = new StatusTile { Caption = "EKF" };
        private readonly StatusTile tPrearm = new StatusTile { Caption = "Pre-arm" };

        private readonly StatusTile fLat = new StatusTile { Caption = "Latitude", ShowDot = false };
        private readonly StatusTile fLng = new StatusTile { Caption = "Longitude", ShowDot = false };
        private readonly StatusTile fAlt = new StatusTile { Caption = "Altitude (rel)", Unit = "m", ShowDot = false };
        private readonly StatusTile fGs = new StatusTile { Caption = "Ground speed", Unit = "m/s", ShowDot = false };
        private readonly StatusTile fAs = new StatusTile { Caption = "Air speed", Unit = "m/s", ShowDot = false };
        private readonly StatusTile fHdg = new StatusTile { Caption = "Heading", Unit = "°", ShowDot = false };
        private readonly StatusTile fRoll = new StatusTile { Caption = "Roll", Unit = "°", ShowDot = false };
        private readonly StatusTile fPitch = new StatusTile { Caption = "Pitch", Unit = "°", ShowDot = false };
        private readonly StatusTile fYaw = new StatusTile { Caption = "Yaw", Unit = "°", ShowDot = false };
        private readonly StatusTile fClimb = new StatusTile { Caption = "Climb rate", Unit = "m/s", ShowDot = false };

        private readonly VehicleMap map = new VehicleMap { Dock = DockStyle.Fill };
        private readonly AeroButton btnConnect;
        private readonly AeroButton btnArm, btnDisarm, btnTakeoff, btnLand, btnRtl, btnHold;
        private readonly FlowLayoutPanel sideColumn;
        private readonly Label lastMessage = AeroDraw.Caption("", 9f);


        public DashboardView() : base("Dashboard", "Live overview of the connected vehicle")
        {
            var p = BrandingManager.Palette;

            btnConnect = AeroDraw.Button("Connect", AeroIcons.Plug, AeroButtonStyle.Primary, 150);
            btnConnect.Click += (s, e) => MainV2.instance.AeroNavigate("Connect");
            HeaderTools.Controls.Add(btnConnect);

            var statusRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, BackColor = p.Background, Padding = new Padding(0, 0, 0, 4) };
            foreach (var t in new[] { tConn, tVehicle, tMode, tArmed, tBatt, tVolt, tGps, tSats, tEkf, tPrearm })
            {
                t.Size = new Size(148, 62);
                statusRow.Controls.Add(t);
            }

            var flightRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, BackColor = p.Background, Padding = new Padding(0, 4, 0, 4) };
            foreach (var t in new[] { fLat, fLng, fAlt, fGs, fAs, fHdg, fRoll, fPitch, fYaw, fClimb })
            {
                t.Size = new Size(148, 56);
                flightRow.Controls.Add(t);
            }

            // map + side column
            var grid = new TableLayoutPanel { Dock = DockStyle.Top, Height = 730, ColumnCount = 2, BackColor = p.Background };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 380));

            var mapCard = new AeroCard { Title = "Map", Glyph = AeroIcons.Mission, Dock = DockStyle.Fill };
            var follow = AeroDraw.Check("Follow vehicle", true);
            follow.Dock = DockStyle.Bottom;
            follow.CheckedChanged += (s, e) => map.FollowVehicle = follow.Checked;
            mapCard.Controls.Add(map);
            mapCard.Controls.Add(follow);
            grid.Controls.Add(mapCard, 0, 0);

            sideColumn = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = p.Background };
            grid.Controls.Add(sideColumn, 1, 0);

            // quick controls
            var quick = new AeroCard { Title = "Quick flight controls", Glyph = AeroIcons.FlightControl, Width = 360, Height = 248 };
            var qf = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = p.Surface };
            btnArm = AeroDraw.Button("Arm", AeroIcons.Unlock, AeroButtonStyle.Primary, 158);
            btnDisarm = AeroDraw.Button("Disarm", AeroIcons.Lock, AeroButtonStyle.Danger, 158);
            btnTakeoff = AeroDraw.Button("Take off", AeroIcons.Takeoff, AeroButtonStyle.Primary, 158);
            btnHold = AeroDraw.Button("Hold position", AeroIcons.Stop, AeroButtonStyle.Secondary, 158);
            btnLand = AeroDraw.Button("Land", AeroIcons.Land, AeroButtonStyle.Danger, 158);
            btnRtl = AeroDraw.Button("Return (RTL)", AeroIcons.Home, AeroButtonStyle.Secondary, 158);
            var open = AeroDraw.Button("Open Flight Control", AeroIcons.FlightControl, AeroButtonStyle.Ghost, 324);
            qf.Controls.AddRange(new Control[] { btnArm, btnDisarm, btnTakeoff, btnHold, btnLand, btnRtl, open });
            quick.Controls.Add(qf);
            btnArm.Click += async (s, e) => await FlightActions.ArmWithConfirmation();
            btnDisarm.Click += async (s, e) => await FlightActions.DisarmWithConfirmation();
            btnHold.Click += async (s, e) => await FlightActions.HoldPosition();
            btnTakeoff.Click += async (s, e) => await FlightActions.TakeoffWithConfirmation(AeroSettings.TakeoffAltitude);
            btnLand.Click += async (s, e) => await FlightActions.LandWithConfirmation();
            btnRtl.Click += async (s, e) => await FlightActions.RtlWithConfirmation();
            open.Click += (s, e) => MainV2.instance.AeroNavigate(AeroShell.ScreenFlightControl);
            sideColumn.Controls.Add(quick);

            sideColumn.Controls.Add(CameraCard());
            sideColumn.Controls.Add(AiCard());

            lastMessage.Dock = DockStyle.Top;
            lastMessage.Padding = new Padding(4, 6, 0, 0);

            Body.Controls.Add(lastMessage);
            Body.Controls.Add(flightRow);
            Body.Controls.Add(grid);
            Body.Controls.Add(statusRow);
        }

        private Control CameraCard()
        {
            var p = BrandingManager.Palette;
            var card = new AeroCard { Title = "Camera preview", Glyph = AeroIcons.Camera, Width = 360, Height = 290 };
            var video = new Camera.VideoView { Dock = DockStyle.Fill, ShowInfo = false };
            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, BackColor = p.Surface, Padding = new Padding(0, 6, 0, 0) };
            var start = AeroDraw.Button("Start / stop", AeroIcons.Play, AeroButtonStyle.Secondary, 150);
            start.Click += (s, e) =>
            {
                var st = Camera.VideoStreamManager.Instance.State;
                if (st == Camera.StreamState.Stopped || st == Camera.StreamState.Error) Camera.CameraUi.StartStream();
                else Camera.VideoStreamManager.Instance.Stop();
            };
            var open = AeroDraw.Button("Live Camera", AeroIcons.Camera, AeroButtonStyle.Ghost, 160);
            open.Click += (s, e) => MainV2.instance.AeroNavigate(AeroShell.ScreenLiveCamera);
            bar.Controls.Add(start);
            bar.Controls.Add(open);
            card.Controls.Add(video);
            card.Controls.Add(bar);
            return card;
        }

        private AeroCard aiCard;
        private Label aiStatus;

        private Control AiCard()
        {
            aiCard = new AeroCard { Title = "AI detection", Glyph = AeroIcons.AiDetection, Width = 360, Height = 160 };
            aiStatus = AeroDraw.Body("", 9.5f);
            aiStatus.MaximumSize = new Size(330, 0);
            aiStatus.Dock = DockStyle.Fill;
            var open = AeroDraw.Button("Open AI Detection", AeroIcons.AiDetection, AeroButtonStyle.Ghost, 220);
            open.Dock = DockStyle.Bottom;
            open.Click += (s, e) => MainV2.instance.AeroNavigate(AeroShell.ScreenAiDetection);
            aiCard.Controls.Add(aiStatus);
            aiCard.Controls.Add(open);
            return aiCard;
        }

        private void UpdateAiCard()
        {
            var p = BrandingManager.Palette;
            var m = Vision.AiDetectionManager.Instance;
            var alerts = m.ActiveAlerts;
            aiCard.AccentOverride = alerts.Count > 0 ? alerts[0].Category.Color : (Color?)null;
            if (alerts.Count > 0)
            {
                aiStatus.Text = string.Join("\n", alerts.Take(4).Select(a =>
                    $"⚠ {a.Category.AlertTitle.ToUpperInvariant()} since {a.Start:HH:mm:ss} ({a.MaxConfidence:P0})" +
                    (a.Acknowledged ? " ✓" : "")));
                aiStatus.ForeColor = alerts.Any(a => !a.Acknowledged) ? p.StatusCritical : p.StatusWarn;
                return;
            }

            var operational = Vision.DetectionCategories.All.Where(c => m.IsOperational(c.Id)).Select(c => c.Name).ToList();
            var last = m.History.FirstOrDefault();
            aiStatus.Text = m.State == Vision.AiEngineState.Running
                ? "Monitoring — no alerts.\nActive categories: " + string.Join(", ", operational) +
                  (last != null ? $"\nLast alert: {last.Start:HH:mm:ss} ({last.Category.Name})" : "")
                : m.StatusMessage;
            aiStatus.ForeColor = m.State == Vision.AiEngineState.Running ? p.StatusOk
                : m.State == Vision.AiEngineState.Disabled ? p.Muted : p.StatusWarn;
        }

        protected override void OnScreenActivated()
        {
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
            btnConnect.Text = t.Connected ? "Disconnect" : "Connect";
            btnConnect.ButtonStyle = t.Connected ? AeroButtonStyle.Secondary : AeroButtonStyle.Primary;

            tConn.SetValue(t.ConnectionText, t.ConnectionLevel);
            tVehicle.SetValue(hb ? t.VehicleType : "--", ok);
            tMode.SetValue(hb ? t.Mode.ToUpperInvariant() : "--", t.ModeLevel);
            tArmed.SetValue(hb ? (t.Armed ? "ARMED" : "Disarmed") : "--", t.ArmedLevel);
            tBatt.SetValue(hb && t.BatteryPercent >= 0 ? t.BatteryPercent + " %" : "--", t.BatteryLevel);
            tVolt.SetValue(hb && t.BatteryVoltage > 0 ? t.BatteryVoltage.ToString("0.00") : "--", t.BatteryLevel);
            tGps.SetValue(t.GpsText, t.GpsLevel);
            tSats.SetValue(hb ? t.Satellites.ToString() : "--", t.GpsLevel);
            tEkf.SetValue(hb ? t.EkfVariance.ToString("0.00") : "--", t.EkfLevel);
            tPrearm.SetValue(!hb || t.PrearmOk == null ? "--" : t.PrearmOk.Value ? "Ready" : "Failing", t.PrearmLevel);

            fLat.SetValue(hb && t.HasPosition ? t.Lat.ToString("0.000000") : "--", ok);
            fLng.SetValue(hb && t.HasPosition ? t.Lng.ToString("0.000000") : "--", ok);
            fAlt.SetValue(hb ? t.AltRelative.ToString("0.0") : "--", ok);
            fGs.SetValue(hb ? t.GroundSpeed.ToString("0.0") : "--", ok);
            fAs.SetValue(hb && (t.IsPlane || t.AirSpeed > 0.1) ? t.AirSpeed.ToString("0.0") : "--", ok);
            fHdg.SetValue(hb ? t.Heading.ToString("0") : "--", ok);
            fRoll.SetValue(hb ? t.Roll.ToString("0.0") : "--", ok);
            fPitch.SetValue(hb ? t.Pitch.ToString("0.0") : "--", ok);
            fYaw.SetValue(hb ? t.Yaw.ToString("0.0") : "--", ok);
            fClimb.SetValue(hb ? t.ClimbRate.ToString("0.0") : "--", ok);

            lastMessage.Text = hb && !string.IsNullOrEmpty(t.LastHighMessage) ? "Vehicle: " + t.LastHighMessage : "";
            lastMessage.ForeColor = BrandingManager.Palette.StatusWarn;

            var busy = FlightControlManager.Instance.CommandInFlight;
            btnArm.Enabled = hb && !t.Armed && !busy;
            btnDisarm.Enabled = hb && t.Armed && !busy;
            btnTakeoff.Enabled = hb && t.Armed && t.IsCopter && !busy;
            btnHold.Enabled = btnLand.Enabled = btnRtl.Enabled = hb && !busy;

            map.UpdateVehicle(t);
            UpdateAiCard();
        }
    }
}
