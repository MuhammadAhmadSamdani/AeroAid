using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MissionPlanner.AeroAid.Telemetry;
using MissionPlanner.AeroAid.Voice;

namespace MissionPlanner.AeroAid.Tests
{
    /// <summary>
    /// The voice command workflow (validation → confirmation → command → verification) against a fake vehicle.
    /// Verifies what is and is NOT sent. No MAVLink traffic.
    /// </summary>
    [TestClass]
    public class VoiceControllerTests
    {
        private sealed class FakeBackend : IVoiceFlightBackend
        {
            public TelemetrySnapshot State = VoiceWorkflowTests.OnGround();
            public bool ConfirmAnswer = true;
            public bool VehicleAccepts = true;
            public bool ClimbAfterTakeoff = true;
            public Action<TelemetrySnapshot> DuringDialog;
            public int Confirms, Notifies, Arms, Disarms, Takeoffs, Stops, Holds, Rtls, Lands;
            public double LastTakeoffAlt;
            public readonly List<string> Dialogs = new List<string>();

            public TelemetrySnapshot Capture() => State;

            public bool Confirm(string title, string text)
            {
                Confirms++;
                Dialogs.Add(text);
                DuringDialog?.Invoke(State);
                return ConfirmAnswer;
            }

            public void Notify(string title, string text)
            {
                Notifies++;
                Dialogs.Add(text);
            }

            public Task<bool> Arm()
            {
                Arms++;
                if (VehicleAccepts) State.Armed = true;
                return Task.FromResult(VehicleAccepts);
            }

            public Task<bool> Disarm()
            {
                Disarms++;
                if (VehicleAccepts) State.Armed = false;
                return Task.FromResult(VehicleAccepts);
            }

            public Task<bool> Takeoff(double altitude)
            {
                Takeoffs++;
                LastTakeoffAlt = altitude;
                if (VehicleAccepts && ClimbAfterTakeoff)
                {
                    State.LandedState = 3;
                    State.AltRelative = 0.8;
                }

                return Task.FromResult(VehicleAccepts);
            }

            public Task<bool> Stop()
            {
                Stops++;
                if (VehicleAccepts) State.Mode = "Brake";
                return Task.FromResult(VehicleAccepts);
            }

            public Task<bool> Hold()
            {
                Holds++;
                if (VehicleAccepts) State.Mode = "Guided";
                return Task.FromResult(VehicleAccepts);
            }

            public Task<bool> Rtl()
            {
                Rtls++;
                if (VehicleAccepts) State.Mode = "RTL";
                return Task.FromResult(VehicleAccepts);
            }

            public Task<bool> Land()
            {
                Lands++;
                if (VehicleAccepts) State.Mode = "Land";
                return Task.FromResult(VehicleAccepts);
            }

            public int Sent => Arms + Disarms + Takeoffs + Stops + Holds + Rtls + Lands;

            public string LastVehicleMessage() => VehicleAccepts ? "" : "PreArm: Throttle below failsafe";
            /// <summary>When true, Delay really yields (lets another command interleave).</summary>
            public bool YieldOnDelay;

            public Task Delay(int ms) => YieldOnDelay ? Task.Delay(5) : Task.CompletedTask;
        }

        private static long nextId = 1000;

        private static VoiceUtterance Utterance(string text, float conf = 0.9f, long? id = null)
        {
            var m = VoiceCommandParser.Parse(text, VoiceSettings.ParseOptions); // as the engine does
            return new VoiceUtterance(id ?? ++nextId, text, conf, VoiceCommandEngine.CommandGrammarName, m,
                conf >= VoiceSettings.MinConfidence, m.Headline, null);
        }

        private static (VoiceCommandController c, FakeBackend b) Make(TelemetrySnapshot state = null)
        {
            var b = new FakeBackend();
            if (state != null) b.State = state;
            return (new VoiceCommandController(b) { TakeoffVerifySeconds = 1 }, b);
        }

        [ClassInitialize]
        public static void Init(TestContext _)
        {
            TestEnv.Init();
            AeroSettings.MinSatellites = 6;
            AeroSettings.MinBatteryPercent = 30;
            VoiceSettings.MinConfidence = 0.65;
            VoiceSettings.CooldownSeconds = 10;
            VoiceSettings.TestMode = false; // these tests exercise the (fake) sending path
            VoiceSettings.ConfirmRtlLand = true;
            VoiceSettings.ConfirmStopHold = false;
        }

        [TestMethod]
        public async Task ArmIsSentOnlyAfterConfirmationAndVerified()
        {
            var (c, b) = Make();
            var r = await c.HandleAsync(Utterance("drone, please arm"));
            Assert.AreEqual(VoiceExecState.Completed, r);
            Assert.AreEqual(1, b.Confirms);
            Assert.AreEqual(1, b.Arms);
            Assert.AreEqual(0, b.Disarms + b.Takeoffs);
            StringAssert.Contains(b.Dialogs[0], "drone, please arm");
            StringAssert.Contains(b.Dialogs[0], "ARM the motors");
        }

        [TestMethod]
        public async Task DeclinedConfirmationSendsNothing()
        {
            foreach (var (text, state) in new[]
                     {
                         ("arm the drone", VoiceWorkflowTests.OnGround()),
                         ("disarm the drone", VoiceWorkflowTests.ArmedOnGround()),
                         ("take off", VoiceWorkflowTests.ArmedOnGround())
                     })
            {
                var (c, b) = Make(state);
                b.ConfirmAnswer = false;
                Assert.AreEqual(VoiceExecState.Cancelled, await c.HandleAsync(Utterance(text)), text);
                Assert.AreEqual(1, b.Confirms, text);
                Assert.AreEqual(0, b.Arms + b.Disarms + b.Takeoffs, text);
            }
        }

        [TestMethod]
        public async Task DisconnectedVehicleGetsNothingAndNoDialog()
        {
            var t = VoiceWorkflowTests.OnGround();
            t.Connected = false;
            var (c, b) = Make(t);
            Assert.AreEqual(VoiceExecState.NotConnected, await c.HandleAsync(Utterance("arm")));
            Assert.AreEqual(0, b.Confirms + b.Arms);
        }

        [TestMethod]
        public async Task AirborneDisarmIsRefusedWithoutAskingOrSending()
        {
            var (c, b) = Make(VoiceWorkflowTests.Flying());
            Assert.AreEqual(VoiceExecState.SafetyCheckFailed, await c.HandleAsync(Utterance("disarm the drone")));
            Assert.AreEqual(0, b.Confirms, "no confirmation dialog for an airborne disarm");
            Assert.AreEqual(0, b.Disarms);
            StringAssert.Contains(c.StateText, "AIRBORNE");
        }

        [TestMethod]
        public async Task GroundDisarmWorks()
        {
            var (c, b) = Make(VoiceWorkflowTests.ArmedOnGround());
            Assert.AreEqual(VoiceExecState.Completed, await c.HandleAsync(Utterance("drone disarm")));
            Assert.AreEqual(1, b.Disarms);
            Assert.IsFalse(b.State.Armed);
        }

        [TestMethod]
        public async Task TakeoffRequiresArmedVehicle()
        {
            var (c, b) = Make(VoiceWorkflowTests.OnGround());
            Assert.AreEqual(VoiceExecState.SafetyCheckFailed, await c.HandleAsync(Utterance("take off")));
            Assert.AreEqual(0, b.Takeoffs + b.Arms + b.Confirms);
            StringAssert.Contains(c.StateText, "not armed");
        }

        [TestMethod]
        public async Task TakeoffIsVerifiedByClimb()
        {
            var (c, b) = Make(VoiceWorkflowTests.ArmedOnGround());
            Assert.AreEqual(VoiceExecState.Completed, await c.HandleAsync(Utterance("please initiate takeoff")));
            Assert.AreEqual(1, b.Takeoffs);
            Assert.AreEqual(VoiceSafety.TakeoffAltitude(), b.LastTakeoffAlt, 1e-9);
            StringAssert.Contains(c.StateText, "climbing");
        }

        [TestMethod]
        public async Task SpokenTakeoffAltitudeIsConfirmedAndSent()
        {
            var (c, b) = Make(VoiceWorkflowTests.ArmedOnGround());
            Assert.AreEqual(VoiceExecState.Completed, await c.HandleAsync(Utterance("aero aid take off to five meters")));
            Assert.AreEqual(1, b.Takeoffs);
            Assert.AreEqual(5.0, b.LastTakeoffAlt, 1e-9);
            // the operator sees the spoken altitude in the confirmation dialog before anything is sent
            Assert.AreEqual(1, b.Confirms);
            StringAssert.Contains(b.Dialogs[b.Dialogs.Count - 1], "TAKE OFF to 5 m");
            StringAssert.Contains(b.Dialogs[b.Dialogs.Count - 1], "altitude as spoken");
        }

        [TestMethod]
        public async Task SpokenTakeoffAltitudeAboveTheLimitIsRefusedNotClamped()
        {
            var (c, b) = Make(VoiceWorkflowTests.ArmedOnGround());
            var tooHigh = (int)AeroSettings.MaxAltitude + 5;
            if (tooHigh > 99) Assert.Inconclusive("maximum altitude above what can be spoken");
            var words = new[] { "", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine" };
            var tens = new[] { "", "ten", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety" };
            var spoken = tooHigh < 20 ? null : tens[tooHigh / 10] + (tooHigh % 10 > 0 ? " " + words[tooHigh % 10] : "");
            if (spoken == null) Assert.Inconclusive("unusual maximum altitude");
            Assert.AreEqual(VoiceExecState.SafetyCheckFailed, await c.HandleAsync(Utterance($"take off {spoken} meters")));
            Assert.AreEqual(0, b.Takeoffs + b.Confirms, "nothing confirmed or sent");
            StringAssert.Contains(c.StateText, "Spoken takeoff altitude");
        }

        [TestMethod]
        public async Task WakeWordRequiredForArmWhenConfigured()
        {
            var before = VoiceSettings.RequirePrefix;
            VoiceSettings.RequirePrefix = VoicePrefixMode.Critical;
            try
            {
                var (c, b) = Make(VoiceWorkflowTests.OnGround());
                Assert.AreEqual(VoiceExecState.Ignored, await c.HandleAsync(Utterance("arm the drone")));
                Assert.AreEqual(0, b.Confirms + b.Sent, "no dialog and nothing sent without the wake word");
                Assert.AreEqual(VoiceExecState.Completed, await c.HandleAsync(Utterance("aero aid arm the drone")));
                Assert.AreEqual(1, b.Arms);
            }
            finally
            {
                VoiceSettings.RequirePrefix = before;
            }
        }

        [TestMethod]
        public async Task TakeoffAckWithoutClimbIsNotReportedAsCompleted()
        {
            var (c, b) = Make(VoiceWorkflowTests.ArmedOnGround());
            b.ClimbAfterTakeoff = false;
            Assert.AreEqual(VoiceExecState.Sent, await c.HandleAsync(Utterance("take off")));
            StringAssert.Contains(c.StateText, "no climb");
        }

        [TestMethod]
        public async Task VehicleRefusalIsReportedWithItsReason()
        {
            var (c, b) = Make();
            b.VehicleAccepts = false;
            Assert.AreEqual(VoiceExecState.Rejected, await c.HandleAsync(Utterance("arm")));
            Assert.AreEqual(1, b.Arms);
            StringAssert.Contains(c.StateText, "PreArm: Throttle below failsafe");
        }

        [TestMethod]
        public async Task StateChangeDuringDialogIsCaughtBeforeSending()
        {
            var (c, b) = Make(VoiceWorkflowTests.ArmedOnGround());
            b.DuringDialog = t => { t.LandedState = 2; t.AltRelative = 4; }; // e.g. someone took off with RC meanwhile
            Assert.AreEqual(VoiceExecState.SafetyCheckFailed, await c.HandleAsync(Utterance("disarm")));
            Assert.AreEqual(0, b.Disarms);
        }

        [TestMethod]
        public async Task EachUtteranceExecutesAtMostOnce()
        {
            var (c, b) = Make();
            var u = Utterance("arm the drone");
            Assert.AreEqual(VoiceExecState.Completed, await c.HandleAsync(u));
            b.State.Armed = false;
            Assert.AreEqual(VoiceExecState.Ignored, await c.HandleAsync(u), "same utterance again");
            Assert.AreEqual(1, b.Arms);
            Assert.AreEqual(1, b.Confirms);
        }

        [TestMethod]
        public async Task CooldownBlocksARepeatedCommand()
        {
            var (c, b) = Make();
            Assert.AreEqual(VoiceExecState.Completed, await c.HandleAsync(Utterance("arm")));
            b.State.Armed = false;
            Assert.AreEqual(VoiceExecState.Rejected, await c.HandleAsync(Utterance("arm")), "second ARM inside the cool-down");
            StringAssert.Contains(c.StateText, "cool-down");
            Assert.AreEqual(1, b.Arms);
        }

        [TestMethod]
        public async Task CooldownNeverDelaysArmThenTakeoffOrDisarm()
        {
            var (c, b) = Make();
            Assert.AreEqual(VoiceExecState.Completed, await c.HandleAsync(Utterance("arm")));
            // ArduCopter auto-disarms ~10 s after arming: TAKEOFF must be possible right away
            Assert.AreEqual(VoiceExecState.Completed, await c.HandleAsync(Utterance("take off")));
            b.State = VoiceWorkflowTests.ArmedOnGround();
            Assert.AreEqual(VoiceExecState.Completed, await c.HandleAsync(Utterance("disarm")));
            Assert.AreEqual(1, b.Arms);
            Assert.AreEqual(1, b.Takeoffs);
            Assert.AreEqual(1, b.Disarms);
        }

        [TestMethod]
        public async Task CommandsWhileADialogIsOpenAreIgnored()
        {
            var (c, b) = Make();
            Task<VoiceExecState> inner = null;
            b.DuringDialog = _ => inner = c.HandleAsync(Utterance("arm"));
            Assert.AreEqual(VoiceExecState.Completed, await c.HandleAsync(Utterance("arm the drone")));
            Assert.AreEqual(VoiceExecState.Ignored, await inner);
            Assert.AreEqual(1, b.Arms);
            Assert.AreEqual(1, b.Confirms);
        }

        [TestMethod]
        public async Task NonActionableUtterancesDoNothing()
        {
            var (c, b) = Make(VoiceWorkflowTests.ArmedOnGround());
            foreach (var u in new[]
                     {
                         Utterance("do not disarm"), Utterance("disarm and arm"), Utterance("is it armed"),
                         Utterance("hello there"), Utterance("disarm", 0.4f)
                     })
                Assert.AreEqual(VoiceExecState.Ignored, await c.HandleAsync(u), u.Text);
            Assert.AreEqual(0, b.Confirms + b.Notifies + b.Arms + b.Disarms + b.Takeoffs);
            Assert.AreEqual(VoiceExecState.Idle, c.State);
        }
    
        // ------------------------------------------------------------------ Test Mode

        [TestMethod]
        public async Task TestModeNeverSendsAnything()
        {
            VoiceSettings.TestMode = true;
            try
            {
                foreach (var (text, state) in new[]
                         {
                             ("arm the drone", VoiceWorkflowTests.OnGround()), ("disarm the drone", VoiceWorkflowTests.ArmedOnGround()),
                             ("take off", VoiceWorkflowTests.ArmedOnGround()), ("stop", VoiceWorkflowTests.Flying()),
                             ("hold position", VoiceWorkflowTests.Flying()), ("return to launch", VoiceWorkflowTests.Flying()),
                             ("land", VoiceWorkflowTests.Flying())
                         })
                {
                    var (c, b) = Make(state);
                    Assert.AreEqual(VoiceExecState.Ignored, await c.HandleAsync(Utterance(text)), text);
                    Assert.AreEqual(0, b.Confirms + b.Notifies + b.Sent, text + ": nothing may be sent or asked in Test Mode");
                }
            }
            finally
            {
                VoiceSettings.TestMode = false;
            }
        }

        [TestMethod]
        public async Task UtteranceRecognisedInTestModeIsNeverSentLater()
        {
            var (c, b) = Make();
            var m = VoiceCommandParser.Parse("arm the drone");
            var u = new VoiceUtterance(++nextId, "arm the drone", 0.95f, "test", m, true, m.Headline, null, testMode: true);
            Assert.AreEqual(VoiceExecState.Ignored, await c.HandleAsync(u));
            Assert.AreEqual(0, b.Sent + b.Confirms);
        }

        // ------------------------------------------------------------------ STOP / HOLD / RTL / LAND

        [TestMethod]
        public async Task StopAndHoldRunWithoutDialogByDefaultAndAreVerified()
        {
            var (c, b) = Make(VoiceWorkflowTests.Flying());
            Assert.AreEqual(VoiceExecState.Completed, await c.HandleAsync(Utterance("stop")));
            Assert.AreEqual(1, b.Stops);
            Assert.AreEqual(0, b.Confirms);
            Assert.AreEqual(VoiceExecState.Completed, await c.HandleAsync(Utterance("drone hold position")));
            Assert.AreEqual(1, b.Holds);
            Assert.AreEqual(0, b.Confirms);
        }

        [TestMethod]
        public async Task StopAndHoldAskWhenConfigured()
        {
            VoiceSettings.ConfirmStopHold = true;
            try
            {
                var (c, b) = Make(VoiceWorkflowTests.Flying());
                b.ConfirmAnswer = false;
                Assert.AreEqual(VoiceExecState.Cancelled, await c.HandleAsync(Utterance("stop")));
                Assert.AreEqual(1, b.Confirms);
                Assert.AreEqual(0, b.Stops);
            }
            finally
            {
                VoiceSettings.ConfirmStopHold = false;
            }
        }

        [TestMethod]
        public async Task RtlAndLandNeedConfirmationAndAreVerified()
        {
            var (c, b) = Make(VoiceWorkflowTests.Flying());
            Assert.AreEqual(VoiceExecState.Completed, await c.HandleAsync(Utterance("return to launch")));
            Assert.AreEqual(1, b.Rtls);
            Assert.AreEqual(1, b.Confirms);
            StringAssert.Contains(b.Dialogs[0], "RETURN TO LAUNCH");
            Assert.AreEqual(VoiceExecState.Completed, await c.HandleAsync(Utterance("please land")));
            Assert.AreEqual(1, b.Lands);
            Assert.AreEqual(2, b.Confirms);

            var (c2, b2) = Make(VoiceWorkflowTests.Flying());
            b2.ConfirmAnswer = false;
            Assert.AreEqual(VoiceExecState.Cancelled, await c2.HandleAsync(Utterance("land")));
            Assert.AreEqual(0, b2.Lands);
        }

        [TestMethod]
        public async Task ModeCommandsAreNotSentToADisarmedOrDisconnectedVehicle()
        {
            foreach (var text in new[] { "stop", "hold position", "return to launch", "land" })
            {
                var (c, b) = Make(VoiceWorkflowTests.OnGround());
                Assert.AreEqual(VoiceExecState.SafetyCheckFailed, await c.HandleAsync(Utterance(text)), text);
                Assert.AreEqual(0, b.Sent + b.Confirms, text);

                var t = VoiceWorkflowTests.Flying();
                t.Connected = false;
                var (c2, b2) = Make(t);
                Assert.AreEqual(VoiceExecState.NotConnected, await c2.HandleAsync(Utterance(text)), text);
                Assert.AreEqual(0, b2.Sent + b2.Confirms, text);
            }
        }

        [TestMethod]
        public async Task ModeCommandIsNotRepeatedWhenAlreadyInThatMode()
        {
            var t = VoiceWorkflowTests.Flying();
            t.Mode = "RTL";
            var (c, b) = Make(t);
            Assert.AreEqual(VoiceExecState.SafetyCheckFailed, await c.HandleAsync(Utterance("return to launch")));
            Assert.AreEqual(0, b.Rtls + b.Confirms);
            StringAssert.Contains(c.StateText, "already in RTL");
        }

        [TestMethod]
        public async Task VehicleRefusingAModeIsReportedAsRejected()
        {
            var (c, b) = Make(VoiceWorkflowTests.Flying());
            b.VehicleAccepts = false;
            Assert.AreEqual(VoiceExecState.Rejected, await c.HandleAsync(Utterance("stop")));
            Assert.AreEqual(1, b.Stops);
        }

        [TestMethod]
        public async Task StopInterruptsTheTakeoffClimbCheck()
        {
            var (c, b) = Make(VoiceWorkflowTests.ArmedOnGround());
            c.TakeoffVerifySeconds = 5;
            b.ClimbAfterTakeoff = false; // the climb check keeps waiting
            b.YieldOnDelay = true;
            var takeoff = c.HandleAsync(Utterance("take off"));
            var until = DateTime.UtcNow.AddSeconds(3);
            while (b.Takeoffs == 0 && DateTime.UtcNow < until)
                await Task.Delay(5);
            Assert.AreEqual(1, b.Takeoffs);
            Assert.IsTrue(c.Busy, "climb verification running");
            Assert.AreEqual(VoiceExecState.Completed, await c.HandleAsync(Utterance("stop")), "STOP must not wait for the takeoff check");
            Assert.AreEqual(1, b.Stops);
            await takeoff;
            Assert.IsFalse(c.Busy);
            // but an ARM during a takeoff check is still refused
            Assert.AreEqual(0, b.Arms);
        }
    }
}
