using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using log4net;

namespace MissionPlanner.AeroAid.Camera
{
    /// <summary>Paths of a usable FFmpeg installation (ffmpeg.exe + ffprobe.exe, optional ffplay.exe).</summary>
    public sealed class FfmpegTools
    {
        public string Ffmpeg;
        public string Ffprobe;
        /// <summary>null when the build has no ffplay (camera audio is then unavailable).</summary>
        public string Ffplay;
        public string Directory => Path.GetDirectoryName(Ffmpeg);
    }

    /// <summary>
    /// Finds FFmpeg for live video. Nothing is downloaded or installed automatically. Search order:
    ///  1. Settings "aeroaid.camera.ffmpeg_path" (folder or ffmpeg.exe)
    ///  2. app-managed copy: &lt;AeroAid folder&gt;\ffmpeg\bin, \ffmpeg, or next to the exe
    ///  3. PATH of this process, then the CURRENT user/machine PATH from the registry
    ///     (picks up an FFmpeg installed after AeroAid / Visual Studio was started)
    ///  4. WinGet (Gyan.FFmpeg / BtbN), Chocolatey, Scoop, C:\ffmpeg\bin, Program Files\ffmpeg\bin
    /// </summary>
    public static class FfmpegLocator
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly object sync = new object();
        private static FfmpegTools cached;
        private static string cachedKey;
        private static readonly Dictionary<string, int> versionCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public const string NotFoundMessage =
            "FFmpeg was not found, so live video cannot be decoded. Install it once (for example run " +
            "\"winget install Gyan.FFmpeg\" in a terminal, or download a Windows build from https://ffmpeg.org) " +
            "or copy ffmpeg.exe and ffprobe.exe into the \"ffmpeg\" folder next to the AeroAid program, then press Start stream again.";

        /// <summary>Returns the FFmpeg tools, or null if FFmpeg is not installed. Fast (file checks only).</summary>
        public static FfmpegTools Find()
        {
            var key = CameraSettings.FfmpegPath;
            lock (sync)
            {
                if (cached != null && cachedKey == key && File.Exists(cached.Ffmpeg) && File.Exists(cached.Ffprobe))
                    return cached;
                cached = null;
                cachedKey = key;
                foreach (var dir in CandidateDirectories(key))
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(dir))
                            continue;
                        var ffmpeg = Path.Combine(dir, "ffmpeg.exe");
                        var ffprobe = Path.Combine(dir, "ffprobe.exe");
                        if (!File.Exists(ffmpeg) || !File.Exists(ffprobe))
                            continue;
                        var ffplay = Path.Combine(dir, "ffplay.exe");
                        cached = new FfmpegTools { Ffmpeg = ffmpeg, Ffprobe = ffprobe, Ffplay = File.Exists(ffplay) ? ffplay : null };
                        log.Info("Using FFmpeg from " + dir);
                        return cached;
                    }
                    catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is IOException ||
                                               ex is UnauthorizedAccessException)
                    {
                        // malformed PATH entry etc. - skip
                    }
                }

                log.Warn("FFmpeg not found");
                return null;
            }
        }

        private static IEnumerable<string> CandidateDirectories(string configured)
        {
            if (!string.IsNullOrEmpty(configured))
                yield return configured.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(configured) : configured;

            var app = AppDomain.CurrentDomain.BaseDirectory;
            yield return Path.Combine(app, "ffmpeg", "bin");
            yield return Path.Combine(app, "ffmpeg");
            yield return app;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var target in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
            {
                string path;
                try
                {
                    path = Environment.GetEnvironmentVariable("PATH", target) ?? "";
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (var raw in path.Split(';'))
                {
                    var d = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));
                    if (d.Length > 0 && seen.Add(d))
                        yield return d;
                }
            }

            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var winget = Path.Combine(local, "Microsoft", "WinGet", "Packages");
            if (System.IO.Directory.Exists(winget))
            {
                IEnumerable<string> bins;
                try
                {
                    bins = System.IO.Directory.GetDirectories(winget, "*FFmpeg*")
                        .SelectMany(p => System.IO.Directory.GetDirectories(p))
                        .Select(p => Path.Combine(p, "bin"))
                        .OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
                catch (Exception)
                {
                    bins = Enumerable.Empty<string>();
                }

                foreach (var b in bins)
                    yield return b;
            }

            yield return Path.Combine(local, "Microsoft", "WinGet", "Links");
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            yield return Path.Combine(programData, "chocolatey", "bin");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "shims");
            yield return @"C:\ffmpeg\bin";
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin");
        }

        /// <summary>
        /// Major version of an ffmpeg/ffprobe executable (cached). FFmpeg 5+ uses "-timeout" for the RTSP socket
        /// timeout; 4.x used "-stimeout" ("-timeout" there means *listen* mode). Unknown/git builds = modern.
        /// </summary>
        public static int MajorVersion(string exe)
        {
            lock (sync)
            {
                if (versionCache.TryGetValue(exe, out var v))
                    return v;
            }

            var major = 99;
            try
            {
                using (var p = Process.Start(new ProcessStartInfo(exe, "-hide_banner -version")
                       {
                           UseShellExecute = false,
                           CreateNoWindow = true,
                           RedirectStandardOutput = true,
                           RedirectStandardError = true,
                           RedirectStandardInput = true
                       }))
                {
                    var outTask = p.StandardOutput.ReadToEndAsync();
                    p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(5000))
                    {
                        try { p.Kill(); } catch (Exception) { }
                    }

                    major = ParseMajorVersion(outTask.Wait(1000) ? outTask.Result : "");
                }
            }
            catch (Exception ex)
            {
                log.Warn("Could not read FFmpeg version: " + ex.Message);
            }

            lock (sync)
                versionCache[exe] = major;
            return major;
        }

        /// <summary>"ffmpeg version 7.1-full_build..." / "n4.4.2" -> 7 / 4; dated git builds -> 99.</summary>
        public static int ParseMajorVersion(string versionOutput)
        {
            var m = Regex.Match(versionOutput ?? "", @"version\s+n?(\d+)\.(\d+)");
            return m.Success && int.TryParse(m.Groups[1].Value, out var major) ? major : 99;
        }

        /// <summary>Name of the RTSP socket-timeout option for this FFmpeg version (microseconds).</summary>
        public static string SocketTimeoutOption(string exe) => MajorVersion(exe) >= 5 ? "-timeout" : "-stimeout";
    }
}
