using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MissionPlanner.AeroAid.Camera;
using MissionPlanner.Utilities;

namespace MissionPlanner.AeroAid.Tests
{
    [TestClass]
    public class CameraAndSettingsTests
    {
        private const string User = "admin";
        private const string Pass = "Unit-Test-Pw1";

        [ClassInitialize]
        public static void Init(TestContext _) => TestEnv.Init();

        // ---------------------------------------------------------------- RTSP probe vs an in-test RTSP server

        /// <summary>Minimal RTSP server: Digest challenge, then 200 + SDP for /cam/realmonitor, 404 otherwise.</summary>
        private static int StartRtspServer(CancellationToken ct)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            ct.Register(() => listener.Stop());
            Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    TcpClient c;
                    try { c = await listener.AcceptTcpClientAsync(); }
                    catch (Exception) { return; }
                    _ = Task.Run(() => Serve(c));
                }
            });
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        private static string Md5(string s)
        {
            using (var m = MD5.Create())
                return BitConverter.ToString(m.ComputeHash(Encoding.UTF8.GetBytes(s))).Replace("-", "").ToLowerInvariant();
        }

        private static void Serve(TcpClient c)
        {
            using (c)
            {
                var s = c.GetStream();
                var buf = new byte[8192];
                var acc = "";
                while (true)
                {
                    int n;
                    try { n = s.Read(buf, 0, buf.Length); }
                    catch (Exception) { return; }
                    if (n <= 0) return;
                    acc += Encoding.ASCII.GetString(buf, 0, n);
                    int end;
                    while ((end = acc.IndexOf("\r\n\r\n", StringComparison.Ordinal)) >= 0)
                    {
                        var req = acc.Substring(0, end);
                        acc = acc.Substring(end + 4);
                        var cseq = Regex.Match(req, @"CSeq:\s*(\d+)").Groups[1].Value;
                        var url = req.Split(' ')[1];
                        var auth = Regex.Match(req, @"Authorization:\s*Digest (.+)");
                        var ok = false;
                        if (auth.Success)
                        {
                            var resp = Regex.Match(auth.Groups[1].Value, "response=\"([^\"]+)\"").Groups[1].Value;
                            var uri = Regex.Match(auth.Groups[1].Value, "uri=\"([^\"]+)\"").Groups[1].Value;
                            ok = resp == Md5($"{Md5($"{User}:R:{Pass}")}:N1:{Md5("DESCRIBE:" + uri)}");
                        }

                        string reply;
                        if (!ok)
                            reply = $"RTSP/1.0 401 Unauthorized\r\nCSeq: {cseq}\r\nWWW-Authenticate: Digest realm=\"R\", nonce=\"N1\"\r\n\r\n";
                        else if (!url.Contains("/cam/realmonitor"))
                            reply = $"RTSP/1.0 404 Not Found\r\nCSeq: {cseq}\r\n\r\n";
                        else
                        {
                            var sdp = "v=0\r\nm=video 0 RTP/AVP 96\r\na=rtpmap:96 H264/90000\r\nm=audio 0 RTP/AVP 8\r\na=rtpmap:8 PCMA/8000\r\n";
                            reply = $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nContent-Length: {sdp.Length}\r\n\r\n{sdp}";
                        }

                        var bytes = Encoding.ASCII.GetBytes(reply);
                        s.Write(bytes, 0, bytes.Length);
                    }
                }
            }
        }

        [TestMethod]
        public void RtspProbe_DistinguishesOkAuthFailureNotFoundAndUnreachable()
        {
            using (var cts = new CancellationTokenSource())
            {
                var port = StartRtspServer(cts.Token);
                var url = $"rtsp://127.0.0.1:{port}/cam/realmonitor?channel=1&subtype=0";

                var ok = RtspProbe.DescribeAsync(url, User, Pass, CancellationToken.None).Result;
                Assert.AreEqual(RtspProbeStatus.Ok, ok.Status, ok.Message);
                Assert.AreEqual("H264", ok.VideoCodec);
                Assert.AreEqual("PCMA", ok.AudioCodec);

                Assert.AreEqual(RtspProbeStatus.AuthFailed, RtspProbe.DescribeAsync(url, User, "wrong", CancellationToken.None).Result.Status);
                Assert.AreEqual(RtspProbeStatus.AuthFailed, RtspProbe.DescribeAsync(url, User, "", CancellationToken.None).Result.Status);
                Assert.AreEqual(RtspProbeStatus.StreamNotFound,
                    RtspProbe.DescribeAsync($"rtsp://127.0.0.1:{port}/wrong/path", User, Pass, CancellationToken.None).Result.Status);
                cts.Cancel();
            }

            // closed port
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var closed = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            Assert.AreEqual(RtspProbeStatus.Unreachable,
                RtspProbe.DescribeAsync($"rtsp://127.0.0.1:{closed}/cam/realmonitor", User, Pass, CancellationToken.None, 1500).Result.Status);
            Assert.AreEqual(RtspProbeStatus.NotConfigured, RtspProbe.DescribeAsync("", User, Pass, CancellationToken.None).Result.Status);
            Assert.AreEqual(RtspProbeStatus.Error, RtspProbe.DescribeAsync("not a url", User, Pass, CancellationToken.None).Result.Status);
        }

        // ---------------------------------------------------------------- camera configuration

        [TestMethod]
        public void CameraUrl_BuildsImouDefaultsAndNeverContainsCredentials()
        {
            CameraSettings.RtspUrlOverride = "";
            CameraSettings.Host = "192.168.1.64";
            CameraSettings.RtspPort = 554;
            CameraSettings.RtspPath = CameraSettings.DefaultRtspPath;
            CameraSettings.UseSubStream = false;
            Assert.AreEqual("rtsp://192.168.1.64:554/cam/realmonitor?channel=1&subtype=0", CameraSettings.BuildRtspUrl());
            CameraSettings.UseSubStream = true;
            StringAssert.EndsWith(CameraSettings.BuildRtspUrl(), "subtype=1");

            CameraSettings.RtspUrlOverride = "rtsp://admin:secret@10.0.0.9:554/live";
            Assert.AreEqual("rtsp://10.0.0.9:554/live", CameraSettings.BuildRtspUrl());
            CameraSettings.Host = "";
            Assert.AreEqual("10.0.0.9", CameraSettings.EffectiveHost, "ONVIF host derived from the RTSP override");
            CameraSettings.RtspUrlOverride = "";
            Assert.IsFalse(CameraSettings.IsConfigured);
        }

        [TestMethod]
        public void PipelineCredentialsAreRedactedFromLogs()
        {
            var p = "rtspsrc location=\"rtsp://admin:s3cret@10.0.0.5/x\" user-id=\"admin\" user-pw=\"p\\\"w d\" ! appsink name=outsink";
            var r = GStreamer.RedactCredentials(p);
            Assert.IsFalse(r.Contains("s3cret"));
            Assert.IsFalse(r.Contains("w d"));
            StringAssert.Contains(r, "user-pw=***");
            StringAssert.Contains(r, "appsink name=outsink");
            Assert.AreEqual("videotestsrc ! appsink name=outsink", GStreamer.RedactCredentials("videotestsrc ! appsink name=outsink"));
        }

        [TestMethod]
        public void CustomPipelineWithoutAppsinkIsRejected()
        {
            CameraSettings.CustomPipeline = "videotestsrc ! autovideosink";
            Assert.IsNull(VideoStreamManager.BuildPipeline(out _));
            CameraSettings.CustomPipeline = "videotestsrc ! videoconvert ! video/x-raw,format=BGRA ! appsink name=outsink";
            Assert.IsNotNull(VideoStreamManager.BuildPipeline(out _));
            CameraSettings.CustomPipeline = "";
        }

        [TestMethod]
        public void PasswordInPipelineIsPassedAsQuotedProperty()
        {
            CameraSettings.Host = "192.168.1.64";
            CameraSettings.Password = "a\"b c";
            var p = VideoStreamManager.BuildPipeline(out var display);
            StringAssert.Contains(p, "user-pw=\"a\\\"b c\"");
            Assert.IsFalse(display.Contains("a\"b"), "display URL never contains the password");
            CameraSettings.Password = "";
            CameraSettings.Host = "";
        }

        // ---------------------------------------------------------------- secrets & persistence

        [TestMethod]
        public void SecretsAreEncryptedAtRestAndRoundTrip()
        {
            AeroSettings.SetSecret("test.secret", Pass);
            Settings.Instance.Save();
            var raw = File.ReadAllText(TestEnv.ConfigFile);
            Assert.IsFalse(raw.Contains(Pass), "plain-text secret found in config file");
            StringAssert.Contains(raw, "dpapi:");
            Assert.AreEqual(Pass, AeroSettings.GetSecret("test.secret"));
            Assert.IsTrue(AeroSettings.HasSecret("test.secret"));
            AeroSettings.SetSecret("test.secret", "");
            Assert.IsFalse(AeroSettings.HasSecret("test.secret"));
        }

        [TestMethod]
        public void TamperedSecretIsTreatedAsMissing()
        {
            Settings.Instance["aeroaid.test.bad"] = "dpapi:AAAA";
            Assert.AreEqual("", AeroSettings.GetSecret("test.bad"));
            Settings.Instance["aeroaid.test.bad2"] = "plain-text-not-accepted";
            Assert.AreEqual("", AeroSettings.GetSecret("test.bad2"));
        }

        [TestMethod]
        public void SettingsPersistAcrossReload()
        {
            AeroSettings.TakeoffAltitude = 7.5;
            CameraSettings.Host = "10.1.2.3";
            AeroSettings.Save();
            // simulate an application restart: drop the singleton and reload from disk
            typeof(Settings).GetField("_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).SetValue(null, null);
            Assert.AreEqual(7.5, AeroSettings.TakeoffAltitude, 1e-9);
            Assert.AreEqual("10.1.2.3", CameraSettings.Host);
            CameraSettings.Host = "";
        }

        [TestMethod]
        public void InvalidNumericSettingsAreClampedOrDefaulted()
        {
            Settings.Instance["aeroaid.flight.max_h_speed"] = "not-a-number";
            Assert.AreEqual(2.0, AeroSettings.MaxHorizontalSpeed, 1e-9);
            Settings.Instance["aeroaid.flight.max_h_speed"] = "NaN";
            Assert.AreEqual(2.0, AeroSettings.MaxHorizontalSpeed, 1e-9);
            Settings.Instance["aeroaid.flight.max_h_speed"] = "-5";
            Assert.AreEqual(0.1, AeroSettings.MaxHorizontalSpeed, 1e-9);
            Settings.Instance["aeroaid.camera.rtsp_port"] = "99999";
            Assert.AreEqual(65535, CameraSettings.RtspPort);
            Settings.Instance["aeroaid.flight.max_h_speed"] = "2";
            Settings.Instance["aeroaid.camera.rtsp_port"] = "554";
        }

        // ---------------------------------------------------------------- restart / damaged config file

        /// <summary>Simulates an application restart: empties the in-memory store and reloads from disk.</summary>
        private static void RestartSettings()
        {
            Settings.config.Clear();
            typeof(Settings).GetField("_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).SetValue(null, null);
            var _ = Settings.Instance; // triggers Load()
        }

        [TestMethod]
        public void SettingsSurviveRealRestartFromDisk()
        {
            AeroSettings.TakeoffAltitude = 8.5;
            AeroSettings.SetSecret("test.restart", Pass);
            AeroSettings.Save();
            RestartSettings();
            Assert.AreEqual(8.5, AeroSettings.TakeoffAltitude, 1e-9);
            Assert.AreEqual(Pass, AeroSettings.GetSecret("test.restart"));
            AeroSettings.SetSecret("test.restart", "");
            AeroSettings.TakeoffAltitude = 5;
            AeroSettings.Save();
        }

        [TestMethod]
        public void MissingConfigFileFallsBackToDefaults()
        {
            AeroSettings.Save();
            var backup = File.ReadAllText(TestEnv.ConfigFile);
            try
            {
                File.Delete(TestEnv.ConfigFile);
                RestartSettings();
                Assert.AreEqual(5.0, AeroSettings.TakeoffAltitude, 1e-9);
                Assert.AreEqual(2.0, AeroSettings.MaxHorizontalSpeed, 1e-9);
                Assert.IsTrue(AeroSettings.KeyboardFlightEnabled);
                Assert.AreEqual("", AeroSettings.GetSecret("camera.password"));
                AeroSettings.Save(); // must recreate the file
                Assert.IsTrue(File.Exists(TestEnv.ConfigFile));
            }
            finally
            {
                File.WriteAllText(TestEnv.ConfigFile, backup);
                RestartSettings();
            }
        }

        [TestMethod]
        public void CorruptConfigFileDoesNotCrashAndIsBackedUp()
        {
            AeroSettings.Save();
            var backup = File.ReadAllText(TestEnv.ConfigFile);
            var dir = Path.GetDirectoryName(TestEnv.ConfigFile);
            foreach (var f in Directory.GetFiles(dir, "*.failed")) File.Delete(f);
            try
            {
                // valid entry, then truncated/garbled XML (e.g. power loss mid-write)
                File.WriteAllText(TestEnv.ConfigFile,
                    "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Config>\n  <aeroaid.flight.takeoff_alt>9</aeroaid.flight.takeoff_alt>\n  <aeroaid.flight.max_h_speed>3<<<garbage \u0000\u0001");
                RestartSettings(); // must not throw
                Assert.AreEqual(9.0, AeroSettings.TakeoffAltitude, 1e-9, "entries before the damage are kept");
                Assert.AreEqual(2.0, AeroSettings.MaxHorizontalSpeed, 1e-9, "damaged entry falls back to default");
                Assert.IsTrue(Directory.GetFiles(dir, "*.failed").Length >= 1, "damaged file copied aside for recovery");

                // the next save writes a well-formed file that loads cleanly
                AeroSettings.Save();
                new System.Xml.XmlDocument().Load(TestEnv.ConfigFile);
                RestartSettings();
                Assert.AreEqual(9.0, AeroSettings.TakeoffAltitude, 1e-9);

                // completely non-XML content
                File.WriteAllText(TestEnv.ConfigFile, "this is not xml at all");
                RestartSettings();
                Assert.AreEqual(5.0, AeroSettings.TakeoffAltitude, 1e-9);
            }
            finally
            {
                File.WriteAllText(TestEnv.ConfigFile, backup);
                foreach (var f in Directory.GetFiles(dir, "*.failed")) File.Delete(f);
                RestartSettings();
            }
        }
    }
}
