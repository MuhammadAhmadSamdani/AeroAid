using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MissionPlanner.AeroAid.Audio;
using MissionPlanner.AeroAid.Branding;
using MissionPlanner.AeroAid.Flight;
using MissionPlanner.AeroAid.Telemetry;
using MissionPlanner.AeroAid.UI;
using MissionPlanner.AeroAid.Voice;
using Timer = System.Windows.Forms.Timer;

namespace MissionPlanner.AeroAid.Views
{
    /// <summary>
    /// Voice &amp; Microphone page: microphone selection / test / level, the speech engine and listening state, the
    /// recognised transcript, the classified command with its confidence and validation result, the execution
    /// result, and a TEST MODE with per-command recognition tests and an accuracy report.
    /// </summary>
    public class VoiceView : AeroScreen
    {
        private const int CardW = 430;
        private const int InnerW = 395;
        private const int WideW = 2 * CardW + 10;

        // microphone
        private readonly ComboBox devices = AeroDraw.Combo();
        private readonly AeroButton micToggle, micTest, micPlay;
        private readonly StatusTile micTile = new StatusTile { Caption = "Microphone", Size = new Size(190, 64) };
        private readonly StatusTile signalTile = new StatusTile { Caption = "Receiving audio", Size = new Size(190, 64) };
        private readonly LevelMeter level = new LevelMeter { Width = InnerW, Height = 12 };
        private readonly Label levelText = AeroDraw.Caption("", 9f);
        private readonly Label micStatus = AeroDraw.Body("", 9f);

        // speech recognition
        private readonly StatusTile engineTile = new StatusTile { Caption = "Speech engine", Size = new Size(190, 64) };
        private readonly StatusTile listenTile = new StatusTile { Caption = "Listening", Size = new Size(190, 64) };
        private readonly AeroButton btnStart, btnStop, btnTestMode, btnTestRec, btnModel;
        private readonly Label partial = AeroDraw.Body("", 10f);
        private readonly Label heard = AeroDraw.Body("", 12f);
        private readonly Label recStatus = AeroDraw.Caption("", 9f);

        // command detection / validation
        private readonly StatusTile detectTile = new StatusTile { Caption = "Detected command", Size = new Size(InnerW, 64) };
        private readonly LevelMeter confMeter = new LevelMeter { Width = InnerW, Height = 10 };
        private readonly Label confText = AeroDraw.Caption("", 9f);
        private readonly Label validation = AeroDraw.Body("", 9.5f);

        // execution
        private readonly StatusTile execTile = new StatusTile { Caption = "Execution result", Size = new Size(InnerW, 64) };
        private readonly Label execInfo = AeroDraw.Body("", 9.5f);
        private readonly Label testBanner = AeroDraw.Body("", 10f);
        private readonly AeroButton btnArm, btnDisarm;

        // test mode
        private readonly Label testPrompt = AeroDraw.Body("", 11f);
        private readonly Label lastAttempt = AeroDraw.Body("", 10f);
        private readonly ListView testTable;
        private readonly Dictionary<VoiceTestTarget, AeroButton> testButtons = new Dictionary<VoiceTestTarget, AeroButton>();

        private readonly ListBox log;
        private readonly Timer ui = new Timer { Interval = 100 };
        private List<(int id, string name)> deviceList = new List<(int, string)>();
        private long shownUtterance = -1;
        private MicTestResult lastMicTest;
        private bool micTesting;
        private DateTime micTestShownUntil = DateTime.MinValue;
        private CancellationTokenSource download;
        private string downloadText = "";

        public VoiceView() : base("Voice & Microphone",
            "Offline command recognition: speak a command, it is classified, validated and confirmed before anything is sent. Test Mode never sends anything.")
        {
            var p = BrandingManager.Palette;
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = p.Background, AutoScroll = true };

            // ---------------- microphone
            var mic = new AeroCard { Title = "Microphone", Glyph = AeroIcons.Mic, Width = CardW, Height = 430 };
            var mf = Column(p);
            mf.Controls.Add(Row(p, 74, micTile, signalTile));
            devices.Width = InnerW;
            devices.SelectedIndexChanged += (s, e) => OnDeviceChanged();
            micToggle = AeroDraw.Button("Microphone ON", AeroIcons.Mic, AeroButtonStyle.Primary, 160, 34);
            micToggle.Click += (s, e) => ToggleMicrophone();
            micTest = AeroDraw.Button("Test microphone", AeroIcons.Check, AeroButtonStyle.Secondary, 175, 34);
            micTest.Click += async (s, e) => await TestMicrophone();
            var refresh = AeroDraw.Button("", AeroIcons.Refresh, AeroButtonStyle.Ghost, 40, 34);
            refresh.Click += (s, e) => LoadDevices();
            micPlay = AeroDraw.Button("Play recording", AeroIcons.Play, AeroButtonStyle.Ghost, 165, 30);
            micPlay.Enabled = false;
            micPlay.Click += (s, e) => PlayMicTest();
            var privacy = AeroDraw.Button("Privacy settings", AeroIcons.Settings, AeroButtonStyle.Ghost, 185, 30);
            privacy.Click += (s, e) => OpenPrivacySettings();
            levelText.MaximumSize = new Size(InnerW, 0);
            micStatus.MaximumSize = new Size(InnerW, 0);
            mf.Controls.Add(AeroDraw.Caption("Input device", 9f));
            mf.Controls.Add(devices);
            mf.Controls.Add(Row(p, 42, micToggle, micTest, refresh));
            mf.Controls.Add(AeroDraw.Caption("Input level", 9f));
            mf.Controls.Add(level);
            mf.Controls.Add(levelText);
            mf.Controls.Add(Row(p, 38, micPlay, privacy));
            mf.Controls.Add(micStatus);
            mic.Controls.Add(mf);

            // ---------------- speech recognition
            var rec = new AeroCard { Title = "Speech recognition", Glyph = AeroIcons.Mic, Width = CardW, Height = 430 };
            var rf = Column(p);
            rf.Controls.Add(Row(p, 74, engineTile, listenTile));
            btnStart = AeroDraw.Button("Start listening", AeroIcons.Play, AeroButtonStyle.Primary, 180, 34);
            btnStart.Click += (s, e) => StartListening();
            btnStop = AeroDraw.Button("Stop listening", AeroIcons.Stop, AeroButtonStyle.Secondary, 180, 34);
            btnStop.Click += (s, e) => VoiceCommandEngine.Instance.StopListening();
            btnTestMode = AeroDraw.Button("Enable Test Mode", AeroIcons.Check, AeroButtonStyle.Secondary, 175, 34);
            btnTestMode.Click += (s, e) => ToggleTestMode();
            btnTestRec = AeroDraw.Button("Test voice recognition", AeroIcons.Mic, AeroButtonStyle.Secondary, 208, 34);
            btnTestRec.Click += (s, e) => StartRecognitionTest(VoiceTestTarget.Free);
            btnModel = AeroDraw.Button("Download speech model (41 MB)", AeroIcons.Save, AeroButtonStyle.Primary, 290, 32);
            btnModel.Click += async (s, e) => await DownloadModel();
            partial.MaximumSize = new Size(InnerW, 0);
            partial.ForeColor = p.Muted;
            partial.Font = AeroDraw.UiFont(10f, FontStyle.Italic);
            heard.MaximumSize = new Size(InnerW, 0);
            heard.Font = AeroDraw.UiFont(12f, FontStyle.Bold);
            recStatus.MaximumSize = new Size(InnerW, 0);
            rf.Controls.Add(Row(p, 42, btnStart, btnStop));
            rf.Controls.Add(Row(p, 42, btnTestMode, btnTestRec));
            rf.Controls.Add(btnModel);
            rf.Controls.Add(AeroDraw.Caption("Live text (while speaking - never acted on)", 9f));
            rf.Controls.Add(partial);
            rf.Controls.Add(AeroDraw.Caption("Recognised transcript", 9f));
            rf.Controls.Add(heard);
            rf.Controls.Add(recStatus);
            rec.Controls.Add(rf);

            // ---------------- command detection & validation
            var det = new AeroCard { Title = "Command detection & validation", Glyph = AeroIcons.Check, Width = CardW, Height = 370 };
            var df = Column(p);
            confText.MaximumSize = new Size(InnerW, 0);
            validation.MaximumSize = new Size(InnerW, 0);
            var help = AeroDraw.Caption(
                "ARM \"arm the drone\" • DISARM \"disarm the drone\" • TAKEOFF \"take off\" • STOP \"stop\" • " +
                "HOLD \"hold position\" • RTL \"return to launch\" • LAND \"land\". \"Drone ...\" and \"please ...\" are fine.\n" +
                "Rejected: negations (\"do not arm\"), two commands (\"disarm and arm\"), questions, other sentences " +
                "(\"my arm hurts\"), low confidence - you are asked to repeat.", 8.5f);
            help.MaximumSize = new Size(InnerW, 0);
            df.Controls.Add(detectTile);
            df.Controls.Add(AeroDraw.Caption("Recognition confidence (command words)", 9f));
            df.Controls.Add(confMeter);
            df.Controls.Add(confText);
            df.Controls.Add(AeroDraw.Caption("Validation result", 9f));
            df.Controls.Add(validation);
            df.Controls.Add(help);
            det.Controls.Add(df);

            // ---------------- execution
            var exe = new AeroCard { Title = "Execution", Glyph = AeroIcons.Power, Width = CardW, Height = 370 };
            var ef = Column(p);
            execInfo.MaximumSize = new Size(InnerW, 0);
            testBanner.MaximumSize = new Size(InnerW, 0);
            testBanner.Font = AeroDraw.UiFont(10f, FontStyle.Bold);
            var check = AeroDraw.Button("Readiness checks", AeroIcons.Check, AeroButtonStyle.Secondary, 170, 34);
            check.Click += (s, e) => ShowChecks();
            btnArm = AeroDraw.Button("Arm", AeroIcons.Unlock, AeroButtonStyle.Primary, 100, 34);
            btnArm.Click += async (s, e) => await FlightActions.ArmWithConfirmation();
            btnDisarm = AeroDraw.Button("Disarm", AeroIcons.Lock, AeroButtonStyle.Danger, 110, 34);
            btnDisarm.Click += async (s, e) => await FlightActions.DisarmWithConfirmation();
            ef.Controls.Add(testBanner);
            ef.Controls.Add(execTile);
            ef.Controls.Add(execInfo);
            ef.Controls.Add(Row(p, 44, check, btnArm, btnDisarm));
            exe.Controls.Add(ef);

            // ---------------- test mode
            var test = new AeroCard { Title = "Voice recognition test (Test Mode - nothing is sent to the vehicle)", Glyph = AeroIcons.Mic, Width = WideW, Height = 420 };
            var tf = Column(p);
            var tb = new FlowLayoutPanel { Width = WideW - 30, Height = 44, BackColor = p.Surface, Margin = new Padding(0), WrapContents = false };
            foreach (var t in new[]
                     {
                         VoiceTestTarget.Arm, VoiceTestTarget.Disarm, VoiceTestTarget.Takeoff, VoiceTestTarget.Stop,
                         VoiceTestTarget.Hold, VoiceTestTarget.Rtl, VoiceTestTarget.Land, VoiceTestTarget.Reject
                     })
            {
                var target = t;
                var label = t == VoiceTestTarget.Reject ? "NEGATED (reject)" : VoiceTestSession.Label(t);
                var b = AeroDraw.Button(label, "", AeroButtonStyle.Secondary, t == VoiceTestTarget.Reject ? 165 : label.Length > 4 ? 105 : 80, 34);
                b.Margin = new Padding(0, 0, 6, 6);
                b.Click += (s, e) => StartRecognitionTest(target);
                testButtons[t] = b;
                tb.Controls.Add(b);
            }

            testPrompt.MaximumSize = new Size(WideW - 30, 0);
            testPrompt.Font = AeroDraw.UiFont(11f, FontStyle.Bold);
            testPrompt.ForeColor = p.Accent;
            lastAttempt.MaximumSize = new Size(WideW - 30, 0);
            testTable = new ListView
            {
                View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, Width = WideW - 30,
                Height = 190, BackColor = p.Surface, ForeColor = p.Text, BorderStyle = BorderStyle.None, Font = AeroDraw.UiFont(9.5f)
            };
            foreach (var (h, w) in new[] { ("Command", 110), ("Attempts", 80), ("Correct", 80), ("Accuracy", 90), ("Wrong command", 120), ("Missed / repeat", 120) })
                testTable.Columns.Add(h, w);
            var reset = AeroDraw.Button("Reset results", AeroIcons.Refresh, AeroButtonStyle.Ghost, 150, 32);
            reset.Click += (s, e) =>
            {
                VoiceTestSession.Instance.Clear();
                RefreshTestTable();
                lastAttempt.Text = "";
            };
            var save = AeroDraw.Button("Save accuracy report", AeroIcons.Save, AeroButtonStyle.Secondary, 200, 32);
            save.Click += (s, e) => SaveReport();
            var folder = AeroDraw.Button("Open reports folder", AeroIcons.Folder, AeroButtonStyle.Ghost, 190, 32);
            folder.Click += (s, e) => OpenFolder(VoiceTestSession.ReportsDirectory);
            tf.Controls.Add(AeroDraw.Caption("Click the command you will say, then say it as often as you like (each attempt is scored):", 9f));
            tf.Controls.Add(tb);
            tf.Controls.Add(testPrompt);
            tf.Controls.Add(lastAttempt);
            tf.Controls.Add(testTable);
            tf.Controls.Add(Row(p, 40, WideW - 30, reset, save, folder));
            test.Controls.Add(tf);

            // ---------------- log
            var lg = new AeroCard { Title = "Voice log (recognised text, decisions, results)", Glyph = AeroIcons.Telemetry, Width = WideW, Height = 230 };
            log = new ListBox
            {
                Dock = DockStyle.Fill, BackColor = p.Surface, ForeColor = p.Text, BorderStyle = BorderStyle.None,
                Font = AeroDraw.UiFont(9f), HorizontalScrollbar = true
            };
            lg.Controls.Add(log);

            var note = AeroDraw.Caption(
                "Voice never flies the vehicle on its own: ARM, DISARM and TAKEOFF always need an explicit Yes in a confirmation dialog " +
                "(RTL / LAND by default, STOP / HOLD optional - Settings › Voice), DISARM is refused while airborne, TAKEOFF requires an armed " +
                "vehicle, and results are verified from the vehicle's MAVLink ACK and reported state. Recognition runs offline on this PC; " +
                "no audio is stored or sent anywhere. Log file: " + VoiceLog.FilePath, 8.5f);
            note.MaximumSize = new Size(WideW, 0);

            flow.Controls.AddRange(new Control[] { mic, rec, det, exe, test, lg, note });
            Body.Controls.Add(flow);

            ui.Tick += (s, e) => RefreshUi();
            VoiceCommandEngine.Instance.UtteranceRecognized += u =>
                AddLog($"Heard \"{u.Text}\" ({u.Confidence:P0}) → {u.Decision}");
            VoiceCommandController.Instance.Progress += (m, l) => AddLog("   " + m);
            VoiceTestSession.Instance.AttemptRecorded += a => AeroDraw.SafeInvoke(this, () => ShowAttempt(a));
            RefreshTestTable();
            UpdatePrompt();
        }

        private static FlowLayoutPanel Column(AeroPalette p) => new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = p.Surface
        };

        private static FlowLayoutPanel Row(AeroPalette p, int height, params Control[] controls) => Row(p, height, InnerW + 10, controls);

        private static FlowLayoutPanel Row(AeroPalette p, int height, int width, params Control[] controls)
        {
            var r = new FlowLayoutPanel { Width = width, Height = height, BackColor = p.Surface, Margin = new Padding(0), WrapContents = false };
            r.Controls.AddRange(controls);
            return r;
        }

        // ------------------------------------------------------------------ microphone

        private void OnDeviceChanged()
        {
            if (devices.SelectedIndex < 0 || devices.SelectedIndex >= deviceList.Count) return;
            var name = deviceList[devices.SelectedIndex].id < 0 ? "" : deviceList[devices.SelectedIndex].name;
            if (name == MicrophoneManager.SelectedDeviceName) return;
            MicrophoneManager.SelectedDeviceName = name;
            AeroSettings.Save();
            AddLog("Microphone selected: " + (name.Length == 0 ? "system default" : name));
            if (!MicrophoneManager.Instance.IsOn) return;
            var wasListening = VoiceCommandEngine.Instance.Listening;
            VoiceCommandEngine.Instance.StopListening("Switching microphone...");
            MicrophoneManager.Instance.TurnOff();
            if (MicrophoneManager.Instance.TurnOn() && wasListening)
                VoiceCommandEngine.Instance.StartListening();
        }

        private void ToggleMicrophone()
        {
            if (MicrophoneManager.Instance.IsOn)
            {
                VoiceCommandEngine.Instance.StopListening("Microphone switched off.");
                MicrophoneManager.Instance.TurnOff();
            }
            else
            {
                MicrophoneManager.Instance.TurnOn();
            }
        }

        private async Task TestMicrophone()
        {
            if (micTesting) return;
            micTesting = true;
            micTest.Enabled = false;
            AddLog("Microphone test: recording 4 s - say \"arm the drone, take off, land\" at your normal speaking volume...");
            micStatus.Text = "Recording 4 s - speak a few commands at your normal volume...";
            try
            {
                var r = await MicrophoneManager.Instance.RecordTestAsync(4);
                lastMicTest = r;
                var (text, ok) = r.Verdict();
                micStatus.Text = "Microphone test: " + text;
                micStatus.ForeColor = ok ? BrandingManager.Palette.StatusOk : BrandingManager.Palette.StatusCritical;
                micPlay.Enabled = r.AnySignal;
                micTestShownUntil = DateTime.UtcNow.AddSeconds(30);
                AddLog("Microphone test: " + text);
                VoiceLog.Write($"mic test device=\"{CurrentDeviceName()}\" speech={r.SpeechDb:0.0} peak={r.PeakDb:0.0} noise={r.NoiseDb:0.0} clip={r.ClipPercent:0.00}% -> {text}");
            }
            catch (Exception ex)
            {
                micStatus.Text = "Microphone test failed: " + ex.Message;
                micTestShownUntil = DateTime.UtcNow.AddSeconds(30);
                micStatus.ForeColor = BrandingManager.Palette.StatusCritical;
                AddLog("Microphone test failed: " + ex.Message);
            }
            finally
            {
                micTesting = false;
                micTest.Enabled = true;
            }
        }

        private void PlayMicTest()
        {
            if (lastMicTest?.Wav == null) return;
            try
            {
                using (var ms = new System.IO.MemoryStream(lastMicTest.Wav))
                using (var player = new System.Media.SoundPlayer(ms))
                    player.Play();
            }
            catch (Exception ex)
            {
                AddLog("Playback failed: " + ex.Message);
            }
        }

        private static void OpenPrivacySettings()
        {
            try
            {
                Process.Start("ms-settings:privacy-microphone");
            }
            catch (Exception)
            {
                // older Windows
            }
        }

        private static void OpenFolder(string dir)
        {
            try
            {
                System.IO.Directory.CreateDirectory(dir);
                Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch (Exception)
            {
                // ignore
            }
        }

        private static string CurrentDeviceName() =>
            string.IsNullOrEmpty(MicrophoneManager.SelectedDeviceName) ? "System default microphone" : MicrophoneManager.SelectedDeviceName;

        // ------------------------------------------------------------------ recognition / test mode

        private void StartListening()
        {
            if (!VoiceCommandEngine.Instance.StartListening())
                AddLog("Cannot start listening: " + VoiceCommandEngine.Instance.Status);
            else if (VoiceCommandEngine.Instance.EngineWarning.Length > 0)
                AddLog("⚠ " + VoiceCommandEngine.Instance.EngineWarning);
        }

        private void ToggleTestMode()
        {
            if (VoiceSettings.TestMode)
            {
                var ok = FlightActions.Confirm("Disable voice Test Mode",
                    "Leave Test Mode?\n\nRecognised voice commands will then be validated and - after your confirmation in a " +
                    "dialog where required - SENT TO THE CONNECTED VEHICLE.\n\nOnly continue after the recognition test showed " +
                    "reliable results for your voice and microphone.");
                if (!ok) return;
                VoiceSettings.TestMode = false;
                AddLog("Test Mode OFF - voice commands can now be sent to the vehicle (with confirmation).");
            }
            else
            {
                VoiceSettings.TestMode = true;
                AddLog("Test Mode ON - nothing is sent to the vehicle.");
            }

            AeroSettings.Save();
        }

        private void StartRecognitionTest(VoiceTestTarget target)
        {
            if (!VoiceSettings.TestMode)
            {
                VoiceSettings.TestMode = true;
                AeroSettings.Save();
                AddLog("Test Mode ON for the recognition test - nothing is sent to the vehicle.");
            }

            VoiceTestSession.Instance.SetTarget(target);
            UpdatePrompt();
            if (!VoiceCommandEngine.Instance.Listening)
                StartListening();
        }

        private void UpdatePrompt()
        {
            var t = VoiceTestSession.Instance.Target;
            testPrompt.Text = (t == VoiceTestTarget.Free ? "" : "Testing " + VoiceTestSession.Label(t) + ": ") + VoiceTestSession.Prompt(t);
            var p = BrandingManager.Palette;
            foreach (var kv in testButtons)
                kv.Value.ButtonStyle = kv.Key == t ? AeroButtonStyle.Primary : AeroButtonStyle.Secondary;
        }

        private void ShowAttempt(VoiceTestAttempt a)
        {
            var p = BrandingManager.Palette;
            var result = a.Correct == null ? "" : a.Correct.Value ? "CORRECT" : a.WrongCommand ? "WRONG COMMAND" : "MISSED - please repeat";
            lastAttempt.Text = $"Heard \"{a.Text}\" ({a.Confidence:P0}) → detected {a.Detected}  {result}";
            lastAttempt.ForeColor = a.Correct == null ? p.Text : a.Correct.Value ? p.StatusOk : a.WrongCommand ? p.StatusCritical : p.StatusWarn;
            RefreshTestTable();
        }

        private void RefreshTestTable()
        {
            var stats = VoiceTestSession.Instance.Stats();
            testTable.BeginUpdate();
            testTable.Items.Clear();
            int n = 0, c = 0, w = 0;
            foreach (var t in Enum.GetValues(typeof(VoiceTestTarget)).Cast<VoiceTestTarget>().Where(t => t != VoiceTestTarget.Free))
            {
                stats.TryGetValue(t, out var s);
                n += s.n;
                c += s.correct;
                w += s.wrong;
                testTable.Items.Add(new ListViewItem(new[]
                {
                    VoiceTestSession.Label(t), s.n.ToString(), s.correct.ToString(), s.n == 0 ? "-" : (100.0 * s.correct / s.n).ToString("0") + " %",
                    s.wrong.ToString(), s.missed.ToString()
                }));
            }

            testTable.Items.Add(new ListViewItem(new[] { "ALL", n.ToString(), c.ToString(), n == 0 ? "-" : (100.0 * c / n).ToString("0") + " %", w.ToString(), (n - c - w).ToString() }));
            testTable.EndUpdate();
        }

        private void SaveReport()
        {
            try
            {
                var path = VoiceTestSession.Instance.SaveReport(CurrentDeviceName(),
                    VoiceCommandEngine.Instance.EngineName.Length > 0 ? VoiceCommandEngine.Instance.EngineName : VoiceCommandEngine.DescribeEngine().name);
                AddLog("Accuracy report saved: " + path);
                CustomMessageBox.Show("Accuracy report saved:\n" + path, BrandingManager.AppName);
            }
            catch (Exception ex)
            {
                AddLog("Saving the report failed: " + ex.Message);
            }
        }

        private async Task DownloadModel()
        {
            if (download != null)
            {
                download.Cancel();
                return;
            }

            download = new CancellationTokenSource();
            btnModel.Text = "Cancel download";
            AddLog("Downloading the offline speech model from " + VoskModelStore.ModelUrl + " ...");
            try
            {
                var progress = new Progress<double>(f => downloadText = $"Downloading speech model... {f:P0}");
                var dir = await VoskModelStore.DownloadAsync(progress, download.Token);
                downloadText = "";
                AddLog("Speech model installed: " + dir);
                await VoiceCommandEngine.PreloadAsync();
                if (VoiceCommandEngine.Instance.Listening)
                {
                    VoiceCommandEngine.Instance.StopListening("Switching to the offline Vosk engine...");
                    StartListening();
                }
            }
            catch (OperationCanceledException)
            {
                downloadText = "";
                AddLog("Model download cancelled.");
            }
            catch (Exception ex)
            {
                downloadText = "";
                AddLog("Model download failed: " + ex.Message);
            }
            finally
            {
                download = null;
                btnModel.Text = "Download speech model (41 MB)";
            }
        }

        private void AddLog(string m)
        {
            AeroDraw.SafeInvoke(this, () =>
            {
                log.Items.Insert(0, DateTime.Now.ToString("HH:mm:ss") + "  " + m);
                while (log.Items.Count > 300) log.Items.RemoveAt(log.Items.Count - 1);
            });
        }

        private void ShowChecks()
        {
            var t = TelemetryManager.Capture();
            foreach (var cmd in VoiceCommandParser.AllCommands)
            {
                var passed = new List<string>();
                var fails = VoiceSafety.Check(cmd, t, passed);
                var name = VoiceCommandParser.Name(cmd);
                AddLog(fails.Count == 0
                    ? $"Readiness {name}: READY ({string.Join(", ", passed)})"
                    : $"Readiness {name}: BLOCKED - {string.Join(" ", fails)}");
            }
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
            if (deviceList.Count <= 1)
                AddLog("No microphone found. Connect a microphone and press refresh.");
        }

        protected override void OnScreenActivated()
        {
            LoadDevices();
            engineInfo = null;
            VoiceCommandEngine.PreloadAsync();
            ui.Start();
        }

        protected override void OnScreenDeactivated() => ui.Stop();

        private static void SetText(Label l, string text)
        {
            if (l.Text != text) l.Text = text;
        }

        private (string name, string warning)? engineInfo;
        private DateTime engineInfoUtc;

        private void RefreshUi()
        {
            var p = BrandingManager.Palette;
            var mic = MicrophoneManager.Instance;
            var v = VoiceCommandEngine.Instance;
            var c = VoiceCommandController.Instance;
            var testMode = VoiceSettings.TestMode;

            // ---- microphone
            micToggle.Text = mic.IsOn ? "Microphone OFF" : "Microphone ON";
            micToggle.Glyph = mic.IsOn ? AeroIcons.MicOff : AeroIcons.Mic;
            micToggle.ButtonStyle = mic.IsOn ? AeroButtonStyle.Secondary : AeroButtonStyle.Primary;
            micTile.SetValue(mic.HasError ? "ERROR" : mic.IsOn ? "ON" : "OFF",
                mic.HasError ? StatusLevel.Critical : mic.IsOn ? StatusLevel.Ok : StatusLevel.Unavailable);
            if (!mic.IsOn)
                signalTile.SetValue("-", StatusLevel.Unavailable);
            else if (mic.ClippingRecently)
                signalTile.SetValue("CLIPPING", StatusLevel.Critical);
            else if (mic.SilentTooLong || !mic.ReceivingAudio)
                signalTile.SetValue("NO SIGNAL", StatusLevel.Critical);
            else
                signalTile.SetValue(v.SpeechActive ? "SPEECH" : "YES", v.SpeechActive ? StatusLevel.Warning : StatusLevel.Ok);
            level.Value = mic.IsOn ? mic.Level : 0;
            level.BarColor = mic.ClippingRecently || mic.PeakDb > -1 ? p.StatusCritical : p.Accent;
            var floor = v.NoiseFloorDb;
            var speech = v.LastSpeechDb;
            SetText(levelText, mic.IsOn
                ? $"Level {mic.LevelDb:0} dBFS • peak {mic.PeakDb:0} dBFS" +
                  (floor.HasValue ? $" • background {floor.Value:0} dBFS" : "") +
                  (speech.HasValue && floor.HasValue ? $" • last speech {speech.Value:0} dBFS (SNR {speech.Value - floor.Value:0} dB)" : "") +
                  (v.LastGainDb > 0.5 ? $" • auto gain +{v.LastGainDb:0} dB" : "") +
                  "\nGood speech level: -30 to -12 dBFS, no red bar."
                : "Microphone off.");
            if (!micTesting && DateTime.UtcNow > micTestShownUntil)
            {
                SetText(micStatus, "Selected: " + CurrentDeviceName() + "\n" + mic.Status + (mic.SilentTooLong
                    ? "\nNo signal from the microphone. Check Windows Settings › Privacy & security › Microphone " +
                      "(\"Let desktop apps access your microphone\") and that the device is not muted."
                    : ""));
                micStatus.ForeColor = mic.HasError || mic.SilentTooLong ? p.StatusCritical : mic.IsOn ? p.Text : p.Muted;
            }

            // ---- speech recognition
            if (engineInfo == null || (!v.Listening && (DateTime.UtcNow - engineInfoUtc).TotalSeconds > 5))
            {
                engineInfo = VoiceCommandEngine.DescribeEngine();
                engineInfoUtc = DateTime.UtcNow;
            }

            var engineName = v.Listening ? v.EngineName : engineInfo.Value.name;
            var warning = v.Listening ? v.EngineWarning : engineInfo.Value.warning;
            var isVosk = engineName.StartsWith("Vosk");
            engineTile.SetValue(isVosk ? "VOSK OFFLINE" : "WINDOWS SPEECH", isVosk ? StatusLevel.Ok : StatusLevel.Warning);
            listenTile.SetValue(!v.Listening ? "NOT LISTENING" : v.SpeechActive ? "HEARING SPEECH" : "LISTENING",
                !v.Listening ? StatusLevel.Unavailable : v.SpeechActive ? StatusLevel.Warning : StatusLevel.Ok);
            btnStart.Enabled = VoiceSettings.Enabled && !v.Listening;
            btnStop.Enabled = v.Listening;
            btnTestMode.Text = testMode ? "Disable Test Mode" : "Enable Test Mode";
            btnTestMode.ButtonStyle = testMode ? AeroButtonStyle.Primary : AeroButtonStyle.Secondary;
            btnModel.Visible = VoskModelStore.FindModel() == null || download != null;
            SetText(partial, v.Listening ? (string.IsNullOrEmpty(v.PartialText) ? (v.SpeechActive ? "..." : "—") : v.PartialText) : "—");
            var u = v.LastUtterance;
            SetText(heard, u == null ? "—" : "\"" + u.Text + "\"");
            var timeout = v.SecondsRemaining;
            SetText(recStatus, "Engine: " + engineName + "\n" + v.Status +
                               (timeout >= 0 ? $"  (auto-stop in {timeout:0} s)" : "") +
                               (warning.Length > 0 ? "\n⚠ " + warning : "") +
                               (v.SignalProblem.Length > 0 ? "\n⚠ " + v.SignalProblem : "") +
                               (downloadText.Length > 0 ? "\n" + downloadText : ""));
            recStatus.ForeColor = warning.Length > 0 || v.SignalProblem.Length > 0 ? p.StatusWarn : p.Muted;

            // ---- command detection / validation
            if (u != null && u.Id != shownUtterance)
            {
                shownUtterance = u.Id;
                var m = u.Match;
                string headline;
                StatusLevel lvl;
                if (m.IsCommand && !u.ConfidenceOk)
                {
                    headline = VoiceCommandParser.Name(m.Command) + "? LOW CONFIDENCE - REPEAT";
                    lvl = StatusLevel.Warning;
                }
                else
                {
                    headline = m.Headline;
                    lvl = m.IsCommand ? StatusLevel.Ok : m.Outcome == VoiceMatchOutcome.NoCommand ? StatusLevel.Unavailable : StatusLevel.Warning;
                }

                detectTile.SetValue(headline, lvl);
                confMeter.Value = u.Confidence;
                confMeter.BarColor = u.ConfidenceOk ? p.StatusOk : p.StatusCritical;
                SetText(confText, $"{u.Confidence:P0} - minimum {VoiceSettings.MinConfidence:P0}" + (u.ConfidenceOk ? "" : " (too low)"));
                SetText(validation, u.Decision + $"\nNormalised: \"{m.Normalized}\"");
                validation.ForeColor = u.Actionable ? p.StatusOk : m.Outcome == VoiceMatchOutcome.NoCommand ? p.Muted : p.StatusWarn;
            }
            else if (u == null)
            {
                detectTile.SetValue("NO COMMAND DETECTED", StatusLevel.Unavailable);
                confMeter.Value = 0;
                SetText(confText, $"Minimum for commands: {VoiceSettings.MinConfidence:P0}");
                SetText(validation, "Nothing recognised yet.");
            }

            // ---- execution
            SetText(testBanner, testMode
                ? "TEST MODE ON - recognised commands are shown and scored but NEVER sent to the vehicle."
                : "LIVE - recognised commands are validated and, after confirmation, sent to the vehicle.");
            testBanner.ForeColor = testMode ? p.Accent : p.StatusCritical;
            if (testMode)
            {
                execTile.SetValue("TEST MODE - NOT SENT", StatusLevel.Warning);
                SetText(execInfo, u != null && u.TestMode && u.Actionable
                    ? $"{VoiceCommandParser.Name(u.AcceptedCommand)} would now be validated and confirmed. Nothing was sent."
                    : "No MAVLink commands are sent in Test Mode.");
            }
            else
            {
                execTile.SetValue(c.State == VoiceExecState.Idle
                        ? "IDLE"
                        : (c.CurrentCommand != VoiceCommand.None ? VoiceCommandParser.Name(c.CurrentCommand) + ": " : "") +
                          VoiceCommandController.Title(c.State).ToUpperInvariant(),
                    VoiceCommandController.LevelOf(c.State));
                SetText(execInfo, c.StateText);
            }

            var t = TelemetryManager.Latest.HasHeartbeat ? TelemetryManager.Latest : TelemetryManager.Capture();
            var busy = FlightControlManager.Instance.CommandInFlight || c.Busy;
            btnArm.Enabled = t.HasHeartbeat && !t.Armed && !busy;
            btnDisarm.Enabled = t.HasHeartbeat && t.Armed && !busy;
        }
    }
}
