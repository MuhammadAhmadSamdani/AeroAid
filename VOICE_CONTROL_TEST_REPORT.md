# AeroAid GCS — voice control test report

| | |
|---|---|
| Date | 2026-11-17 |
| Branch | `custom-gcs` (uncommitted working tree) |
| Recogniser | **Vosk 0.3.38 offline + `vosk-model-small-en-us-0.15`, restricted to the command vocabulary**. Fallback: Windows speech (System.Speech) with a closed grammar. |
| Microphone on this PC | Realtek "Microphone Array" (laptop built-in), captured as 16 kHz 16-bit mono through winmm |
| Engine selection | See `VOICE_RECOGNITION_ENGINE_COMPARISON.md` |

## Summary

| Area | Result |
|---|---|
| Root cause of "I say ARM and it hears another word" | **Found.** Open-vocabulary dictation (§1) |
| Build (`dotnet build`, Release: MissionPlanner + tests) | **PASS**: 0 errors; no new warnings in the voice code |
| AeroAid automated tests (x64) | **PASS**: 217 passed, 0 failed, 3 skipped (corpus, live-microphone and SITL tests run only on demand) |
| Stock Mission Planner tests | 8 failures, all known environmental ones that also fail on the untouched baseline (DetectBoard "ShowEvent Not Set", internet downloads). See QA_TEST_REPORT B9. |
| Synthesised speech through the live pipeline (2 Windows voices × 3 speeds × 16 commands) | **PASS**: 95 / 96 recognised, 0 wrong |
| 4,980-clip corpus through the real app pipeline (12 voices, 7 accents, 5 noise conditions) | Clean **98.0 %**, fan noise **89.6 %**, quiet microphone **97.0 %**. Background talk: commands rejected (asked to repeat). **0 wrong commands, 0 ARM↔DISARM confusions.** |
| Real microphone, acoustic loop (laptop speaker → laptop microphone array) | **PARTIAL**: the capture path works. Recognition of the played-back TTS was poor (8–9 / 24 per run), most likely because the array's echo cancellation suppresses the laptop's own speaker output (§5). |
| **Live human voice (your voice, your accent)** | **NOT TESTED** by me. Use Test Mode (§7); its saved accuracy report is the acceptance test. |
| Pakistani-English accent | **NOT TESTED**: no en-PK synthetic voice was available. Closest covered: en-IN (98.1 %). |
| Background-noise robustness with a real fan or real people talking | **NOT TESTED** live; tested with synthetic noise only |
| Different physical microphones | **NOT TESTED**: only one microphone (the built-in array) exists on this PC. Device selection, refresh and missing-device handling are tested. |
| MAVLink execution against SITL / a real vehicle | **NOT TESTED** in this pass. Controller logic is tested against a fake vehicle (§6). |

## 1. Root cause of the previous recognition problem

The previous engine used the 2006-era Windows desktop recogniser (SAPI 5.4, `MS-1033-80-DESK`, untrained). It ran a
command grammar **and free dictation** side by side, and the recogniser kept whichever scored better.

For a short command like "arm", dictation picks the most likely English word for that vowel sound, and for many
accents that is not "arm". Measured on the same audio:

| Spoken | Previous engine (SAPI + dictation) | Open-vocabulary Vosk | Final engine |
|---|---|---|---|
| "arm" (Indian, Irish voices) | rejected / not a command | "on" | **"arm"** |
| "arm" (British, South-African voices) | rejected / not a command | "i'm" | **"arm"** |
| "arm the drone" (earlier reproduction) | "our major own" | "please on the drone" | **"arm the drone"** |
| "disarm" (Whisper tiny, for comparison) | — | — | Whisper: "This sound.", "DeSan" |

The previous engine recognised **11.6 %** of the corpus commands in clean audio, **0 %** of single-word "arm" and
**0 %** for the Indian and South-African voices. Three further problems made it worse:

- the grammar was built from many filler words;
- no microphone-level handling: the laptop array delivers speech at about −40 dBFS;
- a 65 % sentence-level confidence that dictation results rarely reached.

## 2. What was built

```
Microphone (winmm, 16 kHz mono, selectable device)
 → 90 Hz high-pass → voice-activity detection (adaptive noise floor, 300 ms pre-roll, 650 ms end pause)
 → automatic gain per utterance (to −20 dBFS, max +30 dB, never clipping)
 → Vosk recogniser restricted to the command vocabulary (+ decoy words + [unk])
 → background-word suppression (quiet filler words > 12 dB below the command; never commands, negations, questions)
 → completeness check (speech not covered by recognised words → INCOMPLETE, repeat)
 → classifier: LEAD* COMMAND TAIL*; negation / ambiguity / question / unclear rejection
 → confidence gate on the command words (70 %)
 → TEST MODE? → show and score only, nothing sent
 → safety validation → confirmation → MAVLink (existing MainV2.comPort) → verification from ACK + telemetry
```

| Command | Say | Confirmation | Verified by |
|---|---|---|---|
| ARM | "arm", "arm the drone", "drone arm" | always | ACK + heartbeat armed flag |
| DISARM | "disarm", "disarm the drone", "drone disarm" | always; refused while airborne | ACK + heartbeat armed flag |
| TAKEOFF | "take off", "drone take off", "take off now" | always; vehicle must be armed | ACK + climb (altitude / landed state) |
| STOP | "stop" | optional (default off) | heartbeat mode BRAKE (or GUIDED hold) |
| HOLD | "hold position", "hold" | optional (default off) | heartbeat mode GUIDED |
| RTL | "return to launch", "return home", "RTL" | default on | heartbeat mode RTL |
| LAND | "land" | default on | heartbeat mode LAND |

## 3. Microphone configuration

- **Device:** selectable; stored by name so it survives re-plugging. A missing device is reported, never silently
  replaced. Tested: `MissingSelectedMicrophoneIsReportedNotSilentlyReplaced`.
- **Format:** 16 kHz, 16-bit, mono PCM. This is what both recognisers expect; Windows resamples from the device rate.
- **Indicators:**
  - Microphone ON/OFF/ERROR;
  - **Receiving audio** (YES / SPEECH / NO SIGNAL / CLIPPING);
  - level, peak, background level, last speech level and SNR;
  - the applied auto gain.
- **Test microphone:** a 4 s recording with a verdict (GOOD / TOO QUIET / CLIPPING / NOISY / NO SIGNAL) and playback.
- **Permissions:** digital silence for 5 s raises a NO SIGNAL hint and a **Privacy settings** button
  (`ms-settings:privacy-microphone`). Device errors (unplugged, in use) are shown as text.
- **Measured on this PC:**
  - the built-in array delivered speaker playback at −36…−52 dBFS speech, with a −70 dBFS idle floor;
  - auto gain added +13 to +30 dB per utterance.

  This low level is the second root cause found: without auto gain, 20 of 24 commands produced **no text at all**.

## 4. Test results with synthesised speech

### 4.1 Live pipeline injection (`VoiceInjectionTests`)

TTS speech was streamed through the app's engine from a fake microphone.

| Test | Result |
|---|---|
| 16 commands × 2 voices (David, Zira) × 3 speeds | **95 / 96** recognised, **0 wrong**. The one miss was "take off now" at fast speed, heard as "take off no", which was rejected as negated: a safe miss. |
| 12 negated / ambiguous / unrelated sentences × 2 voices, e.g. "Do not arm the drone", "Disarm and arm the drone", "My arm hurts", "Hold on a second" | **PASS**: 0 commands |
| Test Mode: "Arm the drone" | Recognised and scored as ARM, **never raised as a command**; report written |
| Quiet background talk before a command | Command accepted (3 of 3 rounds); a quiet "do not" before a loud "arm the drone" still rejects it |
| Windows-speech fallback, US voice | 16 / 16 |
| Microphone loss while listening | Listening stops with a message |

### 4.2 Corpus through the real pipeline (`VoiceCorpusTests`, 4,980 clips)

| | clean | fan 10 dB | babble 20 dB | babble 10 dB | quiet mic |
|---|---|---|---|---|---|
| Commands recognised | **98.0 %** | **89.6 %** | 0.8 % | 0.0 % | **97.0 %** |
| False accepts (of 360 non-command clips) | 17 | 17 | 0 | 0 | 17 |
| **Wrong commands** | **0** | **0** | **0** | **0** | **0** |

**Command recognition accuracy (clean audio):**

| Command | ARM | DISARM | TAKEOFF | STOP | HOLD | RTL | LAND |
|---|---|---|---|---|---|---|---|
| Single word, all voices and speeds | 91.7 % | 100 % | 100 % | 100 % | 100 % | — | 100 % |

**By accent (clean):**

| en-AU | en-GB | en-IE | en-IN | en-NG | en-US | en-ZA | en-PK |
|---|---|---|---|---|---|---|---|
| 100 % | 100 % | 100 % | 98.1 % | 81.1 % | 100 % | 98.1 % | NOT TESTED |

**Speaking rate (clean):** normal 98.3 %, fast 95.8 %, slow 98.3 %.

**Incorrectly recognised commands and what was changed** (full history in the comparison document, §5):

| Observed | Change |
|---|---|
| "hold" (Nigerian voice) → "halt" → STOP, a wrong command | "halt" removed |
| "army" → "arming", "charm" → "should arm", "my arm hurts" → "my arm current" | Decoys and a stricter sentence grammar |
| "arm" → "harm", "land" → "lend", "stop" → "stock" (accented) | Near-homophone decoys removed |
| Correct "arm the drone" rejected at 49 % confidence | One spoken form per command word: recall went from 89 % to 98 % |
| Live microphone: no text at all | Automatic gain per utterance |
| Live loop: "Do not arm the drone" → "hover" → HOLD | "hover" removed |
| Live loop: "Disarm and arm the drone" → "disarm" → DISARM | Completeness check added. It catches some cases, not this one (§5). |
| Fan noise starting mid-utterance counted as lost speech | Completeness check uses the utterance's own noise floor |

**Remaining false accepts:** isolated words that sound like "arm" in some accents ("on", "harm", "farm", "charm",
"um") can be accepted as ARM, in 17 of 360 non-command clips in clean audio. ARM always requires the confirmation
dialog, which shows what was heard.

## 5. Real microphone: acoustic loop-back (`VoiceLiveMicTests`)

The 12 command phrases (10 commands, 2 that must be rejected) were played by Windows TTS through the laptop speakers
and captured by the **real built-in microphone array** through the app's normal capture path. The app was in Test
Mode, so nothing could be sent.

| Run | Pipeline state | Correct | Wrong |
|---|---|---|---|
| 1 | before auto gain | 4 / 24 (20 of 24 produced no text) | 0 |
| 2 | auto gain | 9 / 24 | **1**: "Disarm and arm the drone" → "disarm" → DISARM |
| 3 | + completeness check | 9 / 24 | **1**: "Do not arm the drone" → "hover" → HOLD (then "hover" removed) |
| 4 | final | **8 / 24** (4 of 20 commands + 4 of 4 rejections) | **0** |

**What this shows:**

- The capture path works end to end with a real device.
- Level handling was a real problem, and it is now fixed.
- The final pipeline gave no wrong command in this run.

**What it does not show:** human-voice accuracy.

The recorded audio, saved and analysed, explains why the loop scores far below the corpus. Typically the first word
arrives at −36 dBFS and the following words 20–25 dB lower, with almost nothing above 3 kHz. This pattern matches
the array's built-in echo cancellation attenuating the laptop's own speaker output. Echo cancellation does not act on
a person speaking. Decoding the same recording with Vosk as one continuous stream recovers only about half of the
commands, so the audio itself is degraded. **This conclusion is likely but not proven**: the Realtek enhancement settings were not
changed on your system.

**Residual risk seen only in this degraded audio:** if the end of a sentence is 20 dB quieter than its start, the
recogniser can drop it. "Disarm and arm the drone" then becomes "disarm", and the completeness check does not always
catch it. Mitigation: DISARM and ARM always show a confirmation dialog containing the heard text. DISARM is also
refused while airborne.

## 6. Safety verification (fake vehicle, `VoiceControllerTests` / `VoiceWorkflowTests`, all PASS)

| Requirement | Test(s) |
|---|---|
| Nothing executes while MAVLink is disconnected (no dialog either) | `DisconnectedVehicleGetsNothingAndNoDialog`, `ModeCommandsAreNotSentToADisarmedOrDisconnectedVehicle`, `NothingIsAllowedWhileDisconnected` |
| Explicit confirmation before ARM and TAKEOFF (and DISARM); declining sends nothing | `ArmIsSentOnlyAfterConfirmationAndVerified`, `DeclinedConfirmationSendsNothing`, `RtlAndLandNeedConfirmationAndAreVerified` |
| Armed state verified after ARM/DISARM; success not assumed from transmission | `VehicleRefusalIsReportedWithItsReason`, `TakeoffAckWithoutClimbIsNotReportedAsCompleted`, `VehicleRefusingAModeIsReportedAsRejected` |
| Never auto-disarm an airborne vehicle | `AirborneDisarmIsRefusedWithoutAskingOrSending`, `DisarmChecksNeverAllowAnAirborneVehicle` |
| Pre-arm, failsafe, mode and readiness checks respected (never forced) | `ArmChecks`, `TakeoffChecks`, `PrearmNotReportedIsPassedToArduPilotChecks`; the state is re-checked after the dialog: `StateChangeDuringDialogIsCaughtBeforeSending` |
| Duplicate prevention | `EachUtteranceExecutesAtMostOnce`, `SameUtteranceIsProcessedOnce`, `CooldownBlocksARepeatedCommand`, `ModeCommandIsNotRepeatedWhenAlreadyInThatMode`, `CommandsWhileADialogIsOpenAreIgnored` |
| No execution from partial, low-confidence, negated, ambiguous or unclear speech | `LowConfidenceIsNeverACommand`, `SpeechNotCoveredByRecognisedWordsMakesTheResultIncomplete`, `NegationSplitByAPauseStillCancelsTheCommand`, `NonCommandsRaiseNoCommandEvent`, parser tests (spec examples incl. "Do not arm the drone" → REJECT, "Disarm and arm the drone" → AMBIGUOUS) |
| Test Mode never sends | `TestModeNeverSendsAnything`, `UtteranceRecognisedInTestModeIsNeverSentLater`, `TestModeNeverRaisesCommandDetected` |
| STOP / HOLD / RTL / LAND can interrupt a takeoff climb check, never an open dialog | `StopInterruptsTheTakeoffClimbCheck` |
| Existing connection architecture | Commands go through `FlightControlManager` → `MainV2.comPort`; no second connection is created (code review) |

## 7. How to complete the live-voice acceptance test

1. Voice & Microphone: choose the microphone → **Test microphone**. You want a GOOD verdict.
2. Keep **Test Mode** on and press **Test voice recognition**.
3. For each of ARM, DISARM, TAKEOFF, STOP, HOLD, RTL, LAND and NEGATED: click the button, then say the command
   15–20 times. Vary the speed and loudness, and include "drone …" and "please …" forms. Repeat with a fan running.
4. Press **Save accuracy report**. It goes to `Documents\Mission Planner\AeroAid\voice-tests\`.
5. Accept only if WRONG COMMAND = 0 for every row. Repeated misses for one command point to microphone level or
   placement, so check **Test microphone** first. Please share the report: misheard words can be added to the
   vocabulary or decoys, as was done during this work.

## 8. Known limitations and remaining issues

- **Not tested with a human voice, the Pakistani-English accent, other microphones, real background noise, SITL or a
  real vehicle** (see Summary).
- **Background conversation:** commands said while other people talk at a similar level are rejected as unclear,
  negated or question ("please repeat"), never executed wrongly. They are recognised only when your voice is clearly
  louder: use a headset or close microphone.
- **Words that sound like "arm"** ("on", "harm", "um") can be detected as ARM. The ARM confirmation dialog is the
  safeguard.
- **Speech whose end is much quieter than its start** can lose its last words (seen only with echo-cancelled
  loop-back audio). This is partly caught by the completeness check.
- **Nigerian-English** synthetic voice: 81 % in clean audio. Short vowels in "arm" and "land" are the weak spots.
- Accented "take off now" at fast speed can be heard as "take off no", which is rejected (safe miss).
- English only. The model download needs internet once (41 MB); it can also be copied in manually.
- The fallback engine (Windows speech) is much less accurate for non-US accents. The Voice page shows a warning when
  it is in use.

## 9. Reproducing

- Unit and pipeline tests (64-bit is required for Vosk):

  ```
  vstest.console.exe MissionPlannerTests.dll /Platform:x64 /TestCaseFilter:FullyQualifiedName~AeroAid
  ```

- Live loop-back (plays sound): set `AEROAID_LIVE_MIC=1`. Optionally set `AEROAID_LIVE_MIC_DUMP=<file.wav>`.
- Corpus run: see `VOICE_RECOGNITION_ENGINE_COMPARISON.md` §6.7.
- The voice log `aeroaid_voice.log` in the Mission Planner data folder records every utterance, decision, ignored
  background word and command result.
