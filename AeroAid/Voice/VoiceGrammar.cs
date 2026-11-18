using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MissionPlanner.AeroAid.Voice
{
    /// <summary>
    /// The closed vocabulary the speech recogniser is restricted to (Vosk "grammar" phrase list).
    ///
    /// Restricting the recogniser to the command vocabulary is what makes short accented commands reliable: with an
    /// open vocabulary a non-US "arm" is decoded as "on", "I'm" or "ah"; with this list the decoder can only choose
    /// between the command words, the polite/negation words around them, a few DECOY words and [unk] (anything else).
    /// The decoys are real words that sound like commands but are not ("alarm", "harm", "farm", "hand"...): they give
    /// such speech somewhere to go other than a command. [unk] absorbs everything outside the vocabulary.
    /// The recogniser may output any sequence of these entries; <see cref="VoiceCommandParser"/> then decides whether
    /// the sequence is a well-formed command.
    /// </summary>
    public static class VoiceGrammar
    {
        public const string Unknown = "[unk]";

        /// <summary>
        /// Common words that are NOT commands, so that ordinary speech has somewhere to go other than a command.
        /// Deliberately NOT included: near-homophones of the commands ("harm", "lend", "stock", "cold", "on"). The
        /// benchmark showed that they steal accented commands ("arm" → "harm" for Indian and South-African voices,
        /// "land" → "lend") - see VOICE_RECOGNITION_ENGINE_COMPARISON.md. Those words are handled by the confidence
        /// gate, the sentence grammar and the confirmation dialog instead.
        /// </summary>
        public static readonly string[] Decoys =
        {
            "alarm", "alarms", "hurts", "hurt", "harmless",
            "hello", "hi", "yeah", "thanks", "thank you", "sorry", "what", "where", "time", "weather", "today", "nice",
            "camera", "photo", "picture", "record", "video", "battery", "status", "speed", "altitude", "mission",
            "loiter", "guided", "auto", "brake", "follow", "me", "with", "for", "if", "you", "can", "go", "up", "down",
            "forward", "back", "map", "zoom", "telemetry", "connect", "check", "test", "testing", "one", "two", "three"
        };

        /// <summary>Phrase list in recogniser order: command phrases, context words, decoys, then [unk].</summary>
        public static List<string> Phrases(bool decoys = true)
        {
            var list = new List<string>();
            void Add(string s)
            {
                if (!string.IsNullOrWhiteSpace(s) && !list.Contains(s)) list.Add(s);
            }

            foreach (var p in VoiceCommandParser.Phrases) Add(p.Text);
            foreach (var w in VoiceCommandParser.LeadWords.OrderBy(w => w)) Add(w);
            foreach (var w in VoiceCommandParser.TailWords.OrderBy(w => w)) Add(w);
            foreach (var w in VoiceCommandParser.NegationWords.Where(w => w != "dont")) Add(w);
            Add("do");
            Add("don't");
            foreach (var w in VoiceCommandParser.QuestionWords) Add(w);
            if (decoys)
                foreach (var d in Decoys) Add(d);
            Add(Unknown);
            return list;
        }

        /// <summary>The phrase list as the JSON array Vosk expects.</summary>
        public static string ToJson(IEnumerable<string> phrases)
        {
            var sb = new StringBuilder("[");
            var first = true;
            foreach (var p in phrases)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append('"').Append(p.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
            }

            return sb.Append(']').ToString();
        }
    }
}
