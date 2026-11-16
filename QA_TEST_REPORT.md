# AeroAid GCS — QA test report

| | |
|---|---|
| Product | AeroAid 1.0.0 (custom Mission Planner 1.3.83 build), branch `custom-gcs` |
| Test period | 2026-11-15 – 2026-11-16 |
| Platform | Windows 11 Pro (26200), 125 % display scaling, .NET 8 SDK user install (no Visual Studio) |
| Vehicle | ArduCopter SITL (Windows build) — **no real drone** |
| Camera | Local RTSP mock server + GStreamer `videotestsrc` pipeline — **real IMOU K2EP-5R1W not verified by QA** |
| AI models | Two test-fixture ONNX models (not shipped) — **no production model** |

Status legend: **PASS** works as specified · **FAIL** defect still open · **FIXED** defect found during QA and fixed
(re-tested PASS) · **BLOCKED** cannot be tested in this environment · **NOT IMPLEMENTED** out of scope / not built.

## Summary

| Area | Result |
|---|---|
| A. Build | PASS — 0 errors, 0 warnings in AeroAid code |
| B. Automated tests | PASS — 47/47 AeroAid tests; stock suite failures are pre-existing / network |
| C. Functional (UI, flight, camera, AI, voice, branding, settings) | PASS in SITL / mock — real hardware BLOCKED |
| D. Security & privacy | PASS |
| E. Performance & stability | PASS |
| F. Defects | 11 found and fixed, 0 open blocking; 3 low-priority observations |

**Not release-ready for field use until the BLOCKED hardware items in C are tested** (real drone, real camera,
real detection model, live microphone).

---

## A. Build

| # | Check | Result | Notes |
|---|---|---|---|
| A1 | `dotnet clean` + `dotnet build` (Release) | PASS | 0 errors. 1448 warnings total, identical to the untouched baseline (`original-baseline`) |
| A2 | Warnings in AeroAid code | PASS | 0 |
| A3 | `--no-incremental` rebuild | PASS (env) | Fails in stock GDAL (`GdalConfiguration` content-file race) on baseline too — not an AeroAid defect |
| A4 | Vulnerable NuGet packages | Noted | Stock dependencies, unchanged: log4net 2.0.13, SharpCompress 0.29, ImageSharp 2.1.12, SkiaSharp 2.80.2, SSH.NET 2020.0.2. Upgrading is an upstream decision |
| A5 | Exe icon from central logo | FIXED | Was the Mission Planner icon (BRANDING-1) |
| A6 | Original MP sources excluded by upstream `.gitignore` | FIXED | Committed so the branch builds from a clean clone (REPO-1) |

## B. Automated tests

MSTest, `MissionPlannerTests/AeroAid/`, isolated temporary settings directory (never touches the user's config).

| # | Suite | Result |
|---|---|---|
| B1 | Flight control (MAVLink command generation, speed/alt caps, gating) | PASS |
| B2 | Keyboard shortcuts (mapping, conflicts, safety gating) | PASS |
| B3 | Camera (RTSP probe OK/auth/404/unreachable, URL building w/o credentials, log redaction, pipeline validation) | PASS |
| B4 | Settings (DPAPI secrets, tampered secret, persistence, clamping) | PASS |
| B5 | Settings restart from disk, **missing** config, **corrupt** config | PASS (new) — found CFG-1, fixed |
| B6 | AI detection (category mapping, multi-model, filters, alerts, migration of fire settings) | PASS |
| B7 | Voice readiness checks, grammar, bounded settings, microphone selection | PASS |
| B8 | Voice injection (Windows TTS → recogniser with the app's grammar, format and threshold) | PASS (new) — "take off", "drone take off", "initiate takeoff" accepted at 95 %; "land now", "return to launch", "disarm the motors", "what is the battery level", "hello there" all rejected |
| | **Total AeroAid** | **47 / 47 PASS** |
| B9 | Stock Mission Planner suite (47 tests) | 37 pass / 10 fail — all failures are environmental and occur on the untouched baseline: DetectBoard ×6 ("ShowEvent Not Set"), GitHub API 403 rate limit ×2, internet download tests ×2 (one timeout) |

## C. Functional

### C1 Navigation & UI

| # | Check | Result | Notes |
|---|---|---|---|
| C1.1 | All 16 sidebar pages open | PASS | ~1.4 s for the full round |
| C1.2 | Stock top-bar view buttons stay hidden | FIXED | UI-1: re-appeared after a layout refresh. Re-tested by switching Config › Planner › Layout Basic ↔ Advanced: stay hidden |
| C1.3 | Paint artifacts on transparent controls | FIXED | UI-2 |
| C1.4 | Window resize (Live Camera, Dashboard, Flight Control, Telemetry, Settings at min / 1280 / 1920 width) | FIXED | UI-3: header buttons overlapped page title below ~1100 px; UI-4: no minimum window size. Now wrap to a second row; minimum 1024×680 (DPI-scaled). No crash, app responsive |
| C1.5 | Settings page opens at the top | FIXED | UI-5: opened scrolled to *AI detection* |
| C1.6 | Fullscreen video | PASS | |

### C2 Vehicle connection & flight (ArduCopter SITL)

| # | Check | Result |
|---|---|---|
| C2.1 | Connect UDP 14550 and TCP | PASS |
| C2.2 | Link loss / reconnect ×3 | PASS |
| C2.3 | Arm, takeoff, movement pad, keyboard flight, mode keys (with confirmation when armed), land | PASS |
| C2.4 | MAVLink protocol checks (observer on SITL serial1) | PASS 12/12 |
| C2.5 | Real drone, real RC, real GPS/EKF behaviour | **BLOCKED** — no hardware |

### C3 Camera

| # | Check | Result | Notes |
|---|---|---|---|
| C3.1 | Wrong password → clear message | PASS | 1.3 s |
| C3.2 | Unreachable camera → clear message + back-off retries | FIXED → PASS | CAM-1 (slow / lost diagnosis); now 3.6 s |
| C3.3 | Stop while reconnecting | FIXED | CAM-2 |
| C3.4 | Start/stop ×20 + 10 rapid toggles (Stop during Starting) | FIXED → PASS | CAM-3 (late "Playing" after Stop). 20/20 cycles clean, ends *Stopped*, no late Playing |
| C3.5 | PTZ via mock ONVIF; wrong ONVIF password disables PTZ | PASS | |
| C3.6 | Real IMOU K2EP-5R1W video, PTZ, ONVIF | **BLOCKED** | Application log shows successful RTSP DESCRIBE (H.265) against the configured camera address, but video/PTZ on the real camera were not verified by QA |

### C4 AI detection

| # | Check | Result |
|---|---|---|
| C4.1 | Two fixture models together: fire, smoke, injured person, flood detected; unrelated class ignored; "no model" shown for others | PASS |
| C4.2 | Filters, alerts, acknowledge, history, thumbnails, CSV | PASS |
| C4.3 | Production fire / injured-person / flood / damaged-building models | **BLOCKED** — no trained models supplied |

### C5 Voice

| # | Check | Result |
|---|---|---|
| C5.1 | Takeoff only after readiness checks + arm + takeoff confirmations | PASS (unit + SITL) |
| C5.2 | Synthesised speech injection | PASS (B8) |
| C5.3 | Live microphone in a noisy outdoor environment | **BLOCKED** |

### C6 Branding

| # | Check | Result |
|---|---|---|
| C6.1 | Replace `Assets/Branding/logo.png` (no rebuild) → sidebar, About page, window icon change | PASS |
| C6.2 | Missing logo / corrupt logo file → app starts, logo omitted, no error dialog | PASS |
| C6.3 | Exe file icon follows logo | PASS — generated **at build time**; rebuild needed after changing the logo |
| C6.4 | Final customer logo | **BLOCKED** — placeholder logo still in use |

### C7 Settings persistence

| # | Check | Result |
|---|---|---|
| C7.1 | Values survive restart (reloaded from disk) | PASS |
| C7.2 | Missing `config.xml` → defaults, file recreated on save | PASS |
| C7.3 | Damaged `config.xml` (truncated mid-write) → no crash, entries before the damage kept, damaged file preserved | FIXED (CFG-1) |
| C7.4 | Non-XML `config.xml` → defaults, backup made | PASS |
| C7.5 | Out-of-range / non-numeric values clamped or defaulted | PASS |

## D. Security & privacy

| # | Check | Result | Notes |
|---|---|---|---|
| D1 | Camera password stored encrypted (DPAPI, current user) | PASS | Config holds `dpapi:…` only |
| D2 | Camera safety code not on disk, in logs or in git history | PASS | |
| D3 | Hard-coded credentials in source | PASS | None |
| D4 | Credentials in logs | PASS | 23 pipeline log lines, all `user-pw=***`; no `rtsp://user:pass@` URLs; test password not found in logs, config or temp files |
| D5 | Credentials in RTSP URLs shown in UI/logs | PASS | URLs are built without credentials |

## E. Performance & stability

| # | Check | Result |
|---|---|---|
| E1 | Idle | PASS — 0.9 % CPU, 262 MB |
| E2 | Video playing | PASS — 4.1 % CPU, 343 MB |
| E3 | Video + 2 AI models | PASS — 6.9 % CPU; UI responsiveness p95 13 ms |
| E4 | 20 stream start/stop cycles + 10 rapid toggles (leak check) | PASS — after warm-up: private memory −42 MB, handles +17, GDI ±0, USER +1, threads −4 |
| E5 | Repeated resize across 5 pages | PASS — responsive throughout |

## F. Defects

### Found and fixed during QA

| ID | Severity | Description | Fix |
|---|---|---|---|
| CFG-1 | Medium | A partially damaged `config.xml` (e.g. power loss during save) was read up to the damage point without any backup; the next save overwrote it and silently lost all later settings, including camera credentials | `ExtLibs/Utilities/Settings.cs`: a damaged file is now copied to `config.xml<time>.failed` before it can be overwritten |
| CAM-1 | Medium | Unreachable camera diagnosed slowly / message lost | Fast RTSP pre-check on (re)start |
| CAM-2 | Medium | Stop during reconnect left the stream restarting | Fixed race |
| CAM-3 | Medium | Stream could report *Playing* after Stop | Fixed race; re-tested 20 cycles + rapid toggles |
| UI-1 | Low | Stock top-bar buttons re-appeared after a layout refresh | Re-hidden in `updateLayout` |
| UI-2 | Low | Paint artifacts on transparent-parent controls | Fixed |
| UI-3 | Low | Page header buttons overlapped the title on narrow windows | Header toolbar wraps to a second row |
| UI-4 | Low | Window could be shrunk until content was unusable | Minimum size 1024×680 |
| UI-5 | Low | Settings opened scrolled to *AI detection* (focus-driven auto-scroll) | Non-jumping scroll panel, reset to top on open |
| BRANDING-1 | Low | Exe icon was the Mission Planner icon | Generated from `logo.png` at build |
| REPO-1 | Low | Original sources excluded by upstream `.gitignore` | Committed |

### Observations (not fixed)

| ID | Description |
|---|---|
| OBS-1 | A mistyped camera address such as `192.168137.248` is reported as "not reachable" rather than "invalid IP address" |
| OBS-2 | With the logo missing, the About page leaves an empty space where the logo was |
| OBS-3 | Stock theme was `BurntKermit` before QA; not reproducible as a defect (likely a user choice) |

### Blocked — needs real equipment

Real drone flight; real IMOU K2EP-5R1W video/PTZ/ONVIF; trained detection models; live-microphone voice takeoff
outdoors; final customer logo.
