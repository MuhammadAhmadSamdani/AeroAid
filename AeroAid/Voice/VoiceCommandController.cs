using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using log4net;
using MissionPlanner.AeroAid.Branding;
using MissionPlanner.AeroAid.Flight;
using MissionPlanner.AeroAid.Telemetry;
using MissionPlanner.AeroAid.UI;

namespace MissionPlanner.AeroAid.Voice
{
    /// <summary>Execution state of the current / last voice command (shown on the Voice page).</summary>
    public enum VoiceExecState
    {
        Idle,
        Validating,
        WaitingForConfirmation,
        Approved,
        Sent,
        Completed,
        /// <summary>The vehicle refused the command, or it was blocked by the cool-down.</summary>
        Rejected,
        /// <summary>The operator answered No in the confirmation dialog.</summary>
        Cancelled,
        NotConnected,
        SafetyCheckFailed,
        /// <summary>Not executed: duplicate, busy, low confidence or not a command.</summary>
        Ignored
    }

    /// <summary>What the controller needs from the outside world. Replaced by a fake in unit tests.</summary>
    public interface IVoiceFlightBackend
    {
        TelemetrySnapshot Capture();

        /// <summary>Modal Yes/No confirmation (UI thread). Must stop any movement first.</summary>
        bool Confirm(string title, string text);

        /// <summary>Non-blocking-for-safety information message (UI thread).</summary>
        void Notify(string title, string text);

        /// <summary>Arm via MAVLink (never forced); true only when the vehicle ACKed AND reports armed.</summary>
        Task<bool> Arm();

        /// <summary>Disarm via MAVLink (never forced); true only when the vehicle ACKed AND reports disarmed.</summary>
        Task<bool> Disarm();

        /// <summary>GUIDED takeoff via MAVLink; true when the vehicle ACKed MAV_CMD_NAV_TAKEOFF.</summary>
        Task<bool> Takeoff(double altitude);

        /// <summary>Stop now: BRAKE mode (or hold position when BRAKE is unavailable); true when the vehicle reports it.</summary>
        Task<bool> Stop();

        /// <summary>Hold position (GUIDED, zero velocity); true when the vehicle reports GUIDED.</summary>
        Task<bool> Hold();

        /// <summary>RTL mode; true when the vehicle reports RTL.</summary>
        Task<bool> Rtl();

        /// <summary>LAND mode; true when the vehicle reports LAND.</summary>
        Task<bool> Land();

        /// <summary>Latest STATUSTEXT from the vehicle (reason for a refusal), or "".</summary>
        string LastVehicleMessage();

        Task Delay(int ms);
    }

    /// <summary>The real backend: existing MainV2.comPort link through FlightControlManager / TelemetryManager.</summary>
    public sealed class MavlinkVoiceBackend : IVoiceFlightBackend
    {
        public TelemetrySnapshot Capture() => TelemetryManager.Capture();
        public bool Confirm(string title, string text) => FlightActions.Confirm(title, text);
        public void Notify(string title, string text) => CustomMessageBox.Show(text, BrandingManager.AppName + " - " + title);
        public Task<bool> Arm() => FlightControlManager.Instance.ArmAsync();
        public Task<bool> Disarm() => FlightControlManager.Instance.DisarmAsync();
        public Task<bool> Takeoff(double altitude) => FlightControlManager.Instance.TakeoffAsync(altitude);

        public async Task<bool> Stop()
        {
            var t = TelemetryManager.Capture();
            if (FlightControlManager.AvailableModes(t).Any(m => string.Equals(m, "Brake", StringComparison.OrdinalIgnoreCase)))
                return await FlightControlManager.Instance.SetModeAsync("Brake");
            return await Hold();
        }

        public async Task<bool> Hold()
        {
            if (!await FlightControlManager.Instance.HoldPositionAsync())
                return false;
            // verify from the heartbeat, not from the transmission
            for (var i = 0; i < 30; i++)
            {
                if (string.Equals(TelemetryManager.Capture().Mode, "Guided", StringComparison.OrdinalIgnoreCase))
                    return true;
                await Task.Delay(100);
            }

            return false;
        }

        public Task<bool> Rtl() => FlightControlManager.Instance.ReturnToLaunchAsync();
        public Task<bool> Land() => FlightControlManager.Instance.LandAsync();
        public string LastVehicleMessage() => TelemetryManager.Capture().LastHighMessage;
        public Task Delay(int ms) => Task.Delay(ms);
    }

    /// <summary>
    /// Runs a detected voice command: state validation → confirmation dialog → re-validation → MAVLink command →
    /// verification from the vehicle's ACK and its reported state. A recognised sentence NEVER acts by itself.
    ///  - nothing at all is sent while TEST MODE is on;
    ///  - ARM, DISARM and TAKEOFF always need an explicit Yes in a confirmation dialog; RTL and LAND by default;
    ///    STOP and HOLD only when configured (they only stop the vehicle where it is);
    ///  - DISARM is refused outright while the vehicle may be airborne;
    ///  - TAKEOFF requires the vehicle to be armed already (a separate, separately confirmed voice/UI command);
    ///  - one command at a time, each utterance at most once, and a cool-down before the SAME ARM / DISARM / TAKEOFF
    ///    can be sent again; STOP / HOLD / RTL / LAND are refused when the vehicle is already in that mode;
    ///  - STOP, HOLD, RTL and LAND may interrupt a takeoff that is still being verified (climb check);
    ///  - while a confirmation dialog is open, further voice commands are ignored (voice cannot answer the dialog).
    /// </summary>
    public sealed class VoiceCommandController
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private static VoiceCommandController instance;
        public static VoiceCommandController Instance => instance ?? (instance = new VoiceCommandController(new MavlinkVoiceBackend()));

        private readonly IVoiceFlightBackend backend;
        private readonly HashSet<long> handled = new HashSet<long>();
        private readonly Queue<long> handledOrder = new Queue<long>();
        /// <summary>When each command was last sent. The cool-down is per command: it stops a repeated ARM,
        /// but never delays the ARM → TAKEOFF sequence (ArduCopter auto-disarms ~10 s after arming) or a DISARM.</summary>
        private readonly Dictionary<VoiceCommand, DateTime> lastSentUtc = new Dictionary<VoiceCommand, DateTime>();
        private bool busy;
        private bool dialogOpen;
        private bool verifyingTakeoff;

        public VoiceCommandController(IVoiceFlightBackend backend)
        {
            this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
        }

        /// <summary>How long to watch for the vehicle to start climbing after a takeoff ACK.</summary>
        public int TakeoffVerifySeconds { get; set; } = 10;

        public VoiceExecState State { get; private set; } = VoiceExecState.Idle;
        public string StateText { get; private set; } = "No voice command yet.";
        public VoiceCommand CurrentCommand { get; private set; } = VoiceCommand.None;
        public bool Busy => busy;

        /// <summary>(message, level) progress for the UI log (caller's thread - the UI thread in the app).</summary>
        public event Action<string, StatusLevel> Progress;

        public event Action StateChanged;

        public static StatusLevel LevelOf(VoiceExecState s)
        {
            switch (s)
            {
                case VoiceExecState.Completed: return StatusLevel.Ok;
                case VoiceExecState.WaitingForConfirmation:
                case VoiceExecState.Approved:
                case VoiceExecState.Sent:
                case VoiceExecState.Validating:
                case VoiceExecState.Cancelled:
                case VoiceExecState.Ignored: return StatusLevel.Warning;
                case VoiceExecState.Rejected:
                case VoiceExecState.NotConnected:
                case VoiceExecState.SafetyCheckFailed: return StatusLevel.Critical;
                default: return StatusLevel.Unavailable;
            }
        }

        public static string Title(VoiceExecState s)
        {
            switch (s)
            {
                case VoiceExecState.Validating: return "Checking vehicle state";
                case VoiceExecState.WaitingForConfirmation: return "Waiting for confirmation";
                case VoiceExecState.Approved: return "Command approved";
                case VoiceExecState.Sent: return "Command sent";
                case VoiceExecState.Completed: return "Command completed";
                case VoiceExecState.Rejected: return "Command rejected";
                case VoiceExecState.Cancelled: return "Cancelled by operator";
                case VoiceExecState.NotConnected: return "Vehicle not connected";
                case VoiceExecState.SafetyCheckFailed: return "Safety check failed";
                case VoiceExecState.Ignored: return "Ignored";
                default: return "Idle";
            }
        }

        private void Set(VoiceExecState s, string text, StatusLevel? level = null)
        {
            State = s;
            StateText = text;
            log.Info($"Voice command {VoiceCommandParser.Name(CurrentCommand)}: {Title(s)} - {text}");
            VoiceLog.Write($"command {VoiceCommandParser.Name(CurrentCommand)}: {Title(s)} - {text}");
            try
            {
                Progress?.Invoke(Title(s) + ": " + text, level ?? LevelOf(s));
                StateChanged?.Invoke();
            }
            catch (Exception ex)
            {
                log.Error("Voice progress handler failed", ex);
            }
        }

        private void Say(string text, StatusLevel level)
        {
            VoiceLog.Write("  " + text);
            try
            {
                Progress?.Invoke(text, level);
            }
            catch (Exception ex)
            {
                log.Error("Voice progress handler failed", ex);
            }
        }

        /// <summary>
        /// Handles one recognised utterance. Must be called on the UI thread (shows modal dialogs).
        /// Returns the final state.
        /// </summary>
        public async Task<VoiceExecState> HandleAsync(VoiceUtterance u)
        {
            if (u == null || u.Match == null)
                return VoiceExecState.Ignored;

            if (!u.Actionable)
            {
                // not a command: logged by the engine; does not disturb a running workflow's state
                return VoiceExecState.Ignored;
            }

            if (u.TestMode || VoiceSettings.TestMode)
            {
                Say($"TEST MODE: \"{u.Text}\" -> {VoiceCommandParser.Name(u.Match.Command)} recognised, NOT sent to the vehicle.", StatusLevel.Warning);
                return VoiceExecState.Ignored;
            }

            if (!MarkHandled(u.Id))
            {
                Say($"Ignored duplicate of \"{u.Text}\" (already processed).", StatusLevel.Warning);
                return VoiceExecState.Ignored;
            }

            var cmd = u.Match.Command;
            var name = VoiceCommandParser.Name(cmd);
            // STOP / HOLD / RTL / LAND may interrupt the climb verification of a takeoff, never an open dialog
            var preempt = busy && verifyingTakeoff && !dialogOpen && IsModeCommand(cmd);
            if (busy && !preempt)
            {
                Say($"Ignored \"{u.Text}\": another voice command is still in progress.", StatusLevel.Warning);
                return VoiceExecState.Ignored;
            }

            var since = lastSentUtc.TryGetValue(cmd, out var last) ? (DateTime.UtcNow - last).TotalSeconds : double.MaxValue;
            if (!IsModeCommand(cmd) && since < VoiceSettings.CooldownSeconds)
            {
                CurrentCommand = cmd;
                Set(VoiceExecState.Rejected,
                    $"\"{u.Text}\" ignored - {name} was just sent (cool-down {VoiceSettings.CooldownSeconds - since:0} s left).");
                return State;
            }

            if (preempt)
                verifyingTakeoff = false; // ends the running climb check
            busy = true;
            CurrentCommand = cmd;
            try
            {
                Set(VoiceExecState.Validating, $"{name} heard in \"{u.Text}\" ({u.Confidence:P0}).");

                var t = backend.Capture();
                var passed = new List<string>();
                var spokenAlt = cmd == VoiceCommand.Takeoff ? u.Match.Altitude : null;
                var failures = VoiceSafety.Check(cmd, t, passed, spokenAlt);
                if (!t.HasHeartbeat)
                {
                    Set(VoiceExecState.NotConnected, name + " not sent: " + string.Join(" ", failures));
                    return State;
                }

                if (failures.Count > 0)
                {
                    Set(VoiceExecState.SafetyCheckFailed, name + " blocked: " + string.Join(" ", failures));
                    backend.Notify("Voice " + name.ToLowerInvariant(),
                        $"Voice command \"{u.Text}\" → {name}\n\nBlocked by safety checks:\n• " + string.Join("\n• ", failures));
                    return State;
                }

                // ---- explicit confirmation
                if (NeedsConfirmation(cmd))
                {
                    Set(VoiceExecState.WaitingForConfirmation, $"Confirm {name} in the dialog.");
                    bool yes;
                    dialogOpen = true;
                    try
                    {
                        yes = backend.Confirm("Voice " + name.ToLowerInvariant(), ConfirmText(cmd, u, t, passed));
                    }
                    finally
                    {
                        dialogOpen = false;
                    }

                    if (!yes)
                    {
                        Set(VoiceExecState.Cancelled, name + " was not confirmed - nothing sent.");
                        return State;
                    }
                }

                // ---- the state may have changed while the dialog was open: validate again
                var t2 = backend.Capture();
                var failures2 = VoiceSafety.Check(cmd, t2, new List<string>(), spokenAlt);
                if (!t2.HasHeartbeat)
                {
                    Set(VoiceExecState.NotConnected, name + " not sent: link lost while the dialog was open.");
                    return State;
                }

                if (failures2.Count > 0)
                {
                    Set(VoiceExecState.SafetyCheckFailed, name + " not sent - state changed: " + string.Join(" ", failures2));
                    return State;
                }

                Set(VoiceExecState.Approved, NeedsConfirmation(cmd)
                    ? name + " approved by the operator."
                    : name + " approved (no confirmation is configured for " + name + ").");
                lastSentUtc[cmd] = DateTime.UtcNow;
                Set(VoiceExecState.Sent, name + " sent to the vehicle - waiting for its response...");
                switch (cmd)
                {
                    case VoiceCommand.Arm:
                        await Finish(await backend.Arm(), "Vehicle ARMED (confirmed by ACK and heartbeat).", "Vehicle refused to ARM.");
                        break;
                    case VoiceCommand.Disarm:
                        await Finish(await backend.Disarm(), "Vehicle DISARMED (confirmed by ACK and heartbeat).", "Vehicle refused to DISARM.");
                        break;
                    case VoiceCommand.Takeoff:
                        await RunTakeoff(spokenAlt);
                        break;
                    case VoiceCommand.Stop:
                        await Finish(await backend.Stop(), "Vehicle STOPPED (BRAKE / hold position, confirmed by heartbeat).", "Vehicle did not switch to BRAKE / hold.");
                        break;
                    case VoiceCommand.Hold:
                        await Finish(await backend.Hold(), "Vehicle HOLDING POSITION (GUIDED, confirmed by heartbeat).", "Vehicle did not switch to GUIDED hold.");
                        break;
                    case VoiceCommand.Rtl:
                        await Finish(await backend.Rtl(), "Vehicle in RTL - returning to launch (confirmed by heartbeat).", "Vehicle did not switch to RTL.");
                        break;
                    case VoiceCommand.Land:
                        await Finish(await backend.Land(), "Vehicle in LAND mode - landing (confirmed by heartbeat).", "Vehicle did not switch to LAND.");
                        break;
                }

                lastSentUtc[cmd] = DateTime.UtcNow;
                return State;
            }
            catch (Exception ex)
            {
                log.Error("Voice command failed", ex);
                Set(VoiceExecState.Rejected, name + " failed: " + ex.Message);
                return State;
            }
            finally
            {
                // a preempting command leaves the interrupted takeoff's busy flag to its own finally
                if (!preempt)
                    busy = false;
            }
        }

        /// <summary>STOP / HOLD / RTL / LAND: flight-mode changes (no cool-down, may interrupt a takeoff check).</summary>
        public static bool IsModeCommand(VoiceCommand c) =>
            c == VoiceCommand.Stop || c == VoiceCommand.Hold || c == VoiceCommand.Rtl || c == VoiceCommand.Land;

        /// <summary>ARM, DISARM, TAKEOFF always; RTL / LAND and STOP / HOLD as configured.</summary>
        public static bool NeedsConfirmation(VoiceCommand c)
        {
            switch (c)
            {
                case VoiceCommand.Stop:
                case VoiceCommand.Hold:
                    return VoiceSettings.ConfirmStopHold;
                case VoiceCommand.Rtl:
                case VoiceCommand.Land:
                    return VoiceSettings.ConfirmRtlLand;
                default:
                    return true;
            }
        }

        private async Task Finish(bool ok, string okText, string failText)
        {
            if (ok)
            {
                Set(VoiceExecState.Completed, okText);
                return;
            }

            // the refusal reason (STATUSTEXT) can arrive just after the ACK
            await backend.Delay(400);
            var why = backend.LastVehicleMessage();
            Set(VoiceExecState.Rejected, failText + (string.IsNullOrEmpty(why) ? "" : " Vehicle says: " + why));
        }

        private async Task RunTakeoff(double? spokenAlt)
        {
            var alt = VoiceSafety.TakeoffAltitude(spokenAlt);
            if (!backend.Capture().Armed)
            {
                Set(VoiceExecState.Rejected, "The vehicle disarmed before takeoff (ArduCopter auto-disarm). Arm again, then say \"take off\".");
                return;
            }

            var ack = await backend.Takeoff(alt);
            if (!ack)
            {
                await Finish(false, "", $"Vehicle rejected TAKEOFF to {alt:0.#} m.");
                return;
            }

            Say($"Takeoff to {alt:0.#} m accepted by the vehicle - verifying climb...", StatusLevel.Warning);
            verifyingTakeoff = true;
            try
            {
                await VerifyClimb(alt);
            }
            finally
            {
                verifyingTakeoff = false;
            }
        }

        private async Task VerifyClimb(double alt)
        {
            for (var i = 0; i < TakeoffVerifySeconds * 5; i++)
            {
                if (!verifyingTakeoff)
                    return; // interrupted by STOP / HOLD / RTL / LAND
                var t = backend.Capture();
                if (!t.Armed)
                {
                    Set(VoiceExecState.Rejected, "Takeoff accepted, but the vehicle disarmed. " + backend.LastVehicleMessage());
                    return;
                }

                if (t.AltRelative >= 0.5 || t.LandedState == 2 || t.LandedState == 3)
                {
                    Set(VoiceExecState.Completed, $"TAKEOFF confirmed - climbing ({t.AltRelative:0.0} m of {alt:0.#} m).");
                    return;
                }

                await backend.Delay(200);
            }

            Set(VoiceExecState.Sent, $"Takeoff accepted by the vehicle, but no climb seen within {TakeoffVerifySeconds} s - check the vehicle.",
                StatusLevel.Critical);
        }

        private static string ConfirmText(VoiceCommand cmd, VoiceUtterance u, TelemetrySnapshot t, List<string> passed)
        {
            var head = $"Voice command heard: \"{u.Text}\"  (recogniser confidence {u.Confidence:P0})\nDetected: {VoiceCommandParser.Name(cmd)}" +
                       (u.Match.HasPrefix ? "  (wake word heard)" : "") + "\n\n" +
                       "Checks passed:\n✓ " + string.Join("\n✓ ", passed) + "\n\n";
            switch (cmd)
            {
                case VoiceCommand.Arm:
                    return head + $"ARM the motors now in {t.Mode.ToUpperInvariant()} mode?\n\nPropellers may start spinning - make sure " +
                           "people and objects are clear. ArduPilot pre-arm checks still apply (arming is never forced).\n" +
                           "Note: ArduCopter disarms automatically if it does not take off within a few seconds.";
                case VoiceCommand.Disarm:
                    return head + "DISARM the motors now?\n\nThe vehicle is on the ground. Propellers will stop.";
                case VoiceCommand.Takeoff:
                    return head + $"TAKE OFF to {VoiceSafety.TakeoffAltitude(u.Match.Altitude):0.#} m in GUIDED mode?" +
                           (u.Match.Altitude.HasValue ? "  (altitude as spoken)" : "  (configured default altitude)") + "\n\n" +
                           "Make sure the area above the vehicle is clear.";
                case VoiceCommand.Stop:
                    return head + "STOP the vehicle now (BRAKE mode, or hold position)?";
                case VoiceCommand.Hold:
                    return head + "HOLD the current position (GUIDED, zero velocity)?";
                case VoiceCommand.Rtl:
                    return head + $"RETURN TO LAUNCH now?\nCurrent distance to home: {t.DistToHome:0} m. The vehicle climbs to its RTL altitude first.";
                case VoiceCommand.Land:
                    return head + "LAND at the current position now?";
                default:
                    return head;
            }
        }

        private bool MarkHandled(long id)
        {
            lock (handled)
            {
                if (!handled.Add(id))
                    return false;
                handledOrder.Enqueue(id);
                while (handledOrder.Count > 500)
                    handled.Remove(handledOrder.Dequeue());
                return true;
            }
        }
    }
}
