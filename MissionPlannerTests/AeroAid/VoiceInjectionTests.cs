using System;
using System.IO;
using System.Linq;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
using System.Speech.Synthesis;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MissionPlanner.AeroAid.Audio;
using MissionPlanner.AeroAid.Voice;

namespace MissionPlanner.AeroAid.Tests
{
    /// <summary>
    /// Injects synthesised speech (Windows TTS) into a recogniser configured exactly like the app's voice engine
    /// (same grammar, same PCM format as the microphone capture) and applies the app's confidence threshold.
    /// No microphone and no vehicle are involved; nothing is sent anywhere.
    /// </summary>
    [TestClass]
    public class VoiceInjectionTests
    {
        private static RecognizerInfo recognizer;
        private static readonly SpeechAudioFormatInfo Format =
            new SpeechAudioFormatInfo(WaveInCapture.SampleRate, AudioBitsPerSample.Sixteen, AudioChannel.Mono);

        [ClassInitialize]
        public static void Init(TestContext _)
        {
            TestEnv.Init();
            var all = SpeechRecognitionEngine.InstalledRecognizers();
            recognizer = all.FirstOrDefault(r => r.Culture.Name == "en-US") ??
                         all.FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName == "en");
        }

        private static byte[] Speak(string phrase)
        {
            using (var synth = new SpeechSynthesizer())
            using (var ms = new MemoryStream())
            {
                var voice = synth.GetInstalledVoices().FirstOrDefault(v => v.Enabled && v.VoiceInfo.Culture.TwoLetterISOLanguageName == "en");
                if (voice == null) Assert.Inconclusive("No English TTS voice installed");
                synth.SelectVoice(voice.VoiceInfo.Name);
                synth.SetOutputToAudioStream(ms, Format);
                synth.Speak(phrase);
                synth.SetOutputToNull();
                // trailing silence so the recogniser detects the end of the utterance
                var pcm = ms.ToArray();
                var padded = new byte[pcm.Length + WaveInCapture.SampleRate * 2];
                Buffer.BlockCopy(pcm, 0, padded, 0, pcm.Length);
                return padded;
            }
        }

        /// <summary>Returns the takeoff command text if the app would accept it, otherwise null.</summary>
        private static string Inject(string phrase, out float confidence)
        {
            if (recognizer == null) Assert.Inconclusive("No English speech recogniser installed");
            confidence = 0;
            using (var engine = new SpeechRecognitionEngine(recognizer))
            {
                var gb = new GrammarBuilder(new Choices(VoiceCommandEngine.TakeoffPhrases)) { Culture = recognizer.Culture };
                engine.LoadGrammar(new Grammar(gb) { Name = "takeoff" });
                engine.SetInputToAudioStream(new MemoryStream(Speak(phrase)), Format);
                var result = engine.Recognize(TimeSpan.FromSeconds(10));
                if (result == null) return null;
                confidence = result.Confidence;
                return confidence >= VoiceSettings.MinConfidence ? result.Text : null;
            }
        }

        [TestMethod]
        public void SpokenTakeoffPhrasesAreAccepted()
        {
            VoiceSettings.MinConfidence = 0.75;
            foreach (var phrase in new[] { "take off", "drone take off", "initiate takeoff" })
            {
                var text = Inject(phrase, out var conf);
                Console.WriteLine($"'{phrase}' -> '{text}' ({conf:P0})");
                Assert.IsNotNull(text, $"'{phrase}' was not accepted (confidence {conf:P0})");
                StringAssert.Contains(text, "take");
            }
        }

        [TestMethod]
        public void UnrelatedSpeechNeverTriggersTakeoff()
        {
            VoiceSettings.MinConfidence = 0.75;
            foreach (var phrase in new[] { "land now", "return to launch", "disarm the motors", "what is the battery level", "hello there" })
            {
                var text = Inject(phrase, out var conf);
                Console.WriteLine($"'{phrase}' -> '{text}' ({conf:P0})");
                Assert.IsNull(text, $"'{phrase}' was wrongly accepted as '{text}' ({conf:P0})");
            }
        }
    }
}
