using System.Linq;
using System.Windows.Forms;
using MissionPlanner.AeroAid.Audio;
using MissionPlanner.AeroAid.UI;
using MissionPlanner.AeroAid.Voice;

namespace MissionPlanner.AeroAid.Views
{
    public partial class SettingsView
    {
        private AeroCard VoiceSection()
        {
            var card = NewSection("Voice", AeroIcons.Mic, out var grid, 330);
            var enabled = AeroDraw.Check("Enable voice-initiated takeoff (always requires arm + takeoff confirmation)", true);
            var device = AeroDraw.Combo();
            device.Width = 360;
            var conf = AeroDraw.Number(75, 30, 98, 0, 5);
            var timeout = AeroDraw.Number(60, 10, 600, 0, 10);
            var cooldown = AeroDraw.Number(30, 5, 300, 0, 5);
            AeroDraw.AddRow(grid, "Voice control", enabled);
            AeroDraw.AddRow(grid, "Microphone", device);
            AeroDraw.AddRow(grid, "Min. recognition confidence", conf, "%");
            AeroDraw.AddRow(grid, "Listening timeout", timeout, "s without a command");
            AeroDraw.AddRow(grid, "Repeat cool-down", cooldown, "s between voice takeoffs");
            AeroDraw.AddRow(grid, "Recogniser", AeroDraw.Caption(VoiceCommandEngine.RecognizerInfo(), 8.5f));

            Bind(() =>
            {
                enabled.Checked = VoiceSettings.Enabled;
                device.Items.Clear();
                foreach (var d in WaveInCapture.Devices()) device.Items.Add(d.id < 0 ? "System default microphone" : d.name);
                var sel = MicrophoneManager.SelectedDeviceName;
                if (!string.IsNullOrEmpty(sel) && !device.Items.Contains(sel)) device.Items.Add(sel);
                device.SelectedItem = string.IsNullOrEmpty(sel) ? "System default microphone" : sel;
                conf.Value = (decimal)System.Math.Round(VoiceSettings.MinConfidence * 100);
                timeout.Value = VoiceSettings.ListenTimeoutSeconds;
                cooldown.Value = VoiceSettings.CooldownSeconds;
            }, () =>
            {
                VoiceSettings.Enabled = enabled.Checked;
                var selected = device.SelectedItem as string;
                MicrophoneManager.SelectedDeviceName = selected == null || selected == "System default microphone" ? "" : selected;
                VoiceSettings.MinConfidence = (double)conf.Value / 100.0;
                VoiceSettings.ListenTimeoutSeconds = (int)timeout.Value;
                VoiceSettings.CooldownSeconds = (int)cooldown.Value;
                if (!enabled.Checked)
                    VoiceCommandEngine.Instance.StopListening("Voice control disabled.");
            });
            return card;
        }
    }
}
