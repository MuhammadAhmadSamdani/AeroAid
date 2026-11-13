using System;
using System.Linq;
using System.Reflection;
using log4net;

namespace MissionPlanner.AeroAid.Audio
{
    /// <summary>
    /// The voice-command microphone (PC microphone). Independent of the camera's audio.
    /// Provides ON/OFF, device selection, an input level and PCM data for the speech recogniser.
    /// </summary>
    public sealed class MicrophoneManager
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        public static MicrophoneManager Instance { get; } = new MicrophoneManager();

        private readonly object sync = new object();
        private WaveInCapture capture;
        private DateTime lastSignalUtc = DateTime.MinValue;
        private DateTime startedUtc;

        public bool IsOn { get; private set; }
        public string Status { get; private set; } = "Microphone off.";
        public bool HasError { get; private set; }

        /// <summary>Smoothed input level 0..1 (-60 dBFS .. 0 dBFS).</summary>
        public float Level { get; private set; }

        public event Action StateChanged;

        /// <summary>16 kHz 16-bit mono PCM (capture thread).</summary>
        public event Action<byte[]> AudioData;

        /// <summary>Selected device name ("" = system default). Stored by name so it survives re-plugging.</summary>
        public static string SelectedDeviceName
        {
            get => AeroSettings.GetString("voice.mic_device", "");
            set => AeroSettings.SetString("voice.mic_device", value ?? "");
        }

        private static int ResolveDeviceId()
        {
            var name = SelectedDeviceName;
            if (string.IsNullOrEmpty(name)) return -1;
            var match = WaveInCapture.Devices().FirstOrDefault(d => d.id >= 0 && d.name == name);
            return match.name == null ? -1 : match.id;
        }

        private void SetStatus(string s, bool error)
        {
            Status = s;
            HasError = error;
            if (error) log.Warn("Microphone: " + s);
            try
            {
                StateChanged?.Invoke();
            }
            catch (Exception ex)
            {
                log.Error("Microphone StateChanged handler failed", ex);
            }
        }

        public bool TurnOn()
        {
            lock (sync)
            {
                if (IsOn) return true;
                var id = ResolveDeviceId();
                if (!string.IsNullOrEmpty(SelectedDeviceName) && id < 0)
                {
                    SetStatus($"Selected microphone \"{SelectedDeviceName}\" was not found. Choose another device.", true);
                    return false;
                }

                var c = new WaveInCapture(id);
                c.DataAvailable += OnData;
                // raised on the capture thread: stop from a worker (Stop joins the capture thread)
                c.Failed += msg => System.Threading.Tasks.Task.Run(() =>
                {
                    TurnOff();
                    SetStatus(msg, true);
                });
                try
                {
                    c.Start();
                }
                catch (Exception ex)
                {
                    c.Dispose();
                    SetStatus(ex.Message, true);
                    return false;
                }

                capture = c;
                IsOn = true;
                startedUtc = DateTime.UtcNow;
                lastSignalUtc = DateTime.MinValue;
            }

            SetStatus("Microphone on (" + (string.IsNullOrEmpty(SelectedDeviceName) ? "system default" : SelectedDeviceName) + ").", false);
            return true;
        }

        public void TurnOff()
        {
            WaveInCapture c;
            lock (sync)
            {
                c = capture;
                capture = null;
                IsOn = false;
                Level = 0;
            }

            if (c != null)
            {
                c.DataAvailable -= OnData;
                try
                {
                    c.Stop();
                }
                catch (Exception ex)
                {
                    log.Error("Stopping microphone failed", ex);
                }
            }

            SetStatus("Microphone off.", false);
        }

        /// <summary>True if the microphone has been on for a while but delivered only digital silence
        /// (typical when Windows privacy settings block desktop apps from the microphone).</summary>
        public bool SilentTooLong =>
            IsOn && (DateTime.UtcNow - startedUtc).TotalSeconds > 5 &&
            (lastSignalUtc == DateTime.MinValue || (DateTime.UtcNow - lastSignalUtc).TotalSeconds > 30);

        private void OnData(byte[] pcm)
        {
            double sum = 0;
            var samples = pcm.Length / 2;
            var anyNonZero = false;
            for (var i = 0; i + 1 < pcm.Length; i += 2)
            {
                var s = (short)(pcm[i] | (pcm[i + 1] << 8));
                if (s != 0) anyNonZero = true;
                sum += (double)s * s;
            }

            if (anyNonZero) lastSignalUtc = DateTime.UtcNow;
            var rms = samples > 0 ? Math.Sqrt(sum / samples) / 32768.0 : 0;
            var db = rms > 0 ? 20 * Math.Log10(rms) : -100;
            var level = (float)Math.Max(0, Math.Min(1, (db + 60) / 60));
            Level = Level * 0.6f + level * 0.4f;
            try
            {
                AudioData?.Invoke(pcm);
            }
            catch (Exception ex)
            {
                log.Error("Audio consumer failed", ex);
            }
        }
    }
}
