using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using MissionPlanner.AeroAid.Audio;
using MissionPlanner.AeroAid.Branding;
using MissionPlanner.AeroAid.Flight;
using MissionPlanner.AeroAid.Telemetry;
using MissionPlanner.AeroAid.UI;
using MissionPlanner.AeroAid.Voice;

namespace MissionPlanner.AeroAid.Views
{
    /// <summary>Voice-command microphone controls and the voice-takeoff workflow.</summary>
    public class VoiceView : AeroScreen
    {
        private readonly ComboBox devices = AeroDraw.Combo();
        private readonly AeroButton micToggle;
        private readonly LevelMeter level = new LevelMeter { Width = 380, Height = 12 };
        private readonly Label micStatus = AeroDraw.Body("", 9.5f);
        private readonly AeroButton listenToggle;
        private readonly StatusTile listenTile = new StatusTile { Caption = "Voice listening", Size = new Size(380, 64) };
        private readonly Label heard = AeroDraw.Body("", 10.5f);
        private readonly Label voiceStatus = AeroDraw.Caption("", 9f);
        private readonly ListBox log;
        private readonly AeroButton btnArm, btnDisarm;
        private readonly Timer ui = new Timer { Interval = 100 };
        private List<(int id, string name)> deviceList = new List<(int, string)>();

        public VoiceView() : base("Voice & Microphone", "Voice-initiated takeoff with mandatory arm and takeoff confirmations")
        {
            var p = BrandingManager.Palette;
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = p.Background, AutoScroll = true };

            // ---- microphone
            var mic = new AeroCard { Title = "Voice-command microphone", Glyph = AeroIcons.Mic, Width = 430, Height = 300 };
            var mf = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = p.Surface };
            devices.Width = 380;
            devices.SelectedIndexChanged += (s, e) =>
            {
                if (devices.SelectedIndex < 0 || devices.SelectedIndex >= deviceList.Count) return;
                var name = deviceList[devices.SelectedIndex].id < 0 ? "" : deviceList[devices.SelectedIndex].name;
                if (name == MicrophoneManager.SelectedDeviceName) return;
                MicrophoneManager.SelectedDeviceName = name;
                AeroSettings.Save();
                if (MicrophoneManager.Instance.IsOn)
                {
                    var wasListening = VoiceCommandEngine.Instance.Listening;
                    VoiceCommandEngine.Instance.StopListening();
                    MicrophoneManager.Instance.TurnOff();
                    MicrophoneManager.Instance.TurnOn();
                    if (wasListening) VoiceCommandEngine.Instance.StartListening();
                }
            };
            var refresh = AeroDraw.Button("Refresh devices", AeroIcons.Refresh, AeroButtonStyle.Ghost, 180, 32);
            refresh.Click += (s, e) => LoadDevices();
            micToggle = AeroDraw.Button("Microphone ON", AeroIcons.Mic, AeroButtonStyle.Primary, 200);
            micToggle.Click += (s, e) =>
            {
                if (MicrophoneManager.Instance.IsOn)
                {
                    VoiceCommandEngine.Instance.StopListening();
                    MicrophoneManager.Instance.TurnOff();
                }
                else
                {
                    MicrophoneManager.Instance.TurnOn();
                }
            };
            micStatus.MaximumSize = new Size(380, 0);
            mf.Controls.Add(AeroDraw.Caption("Input device", 9f));
            mf.Controls.Add(devices);
            mf.Controls.Add(refresh);
            mf.Controls.Add(micToggle);
            mf.Controls.Add(AeroDraw.Caption("Input level", 9f));
            mf.Controls.Add(level);
            mf.Controls.Add(micStatus);
            mic.Controls.Add(mf);

            // ---- voice recognition
            var voice = new AeroCard { Title = "Voice control", Glyph = AeroIcons.Takeoff, Width = 430, Height = 300 };
            var vf = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = p.Surface };
            listenToggle = AeroDraw.Button("Start listening", AeroIcons.Mic, AeroButtonStyle.Primary, 200);
            listenToggle.Click += (s, e) =>
            {
                if (VoiceCommandEngine.Instance.Listening) VoiceCommandEngine.Instance.StopListening();
                else VoiceCommandEngine.Instance.StartListening();
            };
            heard.MaximumSize = new Size(380, 0);
            voiceStatus.MaximumSize = new Size(380, 0);
            vf.Controls.Add(listenToggle);
            vf.Controls.Add(listenTile);
            vf.Controls.Add(AeroDraw.Caption("Last recognised", 9f));
            vf.Controls.Add(heard);
            vf.Controls.Add(voiceStatus);
            vf.Controls.Add(AeroDraw.Caption("Say: \"" + string.Join("\", \"", VoiceCommandEngine.TakeoffPhrases.Where(x => !x.Contains("takeoff") || x == "initiate takeoff")) + "\"", 8.5f));
            voice.Controls.Add(vf);

            // ---- takeoff workflow
            var wf = new AeroCard { Title = "Takeoff workflow & arming", Glyph = AeroIcons.Power, Width = 870, Height = 300 };
            var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 50, BackColor = p.Surface };
            var check = AeroDraw.Button("Run pre-flight checks", AeroIcons.Check, AeroButtonStyle.Secondary, 220);
            check.Click += (s, e) => ShowChecks();
            btnArm = AeroDraw.Button("Arm", AeroIcons.Unlock, AeroButtonStyle.Primary, 130);
            btnArm.Click += async (s, e) => await FlightActions.ArmWithConfirmation();
            btnDisarm = AeroDraw.Button("Disarm", AeroIcons.Lock, AeroButtonStyle.Danger, 130);
            btnDisarm.Click += async (s, e) => await FlightActions.DisarmWithConfirmation();
            top.Controls.AddRange(new Control[] { check, btnArm, btnDisarm });
            log = new ListBox { Dock = DockStyle.Fill, BackColor = p.Surface, ForeColor = p.Text, BorderStyle = BorderStyle.None, Font = AeroDraw.UiFont(9f) };
            wf.Controls.Add(log);
            wf.Controls.Add(top);

            var note = AeroDraw.Caption(
                "Voice never flies the vehicle on its own: a recognised command runs pre-flight checks, then requires an ARM " +
                "confirmation and a separate TAKEOFF confirmation. This PC microphone is separate from the camera's microphone " +
                "(Live Camera › options); talking through the camera (two-way audio) is not supported.", 8.5f);
            note.MaximumSize = new Size(860, 0);

            flow.Controls.Add(mic);
            flow.Controls.Add(voice);
            flow.Controls.Add(wf);
            flow.Controls.Add(note);
            Body.Controls.Add(flow);

            ui.Tick += (s, e) => RefreshUi();
            VoiceTakeoffWorkflow.Progress += (m, l) => AddLog(m);
        }

        private void AddLog(string m)
        {
            AeroDraw.SafeInvoke(this, () =>
            {
                log.Items.Insert(0, DateTime.Now.ToString("HH:mm:ss") + "  " + m);
                while (log.Items.Count > 200) log.Items.RemoveAt(log.Items.Count - 1);
            });
        }

        private void ShowChecks()
        {
            var passed = new List<string>();
            var fails = VoiceTakeoffWorkflow.Check(TelemetryManager.Capture(), passed);
            foreach (var pz in passed) AddLog("✓ " + pz);
            foreach (var f in fails) AddLog("✗ " + f);
            AddLog(fails.Count == 0 ? "Pre-flight checks: READY for voice takeoff." : $"Pre-flight checks: {fails.Count} problem(s).");
        }

        private void LoadDevices()
        {
            deviceList = WaveInCapture.Devices();
            devices.Items.Clear();
            foreach (var d in deviceList) devices.Items.Add(d.name);
            var sel = MicrophoneManager.SelectedDeviceName;
            var idx = string.IsNullOrEmpty(sel) ? 0 : deviceList.FindIndex(d => d.id >= 0 && d.name == sel);
            if (idx < 0)
            {
                devices.Items.Add(sel + " (not connected)");
                deviceList.Add((-2, sel));
                idx = devices.Items.Count - 1;
            }

            devices.SelectedIndex = idx;
        }

        protected override void OnScreenActivated()
        {
            LoadDevices();
            ui.Start();
        }

        protected override void OnScreenDeactivated() => ui.Stop();

        private void RefreshUi()
        {
            var p = BrandingManager.Palette;
            var mic = MicrophoneManager.Instance;
            var v = VoiceCommandEngine.Instance;
            micToggle.Text = mic.IsOn ? "Microphone OFF" : "Microphone ON";
            micToggle.Glyph = mic.IsOn ? AeroIcons.MicOff : AeroIcons.Mic;
            micToggle.ButtonStyle = mic.IsOn ? AeroButtonStyle.Secondary : AeroButtonStyle.Primary;
            level.Value = mic.IsOn ? mic.Level : 0;
            level.BarColor = mic.Level > 0.85f ? p.StatusCritical : p.Accent;
            micStatus.Text = mic.Status + (mic.SilentTooLong
                ? "\nNo signal from the microphone. Check Windows Settings › Privacy & security › Microphone " +
                  "(\"Let desktop apps access your microphone\") and that the device is not muted."
                : "");
            micStatus.ForeColor = mic.HasError || mic.SilentTooLong ? p.StatusCritical : mic.IsOn ? p.StatusOk : p.Muted;

            listenToggle.Text = v.Listening ? "Stop listening" : "Start listening";
            listenToggle.ButtonStyle = v.Listening ? AeroButtonStyle.Secondary : AeroButtonStyle.Primary;
            listenToggle.Enabled = VoiceSettings.Enabled;
            listenTile.SetValue(v.Listening ? $"LISTENING • {v.SecondsRemaining:0} s left" : VoiceTakeoffWorkflow.Running ? "Workflow running" : "Not listening",
                v.Listening ? StatusLevel.Ok : StatusLevel.Unavailable);
            heard.Text = string.IsNullOrEmpty(v.LastHeard) ? "—" : v.LastHeard;
            voiceStatus.Text = v.Status + "\nRecogniser: " + VoiceCommandEngine.RecognizerInfo();

            var t = TelemetryManager.Capture();
            var busy = FlightControlManager.Instance.CommandInFlight;
            btnArm.Enabled = t.HasHeartbeat && !t.Armed && !busy;
            btnDisarm.Enabled = t.HasHeartbeat && t.Armed && !busy;
        }
    }
}
