extern alias Drawing;
using MPBitmap = Drawing::System.Drawing.Bitmap;

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using MissionPlanner.Utilities;
using SkiaSharp;

namespace MissionPlanner.AeroAid.Camera
{
    public enum StreamState
    {
        Stopped,
        Starting,
        Playing,
        Reconnecting,
        Error
    }

    /// <summary>
    /// Single shared live-video pipeline (GStreamer, as used by Mission Planner's HUD video).
    /// Frames are deep-copied out of the GStreamer buffer into a System.Drawing bitmap so that any number
    /// of consumers (live view, dashboard preview, fire detection) can read them safely.
    /// Handles start/stop, stall detection, automatic reconnection with back-off and RTSP diagnostics.
    /// </summary>
    public sealed class VideoStreamManager
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        public static VideoStreamManager Instance { get; } = new VideoStreamManager();

        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
        private static extern void CopyMemory(IntPtr dest, IntPtr src, UIntPtr count);

        private readonly object frameLock = new object();
        private readonly object controlLock = new object();
        private readonly System.Threading.Timer watchdog;
        private GStreamer gst;
        private Bitmap latest;
        private long frameId;
        private DateTime lastFrameUtc = DateTime.MinValue;
        private DateTime startedUtc = DateTime.MinValue;
        private DateTime nextAttemptUtc = DateTime.MinValue;
        private volatile bool wantRunning;
        private volatile bool pipelineEnded;
        private volatile bool restartBusy;
        private int failures;
        private double fps;
        private DateTime fpsWindowStart = DateTime.UtcNow;
        private int fpsCount;

        public StreamState State { get; private set; } = StreamState.Stopped;
        public string StatusMessage { get; private set; } = "Stream stopped.";
        public string DisplayUrl { get; private set; } = "";
        public long FrameId => Interlocked.Read(ref frameId);
        public double Fps => fps;
        public Size FrameSize { get; private set; }

        /// <summary>(state, message) - raised on worker threads.</summary>
        public event Action<StreamState, string> StateChanged;

        /// <summary>Raised on the GStreamer thread after each new frame is stored (keep handlers tiny).</summary>
        public event Action FrameArrived;

        private VideoStreamManager()
        {
            watchdog = new System.Threading.Timer(_ => Watchdog(), null, 1000, 1000);
        }

        private void SetState(StreamState s, string message)
        {
            State = s;
            StatusMessage = message;
            log.Info("Video: " + s + " - " + message);
            try
            {
                StateChanged?.Invoke(s, message);
            }
            catch (Exception ex)
            {
                log.Error("StateChanged handler failed", ex);
            }
        }

        /// <summary>Locates the GStreamer runtime (installed by Mission Planner's downloader).</summary>
        public static bool GStreamerAvailable()
        {
            try
            {
                GStreamer.GstLaunch = GStreamer.LookForGstreamer();
                return GStreamer.GstLaunchExists;
            }
            catch (Exception ex)
            {
                log.Error("GStreamer lookup failed", ex);
                return false;
            }
        }

        public void Start()
        {
            if (!CameraSettings.IsConfigured)
            {
                SetState(StreamState.Error, "Camera not configured. Enter the camera IP / RTSP settings in Settings › Camera.");
                return;
            }

            wantRunning = true;
            failures = 0;
            nextAttemptUtc = DateTime.MinValue;
            Restart("Connecting to camera... (the very first start can take up to a minute while GStreamer initialises)");
        }

        public void Stop()
        {
            // frameLock makes "stop" atomic with respect to OnNewImage (no frame/state after Stop)
            lock (frameLock)
                wantRunning = false;
            var g = gst;
            Task.Run(() =>
            {
                try
                {
                    g?.Stop();
                }
                catch (Exception ex)
                {
                    log.Error("GStreamer stop failed", ex);
                }
            });
            lock (frameLock)
            {
                latest?.Dispose();
                latest = null;
                FrameSize = Size.Empty;
            }

            Interlocked.Increment(ref frameId);
            lock (frameLock)
                SetState(StreamState.Stopped, "Stream stopped.");
        }

        /// <summary>Restarts the pipeline with the current settings (e.g. after a quality change).</summary>
        public void ApplySettings()
        {
            if (wantRunning)
                Restart("Applying new stream settings...");
        }

        private void Restart(string message)
        {
            if (restartBusy)
                return;
            restartBusy = true;
            SetState(StreamState.Starting, message);
            Task.Run(() =>
            {
                try
                {
                    lock (controlLock)
                    {
                        gst?.Stop();
                        if (!wantRunning)
                            return;
                        if (!GStreamerAvailable())
                        {
                            wantRunning = false;
                            SetState(StreamState.Error, "GStreamer is not installed. Use \"Install GStreamer\" on the Live Camera screen.");
                            return;
                        }

                        var pipeline = BuildPipeline(out var display);
                        DisplayUrl = display;

                        // Fast pre-check (GStreamer itself can retry silently for a long time on a dead camera)
                        if (pipeline != null && string.IsNullOrEmpty(CameraSettings.CustomPipeline))
                        {
                            var probe = RtspProbe.DescribeAsync(display, CameraSettings.Username, CameraSettings.Password,
                                CancellationToken.None, 3000).GetAwaiter().GetResult();
                            if (!wantRunning)
                                return; // Stop() was pressed while probing - keep the Stopped state
                            if (probe.Status == RtspProbeStatus.AuthFailed || probe.Status == RtspProbeStatus.StreamNotFound ||
                                probe.Status == RtspProbeStatus.NotConfigured)
                            {
                                wantRunning = false; // retrying cannot fix these
                                SetState(StreamState.Error, probe.Message);
                                return;
                            }

                            if (probe.Status == RtspProbeStatus.Unreachable || probe.Status == RtspProbeStatus.Error)
                            {
                                failures++;
                                var delay = Math.Min(30, 2 << Math.Min(4, failures - 1));
                                nextAttemptUtc = DateTime.UtcNow.AddSeconds(delay);
                                SetState(StreamState.Reconnecting, probe.Message + $" Retrying in {delay} s...");
                                return;
                            }
                        }
                        if (pipeline == null)
                        {
                            wantRunning = false;
                            SetState(StreamState.Error, "Invalid custom pipeline: it must end with 'appsink name=outsink'.");
                            return;
                        }

                        if (gst == null)
                        {
                            gst = new GStreamer();
                            gst.OnNewImage += OnNewImage;
                        }

                        if (!wantRunning)
                            return;
                        pipelineEnded = false;
                        startedUtc = DateTime.UtcNow;
                        lastFrameUtc = DateTime.MinValue;
                        gst.Start(pipeline);
                    }
                }
                catch (Exception ex)
                {
                    log.Error("Video start failed", ex);
                    SetState(StreamState.Error, "Video start failed: " + ex.Message);
                }
                finally
                {
                    restartBusy = false;
                }
            });
        }

        private void OnNewImage(object sender, MPBitmap image)
        {
            if (image == null)
            {
                pipelineEnded = true;
                return;
            }

            if (!wantRunning)
                return;
            try
            {
                var w = image.Width;
                var h = image.Height;
                if (w <= 0 || h <= 0) return;
                var src = image.LockBits(Rectangle.Empty, null, SKColorType.Bgra8888).Scan0;
                var copy = new Bitmap(w, h, PixelFormat.Format32bppRgb);
                var bd = copy.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                try
                {
                    if (bd.Stride == w * 4)
                    {
                        CopyMemory(bd.Scan0, src, (UIntPtr)(ulong)((long)w * h * 4));
                    }
                    else
                    {
                        for (var y = 0; y < h; y++)
                            CopyMemory(bd.Scan0 + y * bd.Stride, src + y * w * 4, (UIntPtr)(uint)(w * 4));
                    }
                }
                finally
                {
                    copy.UnlockBits(bd);
                }

                bool becamePlaying;
                lock (frameLock)
                {
                    if (!wantRunning)
                    {
                        // Stop() happened while this frame was being copied - discard it
                        copy.Dispose();
                        return;
                    }

                    latest?.Dispose();
                    latest = copy;
                    FrameSize = new Size(w, h);
                    becamePlaying = State != StreamState.Playing;
                    if (becamePlaying)
                    {
                        failures = 0;
                        // inside the lock so a concurrent Stop() cannot be overwritten (handlers only BeginInvoke)
                        SetState(StreamState.Playing, $"Live \u2022 {w}x{h}");
                    }
                }

                lastFrameUtc = DateTime.UtcNow;
                Interlocked.Increment(ref frameId);
                fpsCount++;
                var span = (lastFrameUtc - fpsWindowStart).TotalSeconds;
                if (span >= 1)
                {
                    fps = fpsCount / span;
                    fpsCount = 0;
                    fpsWindowStart = lastFrameUtc;
                }

                if (becamePlaying)
                    SetState(StreamState.Playing, $"Live • {w}x{h}");

                FrameArrived?.Invoke();
            }
            catch (Exception ex)
            {
                log.Error("Frame copy failed", ex);
            }
        }

        /// <summary>Draws the newest frame letter-boxed into <paramref name="dest"/>; returns the image rectangle.</summary>
        public Rectangle DrawLatest(Graphics g, Rectangle dest)
        {
            lock (frameLock)
            {
                if (latest == null)
                    return Rectangle.Empty;
                var r = Fit(latest.Size, dest);
                g.DrawImage(latest, r);
                return r;
            }
        }

        /// <summary>Independent copy of the newest frame (caller disposes), or null.</summary>
        public Bitmap GetFrameCopy(out long id)
        {
            lock (frameLock)
            {
                id = Interlocked.Read(ref frameId);
                return latest == null ? null : (Bitmap)latest.Clone();
            }
        }

        public bool HasFrame
        {
            get
            {
                lock (frameLock) return latest != null;
            }
        }

        public static Rectangle Fit(Size img, Rectangle dest)
        {
            if (img.Width <= 0 || img.Height <= 0) return dest;
            var scale = Math.Min((double)dest.Width / img.Width, (double)dest.Height / img.Height);
            var w = (int)(img.Width * scale);
            var h = (int)(img.Height * scale);
            return new Rectangle(dest.X + (dest.Width - w) / 2, dest.Y + (dest.Height - h) / 2, w, h);
        }

        private void Watchdog()
        {
            try
            {
                if (!wantRunning || restartBusy)
                    return;
                var now = DateTime.UtcNow;
                string reason = null;
                if (pipelineEnded)
                    reason = "Video stream ended.";
                // generous first-frame timeout: GStreamer builds its plugin registry on first use (~30 s once)
                else if (State == StreamState.Starting && lastFrameUtc == DateTime.MinValue && (now - startedUtc).TotalSeconds > 45)
                    reason = "No video received within 45 s.";
                else if (State == StreamState.Playing && (now - lastFrameUtc).TotalSeconds > 6)
                    reason = "Video stream stalled.";

                if (State == StreamState.Reconnecting)
                {
                    if (now >= nextAttemptUtc)
                        Restart("Reconnecting (attempt " + (failures + 1) + ")...");
                    return;
                }

                if (reason == null)
                    return;

                failures++;
                var delay = Math.Min(30, 2 << Math.Min(4, failures - 1));
                nextAttemptUtc = now.AddSeconds(delay);
                SetState(StreamState.Reconnecting, reason + $" Reconnecting in {delay} s...");
                pipelineEnded = false;
                if (string.IsNullOrEmpty(CameraSettings.CustomPipeline))
                    _ = DiagnoseAsync(reason);
            }
            catch (Exception ex)
            {
                log.Error("Video watchdog failed", ex);
            }
        }

        /// <summary>Uses an RTSP DESCRIBE to turn a generic failure into an actionable message.</summary>
        private async Task DiagnoseAsync(string reason)
        {
            try
            {
                var r = await RtspProbe.DescribeAsync(CameraSettings.BuildRtspUrl(), CameraSettings.Username,
                    CameraSettings.Password, CancellationToken.None).ConfigureAwait(false);
                if (!wantRunning || State != StreamState.Reconnecting)
                    return;
                switch (r.Status)
                {
                    case RtspProbeStatus.AuthFailed:
                    case RtspProbeStatus.StreamNotFound:
                    case RtspProbeStatus.NotConfigured:
                        // retrying cannot fix these - stop and tell the user
                        wantRunning = false;
                        var g = gst;
                        _ = Task.Run(() => g?.Stop());
                        SetState(StreamState.Error, r.Message);
                        break;
                    case RtspProbeStatus.Ok:
                        SetState(StreamState.Reconnecting, reason + " Camera RTSP is OK (" + r.VideoCodec +
                                                           ") - the decoder may lack this codec. Retrying...");
                        break;
                    default:
                        SetState(StreamState.Reconnecting, r.Message + " Retrying...");
                        break;
                }
            }
            catch (Exception ex)
            {
                log.Error("RTSP diagnosis failed", ex);
            }
        }

        /// <summary>Builds the GStreamer pipeline. Credentials are passed as rtspsrc properties (redacted in logs).</summary>
        public static string BuildPipeline(out string displayUrl)
        {
            var custom = CameraSettings.CustomPipeline;
            if (!string.IsNullOrEmpty(custom))
            {
                displayUrl = "custom pipeline";
                return custom.Contains("appsink name=outsink") ? custom : null;
            }

            var url = CameraSettings.BuildRtspUrl();
            displayUrl = url;
            var sb = new StringBuilder();
            sb.Append("rtspsrc name=src location=").Append(Quote(url));
            sb.Append(" latency=").Append(CameraSettings.LatencyMs);
            if (CameraSettings.UseTcp)
                sb.Append(" protocols=tcp");
            var user = CameraSettings.Username;
            var pw = CameraSettings.Password;
            if (!string.IsNullOrEmpty(user))
                sb.Append(" user-id=").Append(Quote(user));
            if (!string.IsNullOrEmpty(pw))
                sb.Append(" user-pw=").Append(Quote(pw));
            sb.Append(" src. ! application/x-rtp,media=video ! decodebin ! queue max-size-buffers=1 leaky=downstream" +
                      " ! videoconvert ! video/x-raw,format=BGRA ! appsink name=outsink sync=false max-buffers=1 drop=true");
            if (CameraSettings.PlayCameraAudio)
                sb.Append(" src. ! application/x-rtp,media=audio ! queue ! decodebin ! audioconvert ! audioresample ! autoaudiosink sync=false");
            return sb.ToString();
        }

        private static string Quote(string s) => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
