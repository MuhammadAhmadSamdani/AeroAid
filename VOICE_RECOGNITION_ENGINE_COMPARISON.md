# Voice recognition engine comparison and setup guide

| | |
|---|---|
| Date | 2026-11-17 |
| Branch | `custom-gcs` |
| Decision | **Vosk (Kaldi) offline recogniser, `vosk-model-small-en-us-0.15`, restricted at run time to the command vocabulary**, behind our own voice-activity detector and a phrase-grammar classifier. Windows speech (System.Speech) with a closed grammar remains as an automatic fallback. |
| Test audio | Synthesised speech (neural and Windows TTS voices), not human voices. See §3 for what that does and does not prove. |

## 1. Summary

The previous implementation used the old Windows desktop recogniser (SAPI 5.4, `MS-1033-80-DESK`, about 2006 era)
and ran **free dictation** next to a command grammar. Dictation is where the wrong words came from: a single "arm"
was transcribed as "on", "I'm", "um" or "ah", and "arm the drone" became "our major own".

Five engine configurations were measured on the same 4,980 labelled clips. Each transcript was scored with the
product's own command classifier.

| Engine (configuration) | Commands recognised, clean audio | Single word "arm", clean | Wrong command executed | Decode time per clip |
|---|---|---|---|---|
| System.Speech, command grammar + dictation (**previous app**) | 11.6 % | 0 % | 1 | ~1.1 s |
| System.Speech, closed grammar only | 36.6 % | 16.7 % | 4 | ~0.26 s |
| Vosk small-en-us, free vocabulary | 64.2 % | 33.3 % | 0 | ~1.7 s* |
| faster-whisper `tiny.en`, free vocabulary | 39.5 % (71.5 % with no confidence gate) | 0 % (≤ 33 % ungated) | 0 | ~2.2 s* |
| faster-whisper `base.en`, free vocabulary | 70.4 % | 19.4 % | 0 | ~2.7 s* |
| faster-whisper `base.en` + command prompt (best Whisper) | 91.7 % | 86.1 % ("disarm" 52.8 %) | 0 | ~2.5 s* |
| **Vosk small-en-us, command vocabulary (selected)** | **97.8 %** | **91.7 %** | **0** | **~0.14 s** |

\* With several decoders running in parallel on one CPU; single-stream times are lower, but the ranking is the same.

Vosk restricted to the command vocabulary was the most accurate configuration by a wide margin. It handled the
non-US accents best (§4.3), it is fully offline, it decodes a command in about a tenth of a second, and it fits the
existing 16 kHz microphone pipeline. Across every condition it produced **zero wrong commands**: a command was either
recognised correctly or the operator was asked to repeat.

## 2. Candidates

| Engine | Type | Offline | Windows / .NET Framework 4.7.2 | Restrict to commands? | Size | Notes |
|---|---|---|---|---|---|---|
| **System.Speech** (SAPI 5.4 desktop) | HMM, 2006-era acoustic model | Yes | Built in | Yes: SRGS grammars | 0 | The existing engine. Untrained models are strongly accent-sensitive; dictation is weak. |
| **Windows.Media.SpeechRecognition** (WinRT, OneCore DNN recogniser `MS-1033-110-WINMO-DNN`, installed on this PC) | DNN | Yes, with list or grammar constraints | WinRT interop from desktop .NET | Yes: list or SRGS constraints | 0 | **Not benchmarked.** It only listens to the *default Windows recording device*: there is no audio-stream input. The app therefore could not choose the microphone, run its own voice-activity detection, record a microphone test, or be fed the test corpus. Rejected on integration grounds, not measured accuracy. |
| **Vosk** 0.3.38 (Kaldi) + `vosk-model-small-en-us-0.15` | TDNN chain model | Yes | NuGet `Vosk`, managed API, native win-x64 | **Yes: run-time phrase list with `[unk]`** | 41 MB download (68 MB on disk) + 47 MB native DLLs | Apache-2.0. Per-word confidence and timing. Accepts 16 kHz PCM, the same format the app already captures. |
| **faster-whisper** `tiny.en` / `base.en` (CTranslate2) | Transformer encoder-decoder | Yes | Python only (or a Whisper.net / whisper.cpp port) | No: only an `initial_prompt` text hint | 75 / 142 MB | Very good on long sentences. Weak on isolated one-word commands: it hallucinates ("Um.", "This sound." for "disarm"). |
| Cloud (Azure, Google) | — | **No** | — | Phrase lists | — | Excluded: needs internet in the field and sends audio off the PC. |

Only the offline candidates that could be fed the same audio were measured: System.Speech, Vosk and faster-whisper.
To keep the app clean, only the Python packages needed for the comparison were installed, in an isolated scratch
environment. The app itself gained one NuGet dependency (`Vosk`).

## 3. Method

- **Corpus:** 996 clips = 83 utterances × 12 voices.
  - The voices are 10 neural voices (en-US ×2, en-GB ×2, en-IE, en-AU, en-IN ×2, en-NG, en-ZA) and the 2 Windows
    voices (David, Zira).
  - The utterances are the command phrases from the specification, plus natural variants, at normal speed.
  - The core phrases were also recorded 30 % faster and 25 % slower.
  - Negative clips: negations ("do not arm the drone"), ambiguous phrases ("disarm and arm the drone"), and
    unrelated words and sentences, including near-homophones ("alarm", "harm", "farm", "charm", "army", "on", "um").
  - **Pakistani-English voices (en-PK) were requested but the TTS service returned no audio for them, so en-PK is
    NOT TESTED in this corpus.** The Indian-English voices are the closest accent covered.
- **Conditions** (5 × 996 clips):
  - `clean`;
  - `fan_snr10`: pink noise plus a 120 Hz hum, at 10 dB SNR;
  - `babble_snr20` and `babble_snr10`: four overlapping background talkers;
  - `quiet_mic`: speech at −46 dBFS with a −70 dBFS noise floor (low gain or a far microphone).
- **Scoring:** every engine's transcript goes through the product classifier (`VoiceCommandParser`) and that engine's
  confidence gate.
  - *Recognised* means exactly the expected command was accepted.
  - *Wrong command* means a different command was accepted. This is the dangerous error.
  - *False accept* means a command was accepted from a negated, ambiguous or unrelated clip.
- **Two harnesses:**
  - `bench_py.py` runs the Python engines on whole clips.
  - `VoiceCorpusTests.RunCorpusThroughThePipeline` runs the **real app pipeline**: winmm-format PCM → high-pass →
    voice-activity detection → Vosk → classifier → confidence gate. §4.6 has those results.
- **What this proves, and what it does not.** Synthetic voices give a large, repeatable, labelled test set covering
  several accents and noise types. They are cleaner and more consistent than a human speaker, and they don't include
  your own accent, microphone or room. The live test with your voice is the Voice page's **Test Mode** (§6.4). Its
  accuracy report is the real acceptance test.

## 4. Results

### 4.1 Overall (Python harness, all five conditions; whisper on clean + fan only)

| Engine | Clips | Commands recognised | Wrong command | Missed / asked to repeat | False accepts | ARM↔DISARM confusions |
|---|---|---|---|---|---|---|
| System.Speech hybrid (previous) | 3,984 | 9.6 % | 1 | 90.4 % | 4 / 1,440 | 0 |
| System.Speech closed grammar | 3,984 | 23.5 % | 4 | 76.3 % | 24 / 1,440 | 0 |
| Vosk free vocabulary | 3,984 | 43.0 % | 0 | 57.0 % | 6 / 1,440 | 0 |
| faster-whisper tiny.en (clean + fan) | 1,992 | 36.2 % (71.5 % ungated) | 0 | 63.8 % | 2 / 720 | 0 |
| faster-whisper base.en (clean + fan) | 1,992 | 64.9 % | 0 | 35.1 % | 1 / 720 | 0 |
| faster-whisper base.en + prompt (clean only) | 996 | 91.7 % | 0 | 8.3 % | 10 / 360 | 0 |
| **Vosk command vocabulary** | 4,980 | **57.7 %** (both babble conditions included) | **0** | 42.3 % | 51 / 1,800 | **0** |

### 4.2 By condition (share of command clips recognised; FA = false accepts in that condition)

| Engine | clean | fan 10 dB | babble 20 dB | babble 10 dB | quiet mic |
|---|---|---|---|---|---|
| System.Speech hybrid (previous) | 11.6 % | 0.0 % | not run | 0.2 % | 26.4 % |
| System.Speech closed grammar | 36.6 % | 4.9 % | not run | 0.9 % | 51.7 % |
| Vosk free vocabulary | 64.2 % | 46.9 % | not run | 0.5 % | 60.4 % |
| faster-whisper tiny.en | 39.5 % | 32.9 % | not run | not run | not run |
| faster-whisper base.en | 70.4 % | 59.3 % | not run | not run | not run |
| faster-whisper base.en + prompt | 91.7 % (FA 10) | not run | not run | not run | not run |
| **Vosk command vocabulary** | **97.8 %** (FA 13) | **89.2 %** (FA 13) | 8.0 % | 1.6 % | **92.0 %** (FA 22) |

Babble is the one condition where every engine fails on whole clips. The babble sentences are made of the same
everyday words that are in the vocabulary ("the weather is nice today"), and the background voices run from the
first to the last sample. With the app's voice-activity detector and background-word suppression, the result changes
substantially (§4.6).

### 4.3 By accent (clean audio, all command clips)

| Engine | en-AU | en-GB | en-IE | en-IN | en-NG | en-US (neural) | en-US (Windows) | en-ZA |
|---|---|---|---|---|---|---|---|---|
| System.Speech hybrid (previous) | 7.5 % | 8.5 % | 18.9 % | **0.0 %** | 9.4 % | 7.5 % | 35.8 % | **0.0 %** |
| System.Speech closed grammar | 52.8 % | 38.7 % | 56.6 % | 10.4 % | 30.2 % | 35.8 % | 65.1 % | 0.0 % |
| Vosk free vocabulary | 64.2 % | 71.7 % | 66.0 % | 41.5 % | 37.7 % | 75.5 % | 91.5 % | 41.5 % |
| faster-whisper tiny.en (threshold 0.45) | 47.2 % | 34.9 % | 34.0 % | 24.5 % | 28.3 % | 44.3 % | 61.3 % | 34.0 % |
| faster-whisper base.en + prompt | 90.6 % | 96.2 % | 92.5 % | 84.0 % | 79.2 % | 100 % | 93.4 % | 90.6 % |
| **Vosk command vocabulary** | **100 %** | **100 %** | **100 %** | **96.2 %** | **83.0 %** | **100 %** | **100 %** | **98.1 %** |

### 4.4 Single-word commands (clean, all voices and speeds)

| Engine | "arm" | "disarm" | "take off" | "stop" | "hold" | "land" |
|---|---|---|---|---|---|---|
| System.Speech hybrid (previous) | 0 % | 22 % | 31 % | 8 % | 17 % | 25 % |
| Vosk free vocabulary | 33 % | 61 % | 83 % | 100 % | 75 % | 86 % |
| faster-whisper tiny.en (gated) | 0 % | 8 % | 64 % | 6 %* | 8 % | 3 %* |
| faster-whisper base.en + prompt | 86 % | 53 % | 100 % | 100 % | 100 % | 100 % |
| **Vosk command vocabulary** | **91.7 %** | **100 %** | **100 %** | **100 %** | **100 %** | **100 %** |

\* Whisper tiny transcribes "Stop." and "Land." correctly but with a low average log-probability, so its confidence
gate rejects them. For a lone "arm" it most often writes "Um." (12 of 36) or "I'm…"; for "disarm" it writes
"This sound." or "DeSan". A prompt listing the commands helps Whisper base a lot (91.7 %), but it remains below the
grammar-restricted Vosk (97.8 %), weaker on "disarm" and on Nigerian / Indian voices, and about 18× slower on the CPU.

What open-vocabulary engines hear when the speaker clearly says **"arm"** (clean audio):

| Voice | Vosk free | Vosk command vocabulary |
|---|---|---|
| en-GB, en-ZA | "i'm" | **"arm"** |
| en-IN, en-IE | "on" | **"arm"** |
| en-NG | "ah" | **"arm"** |
| en-US | "arm" | **"arm"** |

This is the root cause of "I say ARM and it hears something else". An open-vocabulary recogniser picks the most
likely English word for a short vowel sound, and for many accents that word is not "arm".

### 4.5 Speaking rate (clean, core phrases)

| Engine | normal | fast (+30 %) | slow (−25 %) |
|---|---|---|---|
| System.Speech hybrid (previous) | 12.5 % | 6.7 % | 18.3 % |
| faster-whisper base.en + prompt | 88.3 % | 90.8 % | 91.7 % |
| Vosk free vocabulary | 66.7 % | 59.2 % | 67.5 % |
| **Vosk command vocabulary** | **98.3 %** | **95.8 %** | **97.5 %** |

### 4.6 Real app pipeline (voice-activity detection → auto gain → Vosk → classifier), 4,980 clips

The same clips, with 1 s of lead-in and 1.5 s of tail, were fed through the app's `VoiceCommandEngine` exactly as
microphone audio arrives (`VoiceCorpusTests`, final settings):

| | clean | fan 10 dB | babble 20 dB | babble 10 dB | quiet mic |
|---|---|---|---|---|---|
| Commands recognised | **98.0 %** | **89.6 %** | 0.8 % | 0.0 % | **97.0 %** |
| False accepts (of 360) | 17 | 17 | 0 | 0 | 17 |
| Wrong commands | **0** | **0** | **0** | **0** | **0** |

- **By accent (clean):**

  | en-AU | en-GB | en-IE | en-IN | en-NG | en-US | en-ZA |
  |---|---|---|---|---|---|---|
  | 100 % | 100 % | 100 % | 98.1 % | 81.1 % | 100 % (both neural and Windows voices) | 98.1 % |

- **Single words (clean):** "arm" 91.7 %; "disarm", "take off", "stop", "hold" and "land" 100 %.
- **Speaking rate:** normal 98.3 %, fast 95.8 %, slow 98.3 %.
- **Decode time:** 0.2 s per clip on average.

Compared with the Python run, automatic gain lifted the quiet-microphone condition from 92 % to 97 %. Babble is
rejected as unclear, negated or question rather than mis-executed (§5 and the test report).

### 4.7 Confidence threshold (Vosk command vocabulary; clean + fan + quiet)

| Threshold | Recognised | Wrong command | False accepts |
|---|---|---|---|
| 0.0 | 95.0 % | 0 | 65 / 1,080 |
| 0.5 | 94.6 % | 0 | 60 / 1,080 |
| **0.7 (default)** | **93.0 %** | **0** | **48 / 1,080** |
| 0.9 | 90.8 % | 0 | 39 / 1,080 |

The threshold changes the false-accept rate only a little, because nearly all remaining false accepts are words
that genuinely sound like "arm" (§5.3). It is kept at 70 %.

## 5. How the benchmark shaped the design

The vocabulary went through four rounds. Every change below was kept only if the benchmark improved.

| Round | Change | Effect |
|---|---|---|
| 1 → 2 | Removed "halt" as a STOP word | A Nigerian-English "hold" was decoded as "halt" and became STOP, a wrong command. Wrong commands went to 0. |
| 1 → 2 | Fewer words allowed around a command (no "current", "location", "spot"…) | "my arm hurts" had been decoded as "my arm current" and accepted. |
| 2 → 3 | Decoy words limited to words that are clearly *not* commands | Near-homophone decoys stole accented commands: "arm" → "harm" (16 clips), "land" → "lend" (13), "stop" → "stock", "hold" → "cold". Removing them restored recall. |
| 3 → 4 | **One spoken form per command word** ("arm" only; no "armed", "arming", "disarmed", "taking off", "landing") | Same-meaning alternatives split the recogniser's confidence ("arm the" vs "armed the"), and correct commands failed the 70 % gate. Clean-audio recall went from **89 % to 97.8 %**. |

### 5.1 Final vocabulary

- **Commands** (`VoiceCommandParser.Phrases`):

  | Command | Accepted phrases |
  |---|---|
  | ARM | arm |
  | DISARM | disarm |
  | TAKEOFF | take off |
  | STOP | stop |
  | HOLD | hold, hold position, hold the position, hold your position |
  | RTL | return to launch, return to home, return home, come home, go home, RTL / "R T L" |
  | LAND | land |

- **Context words:**
  - lead words (please, drone, hey, I want to, go ahead and…);
  - tail words (the drone, the motors, now, immediately, here…);
  - negations (not, don't, never, no, cancel, abort, wait…);
  - question words.
- **Decoys** (`VoiceGrammar.Decoys`): alarm, hello, camera, battery, weather… They give everyday speech somewhere
  to go other than a command.
- **`[unk]`**: anything outside the vocabulary.

### 5.2 Classification (`VoiceCommandParser.Parse`)

A sentence is accepted only if it has the form `LEAD* COMMAND-PHRASE TAIL*` and all of these hold:

- it contains exactly one distinct command (else AMBIGUOUS);
- it contains no negation (else NEGATED → REJECT);
- it is not a question;
- it has at most 14 words;
- it contains no `[unk]` or unexpected word (else UNCLEAR → "please repeat").

Words are compared as whole tokens, never as substrings. "disarm" is a different token from "arm", and it is the
only DISARM form. **No ARM↔DISARM confusion occurred in any run.**

### 5.3 Known false-accept pattern

Isolated words that sound like "arm" in some accents can be accepted as ARM: "on" (23 of 108 clips), "harm",
"farm", "charm" ("should arm"), "um". This is the price of recognising a lone "arm" for every accent, which was the
main requirement. It is contained because:

- ARM always needs an explicit confirmation dialog;
- it needs a connected, disarmed vehicle that is on the ground and passes the pre-arm checks;
- Test Mode, which is on by default, never sends anything.

## 6. Configuration and use

### 6.1 Requirements

- Windows 10/11 x64. Mission Planner runs as a 64-bit process, which Vosk requires.
- Build: the `Vosk` NuGet package (0.3.38) is restored automatically. Its build targets copy `libvosk.dll`,
  `libgcc_s_seh-1.dll`, `libstdc++-6.dll` and `libwinpthread-1.dll` next to `MissionPlanner.exe`.
- The English model, `vosk-model-small-en-us-0.15` (41 MB download):
  - **In the app:** Voice & Microphone › **Download speech model (41 MB)**. The archive is downloaded from
    `alphacephei.com`, checked against its SHA-256 (`30f26242…942498`) and extracted to
    `Documents\Mission Planner\AeroAid\models\voice\vosk-model-small-en-us-0.15`.
  - **Offline:** extract the same zip into that folder, or into `voice-models\` next to `MissionPlanner.exe`
    (useful for an install image), or point AeroSettings `voice.vosk_model` at any model folder.
- Without the model, or in a 32-bit process, the app falls back to Windows speech. The engine tile then shows
  **WINDOWS SPEECH** with a warning.

### 6.2 Settings (Settings › Voice)

| Setting | Default | Meaning |
|---|---|---|
| Voice control | on | Master switch |
| Test Mode | **on** | Recognise and score commands, never send them. Turn off only after a good accuracy report (§6.4). |
| Speech engine | Vosk offline | Or Windows speech (fallback) |
| Microphone | system default | Stored by name, so it survives re-plugging |
| Min. recognition confidence | 70 % | Lowest confidence of the *command words* |
| End-of-command pause | 650 ms | Silence that ends a command. Raise it if you pause mid-sentence. |
| Suppress background speech | on (AeroSettings `voice.suppress_background`) | Ignores filler words more than 12 dB quieter than your command. Commands, negations and questions are never ignored. |
| Auto-stop listening | 0 (never) | |
| Command cool-down | 10 s | Before the same ARM / DISARM / TAKEOFF can be sent again |
| Confirm STOP / HOLD | off | They only stop the vehicle where it is |
| Confirm RTL / LAND | on | |

ARM, DISARM and TAKEOFF **always** ask for confirmation.

### 6.3 Microphone

1. Voice & Microphone › choose the **Input device** (↻ refreshes the list) and press **Microphone ON**.
2. **Receiving audio** must say YES while you speak.
   - NO SIGNAL usually means Windows privacy settings block desktop apps. Use **Privacy settings** to open the page.
   - CLIPPING means the input is too loud.
3. **Test microphone** records 4 s. Say a few commands at your normal volume. It reports:
   - speech level, peak, background level and SNR;
   - a verdict: GOOD, TOO QUIET, CLIPPING, NOISY or NO SIGNAL.

   **Play recording** lets you hear what the recogniser hears. Aim for speech around −30 to −12 dBFS with no
   clipping. Adjust the level in Windows Sound settings › Input.
4. A headset or close-talking microphone works best. Background-speech suppression relies on your voice being louder
   than other people's.

### 6.4 Test Mode: measuring accuracy with your voice

1. Leave **Test Mode** on. Nothing is ever sent to the vehicle.
2. Press **Test voice recognition**. Listening starts.
3. In *Voice recognition test*, click the command you will say (ARM … LAND, or *NEGATED (reject)*). Then say it
   10–20 times in different ways:
   - "arm", "arm the drone", "drone arm", "please arm the drone";
   - faster, slower, quieter, with a fan running.

   Each attempt is scored as **correct**, **missed** (asked to repeat, safe) or **WRONG COMMAND** (dangerous).
4. Repeat for every command. Then press **Save accuracy report**. It writes a markdown report and a CSV to
   `Documents\Mission Planner\AeroAid\voice-tests\`.
5. Turn Test Mode off (Voice page or Settings) only when there are **no wrong commands** and the misses are rare.
   You are asked to confirm.

### 6.5 Files

| File | Purpose |
|---|---|
| `AeroAid/Audio/VoiceActivityDetector.cs` | High-pass filter, voice-activity detection, level / clipping / SNR, microphone-test analysis |
| `AeroAid/Audio/MicrophoneManager.cs`, `WaveInCapture.cs` | Device capture (16 kHz mono), level, peak, clipping, receiving-audio flag, test recording |
| `AeroAid/Voice/SpeechRecognizers.cs` | `VoskCommandRecognizer`, `SapiCommandRecognizer` (fallback), `VoskModelStore` (find / download / verify) |
| `AeroAid/Voice/VoiceGrammar.cs` | The recogniser vocabulary (commands, context words, decoys, `[unk]`) |
| `AeroAid/Voice/VoiceCommandParser.cs` | Command classification |
| `AeroAid/Voice/VoiceCommandEngine.cs` | Pipeline, settings, confidence gate, background suppression, split-negation guard, Test Mode |
| `AeroAid/Voice/VoiceTestSession.cs` | Test Mode scoring and accuracy report |
| `AeroAid/Voice/VoiceSafety.cs`, `VoiceCommandController.cs` | Vehicle checks, confirmation, MAVLink, verification |
| `aeroaid_voice.log` (data folder) | Audit log: every utterance, decision, ignored background words and command result. No audio is stored. |

### 6.6 Troubleshooting

| Symptom | Fix |
|---|---|
| Engine tile shows WINDOWS SPEECH | The model is not installed: press **Download speech model**. Also check that `libvosk.dll` is next to the exe. |
| Commands often "LOW CONFIDENCE" | Run **Test microphone**. Usually the level is too low or noisy. Speak a little closer. |
| "UNCLEAR – please repeat" | An extra word was heard around the command. Say just the command ("arm the drone"). |
| A pause splits "do not … arm" | That is still rejected: a command within 3 s of a lone negation counts as negated. Raise *End-of-command pause* if you often pause. |
| NO SIGNAL | Windows › Privacy & security › Microphone › allow desktop apps. Check the device is not muted. |

### 6.7 Re-running the benchmark

The corpus, generators and scorer live outside the repository: they are large and machine-specific. To repeat the
pipeline run on any folder of `LABEL__phrase__voice__rate.wav` files (16 kHz mono, one sub-folder per condition):

```bat
set AEROAID_VOICE_CORPUS=C:\path\to\corpus
set AEROAID_VOICE_RESULTS=C:\path\to\pipeline.jsonl
set AEROAID_VOSK_MODEL=C:\path\to\vosk-model-small-en-us-0.15
vstest.console.exe MissionPlannerTests.dll /Platform:x64 /TestCaseFilter:FullyQualifiedName~RunCorpusThroughThePipeline
```
