using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MissionPlanner.AeroAid.Voice;

namespace MissionPlanner.AeroAid.Tests
{
    /// <summary>Command classification of recognised text - independent of the speech engine and of any vehicle.</summary>
    [TestClass]
    public class VoiceCommandParserTests
    {
        private static void Expect(string text, VoiceMatchOutcome outcome, VoiceCommand command = VoiceCommand.None)
        {
            var m = VoiceCommandParser.Parse(text);
            Console.WriteLine($"{text,-40} -> {m}");
            Assert.AreEqual(outcome, m.Outcome, $"\"{text}\": {m}");
            Assert.AreEqual(command, m.Command, $"\"{text}\": {m}");
            Assert.AreEqual(outcome == VoiceMatchOutcome.Command, m.IsCommand);
        }

        // ---- the specification table
        [DataTestMethod]
        [DataRow("Arm the drone", VoiceCommand.Arm)]
        [DataRow("Drone arm", VoiceCommand.Arm)]
        [DataRow("Disarm the drone", VoiceCommand.Disarm)]
        [DataRow("Drone disarm", VoiceCommand.Disarm)]
        [DataRow("Take off", VoiceCommand.Takeoff)]
        [DataRow("Drone take off", VoiceCommand.Takeoff)]
        [DataRow("Stop", VoiceCommand.Stop)]
        [DataRow("Hold position", VoiceCommand.Hold)]
        [DataRow("Return to launch", VoiceCommand.Rtl)]
        [DataRow("Land", VoiceCommand.Land)]
        public void SpecificationTable(string text, VoiceCommand cmd) => Expect(text, VoiceMatchOutcome.Command, cmd);

        // ---- the specification's pipeline examples
        [DataTestMethod]
        [DataRow("Please arm the drone", VoiceMatchOutcome.Command, VoiceCommand.Arm)]
        [DataRow("Please disarm the drone", VoiceMatchOutcome.Command, VoiceCommand.Disarm)]
        [DataRow("Take off now", VoiceMatchOutcome.Command, VoiceCommand.Takeoff)]
        [DataRow("Do not arm the drone", VoiceMatchOutcome.Negated, VoiceCommand.None)]
        [DataRow("Do not disarm the drone", VoiceMatchOutcome.Negated, VoiceCommand.None)]
        [DataRow("Disarm and arm the drone", VoiceMatchOutcome.Ambiguous, VoiceCommand.None)]
        public void SpecificationExamples(string text, VoiceMatchOutcome outcome, VoiceCommand cmd) => Expect(text, outcome, cmd);

        // ---- more ways of saying each command
        [DataTestMethod]
        [DataRow("ARM", VoiceCommand.Arm)]
        [DataRow("  Arm   the    DRONE!!! ", VoiceCommand.Arm)]
        [DataRow("Drone, please arm.", VoiceCommand.Arm)]
        [DataRow("I want to arm my drone.", VoiceCommand.Arm)]
        [DataRow("could you arm the motors", VoiceCommand.Arm)]
        [DataRow("go ahead and arm", VoiceCommand.Arm)]
        [DataRow("dis-arm the drone", VoiceCommand.Disarm)]
        [DataRow("dis arm", VoiceCommand.Disarm)]
        [DataRow("Drone takeoff", VoiceCommand.Takeoff)]
        [DataRow("Please initiate takeoff", VoiceCommand.Takeoff)]
        [DataRow("I want the drone to take off.", VoiceCommand.Takeoff)]
        [DataRow("take-off", VoiceCommand.Takeoff)]
        [DataRow("start takeoff", VoiceCommand.Takeoff)]
        [DataRow("drone stop", VoiceCommand.Stop)]
        [DataRow("stop the drone now", VoiceCommand.Stop)]
        [DataRow("stop immediately", VoiceCommand.Stop)]
        [DataRow("hold", VoiceCommand.Hold)]
        [DataRow("drone hold your position", VoiceCommand.Hold)]
        [DataRow("please return to launch", VoiceCommand.Rtl)]
        [DataRow("return home", VoiceCommand.Rtl)]
        [DataRow("drone come home", VoiceCommand.Rtl)]
        [DataRow("RTL", VoiceCommand.Rtl)]
        [DataRow("R.T.L.", VoiceCommand.Rtl)]
        [DataRow("land now", VoiceCommand.Land)]
        [DataRow("land the drone", VoiceCommand.Land)]
        [DataRow("please land here", VoiceCommand.Land)]
        public void CommandVariants(string text, VoiceCommand cmd) => Expect(text, VoiceMatchOutcome.Command, cmd);

        [DataTestMethod]
        [DataRow("Do not arm")]
        [DataRow("Don't arm the drone")]
        [DataRow("dont arm")]
        [DataRow("never arm the drone")]
        [DataRow("don’t disarm")]
        [DataRow("Do not take off")]
        [DataRow("I do not want to take off")]
        [DataRow("I don't want the drone to take off")]
        [DataRow("cancel takeoff")]
        [DataRow("abort take off")]
        [DataRow("no arm")]
        [DataRow("wait, do not take off yet")]
        [DataRow("you shouldn't arm it")]
        [DataRow("we can't take off")]
        [DataRow("do not stop")]
        [DataRow("don't land")]
        [DataRow("do not return to launch")]
        [DataRow("never hold position")]
        public void NegatedSentencesNeverExecute(string text) => Expect(text, VoiceMatchOutcome.Negated);

        /// <summary>A command word inside a sentence that is not a command: never executed, the operator is asked to repeat.</summary>
        [DataTestMethod]
        [DataRow("my arm hurts")]
        [DataRow("hold on")]
        [DataRow("hold on a second")]
        [DataRow("the stop sign")]
        [DataRow("land of the free")]
        [DataRow("stop arming")]
        [DataRow("arm the drone arm")]
        [DataRow("[unk] arm")]
        [DataRow("arm [unk]")]
        [DataRow("so yesterday we went to the field and after a long time of waiting we decided to arm the drone")]
        public void CommandWordsOutsideACommandAreUnclear(string text) => Expect(text, VoiceMatchOutcome.TooLong);

        [DataTestMethod]
        [DataRow("Random conversation")]
        [DataRow("What is the battery level")]
        [DataRow("hello there how are you")]
        [DataRow("the weather is nice today")]
        [DataRow("the alarm is ringing")]
        [DataRow("that would harm the drone")]
        [DataRow("the army base")]
        [DataRow("a farm field")]
        [DataRow("warm up the motors")]
        [DataRow("charming armchair armor")]
        [DataRow("disarmament talks")]
        [DataRow("take it off the table")]
        [DataRow("off we go")]
        [DataRow("launch")]
        [DataRow("arming")]
        [DataRow("drone armed")]
        [DataRow("is the drone armed")]
        [DataRow("landing gear")]
        [DataRow("")]
        [DataRow("   ")]
        [DataRow((string)null)]
        [DataRow("...!!!")]
        public void UnrelatedSpeechIsNoCommand(string text) => Expect(text, VoiceMatchOutcome.NoCommand);

        [DataTestMethod]
        [DataRow("Disarm and arm")]
        [DataRow("arm and disarm")]
        [DataRow("arm and take off")]
        [DataRow("take off then disarm")]
        [DataRow("disarm arm")]
        [DataRow("land and take off")]
        [DataRow("stop and land")]
        [DataRow("hold position and return to launch")]
        public void ConflictingCommandsAreAmbiguous(string text) => Expect(text, VoiceMatchOutcome.Ambiguous);

        [DataTestMethod]
        [DataRow("is the drone arm")]
        [DataRow("drone, is it disarm?")]
        [DataRow("are the motors arm")]
        [DataRow("did it take off")]
        [DataRow("has the drone take off")]
        [DataRow("did it land")]
        [DataRow("why did it stop")]
        public void QuestionsAreNotCommands(string text) => Expect(text, VoiceMatchOutcome.Question);

        // ---- ARM / DISARM separation and word boundaries
        [TestMethod]
        public void DisarmNeverMatchesAsArm()
        {
            foreach (var t in new[] { "disarm", "please disarm the drone", "drone disarm", "dis-arm the drone" })
            {
                var m = VoiceCommandParser.Parse(t);
                Assert.AreEqual(VoiceCommand.Disarm, m.Command, t);
                CollectionAssert.DoesNotContain(m.Found.ToList(), VoiceCommand.Arm, t);
            }

            foreach (var t in new[] { "arm", "arm the drone", "drone arm", "please arm the drone" })
                CollectionAssert.DoesNotContain(VoiceCommandParser.Parse(t).Found.ToList(), VoiceCommand.Disarm, t);
        }

        [TestMethod]
        public void EveryPhraseMapsToItsOwnCommandOnly()
        {
            foreach (var p in VoiceCommandParser.Phrases)
            {
                var m = VoiceCommandParser.Parse(p.Text);
                Assert.AreEqual(VoiceMatchOutcome.Command, m.Outcome, p.Text);
                Assert.AreEqual(p.Command, m.Command, p.Text);
                Assert.AreEqual(1, m.Found.Count, p.Text);
            }
        }

        [TestMethod]
        public void FoundCommandsAreInSpokenOrder()
        {
            var m = VoiceCommandParser.Parse("arm take off disarm");
            CollectionAssert.AreEqual(new[] { VoiceCommand.Arm, VoiceCommand.Takeoff, VoiceCommand.Disarm }, m.Found.ToArray());
            Assert.AreEqual(VoiceMatchOutcome.Ambiguous, m.Outcome);
        }

        // ---- normalisation
        [DataTestMethod]
        [DataRow("Drone, please ARM!", "drone please arm")]
        [DataRow("  take-off   now ", "take off now")]
        [DataRow("Takeoff", "take off")]
        [DataRow("Don't arm", "do not arm")]
        [DataRow("don’t disarm", "do not disarm")]
        [DataRow("I can't take off", "i can not take off")]
        [DataRow("won't", "will not")]
        [DataRow("shouldn't", "should not")]
        [DataRow("dis-arm", "disarm")]
        [DataRow("dis armed", "disarmed")]
        [DataRow("let's go", "lets go")]
        [DataRow("R.T.L.", "r t l")]
        [DataRow("", "")]
        public void Normalize(string input, string expected) => Assert.AreEqual(expected, VoiceCommandParser.Normalize(input));

        [TestMethod]
        public void HeadlinesForTheUi()
        {
            Assert.AreEqual("ARM COMMAND DETECTED", VoiceCommandParser.Parse("arm").Headline);
            Assert.AreEqual("DISARM COMMAND DETECTED", VoiceCommandParser.Parse("disarm").Headline);
            Assert.AreEqual("TAKEOFF COMMAND DETECTED", VoiceCommandParser.Parse("take off").Headline);
            Assert.AreEqual("RTL COMMAND DETECTED", VoiceCommandParser.Parse("return to launch").Headline);
            Assert.AreEqual("NO COMMAND DETECTED", VoiceCommandParser.Parse("hello").Headline);
            StringAssert.Contains(VoiceCommandParser.Parse("disarm and arm").Headline, "AMBIGUOUS");
            StringAssert.Contains(VoiceCommandParser.Parse("do not arm").Headline, "REJECTED");
            StringAssert.Contains(VoiceCommandParser.Parse("my arm hurts").Headline, "REPEAT");
        }

        // ---- recogniser vocabulary
        [TestMethod]
        public void RecogniserVocabularyCoversCommandsNegationsAndUnknown()
        {
            var phrases = VoiceGrammar.Phrases();
            foreach (var p in VoiceCommandParser.Phrases)
                CollectionAssert.Contains(phrases, p.Text);
            // negations can be transcribed, so "do not arm" is never forced to "arm"
            foreach (var n in new[] { "do", "not", "don't", "never", "no", "cancel", "abort" })
                CollectionAssert.Contains(phrases, n);
            Assert.AreEqual(VoiceGrammar.Unknown, phrases.Last());
            // no decoy is (or contains) a command
            foreach (var d in VoiceGrammar.Decoys)
                Assert.AreEqual(0, VoiceCommandParser.Parse(d).Found.Count, d);
            // near-homophones are deliberately NOT decoys: they stole accented commands in the benchmark
            foreach (var h in new[] { "harm", "on", "lend", "stock", "cold", "i'm" })
                CollectionAssert.DoesNotContain(VoiceGrammar.Decoys, h);
            StringAssert.StartsWith(VoiceGrammar.ToJson(new[] { "a", "b\"c" }), "[\"a\",\"b\\\"c\"]");
        }

        [TestMethod]
        public void ProtectedWordsAreNeverTreatedAsBackground()
        {
            foreach (var w in new[] { "not", "don't", "do", "never", "arm", "disarm", "take", "off", "stop", "land", "launch", "is", "what" })
                Assert.IsTrue(VoiceCommandParser.IsProtectedWord(VoiceCommandParser.Normalize(w)), w);
            foreach (var w in new[] { "please", "drone", "hello", "weather", "[unk]" })
                Assert.IsFalse(VoiceCommandParser.IsProtectedWord(VoiceCommandParser.Normalize(w)), w);
        }
    }
}
