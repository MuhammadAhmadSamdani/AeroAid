using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MissionPlanner.AeroAid.Voice
{
    /// <summary>Flight commands (intents) that can be started by voice.</summary>
    public enum VoiceCommand
    {
        None,
        Arm,
        Disarm,
        Takeoff,
        /// <summary>Stop moving now (BRAKE, or hold position when BRAKE is not available).</summary>
        Stop,
        /// <summary>Hold the current position (GUIDED zero velocity).</summary>
        Hold,
        /// <summary>Return to launch (RTL mode).</summary>
        Rtl,
        /// <summary>Land at the current position (LAND mode).</summary>
        Land
    }

    /// <summary>Why a recognised sentence did or did not produce a command.</summary>
    public enum VoiceMatchOutcome
    {
        /// <summary>Exactly one well-formed command, no negation: may start the confirmation workflow.</summary>
        Command,
        /// <summary>No command word in the sentence.</summary>
        NoCommand,
        /// <summary>A command word together with a negation ("do not arm") - never executed.</summary>
        Negated,
        /// <summary>Two or more different commands ("disarm and arm") - never executed.</summary>
        Ambiguous,
        /// <summary>A question ("is the drone armed") - never executed.</summary>
        Question,
        /// <summary>
        /// A command word inside a sentence that is not a command ("my arm hurts"), or too long - never executed;
        /// the operator is asked to repeat.
        /// </summary>
        TooLong,
        /// <summary>A well-formed command without the required wake word ("AeroAid") - never executed.</summary>
        PrefixMissing
    }

    /// <summary>When the wake word ("AeroAid") must precede a command.</summary>
    public enum VoicePrefixMode
    {
        /// <summary>Accepted and stripped when spoken, never required.</summary>
        Optional,
        /// <summary>Required for ARM, DISARM and TAKEOFF; optional for STOP / HOLD / RTL / LAND.</summary>
        Critical,
        /// <summary>Required for every command.</summary>
        All
    }

    /// <summary>Settings-dependent parser options (the parser itself stays a pure function).</summary>
    public sealed class VoiceParseOptions
    {
        public const string DefaultWakePhrase = "aero aid";

        public VoiceParseOptions(string wakePhrase = DefaultWakePhrase, VoicePrefixMode mode = VoicePrefixMode.Optional)
        {
            var n = VoiceCommandParser.Normalize(wakePhrase);
            WakeTokens = string.IsNullOrEmpty(n) ? Array.Empty<string>() : n.Split(' ');
            Mode = WakeTokens.Length == 0 ? VoicePrefixMode.Optional : mode;
        }

        /// <summary>Normalised wake-word tokens, e.g. ["aero", "aid"]; empty = no wake word.</summary>
        public string[] WakeTokens { get; }

        public VoicePrefixMode Mode { get; }

        public static readonly VoiceParseOptions Default = new VoiceParseOptions();

        /// <summary>True if <paramref name="c"/> needs the wake word in this mode.</summary>
        public bool Requires(VoiceCommand c) =>
            Mode == VoicePrefixMode.All ||
            (Mode == VoicePrefixMode.Critical && (c == VoiceCommand.Arm || c == VoiceCommand.Disarm || c == VoiceCommand.Takeoff));
    }

    /// <summary>Result of classifying one recognised sentence. Immutable.</summary>
    public sealed class VoiceMatch
    {
        public VoiceMatch(VoiceMatchOutcome outcome, VoiceCommand command, string normalized, string keyword,
            string reason, IReadOnlyList<VoiceCommand> found, bool hasPrefix = false, double? altitude = null)
        {
            Outcome = outcome;
            Command = outcome == VoiceMatchOutcome.Command ? command : VoiceCommand.None;
            Normalized = normalized ?? "";
            Keyword = keyword ?? "";
            Reason = reason ?? "";
            Found = found ?? Array.Empty<VoiceCommand>();
            HasPrefix = hasPrefix;
            Altitude = altitude;
        }

        /// <summary>The sentence started with the wake word ("AeroAid ...").</summary>
        public bool HasPrefix { get; }

        /// <summary>Takeoff altitude spoken with a TAKEOFF command ("take off five meters"), metres; null = not spoken.</summary>
        public double? Altitude { get; }

        public VoiceMatchOutcome Outcome { get; }

        /// <summary>The command to run; <see cref="VoiceCommand.None"/> unless <see cref="Outcome"/> is Command.</summary>
        public VoiceCommand Command { get; }

        public string Normalized { get; }

        /// <summary>The command phrase as heard, e.g. "take off", "return to launch".</summary>
        public string Keyword { get; }

        public string Reason { get; }

        /// <summary>Every distinct command found in the sentence (in spoken order).</summary>
        public IReadOnlyList<VoiceCommand> Found { get; }

        public bool IsCommand => Outcome == VoiceMatchOutcome.Command;

        /// <summary>Short banner for the UI, e.g. "ARM COMMAND DETECTED".</summary>
        public string Headline
        {
            get
            {
                switch (Outcome)
                {
                    case VoiceMatchOutcome.Command: return VoiceCommandParser.Name(Command) + " COMMAND DETECTED";
                    case VoiceMatchOutcome.Negated: return "NEGATED " + VoiceCommandParser.Name(Found.FirstOrDefault()) + " - REJECTED";
                    case VoiceMatchOutcome.Ambiguous: return "AMBIGUOUS - NO ACTION";
                    case VoiceMatchOutcome.Question: return "QUESTION - NO ACTION";
                    case VoiceMatchOutcome.TooLong: return "UNCLEAR - PLEASE REPEAT";
                    case VoiceMatchOutcome.PrefixMissing: return "WAKE WORD MISSING - NO ACTION";
                    default: return "NO COMMAND DETECTED";
                }
            }
        }

        public override string ToString() => Headline + (Reason.Length > 0 ? " (" + Reason + ")" : "");
    }

    /// <summary>
    /// Classifies a recognised sentence into at most one flight command. Pure functions: no speech engine, no vehicle.
    ///
    /// This is a small phrase grammar, not a keyword search:
    ///   sentence := LEAD* COMMAND-PHRASE TAIL*
    /// where COMMAND-PHRASE is one of the phrases in <see cref="Phrases"/> (matched on whole word TOKENS), LEAD words
    /// are politeness / addressing words ("please", "drone", "I want to", "go ahead and") and TAIL words name the
    /// vehicle or the moment ("the drone", "now", "immediately"). Anything else around a command phrase makes the
    /// sentence UNCLEAR ("my arm hurts" is not ARM). Before the grammar is applied:
    ///   - a negation anywhere ("not", "don't", "never", "cancel", "abort"...) rejects the sentence;
    ///   - two different commands ("disarm and arm") reject it as ambiguous;
    ///   - a question ("is it armed") rejects it.
    /// Tokens are compared whole, so "disarm" can never produce ARM and "alarm", "harm", "army" never match anything.
    /// </summary>
    public static class VoiceCommandParser
    {
        /// <summary>Sentences with more words than this are rejected as unclear.</summary>
        public const int MaxWords = 14;

        /// <summary>One spoken form of a command: its tokens (after <see cref="Normalize"/>) and its intent.</summary>
        public sealed class Phrase
        {
            public Phrase(VoiceCommand command, string text)
            {
                Command = command;
                Text = text;
                Tokens = text.Split(' ');
            }

            public VoiceCommand Command { get; }
            public string Text { get; }
            public string[] Tokens { get; }
        }

        /// <summary>
        /// Every accepted command phrase. Longer phrases are matched first ("hold position" before "hold").
        /// These are also the phrases the speech recognisers are restricted to.
        /// </summary>
        public static readonly Phrase[] Phrases = new[]
        {
            // ONE spoken form per command word: alternatives with the same meaning ("arm" / "armed") split the
            // recogniser's confidence between them and made correct commands fail the confidence gate
            new Phrase(VoiceCommand.Disarm, "disarm"),
            new Phrase(VoiceCommand.Arm, "arm"),
            new Phrase(VoiceCommand.Takeoff, "take off"),
            new Phrase(VoiceCommand.Stop, "stop"),
            new Phrase(VoiceCommand.Hold, "hold position"),
            new Phrase(VoiceCommand.Hold, "hold the position"),
            new Phrase(VoiceCommand.Hold, "hold your position"),
            new Phrase(VoiceCommand.Hold, "hold"),
            new Phrase(VoiceCommand.Rtl, "return to launch"),
            new Phrase(VoiceCommand.Rtl, "return to home"),
            new Phrase(VoiceCommand.Rtl, "return home"),
            new Phrase(VoiceCommand.Rtl, "come home"),
            new Phrase(VoiceCommand.Rtl, "go home"),
            new Phrase(VoiceCommand.Rtl, "rtl"),
            new Phrase(VoiceCommand.Rtl, "r t l"),
            new Phrase(VoiceCommand.Land, "land")
        }.OrderByDescending(p => p.Tokens.Length).ToArray();

        /// <summary>Words that cancel the command of the whole sentence (and of a command spoken right after it).</summary>
        public static readonly string[] NegationWords =
        {
            "not", "no", "never", "dont", "cancel", "abort", "without", "neither", "nor", "avoid", "negative", "wait"
        };

        /// <summary>A sentence starting with one of these (after "drone", "hey"...) is treated as a question.</summary>
        public static readonly string[] QuestionWords =
        {
            "is", "are", "was", "were", "did", "does", "has", "have", "had", "why", "what", "when", "where", "how", "who",
            "which", "whether"
        };

        /// <summary>Words allowed BEFORE the command phrase (addressing, politeness, "I want (the drone) to").</summary>
        public static readonly HashSet<string> LeadWords = new HashSet<string>
        {
            "please", "kindly", "drone", "hey", "ok", "okay", "alright", "so", "um", "uh", "er", "now", "go", "ahead", "and",
            "can", "could", "would", "will", "you", "i", "we", "want", "wanted", "to", "like", "lets", "let", "us", "need",
            "the", "a", "initiate", "start", "begin", "perform", "execute", "just", "right", "should", "then", "yes",
            "vehicle", "copter", "quadcopter", "aircraft", "it", "be", "immediately", "commence"
        };

        /// <summary>Words allowed AFTER the command phrase (the object, the moment, politeness).</summary>
        public static readonly HashSet<string> TailWords = new HashSet<string>
        {
            "the", "drone", "my", "our", "your", "motors", "motor", "vehicle", "copter", "quadcopter", "aircraft", "it",
            "now", "please", "immediately", "right", "away", "here", "okay", "ok"
        };

        /// <summary>Leading words skipped before checking for a question ("drone, is it armed").</summary>
        private static readonly HashSet<string> AddressWords = new HashSet<string>
        {
            "drone", "hey", "ok", "okay", "so", "um", "uh", "er", "and", "well"
        };

        /// <summary>Number words for a spoken takeoff altitude (1..99 m).</summary>
        public static readonly Dictionary<string, int> NumberUnits = new Dictionary<string, int>
        {
            { "one", 1 }, { "two", 2 }, { "three", 3 }, { "four", 4 }, { "five", 5 }, { "six", 6 }, { "seven", 7 },
            { "eight", 8 }, { "nine", 9 }, { "ten", 10 }, { "eleven", 11 }, { "twelve", 12 }, { "thirteen", 13 },
            { "fourteen", 14 }, { "fifteen", 15 }, { "sixteen", 16 }, { "seventeen", 17 }, { "eighteen", 18 },
            { "nineteen", 19 }
        };

        public static readonly Dictionary<string, int> NumberTens = new Dictionary<string, int>
        {
            { "twenty", 20 }, { "thirty", 30 }, { "forty", 40 }, { "fifty", 50 }, { "sixty", 60 }, { "seventy", 70 },
            { "eighty", 80 }, { "ninety", 90 }
        };

        /// <summary>Unit words accepted after a takeoff altitude.</summary>
        public static readonly string[] AltitudeUnits = { "meters", "metres", "meter", "metre", "m" };

        /// <summary>Words allowed between "take off" and the altitude ("take off TO five meters").</summary>
        public static readonly string[] AltitudeLinkWords = { "to", "at" };

        /// <summary>Every word the speech recognisers need: command phrases, lead / tail words and negations.</summary>
        public static IEnumerable<string> Vocabulary =>
            Phrases.SelectMany(p => p.Tokens).Concat(LeadWords).Concat(TailWords).Concat(NegationWords)
                .Concat(new[] { "do", "don't", "is", "are", "did", "what" }).Distinct();

        /// <summary>Spoken phrases for the System.Speech command grammar (kept for that engine).</summary>
        public static readonly string[] GrammarKeywords = Phrases.Select(p => p.Text).Where(t => t != "r t l").ToArray();

        /// <summary>Words the System.Speech grammar accepts around a command (includes negations and question words).</summary>
        public static string[] GrammarFillers =>
            LeadWords.Concat(TailWords).Concat(NegationWords).Concat(new[] { "do", "don't", "is", "are", "did", "what" })
                .Where(w => w != "dont").Distinct().ToArray();

        private static readonly Dictionary<string, string> Contractions = new Dictionary<string, string>
        {
            { "can't", "can not" }, { "cant", "can not" }, { "cannot", "can not" },
            { "won't", "will not" }, { "wont", "will not" },
            { "shan't", "shall not" },
            { "don't", "do not" }, { "dont", "do not" },
            { "doesn't", "does not" }, { "doesnt", "does not" },
            { "didn't", "did not" }, { "didnt", "did not" },
            { "shouldn't", "should not" }, { "shouldnt", "should not" },
            { "wouldn't", "would not" }, { "wouldnt", "would not" },
            { "couldn't", "could not" }, { "couldnt", "could not" },
            { "mustn't", "must not" }, { "mustnt", "must not" },
            { "isn't", "is not" }, { "isnt", "is not" },
            { "aren't", "are not" }, { "arent", "are not" },
            { "wasn't", "was not" }, { "wasnt", "was not" },
            { "weren't", "were not" }, { "werent", "were not" },
            { "haven't", "have not" }, { "hasn't", "has not" },
            { "let's", "lets" }
        };

        private static readonly Regex NonWord = new Regex(@"[^a-z0-9' ]+", RegexOptions.Compiled);
        private static readonly Regex Spaces = new Regex(@"\s+", RegexOptions.Compiled);

        public static string Name(VoiceCommand c)
        {
            switch (c)
            {
                case VoiceCommand.Arm: return "ARM";
                case VoiceCommand.Disarm: return "DISARM";
                case VoiceCommand.Takeoff: return "TAKEOFF";
                case VoiceCommand.Stop: return "STOP";
                case VoiceCommand.Hold: return "HOLD";
                case VoiceCommand.Rtl: return "RTL";
                case VoiceCommand.Land: return "LAND";
                default: return "NO";
            }
        }

        /// <summary>Spoken example for each command (Voice page test buttons, help text).</summary>
        public static string Example(VoiceCommand c)
        {
            switch (c)
            {
                case VoiceCommand.Arm: return "arm the drone";
                case VoiceCommand.Disarm: return "disarm the drone";
                case VoiceCommand.Takeoff: return "take off";
                case VoiceCommand.Stop: return "stop";
                case VoiceCommand.Hold: return "hold position";
                case VoiceCommand.Rtl: return "return to launch";
                case VoiceCommand.Land: return "land";
                default: return "";
            }
        }

        public static readonly VoiceCommand[] AllCommands =
        {
            VoiceCommand.Arm, VoiceCommand.Disarm, VoiceCommand.Takeoff, VoiceCommand.Stop, VoiceCommand.Hold,
            VoiceCommand.Rtl, VoiceCommand.Land
        };

        /// <summary>
        /// Lower-case, expand contractions (so "don't" becomes "do not"), turn punctuation and hyphens into spaces,
        /// join split recogniser forms ("take-off"/"takeoff" become "take off", "dis arm" becomes "disarm",
        /// "R.T.L." becomes "r t l") and collapse whitespace.
        /// </summary>
        public static string Normalize(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";
            var s = text.ToLowerInvariant().Replace('’', '\'').Replace('‘', '\'').Replace('`', '\'');
            s = NonWord.Replace(s, " ");
            var words = new List<string>();
            foreach (var raw in s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var w = raw.Trim('\'');
                if (w.Length == 0)
                    continue;
                if (Contractions.TryGetValue(w, out var expanded))
                {
                    words.AddRange(expanded.Split(' '));
                    continue;
                }

                if (w.EndsWith("n't"))
                {
                    words.Add(w.Substring(0, w.Length - 3));
                    words.Add("not");
                    continue;
                }

                w = w.Replace("'", "");
                switch (w)
                {
                    case "takeoff":
                    case "takeoffs":
                        words.Add("take");
                        words.Add("off");
                        continue;
                    case "liftoff":
                        words.Add("take");
                        words.Add("off");
                        continue;
                }

                words.Add(w);
            }

            // "dis arm" / "dis-arm" -> "disarm"
            for (var i = 0; i + 1 < words.Count; i++)
            {
                if (words[i] == "dis" && (words[i + 1] == "arm" || words[i + 1] == "armed" || words[i + 1] == "arming"))
                {
                    words[i] = "dis" + words[i + 1];
                    words.RemoveAt(i + 1);
                }
            }

            return Spaces.Replace(string.Join(" ", words), " ").Trim();
        }

        public static string[] Tokens(string normalized) =>
            string.IsNullOrEmpty(normalized) ? Array.Empty<string>() : normalized.Split(' ');

        /// <summary>
        /// Words that must never be discarded as background speech: any token of a command phrase, negations
        /// (incl. "do" of "do not") and question words.
        /// </summary>
        public static bool IsProtectedWord(string normalizedWord)
        {
            if (string.IsNullOrEmpty(normalizedWord)) return false;
            foreach (var t in Tokens(normalizedWord))
                if (NegationWords.Contains(t) || QuestionWords.Contains(t) || t == "do" ||
                    Phrases.Any(p => p.Tokens.Contains(t)))
                    return true;
            return false;
        }

        /// <summary>True if the (normalised) sentence contains a negation word.</summary>
        public static bool ContainsNegation(string normalized) => Tokens(normalized).Any(NegationWords.Contains);

        /// <summary>All command phrases in the token list (non-overlapping, longest first, in spoken order).</summary>
        public static List<(int start, Phrase phrase)> FindPhrases(string[] tokens)
        {
            var found = new List<(int, Phrase)>();
            for (var i = 0; i < tokens.Length;)
            {
                var hit = Phrases.FirstOrDefault(p => i + p.Tokens.Length <= tokens.Length &&
                                                     !p.Tokens.Where((t, k) => tokens[i + k] != t).Any());
                if (hit != null)
                {
                    found.Add((i, hit));
                    i += hit.Tokens.Length;
                }
                else
                {
                    i++;
                }
            }

            return found;
        }

        /// <summary>
        /// Reads a takeoff altitude at <paramref name="i"/>: [to|at] NUMBER [meters]. NUMBER is a digit string or
        /// "five", "fifteen", "twenty", "twenty five" (1..99). Returns false (and leaves <paramref name="end"/> = i)
        /// when there is no number there; a link word or unit without a valid number is NOT consumed, so the sentence
        /// grammar then rejects it as unclear rather than guessing.
        /// </summary>
        public static bool TryReadAltitude(string[] tokens, int i, out double altitude, out int end)
        {
            altitude = 0;
            end = i;
            var p = i;
            if (p < tokens.Length && AltitudeLinkWords.Contains(tokens[p])) p++;
            if (p >= tokens.Length) return false;

            int value;
            if (int.TryParse(tokens[p], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out value))
            {
                p++;
            }
            else if (NumberTens.TryGetValue(tokens[p], out value))
            {
                p++;
                if (p < tokens.Length && NumberUnits.TryGetValue(tokens[p], out var u) && u < 10)
                {
                    value += u;
                    p++;
                }
            }
            else if (NumberUnits.TryGetValue(tokens[p], out value))
            {
                p++;
            }
            else
            {
                return false;
            }

            // "two five meters": two numbers in a row is not a number we understand
            if (p < tokens.Length && (NumberUnits.ContainsKey(tokens[p]) || NumberTens.ContainsKey(tokens[p]) || tokens[p].All(char.IsDigit)))
                return false;
            if (value < 1 || value > 99) return false;
            if (p < tokens.Length && AltitudeUnits.Contains(tokens[p])) p++;
            altitude = value;
            end = p;
            return true;
        }

        /// <summary>Address words that may precede the wake word ("hey AeroAid ...").</summary>
        private static readonly HashSet<string> PreWakeWords = new HashSet<string> { "hey", "ok", "okay", "so", "um", "uh", "er" };

        /// <summary>Removes a leading wake word ("hey AeroAid, arm" → "arm"); true if one was found.</summary>
        public static string[] StripWakeWord(string[] tokens, VoiceParseOptions options, out bool found)
        {
            found = false;
            var wake = options?.WakeTokens ?? Array.Empty<string>();
            if (wake.Length == 0) return tokens;
            var p = 0;
            while (p < tokens.Length && PreWakeWords.Contains(tokens[p])) p++;
            int len;
            if (p + wake.Length <= tokens.Length && !wake.Where((w, k) => tokens[p + k] != w).Any())
                len = wake.Length;
            else if (wake.Length > 1 && p < tokens.Length && tokens[p] == string.Concat(wake))
                len = 1; // "aeroaid" written as one word
            else
                return tokens;
            found = true;
            return tokens.Take(p).Concat(tokens.Skip(p + len)).ToArray();
        }

        /// <summary>Classifies recognised text into at most one command (default options: wake word optional).</summary>
        public static VoiceMatch Parse(string text) => Parse(text, VoiceParseOptions.Default);

        /// <summary>Classifies recognised text into at most one command (see class remarks).</summary>
        public static VoiceMatch Parse(string text, VoiceParseOptions options)
        {
            options = options ?? VoiceParseOptions.Default;
            var normalized = Normalize(text);
            var tokens = StripWakeWord(Tokens(normalized), options, out var hasPrefix);
            if (tokens.Length == 0)
                return new VoiceMatch(VoiceMatchOutcome.NoCommand, VoiceCommand.None, normalized, "",
                    hasPrefix ? "only the wake word was heard" : "nothing recognised", null, hasPrefix);

            var hits = FindPhrases(tokens);
            var distinct = hits.Select(h => h.phrase.Command).Distinct().ToList();
            if (distinct.Count == 0)
                return new VoiceMatch(VoiceMatchOutcome.NoCommand, VoiceCommand.None, normalized, "", "no command phrase", distinct, hasPrefix);

            var first = hits[0];
            var keyword = first.phrase.Text;

            if (distinct.Count > 1)
                return new VoiceMatch(VoiceMatchOutcome.Ambiguous, VoiceCommand.None, normalized, keyword,
                    "conflicting commands: " + string.Join(" + ", distinct.Select(Name)) + " - say one command at a time", distinct, hasPrefix);

            var negation = tokens.FirstOrDefault(NegationWords.Contains);
            if (negation != null)
                return new VoiceMatch(VoiceMatchOutcome.Negated, VoiceCommand.None, normalized, keyword,
                    "negation \"" + negation + "\" in the sentence", distinct, hasPrefix);

            var lead = tokens.SkipWhile(AddressWords.Contains).FirstOrDefault();
            if (lead != null && QuestionWords.Contains(lead))
                return new VoiceMatch(VoiceMatchOutcome.Question, VoiceCommand.None, normalized, keyword,
                    "sounds like a question (\"" + lead + " ...\")", distinct, hasPrefix);

            if (tokens.Length > MaxWords)
                return new VoiceMatch(VoiceMatchOutcome.TooLong, VoiceCommand.None, normalized, keyword,
                    $"{tokens.Length} words - say a short command", distinct, hasPrefix);

            // the SAME phrase said twice in a row ("arm arm", "take off take off" - typically the operator repeating
            // a command that seemed not to be heard) is one command. Repeats separated by other words are unclear.
            var start = first.start;
            var end = start + first.phrase.Tokens.Length;
            for (var k = 1; k < hits.Count; k++)
            {
                if (hits[k].start != end || hits[k].phrase != first.phrase)
                    return new VoiceMatch(VoiceMatchOutcome.TooLong, VoiceCommand.None, normalized, keyword,
                        "command repeated in one sentence - say it once", distinct, hasPrefix);
                end = hits[k].start + hits[k].phrase.Tokens.Length;
            }

            // "take off [to] five [meters]"
            double? altitude = null;
            if (first.phrase.Command == VoiceCommand.Takeoff && TryReadAltitude(tokens, end, out var alt, out var altEnd))
            {
                altitude = alt;
                end = altEnd;
            }

            // sentence := LEAD* PHRASE [ALTITUDE] TAIL*
            var badLead = tokens.Take(start).FirstOrDefault(w => !LeadWords.Contains(w));
            var badTail = tokens.Skip(end).FirstOrDefault(w => !TailWords.Contains(w));
            if (badLead != null || badTail != null)
                return new VoiceMatch(VoiceMatchOutcome.TooLong, VoiceCommand.None, normalized, keyword,
                    $"\"{keyword}\" is not used as a command here (unexpected word \"{badLead ?? badTail}\") - please repeat", distinct, hasPrefix);

            if (!hasPrefix && options.Requires(first.phrase.Command))
                return new VoiceMatch(VoiceMatchOutcome.PrefixMissing, VoiceCommand.None, normalized, keyword,
                    $"{Name(first.phrase.Command)} needs the wake word - say \"{string.Join(" ", options.WakeTokens)} {keyword}\"",
                    distinct, false, altitude);

            return new VoiceMatch(VoiceMatchOutcome.Command, first.phrase.Command, normalized, keyword,
                "phrase \"" + keyword + "\"" + (altitude.HasValue ? $", altitude {altitude:0.#} m" : "") + (hasPrefix ? ", wake word" : ""),
                distinct, hasPrefix, altitude);
        }
    }
}
