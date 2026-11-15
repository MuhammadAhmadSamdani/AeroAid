# AeroAid custom GCS — changelog

Branch `custom-gcs`, based on Mission Planner 1.3.83 (tag `original-baseline`). There is one commit per phase;
`git log --oneline original-baseline..custom-gcs` lists them.

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
| `AeroAid/Voice/VoiceCommandEngine.cs`, `VoiceTakeoffWorkflow.cs` | System.Speech command recognition; checked and confirmed voice takeoff |
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
