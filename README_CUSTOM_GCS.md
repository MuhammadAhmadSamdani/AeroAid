# AeroAid — Custom Drone Ground Control Station

AeroAid is a branded, extended build of **Mission Planner** (the ArduPilot ground control station).
All stock Mission Planner functions are still available: MAVLink connections, mission planning, the HUD
and map, parameters, setup/calibration, firmware and SITL. AeroAid adds a new interface on top, plus
button and keyboard flight control, offline voice commands (ARM / DISARM / TAKEOFF / STOP / HOLD / RTL / LAND),
IMOU/ONVIF camera integration and multi-category AI detection.

> AeroAid is a derivative of Mission Planner (GPLv3). See [COPYING.txt](COPYING.txt) and the About screen.

---

## 1. Overview

| Area | What you get |
|---|---|
| Branding | One logo file (`Assets/Branding/aeroaid.png`, the AEROAID wordmark) and one config (`branding.json`) drive the name, version, colours, window/taskbar icon, splash, sidebar and About screen |
| Theme | Mustard/black palette (`AeroAid.mpsystheme`), rounded animated buttons, cards and status tiles |
| Navigation | Left sidebar: Dashboard, Flight Control, Mission Planner, Live Camera, AI Detection, Telemetry, Camera Control, Voice & Microphone, plus the stock Mission Planner tools, Settings, Controls & Shortcuts, About |
| Flight | Hold-to-move pad (MAVLink `SET_POSITION_TARGET_LOCAL_NED`), flight-mode selector built from the vehicle's mode list, Arm/Disarm, Take off, Land, RTL, Hold position |
| Voice | Offline Vosk command recogniser (voice-activity detection, command vocabulary) → phrase classifier for **ARM / DISARM / TAKEOFF / STOP / HOLD / RTL / LAND** → vehicle-state checks → confirmation → MAVLink → result verified from ACK and telemetry. **Test Mode** (default) never sends anything |
| Camera | RTSP live video (GStreamer), ONVIF capability detection, PTZ pad (only enabled for features the camera reports), snapshot, fullscreen |
| AI detection | Multi-category, multi-model YOLO (ONNX) detection on the live video (fire, injured person, flood, damaged building, smoke, other emergencies), run in the background, with filters, overlays, per-category alerts, acknowledgement, history, captured frames and a CSV log |
| Keyboard | Configurable shortcuts with key-down/key-up movement and focus-loss safety |

## 2. New features (by screen)

- **Dashboard.** Connection, vehicle type, mode, armed state, battery % and voltage, GPS fix and satellites, EKF and
  pre-arm status (green/yellow/red/grey). Latitude, longitude, altitude, ground and air speed, heading, roll/pitch/yaw
  and climb rate. A live map using Mission Planner's map provider and cache, quick controls, a camera preview and the
  AI-detection status. All values are live MAVLink data from `MainV2.comPort.MAV.cs`.
- **Flight Control.** A directional pad (forward, back, left, right, up, down, yaw left/right, STOP), the vehicle's
  flight modes (current mode highlighted), Arm / Disarm / GUIDED / Hold position / Take off (altitude field) / Land / RTL,
  the active limits and a command log.
- **Live Camera.** Start/stop, main or sub stream, camera-audio playback, fullscreen, snapshot, stream status and errors.
- **Camera Control.** The PTZ pad (up, down, left, right, zoom ±, center), PTZ speed, detected capabilities and device
  information, and an explanation of the audio channels.
- **AI Detection.** Live feed with category-coloured boxes. Above it: an alert banner (red until acknowledged) and status tiles
  (AI engine, models, camera, live detections, active alerts, inference time). Category filter chips: All, Fire,
  Injured person, Flood, Damaged building, Smoke, Other. A per-category status panel shows Operational vs "no model",
  live confidence and event counts. Detection history has thumbnails and a detail pane (captured frame, category,
  confidence, time, camera source, model, status, Acknowledge).
- **Telemetry.** Link health (heartbeat age, link quality, port, system/component IDs, firmware), vehicle state,
  GPS, battery, an error banner and the vehicle's system messages.
- **Voice & Microphone.** Microphone selection, ON/OFF, **Test microphone** (level, noise, SNR, clipping, playback),
  receiving-audio indicator and live level; speech engine and listening state, Start / Stop listening, live and final
  transcript; the detected command with its confidence and validation result; the execution result; **Test Mode** with a
  test button per command, per-command accuracy and a saved accuracy report; and a voice log.
- **Settings.** Sections for Appearance (theme, animations, sidebar, start screen), Flight controls (speed and
  altitude limits, takeoff altitude, voice pre-flight minimums, confirmations, keyboard), Camera, AI detection (models, thresholds, alerts) and Voice.
- **Controls & Keyboard Shortcuts.** A reference and editor for every key, with conflict checks and Restore defaults.
- **About.** Logo, versions, and the GPLv3 / Mission Planner / ArduPilot attribution.

## 3. Project architecture

AeroAid lives in `AeroAid/` inside the existing `MissionPlanner.csproj`, so there is no second application and no
second MAVLink stack. It hooks into Mission Planner at a few `#if !LIB` points:

```
Program.cs   ── AeroShell.ApplyProgramBranding()  (name, icon, splash)
MainV2.cs    ── AeroShell.EnsureDefaultTheme()     (first run → Mustard/Black)
             ── AeroShell.Install(this, MyView)    (screens + sidebar + background services)
             ── AeroShell.ShowStartScreen()        (open Dashboard)
             ── AeroNavigate(name)                 (sidebar → stock menu handlers incl. their checks)
```

| Module (folder) | Responsibility |
|---|---|
| `Branding/BrandingManager`, `AeroPalette` | Loads `branding.json` and `aeroaid.png`; aspect-preserving fitted logos, icon tile, splash image |
| `AeroSettings` | Typed preferences in Mission Planner's `config.xml` (`aeroaid.*` keys); DPAPI-encrypted secrets |
| `UI/*` | AeroButton (rounded, animated, hold events), AeroCard, StatusTile, LevelMeter, Sidebar, AeroScreen base |
| `Telemetry/TelemetryManager` | 5 Hz snapshots of the existing `CurrentState`, with status grading |
| `Flight/FlightControlManager`, `FlightActions`, `FlightPad` | Velocity streaming, modes, arm/disarm, takeoff/land/RTL, confirmations |
| `Input/KeyboardControlManager`, `ShortcutMap` | Gated `IMessageFilter` keyboard control and the configurable key map |
| `Camera/*` | `VideoStreamManager` (GStreamer), `RtspProbe`, `OnvifClient`, `CameraConnectionManager`, `CameraPtzManager`, `VideoView` |
| `Vision/*` | `DetectionCategories` (category registry), `AiSettings` (models and options), `YoloOnnxDetector` (ONNX Runtime), `AiDetectionManager` (multi-model background loop, per-category alert episodes), `DetectionAlertNotifier` |
| `Audio/*`, `Voice/*` | `WaveInCapture` (winmm), `MicrophoneManager`, `VoiceActivityDetector`, `VoiceCommandEngine` (pipeline), `SpeechRecognizers` (Vosk; Windows speech fallback), `VoiceGrammar`, `VoiceCommandParser` (classifier), `VoiceSafety`, `VoiceCommandController` (confirmation + MAVLink), `VoiceTestSession` (Test Mode), `VoiceLog` |
| `Views/*` | The AeroAid screens (registered with Mission Planner's `MainSwitcher`) |

Data flow: the transport (`ExtLibs/Comms`) feeds `MAVLinkInterface`, which feeds `MAVState.cs`. From there,
`TelemetryManager` drives the UI, and the flight managers send commands back through the **same** `MainV2.comPort`.

`AeroAid/` is Windows-desktop only, so it is excluded from the Android/Mac `MissionPlannerLib.csproj` (LIB) build.

## 4. How to build and run

Requirements: Windows 10/11 x64. **Either** Visual Studio 2022 (import `vs2022.vsconfig`) **or** just the
.NET 8 SDK (this is how this branch was built, without VS or admin rights).

```bat
git submodule update --init          :: or: git clone --depth 1 https://github.com/meee1/mono.git ExtLibs/mono
dotnet build MissionPlanner.csproj -c Release
:: first build ~10 min; later main-project rebuilds:
dotnet build MissionPlanner.csproj -c Release -p:BuildProjectReferences=false
bin\Release\net461\MissionPlanner.exe
```

With Visual Studio / MSBuild, use the normal `msbuild -restore -p:Configuration=Release MissionPlanner.sln`.

Branch and backup: work is on git branch **`custom-gcs`**. The untouched original is on `master`, tagged
`original-baseline`. An offline ZIP of the original is at `..\..\MissionPlanner-master-ORIGINAL-backup-2026-11-13.zip`.
Restore everything with `git checkout original-baseline`; review the changes with `git diff original-baseline`.

## 5. Required dependencies

| Dependency | Used for | How it is obtained |
|---|---|---|
| .NET Framework 4.7.2 runtime | Application | Part of Windows 10/11 |
| GStreamer 1.x (x64) | Live video | Offered in-app ("Live video requires GStreamer…"). Downloads Mission Planner's package (~120 MB) to `C:\ProgramData\Mission Planner\gstreamer` |
| `Microsoft.ML.OnnxRuntime` 1.20.1 (NuGet) | AI detection inference | Restored at build time; `onnxruntime.dll` ships next to the exe |
| Trained YOLO ONNX model(s) per category | AI detection | **You must provide them** (see §8) |
| `Vosk` 0.3.38 (NuGet, Apache-2.0) | Voice command recognition | Restored at build time; `libvosk.dll` + MinGW runtime DLLs ship next to the exe |
| `vosk-model-small-en-us-0.15` (41 MB) | Voice command recognition | Downloaded in-app (Voice page), SHA-256 verified |
| Windows speech recogniser (en-US) | Voice fallback only | Installed with Windows |
| Microsoft Visual C++ 2015–2022 x64 redistributable | ONNX Runtime | Usually already installed |

## 6. Camera configuration

Go to **Settings › Camera**:

1. **Camera IP address.** Find it in the Imou Life app (device details) or your router's client list. Give the camera a
   **fixed IP / DHCP reservation**.
2. **RTSP port** 554 and **ONVIF port** 80 (the defaults).
3. **Username:** `admin`. **Password:** see §7. It is stored **encrypted with Windows DPAPI** for your Windows user,
   never shown, and redacted from logs.
4. **RTSP path:** the default is `/cam/realmonitor?channel=1&subtype={subtype}`, where `{subtype}` is 0 for the main
   stream and 1 for the sub stream. You can instead enter a full RTSP URL (without the password).
5. Click **Save & test connection**. It reports whether the camera is reachable, whether authentication succeeded,
   whether the stream path exists, the video and audio codecs, ONVIF status, and PTZ support.
6. Open **Live Camera** and click **Start stream**. Main stream gives full quality; sub stream gives lower latency and
   bandwidth. The latency buffer (default 200 ms) and TCP transport are in Settings.

**Custom pipeline (advanced).** This replaces the RTSP source with any GStreamer pipeline, for example a UDP H.264
stream from a companion computer. It must end with `videoconvert ! video/x-raw,format=BGRA ! appsink name=outsink`.

## 7. IMOU camera compatibility (model K2EP-5R1W)

No public documentation for the K2EP's RTSP, ONVIF or PTZ support could be found, so AeroAid **detects** what the
camera supports instead of assuming it:

- **RTSP:** Most IMOU cameras serve `rtsp://IP:554/cam/realmonitor?channel=1&subtype=0`. On many models RTSP/ONVIF
  must first be **enabled in the Imou Life app** (Device settings › … › ONVIF/RTSP). The password is either the
  RTSP/ONVIF password you set there or the **safety code printed on the camera label**. Some users report RTSP only
  responds after the Imou Life app has been opened once.
- **ONVIF / PTZ:** If the camera exposes an ONVIF PTZ service, the PTZ pad is enabled for the features it reports
  (pan/tilt, zoom, home). Otherwise the pad is disabled and the reason is shown. Imou's cloud-only PTZ API is **not** used.
- **Audio:** If the RTSP stream contains an audio track, you can play the camera's microphone (Live Camera ›
  options). **Two-way audio** (talking through the camera) is **not implemented**. It would need an ONVIF Profile-T
  audio back-channel, which is unconfirmed for this model.
- **Security:** Don't share the safety code or put it in screenshots or source files. AeroAid never stores it in plain text.

## 8. AI detection model setup

AeroAid does **not** use colour heuristics or simulated results. A category only reports detections, and only shows
as operational, when a loaded model covers it. Otherwise it shows "no model" and detects nothing.

| Category | Id | Matched class-name keywords |
|---|---|---|
| Fire | `fire` | fire, flame, blaze, wildfire |
| Smoke | `smoke` | smoke |
| Injured person | `injured_person` | injured, casualty, victim, wounded, person_down, fallen_person, lying_person |
| Flood | `flood` | flood, inundat…, standing_water, submerged |
| Damaged building | `damaged_building` | damaged, collapse…, rubble, debris, destroyed, building_damage |
| Other emergency | `other_emergency` | emergency, accident, crash, distress, hazard, sos |

Classes matching no category (for example `car` or `person`) are ignored.

1. Get or train YOLO models for the categories you need. A common route is Ultralytics YOLOv8/YOLO11 trained on public
   datasets (fire/smoke: D-Fire; flood and building damage: aerial disaster datasets such as FloodNet or xBD; injured
   persons: dedicated casualty or "person lying" data). Check that dataset and weights licences allow your use.
2. Export each model to ONNX: `yolo export model=best.pt format=onnx imgsz=640 opset=12`.
3. Add them in **Settings › AI detection › Add model…**. The default fire model location
   `Documents\Mission Planner\AeroAid\models\fire_smoke.onnx` is still used, and settings from the earlier
   fire-only version are migrated. Each model can be enabled or disabled, and all enabled models run on every frame.
4. Class names come from `<model>.labels.txt` or `labels.txt` (one class per line), or from the model metadata.
   **Explicit mapping:** write `class_name=category_id`, for example `person_lying=injured_person` or
   `water=flood`.
5. **New categories without code changes:** create `categories.json` in the models folder, for example
   `[{"id":"vehicle_accident","name":"Vehicle accident","color":"#FF8800","keywords":["crash","accident"]}]`.
   It gets its own filter chip, status row, alert and history entries.
6. Supported output layouts: YOLOv8/v11 `[1, 4+nc, N]`, YOLOv5/v7 `[1, N, 5+nc]`, and end-to-end exports `[1, ≤300, 6]`.
   The input must be `[1,3,H,W]`.

Tuning (Settings › AI detection): global confidence threshold, analysis rate (CPU load grows with each model),
confirm frames, alert clear time (one alert per continuous event per category), captured frames and sound.
The filter chips on the AI Detection page switch categories on or off; a disabled category is neither drawn nor
alerted. Alerts, captured frames and `detections.csv` (category, confidence, time, duration, camera source, model,
classes, screenshot) are saved in `Documents\Mission Planner\AeroAid\detections\`.
## 9. Voice recognition setup

Voice control uses the offline **Vosk** recogniser restricted to the command vocabulary. Why it was chosen over Windows
speech and Whisper is in `VOICE_RECOGNITION_ENGINE_COMPARISON.md`; test results are in `VOICE_CONTROL_TEST_REPORT.md`.

1. **Speech model (once):** Voice & Microphone › **Download speech model (41 MB)**. It is checksum-verified and stored in
   `Documents\Mission Planner\AeroAid\models\voice\`. Without it the app falls back to Windows speech (less accurate);
   the *Speech engine* tile shows which engine is active.
2. Allow desktop apps to use the microphone: Settings › Privacy & security › Microphone (**Privacy settings** button).
3. Choose the microphone, press **Microphone ON**, then **Test microphone**. It records 4 s and reports level, noise, SNR
   and clipping; **Play recording** plays it back. "Receiving audio" must show YES while you speak.
4. **Test Mode is on by default.** Nothing is sent to the vehicle. Press **Test voice recognition**, click a command
   (ARM … LAND, or NEGATED), say it repeatedly, then **Save accuracy report**. Turn Test Mode off only when the report
   shows no wrong commands.
5. Commands (say "drone …" or "please …" around them if you like):

   | Say | Command | Confirmation |
   |---|---|---|
   | "arm", "arm the drone", "drone arm" | ARM | always |
   | "disarm", "disarm the drone", "drone disarm" | DISARM | always; refused while airborne |
   | "take off", "drone take off", "take off now" | TAKEOFF | always; vehicle must be armed |
   | "stop" | STOP (BRAKE, or hold) | optional (off) |
   | "hold position", "hold" | HOLD (GUIDED hold) | optional (off) |
   | "return to launch", "return home", "RTL" | RTL | on by default |
   | "land" | LAND | on by default |

   Rejected, never executed:
   - negations ("do not arm the drone");
   - two commands ("disarm and arm the drone");
   - questions;
   - sentences that merely contain a command word ("my arm hurts", "hold on");
   - low confidence ("please repeat").
6. Every command is checked against the vehicle state, confirmed where required, sent over the existing
   `MainV2.comPort` link, and reported as completed only when the vehicle's ACK **and** its reported state (armed flag,
   mode, climb) confirm it.
7. Settings › Voice: engine, Test Mode, microphone, minimum confidence (70 %), end-of-command pause (650 ms), auto-stop,
   cool-down, STOP/HOLD and RTL/LAND confirmation.
8. Every utterance, decision and result is logged to `aeroaid_voice.log` in the Mission Planner data folder. No audio
   is stored.

## 10. Drone connection

Use the connection controls at the top right: choose **COM port** (USB / telemetry radio), **TCP** or **UDP** and
the baud rate, then **Connect** (or use Connect on the Dashboard/Telemetry screens, or F12). Mission Planner also
auto-connects to MAVLink on UDP 14550. The Telemetry screen shows heartbeat age, link quality and system messages.

**Test in SITL first.** Use Mission Planner tools › Simulation (SITL) before connecting real hardware.

## 11. Keyboard shortcut reference

| Drone | Key | | Flight modes | Key | | Camera | Key |
|---|---|---|---|---|---|---|---|
| Forward | W | | Stabilize | 1 | | Camera up | ↑ |
| Backward | S | | AltHold | 2 | | Camera down | ↓ |
| Left | A | | Loiter | 3 | | Camera left | ← |
| Right | D | | Guided | 4 | | Camera right | → |
| Up | R | | Auto | 5 | | Zoom in | + |
| Down | F | | RTL | 6 | | Zoom out | − |
| Rotate left | Q | | Land | 7 | | Center | C |
| Rotate right | E | | | | | | |
| Stop / neutral | Space | | | | | | |

All keys can be changed on **Controls & Keyboard Shortcuts**, which also has Restore defaults. F2–F5, F12, Tab,
Enter and Esc are reserved. Keys only work while the AeroAid window is focused, on Dashboard, Flight Control, Live
Camera, Camera Control and AI Detection, and never while typing in a text field.

## 12. Safety precautions

- Movement commands are **refused** unless ArduCopter is connected (live heartbeat), **armed, in GUIDED, airborne**
  and not in failsafe.
- Speeds are bounded by Settings, which are themselves clamped to hard caps: 5 m/s horizontal, 2.5 m/s vertical, 90°/s yaw.
- Climbing stops at the max altitude (default 30 m). Descending stops at the min altitude (default 2 m); use **LAND**
  to land.
- Velocity is streamed at 5 Hz **only while a control is held**. Releasing it sends zero velocity (hold). Losing mouse
  capture, window focus or the screen also stops it, and ArduCopter itself stops if updates cease.
- Arming is never forced, and ArduPilot pre-arm checks, failsafes and geofences are never bypassed. Disarm is not
  forced either, so ArduPilot refuses to disarm a flying copter.
- Confirmation dialogs are shown for Arm, Disarm, Take off, Land, RTL, and every mode change while armed except
  GUIDED/BRAKE.
- **LOITER / ALT_HOLD / POSHOLD / STABILIZE take throttle from the RC transmitter.** Without an RC transmitter the
  vehicle can descend in these modes, so AeroAid warns about it. **Hold position** uses GUIDED, which needs no RC input.
- Command buttons don't react to the Enter key, so a stray keystroke can't trigger a vehicle command.
- Keep an RC transmitter with a trained safety pilot available, follow local regulations, and test new configurations
  in SITL first.

## 13. Known limitations

- **Verification status (2026-11-13):**
  - **Tested in ArduCopter SITL (4.7.3):** MAVLink velocity semantics (frame, signs, limits), the pad, keyboard,
    modes, arm/disarm, takeoff/land/RTL, the safety gating, and the voice pre-flight checks.
  - **Tested against a local mock camera:** the RTSP probe, ONVIF and PTZ protocol code.
  - **Tested with a GStreamer test pattern:** the live-video path.
  - **Tested with a synthetic test-fixture ONNX model:** the AI-detection pipeline (two models; fire, smoke, injured person, flood, and ignored classes). This checks decoding and the
    alert flow, not detection quality.
  - **Tested with synthesised speech (12 voices, 7 English accents, noise) and an acoustic speaker → microphone loop:**
    voice recognition. **Not yet tested with a live human voice**: use Test Mode (§9).
  - **Nothing has been tested on real hardware:** not with a real drone, not with the IMOU K2EP, not with real detection models.
- Voice control has been tested with synthesised speech and a speaker-to-microphone loop, not yet with a live human
  voice; see `VOICE_CONTROL_TEST_REPORT.md`. Isolated words that sound like "arm" ("on", "harm") can be heard as ARM, and ARM
  always asks for confirmation.
- Button and keyboard movement supports **ArduCopter only**. Planes and rovers keep all stock Mission Planner functions.
- IMOU PTZ and two-way audio depend on the camera's ONVIF support (see §7).
- AI detection needs models you supply (§8) and runs on the CPU. Injured person, flood, damaged building and other-emergency detection are **architecture-ready but not operational** until such models are added and validated. Analysis rate trades accuracy against CPU load.
- GStreamer's very first start can take up to a minute while it builds its plugin cache.
- Speech recognition is English-only (offline Vosk model). Accuracy depends on the speaker's voice, the microphone and
  background noise; loud background conversation makes commands "unclear" (asked to repeat), never a different command.
- The stock Mission Planner screens keep their own layouts; they adopt the Mustard/Black colours through the theme.
- The logo is the AEROAID wordmark `Assets/Branding/aeroaid.png` (transparent PNG). It is wide (about 4:1), so in the
  square window/taskbar icon it is small: legible from 48 px, only a mark at 16–32 px. A square symbol version could be
  added later for the icon only. See `AEROAID_BRANDING_REPORT.md`.
