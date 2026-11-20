using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
using log4net;
using Newtonsoft.Json.Linq;

namespace MissionPlanner.AeroAid.Voice
{
    /// <summary>One recognised word with its confidence and time (seconds from the start of the utterance audio).</summary>
    public sealed class SpeechWord
    {
        public SpeechWord(string word, float conf, double start = -1, double end = -1)
        {
            Word = word ?? "";
            Conf = conf;
            Start = start;
            End = end;
        }

        public string Word { get; }
        public float Conf { get; }
        public double Start { get; }
        public double End { get; }
        public bool HasTime => Start >= 0 && End > Start;
    }

    /// <summary>Final result of one utterance from a recogniser.</summary>
    public sealed class SpeechResult
    {
        public SpeechResult(string text, float confidence, IReadOnlyList<SpeechWord> words, string engine)
        {
            Text = text ?? "";
            Confidence = confidence;
            Words = words ?? Array.Empty<SpeechWord>();
            Engine = engine ?? "";
        }

        /// <summary>
        /// Completeness check: milliseconds of clear speech (frames more than <paramref name="marginDb"/> above the
        /// noise floor) that are NOT covered by any recognised word (±120 ms). A large value means the recogniser
        /// dropped words it could not match - e.g. "disarm and arm the drone" returned as just "disarm" - so the
        /// transcript must not be trusted. Returns 0 when the engine gives no word times.
        /// </summary>
        public double UncoveredSpeechMs(IReadOnlyList<double> frameDb, double floorDb, double marginDb = 6)
        {
            if (frameDb == null || frameDb.Count == 0 || Words.Count == 0 || Words.Any(w => !w.HasTime))
                return 0;
            var uncovered = 0;
            for (var i = 0; i < frameDb.Count; i++)
            {
                if (frameDb[i] < floorDb + marginDb) continue;
                var t = i * 0.02 + 0.01;
                if (!Words.Any(w => t >= w.Start - 0.12 && t <= w.End + 0.12))
                    uncovered++;
            }

            return uncovered * 20.0;
        }

        /// <summary>Words dropped as quiet background speech by <see cref="WithoutBackgroundWords"/> ("" = none).</summary>
        public string DroppedBackground { get; private set; } = "";

        /// <summary>
        /// Background-speech suppression: the operator talks close to the microphone, other people further away.
        /// Words more than <paramref name="marginDb"/> quieter than the loudest word of the utterance are removed -
        /// EXCEPT command words, negations and question words, which are always kept (so a quiet "not" can never be
        /// removed from "do not arm", and a quiet second command still makes the sentence ambiguous).
        /// <paramref name="frameDb"/> = 20 ms frame energies aligned with the recogniser's audio.
        /// </summary>
        public SpeechResult WithoutBackgroundWords(IReadOnlyList<double> frameDb, double marginDb = 12)
        {
            if (frameDb == null || frameDb.Count == 0 || Words.Count < 2 || Words.Any(w => !w.HasTime))
                return this;
            double Energy(SpeechWord w)
            {
                var a = Math.Max(0, (int)(w.Start / 0.02));
                var b = Math.Min(frameDb.Count - 1, (int)Math.Ceiling(w.End / 0.02));
                if (a > b) return -100;
                double max = -100;
                for (var i = a; i <= b; i++) max = Math.Max(max, frameDb[i]);
                return max;
            }

            var energies = Words.Select(Energy).ToList();
            var loudest = energies.Max();
            var keep = new List<SpeechWord>();
            var dropped = new List<string>();
            for (var i = 0; i < Words.Count; i++)
            {
                var w = Words[i];
                var norm = VoiceCommandParser.Normalize(w.Word);
                var protectedWord = VoiceCommandParser.IsProtectedWord(norm);
                if (!protectedWord && energies[i] < loudest - marginDb)
                    dropped.Add(w.Word);
                else
                    keep.Add(w);
            }

            if (dropped.Count == 0 || keep.Count == 0)
                return this;
            return new SpeechResult(string.Join(" ", keep.Select(w => w.Word)), keep.Min(w => w.Conf), keep, Engine)
            {
                DroppedBackground = string.Join(" ", dropped)
            };
        }

        public string Text { get; }

        /// <summary>Engine confidence for the whole sentence (0..1).</summary>
        public float Confidence { get; }

        /// <summary>Per-word confidences and times when the engine provides them (Vosk), else empty.</summary>
        public IReadOnlyList<SpeechWord> Words { get; }

        public string Engine { get; }

        /// <summary>
        /// Confidence of the COMMAND part of the sentence: the lowest confidence of the words that form the matched
        /// command phrase ("arm" in "please arm the drone"). Falls back to the sentence confidence.
        /// </summary>
        public float ConfidenceFor(VoiceMatch match)
        {
            if (Words.Count == 0)
                return Confidence;
            var keyword = VoiceCommandParser.Tokens(match?.Keyword ?? "");
            var relevant = Words.Where(w => keyword.Contains(VoiceCommandParser.Normalize(w.Word))).Select(w => w.Conf).ToList();
            return relevant.Count > 0 ? relevant.Min() : Words.Min(w => w.Conf);
        }
    }

    /// <summary>
    /// A speech recogniser that decodes one utterance at a time (the voice-activity detector decides where an
    /// utterance starts and ends). Implementations are used from a single worker thread.
    /// </summary>
    public interface ICommandRecognizer : IDisposable
    {
        /// <summary>Display name, e.g. "Vosk offline (command vocabulary)".</summary>
        string Name { get; }

        void BeginUtterance();

        /// <summary>16 kHz 16-bit mono PCM.</summary>
        void Feed(byte[] pcm);

        /// <summary>Partial (unstable) text of the current utterance, or "".</summary>
        string Partial();

        SpeechResult EndUtterance();
    }

    // ====================================================================== Vosk

    /// <summary>
    /// Offline Kaldi recogniser (Vosk, Apache-2.0) restricted to the command vocabulary of <see cref="VoiceGrammar"/>.
    /// Needs the 64-bit native libvosk (NuGet package "Vosk") and an English model directory
    /// (vosk-model-small-en-us-0.15, ~40 MB download, see <see cref="VoskModelStore"/>).
    /// </summary>
    public sealed class VoskCommandRecognizer : ICommandRecognizer
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly object modelLock = new object();
        private static Vosk.Model model;
        private static string modelPath;

        private Vosk.VoskRecognizer rec;
        private readonly List<SpeechWord> words = new List<SpeechWord>();
        private readonly List<string> texts = new List<string>();

        /// <summary>Samples fed since the recogniser was created, and at the start of the current utterance.
        /// Vosk word times count from recogniser creation, not from Reset(); they are converted to
        /// utterance-relative times so they line up with the voice-activity detector's frame energies.</summary>
        private long fedSamples, utteranceStartSamples;

        public string Name { get; }

        private VoskCommandRecognizer(Vosk.VoskRecognizer rec, string name)
        {
            this.rec = rec;
            Name = name;
        }

        /// <summary>Checks that Vosk can run here. Returns null if OK, else a readable reason.</summary>
        public static string CheckAvailable(out string path)
        {
            path = VoskModelStore.FindModel();
            if (!Environment.Is64BitProcess)
                return "Vosk needs a 64-bit process.";
            if (path == null)
                return "The offline speech model is not installed (Voice page › Download speech model).";
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            if (!File.Exists(Path.Combine(dir, "libvosk.dll")))
                return "libvosk.dll is missing next to the application.";
            var conflict = VoskNativeRuntime.FindConflict();
            if (conflict != null)
                return "another component (usually GStreamer video) loaded an incompatible " + conflict +
                       " before the speech engine; starting Vosk now could crash the application. Restart AeroAid.";
            return null;
        }

        /// <summary>Loads the model once (about 1 s) - safe to call from a background thread.</summary>
        public static Vosk.Model LoadModel(string path)
        {
            lock (modelLock)
            {
                if (model != null && string.Equals(modelPath, path, StringComparison.OrdinalIgnoreCase))
                    return model;
                Vosk.Vosk.SetLogLevel(-1);
                var m = new Vosk.Model(path);
                model?.Dispose();
                model = m;
                modelPath = path;
                log.Info("Vosk model loaded: " + path);
                return m;
            }
        }

        public static bool ModelLoaded
        {
            get
            {
                lock (modelLock) return model != null;
            }
        }

        /// <summary>Creates a recogniser; throws with a readable message when Vosk is not usable.</summary>
        public static VoskCommandRecognizer Create(IEnumerable<string> phrases = null)
        {
            var why = CheckAvailable(out var path);
            if (why != null)
                throw new InvalidOperationException(why);
            var m = LoadModel(path);
            var grammar = VoiceGrammar.ToJson(phrases ?? VoiceGrammar.Phrases(true, VoiceSettings.WakePhrase));
            var r = new Vosk.VoskRecognizer(m, VoiceActivityDetectorRate, grammar);
            r.SetWords(true);
            return new VoskCommandRecognizer(r, "Vosk offline - command vocabulary (" + Path.GetFileName(path) + ")");
        }

        private const float VoiceActivityDetectorRate = Audio.VoiceActivityDetector.SampleRate;

        public void BeginUtterance()
        {
            rec.Reset();
            words.Clear();
            texts.Clear();
            utteranceStartSamples = fedSamples;
        }

        public void Feed(byte[] pcm)
        {
            fedSamples += pcm.Length / 2;
            if (rec.AcceptWaveform(pcm, pcm.Length))
                Collect(rec.Result()); // Kaldi found an internal end point: keep that part
        }

        public string Partial()
        {
            try
            {
                var p = (string)JObject.Parse(rec.PartialResult())["partial"] ?? "";
                return string.Join(" ", texts.Concat(new[] { p }).Where(t => t.Length > 0));
            }
            catch (Exception)
            {
                return "";
            }
        }

        public SpeechResult EndUtterance()
        {
            Collect(rec.FinalResult());
            var text = string.Join(" ", texts);
            var conf = words.Count == 0 ? 0f : words.Min(w => w.Conf);
            var result = new SpeechResult(text, conf, words.ToList(), Name);
            rec.Reset();
            return result;
        }

        private void Collect(string json)
        {
            var o = JObject.Parse(json);
            var t = ((string)o["text"] ?? "").Trim();
            if (t.Length > 0) texts.Add(t);
            if (o["result"] is JArray arr)
            {
                var offset = utteranceStartSamples / (double)VoiceActivityDetectorRate;
                foreach (var w in arr)
                {
                    var start = (double?)w["start"] ?? -1;
                    var end = (double?)w["end"] ?? -1;
                    // absolute (since creation) -> relative to this utterance; tolerate engines that already reset
                    if (start >= offset - 0.05 && offset > 0)
                    {
                        start = Math.Max(0, start - offset);
                        end = Math.Max(0, end - offset);
                    }

                    words.Add(new SpeechWord((string)w["word"] ?? "", (float?)w["conf"] ?? 0f, start, end));
                }
            }
        }

        public void Dispose()
        {
            rec?.Dispose();
            rec = null;
        }
    }

    // ====================================================================== Windows speech (fallback)

    /// <summary>
    /// Fallback: the Windows desktop recogniser (System.Speech, SAPI) with a CLOSED grammar only - the command
    /// phrases with optional context words, plus decoy words. Free dictation is deliberately NOT loaded: it was the
    /// main source of wrong words ("arm the drone" → "our major own"). Less accurate for non-US accents than Vosk.
    /// </summary>
    public sealed class SapiCommandRecognizer : ICommandRecognizer
    {
        public const string CommandGrammarName = "command grammar";
        public const string DecoyGrammarName = "decoy";

        private readonly SpeechRecognitionEngine engine;
        private readonly MemoryStream buffer = new MemoryStream();

        public string Name { get; }

        private SapiCommandRecognizer(SpeechRecognitionEngine engine, string name)
        {
            this.engine = engine;
            Name = name;
        }

        public static RecognizerInfo FindRecognizer()
        {
            var all = SpeechRecognitionEngine.InstalledRecognizers();
            var ui = CultureInfo.CurrentUICulture;
            return all.FirstOrDefault(r => ui.TwoLetterISOLanguageName == "en" && r.Culture.Name == ui.Name) ??
                   all.FirstOrDefault(r => r.Culture.Name == "en-US") ??
                   all.FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName == "en");
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

        /// <summary>Command grammar = context{0,5} PHRASE context{0,5}; decoy grammar = 1-4 decoy words.</summary>
        public static Grammar[] BuildGrammars(CultureInfo culture)
        {
            // context = polite / negation / question words, the wake word ("aero aid") and spoken altitudes
            var wake = VoiceCommandParser.Tokens(VoiceCommandParser.Normalize(VoiceParseOptions.DefaultWakePhrase));
            var context = VoiceCommandParser.GrammarFillers.Concat(wake).Concat(VoiceGrammar.AltitudeWords)
                .Where(w => w != "r").Distinct().ToArray();
            var filler = new GrammarBuilder(new Choices(context)) { Culture = culture };
            var gb = new GrammarBuilder { Culture = culture };
            gb.Append(filler, 0, 5);
            gb.Append(new GrammarBuilder(new Choices(VoiceCommandParser.GrammarKeywords)) { Culture = culture });
            // separate builder instance: System.Speech's optimiser throws when one instance is appended twice with
            // different repeat counts. Up to 6 words after the command: "take off to twenty five meters now".
            gb.Append(new GrammarBuilder(new Choices(context)) { Culture = culture }, 0, 6);
            var phraseTokens = VoiceCommandParser.Phrases.SelectMany(p => p.Tokens).ToList();
            var decoys = VoiceGrammar.Decoys.Where(d => !d.Split(' ').Any(phraseTokens.Contains) && !context.Contains(d)).ToArray();
            var dg = new GrammarBuilder { Culture = culture };
            dg.Append(new GrammarBuilder(new Choices(decoys)) { Culture = culture }, 1, 4);
            return new Grammar[] { new Grammar(gb) { Name = CommandGrammarName }, new Grammar(dg) { Name = DecoyGrammarName } };
        }

        public static SapiCommandRecognizer Create()
        {
            var info = FindRecognizer();
            if (info == null)
                throw new InvalidOperationException(
                    "No English Windows speech recogniser is installed (Windows Settings › Time & language › Speech).");
            var e = new SpeechRecognitionEngine(info);
            foreach (var g in BuildGrammars(info.Culture))
                e.LoadGrammar(g);
            return new SapiCommandRecognizer(e, "Windows speech (SAPI " + info.Culture.Name + ") - command grammar");
        }

        public void BeginUtterance() => buffer.SetLength(0);

        public void Feed(byte[] pcm) => buffer.Write(pcm, 0, pcm.Length);

        public string Partial() => "";

        public SpeechResult EndUtterance()
        {
            var texts = new List<string>();
            var conf = 1f;
            // a little trailing silence lets the recogniser close the phrase
            var pcm = buffer.ToArray().Concat(new byte[16000]).ToArray();
            using (var ms = new MemoryStream(pcm))
            {
                engine.SetInputToAudioStream(ms, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
                while (true)
                {
                    RecognitionResult r;
                    try
                    {
                        r = engine.Recognize();
                    }
                    catch (InvalidOperationException)
                    {
                        break;
                    }

                    if (r == null) break;
                    texts.Add(r.Text);
                    conf = Math.Min(conf, r.Confidence);
                }

                engine.SetInputToNull();
            }

            return new SpeechResult(string.Join(" ", texts), texts.Count == 0 ? 0 : conf, null, Name);
        }

        public void Dispose() => engine.Dispose();
    }

    // ====================================================================== model location / download

    /// <summary>Finds, and downloads on request, the Vosk English model.</summary>
    public static class VoskModelStore
    {
        public const string ModelName = "vosk-model-small-en-us-0.15";
        public const string ModelUrl = "https://alphacephei.com/vosk/models/vosk-model-small-en-us-0.15.zip";
        public const string ModelSha256 = "30f26242c4eb449f948e42cb302dd7a686cb29a3423a8367f99ff41780942498";
        public const long ModelZipBytes = 41205931;

        /// <summary>Where downloaded models are stored (Mission Planner data folder\AeroAid\models\voice).</summary>
        public static string ModelsDirectory =>
            Path.Combine(MissionPlanner.Utilities.Settings.GetUserDataDirectory(), "AeroAid", "models", "voice");

        public static bool IsModelDirectory(string dir) =>
            !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "am", "final.mdl")) &&
            File.Exists(Path.Combine(dir, "conf", "model.conf"));

        /// <summary>The model to use: the configured path, then the data folder, then next to the exe. Null if none.</summary>
        public static string FindModel()
        {
            var configured = VoiceSettings.VoskModelPath;
            if (IsModelDirectory(configured))
                return configured;
            foreach (var root in new[] { ModelsDirectory, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "voice-models") })
            {
                try
                {
                    var preferred = Path.Combine(root, ModelName);
                    if (IsModelDirectory(preferred))
                        return preferred;
                    if (Directory.Exists(root))
                        foreach (var d in Directory.GetDirectories(root).OrderBy(d => d))
                            if (IsModelDirectory(d))
                                return d;
                }
                catch (Exception)
                {
                    // inaccessible folder
                }
            }

            return null;
        }

        /// <summary>
        /// Downloads the model zip, verifies its SHA-256 and extracts it into <see cref="ModelsDirectory"/>.
        /// Progress is reported as 0..1. Returns the model directory.
        /// </summary>
        public static async System.Threading.Tasks.Task<string> DownloadAsync(IProgress<double> progress,
            System.Threading.CancellationToken ct)
        {
            Directory.CreateDirectory(ModelsDirectory);
            var zip = Path.Combine(ModelsDirectory, ModelName + ".zip.part");
            using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(30) })
            using (var resp = await http.GetAsync(ModelUrl, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? ModelZipBytes;
                using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var dst = File.Create(zip))
                {
                    var buf = new byte[81920];
                    long done = 0;
                    int n;
                    while ((n = await src.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false)) > 0)
                    {
                        await dst.WriteAsync(buf, 0, n, ct).ConfigureAwait(false);
                        done += n;
                        progress?.Report(Math.Min(0.99, (double)done / total));
                    }
                }
            }

            string hash;
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (var f = File.OpenRead(zip))
                hash = BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
            if (hash != ModelSha256)
            {
                File.Delete(zip);
                throw new InvalidDataException("The downloaded speech model is corrupt (checksum mismatch). Try again.");
            }

            var target = Path.Combine(ModelsDirectory, ModelName);
            if (Directory.Exists(target))
                Directory.Delete(target, true);
            System.IO.Compression.ZipFile.ExtractToDirectory(zip, ModelsDirectory);
            File.Delete(zip);
            progress?.Report(1);
            if (!IsModelDirectory(target))
                throw new InvalidDataException("The speech model archive did not contain " + ModelName + ".");
            return target;
        }
    }
}
