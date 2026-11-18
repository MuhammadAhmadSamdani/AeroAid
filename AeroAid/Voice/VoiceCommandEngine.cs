using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Speech.Recognition;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using MissionPlanner.AeroAid.Audio;

namespace MissionPlanner.AeroAid.Voice
{
    /// <summary>Voice preferences (AeroSettings "voice.*").</summary>
    public static class VoiceSettings
    {
        public const string EngineVosk = "vosk";
        public const string EngineWindows = "windows";

        public static bool Enabled
        {
            get => AeroSettings.GetBool("voice.enabled", true);
            set => AeroSettings.SetBool("voice.enabled", value);
        }

        /// <summary>
        /// TEST MODE: recognition runs and is displayed and scored, but nothing is ever sent to the vehicle.
        /// On by default - the operator switches it off deliberately once recognition has been verified.
        /// </summary>
        public static bool TestMode
        {
            get => AeroSettings.GetBool("voice.test_mode", true);
            set => AeroSettings.SetBool("voice.test_mode", value);
        }

        /// <summary>Speech engine: "vosk" (offline, default) or "windows" (System.Speech).</summary>
        public static string Engine
        {
            get => AeroSettings.GetString("voice.engine", EngineVosk) == EngineWindows ? EngineWindows : EngineVosk;
            set => AeroSettings.SetString("voice.engine", value == EngineWindows ? EngineWindows : EngineVosk);
        }

        /// <summary>Optional explicit Vosk model directory ("" = automatic).</summary>
        public static string VoskModelPath
        {
            get => AeroSettings.GetString("voice.vosk_model", "");
            set => AeroSettings.SetString("voice.vosk_model", value ?? "");
        }

        /// <summary>
        /// Minimum confidence (0..1) of the command words for a sentence to be allowed to start a command.
        /// Vosk reports per-word posteriors; System.Speech a sentence confidence.
        /// </summary>
        public static double MinConfidence
        {
            get => AeroSettings.GetDouble("voice.min_conf", 0.70, 0.3, 0.98);
            set => AeroSettings.SetDouble("voice.min_conf", value);
        }

        /// <summary>Silence (ms) that ends a spoken command.</summary>
        public static int EndSilenceMs
        {
            get => AeroSettings.GetInt("voice.end_silence_ms", 650, 300, 2000);
            set => AeroSettings.SetInt("voice.end_silence_ms", value);
        }

        /// <summary>
        /// Ignore filler words that are much quieter (> 12 dB) than the loudest word of a command - background talk.
        /// Command words, negations and question words are never ignored.
        /// </summary>
        public static bool SuppressBackgroundSpeech
        {
            get => AeroSettings.GetBool("voice.suppress_background", true);
            set => AeroSettings.SetBool("voice.suppress_background", value);
        }

        /// <summary>
        /// Automatic gain: each utterance is normalised to about -20 dBFS speech level (up to +30 dB, never into
        /// clipping) before the final recognition. Laptop microphone arrays often deliver -40 dBFS, which the
        /// recogniser cannot adapt to within one short command.
        /// </summary>
        public static bool AutoGain
        {
            get => AeroSettings.GetBool("voice.auto_gain", true);
            set => AeroSettings.SetBool("voice.auto_gain", value);
        }

        /// <summary>Clear speech (ms) not covered by any recognised word above which a command is rejected as incomplete.</summary>
        public static int MaxUncoveredSpeechMs
        {
            get => AeroSettings.GetInt("voice.max_uncovered_ms", 300, 150, 2000);
            set => AeroSettings.SetInt("voice.max_uncovered_ms", value);
        }

        /// <summary>Listening stops automatically after this many seconds without speech. 0 = never (default).</summary>
        public static int IdleTimeoutSeconds
        {
            get => AeroSettings.GetInt("voice.idle_timeout", 0, 0, 3600);
            set => AeroSettings.SetInt("voice.idle_timeout", value);
        }

        /// <summary>Minimum seconds before the same ARM / DISARM / TAKEOFF voice command can be sent again.</summary>
        public static int CooldownSeconds
        {
            get => AeroSettings.GetInt("voice.cooldown", 10, 5, 300);
            set => AeroSettings.SetInt("voice.cooldown", value);
        }

        /// <summary>Ask for confirmation before STOP and HOLD (default off: they only stop the vehicle where it is).</summary>
        public static bool ConfirmStopHold
        {
            get => AeroSettings.GetBool("voice.confirm_stop_hold", false);
            set => AeroSettings.SetBool("voice.confirm_stop_hold", value);
        }

        /// <summary>Ask for confirmation before RTL and LAND (default on).</summary>
        public static bool ConfirmRtlLand
        {
            get => AeroSettings.GetBool("voice.confirm_rtl_land", true);
            set => AeroSettings.SetBool("voice.confirm_rtl_land", value);
        }
    }

    /// <summary>One completed (final) recognition result and what the app decided about it. Immutable.</summary>
    public sealed class VoiceUtterance
    {
        public VoiceUtterance(long id, string text, float confidence, string source, VoiceMatch match, bool confidenceOk,
            string decision, TimeSpan? audioPosition, bool testMode = false)
        {
            Id = id;
            TimeUtc = DateTime.UtcNow;
            Text = text ?? "";
            Confidence = confidence;
            Source = source ?? "";
            Match = match;
            ConfidenceOk = confidenceOk;
            Decision = decision ?? "";
            AudioPosition = audioPosition;
            TestMode = testMode;
        }

        /// <summary>Unique per engine instance; used to execute each utterance at most once.</summary>
        public long Id { get; }

        public DateTime TimeUtc { get; }
        public string Text { get; }

        /// <summary>Confidence of the command words (see <see cref="SpeechResult.ConfidenceFor"/>).</summary>
        public float Confidence { get; }

        /// <summary>Recogniser that produced the text (or "rejected").</summary>
        public string Source { get; }

        public VoiceMatch Match { get; }
        public bool ConfidenceOk { get; }
        public string Decision { get; }
        public TimeSpan? AudioPosition { get; }

        /// <summary>Recognised while TEST MODE was on: never executed.</summary>
        public bool TestMode { get; }

        /// <summary>Command intent that passed classification and the confidence gate (None otherwise).</summary>
        public VoiceCommand AcceptedCommand => ConfidenceOk && Match != null && Match.IsCommand ? Match.Command : VoiceCommand.None;

        /// <summary>True only for a single, well-formed, non-negated command above the confidence threshold.</summary>
        public bool Actionable => AcceptedCommand != VoiceCommand.None;
    }

    /// <summary>
    /// The voice pipeline:
    ///   microphone (16 kHz mono) → high-pass + voice-activity detection (one utterance at a time)
    ///   → speech recogniser restricted to the command vocabulary (Vosk offline; Windows speech as fallback)
    ///   → <see cref="VoiceCommandParser"/> (phrase grammar, negation / ambiguity / question rejection)
    ///   → confidence gate on the COMMAND words → split-negation guard → events.
    /// Partial hypotheses are shown but never acted on. This class only REPORTS commands; it never sends anything
    /// to the vehicle. In TEST MODE <see cref="CommandDetected"/> is never raised.
    /// </summary>
    public sealed class VoiceCommandEngine
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        public static VoiceCommandEngine Instance { get; } = new VoiceCommandEngine(MicrophoneManager.Instance);

        /// <summary>Source names (kept for tests / logs).</summary>
        public const string CommandGrammarName = SapiCommandRecognizer.CommandGrammarName;
        public const string DictationGrammarName = "dictation";

        /// <summary>A command spoken this soon after a negation-only phrase ("do not ... arm") is treated as negated.</summary>
        public static readonly TimeSpan SplitNegationWindow = TimeSpan.FromSeconds(3);

        private readonly IPcmSource source;
        private readonly object sync = new object();
        private readonly System.Threading.Timer timeoutTimer;
        private BlockingCollection<byte[]> queue;
        private Thread worker;
        private ICommandRecognizer recognizer;
        private VoiceActivityDetector vad;
        private int session;
        private long nextId;
        private long processedSamples;
        private TimeSpan? lastAudioPosition;
        private string lastText;
        private DateTime lastTextUtc;
        private TimeSpan? negationEndAudio;
        private DateTime negationEndUtc = DateTime.MinValue;
        private DateTime lastActivityUtc;
        private DateTime lastPartialUtc;
        private TimeSpan utteranceStart;
        private readonly System.IO.MemoryStream utteranceAudio = new System.IO.MemoryStream();
        private double liveGain = 1;

        /// <summary>Gain (dB) applied to the last utterance by <see cref="VoiceSettings.AutoGain"/>.</summary>
        public double LastGainDb { get; private set; }

        public const double AutoGainTargetDb = -20, AutoGainMaxDb = 30;

        /// <summary>Gain (linear) that brings <paramref name="speechDb"/> to the target without clipping the peak.</summary>
        public static double ComputeGain(double speechDb, double peakDb)
        {
            if (speechDb <= -99) return 1;
            var db = Math.Max(0, Math.Min(AutoGainMaxDb, AutoGainTargetDb - speechDb));
            db = Math.Min(db, Math.Max(0, -1 - peakDb)); // keep the peak below -1 dBFS
            return Math.Pow(10, db / 20);
        }

        /// <summary>Applies a linear gain to 16-bit PCM (with saturation).</summary>
        public static byte[] ApplyGain(byte[] pcm, double gain)
        {
            if (Math.Abs(gain - 1) < 1e-3) return pcm;
            var o = new byte[pcm.Length];
            for (var i = 0; i + 1 < pcm.Length; i += 2)
            {
                var v = (short)(pcm[i] | (pcm[i + 1] << 8)) * gain;
                var s = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, Math.Round(v)));
                o[i] = (byte)s;
                o[i + 1] = (byte)(s >> 8);
            }

            return o;
        }

        /// <summary>Optional recogniser factory (tests). Null = from <see cref="VoiceSettings.Engine"/>.</summary>
        public Func<ICommandRecognizer> RecognizerFactory { get; set; }

        public bool Listening { get; private set; }

        /// <summary>True while the voice-activity detector hears speech.</summary>
        public bool SpeechActive { get; private set; }

        public string Status { get; private set; } = "Voice control off.";

        /// <summary>Name of the recogniser in use (or that will be used).</summary>
        public string EngineName { get; private set; } = "";

        /// <summary>Warning about the engine choice (e.g. fell back to Windows speech), or "".</summary>
        public string EngineWarning { get; private set; } = "";

        /// <summary>Live (partial, unstable) text of the sentence being spoken. Display only.</summary>
        public string PartialText { get; private set; } = "";

        /// <summary>Last audio problem (too loud, too quiet, noisy), or "".</summary>
        public string SignalProblem { get; private set; } = "";

        /// <summary>Background noise level from the voice-activity detector (dBFS), or null when not listening.</summary>
        public double? NoiseFloorDb => Listening ? vad?.NoiseFloorDb : null;

        /// <summary>Speech level of the last utterance (dBFS), or null.</summary>
        public double? LastSpeechDb => vad != null && vad.LastSpeechDb > -100 ? vad.LastSpeechDb : (double?)null;

        public VoiceUtterance LastUtterance { get; private set; }

        /// <summary>Seconds until the idle timeout stops listening; -1 when listening continuously.</summary>
        public double SecondsRemaining
        {
            get
            {
                var t = VoiceSettings.IdleTimeoutSeconds;
                if (!Listening || t <= 0) return -1;
                return Math.Max(0, t - (DateTime.UtcNow - lastActivityUtc).TotalSeconds);
            }
        }

        /// <summary>Every final recognition result, accepted or not (worker thread).</summary>
        public event Action<VoiceUtterance> UtteranceRecognized;

        /// <summary>Raised exactly once per actionable utterance, never in TEST MODE (worker thread).</summary>
        public event Action<VoiceUtterance> CommandDetected;

        /// <summary>Status / partial-text changes (any thread).</summary>
        public event Action StateChanged;

        public VoiceCommandEngine(IPcmSource source)
        {
            this.source = source ?? throw new ArgumentNullException(nameof(source));
            timeoutTimer = new System.Threading.Timer(_ => CheckTimeout(), null, 1000, 1000);
        }

        private void RaiseStateChanged()
        {
            try
            {
                StateChanged?.Invoke();
            }
            catch (Exception ex)
            {
                log.Error("Voice StateChanged handler failed", ex);
            }
        }

        private void SetStatus(string s)
        {
            Status = s;
            log.Info("Voice: " + s);
            RaiseStateChanged();
        }

        // ------------------------------------------------------------------ recogniser selection

        public static string RecognizerInfo() => SapiCommandRecognizer.RecognizerInfo();

        /// <summary>The System.Speech grammars of the fallback engine (tests).</summary>
        public static Grammar[] BuildGrammars(CultureInfo culture) => SapiCommandRecognizer.BuildGrammars(culture);

        /// <summary>Describes which engine will be used and why (for the UI before listening starts).</summary>
        public static (string name, string warning) DescribeEngine()
        {
            if (VoiceSettings.Engine == VoiceSettings.EngineWindows)
                return ("Windows speech (System.Speech) - command grammar", "Windows speech is less accurate for short commands and non-US accents than Vosk.");
            var why = VoskCommandRecognizer.CheckAvailable(out var path);
            if (why == null)
                return ("Vosk offline - command vocabulary (" + System.IO.Path.GetFileName(path) + ")", "");
            return ("Windows speech (fallback)", "Vosk unavailable: " + why + " Using Windows speech, which is less accurate.");
        }

        /// <summary>Loads the Vosk model in the background so that Start Listening is instant.</summary>
        public static Task PreloadAsync() => Task.Run(() =>
        {
            try
            {
                if (VoiceSettings.Engine == VoiceSettings.EngineVosk && VoskCommandRecognizer.CheckAvailable(out var p) == null)
                    VoskCommandRecognizer.LoadModel(p);
            }
            catch (Exception ex)
            {
                log.Warn("Vosk preload failed: " + ex.Message);
            }
        });

        private ICommandRecognizer CreateRecognizer(out string warning)
        {
            warning = "";
            if (RecognizerFactory != null)
                return RecognizerFactory();
            if (VoiceSettings.Engine == VoiceSettings.EngineVosk)
            {
                try
                {
                    return VoskCommandRecognizer.Create();
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is DllNotFoundException ||
                                           ex is BadImageFormatException || ex is EntryPointNotFoundException ||
                                           ex is TypeInitializationException)
                {
                    warning = "Vosk unavailable: " + ex.Message + " Using Windows speech (less accurate).";
                    log.Warn(warning, ex);
                }
            }
            else
            {
                warning = "Windows speech is less accurate for short commands and non-US accents than Vosk.";
            }

            return SapiCommandRecognizer.Create();
        }

        // ------------------------------------------------------------------ start / stop

        /// <summary>Starts continuous listening (turns the microphone on). Returns false with Status explaining why.</summary>
        public bool StartListening()
        {
            lock (sync)
            {
                if (Listening) return true;
                if (!VoiceSettings.Enabled)
                {
                    SetStatus("Voice control is disabled in Settings › Voice.");
                    return false;
                }

                ICommandRecognizer r;
                try
                {
                    r = CreateRecognizer(out var warning);
                    EngineWarning = warning;
                    EngineName = r.Name;
                }
                catch (Exception ex)
                {
                    log.Error("Creating the speech recogniser failed", ex);
                    SetStatus("Speech recognition is unavailable: " + ex.Message);
                    return false;
                }

                if (!source.IsOn && !source.TurnOn())
                {
                    r.Dispose();
                    SetStatus("Cannot listen: " + source.Status);
                    return false;
                }

                var mySession = ++session;
                recognizer = r;
                vad = new VoiceActivityDetector { HangoverMs = VoiceSettings.EndSilenceMs };
                vad.UtteranceStarted += pre => OnUtteranceStarted(mySession, pre);
                vad.UtteranceAudio += pcm => OnUtteranceAudio(mySession, pcm);
                vad.UtteranceEnded += valid => OnUtteranceEnded(mySession, valid);
                queue = new BlockingCollection<byte[]>(500);
                var q = queue;
                var v = vad;
                worker = new Thread(() => WorkerLoop(mySession, q, v)) { IsBackground = true, Name = "AeroAid voice recognition" };
                processedSamples = 0;
                lastAudioPosition = null;
                negationEndAudio = null;
                PartialText = "";
                SignalProblem = "";
                Listening = true;
                lastActivityUtc = DateTime.UtcNow;
                source.AudioData += OnAudio;
                source.StateChanged += OnSourceStateChanged;
                worker.Start();
            }

            SetStatus(VoiceSettings.TestMode
                ? "Listening (TEST MODE - nothing is sent to the vehicle)."
                : "Listening - say a command, e.g. \"arm the drone\", \"take off\", \"land\".");
            VoiceLog.Write("listening started (" + EngineName + ", " + source.Status + ")" + (VoiceSettings.TestMode ? " TEST MODE" : ""));
            return true;
        }

        private int pending;

        private void OnAudio(byte[] pcm)
        {
            var q = queue;
            Interlocked.Increment(ref pending);
            try
            {
                if (q != null && !q.IsAddingCompleted && q.TryAdd(pcm))
                    return;
                if (q != null && !q.IsAddingCompleted)
                    log.Warn("Voice: recogniser is behind, audio chunk dropped");
            }
            catch (InvalidOperationException)
            {
                // completed concurrently
            }

            Interlocked.Decrement(ref pending);
        }

        /// <summary>Waits until every audio chunk received so far has been processed (tests).</summary>
        public bool WaitIdle(int timeoutMs = 10000)
        {
            var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (Volatile.Read(ref pending) > 0)
            {
                if (DateTime.UtcNow > until) return false;
                Thread.Sleep(2);
            }

            return true;
        }

        private void WorkerLoop(int mySession, BlockingCollection<byte[]> q, VoiceActivityDetector v)
        {
            try
            {
                foreach (var pcm in q.GetConsumingEnumerable())
                {
                    try
                    {
                        if (mySession != session) break;
                        v.Process(pcm);
                        processedSamples += pcm.Length / 2;
                    }
                    finally
                    {
                        Interlocked.Decrement(ref pending);
                    }
                }
            }
            catch (Exception ex)
            {
                log.Error("Voice worker failed", ex);
                if (mySession == session)
                    Task.Run(() => StopListening("Speech recognition stopped unexpectedly: " + ex.Message));
            }
        }

        private TimeSpan AudioNow => TimeSpan.FromSeconds(processedSamples / (double)VoiceActivityDetector.SampleRate);

        private void OnUtteranceStarted(int mySession, byte[] preRoll)
        {
            if (mySession != session) return;
            utteranceStart = AudioNow - TimeSpan.FromMilliseconds(preRoll.Length / 32.0);
            SpeechActive = true;
            PartialText = "";
            utteranceAudio.SetLength(0);
            utteranceAudio.Write(preRoll, 0, preRoll.Length);
            recognizer.BeginUtterance();
            recognizer.Feed(VoiceSettings.AutoGain ? ApplyGain(preRoll, liveGain) : preRoll);
            RaiseStateChanged();
        }

        private void OnUtteranceAudio(int mySession, byte[] pcm)
        {
            if (mySession != session) return;
            utteranceAudio.Write(pcm, 0, pcm.Length);
            recognizer.Feed(VoiceSettings.AutoGain ? ApplyGain(pcm, liveGain) : pcm); // live pass: partial text only
            if ((DateTime.UtcNow - lastPartialUtc).TotalMilliseconds > 150)
            {
                lastPartialUtc = DateTime.UtcNow;
                var p = recognizer.Partial();
                if (p != PartialText)
                {
                    PartialText = p;
                    RaiseStateChanged();
                }
            }
        }

        private void OnUtteranceEnded(int mySession, bool valid)
        {
            if (mySession != session) return;
            SpeechActive = false;
            SpeechResult result;
            if (VoiceSettings.AutoGain && valid)
            {
                // final pass on the whole utterance, normalised to the target speech level
                var gain = ComputeGain(vad.LastSpeechDb, vad.LastPeakDb);
                LastGainDb = 20 * Math.Log10(gain);
                liveGain = gain;
                recognizer.EndUtterance(); // discard the live pass
                recognizer.BeginUtterance();
                recognizer.Feed(ApplyGain(utteranceAudio.ToArray(), gain));
                result = recognizer.EndUtterance();
            }
            else
            {
                LastGainDb = 0;
                result = recognizer.EndUtterance();
            }
            if (VoiceSettings.SuppressBackgroundSpeech)
            {
                result = result.WithoutBackgroundWords(vad.UtteranceFrameDb);
                if (result.DroppedBackground.Length > 0)
                    VoiceLog.Write($"ignored quiet background words: \"{result.DroppedBackground}\"");
            }

            SignalProblem = DescribeLevel(vad);
            PartialText = "";
            if (!valid || string.IsNullOrWhiteSpace(result.Text))
            {
                RaiseStateChanged();
                return;
            }

            // floor for the completeness check: the VAD floor lags behind a noise that starts with the utterance (a fan
            // switched on), so also use the utterance's own quiet level (20th percentile of its frames)
            var frames = vad.UtteranceFrameDb.OrderBy(x => x).ToList();
            var ownFloor = frames.Count > 0 ? frames[frames.Count / 5] : -100;
            var uncovered = result.UncoveredSpeechMs(vad.UtteranceFrameDb, Math.Max(vad.NoiseFloorDb, ownFloor));
            var incomplete = uncovered > VoiceSettings.MaxUncoveredSpeechMs
                ? $"INCOMPLETE - {uncovered:0} ms of your speech was not recognised (words were lost). Please repeat."
                : null;
            ProcessResult(result, utteranceStart, AudioNow - utteranceStart, incomplete);
        }

        private static string DescribeLevel(VoiceActivityDetector v)
        {
            if (v.LastClipRatio > 0.001 || v.LastPeakDb > -0.5)
                return "Input too loud (clipping) - lower the microphone level or move it further away.";
            if (v.LastSpeechDb < -48)
                return "Input very quiet - speak closer to the microphone or raise its level in Windows Sound settings.";
            if (v.LastSpeechDb - v.NoiseFloorDb < 10)
                return "Background noise is high compared with your voice.";
            return "";
        }

        public void StopListening(string reason = "Voice control off.")
        {
            ICommandRecognizer r;
            BlockingCollection<byte[]> q;
            Thread w;
            bool was;
            lock (sync)
            {
                was = Listening;
                session++;
                Listening = false;
                SpeechActive = false;
                PartialText = "";
                r = recognizer;
                q = queue;
                w = worker;
                recognizer = null;
                queue = null;
                worker = null;
                source.AudioData -= OnAudio;
                source.StateChanged -= OnSourceStateChanged;
                q?.CompleteAdding();
                if (q != null)
                    while (q.TryTake(out _))
                        Interlocked.Decrement(ref pending);
            }

            if (w != null && w != Thread.CurrentThread)
                w.Join(2000);
            try
            {
                r?.Dispose();
            }
            catch (Exception ex)
            {
                log.Error("Stopping speech recognition failed", ex);
            }

            if (was)
                VoiceLog.Write("listening stopped: " + reason);
            SetStatus(reason);
        }

        private void OnSourceStateChanged()
        {
            if (Listening && !source.IsOn)
                Task.Run(() => StopListening("Listening stopped: the microphone was turned off or disconnected."));
        }

        private void CheckTimeout()
        {
            var t = VoiceSettings.IdleTimeoutSeconds;
            if (Listening && t > 0 && (DateTime.UtcNow - lastActivityUtc).TotalSeconds > t)
                StopListening("Listening stopped after " + t + " s without speech (Settings › Voice).");
            else if (Listening)
                RaiseStateChanged();
        }

        // ------------------------------------------------------------------ results

        /// <summary>Classifies a recogniser result (uses the confidence of the command words).</summary>
        public VoiceUtterance ProcessResult(SpeechResult r, TimeSpan? audioPosition = null, TimeSpan? duration = null,
            string incomplete = null)
        {
            var match = VoiceCommandParser.Parse(r.Text);
            return ProcessFinal(r.Text, r.ConfidenceFor(match), r.Engine, audioPosition, duration, false, incomplete);
        }

        /// <summary>
        /// Handles one FINAL recognition result: de-duplicates it, classifies it, applies the confidence threshold and
        /// the split-negation guard, logs it and raises the events. Called by the pipeline (and directly by tests).
        /// Returns null for a duplicate.
        /// </summary>
        public VoiceUtterance ProcessFinal(string text, float confidence, string sourceName, TimeSpan? audioPosition,
            TimeSpan? duration = null, bool rejectedByRecognizer = false, string incomplete = null)
        {
            VoiceUtterance u;
            var testMode = VoiceSettings.TestMode;
            lock (sync)
            {
                var now = DateTime.UtcNow;
                // each utterance is processed once: same audio position, or identical text within 1 s, is a duplicate
                if (audioPosition.HasValue && lastAudioPosition.HasValue && audioPosition.Value == lastAudioPosition.Value)
                    return null;
                if (!audioPosition.HasValue && text == lastText && (now - lastTextUtc).TotalSeconds < 1)
                    return null;
                lastAudioPosition = audioPosition;
                lastText = text;
                lastTextUtc = now;
                lastActivityUtc = now;

                var match = VoiceCommandParser.Parse(text);

                // "do not ... arm" split into two utterances by a pause: the second part is negated too
                if (match.IsCommand && RecentNegation(audioPosition, now))
                    match = new VoiceMatch(VoiceMatchOutcome.Negated, VoiceCommand.None, match.Normalized, match.Keyword,
                        "follows a negation spoken just before", match.Found);
                if (match.Found.Count == 0 && VoiceCommandParser.ContainsNegation(match.Normalized))
                {
                    negationEndAudio = audioPosition.HasValue ? audioPosition + (duration ?? TimeSpan.Zero) : null;
                    negationEndUtc = now;
                }

                var min = VoiceSettings.MinConfidence;
                var confOk = !rejectedByRecognizer && incomplete == null && confidence >= min;
                string decision;
                if (!match.IsCommand)
                    decision = "REJECTED: " + match.Headline + (match.Reason.Length > 0 ? " - " + match.Reason : "");
                else if (incomplete != null)
                    decision = $"REJECTED: {VoiceCommandParser.Name(match.Command)}? {incomplete}";
                else if (rejectedByRecognizer)
                    decision = "REJECTED: the recogniser rejected this sentence as unclear. Please repeat.";
                else if (!confOk)
                    decision = $"REJECTED: {VoiceCommandParser.Name(match.Command)}? confidence {confidence:P0} is below the {min:P0} threshold. Please repeat clearly.";
                else
                    decision = "ACCEPTED: " + match.Headline + (testMode ? " (test mode - not sent)" : "");

                u = new VoiceUtterance(++nextId, text, confidence, sourceName, match, confOk, decision, audioPosition, testMode);
                LastUtterance = u;
                PartialText = "";
            }

            log.Info($"Voice heard \"{u.Text}\" ({u.Confidence:P0}, {u.Source}) -> {u.Decision}");
            VoiceLog.Write($"heard \"{u.Text}\" conf={u.Confidence:0.00} src={u.Source} norm=\"{u.Match.Normalized}\" -> {u.Decision}");
            RaiseStateChanged();
            try
            {
                if (u.TestMode)
                    VoiceTestSession.Instance.Record(u);
                UtteranceRecognized?.Invoke(u);
                if (u.Actionable && !u.TestMode)
                    CommandDetected?.Invoke(u);
            }
            catch (Exception ex)
            {
                log.Error("Voice command handler failed", ex);
            }

            return u;
        }

        private bool RecentNegation(TimeSpan? audioPosition, DateTime now)
        {
            if (audioPosition.HasValue && negationEndAudio.HasValue)
                return audioPosition.Value >= negationEndAudio.Value && audioPosition.Value - negationEndAudio.Value < SplitNegationWindow;
            return now - negationEndUtc < SplitNegationWindow;
        }

        // ------------------------------------------------------------------ test support

        /// <summary>Clears the de-duplication / negation memory (tests).</summary>
        public void ResetHistory()
        {
            lock (sync)
            {
                lastAudioPosition = null;
                lastText = null;
                negationEndAudio = null;
                negationEndUtc = DateTime.MinValue;
            }
        }
    }
}
