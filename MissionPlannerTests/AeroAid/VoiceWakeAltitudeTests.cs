using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MissionPlanner.AeroAid.Voice;

namespace MissionPlanner.AeroAid.Tests
{
    /// <summary>
    /// Wake word ("AeroAid ..."), spoken takeoff altitude ("take off five meters"), repeated commands and the
    /// confusable-word rules of the specification. Pure parser tests - no speech engine, no vehicle.
    /// </summary>
    [TestClass]
    public class VoiceWakeAltitudeTests
    {
        private static readonly VoiceParseOptions Optional = new VoiceParseOptions("aero aid", VoicePrefixMode.Optional);
        private static readonly VoiceParseOptions Critical = new VoiceParseOptions("aero aid", VoicePrefixMode.Critical);
        private static readonly VoiceParseOptions All = new VoiceParseOptions("aero aid", VoicePrefixMode.All);

        private static VoiceMatch P(string text, VoiceParseOptions o = null)
        {
            var m = VoiceCommandParser.Parse(text, o ?? Optional);
            Console.WriteLine($"{text,-45} -> {m}  prefix={m.HasPrefix} alt={m.Altitude}");
            return m;
        }

        // ---- the specification's command table (wake word optional)
        [DataTestMethod]
        [DataRow("Arm", VoiceCommand.Arm, null)]
        [DataRow("Arm drone", VoiceCommand.Arm, null)]
        [DataRow("AeroAid arm", VoiceCommand.Arm, null)]
        [DataRow("AeroAid, arm drone", VoiceCommand.Arm, null)]
        [DataRow("aero aid arm the drone", VoiceCommand.Arm, null)]
        [DataRow("Disarm", VoiceCommand.Disarm, null)]
        [DataRow("Disarm drone", VoiceCommand.Disarm, null)]
        [DataRow("AeroAid disarm", VoiceCommand.Disarm, null)]
        [DataRow("Aero-Aid, disarm drone", VoiceCommand.Disarm, null)]
        [DataRow("Take off", VoiceCommand.Takeoff, null)]
        [DataRow("Take off five meters", VoiceCommand.Takeoff, 5.0)]
        [DataRow("Takeoff to five meters", VoiceCommand.Takeoff, 5.0)]
        [DataRow("AeroAid, take off five meters", VoiceCommand.Takeoff, 5.0)]
        [DataRow("take off to 5 m", VoiceCommand.Takeoff, 5.0)]
        [DataRow("take off at ten metres", VoiceCommand.Takeoff, 10.0)]
        [DataRow("take off fifteen meters now", VoiceCommand.Takeoff, 15.0)]
        [DataRow("take off to twenty five meters", VoiceCommand.Takeoff, 25.0)]
        [DataRow("take off twenty", VoiceCommand.Takeoff, 20.0)]
        [DataRow("Land", VoiceCommand.Land, null)]
        [DataRow("Return to launch", VoiceCommand.Rtl, null)]
        [DataRow("hey aero aid, return to launch", VoiceCommand.Rtl, null)]
        public void SpecificationCommands(string text, VoiceCommand cmd, double? alt)
        {
            var m = P(text);
            Assert.AreEqual(VoiceMatchOutcome.Command, m.Outcome, m.ToString());
            Assert.AreEqual(cmd, m.Command);
            Assert.AreEqual(alt, m.Altitude);
            Assert.AreEqual(text.ToLowerInvariant().Contains("aero"), m.HasPrefix, "wake word detection");
        }

        // ---- malformed altitudes are never guessed
        [DataTestMethod]
        [DataRow("take off two five meters")]   // "to five" misheard? two numbers in a row: unclear
        [DataRow("take off to meters")]          // no number
        [DataRow("take off meters")]
        [DataRow("take off five five meters")]
        [DataRow("take off five meters five")]
        [DataRow("take off to")]
        [DataRow("take off one hundred meters")]
        [DataRow("land five meters")]            // an altitude belongs to TAKEOFF only
        [DataRow("arm five")]
        public void MalformedAltitudeIsUnclear(string text)
        {
            var m = P(text);
            Assert.AreEqual(VoiceMatchOutcome.TooLong, m.Outcome, m.ToString());
            Assert.IsFalse(m.IsCommand);
        }

        [TestMethod]
        public void OutOfRangeNumbersAreNotAltitudes()
        {
            Assert.IsFalse(VoiceCommandParser.TryReadAltitude(new[] { "0" }, 0, out _, out _));
            Assert.IsFalse(VoiceCommandParser.TryReadAltitude(new[] { "100" }, 0, out _, out _));
            Assert.IsTrue(VoiceCommandParser.TryReadAltitude(new[] { "ninety", "nine", "meters" }, 0, out var a, out var end));
            Assert.AreEqual(99, a);
            Assert.AreEqual(3, end);
        }

        // ---- wake word requirement
        [TestMethod]
        public void CriticalModeRequiresTheWakeWordForArmDisarmTakeoffOnly()
        {
            foreach (var t in new[] { "arm", "arm the drone", "disarm", "take off", "take off five meters" })
            {
                var m = P(t, Critical);
                Assert.AreEqual(VoiceMatchOutcome.PrefixMissing, m.Outcome, t);
                Assert.IsFalse(m.IsCommand, t);
                Assert.AreEqual(VoiceCommand.None, m.Command, t);
                StringAssert.Contains(m.Headline, "WAKE WORD");
            }

            Assert.AreEqual(VoiceCommand.Arm, P("aero aid arm the drone", Critical).Command);
            Assert.AreEqual(VoiceCommand.Disarm, P("AeroAid disarm", Critical).Command);
            Assert.AreEqual(5.0, P("aero aid take off to five meters", Critical).Altitude);
            // stopping / landing must stay fast: no wake word needed in Critical mode
            foreach (var t in new[] { "stop", "land", "hold position", "return to launch" })
                Assert.IsTrue(P(t, Critical).IsCommand, t);
        }

        [TestMethod]
        public void AllModeRequiresTheWakeWordEverywhere()
        {
            foreach (var t in new[] { "arm", "stop", "land", "return to launch" })
                Assert.AreEqual(VoiceMatchOutcome.PrefixMissing, P(t, All).Outcome, t);
            Assert.AreEqual(VoiceCommand.Land, P("aero aid land", All).Command);
        }

        [TestMethod]
        public void WakeWordOnlyCountsAtTheStart()
        {
            // in the middle or at the end it is an unexpected word, not a wake word
            Assert.AreEqual(VoiceMatchOutcome.TooLong, P("arm aero aid", Critical).Outcome);
            Assert.AreEqual(VoiceMatchOutcome.TooLong, P("please aero aid arm", Critical).Outcome);
            Assert.AreEqual(VoiceMatchOutcome.NoCommand, P("aero aid").Outcome);
            // a negation after the wake word still cancels
            Assert.AreEqual(VoiceMatchOutcome.Negated, P("aero aid do not arm the drone", Critical).Outcome);
            Assert.AreEqual(VoiceMatchOutcome.Ambiguous, P("aero aid disarm and arm", Critical).Outcome);
        }

        [TestMethod]
        public void ConfigurableWakeWord()
        {
            var falcon = new VoiceParseOptions("Falcon", VoicePrefixMode.Critical);
            Assert.AreEqual(VoiceCommand.Arm, P("falcon arm", falcon).Command);
            Assert.IsFalse(P("aero aid arm", falcon).IsCommand, "the old wake word is just unexpected words now");
            Assert.AreEqual(VoiceMatchOutcome.PrefixMissing, P("arm", falcon).Outcome);
            // no wake word configured: the requirement cannot apply
            var none = new VoiceParseOptions("", VoicePrefixMode.All);
            Assert.AreEqual(VoicePrefixMode.Optional, none.Mode);
            Assert.IsTrue(P("arm", none).IsCommand);
            // the recogniser vocabulary contains the configured wake word
            CollectionAssert.Contains(VoiceGrammar.Phrases(true, "Falcon"), "falcon");
            CollectionAssert.Contains(VoiceGrammar.Phrases(), "aero aid");
            foreach (var w in new[] { "five", "twenty", "meters", "metres", "to" })
                CollectionAssert.Contains(VoiceGrammar.Phrases(), w);
        }

        // ---- repeats
        [DataTestMethod]
        [DataRow("arm arm", VoiceCommand.Arm)]
        [DataRow("take off take off", VoiceCommand.Takeoff)]
        [DataRow("land land the drone", VoiceCommand.Land)]
        [DataRow("aero aid disarm disarm", VoiceCommand.Disarm)]
        public void ImmediateRepeatOfTheSameCommandIsOneCommand(string text, VoiceCommand cmd) =>
            Assert.AreEqual(cmd, P(text).Command);

        [DataTestMethod]
        [DataRow("stop stop land")]
        [DataRow("arm disarm")]
        [DataRow("disarm arm arm")]
        public void DifferentCommandsStayAmbiguous(string text) =>
            Assert.AreEqual(VoiceMatchOutcome.Ambiguous, P(text).Outcome);

        // ---- confusable words (specification section 6): never ARM
        [DataTestMethod]
        [DataRow("alarm")]
        [DataRow("harm")]
        [DataRow("the alarm is ringing")]
        [DataRow("that would harm the drone")]
        [DataRow("farm")]
        [DataRow("charm")]
        [DataRow("army")]
        [DataRow("armed")]
        [DataRow("my arm hurts")]
        [DataRow("he hurt his arm yesterday")]
        [DataRow("disarmament")]
        public void ConfusableWordsAreNeverArm(string text)
        {
            foreach (var o in new[] { Optional, Critical })
            {
                var m = P(text, o);
                Assert.IsFalse(m.IsCommand, text);
                Assert.AreNotEqual(VoiceCommand.Arm, m.Command, text);
            }
        }

        [TestMethod]
        public void DisarmIsNeverArmWithWakeWordOrAltitude()
        {
            foreach (var t in new[] { "disarm", "aero aid disarm", "aeroaid disarm the drone", "dis arm", "disarm disarm" })
            {
                var m = P(t, Optional);
                Assert.AreEqual(VoiceCommand.Disarm, m.Command, t);
                CollectionAssert.DoesNotContain(m.Found.ToList(), VoiceCommand.Arm, t);
            }
        }
    }

    /// <summary>Spoken altitude through safety validation and the controller (fake vehicle - nothing is sent).</summary>
    [TestClass]
    public class VoiceSpokenAltitudeControllerTests
    {
        [ClassInitialize]
        public static void Init(TestContext _) => TestEnv.Init();

        [TestMethod]
        public void SpokenAltitudeIsValidatedNotClamped()
        {
            var max = AeroSettings.MaxAltitude;
            var t = VoiceWorkflowTests.ArmedOnGround();
            Assert.AreEqual(0, VoiceSafety.Check(VoiceCommand.Takeoff, t, new List<string>(), 5).Count);
            var tooHigh = VoiceSafety.Check(VoiceCommand.Takeoff, t, new List<string>(), max + 10);
            Assert.AreEqual(1, tooHigh.Count, string.Join(" ", tooHigh));
            StringAssert.Contains(tooHigh[0], "Spoken takeoff altitude");
            Assert.AreEqual(5, VoiceSafety.TakeoffAltitude(5), 1e-9);
            Assert.AreEqual(Math.Max(1, Math.Min(AeroSettings.TakeoffAltitude, max)), VoiceSafety.TakeoffAltitude(), 1e-9);
        }
    }
}
