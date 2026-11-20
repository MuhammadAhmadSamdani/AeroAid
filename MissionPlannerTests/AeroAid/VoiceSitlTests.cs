using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MissionPlanner.AeroAid.Audio;
using MissionPlanner.AeroAid.Flight;
using MissionPlanner.AeroAid.Telemetry;
using MissionPlanner.AeroAid.Voice;
using MissionPlanner.Comms;

namespace MissionPlanner.AeroAid.Tests
{
    /// <summary>
    /// Complete voice workflow against ArduCopter SITL:
    /// synthesised speech â†’ VoiceCommandEngine (speech-to-text + keyword matching) â†’ VoiceCommandController
    /// (validation, confirmation, MAVLink via the app's MainV2.comPort / FlightControlManager, verification).
    /// Only the confirmation dialog is answered automatically (and recorded).
    ///
    /// SITL ONLY. Runs only when the environment variable AEROAID_SITL is set to "host:port" of a running
    /// ArduCopter SITL (e.g. 127.0.0.1:5760); otherwise the test is skipped (Inconclusive).
    /// Never point this at a real vehicle.
    /// </summary>
    [TestClass]
    public class VoiceSitlTests
    {
        private sealed class AutoConfirmBackend : IVoiceFlightBackend
        {
            private readonly MavlinkVoiceBackend real = new MavlinkVoiceBackend();
            public readonly List<string> Dialogs = new List<string>();
            public int Confirms;
            public TelemetrySnapshot Capture() => real.Capture();

            public bool Confirm(string title, string text)
            {
                Confirms++;
                Dialogs.Add(title);
                Console.WriteLine("    [dialog auto-confirmed] " + title);
                return true;
            }

            public void Notify(string title, string text)
            {
                Dialogs.Add(title);
                Console.WriteLine("    [notice] " + text.Replace("\n", " "));
            }

            public Task<bool> Arm() => real.Arm();
            public Task<bool> Disarm() => real.Disarm();
            public Task<bool> Takeoff(double altitude) => real.Takeoff(altitude);
            public Task<bool> Stop() => real.Stop();
            public Task<bool> Hold() => real.Hold();
            public Task<bool> Rtl() => real.Rtl();
            public Task<bool> Land() => real.Land();
            public string LastVehicleMessage() => real.LastVehicleMessage();
            public Task Delay(int ms) => real.Delay(ms);
        }

        private static volatile bool reading;
        private static Thread reader;

        private static void Log(string s) => Console.WriteLine(DateTime.Now.ToString("HH:mm:ss.f") + " " + s);

        private static void Connect(string hostPort)
        {
            var parts = hostPort.Split(':');
            var port = MainV2.comPort;
            port.BaseStream = new TcpSerial { Host = parts[0], Port = parts.Length > 1 ? parts[1] : "5760" };
            port.Open(false, true, false);
            Assert.IsTrue(port.BaseStream.IsOpen, "could not open the SITL link");

            // what MainV2.SerialReader does in the app: read packets, update CurrentState, send GCS heartbeats
            reading = true;
            reader = new Thread(() =>
            {
                var lastHb = DateTime.MinValue;
                var lastStreams = DateTime.MinValue;
                while (reading)
                {
                    try
                    {
                        if (DateTime.UtcNow - lastHb > TimeSpan.FromSeconds(1))
                        {
                            lastHb = DateTime.UtcNow;
                            port.sendPacket(new MAVLink.mavlink_heartbeat_t
                            {
                                type = (byte)MAVLink.MAV_TYPE.GCS, autopilot = (byte)MAVLink.MAV_AUTOPILOT.INVALID,
                                mavlink_version = 3
                            }, port.sysidcurrent, port.compidcurrent);
                        }

                        if (port.MAV.sysid != 0 && DateTime.UtcNow - lastStreams > TimeSpan.FromSeconds(10))
                        {
                            lastStreams = DateTime.UtcNow;
                            foreach (var s in new[] { MAVLink.MAV_DATA_STREAM.EXTENDED_STATUS, MAVLink.MAV_DATA_STREAM.POSITION,
                                         MAVLink.MAV_DATA_STREAM.EXTRA1, MAVLink.MAV_DATA_STREAM.EXTRA2, MAVLink.MAV_DATA_STREAM.EXTRA3 })
                                port.requestDatastream(s, 4);
                        }

                        if (!port.giveComport && port.BaseStream.IsOpen && port.BaseStream.BytesToRead > 0)
                            port.readPacketAsync().Wait();
                        else
                            Thread.Sleep(5);
                        port.MAV.cs.UpdateCurrentSettings(null, false, port, port.MAV);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("reader: " + ex.Message);
                        Thread.Sleep(50);
                    }
                }
            }) { IsBackground = true, Name = "test MAVLink reader" };
            reader.Start();
        }

        private static bool WaitFor(Func<TelemetrySnapshot, bool> cond, int seconds, string what)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
            {
                if (cond(TelemetryManager.Capture())) return true;
                Thread.Sleep(200);
            }

            var t = TelemetryManager.Capture();
            Log($"timeout waiting for {what}: armed={t.Armed} mode={t.Mode} alt={t.AltRelative:0.0} gps={t.GpsFix}/{t.Satellites} " +
                $"ekf={t.EkfVariance:0.00} prearm={t.PrearmOk} landed_state={t.LandedState} msg={t.LastHighMessage}");
            return false;
        }

        // ---------------------------------------------------------------- speech
        private static readonly SpeechAudioFormatInfo Format =
            new SpeechAudioFormatInfo(WaveInCapture.SampleRate, AudioBitsPerSample.Sixteen, AudioChannel.Mono);

        private static string voice;

        private static void Feed(FakePcmSource src, byte[] pcm)
        {
            const int chunk = WaveInCapture.SampleRate * 2 / 10;
            for (var o = 0; o < pcm.Length; o += chunk)
            {
                var b = new byte[Math.Min(chunk, pcm.Length - o)];
                Buffer.BlockCopy(pcm, o, b, 0, b.Length);
                src.Feed(b);
                Thread.Sleep(25);
            }
        }

        /// <summary>Speaks a sentence into the engine; returns the utterances it produced.</summary>
        private static List<VoiceUtterance> Speak(FakePcmSource src, ConcurrentQueue<VoiceUtterance> heard, string sentence)
        {
            while (heard.TryDequeue(out _)) { }
            byte[] pcm;
            using (var synth = new SpeechSynthesizer())
            using (var ms = new MemoryStream())
            {
                synth.SelectVoice(voice);
                synth.SetOutputToAudioStream(ms, Format);
                synth.Speak(sentence);
                synth.SetOutputToNull();
                pcm = ms.ToArray();
            }

            Feed(src, pcm);
            Feed(src, new byte[WaveInCapture.SampleRate * 2 * 2]);
            var until = DateTime.UtcNow.AddSeconds(6);
            while (heard.IsEmpty && DateTime.UtcNow < until) Thread.Sleep(50);
            Thread.Sleep(300);
            var list = heard.ToList();
            foreach (var u in list)
                Log($"  heard \"{u.Text}\" {u.Confidence:P0} [{u.Source}] -> {u.Decision}");
            return list;
        }

        private static async Task<VoiceExecState> SayAndRun(FakePcmSource src, ConcurrentQueue<VoiceUtterance> heard,
            VoiceCommandController c, string sentence, VoiceCommand expected)
        {
            Log($"SAY \"{sentence}\"");
            var u = Speak(src, heard, sentence).Where(x => x.Actionable).ToList();
            if (expected == VoiceCommand.None)
            {
                Assert.AreEqual(0, u.Count, $"\"{sentence}\" must not produce a command");
                return VoiceExecState.Ignored;
            }

            Assert.AreEqual(1, u.Count, $"\"{sentence}\" should produce exactly one command");
            Assert.AreEqual(expected, u[0].Match.Command);
            var r = await c.HandleAsync(u[0]);
            Log($"  => {VoiceCommandController.Title(r)}: {c.StateText}");
            return r;
        }

        [TestMethod]
        [Timeout(600000)]
        public async Task FullVoiceWorkflowOnSitl()
        {
            var sitl = Environment.GetEnvironmentVariable("AEROAID_SITL");
            if (string.IsNullOrEmpty(sitl))
                Assert.Inconclusive("Set AEROAID_SITL=host:port of a running ArduCopter SITL to run this test.");

            TestEnv.Init();
            VoiceSettings.Enabled = true;
            VoiceSettings.MinConfidence = 0.65;
            VoiceSettings.CooldownSeconds = 5;
            AeroSettings.TakeoffAltitude = 3; // the spoken altitude (5 m) must override this default
            AeroSettings.MinSatellites = 6;
            AeroSettings.MinBatteryPercent = 30;
            using (var s = new SpeechSynthesizer())
                voice = s.GetInstalledVoices().Select(v => v.VoiceInfo.Name).FirstOrDefault(n => n.Contains("David")) ??
                        s.GetInstalledVoices().First().VoiceInfo.Name;

            Connect(sitl);
            var src = new FakePcmSource();
            var engine = new VoiceCommandEngine(src);
            var heard = new ConcurrentQueue<VoiceUtterance>();
            engine.UtteranceRecognized += u => heard.Enqueue(u);
            var backend = new AutoConfirmBackend();
            var c = new VoiceCommandController(backend);
            c.Progress += (m, l) => Log("    " + m);
            Assert.IsTrue(engine.StartListening(), engine.Status);
            try
            {
                Assert.IsTrue(WaitFor(t => t.HasHeartbeat && t.IsCopter, 30, "heartbeat"), "no SITL heartbeat");
                Assert.IsTrue(WaitFor(t => t.GpsFix >= 3 && t.EkfLevel == UI.StatusLevel.Ok && t.PrearmOk != false && t.BatteryPercent > 0, 180,
                    "GPS/EKF/pre-arm ready"), "SITL never became ready");
                Assert.IsTrue(await FlightControlManager.Instance.SetModeAsync("Guided"), "GUIDED");

                // 1. negations and questions: nothing happens
                await SayAndRun(src, heard, c, "Do not arm the drone.", VoiceCommand.None);
                await SayAndRun(src, heard, c, "Is the drone armed?", VoiceCommand.None);
                Assert.IsFalse(TelemetryManager.Capture().Armed);

                // 2. invalid for the current state: blocked before any dialog
                var confirms = backend.Confirms;
                Assert.AreEqual(VoiceExecState.SafetyCheckFailed, await SayAndRun(src, heard, c, "Take off.", VoiceCommand.Takeoff));
                StringAssert.Contains(c.StateText, "not armed");
                Assert.AreEqual(VoiceExecState.SafetyCheckFailed, await SayAndRun(src, heard, c, "Please disarm the drone.", VoiceCommand.Disarm));
                StringAssert.Contains(c.StateText, "already disarmed");
                Assert.IsFalse(TelemetryManager.Capture().Armed);
                Assert.AreEqual(confirms, backend.Confirms, "no confirmation dialog for blocked commands");

                // 3. ARM: confirmed, sent, verified by ACK + heartbeat
                Assert.AreEqual(VoiceExecState.Completed, await SayAndRun(src, heard, c, "Drone, please arm.", VoiceCommand.Arm));
                Assert.IsTrue(TelemetryManager.Capture().Armed, "SITL reports armed");
                Assert.AreEqual(confirms + 1, backend.Confirms, "a confirmation dialog was shown before arming");

                // 4. cool-down: repeating ARM right away is refused (a different command is not delayed)
                Assert.AreEqual(VoiceExecState.Rejected, await SayAndRun(src, heard, c, "Arm the drone.", VoiceCommand.Arm));
                StringAssert.Contains(c.StateText, "cool-down");

                // 5. TAKEOFF (must happen within ArduCopter's DISARM_DELAY, default 10 s, after arming): confirmed, ACKed, climb verified
                // spoken altitude + wake word: "Aero Aid, take off to five meters" -> MAV_CMD_NAV_TAKEOFF 5 m
                Assert.AreEqual(VoiceExecState.Completed, await SayAndRun(src, heard, c, "Aero Aid, take off to five meters.", VoiceCommand.Takeoff));
                StringAssert.Contains(backend.Dialogs[backend.Dialogs.Count - 1], "TAKE OFF to 5 m");
                Assert.IsTrue(WaitFor(t => t.AltRelative > 4.0, 30, "climb above 4 m (spoken 5 m, default 3 m)"), "vehicle did not climb to the spoken altitude");

                // 6. DISARM while airborne: refused, no dialog, still armed
                confirms = backend.Confirms;
                Assert.AreEqual(VoiceExecState.SafetyCheckFailed, await SayAndRun(src, heard, c, "Disarm the drone.", VoiceCommand.Disarm));
                StringAssert.Contains(c.StateText, "AIRBORNE");
                Assert.IsTrue(TelemetryManager.Capture().Armed, "still armed in the air");
                Assert.AreEqual(confirms, backend.Confirms, "no disarm confirmation dialog in the air");

                // 7. land (normal app control), then voice ARM + voice DISARM on the ground
                Assert.IsTrue(await FlightControlManager.Instance.LandAsync(), "LAND");
                Assert.IsTrue(WaitFor(t => !t.Armed, 90, "landed and auto-disarmed"), "did not land/disarm");
                Assert.IsTrue(await FlightControlManager.Instance.SetModeAsync("Guided"), "GUIDED again");
                Assert.AreEqual(VoiceExecState.Completed, await SayAndRun(src, heard, c, "Arm the drone.", VoiceCommand.Arm));
                Assert.AreEqual(VoiceExecState.Completed, await SayAndRun(src, heard, c, "Drone disarmed.", VoiceCommand.Disarm));
                Assert.IsTrue(WaitFor(t => !t.Armed, 5, "disarmed"), "SITL reports disarmed");
            }
            finally
            {
                engine.StopListening();
                reading = false;
                reader?.Join(2000);
                try
                {
                    MainV2.comPort.Close();
                }
                catch
                {
                }
            }
        }
    }
}
