using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using MissionPlanner.AeroAid.Branding;
using MissionPlanner.AeroAid.Flight;
using MissionPlanner.AeroAid.Telemetry;
using MissionPlanner.AeroAid.UI;

namespace MissionPlanner.AeroAid.Views
{
    /// <summary>Button-based flight control: hold-to-move pad, dynamic flight-mode selector, arm/disarm, takeoff, land, RTL.</summary>
    public class FlightControlView : AeroScreen
    {
        private readonly StatusTile tileLink = new StatusTile { Caption = "Link" };
        private readonly StatusTile tileMode = new StatusTile { Caption = "Mode" };
        private readonly StatusTile tileArmed = new StatusTile { Caption = "Motors" };
        private readonly StatusTile tileAlt = new StatusTile { Caption = "Altitude (rel)", Unit = "m" };
        private readonly StatusTile tileSpeed = new StatusTile { Caption = "Ground speed", Unit = "m/s" };
        private readonly StatusTile tileBatt = new StatusTile { Caption = "Battery" };
        private readonly StatusTile tileGps = new StatusTile { Caption = "GPS" };

        private readonly FlightPad pad = new FlightPad();
        private readonly Label readiness = AeroDraw.Body("", 10f);
        private readonly FlowLayoutPanel modeButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, BackColor = BrandingManager.Palette.Surface, MaximumSize = new Size(560, 0) };
        private readonly NumericUpDown takeoffAlt;
        private readonly Label limitsLabel = AeroDraw.Caption("", 9f);
        private readonly ListBox feedback;
        private readonly AeroButton btnArm, btnDisarm, btnTakeoff, btnLand, btnRtl, btnGuided, btnLoiter;
        private string modesSignature = "";

        public FlightControlView() : base("Flight Control", "Hold a direction to move (GUIDED mode, ArduCopter). Release to stop.")
        {
            var p = BrandingManager.Palette;

            // ---- status tiles
            var tiles = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, BackColor = p.Background, Padding = new Padding(0, 0, 0, 6) };
            foreach (var t in new[] { tileLink, tileMode, tileArmed, tileAlt, tileSpeed, tileBatt, tileGps })
            {
                t.Size = new Size(150, 62);
                tiles.Controls.Add(t);
            }

            // ---- pad card
            var padCard = new AeroCard { Title = "Movement", Glyph = AeroIcons.FlightControl, Width = 520, Height = 420 };
            readiness.MaximumSize = new Size(480, 0);
            readiness.Dock = DockStyle.Top;
            readiness.Padding = new Padding(2, 2, 2, 10);
            pad.Dock = DockStyle.Top;
            limitsLabel.Dock = DockStyle.Bottom;
            limitsLabel.Padding = new Padding(2, 8, 2, 0);
            padCard.Controls.Add(pad);
            padCard.Controls.Add(readiness);
            padCard.Controls.Add(limitsLabel);

            // ---- modes card
            var modeCard = new AeroCard { Title = "Flight mode", Glyph = AeroIcons.Settings, Width = 580, Height = 190, AutoScroll = true };
            modeCard.Controls.Add(modeButtons);

            // ---- actions card
            var actionCard = new AeroCard { Title = "Vehicle actions", Glyph = AeroIcons.Power, Width = 580, Height = 214 };
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = p.Surface };
            btnArm = AeroDraw.Button("Arm", AeroIcons.Unlock, AeroButtonStyle.Primary, 128);
            btnDisarm = AeroDraw.Button("Disarm", AeroIcons.Lock, AeroButtonStyle.Danger, 128);
            btnGuided = AeroDraw.Button("GUIDED", AeroIcons.FlightControl, AeroButtonStyle.Secondary, 128);
            btnLoiter = AeroDraw.Button("Hold position", AeroIcons.Stop, AeroButtonStyle.Secondary, 150);
            btnTakeoff = AeroDraw.Button("Take off", AeroIcons.Takeoff, AeroButtonStyle.Primary, 128);
            takeoffAlt = AeroDraw.Number((decimal)AeroSettings.TakeoffAltitude, 1, 50, 1, 1);
            takeoffAlt.Width = 70;
            takeoffAlt.Margin = new Padding(0, 8, 4, 8);
            var mLabel = AeroDraw.Caption("m", 9f);
            mLabel.Margin = new Padding(0, 10, 18, 0);
            btnLand = AeroDraw.Button("Land", AeroIcons.Land, AeroButtonStyle.Danger, 128);
            btnRtl = AeroDraw.Button("Return (RTL)", AeroIcons.Home, AeroButtonStyle.Secondary, 150);
            actions.Controls.AddRange(new Control[] { btnArm, btnDisarm, btnGuided, btnLoiter, btnTakeoff, takeoffAlt, mLabel, btnLand, btnRtl });
            actionCard.Controls.Add(actions);

            btnArm.Click += async (s, e) => await FlightActions.ArmWithConfirmation();
            btnDisarm.Click += async (s, e) => await FlightActions.DisarmWithConfirmation();
            btnGuided.Click += async (s, e) => await FlightActions.SetModeWithConfirmation("Guided");
            btnLoiter.Click += async (s, e) => await FlightActions.HoldPosition();
            btnTakeoff.Click += async (s, e) =>
            {
                AeroSettings.TakeoffAltitude = (double)takeoffAlt.Value;
                await FlightActions.TakeoffWithConfirmation((double)takeoffAlt.Value);
            };
            btnLand.Click += async (s, e) => await FlightActions.LandWithConfirmation();
            btnRtl.Click += async (s, e) => await FlightActions.RtlWithConfirmation();

            // ---- feedback
            var feedbackCard = new AeroCard { Title = "Command log", Glyph = AeroIcons.Telemetry, Height = 170, Dock = DockStyle.Bottom };
            feedback = new ListBox
            {
                Dock = DockStyle.Fill,
                BackColor = p.Surface,
                ForeColor = p.Text,
                BorderStyle = BorderStyle.None,
                Font = AeroDraw.UiFont(9f),
                IntegralHeight = false
            };
            feedbackCard.Controls.Add(feedback);

            var right = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, BackColor = p.Background, WrapContents = false };
            right.Controls.Add(modeCard);
            right.Controls.Add(actionCard);

            var main = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, BackColor = p.Background };
            main.Controls.Add(padCard);
            main.Controls.Add(right);

            Body.Controls.Add(main);
            Body.Controls.Add(tiles);
            Body.Controls.Add(feedbackCard);
            Body.Resize += (s, e) => feedbackCard.Width = Body.ClientSize.Width;
        }

        protected override void OnScreenActivated()
        {
            TelemetryManager.Updated += OnTelemetry;
            FlightControlManager.Instance.Feedback += OnFeedback;
            takeoffAlt.Value = (decimal)Math.Max(1, Math.Min(50, AeroSettings.TakeoffAltitude));
            limitsLabel.Text = $"Limits: {AeroSettings.MaxHorizontalSpeed:0.#} m/s horizontal • {AeroSettings.MaxVerticalSpeed:0.#} m/s vertical • " +
                               $"{AeroSettings.MaxYawRate:0}°/s yaw • {AeroSettings.MinAltitude:0.#}–{AeroSettings.MaxAltitude:0} m altitude (edit in Settings)";
            KeyHintsHook?.Invoke(pad);
        }

        /// <summary>Set by the keyboard module so pad hints show the configured keys.</summary>
        internal static Action<FlightPad> KeyHintsHook;

        protected override void OnScreenDeactivated()
        {
            TelemetryManager.Updated -= OnTelemetry;
            FlightControlManager.Instance.Feedback -= OnFeedback;
            FlightControlManager.Instance.StopAll();
        }

        private void OnFeedback(string message, StatusLevel level)
        {
            AeroDraw.SafeInvoke(this, () =>
            {
                feedback.Items.Insert(0, DateTime.Now.ToString("HH:mm:ss") + "  " + message);
                while (feedback.Items.Count > 200)
                    feedback.Items.RemoveAt(feedback.Items.Count - 1);
            });
        }

        private void OnTelemetry(TelemetrySnapshot t)
        {
            tileLink.SetValue(t.ConnectionText, t.ConnectionLevel);
            tileMode.SetValue(t.HasHeartbeat ? t.Mode.ToUpperInvariant() : "--", t.ModeLevel);
            tileArmed.SetValue(t.HasHeartbeat ? (t.Armed ? "ARMED" : "Disarmed") : "--", t.ArmedLevel);
            tileAlt.SetValue(t.HasHeartbeat ? t.AltRelative.ToString("0.0") : "--", t.HasHeartbeat ? StatusLevel.Ok : StatusLevel.Unavailable);
            tileSpeed.SetValue(t.HasHeartbeat ? t.GroundSpeed.ToString("0.0") : "--", t.HasHeartbeat ? StatusLevel.Ok : StatusLevel.Unavailable);
            tileBatt.SetValue(!t.HasHeartbeat ? "--" : t.BatteryPercent >= 0 ? $"{t.BatteryPercent}%  {t.BatteryVoltage:0.0}V" : $"{t.BatteryVoltage:0.0} V", t.BatteryLevel);
            tileGps.SetValue(t.HasHeartbeat ? $"{t.GpsText} • {t.Satellites}" : "--", t.GpsLevel);

            var canMove = FlightControlManager.CanMove(t, out var reason);
            pad.SetMovementEnabled(canMove);
            readiness.Text = canMove ? "● Movement controls ready" : "● " + reason;
            readiness.ForeColor = canMove ? BrandingManager.Palette.StatusOk : BrandingManager.Palette.StatusWarn;

            var link = t.HasHeartbeat;
            var busy = FlightControlManager.Instance.CommandInFlight;
            btnArm.Enabled = link && !t.Armed && !busy;
            btnDisarm.Enabled = link && t.Armed && !busy;
            btnTakeoff.Enabled = link && t.Armed && t.IsCopter && !busy;
            btnGuided.Enabled = link && !busy;
            btnLoiter.Enabled = link && !busy;
            btnLand.Enabled = link && !busy;
            btnRtl.Enabled = link && !busy;
            takeoffAlt.Enabled = btnTakeoff.Enabled;

            UpdateModes(t);
        }

        private void UpdateModes(TelemetrySnapshot t)
        {
            var modes = FlightControlManager.AvailableModes(t);
            var sig = t.Firmware + "|" + string.Join(",", modes);
            if (sig != modesSignature)
            {
                modesSignature = sig;
                modeButtons.SuspendLayout();
                foreach (Control c in modeButtons.Controls.Cast<Control>().ToList())
                    c.Dispose();
                modeButtons.Controls.Clear();
                if (modes.Count == 0)
                {
                    modeButtons.Controls.Add(AeroDraw.Caption("Connect a vehicle to list its flight modes.", 9.5f));
                }
                else
                {
                    foreach (var m in modes)
                    {
                        var mode = m;
                        var b = AeroDraw.Button(mode.ToUpperInvariant(), "", AeroButtonStyle.Secondary, 104, 34);
                        b.Font = AeroDraw.UiFont(8.5f, FontStyle.Bold);
                        b.Tag = mode;
                        b.Margin = new Padding(0, 0, 6, 6);
                        b.Click += async (s, e) => await FlightActions.SetModeWithConfirmation(mode);
                        modeButtons.Controls.Add(b);
                    }
                }

                modeButtons.ResumeLayout();
            }

            var busy = FlightControlManager.Instance.CommandInFlight;
            foreach (var b in modeButtons.Controls.OfType<AeroButton>())
            {
                b.IsActive = t.HasHeartbeat && string.Equals((string)b.Tag, t.Mode, StringComparison.OrdinalIgnoreCase);
                b.Enabled = t.HasHeartbeat && !busy;
            }
        }
    }
}
