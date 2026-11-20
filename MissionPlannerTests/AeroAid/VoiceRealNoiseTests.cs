using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MissionPlanner.AeroAid.Audio;
using MissionPlanner.AeroAid.Voice;

namespace MissionPlanner.AeroAid.Tests
{
    /// <summary>
    /// Realistic microphone backgrounds. The original pipeline tests used perfectly stationary hiss (uniform +-7 LSB);
    /// a real laptop microphone array (with Windows voice processing / AGC) delivers a background whose 20 ms frame
    /// energy fluctuates by several dB. These generators reproduce that so the voice-activity detector and the whole
    /// pipeline are tested under the conditions in which the operator's log showed failures
    /// ("INCOMPLETE - 2500 ms of your speech was not recognised" for correctly recognised commands).
    /// </summary>
    internal static class RealNoise
    {
        /// <summary>
        /// Gaussian noise whose level changes every 20 ms frame by a random amount (std <paramref name="frameStdDb"/>),
        /// smoothed over ~60 ms - like a room / fan / AGC-pumped laptop-array background.
        /// </summary>
        public static byte[] Fluctuating(double seconds, double meanDb, double frameStdDb, int seed)
        {
            var r = new Random(seed);
            var n = (int)(VoiceActivityDetector.SampleRate * seconds);
            var o = new byte[n * 2];
            double levelDb = meanDb, target = meanDb;
            for (var i = 0; i < n; i++)
            {
                if (i % VoiceActivityDetector.FrameSamples == 0)
                    target = meanDb + Gauss(r) * frameStdDb;
                levelDb += (target - levelDb) * 0.01; // ~6 ms smoothing: each 20 ms frame really has its own level
                var amp = Math.Pow(10, levelDb / 20) * 32768;
                var s = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, Gauss(r) * amp));
                o[2 * i] = (byte)s;
                o[2 * i + 1] = (byte)(s >> 8);
            }

            return o;
        }

        /// <summary>Speech-like test signal: a 180 Hz harmonic vowel with 4 Hz syllable modulation.</summary>
        public static byte[] Vowels(double seconds, double levelDb)
        {
            var n = (int)(VoiceActivityDetector.SampleRate * seconds);
            var o = new byte[n * 2];
            var amp = Math.Pow(10, levelDb / 20) * 32768 * 0.6;
            for (var i = 0; i < n; i++)
            {
                var t = i / (double)VoiceActivityDetector.SampleRate;
                var env = 0.55 + 0.45 * Math.Sin(2 * Math.PI * 4 * t);
                var v = 0.0;
                for (var h = 1; h <= 8; h++) v += Math.Sin(2 * Math.PI * 180 * h * t) / h;
                var s = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, v * amp * env));
                o[2 * i] = (byte)s;
                o[2 * i + 1] = (byte)(s >> 8);
            }

            return o;
        }

        /// <summary>Adds <paramref name="b"/> onto <paramref name="a"/> (same length or shorter).</summary>
        public static byte[] Mix(byte[] a, byte[] b)
        {
            var o = (byte[])a.Clone();
            for (var i = 0; i + 1 < Math.Min(a.Length, b.Length); i += 2)
            {
                var v = (short)(a[i] | (a[i + 1] << 8)) + (short)(b[i] | (b[i + 1] << 8));
                var s = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, v));
                o[i] = (byte)s;
                o[i + 1] = (byte)(s >> 8);
            }

            return o;
        }

        /// <summary>Speech of a TTS voice mixed into fluctuating noise, with noise before and after.</summary>
        public static byte[] InNoise(byte[] speech, double noiseDb, double stdDb, int seed, double lead = 1.0, double tail = 1.5)
        {
            var noise = Fluctuating(lead + speech.Length / 32000.0 + tail, noiseDb, stdDb, seed);
            var offset = (int)(lead * 16000) * 2;
            var padded = new byte[Math.Max(noise.Length, offset + speech.Length)];
            Buffer.BlockCopy(speech, 0, padded, offset, speech.Length);
            if (padded.Length > noise.Length)
            {
                var longer = new byte[padded.Length];
                Buffer.BlockCopy(noise, 0, longer, 0, noise.Length);
                noise = longer;
            }
            return Mix(noise, padded);
        }

        private static double Gauss(Random r)
        {
            var u1 = 1.0 - r.NextDouble();
            var u2 = r.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
        }
    }

    [TestClass]
    public class VoiceRealNoiseTests
    {
        [ClassInitialize]
        public static void Init(TestContext _)
        {
            TestEnv.Init();
            VoiceSettings.Enabled = true;
            VoiceSettings.TestMode = false;
            VoiceSettings.IdleTimeoutSeconds = 0;
            VoiceSettings.Engine = VoiceSettings.EngineVosk;
        }

        /// <summary>Runs the VAD over: noise, 0.7 s speech, noise. Returns (utterance length ms, end delay after speech ms).</summary>
        private static (int lengthMs, int endDelayMs, int count) RunVad(double noiseDb, double stdDb, int seed)
        {
            var vad = new VoiceActivityDetector();
            var processedMs = 0;
            int startMs = -1, endMs = -1, count = 0;
            vad.UtteranceStarted += pre => { if (startMs < 0) startMs = processedMs - pre.Length / 32; };
            vad.UtteranceEnded += valid => { if (endMs < 0 && startMs >= 0) { endMs = processedMs; } count++; };
            var lead = RealNoise.Fluctuating(3.0, noiseDb, stdDb, seed);
            var speech = RealNoise.Mix(RealNoise.Fluctuating(0.7, noiseDb, stdDb, seed + 1), RealNoise.Vowels(0.7, -24));
            var tail = RealNoise.Fluctuating(8.0, noiseDb, stdDb, seed + 2);
            const int speechEndMs = 3700;
            foreach (var part in new[] { lead, speech, tail })
            {
                for (var o = 0; o < part.Length; o += 640)
                {
                    var n = Math.Min(640, part.Length - o);
                    var b = new byte[n];
                    Buffer.BlockCopy(part, o, b, 0, n);
                    vad.Process(b);
                    processedMs += n / 32;
                }
            }

            return (endMs - startMs, endMs - speechEndMs, count);
        }

        /// <summary>
        /// ROOT CAUSE reproduction: in a background whose level fluctuates (real laptop microphone), the utterance must
        /// end shortly after the speech (hang-over ~650 ms), not run on to the 6 s maximum collecting noise.
        /// </summary>
        [TestMethod]
        public void UtteranceEndsSoonAfterSpeechInFluctuatingNoise()
        {
            var bad = new List<string>();
            foreach (var std in new[] { 0.5, 1.5, 3.0, 4.5 })
            foreach (var noiseDb in new[] { -62.0, -50.0, -44.0 })
            foreach (var seed in new[] { 1, 2, 3 })
            {
                var (len, delay, count) = RunVad(noiseDb, std, seed * 10);
                Console.WriteLine($"noise {noiseDb,4} dBFS  frame std {std} dB  seed {seed}: utterance {len,5} ms, ends {delay,5} ms after speech, {count} utterance(s)");
                if (len < 0 || delay > 1100 || delay < 300)
                    bad.Add($"noise {noiseDb} std {std} seed {seed}: length {len} ms, end delay {delay} ms");
            }

            Assert.AreEqual(0, bad.Count, "Utterance did not end promptly after the speech:\n" + string.Join("\n", bad));
        }

        /// <summary>
        /// A background that suddenly becomes much louder (a fan switched on) is taken as one max-length segment and then
        /// learned: afterwards the detector is quiet again and does not re-trigger forever.
        /// </summary>
        [TestMethod]
        public void BackgroundStepIsLearnedAfterOneSegment()
        {
            var vad = new VoiceActivityDetector();
            var n = 0;
            vad.UtteranceEnded += _ => n++;
            vad.Process(RealNoise.Fluctuating(3, -75, 1, 1));
            vad.Process(RealNoise.Fluctuating(20, -48, 3, 2));
            Console.WriteLine($"{n} segment(s) in 20 s after a +27 dB background step; noise floor now {vad.NoiseFloorDb:0} dBFS");
            Assert.IsTrue(n <= 2, n + " segments: the new background was not learned");
            Assert.IsTrue(vad.NoiseFloorDb > -55, "noise floor did not follow the new background");
        }

        /// <summary>No utterance at all from fluctuating background without speech (it must not trigger on noise).</summary>
        [TestMethod]
        public void FluctuatingBackgroundAloneDoesNotProduceUtterances()
        {
            foreach (var std in new[] { 1.5, 3.0 })
            {
                var vad = new VoiceActivityDetector();
                var n = 0;
                vad.UtteranceEnded += valid => { if (valid) n++; };
                vad.Process(RealNoise.Fluctuating(20, -50, std, 5));
                Console.WriteLine($"frame std {std} dB: {n} utterances in 20 s of background");
                Assert.IsTrue(n <= 1, $"frame std {std} dB: {n} utterances from background noise alone");
            }
        }

        // ------------------------------------------------------------------ whole pipeline with Vosk

        private static readonly (string phrase, VoiceCommand cmd)[] Spoken =
        {
            ("Arm.", VoiceCommand.Arm), ("Disarm.", VoiceCommand.Disarm), ("Arm the drone.", VoiceCommand.Arm),
            ("Disarm the drone.", VoiceCommand.Disarm), ("Take off.", VoiceCommand.Takeoff), ("Land.", VoiceCommand.Land),
            ("Return to launch.", VoiceCommand.Rtl), ("Stop.", VoiceCommand.Stop), ("Hold position.", VoiceCommand.Hold),
            ("Aero Aid, arm the drone.", VoiceCommand.Arm), ("Aero Aid, disarm the drone.", VoiceCommand.Disarm),
            ("Take off to five meters.", VoiceCommand.Takeoff), ("Aero Aid, take off five meters.", VoiceCommand.Takeoff)
        };

        /// <summary>
        /// ROOT CAUSE reproduction through the real pipeline: TTS commands in a fluctuating background at a typical laptop
        /// level must be recognised, not rejected as INCOMPLETE, and the decision must arrive within ~1.5 s of the speech end.
        /// </summary>
        [TestMethod]
        public void CommandsInFluctuatingNoiseAreRecognisedPromptly()
        {
            if (!VoiceTestAudio.UseVosk()) Assert.Inconclusive("Vosk model not installed or not a 64-bit test process");
            var voices = VoiceTestAudio.EnglishVoices();
            if (voices.Count == 0) Assert.Inconclusive("No English TTS voice");
            VoiceSettings.RequirePrefix = VoicePrefixMode.Optional;
            var total = 0;
            var misses = new List<string>();
            var lengths = new List<double>();
            var decode = new List<double>();
            foreach (var voice in voices)
            using (var s = new VoiceSession())
            {
                // the session starts on near-silent hiss: a sudden 25 dB louder background is first taken as one
                // 5 s 'utterance' and then learned (see BackgroundStepIsLearnedAfterOneSegment); settle past that
                s.Feed(RealNoise.Fluctuating(8, -50, 3, 99));
                s.Engine.WaitIdle();
                var seed = 100;
                foreach (var (phrase, cmd) in Spoken)
                {
                    total++;
                    var speech = VoiceTestAudio.Speak(phrase, voice);
                    var (heard, commands) = s.Say(RealNoise.InNoise(speech, -50, 3, seed++, 0.3, 0.0), seed);
                    // Say() appends 1.5 s of stationary hiss: replace the tail by realistic noise as well
                    var ok = commands.Count == 1 && commands[0].Match.Command == cmd;
                    foreach (var u in heard) { lengths.Add(u.UtteranceMs); decode.Add(u.DecodeMs); }
                    Console.WriteLine($"[{voice}] {(ok ? "PASS" : "MISS")} {phrase,-20} -> " +
                                      string.Join(" | ", heard.Select(u => $"\"{u.Text}\" utt {u.UtteranceMs:0} ms, decode {u.DecodeMs:0} ms -> {u.Decision}")));
                    if (!ok) misses.Add($"{voice}: {phrase} -> " + string.Join(" | ", heard.Select(u => u.Decision)));
                    Assert.IsTrue(commands.All(c => c.Match.Command == cmd), "WRONG command for " + phrase);
                }
            }

            Console.WriteLine($"Recognised {total - misses.Count}/{total}; utterance length avg {lengths.DefaultIfEmpty(0).Average():0} ms, max {lengths.DefaultIfEmpty(0).Max():0} ms; " +
                              $"decode avg {decode.DefaultIfEmpty(0).Average():0} ms, max {decode.DefaultIfEmpty(0).Max():0} ms");
            Assert.IsTrue(misses.Count <= total / 10, "Too many misses in realistic noise:\n" + string.Join("\n", misses));
            Assert.IsTrue(lengths.Count > 0 && lengths.Max() < 4000, "utterances run on far beyond the speech");
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

        /// <summary>
        /// Low microphone volume (speech about 30 dB quieter, -70 dBFS background) and a noisy room (background
        /// -38 dBFS, fluctuating 4 dB). Reports the recognition rate; asserts that nothing is ever the WRONG command
        /// and that the quiet case still works (auto gain).
        /// </summary>
        [TestMethod]
        public void LowVolumeAndNoisyRoom()
        {
            if (!VoiceTestAudio.UseVosk()) Assert.Inconclusive("Vosk model not installed or not a 64-bit test process");
            var voices = VoiceTestAudio.EnglishVoices();
            if (voices.Count == 0) Assert.Inconclusive("No English TTS voice");
            VoiceSettings.RequirePrefix = VoicePrefixMode.Optional;
            foreach (var (name, gain, noiseDb, std, minRate) in new[] { ("low volume", 0.03, -70.0, 2.0, 0.85), ("noisy room", 1.0, -38.0, 4.0, 0.0) })
            {
                int ok = 0, total = 0, wrong = 0;
                foreach (var voice in voices)
                using (var s = new VoiceSession())
                {
                    s.Feed(RealNoise.Fluctuating(8, noiseDb, std, 7));
                    s.Engine.WaitIdle();
                    var seed = 300;
                    foreach (var (phrase, cmd) in Spoken)
                    {
                        total++;
                        var speech = Scale(VoiceTestAudio.Speak(phrase, voice), gain);
                        var (heard, commands) = s.Say(RealNoise.InNoise(speech, noiseDb, std, seed++, 0.3, 0.0), seed);
                        if (commands.Count == 1 && commands[0].Match.Command == cmd) ok++;
                        if (commands.Any(c => c.Match.Command != cmd)) wrong++;
                        Console.WriteLine($"[{name}/{voice}] {phrase,-32} -> " + string.Join(" | ", heard.Select(u => $"\"{u.Text}\" {u.Decision}")));
                    }
                }

                Console.WriteLine($"{name}: recognised {ok}/{total} ({100.0 * ok / total:0} %), wrong commands {wrong}");
                Assert.AreEqual(0, wrong, name + ": WRONG command executed");
                Assert.IsTrue(ok >= total * minRate, $"{name}: only {ok}/{total} recognised");
            }
        }

        /// <summary>Two commands spoken ~1 s apart are two separate utterances (not merged into one ambiguous sentence).</summary>
        [TestMethod]
        public void RapidConsecutiveCommandsAreSeparate()
        {
            if (!VoiceTestAudio.UseVosk()) Assert.Inconclusive("Vosk model not installed or not a 64-bit test process");
            var voices = VoiceTestAudio.EnglishVoices();
            if (voices.Count == 0) Assert.Inconclusive("No English TTS voice");
            using (var s = new VoiceSession())
            {
                s.Feed(RealNoise.Fluctuating(8, -50, 3, 11));
                s.Engine.WaitIdle();
                var a = VoiceTestAudio.Speak("Arm.", voices[0]);
                var gap = new byte[(int)(0.9 * 16000) * 2];
                var b = VoiceTestAudio.Speak("Land.", voices[0]);
                var both = a.Concat(gap).Concat(b).ToArray();
                var (heard, commands) = s.Say(RealNoise.InNoise(both, -50, 3, 12, 0.3, 0.0), 13);
                Console.WriteLine(string.Join(" | ", heard.Select(u => $"\"{u.Text}\" {u.Decision}")));
                CollectionAssert.AreEqual(new[] { VoiceCommand.Arm, VoiceCommand.Land }, commands.Select(c => c.Match.Command).ToArray());
            }
        }
    }
}
