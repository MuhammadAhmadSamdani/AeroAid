using System;
using System.IO;
using System.Reflection;
using MissionPlanner.Utilities;

namespace MissionPlanner.AeroAid.Tests
{
    /// <summary>
    /// Isolates tests from the user's real Mission Planner configuration: settings, models and detections are
    /// redirected to a temporary directory before the settings singleton is (re)created.
    /// </summary>
    internal static class TestEnv
    {
        private static readonly object sync = new object();
        public static string Root { get; private set; }

        public static void Init()
        {
            lock (sync)
            {
                if (Root != null) return;
                Root = Path.Combine(Path.GetTempPath(), "AeroAidTests_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Root);
                Settings.CustomUserDataDirectory = Root;
                // drop any instance that may already point at the real user config
                typeof(Settings).GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, null);
                if (!Settings.GetUserDataDirectory().StartsWith(Root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Test settings are not isolated");
            }
        }

        public static string ConfigFile => Path.Combine(Settings.GetUserDataDirectory(), Settings.FileName);

        public static string Data(string name) =>
            Path.Combine(Path.GetDirectoryName(typeof(TestEnv).Assembly.Location) ?? "", "AeroAid", "TestData", name);
    }
}
