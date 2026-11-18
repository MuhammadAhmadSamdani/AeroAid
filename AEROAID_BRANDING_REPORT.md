# AEROAID branding report: ArduPilot logo replaced

| | |
|---|---|
| Date | 2026-11-18 |
| Branch | `custom-gcs` (uncommitted) |
| Logo source | `Assets/Branding/aeroaid.png`: 2172 × 724 px, RGBA with a transparent background. The visible wordmark is about 4.2 : 1. |
| Result | The ArduPilot logo no longer appears in the application UI. Every logo location now uses the AEROAID wordmark from this one file. |

## 1. Where the ArduPilot logo (and other stock logos) were found

The search covered the code, the Designer files, `Properties/Resources.resx`, `Resources/`, the files next to the exe
and the AeroAid shell. Mission Planner is WinForms: there is no XAML.

| # | Location | What was there | Shown in this build? | Action |
|---|---|---|---|---|
| 1 | **Main window menu bar**, right side: `MenuArduPilot` button (`MainV2.cs` and `MainV2.Designer.cs`, resource `_0d92fed790a3a70170e61a86db103f399a595c70.png`) | **ArduPilot wordmark** | **Yes**: the stock top bar (connect / COM port) is visible above every page | **Replaced** with the AEROAID wordmark |
| 2 | Splash screen (`Splash.Designer.cs`, `Resources/splashdark.jpg`) | "MissionPlanner" wordmark + plane | No: replaced at start-up by the generated AeroAid splash | Splash redesigned around the AEROAID wordmark |
| 3 | Window / taskbar icon (`Program.IconFile`, default `mpdesktop.ico`; applied to the main window and to every themed form by `ThemeManager`) | Mission Planner icon, previously the AeroAid placeholder | Yes | **Replaced**: icon tile generated from `aeroaid.png` |
| 4 | Exe file icon (`Assets/Branding/app.ico`, `<ApplicationIcon>`) | Generated from the placeholder `logo.png` | Yes (Explorer, shortcuts) | **Regenerated from `aeroaid.png`** |
| 5 | AeroAid sidebar header (`AeroAid/UI/Sidebar.cs`) | Placeholder logo `Assets/Branding/logo.png` + "AeroAid" text | Yes | **Replaced** with the wordmark |
| 6 | About page (`AeroAid/Views/AboutView.cs`) | Placeholder logo + title text | Yes | **Replaced** with the wordmark |
| 7 | `Program.Logo` / `Program.Logo2` (`logo.png` / `logo2.png` next to the exe, the stock Mission Planner OEM hook) | Nothing: no such files are shipped | No | Unchanged (the hook still works if files are added) |
| 8 | `missionplannerlogo`, `missionplannerlogodark` in `Resources.resx` | Mission Planner logos | No: referenced nowhere in code | Unchanged |
| 9 | Firmware screen "FW icons 2013+logos-*.png", `APM_airframes_*` | Vehicle frame-type pictures (quad, hexa, plane…) | Yes, on Install Firmware | Unchanged: functional pictures, not the ArduPilot logo |
| 10 | Dashboard, Flight Data (HUD), Flight Plan, Help, Settings | No logo images | — | Checked, nothing to replace |

## 2. Files modified

| File | Change |
|---|---|
| `MainV2.cs` | The menu-bar logo button shows `BrandingManager.GetLogo(200, 31)`: the AEROAID wordmark at the original 31 px height, aspect preserved, no scaling by the tool strip. Tooltip: "AeroAid - based on Mission Planner for ArduPilot (opens ardupilot.org)". The click still opens ardupilot.org (behaviour unchanged). Inside `#if !LIB`. |
| `AeroAid/Branding/BrandingManager.cs` | Default logo `aeroaid.png`. On load, only the fully transparent margin around the artwork is trimmed, in memory; the file is not changed. New `GetLogo(maxWidth, maxHeight)` scales to fit with high-quality bicubic sampling and edge handling: never stretched, never cropped, transparent. New `GetIconBitmap(size)` (wordmark on a rounded dark tile), `AppIcon`, wordmark-centred `CreateSplashImage`, and `Reload()`. |
| `AeroAid/UI/Sidebar.cs` | Wordmark centred in the header, with the tagline below. The duplicate "AeroAid" text was removed, because the wordmark already contains the name. |
| `AeroAid/Views/AboutView.cs` | Wordmark (up to 400 × 96) instead of the square logo and name title |
| `Assets/Branding/branding.json` | `"logoFile": "aeroaid.png"` |
| `Assets/Branding/make-icon.ps1` | Builds `app.ico` (16–256 px) from `aeroaid.png` with the same trimmed, centred wordmark on a dark tile |
| `Assets/Branding/app.ico` | Regenerated |
| `MissionPlanner.csproj` | The icon-regeneration target now watches `aeroaid.png`; comments updated. It is Windows-1252 encoded, which was preserved. |
| `Assets/Branding/logo.png` | **Deleted**: the old placeholder logo. `aeroaid.png` is now the only logo file. |
| `Program.cs` | Comment only |
| `README_CUSTOM_GCS.md`, `CHANGELOG_CUSTOM_GCS.md` | Documentation |
| `MissionPlannerTests/AeroAid/BrandingLogoTests.cs` (new) | Logo source, aspect ratio, transparency, icon, splash |
| `docs/branding-screenshots/*.png` (new) | Verification screenshots (§5) |

No MAVLink, flight-control, telemetry, mission-planning, camera, AI-detection or voice code was changed.

## 3. How the logo is integrated

```
Assets/Branding/aeroaid.png  (the only logo file; copied next to the exe by the build)
   │
   ├─ BrandingManager (runtime, read once into memory - the file is not locked)
   │     ├─ GetLogo(w, h)      → sidebar 192×46 box · About 400×96 · menu bar 200×31 · splash
   │     ├─ GetIconBitmap()    → Program.IconFile → main window + every themed form (ThemeManager) + taskbar
   │     └─ CreateSplashImage  → start-up splash
   └─ make-icon.ps1 (build time, MissionPlanner.csproj target AeroAidAppIcon) → app.ico → exe icon
```

- **Aspect ratio:** every location scales the wordmark to *fit inside* a box. One dimension fills the box and the
  other is smaller. Seven tests check that width ÷ height matches the original within 5 % and that the corners stay
  transparent.
- **No crop, no stretch:** only the fully transparent padding of the PNG (alpha ≤ 8) is ignored, with a 1 % safety
  margin. The visible artwork is untouched.
- **Transparency:** the logo keeps its transparent background in the sidebar, About page, menu bar and splash, all of
  which have the dark brand background.
- **The one exception is the icon,** which sits on a rounded **#111111 tile**. The wordmark's "AERO" is white, so a
  transparent icon would vanish on a light Windows taskbar or in Explorer.
- **Rebranding later:** replace `Assets/Branding/aeroaid.png` (or point `logoFile` in `branding.json` at another file)
  and rebuild. The icon is regenerated automatically.

## 4. Build status

| Step | Result |
|---|---|
| `dotnet clean` + `dotnet build MissionPlanner.csproj -c Release` (full, including all referenced projects) | **PASS: 0 errors.** 1,469 warnings, which is Mission Planner's usual baseline. |
| First attempt | Failed because a running Mission Planner locked `bin\Release\net461`. With your approval it was closed and the build repeated. One edit had also put a control character into the csproj; it was found and fixed. |
| `MissionPlannerTests` (Release) | Builds with 0 errors |
| AeroAid automated tests (x64) | **224 passed, 0 failed**, 3 skipped (on-demand corpus, live-microphone and SITL tests). Includes 7 new branding tests. |
| Build output | `bin\Release\net461\Assets\Branding\` contains `aeroaid.png`, `app.ico`, `branding.json` and no `logo.png` |

## 5. Visual verification (the real application, launched from `bin\Release\net461`)

The display runs at **125 % Windows scaling**, so the screens were also checked at a higher DPI.

| Screen / size | Result | Screenshot |
|---|---|---|
| Splash at start-up | PASS: wordmark centred, undistorted, transparent on dark; tagline, version, attribution | `docs/branding-screenshots/splash.png` |
| Main window at minimum size (1024 × 680 logical) | PASS: sidebar wordmark; menu-bar wordmark where the ArduPilot logo was; title-bar icon tile | `main_min_size.png` |
| Main window 1366 × 768 and 1920 × 1040, maximised | PASS: the same logos at the same proportions, nothing clipped | `main_1920x1040.png` |
| About page | PASS: large wordmark, undistorted | `about.png` |
| Flight Data (HUD) | PASS: no ArduPilot logo; AEROAID logos in the sidebar and menu bar | `flight_data.png` |
| Help | PASS: no logo image (ArduPilot *text and links* remain, §6) | — |
| Exe / window icon 256, 48 and 32 px | PASS: legible at 48 px and above; at 32 px and below the wordmark is very small (§6) | `icon_256_48_32.png` |
| Search for the ArduPilot image | Only two code references to the ArduPilot logo resource exist: the Designer default and the stock resize in `MainV2.cs`. The AEROAID image overrides both at start-up. | — |

**Not verified:** Windows scaling other than 125 %, multi-monitor setups with different DPI, and stock dialogs that
are not themed. Those dialogs keep their own resource icon; see §6.

## 6. Remaining ArduPilot / Mission Planner branding that was not replaced

| Item | Why it stays |
|---|---|
| ArduPilot and Mission Planner **text**: the About attribution, the splash attribution line, the Help page, the window title "(Mission Planner 1.3.83 …)" | Required GPLv3 / project attribution and support information. These are not logos. |
| `MenuArduPilot` click opens ardupilot.org | Existing behaviour kept, as required. The tooltip now says so. |
| Vehicle frame and firmware pictures (Install Firmware, frame type) | Functional images of vehicle types, not branding |
| Unused stock image resources (`_0d92fed….png`, `missionplannerlogo*.png`, `splashdark.jpg`, `mpdesktop.ico`) inside the exe | No longer displayed. They were left in the resource file so the stock Designer code and the non-AeroAid fallback paths still compile and work. |
| Icons of stock dialogs that do not go through `ThemeManager` | They may still show Mission Planner's own icon in their title bar. None were found on the screens checked. |
| **Small icon sizes:** a wide 4 : 1 wordmark inside a square icon is about 30 % of the icon's height, so at 16–32 px (title bar, small taskbar) it is only a recognisable mark | Inherent to using the wordmark without cropping. A square AEROAID symbol (for example the "A" with the arrow) supplied as a separate file could be used for the icon alone. |
