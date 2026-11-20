using System.Linq;
using System.Windows.Forms;
using MissionPlanner.AeroAid.Audio;
using MissionPlanner.AeroAid.UI;
using MissionPlanner.AeroAid.Voice;

namespace MissionPlanner.AeroAid.Views
{
    public partial class SettingsView
    {
        private const string EngineVoskText = "Vosk offline - command vocabulary (recommended)";
        private const string EngineWindowsText = "Windows speech (System.Speech) - fallback";
        private const string WakeOptionalText = "Optional - accepted, not required";
        private const string WakeCriticalText = "Required for ARM / DISARM / TAKEOFF (recommended near people)";
        private const string WakeAllText = "Required for every command";

        private AeroCard VoiceSection()
        {
            var card = NewSection("Voice", AeroIcons.Mic, out var grid, 600);
            var enabled = AeroDraw.Check("Enable voice commands (ARM / DISARM / TAKEOFF / STOP / HOLD / RTL / LAND)", true);
            var testMode = AeroDraw.Check("Test Mode - recognise and score commands, never send them to the vehicle", true);
            var engine = AeroDraw.Combo();
            engine.Width = 360;
            engine.Items.AddRange(new object[] { EngineVoskText, EngineWindowsText });
            var device = AeroDraw.Combo();
            device.Width = 360;
            var conf = AeroDraw.Number(70, 30, 98, 0, 5);
            var endSilence = AeroDraw.Number(650, 300, 2000, 0, 50);
            var timeout = AeroDraw.Number(0, 0, 3600, 0, 30);
            var cooldown = AeroDraw.Number(10, 5, 300, 0, 5);
            var confirmStop = AeroDraw.Check("Ask for confirmation before STOP and HOLD", false);
            var confirmRtl = AeroDraw.Check("Ask for confirmation before RTL and LAND", true);
            var wakeWord = AeroDraw.TextBox(VoiceParseOptions.DefaultWakePhrase);
            wakeWord.Width = 200;
            var wakeMode = AeroDraw.Combo();
            wakeMode.Width = 360;
            wakeMode.Items.AddRange(new object[] { WakeOptionalText, WakeCriticalText, WakeAllText });
            AeroDraw.AddRow(grid, "Voice control", enabled);
            AeroDraw.AddRow(grid, "Test Mode", testMode);
            AeroDraw.AddRow(grid, "Speech engine", engine);
            AeroDraw.AddRow(grid, "Microphone", device);
            AeroDraw.AddRow(grid, "Min. recognition confidence", conf, "% (command words)");
            AeroDraw.AddRow(grid, "End-of-command pause", endSilence, "ms of silence that ends a command");
            AeroDraw.AddRow(grid, "Auto-stop listening", timeout, "s without speech (0 = listen continuously)");
            AeroDraw.AddRow(grid, "Command cool-down", cooldown, "s before the same ARM / DISARM / TAKEOFF can be sent again");
            AeroDraw.AddRow(grid, "STOP / HOLD", confirmStop);
            AeroDraw.AddRow(grid, "RTL / LAND", confirmRtl);
            AeroDraw.AddRow(grid, "Wake word", wakeWord, "spoken before a command, e.g. \"aero aid, arm the drone\"");
            AeroDraw.AddRow(grid, "Wake word required", wakeMode);
            AeroDraw.AddRow(grid, "Offline model", AeroDraw.Caption(VoskModelStore.FindModel() ?? "not installed (Voice page › Download speech model)", 8.5f));
            AeroDraw.AddRow(grid, "Windows recognisers", AeroDraw.Caption(VoiceCommandEngine.RecognizerInfo(), 8.5f));
            AeroDraw.AddRow(grid, "", AeroDraw.Caption("ARM, DISARM and TAKEOFF always require confirmation.", 8.5f));

            Bind(() =>
            {
                enabled.Checked = VoiceSettings.Enabled;
                testMode.Checked = VoiceSettings.TestMode;
                engine.SelectedItem = VoiceSettings.Engine == VoiceSettings.EngineWindows ? EngineWindowsText : EngineVoskText;
                device.Items.Clear();
                foreach (var d in WaveInCapture.Devices()) device.Items.Add(d.id < 0 ? "System default microphone" : d.name);
                var sel = MicrophoneManager.SelectedDeviceName;
                if (!string.IsNullOrEmpty(sel) && !device.Items.Contains(sel)) device.Items.Add(sel);
                device.SelectedItem = string.IsNullOrEmpty(sel) ? "System default microphone" : sel;
                conf.Value = (decimal)System.Math.Round(VoiceSettings.MinConfidence * 100);
                endSilence.Value = VoiceSettings.EndSilenceMs;
                timeout.Value = VoiceSettings.IdleTimeoutSeconds;
                cooldown.Value = VoiceSettings.CooldownSeconds;
                confirmStop.Checked = VoiceSettings.ConfirmStopHold;
                confirmRtl.Checked = VoiceSettings.ConfirmRtlLand;
                wakeWord.Text = VoiceSettings.WakePhrase;
                var mode = VoiceSettings.RequirePrefix;
                wakeMode.SelectedItem = mode == VoicePrefixMode.Critical ? WakeCriticalText : mode == VoicePrefixMode.All ? WakeAllText : WakeOptionalText;
            }, () =>
            {
                var engineChanged = (engine.SelectedItem as string == EngineWindowsText) != (VoiceSettings.Engine == VoiceSettings.EngineWindows);
                VoiceSettings.Enabled = enabled.Checked;
                VoiceSettings.TestMode = testMode.Checked;
                VoiceSettings.Engine = engine.SelectedItem as string == EngineWindowsText ? VoiceSettings.EngineWindows : VoiceSettings.EngineVosk;
                var selected = device.SelectedItem as string;
                MicrophoneManager.SelectedDeviceName = selected == null || selected == "System default microphone" ? "" : selected;
                VoiceSettings.MinConfidence = (double)conf.Value / 100.0;
                VoiceSettings.EndSilenceMs = (int)endSilence.Value;
                VoiceSettings.IdleTimeoutSeconds = (int)timeout.Value;
                VoiceSettings.CooldownSeconds = (int)cooldown.Value;
                VoiceSettings.ConfirmStopHold = confirmStop.Checked;
                VoiceSettings.ConfirmRtlLand = confirmRtl.Checked;
                var wakeChanged = VoiceCommandParser.Normalize(wakeWord.Text) != VoiceSettings.WakePhrase;
                VoiceSettings.WakePhrase = wakeWord.Text;
                VoiceSettings.RequirePrefix = wakeMode.SelectedItem as string == WakeCriticalText ? VoicePrefixMode.Critical
                    : wakeMode.SelectedItem as string == WakeAllText ? VoicePrefixMode.All : VoicePrefixMode.Optional;
                if (!enabled.Checked)
                    VoiceCommandEngine.Instance.StopListening("Voice control disabled.");
                else if ((engineChanged || wakeChanged) && VoiceCommandEngine.Instance.Listening)
                    // the recogniser vocabulary contains the wake word: it is rebuilt when listening starts again
                    VoiceCommandEngine.Instance.StopListening((engineChanged ? "Speech engine" : "Wake word") + " changed - press Start listening.");
            });
            return card;
        }
    }
}
