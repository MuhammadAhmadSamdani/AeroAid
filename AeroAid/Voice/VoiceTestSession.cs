using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using MissionPlanner.Utilities;

namespace MissionPlanner.AeroAid.Voice
{
    /// <summary>What the operator was asked to say in a recognition test.</summary>
    public enum VoiceTestTarget
    {
        /// <summary>Free test: no expected command, results are only listed.</summary>
        Free,
        Arm,
        Disarm,
        Takeoff,
        Stop,
        Hold,
        Rtl,
        Land,
        /// <summary>A negated / non-command sentence ("do not arm the drone"): the correct result is NO command.</summary>
        Reject
    }

    /// <summary>One scored test utterance.</summary>
    public sealed class VoiceTestAttempt
    {
        public DateTime Time;
        public VoiceTestTarget Target;
        public string Text;
        public float Confidence;
        public string Detected;   // e.g. "ARM", "NONE"
        public string Outcome;    // e.g. "ARM COMMAND DETECTED"
        public bool Accepted;
        public bool? Correct;     // null for free tests
        public bool WrongCommand; // a DIFFERENT command was accepted (the dangerous error)
        public string Engine;
    }

    /// <summary>
    /// Voice recognition test session (TEST MODE). Scores every utterance against the command the operator was asked
    /// to say and keeps per-command accuracy. Nothing here can reach the vehicle. Thread-safe.
    /// </summary>
    public sealed class VoiceTestSession
    {
        public static VoiceTestSession Instance { get; } = new VoiceTestSession();

        private readonly object sync = new object();
        private readonly List<VoiceTestAttempt> attempts = new List<VoiceTestAttempt>();
        private readonly DateTime started = DateTime.Now;

        public VoiceTestTarget Target { get; private set; } = VoiceTestTarget.Free;

        /// <summary>Raised after every scored attempt (any thread).</summary>
        public event Action<VoiceTestAttempt> AttemptRecorded;

        public static VoiceCommand CommandOf(VoiceTestTarget t)
        {
            switch (t)
            {
                case VoiceTestTarget.Arm: return VoiceCommand.Arm;
                case VoiceTestTarget.Disarm: return VoiceCommand.Disarm;
                case VoiceTestTarget.Takeoff: return VoiceCommand.Takeoff;
                case VoiceTestTarget.Stop: return VoiceCommand.Stop;
                case VoiceTestTarget.Hold: return VoiceCommand.Hold;
                case VoiceTestTarget.Rtl: return VoiceCommand.Rtl;
                case VoiceTestTarget.Land: return VoiceCommand.Land;
                default: return VoiceCommand.None;
            }
        }

        public static string Label(VoiceTestTarget t) =>
            t == VoiceTestTarget.Reject ? "REJECT" : t == VoiceTestTarget.Free ? "FREE" : VoiceCommandParser.Name(CommandOf(t));

        /// <summary>What the operator should say for a target.</summary>
        public static string Prompt(VoiceTestTarget t)
        {
            switch (t)
            {
                case VoiceTestTarget.Free: return "Say anything - results are listed but not scored.";
                case VoiceTestTarget.Reject: return "Say a NEGATED command, e.g. \"do not arm the drone\" - correct result: rejected.";
                default:
                    var c = CommandOf(t);
                    return $"Say \"{VoiceCommandParser.Example(c)}\" (or another {VoiceCommandParser.Name(c)} phrase) - repeat as often as you like.";
            }
        }

        public void SetTarget(VoiceTestTarget t)
        {
            lock (sync) Target = t;
        }

        public IReadOnlyList<VoiceTestAttempt> Attempts
        {
            get
            {
                lock (sync) return attempts.ToList();
            }
        }

        public void Clear()
        {
            lock (sync) attempts.Clear();
        }

        /// <summary>Scores one utterance against the current target (call only in TEST MODE).</summary>
        public VoiceTestAttempt Record(VoiceUtterance u)
        {
            if (u == null) return null;
            VoiceTestAttempt a;
            lock (sync)
            {
                var detected = u.AcceptedCommand;
                var expected = CommandOf(Target);
                a = new VoiceTestAttempt
                {
                    Time = DateTime.Now,
                    Target = Target,
                    Text = u.Text,
                    Confidence = u.Confidence,
                    Detected = detected == VoiceCommand.None ? "NONE" : VoiceCommandParser.Name(detected),
                    Outcome = u.Match?.IsCommand == true && !u.ConfidenceOk ? "LOW CONFIDENCE" : u.Match?.Headline ?? "",
                    Accepted = detected != VoiceCommand.None,
                    Engine = u.Source
                };
                if (Target == VoiceTestTarget.Reject)
                {
                    a.Correct = detected == VoiceCommand.None;
                    a.WrongCommand = detected != VoiceCommand.None;
                }
                else if (Target != VoiceTestTarget.Free)
                {
                    a.Correct = detected == expected;
                    a.WrongCommand = detected != VoiceCommand.None && detected != expected;
                }

                attempts.Add(a);
            }

            VoiceLog.Write($"TEST target={Label(a.Target)} heard=\"{a.Text}\" conf={a.Confidence:0.00} detected={a.Detected} " +
                           (a.Correct == null ? "" : a.Correct.Value ? "CORRECT" : a.WrongCommand ? "WRONG COMMAND" : "MISSED"));
            try
            {
                AttemptRecorded?.Invoke(a);
            }
            catch (Exception)
            {
                // UI handler problems must not break recognition
            }

            return a;
        }

        /// <summary>Per-target statistics: (attempts, correct, wrong command, missed / rejected).</summary>
        public Dictionary<VoiceTestTarget, (int n, int correct, int wrong, int missed)> Stats()
        {
            lock (sync)
            {
                return attempts.Where(a => a.Correct != null).GroupBy(a => a.Target).ToDictionary(g => g.Key, g =>
                    (g.Count(), g.Count(a => a.Correct == true), g.Count(a => a.WrongCommand),
                        g.Count(a => a.Correct == false && !a.WrongCommand)));
            }
        }

        public static string ReportsDirectory =>
            Path.Combine(Settings.GetUserDataDirectory(), "AeroAid", "voice-tests");

        /// <summary>Writes a markdown accuracy report and a CSV of every attempt. Returns the report path.</summary>
        public string SaveReport(string microphone, string engine)
        {
            Directory.CreateDirectory(ReportsDirectory);
            var stamp = started.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            var md = Path.Combine(ReportsDirectory, $"voice_accuracy_{stamp}.md");
            var csv = Path.Combine(ReportsDirectory, $"voice_accuracy_{stamp}.csv");
            List<VoiceTestAttempt> all;
            lock (sync) all = attempts.ToList();
            var stats = Stats();

            var sb = new StringBuilder();
            sb.AppendLine("# Voice command recognition - live microphone accuracy report");
            sb.AppendLine();
            sb.AppendLine($"- Date: {DateTime.Now:yyyy-MM-dd HH:mm}");
            sb.AppendLine($"- Microphone: {microphone}");
            sb.AppendLine($"- Recogniser: {engine}");
            sb.AppendLine($"- Confidence threshold: {VoiceSettings.MinConfidence:P0}");
            sb.AppendLine("- Mode: TEST MODE (nothing sent to the vehicle)");
            sb.AppendLine();
            sb.AppendLine("| Command | Attempts | Correct | Accuracy | Wrong command | Missed / asked to repeat |");
            sb.AppendLine("|---|---|---|---|---|---|");
            int tn = 0, tc = 0, tw = 0;
            foreach (var t in Enum.GetValues(typeof(VoiceTestTarget)).Cast<VoiceTestTarget>().Where(stats.ContainsKey))
            {
                var s = stats[t];
                tn += s.n;
                tc += s.correct;
                tw += s.wrong;
                sb.AppendLine($"| {Label(t)} | {s.n} | {s.correct} | {Pct(s.correct, s.n)} | {s.wrong} | {s.missed} |");
            }

            sb.AppendLine($"| **All** | **{tn}** | **{tc}** | **{Pct(tc, tn)}** | **{tw}** | **{tn - tc - tw}** |");
            sb.AppendLine();
            sb.AppendLine("## Every attempt");
            sb.AppendLine();
            sb.AppendLine("| Time | Asked | Heard | Confidence | Detected | Result |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var a in all)
                sb.AppendLine($"| {a.Time:HH:mm:ss} | {Label(a.Target)} | \"{a.Text.Replace("|", "/")}\" | {a.Confidence:P0} | {a.Detected} | " +
                              (a.Correct == null ? "-" : a.Correct.Value ? "correct" : a.WrongCommand ? "**WRONG COMMAND**" : "missed") + " |");
            File.WriteAllText(md, sb.ToString());

            var c = new StringBuilder("time,asked,heard,confidence,detected,outcome,correct,wrong_command,engine\n");
            foreach (var a in all)
                c.AppendLine(string.Join(",", a.Time.ToString("s"), Label(a.Target), Csv(a.Text),
                    a.Confidence.ToString("0.000", CultureInfo.InvariantCulture), a.Detected, Csv(a.Outcome),
                    a.Correct?.ToString() ?? "", a.WrongCommand, Csv(a.Engine)));
            File.WriteAllText(csv, c.ToString());
            return md;
        }

        private static string Csv(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";

        private static string Pct(int a, int b) => b == 0 ? "-" : (100.0 * a / b).ToString("0.0", CultureInfo.InvariantCulture) + " %";
    }
}
