using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
using log4net;
using MissionPlanner.AeroAid.Audio;

namespace MissionPlanner.AeroAid.Voice
{
    /// <summary>Voice preferences (AeroSettings "voice.*").</summary>
    public static class VoiceSettings
    {
        public static bool Enabled
        {
            get => AeroSettings.GetBool("voice.enabled", true);
            set => AeroSettings.SetBool("voice.enabled", value);
        }

        /// <summary>Minimum recogniser confidence (0..1) for a command to be accepted.</summary>
        public static double MinConfidence
        {
            get => AeroSettings.GetDouble("voice.min_conf", 0.75, 0.3, 0.98);
            set => AeroSettings.SetDouble("voice.min_conf", value);
        }

        /// <summary>Listening stops automatically after this many seconds without a command.</summary>
        public static int ListenTimeoutSeconds
        {
            get => AeroSettings.GetInt("voice.timeout", 60, 10, 600);
            set => AeroSettings.SetInt("voice.timeout", value);
        }

        /// <summary>Minimum seconds between two voice takeoff workflows.</summary>
        public static int CooldownSeconds
        {
            get => AeroSettings.GetInt("voice.cooldown", 30, 5, 300);
            set => AeroSettings.SetInt("voice.cooldown", value);
        }
    }

    /// <summary>
    /// Offline command recognition with the Windows speech recogniser (System.Speech) restricted to a small
    /// fixed grammar, fed from the selected microphone. It only REPORTS recognised commands; it never acts.
    /// </summary>
    public sealed class VoiceCommandEngine
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        public static VoiceCommandEngine Instance { get; } = new VoiceCommandEngine();

        public static readonly string[] TakeoffPhrases =
        {
            "take off", "takeoff", "drone take off", "drone takeoff", "initiate takeoff", "initiate take off"
        };

        private SpeechRecognitionEngine engine;
        private PcmPipeStream stream;
        private DateTime listenUntilUtc;
        private readonly System.Threading.Timer timeoutTimer;

        public bool Listening { get; private set; }
        public string Status { get; private set; } = "Voice control off.";
        public string LastHeard { get; private set; } = "";
        public double SecondsRemaining => Listening ? Math.Max(0, (listenUntilUtc - DateTime.UtcNow).TotalSeconds) : 0;

        /// <summary>(command, recognised text, confidence) for accepted commands. Engine thread.</summary>
        public event Action<string, string, float> CommandRecognized;

        /// <summary>Status changes (engine/timer threads).</summary>
        public event Action StateChanged;

        private VoiceCommandEngine()
        {
            timeoutTimer = new System.Threading.Timer(_ => CheckTimeout(), null, 1000, 1000);
        }

        private void SetStatus(string s)
        {
            Status = s;
            log.Info("Voice: " + s);
            try
            {
                StateChanged?.Invoke();
            }
            catch (Exception ex)
            {
                log.Error("Voice StateChanged handler failed", ex);
            }
        }

        public static string RecognizerInfo()
        {
            try
            {
                var r = SpeechRecognitionEngine.InstalledRecognizers();
                return r.Count == 0 ? "none installed" : string.Join(", ", r.Select(x => x.Culture.Name + " (" + x.Name + ")"));
            }
            catch (Exception ex)
            {
                return "unavailable: " + ex.Message;
            }
        }

        /// <summary>Starts listening (turns the microphone on). Returns false with Status explaining why.</summary>
        public bool StartListening()
        {
            if (Listening) return true;
            if (!VoiceSettings.Enabled)
            {
                SetStatus("Voice control is disabled in Settings › Voice.");
                return false;
            }

            RecognizerInfo recognizer;
            try
            {
                var all = SpeechRecognitionEngine.InstalledRecognizers();
                recognizer = all.FirstOrDefault(r => r.Culture.Name == "en-US") ??
                             all.FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName == "en");
            }
            catch (Exception ex)
            {
                SetStatus("Windows speech recognition is unavailable: " + ex.Message);
                return false;
            }

            if (recognizer == null)
            {
                SetStatus("No English speech recogniser is installed. Install one in Windows Settings › Time & language › Speech.");
                return false;
            }

            if (!MicrophoneManager.Instance.IsOn && !MicrophoneManager.Instance.TurnOn())
            {
                SetStatus("Cannot listen: " + MicrophoneManager.Instance.Status);
                return false;
            }

            try
            {
                engine = new SpeechRecognitionEngine(recognizer);
                var gb = new GrammarBuilder(new Choices(TakeoffPhrases)) { Culture = recognizer.Culture };
                engine.LoadGrammar(new Grammar(gb) { Name = "takeoff" });
                engine.SpeechRecognized += OnRecognized;
                engine.SpeechRecognitionRejected += (s, e) =>
                {
                    if (e.Result != null && !string.IsNullOrEmpty(e.Result.Text))
                    {
                        LastHeard = $"\"{e.Result.Text}\" (rejected, {e.Result.Confidence:P0})";
                        SetStatus("Listening... (not a command)");
                    }
                };
                stream = new PcmPipeStream();
                MicrophoneManager.Instance.AudioData += stream.Feed;
                engine.SetInputToAudioStream(stream, new SpeechAudioFormatInfo(WaveInCapture.SampleRate, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
                engine.RecognizeAsync(RecognizeMode.Multiple);
            }
            catch (Exception ex)
            {
                log.Error("Starting speech recognition failed", ex);
                StopListening();
                SetStatus("Speech recognition failed to start: " + ex.Message);
                return false;
            }

            Listening = true;
            listenUntilUtc = DateTime.UtcNow.AddSeconds(VoiceSettings.ListenTimeoutSeconds);
            SetStatus("Listening for \"take off\"...");
            return true;
        }

        public void StopListening(string reason = "Voice control off.")
        {
            Listening = false;
            if (stream != null)
            {
                MicrophoneManager.Instance.AudioData -= stream.Feed;
                stream.Complete();
            }

            if (engine != null)
            {
                try
                {
                    engine.RecognizeAsyncCancel();
                    engine.SpeechRecognized -= OnRecognized;
                    engine.Dispose();
                }
                catch (Exception ex)
                {
                    log.Error("Stopping speech recognition failed", ex);
                }

                engine = null;
            }

            stream = null;
            SetStatus(reason);
        }

        /// <summary>Extends the listening window (e.g. after a command was handled).</summary>
        public void ResetTimeout() => listenUntilUtc = DateTime.UtcNow.AddSeconds(VoiceSettings.ListenTimeoutSeconds);

        private void CheckTimeout()
        {
            if (Listening && DateTime.UtcNow > listenUntilUtc)
                StopListening("Voice listening timed out after " + VoiceSettings.ListenTimeoutSeconds + " s without a command.");
            else if (Listening)
                StateChanged?.Invoke();
        }

        private void OnRecognized(object sender, SpeechRecognizedEventArgs e)
        {
            var text = e.Result.Text;
            var conf = e.Result.Confidence;
            if (conf < VoiceSettings.MinConfidence)
            {
                LastHeard = $"\"{text}\" ({conf:P0} — below the {VoiceSettings.MinConfidence:P0} threshold, ignored)";
                SetStatus("Listening... (low confidence)");
                return;
            }

            LastHeard = $"\"{text}\" ({conf:P0})";
            SetStatus("Recognised: " + text);
            try
            {
                CommandRecognized?.Invoke("takeoff", text, conf);
            }
            catch (Exception ex)
            {
                log.Error("Voice command handler failed", ex);
            }
        }

        /// <summary>Blocking, non-seekable PCM stream that the recogniser reads from.</summary>
        private sealed class PcmPipeStream : Stream
        {
            private readonly BlockingCollection<byte[]> queue = new BlockingCollection<byte[]>(200);
            private byte[] current;
            private int offset;

            public void Feed(byte[] pcm)
            {
                if (!queue.IsAddingCompleted)
                    queue.TryAdd(pcm);
            }

            public void Complete() => queue.CompleteAdding();

            /// <summary>
            /// Blocks until <paramref name="count"/> bytes are available. The speech engine treats a short
            /// read as end-of-stream, so a partial count is only returned once the stream is completed.
            /// </summary>
            public override int Read(byte[] buffer, int off, int count)
            {
                var total = 0;
                while (total < count)
                {
                    if (current == null || offset >= current.Length)
                    {
                        try
                        {
                            if (!queue.TryTake(out current, -1))
                                return total; // completed
                        }
                        catch (InvalidOperationException)
                        {
                            return total; // completed
                        }

                        offset = 0;
                        continue;
                    }

                    var n = Math.Min(count - total, current.Length - offset);
                    Buffer.BlockCopy(current, offset, buffer, off + total, n);
                    offset += n;
                    total += n;
                }

                return total;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => -1;
            public override long Position { get => 0; set { } }
            public override void Flush() { }
            public override long Seek(long o, SeekOrigin origin) => 0;
            public override void SetLength(long value) { }
            public override void Write(byte[] buffer, int off, int count) => throw new NotSupportedException();
        }
    }
}
