using System;
using System.IO;
using System.Reflection;
using log4net;
using MissionPlanner.Utilities;

namespace MissionPlanner.AeroAid.Voice
{
    /// <summary>
    /// Append-only audit log of recognised speech, detected commands and their results
    /// (aeroaid_voice.log in the Mission Planner user data directory, rotated at 1 MB). Contains no audio.
    /// </summary>
    public static class VoiceLog
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly object sync = new object();
        private const long MaxBytes = 1024 * 1024;

        public static string FilePath => Path.Combine(Settings.GetUserDataDirectory(), "aeroaid_voice.log");

        public static void Write(string line)
        {
            try
            {
                lock (sync)
                {
                    var path = FilePath;
                    var fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > MaxBytes)
                    {
                        var old = path + ".1";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(path, old);
                    }

                    File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " +
                                             (line ?? "").Replace('\r', ' ').Replace('\n', ' ') + Environment.NewLine);
                }
            }
            catch (Exception ex)
            {
                log.Debug("Voice log write failed", ex);
            }
        }
    }
}
