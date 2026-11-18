using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Speech.Synthesis;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MissionPlanner.AeroAid.Audio;
using MissionPlanner.AeroAid.Voice;

namespace MissionPlanner.AeroAid.Tests
{
    /// <summary>
    /// ACOUSTIC loop-back test with the REAL microphone: command phrases are played through the PC speakers and
    /// captured by the real microphone through MicrophoneManager / WaveInCapture, then recognised by the app's
    /// pipeline. Tests the actual device capture path and room acoustics - NOT a human voice.
    /// Plays sound: runs only when AEROAID_LIVE_MIC=1 (optionally AEROAID_LIVE_MIC_DEVICE = microphone name).
    /// Always in TEST MODE: nothing can be sent anywhere.
    /// </summary>
    [TestClass]
    public class VoiceLiveMicTests
    {
        [TestMethod]
        public void SpeakerToMicrophoneLoopback()
        {
            if (Environment.GetEnvironmentVariable("AEROAID_LIVE_MIC") != "1")
                Assert.Inconclusive("Set AEROAID_LIVE_MIC=1 to run the acoustic loop-back test (plays sound).");
            TestEnv.Init();
            if (!VoiceTestAudio.UseVosk()) Assert.Inconclusive("Vosk not available");
            VoiceSettings.Enabled = true;
            VoiceSettings.TestMode = true;
            VoiceSettings.MinConfidence = 0.70;
            MicrophoneManager.SelectedDeviceName = Environment.GetEnvironmentVariable("AEROAID_LIVE_MIC_DEVICE") ?? "";
            Console.WriteLine("Microphones: " + string.Join(" | ", WaveInCapture.Devices().Select(d => d.id + ":" + d.name)));

            var engine = new VoiceCommandEngine(MicrophoneManager.Instance);
            var dump = Environment.GetEnvironmentVariable("AEROAID_LIVE_MIC_DUMP");
            var raw = new System.IO.MemoryStream();
            Action<byte[]> rec = pcm => { lock (raw) raw.Write(pcm, 0, pcm.Length); };
            if (!string.IsNullOrEmpty(dump)) MicrophoneManager.Instance.AudioData += rec;
            var heard = new ConcurrentQueue<VoiceUtterance>();
            engine.UtteranceRecognized += u => heard.Enqueue(u);
            Assert.IsTrue(engine.StartListening(), engine.Status);
            Console.WriteLine("Engine: " + engine.EngineName + " / " + MicrophoneManager.Instance.Status);
            var phrases = new (string text, VoiceCommand cmd)[]
            {
                ("Arm the drone.", VoiceCommand.Arm), ("Arm.", VoiceCommand.Arm), ("Disarm the drone.", VoiceCommand.Disarm),
                ("Disarm.", VoiceCommand.Disarm), ("Take off.", VoiceCommand.Takeoff), ("Drone take off.", VoiceCommand.Takeoff),
                ("Stop.", VoiceCommand.Stop), ("Hold position.", VoiceCommand.Hold), ("Return to launch.", VoiceCommand.Rtl),
                ("Land.", VoiceCommand.Land), ("Do not arm the drone.", VoiceCommand.None), ("Disarm and arm the drone.", VoiceCommand.None)
            };
            var results = new List<string>();
            int ok = 0, wrong = 0, n = 0;
            try
            {
                Thread.Sleep(1500); // noise floor
                using (var synth = new SpeechSynthesizer())
                {
                    synth.SetOutputToDefaultAudioDevice();
                    foreach (var voice in VoiceTestAudio.EnglishVoices())
                    {
                        synth.SelectVoice(voice);
                        foreach (var (text, cmd) in phrases)
                        {
                            while (heard.TryDequeue(out _)) { }
                            synth.Speak(text);
                            Thread.Sleep(1600);
                            var got = heard.ToList();
                            var accepted = got.Where(u => u.Actionable).Select(u => u.AcceptedCommand).Distinct().ToList();
                            var pass = cmd == VoiceCommand.None ? accepted.Count == 0 : accepted.Count == 1 && accepted[0] == cmd;
                            var isWrong = accepted.Any(a => a != cmd);
                            n++;
                            if (pass) ok++;
                            if (isWrong) wrong++;
                            var line = $"[{voice}] {(pass ? "PASS" : isWrong ? "WRONG" : "MISS")} {text,-28} -> " +
                                       (got.Count == 0 ? "(nothing heard)" : string.Join(" | ", got.Select(u => $"\"{u.Text}\" {u.Confidence:P0} {u.Decision}"))) +
                                       $"  [mic level {MicrophoneManager.Instance.LevelDb:0} dBFS, speech {engine.LastSpeechDb:0} dBFS, floor {engine.NoiseFloorDb:0} dBFS]";
                            results.Add(line);
                            Console.WriteLine(line);
                        }
                    }
                }
            }
            finally
            {
                engine.StopListening();
                MicrophoneManager.Instance.AudioData -= rec;
                MicrophoneManager.Instance.TurnOff();
                if (!string.IsNullOrEmpty(dump))
                    lock (raw) System.IO.File.WriteAllBytes(dump, MicTestResult.ToWav(raw.ToArray()));
            }

            Console.WriteLine($"LOOPBACK RESULT: {ok}/{n} correct, {wrong} wrong command(s)");
            Assert.AreEqual(0, wrong, "a wrong command was accepted:\n" + string.Join("\n", results));
        }
    }
}
