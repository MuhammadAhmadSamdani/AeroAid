using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
using System.Speech.Synthesis;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MissionPlanner.AeroAid.Audio;
using MissionPlanner.AeroAid.Voice;

namespace MissionPlanner.AeroAid.Tests
{
    /// <summary>A PCM source that stands in for the microphone.</summary>
    internal sealed class FakePcmSource : IPcmSource
    {
        public bool IsOn { get; private set; } = true;
        public string Status => IsOn ? "test feed" : "test feed off";
        public bool TurnOn() => IsOn = true;
        public event Action<byte[]> AudioData;
        public event Action StateChanged;
        public void Feed(byte[] pcm) => AudioData?.Invoke(pcm);

        public void Disconnect()
        {
            IsOn = false;
            StateChanged?.Invoke();
        }
    }

    /// <summary>Shared helpers: speech synthesis, the Vosk model location, a listening session.</summary>
    internal static class VoiceTestAudio
    {
        public const int Rate = WaveInCapture.SampleRate;
        public static readonly SpeechAudioFormatInfo Format = new SpeechAudioFormatInfo(Rate, AudioBitsPerSample.Sixteen, AudioChannel.Mono);

        /// <summary>
        /// The Vosk model for tests: AEROAID_VOSK_MODEL, else the model installed in the real Mission Planner data
        /// folder (the tests run with an isolated temporary data folder).
        /// </summary>
        public static string FindModel()
        {
            var env = Environment.GetEnvironmentVariable("AEROAID_VOSK_MODEL");
            if (VoskModelStore.IsModelDirectory(env)) return env;
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var real = Path.Combine(docs, "Mission Planner", "AeroAid", "models", "voice", VoskModelStore.ModelName);
            return VoskModelStore.IsModelDirectory(real) ? real : null;
        }

        public static bool UseVosk()
        {
            var m = FindModel();
            if (m == null || !Environment.Is64BitProcess) return false;
            VoiceSettings.VoskModelPath = m;
            return VoskCommandRecognizer.CheckAvailable(out _) == null;
        }

        public static byte[] Speak(string phrase, string voice, int rate = 0)
        {
            using (var synth = new SpeechSynthesizer())
            using (var ms = new MemoryStream())
            {
                synth.SelectVoice(voice);
                synth.Rate = rate;
                synth.SetOutputToAudioStream(ms, Format);
                synth.Speak(phrase);
                synth.SetOutputToNull();
                return ms.ToArray();
            }
        }

        public static byte[] Silence(double seconds) => new byte[(int)(Rate * seconds) * 2];

        /// <summary>Low-level microphone self-noise (a real microphone never delivers digital zero).</summary>
        public static byte[] Hiss(double seconds, int seed)
        {
            var r = new Random(seed);
            var b = new byte[(int)(Rate * seconds) * 2];
            for (var i = 0; i < b.Length; i += 2)
            {
                var s = (short)(r.Next(-7, 8));
                b[i] = (byte)s;
                b[i + 1] = (byte)(s >> 8);
            }

            return b;
        }

        public static List<string> EnglishVoices()
        {
            using (var s = new SpeechSynthesizer())
                return s.GetInstalledVoices().Where(v => v.Enabled && v.VoiceInfo.Culture.TwoLetterISOLanguageName == "en")
                    .Select(v => v.VoiceInfo.Name).ToList();
        }

        /// <summary>Reads a 16 kHz 16-bit mono WAV file (data chunk only).</summary>
        public static byte[] ReadWav(string path)
        {
            var b = File.ReadAllBytes(path);
            var i = 12;
            while (i + 8 <= b.Length)
            {
                var id = Encoding.ASCII.GetString(b, i, 4);
                var len = BitConverter.ToInt32(b, i + 4);
                if (id == "data")
                    return b.Skip(i + 8).Take(Math.Min(len, b.Length - i - 8)).ToArray();
                i += 8 + len + (len & 1);
            }

            throw new InvalidDataException("no data chunk: " + path);
        }
    }

    /// <summary>A listening engine fed from a fake microphone.</summary>
    internal sealed class VoiceSession : IDisposable
    {
        public readonly FakePcmSource Source = new FakePcmSource();
        public readonly VoiceCommandEngine Engine;
        public readonly ConcurrentQueue<VoiceUtterance> Heard = new ConcurrentQueue<VoiceUtterance>();
        public readonly ConcurrentQueue<VoiceUtterance> Commands = new ConcurrentQueue<VoiceUtterance>();

        public VoiceSession(Func<ICommandRecognizer> factory = null)
        {
            Engine = new VoiceCommandEngine(Source) { RecognizerFactory = factory };
            Engine.UtteranceRecognized += u => Heard.Enqueue(u);
            Engine.CommandDetected += u => Commands.Enqueue(u);
            Assert.IsTrue(Engine.StartListening(), Engine.Status);
            Feed(VoiceTestAudio.Hiss(1.0, 1)); // let the noise floor settle
            Engine.WaitIdle();
        }

        /// <summary>Feeds audio in 100 ms chunks (as fast as the engine accepts it).</summary>
        public void Feed(byte[] pcm)
        {
            const int chunk = VoiceTestAudio.Rate * 2 / 10;
            for (var o = 0; o < pcm.Length; o += chunk)
            {
                var n = Math.Min(chunk, pcm.Length - o);
                var b = new byte[n];
                Buffer.BlockCopy(pcm, o, b, 0, n);
                Source.Feed(b);
            }
        }

        /// <summary>Feeds one spoken phrase plus trailing silence and returns every final result / command for it.</summary>
        public (List<VoiceUtterance> heard, List<VoiceUtterance> commands) Say(byte[] speech, int seed = 2)
        {
            while (Heard.TryDequeue(out _)) { }
            while (Commands.TryDequeue(out _)) { }
            Feed(speech);
            Feed(VoiceTestAudio.Hiss(1.5, seed));
            Assert.IsTrue(Engine.WaitIdle(20000), "engine did not keep up");
            return (Heard.ToList(), Commands.ToList());
        }

        public void Dispose() => Engine.StopListening();
    }

    /// <summary>
    /// Speech recognition end to end through the app's own pipeline: Windows TTS speech is streamed from a fake
    /// microphone through VoiceCommandEngine (high-pass + voice activity detection → Vosk command vocabulary →
    /// classifier → confidence gate). No real microphone and no vehicle are involved; nothing is sent anywhere.
    /// </summary>
    [TestClass]
    public class VoiceInjectionTests
    {
        private static List<string> voices = new List<string>();
        private static bool vosk;

        [ClassInitialize]
        public static void Init(TestContext _)
        {
            TestEnv.Init();
            VoiceSettings.Enabled = true;
            VoiceSettings.TestMode = false; // CommandDetected is asserted on (nothing is connected to send to)
            VoiceSettings.MinConfidence = 0.70;
            VoiceSettings.IdleTimeoutSeconds = 0;
            VoiceSettings.Engine = VoiceSettings.EngineVosk;
            voices = VoiceTestAudio.EnglishVoices();
            vosk = VoiceTestAudio.UseVosk();
        }

        private static void RequireVosk()
        {
            if (!vosk) Assert.Inconclusive("Vosk model not installed (set AEROAID_VOSK_MODEL) or not a 64-bit test process (/Platform:x64)");
            if (voices.Count == 0) Assert.Inconclusive("No English TTS voice installed");
        }

        private static string Describe(List<VoiceUtterance> heard) =>
            heard.Count == 0 ? "(nothing)" : string.Join(" | ", heard.Select(u => $"\"{u.Text}\" {u.Confidence:P0} -> {u.Decision}"));

        private static readonly (string phrase, VoiceCommand cmd)[] Positives =
        {
            ("Arm the drone.", VoiceCommand.Arm), ("Drone arm.", VoiceCommand.Arm), ("Arm.", VoiceCommand.Arm),
            ("Please arm the drone.", VoiceCommand.Arm),
            ("Disarm the drone.", VoiceCommand.Disarm), ("Drone disarm.", VoiceCommand.Disarm), ("Disarm.", VoiceCommand.Disarm),
            ("Please disarm the drone.", VoiceCommand.Disarm),
            ("Take off.", VoiceCommand.Takeoff), ("Drone take off.", VoiceCommand.Takeoff), ("Take off now.", VoiceCommand.Takeoff),
            ("Stop.", VoiceCommand.Stop), ("Hold position.", VoiceCommand.Hold), ("Return to launch.", VoiceCommand.Rtl),
            ("Land.", VoiceCommand.Land), ("Drone land.", VoiceCommand.Land)
        };

        private static readonly string[] Negatives =
        {
            "Do not arm the drone.", "Do not disarm the drone.", "Disarm and arm the drone.", "Do not take off.",
            "Don't land.", "Is the drone armed?", "My arm hurts.", "The weather is nice today.", "What is the battery level?",
            "Hello there, how are you?", "Hold on a second.", "The alarm is ringing."
        };

        [TestMethod]
        public void SpokenCommandsAreRecognisedThroughTheLivePipeline()
        {
            RequireVosk();
            var failures = new List<string>();
            var total = 0;
            foreach (var voice in voices)
            {
                using (var s = new VoiceSession())
                {
                    StringAssert.StartsWith(s.Engine.EngineName, "Vosk");
                    foreach (var rate in new[] { 0, 3, -3 })
                    foreach (var (phrase, cmd) in Positives)
                    {
                        total++;
                        var (heard, commands) = s.Say(VoiceTestAudio.Speak(phrase, voice, rate));
                        var got = commands.Select(c => c.Match.Command).ToList();
                        var ok = got.Count == 1 && got[0] == cmd;
                        Console.WriteLine($"[{voice} rate {rate,2}] {(ok ? "PASS" : "MISS")} {phrase,-28} expect {VoiceCommandParser.Name(cmd),-7} -> {Describe(heard)}");
                        if (!ok) failures.Add($"{voice}/{rate}: {phrase} -> {Describe(heard)}");
                        // never the WRONG command
                        Assert.IsTrue(got.All(g => g == cmd), $"\"{phrase}\" produced a wrong command: {Describe(heard)}");
                        Assert.IsTrue(got.Count <= 1, $"\"{phrase}\" produced {got.Count} commands");
                    }
                }
            }

            Console.WriteLine($"Recognised {total - failures.Count}/{total}");
            // at least 95 % of these clear TTS commands must be recognised (misses are 'please repeat', never wrong)
            Assert.IsTrue(failures.Count <= total * 0.05, "Too many misses:\n" + string.Join("\n", failures));
        }

        [TestMethod]
        public void NegatedAmbiguousAndUnrelatedSpeechNeverProducesACommand()
        {
            RequireVosk();
            foreach (var voice in voices)
            {
                using (var s = new VoiceSession())
                {
                    foreach (var phrase in Negatives)
                    {
                        var (heard, commands) = s.Say(VoiceTestAudio.Speak(phrase, voice));
                        Console.WriteLine($"[{voice}] {(commands.Count == 0 ? "PASS" : "FAIL")} {phrase,-32} -> {Describe(heard)}");
                        Assert.AreEqual(0, commands.Count, $"[{voice}] \"{phrase}\" produced a command: {Describe(heard)}");
                    }
                }
            }
        }

        [TestMethod]
        public void TestModeShowsTheCommandButNeverRaisesIt()
        {
            RequireVosk();
            VoiceSettings.TestMode = true;
            try
            {
                VoiceTestSession.Instance.Clear();
                VoiceTestSession.Instance.SetTarget(VoiceTestTarget.Arm);
                using (var s = new VoiceSession())
                {
                    var (heard, commands) = s.Say(VoiceTestAudio.Speak("Arm the drone.", voices[0]));
                    Assert.AreEqual(0, commands.Count, "Test Mode must never raise CommandDetected");
                    Assert.IsTrue(heard.Any(u => u.TestMode && u.AcceptedCommand == VoiceCommand.Arm), Describe(heard));
                    var a = VoiceTestSession.Instance.Attempts.Last();
                    Assert.AreEqual("ARM", a.Detected);
                    Assert.AreEqual(true, a.Correct);
                    var report = VoiceTestSession.Instance.SaveReport("fake microphone", s.Engine.EngineName);
                    StringAssert.Contains(File.ReadAllText(report), "| ARM | 1 | 1 | 100.0 % |");
                }
            }
            finally
            {
                VoiceSettings.TestMode = false;
                VoiceTestSession.Instance.SetTarget(VoiceTestTarget.Free);
            }
        }

        [TestMethod]
        public void QuietBackgroundTalkAroundACommandIsIgnored()
        {
            RequireVosk();
            var voice = voices[0];
            var other = voices.Count > 1 ? voices[1] : voices[0];
            using (var s = new VoiceSession())
            {
                // background sentence 24 dB below the command, directly before it
                var bg = Scale(VoiceTestAudio.Speak("hello there, nice weather today", other), 0.063);
                var cmd = VoiceTestAudio.Speak("land the drone", voice);
                // repeated: word times must stay aligned with the audio after many utterances (Vosk counts from creation)
                for (var i = 0; i < 3; i++)
                {
                    var (heard, commands) = s.Say(bg.Concat(cmd).ToArray(), 10 + i);
                    Console.WriteLine(Describe(heard));
                    Assert.AreEqual(1, commands.Count, $"round {i}: " + Describe(heard));
                    Assert.AreEqual(VoiceCommand.Land, commands[0].Match.Command);
                }

                // a QUIET negation is never dropped: "do not" quiet + "arm the drone" loud -> rejected
                var neg = Scale(VoiceTestAudio.Speak("do not", voice), 0.063).Concat(VoiceTestAudio.Speak("arm the drone", voice)).ToArray();
                var (heard2, commands2) = s.Say(neg, 7);
                Console.WriteLine(Describe(heard2));
                Assert.AreEqual(0, commands2.Count, "quiet negation must still cancel the command: " + Describe(heard2));
            }
        }

        private static byte[] Scale(byte[] pcm, double gain)
        {
            var o = new byte[pcm.Length];
            for (var i = 0; i + 1 < pcm.Length; i += 2)
            {
                var v = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, (short)(pcm[i] | (pcm[i + 1] << 8)) * gain));
                o[i] = (byte)v;
                o[i + 1] = (byte)(v >> 8);
            }

            return o;
        }

        [TestMethod]
        public void WindowsSpeechFallbackRecognisesUsVoices()
        {
            if (SapiCommandRecognizer.FindRecognizer() == null) Assert.Inconclusive("No English Windows recogniser installed");
            var voice = voices.FirstOrDefault(v => v.Contains("David")) ?? voices.FirstOrDefault();
            if (voice == null) Assert.Inconclusive("No TTS voice");
            var hits = 0;
            using (var s = new VoiceSession(SapiCommandRecognizer.Create))
            {
                StringAssert.StartsWith(s.Engine.EngineName, "Windows speech");
                foreach (var (phrase, cmd) in Positives)
                {
                    var (heard, commands) = s.Say(VoiceTestAudio.Speak(phrase, voice));
                    var got = commands.Select(c => c.Match.Command).ToList();
                    if (got.Count == 1 && got[0] == cmd) hits++;
                    Console.WriteLine($"[SAPI {voice}] {phrase,-28} -> {Describe(heard)}");
                    Assert.IsTrue(got.All(g => g == cmd), $"wrong command for \"{phrase}\": {Describe(heard)}");
                }
            }

            Console.WriteLine($"Windows speech fallback: {hits}/{Positives.Length}");
            Assert.IsTrue(hits >= Positives.Length * 0.75, $"fallback recognised only {hits}/{Positives.Length}");
        }

        [TestMethod]
        public void MicrophoneLossStopsListening()
        {
            if (SapiCommandRecognizer.FindRecognizer() == null && !vosk) Assert.Inconclusive("No recogniser");
            var s = vosk ? new VoiceSession() : new VoiceSession(SapiCommandRecognizer.Create);
            try
            {
                Assert.IsTrue(s.Engine.Listening);
                s.Source.Disconnect();
                var until = DateTime.UtcNow.AddSeconds(5);
                while (s.Engine.Listening && DateTime.UtcNow < until)
                    Thread.Sleep(50);
                Assert.IsFalse(s.Engine.Listening, "engine must stop when the microphone goes away");
                StringAssert.Contains(s.Engine.Status, "microphone");
            }
            finally
            {
                s.Dispose();
            }
        }

        [TestMethod]
        public void FallbackGrammarsLoadIntoTheWindowsRecogniser()
        {
            var r = SapiCommandRecognizer.FindRecognizer();
            if (r == null) Assert.Inconclusive("No English speech recogniser installed");
            using (var e = new SpeechRecognitionEngine(r))
            {
                foreach (var g in VoiceCommandEngine.BuildGrammars(r.Culture))
                    e.LoadGrammar(g);
                CollectionAssert.AreEquivalent(new[] { SapiCommandRecognizer.CommandGrammarName, SapiCommandRecognizer.DecoyGrammarName },
                    e.Grammars.Select(g => g.Name).ToArray());
                Assert.IsFalse(e.Grammars.Any(g => g is DictationGrammar), "free dictation must not be loaded");
            }
        }

        [TestMethod]
        public void MissingModelFallsBackToWindowsSpeechWithAWarning()
        {
            var saved = VoiceSettings.VoskModelPath;
            try
            {
                VoiceSettings.VoskModelPath = Path.Combine(TestEnv.Root, "no-model-here");
                if (VoskModelStore.FindModel() != null) Assert.Inconclusive("a model is installed in the test data folder");
                var (name, warning) = VoiceCommandEngine.DescribeEngine();
                StringAssert.Contains(name, "Windows speech");
                StringAssert.Contains(warning, "Vosk unavailable");
            }
            finally
            {
                VoiceSettings.VoskModelPath = saved;
            }
        }
    }

    /// <summary>
    /// Large-corpus run of the real pipeline (set AEROAID_VOICE_CORPUS to a folder with one sub-folder per noise
    /// condition containing LABEL__phrase__voice__rate.wav files, and AEROAID_VOICE_RESULTS to the output JSONL).
    /// Used for VOICE_CONTROL_TEST_REPORT.md; skipped when the variables are not set.
    /// </summary>
    [TestClass]
    public class VoiceCorpusTests
    {
        [TestMethod]
        public void RunCorpusThroughThePipeline()
        {
            TestEnv.Init();
            var corpus = Environment.GetEnvironmentVariable("AEROAID_VOICE_CORPUS");
            var output = Environment.GetEnvironmentVariable("AEROAID_VOICE_RESULTS");
            if (string.IsNullOrEmpty(corpus) || string.IsNullOrEmpty(output)) Assert.Inconclusive("AEROAID_VOICE_CORPUS / _RESULTS not set");
            var engineKind = Environment.GetEnvironmentVariable("AEROAID_VOICE_ENGINE") ?? "vosk";
            VoiceSettings.Enabled = true;
            VoiceSettings.TestMode = true;
            VoiceSettings.MinConfidence = 0.70;
            VoiceSettings.SuppressBackgroundSpeech = Environment.GetEnvironmentVariable("AEROAID_VOICE_SUPPRESS") != "0";
            if (engineKind == "vosk" && !VoiceTestAudio.UseVosk()) Assert.Inconclusive("Vosk not available");
            var name = "pipeline_" + engineKind + (VoiceSettings.SuppressBackgroundSpeech ? "" : "_nosuppress");
            var lines = new ConcurrentBag<string>();
            var conds = Directory.GetDirectories(corpus).OrderBy(d => d).ToList();
            Parallel.ForEach(conds, new ParallelOptions { MaxDegreeOfParallelism = 6 }, dir =>
            {
                var cond = Path.GetFileName(dir);
                using (var s = engineKind == "vosk" ? new VoiceSession() : new VoiceSession(SapiCommandRecognizer.Create))
                {
                    foreach (var f in Directory.GetFiles(dir, "*.wav").OrderBy(x => x))
                    {
                        var file = Path.GetFileName(f);
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        var (heard, _) = s.Say(VoiceTestAudio.ReadWav(f));
                        var accepted = heard.Where(u => u.Actionable).Select(u => VoiceCommandParser.Name(u.AcceptedCommand)).Distinct().ToList();
                        var text = string.Join(" | ", heard.Select(u => u.Text));
                        var conf = heard.Count == 0 ? 0 : heard.Max(u => u.Confidence);
                        lines.Add("{" + $"\"engine\":\"{name}\",\"cond\":\"{cond}\",\"file\":\"{file}\",\"label\":\"{file.Split(new[] { "__" }, StringSplitOptions.None)[0]}\"," +
                                  $"\"text\":\"{Esc(text)}\",\"conf\":{conf.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)}," +
                                  $"\"accepted\":\"{string.Join(",", accepted)}\",\"decisions\":\"{Esc(string.Join(" | ", heard.Select(u => u.Decision)))}\"," +
                                  $"\"ms\":{sw.ElapsedMilliseconds}" + "}");
                    }
                }

                Console.WriteLine(cond + " done");
            });
            File.WriteAllLines(output, lines.OrderBy(l => l));
            Console.WriteLine($"{lines.Count} results -> {output}");
        }

        private static string Esc(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    /// <summary>Result handling of the engine (no audio): de-duplication, threshold, split negations, events.</summary>
    [TestClass]
    public class VoiceEngineTests
    {
        [ClassInitialize]
        public static void Init(TestContext _)
        {
            TestEnv.Init();
            VoiceSettings.MinConfidence = 0.65;
            VoiceSettings.TestMode = false;
        }

        private static (VoiceCommandEngine e, List<VoiceUtterance> commands) Make()
        {
            VoiceSettings.TestMode = false;
            var e = new VoiceCommandEngine(new FakePcmSource());
            var list = new List<VoiceUtterance>();
            e.CommandDetected += u => list.Add(u);
            return (e, list);
        }

        private static TimeSpan S(double s) => TimeSpan.FromSeconds(s);

        [TestMethod]
        public void SameUtteranceIsProcessedOnce()
        {
            var (e, cmds) = Make();
            Assert.IsNotNull(e.ProcessFinal("arm the drone", 0.9f, "vosk", S(5)));
            Assert.IsNull(e.ProcessFinal("arm the drone", 0.9f, "vosk", S(5)), "same audio position = same utterance");
            Assert.AreEqual(1, cmds.Count);
            Assert.IsNotNull(e.ProcessFinal("arm the drone", 0.9f, "vosk", S(20)));
            Assert.AreEqual(2, cmds.Count);
            Assert.AreNotEqual(cmds[0].Id, cmds[1].Id);
        }

        [TestMethod]
        public void LowConfidenceIsNeverACommand()
        {
            var (e, cmds) = Make();
            var u = e.ProcessFinal("take off", 0.5f, "vosk", S(1));
            Assert.IsFalse(u.Actionable);
            Assert.IsTrue(u.Match.IsCommand, "detected but not trusted");
            StringAssert.Contains(u.Decision, "below");
            StringAssert.Contains(u.Decision, "repeat");
            Assert.AreEqual(0, cmds.Count);
            Assert.IsTrue(e.ProcessFinal("take off", 0.66f, "vosk", S(5)).Actionable);
        }

        [TestMethod]
        public void ConfidenceIsTakenFromTheCommandWords()
        {
            var r = new SpeechResult("please arm the drone", 0.3f,
                new[] { new SpeechWord("please", 0.3f), new SpeechWord("arm", 0.95f), new SpeechWord("the", 1f), new SpeechWord("drone", 0.4f) }, "vosk");
            Assert.AreEqual(0.95f, r.ConfidenceFor(VoiceCommandParser.Parse(r.Text)), 1e-6);
            var r2 = new SpeechResult("take off", 0.5f, new[] { new SpeechWord("take", 0.9f), new SpeechWord("off", 0.5f) }, "vosk");
            Assert.AreEqual(0.5f, r2.ConfidenceFor(VoiceCommandParser.Parse(r2.Text)), 1e-6, "weakest command word counts");
        }

        [TestMethod]
        public void RecogniserRejectedResultsAreNeverCommands()
        {
            var (e, cmds) = Make();
            var u = e.ProcessFinal("arm", 0.95f, "rejected", S(1), null, true);
            Assert.IsFalse(u.Actionable);
            Assert.AreEqual(0, cmds.Count);
        }

        [TestMethod]
        public void NegationSplitByAPauseStillCancelsTheCommand()
        {
            var (e, cmds) = Make();
            e.ProcessFinal("do not", 0.9f, "vosk", S(10), S(0.6));
            var u = e.ProcessFinal("arm the drone", 0.9f, "vosk", S(11.5));
            Assert.AreEqual(VoiceMatchOutcome.Negated, u.Match.Outcome);
            Assert.AreEqual(0, cmds.Count);
            Assert.IsTrue(e.ProcessFinal("arm the drone", 0.9f, "vosk", S(30)).Actionable);
        }

        [TestMethod]
        public void NonCommandsRaiseNoCommandEvent()
        {
            var (e, cmds) = Make();
            var pos = 1.0;
            foreach (var t in new[] { "do not arm", "disarm and arm", "is it armed", "hello", "my arm hurts", "[unk] land", "" })
                e.ProcessFinal(t, 0.95f, "vosk", S(pos += 5));
            Assert.AreEqual(0, cmds.Count);
        }

        [TestMethod]
        public void TestModeNeverRaisesCommandDetected()
        {
            var (e, cmds) = Make();
            VoiceSettings.TestMode = true;
            try
            {
                var u = e.ProcessFinal("arm the drone", 0.95f, "vosk", S(3));
                Assert.IsTrue(u.TestMode);
                Assert.AreEqual(VoiceCommand.Arm, u.AcceptedCommand);
                StringAssert.Contains(u.Decision, "test mode");
                Assert.AreEqual(0, cmds.Count);
            }
            finally
            {
                VoiceSettings.TestMode = false;
            }
        }

        // ---- background-speech suppression (word energies from the voice-activity detector)
        private static List<double> Frames(params (double from, double to, double db)[] spans)
        {
            var f = Enumerable.Repeat(-70.0, 150).ToList(); // 3 s
            foreach (var (a, b, db) in spans)
                for (var i = (int)(a / 0.02); i < (int)(b / 0.02); i++) f[i] = db;
            return f;
        }

        [TestMethod]
        public void SpeechNotCoveredByRecognisedWordsMakesTheResultIncomplete()
        {
            // 1.8 s of speech, but the recogniser returned only "disarm" (0.1-0.6 s): the rest was lost
            var frames = Frames((0.1, 1.9, -25));
            var partial = new SpeechResult("disarm", 1f, new[] { new SpeechWord("disarm", 1f, 0.1, 0.6) }, "vosk");
            Assert.IsTrue(partial.UncoveredSpeechMs(frames, -70) > 800);
            var full = new SpeechResult("disarm and arm the drone", 1f, new[]
            {
                new SpeechWord("disarm", 1f, 0.1, 0.6), new SpeechWord("and", 1f, 0.6, 0.8), new SpeechWord("arm", 1f, 0.8, 1.2),
                new SpeechWord("the", 1f, 1.2, 1.35), new SpeechWord("drone", 1f, 1.35, 1.9)
            }, "vosk");
            Assert.AreEqual(0, full.UncoveredSpeechMs(frames, -70), 1e-9);

            var (e, cmds) = Make();
            var u = e.ProcessResult(partial, S(40), S(2), "INCOMPLETE - 900 ms of your speech was not recognised (words were lost). Please repeat.");
            Assert.IsFalse(u.Actionable);
            StringAssert.Contains(u.Decision, "INCOMPLETE");
            Assert.AreEqual(0, cmds.Count);
        }

        [TestMethod]
        public void AutoGainBringsQuietSpeechToTheTargetWithoutClipping()
        {
            Assert.AreEqual(20, 20 * Math.Log10(VoiceCommandEngine.ComputeGain(-40, -25)), 1e-6);
            Assert.AreEqual(17, 20 * Math.Log10(VoiceCommandEngine.ComputeGain(-45, -18)), 1e-6, "peak limit -1 dBFS");
            Assert.AreEqual(1, VoiceCommandEngine.ComputeGain(-15, -3), 1e-9, "never attenuates");
            Assert.AreEqual(30, 20 * Math.Log10(VoiceCommandEngine.ComputeGain(-70, -50)), 1e-6, "at most +30 dB");
            var pcm = new byte[] { 0x00, 0x40, 0x00, 0xC0 }; // +16384, -16384
            var g = VoiceCommandEngine.ApplyGain(pcm, 4);
            Assert.AreEqual(short.MaxValue, (short)(g[0] | (g[1] << 8)));
            Assert.AreEqual(short.MinValue, (short)(g[2] | (g[3] << 8)));
        }

        [TestMethod]
        public void QuietFillerWordsAreDroppedButNeverCommandsOrNegations()
        {
            var words = new[]
            {
                new SpeechWord("hello", 1f, 0.1, 0.4), new SpeechWord("weather", 1f, 0.5, 0.9),
                new SpeechWord("land", 1f, 1.2, 1.5), new SpeechWord("the", 1f, 1.5, 1.6), new SpeechWord("drone", 1f, 1.6, 2.0)
            };
            var r = new SpeechResult("hello weather land the drone", 1f, words, "vosk")
                .WithoutBackgroundWords(Frames((0.1, 0.9, -45), (1.2, 2.0, -22)));
            Assert.AreEqual("land the drone", r.Text);
            Assert.AreEqual("hello weather", r.DroppedBackground);
            Assert.AreEqual(VoiceCommand.Land, VoiceCommandParser.Parse(r.Text).Command);

            // quiet "do not" before a loud command: kept -> the sentence stays negated
            var neg = new SpeechResult("do not arm the drone", 1f, new[]
            {
                new SpeechWord("do", 1f, 0.1, 0.3), new SpeechWord("not", 1f, 0.3, 0.6), new SpeechWord("arm", 1f, 1.2, 1.5),
                new SpeechWord("the", 1f, 1.5, 1.6), new SpeechWord("drone", 1f, 1.6, 2.0)
            }, "vosk").WithoutBackgroundWords(Frames((0.1, 0.6, -50), (1.2, 2.0, -20)));
            Assert.AreEqual("do not arm the drone", neg.Text);
            Assert.AreEqual(VoiceMatchOutcome.Negated, VoiceCommandParser.Parse(neg.Text).Outcome);

            // quiet second command: kept -> ambiguous
            var amb = new SpeechResult("disarm arm", 1f, new[] { new SpeechWord("disarm", 1f, 0.1, 0.5), new SpeechWord("arm", 1f, 1.2, 1.5) }, "vosk")
                .WithoutBackgroundWords(Frames((0.1, 0.5, -50), (1.2, 1.5, -20)));
            Assert.AreEqual(VoiceMatchOutcome.Ambiguous, VoiceCommandParser.Parse(amb.Text).Outcome);

            // similar levels: nothing dropped
            var same = new SpeechResult("my arm hurts", 1f, new[] { new SpeechWord("my", 1f, 0.1, 0.3), new SpeechWord("arm", 1f, 0.3, 0.6), new SpeechWord("[unk]", 1f, 0.6, 1.0) }, "vosk")
                .WithoutBackgroundWords(Frames((0.1, 1.0, -25)));
            Assert.AreEqual("", same.DroppedBackground);
            Assert.IsFalse(VoiceCommandParser.Parse(same.Text).IsCommand);
        }
    }
}
