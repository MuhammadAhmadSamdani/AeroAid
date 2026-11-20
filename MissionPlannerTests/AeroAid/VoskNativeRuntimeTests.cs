using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MissionPlanner.AeroAid.Voice;

namespace MissionPlanner.AeroAid.Tests
{
    /// <summary>The test process starts like the application: Vosk's MinGW runtime is preloaded first (Program.Main).</summary>
    [TestClass]
    public static class AeroAidAssemblySetup
    {
        [AssemblyInitialize]
        public static void Init(TestContext _)
        {
            VoskNativeRuntime.Preload();
            Console.WriteLine("Vosk runtime preload: " + VoskNativeRuntime.PreloadResult);
        }
    }

    /// <summary>
    /// Regression test for the native crash "ASSERTION_FAILED (VoskAPI:RunTask():util/kaldi-thread.h:263)": GStreamer's
    /// libwinpthread-1.dll (2018) loaded before libvosk made Kaldi abort the whole process.
    /// </summary>
    [TestClass]
    public class VoskNativeRuntimeTests
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryW(string path);

        [ClassInitialize]
        public static void InitClass(TestContext _) => TestEnv.Init();

        [TestMethod]
        public void VoskRuntimeStaysOwnAfterGStreamerLoads()
        {
            if (!Environment.Is64BitProcess) Assert.Inconclusive("64-bit only");
            StringAssert.StartsWith(VoskNativeRuntime.PreloadResult, "loaded 3/3");

            // load GStreamer's glib the way the video code does (it pulls in its own libwinpthread-1.dll)
            var gst = @"C:\ProgramData\Mission Planner\gstreamer\1.0\x86_64\bin";
            var glib = Path.Combine(gst, "libglib-2.0-0.dll");
            if (File.Exists(glib))
            {
                var old = Environment.GetEnvironmentVariable("PATH");
                Environment.SetEnvironmentVariable("PATH", gst + ";" + old);
                try
                {
                    Assert.AreNotEqual(IntPtr.Zero, LoadLibraryW(glib), "GStreamer glib must still load with Vosk's runtime");
                }
                finally
                {
                    Environment.SetEnvironmentVariable("PATH", old);
                }
            }
            else
            {
                Console.WriteLine("GStreamer not installed - only the module check runs");
            }

            foreach (ProcessModule m in Process.GetCurrentProcess().Modules)
                if (VoskNativeRuntime.RuntimeDlls.Any(d => string.Equals(d, m.ModuleName, StringComparison.OrdinalIgnoreCase)) ||
                    m.ModuleName.StartsWith("libglib", StringComparison.OrdinalIgnoreCase))
                    Console.WriteLine("MODULE " + m.FileName);
            Assert.IsNull(VoskNativeRuntime.FindConflict(), "a foreign MinGW runtime replaced Vosk's");

            // and Vosk actually runs on it
            if (VoiceTestAudio.UseVosk())
            {
                using (var r = VoskCommandRecognizer.Create())
                {
                    r.BeginUtterance();
                    r.Feed(VoiceTestAudio.Hiss(0.5, 3));
                    Assert.IsNotNull(r.EndUtterance());
                }
            }
        }
    }
}
