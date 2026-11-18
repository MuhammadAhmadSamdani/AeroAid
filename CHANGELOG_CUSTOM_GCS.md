# AeroAid custom GCS — changelog

Branch `custom-gcs`, based on Mission Planner 1.3.83 (tag `original-baseline`). There is one commit per phase;
`git log --oneline original-baseline..custom-gcs` lists them.

## AEROAID logo replaces the ArduPilot logo (2026-11-18)

Details: `AEROAID_BRANDING_REPORT.md`. Single logo source: `Assets/Branding/aeroaid.png`.

| File | Change |
|---|---|
| `MainV2.cs` | Main-menu logo button: AEROAID wordmark instead of the ArduPilot logo (31 px high, aspect preserved) |
| `AeroAid/Branding/BrandingManager.cs` | Default logo `aeroaid.png`; transparent margin trimmed in memory; `GetLogo(maxW, maxH)` fit-within (never stretched or cropped); icon tile; wordmark-centred splash; `Reload()` |
| `AeroAid/UI/Sidebar.cs`, `AeroAid/Views/AboutView.cs` | Wordmark instead of square logo + name text |
| `Assets/Branding/branding.json`, `make-icon.ps1`, `app.ico`, `MissionPlanner.csproj` | Point at `aeroaid.png`; exe icon regenerated from it |
| `Assets/Branding/logo.png` | Removed (old placeholder); `aeroaid.png` is the only logo file |
| `Program.cs`, `README_CUSTOM_GCS.md` | Comments / documentation |
| `MissionPlannerTests/AeroAid/BrandingLogoTests.cs` (new) | Logo source, aspect ratio, transparency, icon, splash |

## Voice recognition rebuild (2026-11-17, second pass)

Root cause, engine comparison and results: `VOICE_RECOGNITION_ENGINE_COMPARISON.md` and `VOICE_CONTROL_TEST_REPORT.md`.

| File | Change |
|---|---|
| `MissionPlanner.csproj`, `MissionPlannerTests.csproj` | NuGet `Vosk` 0.3.38 (offline Kaldi recogniser; win-x64 native DLLs copied next to the exe) |
| `AeroAid/Voice/SpeechRecognizers.cs` (new) | `VoskCommandRecognizer` (command vocabulary, per-word confidence and timing), `SapiCommandRecognizer` (Windows speech fallback, closed grammar, **no dictation**), `VoskModelStore` (model lookup, SHA-256-verified download) |
| `AeroAid/Voice/VoiceGrammar.cs` (new) | Recogniser vocabulary: command phrases, context words, decoy words, `[unk]` |
| `AeroAid/Voice/VoiceCommandParser.cs` | Phrase-grammar classifier (`LEAD* COMMAND TAIL*`) for ARM / DISARM / TAKEOFF / **STOP / HOLD / RTL / LAND**; "my arm hurts" and "hold on" are unclear, not commands; one spoken form per command word |
| `AeroAid/Audio/VoiceActivityDetector.cs` (new) | 90 Hz high-pass, adaptive-noise-floor voice-activity detection with pre-roll, level / clipping / SNR, microphone-test analysis |
| `AeroAid/Voice/VoiceCommandEngine.cs` | New pipeline (VAD → recogniser → classifier → confidence of the command words), background-speech suppression, Test Mode, engine fallback, model preload |
| `AeroAid/Voice/VoiceTestSession.cs` (new) | Test Mode scoring per command and markdown/CSV accuracy reports |
| `AeroAid/Voice/VoiceCommandController.cs`, `VoiceSafety.cs` | STOP / HOLD / RTL / LAND (verified from the heartbeat mode), confirmation policy, Test Mode guard, STOP/HOLD/RTL/LAND may interrupt a takeoff climb check |
| `AeroAid/Audio/MicrophoneManager.cs` | dBFS level, peak, clipping and receiving-audio indicators; 4 s test recording |
| `AeroAid/Views/VoiceView.cs`, `SettingsView.Voice.cs` | Engine / listening / receiving-audio tiles, Start / Stop listening, Test microphone + playback, Test Mode, per-command test buttons, accuracy table and report, model download; new settings |
| `MissionPlannerTests/AeroAid/Voice*.cs` | Classifier, controller, engine, live-pipeline (Vosk and fallback), corpus and acoustic loop-back tests |

## Voice control fix (2026-11-17)

Details and test results are in `VOICE_CONTROL_TEST_REPORT.md`.

| File | Change |
|---|---|
| `AeroAid/Voice/VoiceCommandEngine.cs` | Speech-to-text instead of a whole-sentence, takeoff-only grammar: a keyword-anchored command grammar (with negations and filler words) runs alongside free dictation; partial results shown, final results processed once, confidence threshold, split-negation guard, continuous listening, microphone-loss handling |
| `AeroAid/Voice/VoiceCommandParser.cs` (new) | Normalisation and whole-word keyword matching for ARM / DISARM / TAKEOFF (priority DISARM, TAKEOFF, ARM); negation, question, ambiguity and length rejection |
| `AeroAid/Voice/VoiceSafety.cs` (new) | Per-command vehicle-state checks; DISARM refused while airborne (EXTENDED_SYS_STATE, altitude, climb rate) |
| `AeroAid/Voice/VoiceCommandController.cs` (new, replaces `VoiceTakeoffWorkflow.cs`) | Validation, confirmation, re-validation, MAVLink command, verification from ACK and telemetry; per-command cool-down; at most once per utterance |
| `AeroAid/Voice/VoiceLog.cs` (new) | `aeroaid_voice.log` audit log |
| `AeroAid/Views/VoiceView.cs`, `SettingsView.Voice.cs` | Microphone, recognition, detected-command and execution-status cards; new settings |
| `AeroAid/Flight/FlightControlManager.cs` | Failure messages now include the vehicle's refusal reason (it was read before the command was sent) |
| `AeroAid/Telemetry/TelemetryManager.cs` | `LandedState` (MAV_LANDED_STATE) in the snapshot |
| `MissionPlannerTests/AeroAid/Voice*.cs` | Keyword-matching, safety, controller, engine, live-pipeline speech, UI smoke and SITL tests |

## QA pass (2026-11-16)

Full results are in `QA_TEST_REPORT.md`. Fixes made during QA:

| File | Change |
|---|---|
| `ExtLibs/Utilities/Settings.cs` (stock) | A partially damaged `config.xml` is now backed up as `config.xml<time>.failed` before the next save can overwrite it (previously settings after the damage point were silently lost) |
| `AeroAid/UI/AeroScreen.cs` | Page header toolbar wraps below the title on narrow windows instead of overlapping it |
| `AeroAid/Views/SettingsView.cs` | Settings opens at the top (no focus-driven auto-scroll); section buttons scroll explicitly |
| `AeroAid/AeroShell.cs` | Minimum main-window size 1024×680 |
| `AeroAid/Camera/VideoStreamManager.cs`, `MainV2.cs`, `AeroAid/UI/*`, `MissionPlanner.csproj` | Camera start/stop/reconnect races, fast RTSP pre-check, stock top-bar buttons kept hidden, paint artifacts, exe icon from logo (earlier QA commit) |
| `MissionPlannerTests/AeroAid/*` | 47 automated tests, including restart/missing/corrupt config and synthesised-speech voice injection |

## AI Detection page (2026-11-15)

The Fire Detection page was transformed **in place** into **AI Detection**, a multi-category, multi-model monitoring
page. The fire/smoke functionality and its settings are preserved and migrated (`fire.*` keys become the defaults for
`ai.*`, and the old fire model becomes the first model entry).

| File | Change |
|---|---|
| `AeroAid/Views/FireDetectionView.cs` → `AiDetectionView.cs` | Redesigned page: alert banner, status tiles (AI engine, models, camera, live detections, active alerts, inference), category filter chips, live feed with category overlays, per-category status rows (operational / no model / filtered, live confidence, counts), detection history with thumbnails and a detail pane (captured frame, category, confidence, time, camera source, model, status, Acknowledge) |
| `AeroAid/Vision/FireDetectionManager.cs` → `AiDetectionManager.cs` | Multi-model engine (all enabled models per frame, background thread), category filters, per-category alert episodes with acknowledgement, captured frames, extended CSV |
| `AeroAid/Vision/FireAlertNotifier.cs` → `DetectionAlertNotifier.cs` | Category-specific, stacked, non-activating alert toasts ("Injured person detected"…) |
| `AeroAid/Views/SettingsView.Fire.cs` → `SettingsView.Ai.cs` | Model list (add/remove/enable, per-model status and categories), thresholds, alert options, mapping help |
| `AeroAid/Vision/DetectionCategories.cs` (new) | Category registry: Fire, Injured person, Flood, Damaged building, Smoke, Other emergency; keyword mapping; extra categories via `categories.json` |
| `AeroAid/Vision/AiSettings.cs` (new) | Model configuration (`ai.models`), category filters, options, migration of `fire.*` settings |
| `AeroAid/Vision/YoloOnnxDetector.cs` | Classes mapped to categories (keywords or explicit `class=category` in `<model>.labels.txt` / `labels.txt`); unrelated classes ignored |
| `AeroAid/AeroShell.cs`, `Views/DashboardView.cs`, `Views/SettingsView.cs`, `Input/KeyboardControlManager.cs`, `Views/ShortcutsView.cs`, `UI/AeroIcons.cs` | Sidebar "AI Detection" (blinks on unacknowledged alerts), Dashboard AI summary card, start-screen and keyboard screen lists updated (the old start-screen value is mapped to the new page) |

Verified with two test-fixture models, which are not shipped. Fire, smoke, injured person and flood were detected
together, a `car` class was ignored, and categories without a model showed "no model". The filter test removed a
category's overlay and ended its alert. Acknowledgement, history, thumbnails, the detail pane and the CSV all worked.
Real injured-person, flood and damaged-building models have **not** been integrated or tested.

## Version 1.0.0 (2026-11-13)

### Modified existing files (every hook is guarded with `#if !LIB` where the file is shared with the LIB build)

| File | Change |
|---|---|
| `Program.cs` | Applies AeroAid branding before the splash: app name, icon and splash image from `Assets/Branding`; splash title shows the AeroAid and Mission Planner versions |
| `Splash.cs` | Hides the stock labels when the AeroAid splash image (which includes the attribution) is used |
| `MainV2.cs` | `EnsureDefaultTheme()` on first run; `AeroShell.Install()` after the stock screens are registered; `ShowStartScreen()`; new `AeroNavigate()` that routes sidebar navigation (and Connect) through the original menu handlers |
| `ExtLibs/Controls/MyButton.cs` | New static `GlobalCornerRadius` (default 1 = original look) for rounded stock buttons; corners painted in the parent colour |
| `ExtLibs/Utilities/GStreamer.cs` | `RedactCredentials()`: camera passwords are removed from the pipeline log line, and the plain-text `pipeline.dot` dump is skipped when credentials are present |
| `MissionPlanner.csproj` | Copies `AeroAid.mpsystheme`, `Assets/Branding/**` and `COPYING.txt` to the output; adds `System.Security` (DPAPI) and the `Microsoft.ML.OnnxRuntime` 1.20.1 package |
| `MissionPlannerLib.csproj` | Excludes `AeroAid/**` from the Android/Mac LIB build |

### Added files

| File | Purpose |
|---|---|
| `Assets/Branding/logo.png` | Central logo, used everywhere (a placeholder; replace with your own) |
| `Assets/Branding/branding.json` | App name, tagline, version, company, corner radius, animations, palette |
| `AeroAid.mpsystheme` | Mustard/Black Mission Planner theme |
| `AeroAid/AeroShell.cs` | Integration with MainV2: screens, sidebar, background services, focus-loss safety |
| `AeroAid/AeroSettings.cs` | Persistent typed settings (`aeroaid.*`); DPAPI secrets; flight hard caps |
| `AeroAid/Branding/BrandingManager.cs`, `AeroPalette.cs` | Branding loader, scaled logos and icon, splash renderer, palette |
| `AeroAid/UI/AeroButton.cs` | Rounded, animated button (hover, press, active, disabled, glyph, hold events, alert colour; no Enter activation by default) |
| `AeroAid/UI/AeroCard.cs`, `StatusIndicator.cs`, `AeroDraw.cs`, `AeroIcons.cs`, `AeroScreen.cs`, `Sidebar.cs` | Cards, status tiles and level meters, drawing and styling helpers, icon glyphs, screen base class, navigation sidebar |
| `AeroAid/Telemetry/TelemetryManager.cs` | Telemetry snapshots and status grading from the existing `CurrentState` |
| `AeroAid/Flight/FlightControlManager.cs` | Velocity streaming (`SET_POSITION_TARGET_LOCAL_NED`, BODY_OFFSET_NED, mask 0x05C7), limits, modes, arm/disarm, takeoff, land, RTL, hold |
| `AeroAid/Flight/FlightActions.cs` | Confirmation dialogs and the RC-dependent-mode warnings |
| `AeroAid/Flight/FlightPad.cs` | Hold-to-move directional pad |
| `AeroAid/Input/ShortcutMap.cs`, `KeyboardControlManager.cs` | Configurable shortcuts and the gated keyboard control |
| `AeroAid/Camera/CameraSettings.cs` | Camera configuration (IMOU defaults) |
| `AeroAid/Camera/RtspProbe.cs` | RTSP DESCRIBE with Digest/Basic authentication, used for diagnostics |
| `AeroAid/Camera/OnvifClient.cs` | ONVIF SOAP client (device, media, PTZ) with WS-Security digest |
| `AeroAid/Camera/CameraConnectionManager.cs`, `CameraPtzManager.cs` | Capability probe and hold-to-move PTZ |
| `AeroAid/Camera/VideoStreamManager.cs`, `VideoView.cs`, `PtzPad.cs`, `CameraUi.cs` | Shared GStreamer stream (copied frames, reconnect, watchdog), video display and fullscreen, PTZ pad, GStreamer install and snapshot helpers |
| `AeroAid/Vision/YoloOnnxDetector.cs` | ONNX Runtime YOLO inference (v5, v8/v11, end-to-end), letterbox, NMS |
| `AeroAid/Vision/FireDetectionManager.cs`, `FireAlertNotifier.cs` | Background detection loop, alert episodes, history, screenshots, CSV log, overlay, toast and sound |
| `AeroAid/Audio/WaveInCapture.cs`, `MicrophoneManager.cs` | winmm microphone capture, device selection, level, diagnostics |
| `AeroAid/Voice/VoiceCommandEngine.cs`, `VoiceTakeoffWorkflow.cs` | System.Speech command recognition; checked and confirmed voice takeoff (replaced 2026-11-17, see above) |
| `AeroAid/Views/DashboardView.cs`, `VehicleMap.cs` | Dashboard and the branded vehicle map |
| `AeroAid/Views/FlightControlView.cs`, `TelemetryView.cs` | Flight Control and Telemetry screens |
| `AeroAid/Views/LiveCameraView.cs`, `CameraControlView.cs`, `FireDetectionView.cs` | Camera and fire screens |
| `AeroAid/Views/VoiceView.cs`, `ShortcutsView.cs`, `AboutView.cs` | Voice & Microphone, Controls & Shortcuts and About screens |
| `AeroAid/Views/SettingsView.cs` (+ `.Camera.cs`, `.Fire.cs`, `.Voice.cs`) | Settings screen and its sections |
| `README_CUSTOM_GCS.md`, `CHANGELOG_CUSTOM_GCS.md` | Documentation |

### Safety-relevant fixes made during testing

- Command buttons no longer activate on Enter. A stray keystroke after a focus change had selected LOITER in SITL.
- "Hold" now means GUIDED position hold instead of LOITER, which needs RC throttle input.
- The heartbeat age is now computed in UTC. `MAVState.lastvalidpacket` is UTC, so comparing it with local time had
  shown "No heartbeat".
- The speech-recogniser input stream no longer returns short reads, which the engine treats as end-of-stream.
