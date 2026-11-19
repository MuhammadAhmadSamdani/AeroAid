using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using log4net;

namespace MissionPlanner.AeroAid.Camera
{
    /// <summary>Video stream properties reported by ffprobe.</summary>
    public sealed class FfprobeResult
    {
        public bool Ok;
        public bool AuthFailed;
        public bool NotFound;
        public string Error = "";
        public string Codec = "";
        public int Width;
        public int Height;
        public double Fps;
        /// <summary>Sample (pixel) aspect ratio; 1 for square pixels (normal for IP cameras).</summary>
        public double PixelAspect = 1;
    }

    /// <summary>
    /// Decodes an RTSP stream with an owned ffmpeg.exe process (same approach as the reference imou_stream.py):
    ///   ffmpeg -rtsp_transport tcp -fflags nobuffer -flags low_delay -i URL -an -vf scale=W:H -pix_fmt bgr24 -f rawvideo pipe:1
    /// A background thread reads exactly W*H*3 bytes per frame from stdout and raises <see cref="FrameReady"/>.
    /// The process is placed in a kill-on-close Windows job, so it can never outlive AeroAid (even after a crash).
    /// Credentials are only ever passed to the child process - all logs/messages are redacted.
    /// </summary>
    public sealed class FfmpegVideoSource : IDisposable
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly FfmpegTools tools;
        private readonly string url;
        private readonly bool tcp;
        private readonly bool audio;
        private readonly object sync = new object();
        private readonly Queue<string> errorLines = new Queue<string>();
        private Process video;
        private Process audioProcess;
        private Thread reader;
        private volatile bool disposed;

        public int Width { get; }
        public int Height { get; }
        /// <summary>Owner tag (the stream manager's start generation) - lets stale callbacks be ignored.</summary>
        public int Generation { get; }

        /// <summary>(source, BGR24 buffer of Height rows x Width*3 bytes, reused - copy before returning).</summary>
        public event Action<FfmpegVideoSource, byte[]> FrameReady;

        /// <summary>(source, last decoder error or null) - raised once when ffmpeg exits / the pipe closes.</summary>
        public event Action<FfmpegVideoSource, string> Ended;

        public FfmpegVideoSource(FfmpegTools tools, string authenticatedUrl, int width, int height, bool tcp, bool audio, int generation)
        {
            if (width <= 0 || height <= 0)
                throw new ArgumentException("Invalid output size");
            this.tools = tools;
            url = authenticatedUrl;
            Width = width;
            Height = height;
            this.tcp = tcp;
            this.audio = audio;
            Generation = generation;
        }

        // ------------------------------------------------------------------ arguments

        private static string Quote(string s) => "\"" + (s ?? "").Replace("\"", "%22") + "\"";

        private static string Transport(bool tcp) => tcp ? "tcp" : "udp";

        /// <summary>ffmpeg arguments (contain the credentials - never log un-redacted).</summary>
        public static string BuildFfmpegArguments(string url, int width, int height, bool tcp, string timeoutOption)
        {
            return "-hide_banner -nostdin -loglevel error" +
                   " -rtsp_transport " + Transport(tcp) +
                   " -fflags nobuffer -flags low_delay" +
                   " " + timeoutOption + " 5000000" +
                   " -i " + Quote(url) +
                   " -an -sn -dn" +
                   " -vf scale=" + width + ":" + height +
                   " -pix_fmt bgr24 -f rawvideo pipe:1";
        }

        public static string BuildFfprobeArguments(string url, bool tcp, string timeoutOption)
        {
            return "-hide_banner -v error" +
                   " -rtsp_transport " + Transport(tcp) +
                   " " + timeoutOption + " 8000000" +
                   " -select_streams v:0" +
                   " -show_entries stream=codec_name,width,height,avg_frame_rate,r_frame_rate,sample_aspect_ratio" +
                   " -of default=noprint_wrappers=1 " + Quote(url);
        }

        // ------------------------------------------------------------------ ffprobe

        /// <summary>Runs ffprobe (blocking, call off the UI thread). Cancelling kills ffprobe immediately.</summary>
        public static FfprobeResult Probe(FfmpegTools tools, string authenticatedUrl, bool tcp, CancellationToken ct, int timeoutMs = 15000)
        {
            var result = new FfprobeResult();
            var args = BuildFfprobeArguments(authenticatedUrl, tcp, FfmpegLocator.SocketTimeoutOption(tools.Ffprobe));
            Process p = null;
            try
            {
                p = Process.Start(NewStartInfo(tools.Ffprobe, args));
                JobObject.Assign(p);
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                var proc = p;
                using (ct.Register(() => KillQuietly(proc)))
                {
                    if (!p.WaitForExit(timeoutMs))
                    {
                        KillQuietly(p);
                        result.Error = "ffprobe timed out after " + timeoutMs / 1000 + " s";
                        return result;
                    }
                }

                ct.ThrowIfCancellationRequested();
                var stdout = outTask.Wait(2000) ? outTask.Result : "";
                var stderr = errTask.Wait(2000) ? errTask.Result : "";
                ParseProbeOutput(stdout, result);
                if (!result.Ok)
                {
                    result.Error = CameraSettings.RedactSecrets(LastLines(stderr, 3));
                    if (string.IsNullOrEmpty(result.Error))
                        result.Error = "ffprobe found no video stream";
                    result.AuthFailed = stderr.Contains("401") || stderr.IndexOf("Unauthorized", StringComparison.OrdinalIgnoreCase) >= 0;
                    result.NotFound = stderr.Contains("404");
                }

                return result;
            }
            catch (Win32Exception ex)
            {
                result.Error = "Could not run ffprobe: " + ex.Message;
                return result;
            }
            finally
            {
                p?.Dispose();
            }
        }

        /// <summary>Parses "key=value" lines from ffprobe -of default=noprint_wrappers=1.</summary>
        public static void ParseProbeOutput(string stdout, FfprobeResult r)
        {
            string avg = null, rate = null;
            foreach (var raw in (stdout ?? "").Split('\n'))
            {
                var line = raw.Trim();
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var k = line.Substring(0, eq);
                var v = line.Substring(eq + 1);
                switch (k)
                {
                    case "codec_name": if (r.Codec.Length == 0) r.Codec = v; break;
                    case "width": if (r.Width == 0) int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out r.Width); break;
                    case "height": if (r.Height == 0) int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out r.Height); break;
                    case "avg_frame_rate": avg = avg ?? v; break;
                    case "r_frame_rate": rate = rate ?? v; break;
                    case "sample_aspect_ratio":
                        var sar = Ratio(v);
                        if (sar > 0.1 && sar < 10) r.PixelAspect = sar;
                        break;
                }
            }

            var fps = Ratio(avg);
            if (fps <= 0 || fps > 240) fps = Ratio(rate);
            r.Fps = fps > 0 && fps <= 240 ? fps : 0;
            r.Ok = r.Width > 0 && r.Height > 0;
        }

        private static double Ratio(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            var parts = s.Split('/', ':');
            if (parts.Length == 2 &&
                double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d != 0)
                return n / d;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : 0;
        }

        /// <summary>
        /// Output frame size: the source display size (pixel aspect applied) scaled down to at most
        /// <paramref name="maxWidth"/>, keeping the aspect ratio, with even dimensions.
        /// </summary>
        public static Size OutputSize(FfprobeResult info, int maxWidth)
        {
            double w = info.Width * (info.PixelAspect > 0 ? info.PixelAspect : 1);
            double h = info.Height;
            if (w > maxWidth)
            {
                h = h * maxWidth / w;
                w = maxWidth;
            }

            int Even(double v) => Math.Max(2, (int)Math.Round(v / 2) * 2);
            return new Size(Even(w), Even(h));
        }

        // ------------------------------------------------------------------ decoding

        public void Start()
        {
            lock (sync)
            {
                if (disposed)
                    return;
                var args = BuildFfmpegArguments(url, Width, Height, tcp, FfmpegLocator.SocketTimeoutOption(tools.Ffmpeg));
                log.Info("Starting " + tools.Ffmpeg + " " + CameraSettings.RedactSecrets(args));
                video = new Process { StartInfo = NewStartInfo(tools.Ffmpeg, args), EnableRaisingEvents = false };
                video.ErrorDataReceived += (s, e) =>
                {
                    if (string.IsNullOrWhiteSpace(e.Data)) return;
                    lock (errorLines)
                    {
                        errorLines.Enqueue(e.Data.Trim());
                        while (errorLines.Count > 4) errorLines.Dequeue();
                    }
                };
                video.Start();
                JobObject.Assign(video);
                // stderr must be drained continuously - a full pipe would freeze ffmpeg
                video.BeginErrorReadLine();
                var stdout = video.StandardOutput.BaseStream;
                reader = new Thread(() => ReadLoop(stdout)) { IsBackground = true, Name = "AeroAid FFmpeg video reader" };
                reader.Start();

                if (audio)
                    StartAudio();
            }
        }

        private void StartAudio()
        {
            if (tools.Ffplay == null)
            {
                log.Info("Camera audio requested but ffplay.exe is not available");
                return;
            }

            try
            {
                var args = "-hide_banner -nodisp -vn -loglevel quiet -rtsp_transport " + Transport(tcp) +
                           " -fflags nobuffer -flags low_delay " + Quote(url);
                audioProcess = Process.Start(NewStartInfo(tools.Ffplay, args, redirect: false));
                JobObject.Assign(audioProcess);
            }
            catch (Exception ex)
            {
                log.Warn("Camera audio (ffplay) failed: " + CameraSettings.RedactSecrets(ex.Message));
            }
        }

        private void ReadLoop(Stream stdout)
        {
            var frameBytes = Width * Height * 3;
            var buffer = new byte[frameBytes];
            try
            {
                while (!disposed)
                {
                    var read = 0;
                    while (read < frameBytes)
                    {
                        var n = stdout.Read(buffer, read, frameBytes - read);
                        if (n <= 0)
                            return; // ffmpeg exited / pipe closed
                        read += n;
                    }

                    if (disposed)
                        return;
                    try
                    {
                        FrameReady?.Invoke(this, buffer);
                    }
                    catch (Exception ex)
                    {
                        log.Error("Frame handler failed", ex);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException)
            {
                // pipe broken by Stop()/process exit
            }
            finally
            {
                if (!disposed)
                {
                    var err = LastError;
                    log.Info("FFmpeg video ended" + (err == null ? "" : ": " + err));
                    try
                    {
                        Ended?.Invoke(this, err);
                    }
                    catch (Exception ex)
                    {
                        log.Error("Ended handler failed", ex);
                    }
                }
            }
        }

        /// <summary>Last ffmpeg error output (redacted), or null.</summary>
        public string LastError
        {
            get
            {
                lock (errorLines)
                    return errorLines.Count == 0 ? null : CameraSettings.RedactSecrets(string.Join(" | ", errorLines));
            }
        }

        /// <summary>Terminates the owned processes without waiting (safe from any thread, idempotent).</summary>
        public void Kill()
        {
            disposed = true;
            KillQuietly(video);
            KillQuietly(audioProcess);
        }

        /// <summary>Terminates the processes, waits for them and the reader thread, releases handles.</summary>
        public void Dispose()
        {
            Kill();
            lock (sync)
            {
                foreach (var p in new[] { video, audioProcess })
                {
                    if (p == null) continue;
                    try
                    {
                        p.WaitForExit(2000);
                    }
                    catch (Exception)
                    {
                        // not started / already disposed
                    }

                    p.Dispose();
                }

                video = null;
                audioProcess = null;
                if (reader != null && reader != Thread.CurrentThread)
                    reader.Join(2000);
                reader = null;
            }
        }

        // ------------------------------------------------------------------ helpers

        private static ProcessStartInfo NewStartInfo(string exe, string args, bool redirect = true)
        {
            return new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = redirect,
                RedirectStandardOutput = redirect,
                RedirectStandardError = redirect,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? ""
            };
        }

        private static void KillQuietly(Process p)
        {
            try
            {
                if (p != null && !p.HasExited)
                    p.Kill();
            }
            catch (Exception)
            {
                // already exited / not started / disposed
            }
        }

        private static string LastLines(string text, int count)
        {
            var lines = (text ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var sb = new StringBuilder();
            for (var i = Math.Max(0, lines.Length - count); i < lines.Length; i++)
                sb.Append(sb.Length > 0 ? " | " : "").Append(lines[i].Trim());
            return sb.ToString();
        }

        /// <summary>A kill-on-close job object: child decoder processes die with AeroAid, even on a crash.</summary>
        private static class JobObject
        {
            private static readonly IntPtr handle = Create();

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
            private static extern IntPtr CreateJobObject(IntPtr attributes, string name);

            [DllImport("kernel32.dll")]
            private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimitInformation info, uint length);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

            [StructLayout(LayoutKind.Sequential)]
            private struct BasicLimitInformation
            {
                public long PerProcessUserTimeLimit;
                public long PerJobUserTimeLimit;
                public uint LimitFlags;
                public UIntPtr MinimumWorkingSetSize;
                public UIntPtr MaximumWorkingSetSize;
                public uint ActiveProcessLimit;
                public UIntPtr Affinity;
                public uint PriorityClass;
                public uint SchedulingClass;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct IoCounters
            {
                public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
                public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct ExtendedLimitInformation
            {
                public BasicLimitInformation BasicLimitInformation;
                public IoCounters IoInfo;
                public UIntPtr ProcessMemoryLimit;
                public UIntPtr JobMemoryLimit;
                public UIntPtr PeakProcessMemoryUsed;
                public UIntPtr PeakJobMemoryUsed;
            }

            private static IntPtr Create()
            {
                try
                {
                    var job = CreateJobObject(IntPtr.Zero, null);
                    if (job == IntPtr.Zero)
                        return IntPtr.Zero;
                    var info = new ExtendedLimitInformation();
                    info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                    const int extendedLimitInformation = 9;
                    if (!SetInformationJobObject(job, extendedLimitInformation, ref info, (uint)Marshal.SizeOf(typeof(ExtendedLimitInformation))))
                        log.Warn("SetInformationJobObject failed - decoder processes may survive a crash");
                    return job; // intentionally never closed: the OS closes it (killing the children) when AeroAid exits
                }
                catch (Exception ex)
                {
                    log.Warn("Job object unavailable: " + ex.Message);
                    return IntPtr.Zero;
                }
            }

            public static void Assign(Process p)
            {
                try
                {
                    if (handle != IntPtr.Zero && p != null && !AssignProcessToJobObject(handle, p.Handle))
                        log.Warn("AssignProcessToJobObject failed: " + Marshal.GetLastWin32Error());
                }
                catch (Exception ex)
                {
                    log.Warn("AssignProcessToJobObject failed: " + ex.Message);
                }
            }
        }
    }
}
