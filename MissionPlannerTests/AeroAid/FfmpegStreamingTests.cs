using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MissionPlanner.AeroAid.Camera;

namespace MissionPlanner.AeroAid.Tests
{
    /// <summary>
    /// FFmpeg-based RTSP live video: URL/credential handling, process arguments, ffprobe parsing, and an
    /// end-to-end run of the real FFmpeg decoder against an in-test RTSP camera (Digest auth, H.264 RTP
    /// interleaved over TCP) - start, decode, stop, restart, reconnect, no leaked processes.
    /// </summary>
    [TestClass]
    public class FfmpegStreamingTests
    {
        [ClassInitialize]
        public static void Init(TestContext _) => TestEnv.Init();

        private static void ResetCamera()
        {
            CameraSettings.Host = "";
            CameraSettings.RtspUrlOverride = "";
            CameraSettings.RtspPath = CameraSettings.DefaultRtspPath;
            CameraSettings.UseSubStream = false;
            CameraSettings.Username = "admin";
            CameraSettings.Password = "";
            CameraSettings.CustomPipeline = "";
            CameraSettings.UseTcp = true;
            CameraSettings.PlayCameraAudio = false;
        }

        // ---------------------------------------------------------------- URL & credentials

        [TestMethod]
        public void AuthenticatedUrl_MatchesImouFormatAndPreservesQuery()
        {
            ResetCamera();
            CameraSettings.Host = "192.168.137.248";
            CameraSettings.RtspPort = 554;
            CameraSettings.RtspPath = "/cam/realmonitor?channel=1&subtype=0";
            CameraSettings.Username = "admin";
            CameraSettings.Password = "Abc123xy";
            Assert.AreEqual("rtsp://admin:Abc123xy@192.168.137.248:554/cam/realmonitor?channel=1&subtype=0",
                CameraSettings.BuildAuthenticatedRtspUrl());
            Assert.AreEqual("rtsp://192.168.137.248:554/cam/realmonitor?channel=1&subtype=0", CameraSettings.BuildRtspUrl(),
                "display URL never contains credentials");
            ResetCamera();
        }

        [TestMethod]
        public void Credentials_ArePercentEncoded()
        {
            Assert.AreEqual("p%40ss%3Aw%20rd%2F%23%25%22", CameraSettings.EncodeUserInfo("p@ss:w rd/#%\""));
            Assert.AreEqual("Abc-._~!$&'()*+,;=", CameraSettings.EncodeUserInfo("Abc-._~!$&'()*+,;="), "RFC 3986 safe characters stay literal");
            Assert.AreEqual("%C3%A9", CameraSettings.EncodeUserInfo("é"), "UTF-8 percent encoding");
            Assert.AreEqual("rtsp://ad%40min:p%3Aw@10.0.0.5:554/x?a=1&b=2",
                CameraSettings.WithCredentials("rtsp://old:creds@10.0.0.5:554/x?a=1&b=2", "ad@min", "p:w"),
                "existing credentials replaced, query kept");
            Assert.AreEqual("rtsp://10.0.0.5/x", CameraSettings.WithCredentials("rtsp://10.0.0.5/x", "", ""));
        }

        [TestMethod]
        public void LiteralSubtypeInSavedPathFollowsSubStreamOption()
        {
            ResetCamera();
            CameraSettings.Host = "192.168.137.248";
            CameraSettings.RtspPath = "/cam/realmonitor?channel=1&subtype=0";
            CameraSettings.UseSubStream = true;
            Assert.AreEqual("rtsp://192.168.137.248:554/cam/realmonitor?channel=1&subtype=1", CameraSettings.BuildRtspUrl());
            CameraSettings.UseSubStream = false;
            Assert.AreEqual("rtsp://192.168.137.248:554/cam/realmonitor?channel=1&subtype=0", CameraSettings.BuildRtspUrl());
            ResetCamera();
        }

        [TestMethod]
        public void RedactSecrets_RemovesUrlCredentialsAndPassword()
        {
            ResetCamera();
            CameraSettings.Password = "p@ss:word";
            var text = "Error opening rtsp://admin:p%40ss%3Aword@10.0.0.5:554/x | raw p@ss:word | http://u:v@h/";
            var r = CameraSettings.RedactSecrets(text);
            Assert.IsFalse(r.Contains("p@ss:word"));
            Assert.IsFalse(r.Contains("p%40ss%3Aword"));
            Assert.IsFalse(r.Contains("u:v@"));
            StringAssert.Contains(r, "rtsp://***@10.0.0.5:554/x");
            ResetCamera();
        }

        // ---------------------------------------------------------------- process arguments & ffprobe parsing

        [TestMethod]
        public void FfmpegArguments_UseTcpLowLatencyRawBgr24()
        {
            var a = FfmpegVideoSource.BuildFfmpegArguments("rtsp://u:p@h:554/cam/realmonitor?channel=1&subtype=0", 1280, 720, true, "-timeout");
            StringAssert.Contains(a, "-rtsp_transport tcp");
            StringAssert.Contains(a, "-fflags nobuffer -flags low_delay");
            StringAssert.Contains(a, "-timeout 5000000");
            StringAssert.Contains(a, "-i \"rtsp://u:p@h:554/cam/realmonitor?channel=1&subtype=0\"");
            StringAssert.Contains(a, "-an");
            StringAssert.Contains(a, "-vf scale=1280:720");
            StringAssert.Contains(a, "-pix_fmt bgr24 -f rawvideo pipe:1");
            StringAssert.Contains(FfmpegVideoSource.BuildFfmpegArguments("rtsp://h/x", 2, 2, false, "-stimeout"), "-rtsp_transport udp -fflags nobuffer -flags low_delay -stimeout 5000000");
            var p = FfmpegVideoSource.BuildFfprobeArguments("rtsp://h/x", true, "-timeout");
            StringAssert.Contains(p, "-rtsp_transport tcp -timeout 8000000 -select_streams v:0");
        }

        [TestMethod]
        public void FfprobeOutput_ParsedAndScaledKeepingAspect()
        {
            var r = new FfprobeResult();
            FfmpegVideoSource.ParseProbeOutput("codec_name=hevc\r\nwidth=2880\r\nheight=1620\r\nsample_aspect_ratio=1:1\r\nr_frame_rate=20/1\r\navg_frame_rate=0/0\r\n", r);
            Assert.IsTrue(r.Ok);
            Assert.AreEqual("hevc", r.Codec);
            Assert.AreEqual(2880, r.Width);
            Assert.AreEqual(1620, r.Height);
            Assert.AreEqual(20, r.Fps, 0.001, "falls back to r_frame_rate when avg is 0/0");
            Assert.AreEqual(new Size(1280, 720), FfmpegVideoSource.OutputSize(r, 1280));
            Assert.AreEqual(new Size(2880, 1620), FfmpegVideoSource.OutputSize(r, 4000), "never upscaled");

            var sub = new FfprobeResult();
            FfmpegVideoSource.ParseProbeOutput("codec_name=h264\nwidth=704\nheight=576\nsample_aspect_ratio=N/A\navg_frame_rate=25/1\n", sub);
            Assert.AreEqual(new Size(704, 576), FfmpegVideoSource.OutputSize(sub, 1280));
            Assert.AreEqual(25, sub.Fps, 0.001);

            var odd = new FfprobeResult { Width = 1001, Height = 333, PixelAspect = 1, Ok = true };
            var s = FfmpegVideoSource.OutputSize(odd, 1280);
            Assert.AreEqual(0, s.Width % 2);
            Assert.AreEqual(0, s.Height % 2);

            var none = new FfprobeResult();
            FfmpegVideoSource.ParseProbeOutput("", none);
            Assert.IsFalse(none.Ok);
        }

        [TestMethod]
        public void FfmpegVersionParsing_SelectsTimeoutOption()
        {
            Assert.AreEqual(9, FfmpegLocator.ParseMajorVersion("ffmpeg version 9.0.2-full_build-www.gyan.dev Copyright"));
            Assert.AreEqual(4, FfmpegLocator.ParseMajorVersion("ffprobe version n4.4.2 Copyright"));
            Assert.AreEqual(99, FfmpegLocator.ParseMajorVersion("ffmpeg version 2024-05-13-git-37db0454e4-full_build"));
            Assert.AreEqual(99, FfmpegLocator.ParseMajorVersion("ffmpeg version N-115000-g0123456"));
        }

        // ---------------------------------------------------------------- RTSP pre-check & ONVIF independence

        [TestMethod]
        public void RtspProbe_CameraThatNeverAnswersDoesNotHang()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var accepted = new List<TcpClient>();
            Task.Run(async () =>
            {
                try
                {
                    while (true) accepted.Add(await listener.AcceptTcpClientAsync()); // accept, never reply
                }
                catch (Exception)
                {
                }
            });
            try
            {
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var sw = Stopwatch.StartNew();
                var r = RtspProbe.DescribeAsync($"rtsp://127.0.0.1:{port}/cam/realmonitor", "admin", "x", CancellationToken.None, 1000)
                    .Wait(10000);
                Assert.IsTrue(r, "probe returned");
                Assert.IsTrue(sw.ElapsedMilliseconds < 5000, "bounded by the timeout: " + sw.ElapsedMilliseconds);
                var res = RtspProbe.DescribeAsync($"rtsp://127.0.0.1:{port}/cam/realmonitor", "admin", "x", CancellationToken.None, 1000).Result;
                Assert.AreEqual(RtspProbeStatus.Unreachable, res.Status);
                StringAssert.Contains(res.Message, "did not answer");
            }
            finally
            {
                listener.Stop();
                foreach (var c in accepted.ToArray()) c.Dispose();
            }
        }

        [TestMethod]
        public void StatusSummary_ReportsRtspAndOnvifIndependently()
        {
            var c = new CameraCapabilities
            {
                ProbedAt = DateTime.Now,
                Rtsp = new RtspProbeResult { Status = RtspProbeStatus.Ok, Message = "RTSP OK • video H265" },
                OnvifAvailable = false,
                OnvifMessage = "ONVIF request timed out: http://192.168.137.248:80/onvif/device_service",
                PtzMessage = "PTZ unavailable: timed out"
            };
            var s = CameraConnectionManager.StatusSummary(c);
            StringAssert.Contains(s, "RTSP video: RTSP OK");
            StringAssert.Contains(s, "ONVIF/PTZ: Unavailable");
            Assert.AreEqual("Unavailable", CameraConnectionManager.OnvifPtzStatus(c));
            c.OnvifAvailable = true;
            c.PtzAvailable = true;
            Assert.AreEqual("Available", CameraConnectionManager.OnvifPtzStatus(c));
        }

        // ---------------------------------------------------------------- end-to-end with the real FFmpeg

        private const string E2EUser = "admin";
        /// <summary>Contains every character that breaks a URL unless percent-encoded.</summary>
        private const string E2EPass = "p@ss:w rd/#%1";

        private static FfmpegTools RequireFfmpeg()
        {
            var tools = FfmpegLocator.Find();
            if (tools == null)
                Assert.Inconclusive("FFmpeg is not installed on this machine - end-to-end streaming test skipped.");
            return tools;
        }

        private static string testClip;

        /// <summary>10 s 640x360 15 fps H.264 Annex-B clip generated with FFmpeg (cached per run).</summary>
        private static string TestClip(FfmpegTools tools)
        {
            if (testClip != null && File.Exists(testClip))
                return testClip;
            var path = Path.Combine(TestEnv.Root, "testsrc.h264");
            var psi = new ProcessStartInfo(tools.Ffmpeg,
                "-hide_banner -loglevel error -y -f lavfi -i testsrc=size=640x360:rate=15 -t 10 " +
                "-c:v libx264 -profile:v baseline -bf 0 -g 15 -threads 1 -pix_fmt yuv420p -f h264 \"" + path + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            using (var p = Process.Start(psi))
            {
                var err = p.StandardError.ReadToEnd();
                p.WaitForExit(60000);
                if (p.ExitCode != 0 || !File.Exists(path))
                    Assert.Inconclusive("Could not generate an H.264 test clip with this FFmpeg build: " + err);
            }

            return testClip = path;
        }

        private static int CountFfmpeg(FfmpegTools tools) =>
            Process.GetProcessesByName("ffmpeg").Count(p =>
            {
                try
                {
                    return string.Equals(p.MainModule?.FileName, tools.Ffmpeg, StringComparison.OrdinalIgnoreCase);
                }
                catch (Exception)
                {
                    return false;
                }
                finally
                {
                    p.Dispose();
                }
            });

        private static bool WaitFor(Func<bool> cond, int ms)
        {
            var end = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < end)
            {
                if (cond()) return true;
                Thread.Sleep(50);
            }

            return cond();
        }

        private static void ConfigureFor(FakeRtspCamera cam)
        {
            ResetCamera();
            CameraSettings.RtspUrlOverride = $"rtsp://127.0.0.1:{cam.Port}/cam/realmonitor?channel=1&subtype=0";
            CameraSettings.Username = E2EUser;
            CameraSettings.Password = E2EPass;
        }

        [TestMethod]
        [Timeout(180000)]
        public void EndToEnd_StreamsDecodesStopsAndRestartsWithoutLeaks()
        {
            var tools = RequireFfmpeg();
            var vs = VideoStreamManager.Instance;
            using (var cam = new FakeRtspCamera(TestClip(tools), E2EUser, E2EPass))
            {
                ConfigureFor(cam);
                var baseline = CountFfmpeg(tools);
                try
                {
                    for (var round = 1; round <= 3; round++)
                    {
                        vs.Start();
                        Assert.IsTrue(WaitFor(() => vs.State == StreamState.Playing, 40000),
                            $"round {round}: not playing - {vs.State}: {vs.StatusMessage}");
                        Assert.AreEqual(new Size(640, 360), vs.FrameSize, "decoded at the source size (aspect kept)");
                        StringAssert.Contains(vs.DecoderName, "FFmpeg");
                        Assert.IsFalse(vs.DisplayUrl.Contains("p@ss") || vs.DisplayUrl.Contains("%40"), "no credentials in display URL");
                        var id = vs.FrameId;
                        Assert.IsTrue(WaitFor(() => vs.FrameId > id + 5, 10000), "frames keep arriving");
                        using (var frame = vs.GetFrameCopy(out _))
                        {
                            Assert.IsNotNull(frame);
                            var colours = new HashSet<int>();
                            for (var x = 10; x < frame.Width; x += 40)
                                colours.Add(frame.GetPixel(x, frame.Height / 3).ToArgb());
                            Assert.IsTrue(colours.Count > 3, "decoded picture has content (testsrc colour bars)");
                        }

                        Assert.AreEqual(baseline + 1, CountFfmpeg(tools), "exactly one owned ffmpeg process");
                        vs.Stop();
                        Assert.AreEqual(StreamState.Stopped, vs.State);
                        Assert.IsFalse(vs.HasFrame);
                        Assert.IsTrue(WaitFor(() => CountFfmpeg(tools) == baseline, 5000), "ffmpeg terminated on Stop");
                    }

                    Assert.IsTrue(cam.AuthenticatedDescribes >= 3, "FFmpeg authenticated with the percent-encoded password");
                    Assert.AreEqual(0, cam.FailedAuthAfterChallenge, "no wrong-password attempts");
                }
                finally
                {
                    vs.Stop();
                    ResetCamera();
                }
            }
        }

        [TestMethod]
        [Timeout(180000)]
        public void EndToEnd_ReconnectsAfterStreamInterruption()
        {
            var tools = RequireFfmpeg();
            var vs = VideoStreamManager.Instance;
            using (var cam = new FakeRtspCamera(TestClip(tools), E2EUser, E2EPass) { DropSessionsAfterFrames = 150, DropsRemaining = 1 })
            {
                ConfigureFor(cam);
                var states = new List<StreamState>();
                Action<StreamState, string> h = (s, m) => { lock (states) states.Add(s); };
                vs.StateChanged += h;
                try
                {
                    vs.Start();
                    Assert.IsTrue(WaitFor(() => vs.State == StreamState.Playing, 40000), vs.StatusMessage);
                    Assert.IsTrue(WaitFor(() => { lock (states) return states.Contains(StreamState.Reconnecting); }, 30000),
                        "interruption detected: " + vs.StatusMessage);
                    Assert.IsTrue(WaitFor(() =>
                    {
                        lock (states) return states.LastIndexOf(StreamState.Playing) > states.IndexOf(StreamState.Reconnecting);
                    }, 60000), "video resumed automatically: " + vs.StatusMessage);
                    Assert.AreEqual(StreamState.Playing, vs.State);
                }
                finally
                {
                    vs.StateChanged -= h;
                    vs.Stop();
                    ResetCamera();
                }
            }
        }

        [TestMethod]
        [Timeout(120000)]
        public void EndToEnd_WrongPasswordStopsWithClearError()
        {
            var tools = RequireFfmpeg();
            var vs = VideoStreamManager.Instance;
            using (var cam = new FakeRtspCamera(TestClip(tools), E2EUser, E2EPass))
            {
                ConfigureFor(cam);
                CameraSettings.Password = "Bad-Pw-9x";
                try
                {
                    vs.Start();
                    Assert.IsTrue(WaitFor(() => vs.State == StreamState.Error, 20000), vs.State + ": " + vs.StatusMessage);
                    StringAssert.Contains(vs.StatusMessage, "Authentication failed");
                    Assert.IsFalse(vs.StatusMessage.Contains("Bad-Pw-9x"), "password never shown");
                }
                finally
                {
                    vs.Stop();
                    ResetCamera();
                }
            }
        }

        // ---------------------------------------------------------------- in-test RTSP camera

        /// <summary>
        /// Minimal RTSP/1.0 server modelled on a Dahua/Imou camera: Digest auth on DESCRIBE, one H.264 track,
        /// RTP interleaved over the RTSP TCP connection, 15 fps, the clip loops forever.
        /// </summary>
        private sealed class FakeRtspCamera : IDisposable
        {
            private readonly TcpListener listener;
            private readonly CancellationTokenSource cts = new CancellationTokenSource();
            private readonly List<List<byte[]>> accessUnits;
            private readonly string user, pass;
            private readonly byte[] sps, pps;
            private int authenticatedDescribes, failedAuth;

            public int Port { get; }
            public int DropSessionsAfterFrames = -1;
            public int DropsRemaining;
            public int AuthenticatedDescribes => Volatile.Read(ref authenticatedDescribes);
            public int FailedAuthAfterChallenge => Volatile.Read(ref failedAuth);

            public FakeRtspCamera(string h264File, string user, string pass)
            {
                this.user = user;
                this.pass = pass;
                accessUnits = SplitAccessUnits(SplitNals(File.ReadAllBytes(h264File)));
                sps = accessUnits.SelectMany(a => a).First(n => (n[0] & 0x1F) == 7);
                pps = accessUnits.SelectMany(a => a).First(n => (n[0] & 0x1F) == 8);
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                Task.Run(async () =>
                {
                    while (!cts.IsCancellationRequested)
                    {
                        TcpClient c;
                        try { c = await listener.AcceptTcpClientAsync(); }
                        catch (Exception) { return; }
                        _ = Task.Run(() => Serve(c));
                    }
                });
            }

            public void Dispose()
            {
                cts.Cancel();
                listener.Stop();
            }

            private static List<byte[]> SplitNals(byte[] d)
            {
                var starts = new List<(int pos, int len)>();
                for (var i = 0; i + 3 <= d.Length; i++)
                {
                    if (d[i] == 0 && d[i + 1] == 0 && d[i + 2] == 1)
                    {
                        starts.Add((i + 3, 3));
                        i += 2;
                    }
                }

                var nals = new List<byte[]>();
                for (var k = 0; k < starts.Count; k++)
                {
                    var begin = starts[k].pos;
                    var end = k + 1 < starts.Count ? starts[k + 1].pos - 3 : d.Length;
                    while (end > begin && d[end - 1] == 0) end--; // 4-byte start code / trailing zeros
                    if (end > begin) nals.Add(d.Skip(begin).Take(end - begin).ToArray());
                }

                return nals;
            }

            private static List<List<byte[]>> SplitAccessUnits(List<byte[]> nals)
            {
                var aus = new List<List<byte[]>>();
                var cur = new List<byte[]>();
                var hasSlice = false;
                foreach (var n in nals)
                {
                    var t = n[0] & 0x1F;
                    var isSlice = t == 1 || t == 5;
                    if ((t == 7 || t == 9 || t == 6 || isSlice) && hasSlice)
                    {
                        aus.Add(cur);
                        cur = new List<byte[]>();
                        hasSlice = false;
                    }

                    cur.Add(n);
                    hasSlice |= isSlice;
                }

                if (cur.Count > 0) aus.Add(cur);
                return aus;
            }

            private static string Md5(string s)
            {
                using (var m = MD5.Create())
                    return BitConverter.ToString(m.ComputeHash(Encoding.UTF8.GetBytes(s))).Replace("-", "").ToLowerInvariant();
            }

            private void Serve(TcpClient client)
            {
                var playing = false;
                var writeLock = new object();
                using (client)
                {
                    var s = client.GetStream();
                    var buf = new List<byte>();
                    var chunk = new byte[8192];
                    var challenged = false;
                    while (!cts.IsCancellationRequested)
                    {
                        int n;
                        try { n = s.Read(chunk, 0, chunk.Length); }
                        catch (Exception) { return; }
                        if (n <= 0) return;
                        buf.AddRange(chunk.Take(n));
                        while (true)
                        {
                            if (buf.Count >= 4 && buf[0] == (byte)'$')
                            {
                                var len = (buf[2] << 8) | buf[3];
                                if (buf.Count < 4 + len) break;
                                buf.RemoveRange(0, 4 + len); // client RTCP - ignore
                                continue;
                            }

                            var text = Encoding.ASCII.GetString(buf.ToArray());
                            var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                            if (end < 0) break;
                            buf.RemoveRange(0, end + 4);
                            var req = text.Substring(0, end);
                            var first = req.Split(new[] { "\r\n" }, StringSplitOptions.None)[0].Split(' ');
                            var method = first[0];
                            var url = first.Length > 1 ? first[1] : "";
                            var cseq = Regex.Match(req, @"CSeq:\s*(\d+)", RegexOptions.IgnoreCase).Groups[1].Value;
                            string reply;
                            switch (method)
                            {
                                case "OPTIONS":
                                    reply = $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nPublic: OPTIONS, DESCRIBE, SETUP, PLAY, TEARDOWN, GET_PARAMETER\r\n\r\n";
                                    break;
                                case "DESCRIBE":
                                    var auth = Regex.Match(req, @"Authorization:\s*Digest (.+)", RegexOptions.IgnoreCase);
                                    var ok = false;
                                    if (auth.Success)
                                    {
                                        var a = auth.Groups[1].Value;
                                        var resp = Regex.Match(a, "response=\"([^\"]+)\"").Groups[1].Value;
                                        var uri = Regex.Match(a, "uri=\"([^\"]+)\"").Groups[1].Value;
                                        var uname = Regex.Match(a, "username=\"([^\"]*)\"").Groups[1].Value;
                                        ok = uname == user && resp == Md5($"{Md5($"{user}:Login to Camera:{pass}")}:N0nce42:{Md5("DESCRIBE:" + uri)}");
                                        if (!ok && challenged) Interlocked.Increment(ref failedAuth);
                                    }

                                    if (!ok)
                                    {
                                        challenged = true;
                                        reply = $"RTSP/1.0 401 Unauthorized\r\nCSeq: {cseq}\r\nWWW-Authenticate: Digest realm=\"Login to Camera\", nonce=\"N0nce42\"\r\n\r\n";
                                    }
                                    else if (!url.Contains("/cam/realmonitor"))
                                        reply = $"RTSP/1.0 404 Not Found\r\nCSeq: {cseq}\r\n\r\n";
                                    else
                                    {
                                        Interlocked.Increment(ref authenticatedDescribes);
                                        var sdp = "v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=AeroAid test camera\r\nc=IN IP4 0.0.0.0\r\nt=0 0\r\n" +
                                                  "m=video 0 RTP/AVP 96\r\na=rtpmap:96 H264/90000\r\n" +
                                                  $"a=fmtp:96 packetization-mode=1;sprop-parameter-sets={Convert.ToBase64String(sps)},{Convert.ToBase64String(pps)}\r\n" +
                                                  "a=control:trackID=0\r\n";
                                        var baseUrl = url.Split('?')[0].TrimEnd('/') + "/";
                                        reply = $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nContent-Base: {baseUrl}\r\nContent-Type: application/sdp\r\n" +
                                                $"Content-Length: {Encoding.ASCII.GetByteCount(sdp)}\r\n\r\n{sdp}";
                                    }

                                    break;
                                case "SETUP":
                                    reply = $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nTransport: RTP/AVP/TCP;unicast;interleaved=0-1;ssrc=1234ABCD\r\nSession: 4242;timeout=60\r\n\r\n";
                                    break;
                                case "PLAY":
                                    reply = $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nSession: 4242\r\nRange: npt=0.000-\r\n\r\n";
                                    break;
                                case "TEARDOWN":
                                    reply = $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nSession: 4242\r\n\r\n";
                                    break;
                                default:
                                    reply = $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nSession: 4242\r\n\r\n";
                                    break;
                            }

                            var bytes = Encoding.ASCII.GetBytes(reply);
                            try
                            {
                                lock (writeLock) s.Write(bytes, 0, bytes.Length);
                            }
                            catch (Exception)
                            {
                                return;
                            }

                            if (method == "TEARDOWN")
                                return;
                            if (method == "PLAY" && !playing)
                            {
                                playing = true;
                                var t = new Thread(() => SendVideo(client, s, writeLock)) { IsBackground = true };
                                t.Start();
                            }
                        }
                    }
                }
            }

            private void SendVideo(TcpClient client, NetworkStream s, object writeLock)
            {
                ushort seq = 0;
                uint ts = 0;
                var frames = 0;
                var sw = Stopwatch.StartNew();
                try
                {
                    while (!cts.IsCancellationRequested && client.Connected)
                    {
                        foreach (var au in accessUnits)
                        {
                            for (var i = 0; i < au.Count; i++)
                            {
                                foreach (var pkt in Packetize(au[i], i == au.Count - 1))
                                {
                                    var rtp = new byte[12 + pkt.payload.Length];
                                    rtp[0] = 0x80;
                                    rtp[1] = (byte)(96 | (pkt.marker ? 0x80 : 0));
                                    rtp[2] = (byte)(seq >> 8);
                                    rtp[3] = (byte)seq;
                                    seq++;
                                    rtp[4] = (byte)(ts >> 24); rtp[5] = (byte)(ts >> 16); rtp[6] = (byte)(ts >> 8); rtp[7] = (byte)ts;
                                    rtp[8] = 0x12; rtp[9] = 0x34; rtp[10] = 0xAB; rtp[11] = 0xCD;
                                    Buffer.BlockCopy(pkt.payload, 0, rtp, 12, pkt.payload.Length);
                                    var frame = new byte[4 + rtp.Length];
                                    frame[0] = (byte)'$';
                                    frame[1] = 0;
                                    frame[2] = (byte)(rtp.Length >> 8);
                                    frame[3] = (byte)rtp.Length;
                                    Buffer.BlockCopy(rtp, 0, frame, 4, rtp.Length);
                                    lock (writeLock) s.Write(frame, 0, frame.Length);
                                }
                            }

                            ts += 90000 / 15;
                            frames++;
                            if (DropSessionsAfterFrames > 0 && frames >= DropSessionsAfterFrames && Interlocked.Decrement(ref DropsRemaining) >= 0)
                            {
                                client.Close(); // simulated network interruption
                                return;
                            }

                            var due = frames * 1000 / 15;
                            var wait = due - (int)sw.ElapsedMilliseconds;
                            if (wait > 0) Thread.Sleep(wait);
                            if (cts.IsCancellationRequested || !client.Connected) return;
                        }
                    }
                }
                catch (Exception)
                {
                    // client went away
                }
            }

            private static IEnumerable<(byte[] payload, bool marker)> Packetize(byte[] nal, bool lastOfFrame)
            {
                const int max = 1200;
                if (nal.Length <= max)
                {
                    yield return (nal, lastOfFrame);
                    yield break;
                }

                var fuIndicator = (byte)((nal[0] & 0xE0) | 28);
                var type = (byte)(nal[0] & 0x1F);
                for (var off = 1; off < nal.Length; off += max)
                {
                    var len = Math.Min(max, nal.Length - off);
                    var first = off == 1;
                    var last = off + len >= nal.Length;
                    var p = new byte[2 + len];
                    p[0] = fuIndicator;
                    p[1] = (byte)(type | (first ? 0x80 : 0) | (last ? 0x40 : 0));
                    Buffer.BlockCopy(nal, off, p, 2, len);
                    yield return (p, last && lastOfFrame);
                }
            }
        }
    }
}
