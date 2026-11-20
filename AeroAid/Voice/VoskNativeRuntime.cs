using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace MissionPlanner.AeroAid.Voice
{
    /// <summary>
    /// Protects the offline recogniser (libvosk) from a MinGW runtime clash.
    ///
    /// libvosk.dll needs the MinGW runtime it was built with: libwinpthread-1.dll, libgcc_s_seh-1.dll and
    /// libstdc++-6.dll, which the Vosk package copies next to the exe. Mission Planner's GStreamer runtime
    /// (C:\ProgramData\Mission Planner\gstreamer\...\bin) ships DIFFERENT DLLs with the SAME names (a 2018
    /// libwinpthread-1.dll; an sjlj libstdc++-6.dll). Windows loads a DLL name only once per process, so whichever
    /// component loads first wins. When GStreamer was loaded first, libvosk ran on GStreamer's winpthread and Kaldi
    /// aborted the WHOLE PROCESS ("ASSERTION_FAILED ... kaldi-thread.h:263") - reproduced in the test suite
    /// (CameraAndSettingsTests followed by a Vosk test).
    ///
    /// Fix: <see cref="Preload"/> loads Vosk's own runtime by absolute path at process start, before anything can
    /// load GStreamer; GStreamer's C code then uses the newer, backward-compatible winpthread. As a second line of
    /// defence, <see cref="FindConflict"/> is checked before libvosk is ever loaded: if a foreign copy is already in
    /// the process, Vosk is NOT started (the app falls back to Windows speech and says why) instead of crashing.
    /// </summary>
    public static class VoskNativeRuntime
    {
        /// <summary>In dependency order.</summary>
        public static readonly string[] RuntimeDlls = { "libwinpthread-1.dll", "libgcc_s_seh-1.dll", "libstdc++-6.dll" };

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryW(string path);

        /// <summary>What <see cref="Preload"/> did (for the log / diagnostics).</summary>
        public static string PreloadResult { get; private set; } = "not attempted";

        private static string AppDir => AppDomain.CurrentDomain.BaseDirectory;

        /// <summary>
        /// Loads Vosk's MinGW runtime from the application folder. Call first thing at process start. Never throws.
        /// </summary>
        public static void Preload()
        {
            try
            {
                if (!Environment.Is64BitProcess || !File.Exists(Path.Combine(AppDir, "libvosk.dll")))
                {
                    PreloadResult = "skipped (no 64-bit libvosk)";
                    return;
                }

                var loaded = 0;
                foreach (var dll in RuntimeDlls)
                {
                    var path = Path.Combine(AppDir, dll);
                    if (File.Exists(path) && LoadLibraryW(path) != IntPtr.Zero)
                        loaded++;
                }

                PreloadResult = $"loaded {loaded}/{RuntimeDlls.Length} Vosk runtime DLLs from the application folder";
            }
            catch (Exception ex)
            {
                PreloadResult = "failed: " + ex.Message;
            }
        }

        /// <summary>
        /// Null if libvosk can be loaded safely; otherwise which runtime DLL is already loaded from another folder.
        /// </summary>
        public static string FindConflict()
        {
            try
            {
                var app = Path.GetFullPath(AppDir).TrimEnd('\\');
                foreach (ProcessModule m in Process.GetCurrentProcess().Modules)
                {
                    if (!RuntimeDlls.Any(d => string.Equals(d, m.ModuleName, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    var dir = Path.GetDirectoryName(Path.GetFullPath(m.FileName))?.TrimEnd('\\');
                    if (!string.Equals(dir, app, StringComparison.OrdinalIgnoreCase))
                        return m.ModuleName + " from " + m.FileName;
                }
            }
            catch (Exception)
            {
                // module list unavailable: do not block
            }

            return null;
        }
    }
}
