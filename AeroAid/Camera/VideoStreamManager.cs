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
    /// Single shared live-video pipeline. RTSP video is decoded by an owned FFmpeg process (RTSP over TCP,
    /// raw BGR24 frames over a pipe - see <see cref="FfmpegVideoSource"/>); GStreamer is only used for the
    /// advanced "custom pipeline" setting, or as a fallback when FFmpeg is missing but GStreamer is installed.
    /// Frames are copied into a System.Drawing bitmap so that any number of consumers (live view, dashboard
    /// preview, AI detection) can read them safely.
    /// Handles start/stop, stall detection, automatic reconnection with back-off and RTSP diagnostics.
    /// Video never depends on ONVIF: ONVIF/PTZ is probed separately by <see cref="CameraConnectionManager"/>.
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
        private volatile int gstGeneration = -1;
        private volatile FfmpegVideoSource ffmpeg;
        private CancellationTokenSource startCts = new CancellationTokenSource();
        /// <summary>Incremented by every Start/Restart/Stop; work and frames from older generations are discarded.</summary>
        private int generation;
        private Bitmap latest;
        private long frameId;
        private DateTime lastFrameUtc = DateTime.MinValue;
        private DateTime startedUtc = DateTime.MinValue;
        private DateTime nextAttemptUtc = DateTime.MinValue;
        private volatile bool wantRunning;
        private volatile bool pipelineEnded;
        private volatile bool restartBusy;
        private volatile bool usingGStreamer;
        private volatile string decoderError;
        private int failures;
        private double fps;
        private DateTime fpsWindowStart = DateTime.UtcNow;
        private int fpsCount;

        public StreamState State { get; private set; } = StreamState.Stopped;
        public string StatusMessage { get; private set; } = "Stream stopped.";
        public string DisplayUrl { get; private set; } = "";
        /// <summary>Decoder in use, e.g. "FFmpeg" or "GStreamer (custom pipeline)".</summary>
        public string DecoderName { get; private set; } = "";
        public long FrameId => Interlocked.Read(ref frameId);
        public double Fps => fps;
        public Size FrameSize { get; private set; }

        /// <summary>(state, message) - raised on worker threads.</summary>
        public event Action<StreamState, string> StateChanged;

        /// <summary>Raised on the decoder thread after each new frame is stored (keep handlers tiny).</summary>
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

        private bool IsCurrent(int gen) => wantRunning && gen == Volatile.Read(ref generation);

        /// <summary>Sets the state only if <paramref name="gen"/> is still the active start (no stale overwrite).</summary>
        private bool SetStateIfCurrent(int gen, StreamState s, string message)
        {
            lock (frameLock)
            {
                if (!IsCurrent(gen))
                    return false;
                SetState(s, message);
                return true;
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

        /// <summary>true if ffmpeg.exe and ffprobe.exe were found (fast, file checks only).</summary>
        public static bool FfmpegAvailable() => FfmpegLocator.Find() != null;

        /// <summary>Only the advanced custom pipeline setting requires GStreamer.</summary>
        public static bool NeedsGStreamer => !string.IsNullOrEmpty(CameraSettings.CustomPipeline);

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
            Restart("Connecting to camera...");
        }

        public void Stop()
        {
            int gen;
            CancellationTokenSource cts;
            // frameLock makes "stop" atomic with respect to frame delivery (no frame/state after Stop)
            lock (frameLock)
            {
                wantRunning = false;
                gen = Interlocked.Increment(ref generation);
                cts = startCts;
                startCts = new CancellationTokenSource();
            }

            CancelQuietly(cts);
            restartBusy = false;
            // terminate the decoder immediately (non-blocking); waiting/cleanup happens off the UI thread
            ffmpeg?.Kill();
            Task.Run(() =>
            {
                try
                {
                    lock (controlLock)
                    {
                        // a newer Start() already replaced (and stopped) the decoders - leave its pipeline alone
                        if (gen == Volatile.Read(ref generation))
                            StopDecoders();
                    }
                }
                catch (Exception ex)
                {
                    log.Error("Video stop failed", ex);
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

        private static void CancelQuietly(CancellationTokenSource cts)
        {
            try
            {
                cts.Cancel();
            }
            catch (Exception)
            {
                // a registration callback failed - nothing to do
            }
        }

        /// <summary>Stops and releases all decoders. Caller holds controlLock; runs off the UI thread.</summary>
        private void StopDecoders()
        {
            var f = ffmpeg;
            ffmpeg = null;
            f?.Dispose();
            gstGeneration = -1;
            gst?.Stop();
        }

        private void Restart(string message)
        {
            int gen;
            CancellationToken ct;
            CancellationTokenSource old;
            lock (frameLock)
            {
                if (!wantRunning)
                    return;
                gen = Interlocked.Increment(ref generation);
                old = startCts;
                startCts = new CancellationTokenSource();
                ct = startCts.Token;
                restartBusy = true;
                SetState(StreamState.Starting, message);
            }

            CancelQuietly(old); // abort an older start that is still probing
            Task.Run(() =>
            {
                try
                {
                    lock (controlLock)
                    {
                        if (!IsCurrent(gen))
                            return;
                        StopDecoders();
                        if (!IsCurrent(gen))
                            return;

                        var tools = NeedsGStreamer ? null : FfmpegLocator.Find();
                        if (!NeedsGStreamer && tools == null && GStreamerAvailable())
                        {
                            log.Warn("FFmpeg not found - falling back to the installed GStreamer runtime");
                            StartGStreamer(gen);
                        }
                        else if (NeedsGStreamer)
                            StartGStreamer(gen);
                        else if (tools == null)
                        {
                            if (SetStateIfCurrent(gen, StreamState.Error, FfmpegLocator.NotFoundMessage))
                                wantRunning = false;
                        }
                        else
                            StartFfmpeg(gen, tools, ct);
                    }
                }
                catch (OperationCanceledException)
                {
                    // superseded by Stop()/a newer start
                }
                catch (Exception ex)
                {
                    log.Error("Video start failed", ex);
                    SetStateIfCurrent(gen, StreamState.Error, "Video start failed: " + CameraSettings.RedactSecrets(ex.Message));
                }
                finally
                {
                    if (gen == Volatile.Read(ref generation))
                        restartBusy = false;
                }
            });
        }

        private void ScheduleRetry(int gen, string message)
        {
            failures++;
            var delay = Math.Min(30, 2 << Math.Min(4, failures - 1));
            nextAttemptUtc = DateTime.UtcNow.AddSeconds(delay);
            SetStateIfCurrent(gen, StreamState.Reconnecting, message + $" Retrying in {delay} s...");
        }

        /// <summary>
        /// Quick RTSP check (TCP connect + DESCRIBE/auth). Returns false (and sets the state) if starting
        /// the decoder is pointless right now. Independent of ONVIF.
        /// </summary>
        private bool PreCheckRtsp(int gen, string displayUrl, CancellationToken ct)
        {
            var probe = RtspProbe.DescribeAsync(displayUrl, CameraSettings.Username, CameraSettings.Password, ct, 5000)
                .GetAwaiter().GetResult();
            if (!IsCurrent(gen))
                return false; // Stop() was pressed while probing - keep the Stopped state
            switch (probe.Status)
            {
                case RtspProbeStatus.AuthFailed:
                case RtspProbeStatus.StreamNotFound:
                case RtspProbeStatus.NotConfigured:
                    if (SetStateIfCurrent(gen, StreamState.Error, probe.Message))
                        wantRunning = false; // retrying cannot fix these
                    return false;
                case RtspProbeStatus.Unreachable:
                case RtspProbeStatus.Error:
                    ScheduleRetry(gen, probe.Message);
                    return false;
                default:
                    return true;
            }
        }

        private void StartFfmpeg(int gen, FfmpegTools tools, CancellationToken ct)
        {
            usingGStreamer = false;
            DecoderName = "FFmpeg";
            var display = CameraSettings.BuildRtspUrl();
            DisplayUrl = display;

            // 1) fast RTSP diagnostics (FFmpeg alone would only say "connection failed" after its timeout)
            if (!PreCheckRtsp(gen, display, ct))
                return;

            // 2) ffprobe: real codec / resolution / frame rate of the stream
            var url = CameraSettings.BuildAuthenticatedRtspUrl();
            var tcp = CameraSettings.UseTcp;
            SetStateIfCurrent(gen, StreamState.Starting, "Camera RTSP OK - reading stream format...");
            var info = FfmpegVideoSource.Probe(tools, url, tcp, ct);
            if (!IsCurrent(gen))
                return;
            if (!info.Ok)
            {
                if (info.AuthFailed)
                {
                    if (SetStateIfCurrent(gen, StreamState.Error, "Authentication failed: wrong username or password."))
                        wantRunning = false;
                }
                else
                    ScheduleRetry(gen, "FFprobe could not read the video stream (" + info.Error + ").");
                return;
            }

            // 3) decoder process
            var size = FfmpegVideoSource.OutputSize(info, CameraSettings.MaxVideoWidth);
            var source = new FfmpegVideoSource(tools, url, size.Width, size.Height, tcp, CameraSettings.PlayCameraAudio, gen);
            source.FrameReady += OnFfmpegFrame;
            source.Ended += OnFfmpegEnded;
            if (!SetStateIfCurrent(gen, StreamState.Starting,
                    $"Connecting to RTSP video ({info.Codec} {info.Width}x{info.Height}" +
                    (info.Fps > 0 ? $" @ {info.Fps:0.#} fps" : "") + ")..."))
                return;
            pipelineEnded = false;
            decoderError = null;
            startedUtc = DateTime.UtcNow;
            lastFrameUtc = DateTime.MinValue;
            ffmpeg = source;
            source.Start();
        }

        private void StartGStreamer(int gen)
        {
            usingGStreamer = true;
            DecoderName = NeedsGStreamer ? "GStreamer (custom pipeline)" : "GStreamer";
            if (!GStreamerAvailable())
            {
                if (SetStateIfCurrent(gen, StreamState.Error,
                        "The custom GStreamer pipeline (Settings › Camera) needs GStreamer, which is not installed. " +
                        "Clear the custom pipeline to use the built-in FFmpeg RTSP video."))
                    wantRunning = false;
                return;
            }

            var pipeline = BuildPipeline(out var display);
            DisplayUrl = display;
            if (pipeline == null)
            {
                if (SetStateIfCurrent(gen, StreamState.Error, "Invalid custom pipeline: it must end with 'appsink name=outsink'."))
                    wantRunning = false;
                return;
            }

            if (!NeedsGStreamer && !PreCheckRtsp(gen, display, CancellationToken.None))
                return;

            if (gst == null)
            {
                gst = new GStreamer();
                gst.OnNewImage += OnNewImage;
            }

            if (!IsCurrent(gen))
                return;
            pipelineEnded = false;
            decoderError = null;
            startedUtc = DateTime.UtcNow;
            lastFrameUtc = DateTime.MinValue;
            gstGeneration = gen;
            gst.Start(pipeline);
        }

        private void OnFfmpegEnded(FfmpegVideoSource source, string error)
        {
            if (source.Generation != Volatile.Read(ref generation))
                return;
            decoderError = error;
            pipelineEnded = true;
        }

        private void OnFfmpegFrame(FfmpegVideoSource source, byte[] bgr)
        {
            var gen = source.Generation;
            if (!IsCurrent(gen))
                return;
            int w = source.Width, h = source.Height;
            var copy = new Bitmap(w, h, PixelFormat.Format24bppRgb); // GDI 24bpp memory order is B,G,R = ffmpeg bgr24
            var bd = copy.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                var row = w * 3;
                if (bd.Stride == row)
                    Marshal.Copy(bgr, 0, bd.Scan0, row * h);
                else
                    for (var y = 0; y < h; y++) // GDI rows are padded to 4 bytes
                        Marshal.Copy(bgr, y * row, bd.Scan0 + y * bd.Stride, row);
            }
            finally
            {
                copy.UnlockBits(bd);
            }

            StoreFrame(gen, copy, w, h);
        }

        private void OnNewImage(object sender, MPBitmap image)
        {
            var gen = gstGeneration;
            if (image == null)
            {
                if (gen == Volatile.Read(ref generation))
                    pipelineEnded = true;
                return;
            }

            if (!IsCurrent(gen))
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

                StoreFrame(gen, copy, w, h);
            }
            catch (Exception ex)
            {
                log.Error("Frame copy failed", ex);
            }
        }

        /// <summary>Publishes a decoded frame (takes ownership of <paramref name="copy"/>).</summary>
        private void StoreFrame(int gen, Bitmap copy, int w, int h)
        {
            lock (frameLock)
            {
                if (!IsCurrent(gen))
                {
                    // Stop()/restart happened while this frame was being copied - discard it
                    copy.Dispose();
                    return;
                }

                latest?.Dispose();
                latest = copy;
                FrameSize = new Size(w, h);
                if (State != StreamState.Playing)
                {
                    failures = 0;
                    // inside the lock so a concurrent Stop() cannot be overwritten (handlers only BeginInvoke)
                    SetState(StreamState.Playing, $"RTSP video connected • {w}x{h} • {DecoderName}");
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

            try
            {
                FrameArrived?.Invoke();
            }
            catch (Exception ex)
            {
                log.Error("FrameArrived handler failed", ex);
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
                if (State == StreamState.Reconnecting)
                {
                    if (now >= nextAttemptUtc)
                        Restart("Reconnecting (attempt " + (failures + 1) + ")...");
                    return;
                }

                string reason = null;
                // GStreamer builds its plugin registry on first use (~30 s once); FFmpeg starts in a few seconds
                var firstFrameTimeout = usingGStreamer ? 45 : 20;
                if (pipelineEnded)
                    reason = "Video stream ended" + (string.IsNullOrEmpty(decoderError) ? "." : ": " + decoderError);
                else if (State == StreamState.Starting && lastFrameUtc == DateTime.MinValue && startedUtc != DateTime.MinValue &&
                         (now - startedUtc).TotalSeconds > firstFrameTimeout)
                    reason = $"No video received within {firstFrameTimeout} s.";
                else if (State == StreamState.Playing && (now - lastFrameUtc).TotalSeconds > 6)
                    reason = "Video stream stalled.";

                if (reason == null)
                    return;

                failures++;
                var delay = Math.Min(30, 2 << Math.Min(4, failures - 1));
                nextAttemptUtc = now.AddSeconds(delay);
                var gen = Volatile.Read(ref generation);
                if (!SetStateIfCurrent(gen, StreamState.Reconnecting, reason + $" Reconnecting in {delay} s..."))
                    return;
                pipelineEnded = false;
                startedUtc = DateTime.MinValue;
                // free the dead/stalled decoder now (the reconnect replaces it)
                ffmpeg?.Kill();
                if (string.IsNullOrEmpty(CameraSettings.CustomPipeline))
                    _ = DiagnoseAsync(gen, reason);
            }
            catch (Exception ex)
            {
                log.Error("Video watchdog failed", ex);
            }
        }

        /// <summary>Uses an RTSP DESCRIBE to turn a generic failure into an actionable message.</summary>
        private async Task DiagnoseAsync(int gen, string reason)
        {
            try
            {
                var r = await RtspProbe.DescribeAsync(CameraSettings.BuildRtspUrl(), CameraSettings.Username,
                    CameraSettings.Password, CancellationToken.None).ConfigureAwait(false);
                if (!IsCurrent(gen) || State != StreamState.Reconnecting)
                    return;
                switch (r.Status)
                {
                    case RtspProbeStatus.AuthFailed:
                    case RtspProbeStatus.StreamNotFound:
                    case RtspProbeStatus.NotConfigured:
                        // retrying cannot fix these - stop and tell the user
                        if (!SetStateIfCurrent(gen, StreamState.Error, r.Message))
                            return;
                        wantRunning = false;
                        _ = Task.Run(() =>
                        {
                            lock (controlLock)
                            {
                                if (gen == Volatile.Read(ref generation))
                                    StopDecoders();
                            }
                        });
                        break;
                    case RtspProbeStatus.Ok:
                        SetStateIfCurrent(gen, StreamState.Reconnecting, reason + " Camera RTSP is OK (" + r.VideoCodec +
                                                                        ") - the decoder will be restarted...");
                        break;
                    default:
                        SetStateIfCurrent(gen, StreamState.Reconnecting, r.Message + " Retrying...");
                        break;
                }
            }
            catch (Exception ex)
            {
                log.Error("RTSP diagnosis failed", ex);
            }
        }

        /// <summary>
        /// Builds the GStreamer pipeline (custom pipeline, or the RTSP fallback used only when FFmpeg is missing
        /// but GStreamer is installed). Credentials are passed as rtspsrc properties (redacted in logs).
        /// </summary>
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
