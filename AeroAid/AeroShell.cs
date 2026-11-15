using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using log4net;
using MissionPlanner.AeroAid.Branding;
using MissionPlanner.AeroAid.UI;
using MissionPlanner.AeroAid.Views;
using MissionPlanner.Controls;
using MissionPlanner.Utilities;

namespace MissionPlanner.AeroAid
{
    /// <summary>
    /// Integration point between AeroAid and the stock Mission Planner main window.
    /// Called from a few guarded (#if !LIB) hooks in Program.cs and MainV2.cs.
    /// </summary>
    public static class AeroShell
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public const string ThemeFile = "AeroAid.mpsystheme";

        // Screen names registered with MainSwitcher
        public const string ScreenDashboard = "AeroDashboard";
        public const string ScreenFlightControl = "AeroFlightControl";
        public const string ScreenLiveCamera = "AeroLiveCamera";
        public const string ScreenAiDetection = "AeroAiDetection";
        public const string ScreenTelemetry = "AeroTelemetry";
        public const string ScreenCameraControl = "AeroCameraControl";
        public const string ScreenSettings = "AeroSettings";
        public const string ScreenShortcuts = "AeroShortcuts";
        public const string ScreenAbout = "AeroAbout";
        public const string ScreenVoice = "AeroVoice";

        private static Sidebar sidebar;
        private static Timer syncTimer;
        private static MainV2 main;

        public static Sidebar Sidebar => sidebar;

        /// <summary>Early startup (Program.Main, before the splash is shown): apply branding.</summary>
        public static void ApplyProgramBranding()
        {
            try
            {
                BrandingManager.Load();
                MissionPlanner.Controls.MyButton.GlobalCornerRadius = BrandingManager.CornerRadius;
            }
            catch (Exception ex)
            {
                log.Error("Branding failed to load", ex);
            }
        }

        /// <summary>
        /// First run of AeroAid on this settings file: select the Mustard/Black theme.
        /// After that the user's theme choice (Config > Planner > Theme) is respected.
        /// </summary>
        public static void EnsureDefaultTheme()
        {
            try
            {
                if (!AeroSettings.GetBool("theme.initialized", false))
                {
                    if (System.IO.File.Exists(System.IO.Path.Combine(Settings.GetRunningDirectory(), ThemeFile)))
                        Settings.Instance["theme"] = ThemeFile;
                    else
                        log.Warn(ThemeFile + " not found next to the executable - keeping the current theme");
                    AeroSettings.SetBool("theme.initialized", true);
                }
            }
            catch (Exception ex)
            {
                log.Error("EnsureDefaultTheme failed", ex);
            }
        }

        /// <summary>Registers AeroAid screens and installs the sidebar navigation.</summary>
        public static void Install(MainV2 mainForm, MainSwitcher view)
        {
            main = mainForm;
            try
            {
                RegisterScreens(view);

                // Safety: any loss of focus of the main window releases all held movement controls.
                MainFormDeactivated += () => Flight.FlightControlManager.Instance.StopAll();
                MainFormDeactivated += () => Camera.CameraPtzManager.Instance.StopAll();

                // Keyboard flight/camera control (gated; see KeyboardControlManager)
                Input.KeyboardControlManager.Instance.Install(mainForm, view);
                MainFormDeactivated += () => Input.KeyboardControlManager.Instance.ReleaseAll();
                FlightControlView.KeyHintsHook = pad => pad.SetHints(
                    d => Input.ShortcutMap.KeyText(Input.ShortcutMap.KeyFor(MoveToAction(d))),
                    Input.ShortcutMap.KeyText(Input.ShortcutMap.KeyFor(Input.ShortcutAction.Stop)));
                mainForm.Deactivate += (s, e) => OnMainFormDeactivated();

                // AI detection runs in the background (waits for video; inactive without a model)
                Vision.DetectionAlertNotifier.Install(mainForm);
                Vision.AiDetectionManager.Instance.ApplySettings();
                // Voice: a recognised command only STARTS the confirmation workflow (UI thread)
                Voice.VoiceCommandEngine.Instance.CommandRecognized += (cmd, text, conf) =>
                    AeroDraw.SafeInvoke(mainForm, async () => await Voice.VoiceTakeoffWorkflow.RunAsync(text, conf));

                mainForm.FormClosing += (s, e) =>
                {
                    Voice.VoiceCommandEngine.Instance.StopListening();
                    Audio.MicrophoneManager.Instance.TurnOff();
                    Vision.AiDetectionManager.Instance.Stop();
                    Camera.VideoStreamManager.Instance.Stop();
                };

                if (!AeroSettings.UseSidebar)
                    return;

                sidebar = new Sidebar();
                BuildNavigation(sidebar, view);
                mainForm.Controls.Add(sidebar);

                // top bar (connection controls) in the AeroAid palette
                mainForm.MainMenu.BackgroundImage = null;
                mainForm.MainMenu.BackColor = BrandingManager.Palette.Background;
                mainForm.panel1.BackColor = BrandingManager.Palette.Background;

                // The sidebar replaces the stock view buttons; the connect controls stay in the top bar.
                foreach (var item in new ToolStripItem[]
                         {
                             mainForm.MenuFlightData, mainForm.MenuFlightPlanner, mainForm.MenuInitConfig,
                             mainForm.MenuConfigTune, mainForm.MenuSimulation, mainForm.MenuHelp
                         })
                    item.Visible = false;

                syncTimer = new Timer { Interval = 250 };
                syncTimer.Tick += (s, e) =>
                {
                    var name = view.current?.Name;
                    if (name != null)
                        sidebar.SetActive(name);
                };
                syncTimer.Start();
            }
            catch (Exception ex)
            {
                log.Error("AeroAid shell install failed - stock Mission Planner navigation remains available", ex);
                foreach (var item in new ToolStripItem[]
                         {
                             mainForm.MenuFlightData, mainForm.MenuFlightPlanner, mainForm.MenuInitConfig,
                             mainForm.MenuConfigTune, mainForm.MenuSimulation, mainForm.MenuHelp
                         })
                    item.Visible = true;
            }
        }

        /// <summary>Hides the stock view buttons (Data/Plan/Setup/Config/Sim/Help) while the sidebar is in use.</summary>
        public static void HideStockViewButtons(MainV2 mainForm)
        {
            if (sidebar == null || mainForm == null)
                return;
            foreach (var item in new ToolStripItem[]
                     {
                         mainForm.MenuFlightData, mainForm.MenuFlightPlanner, mainForm.MenuInitConfig,
                         mainForm.MenuConfigTune, mainForm.MenuSimulation, mainForm.MenuHelp
                     })
                if (item != null && item.Visible)
                    item.Visible = false;
        }

        /// <summary>Called once the stock start-up screen has been shown: open the AeroAid dashboard.</summary>
        public static void ShowStartScreen(MainSwitcher view)
        {
            if (!AeroSettings.UseSidebar)
                return;
            try
            {
                var target = AeroSettings.StartScreen;
                if (target == "AeroFireDetection") target = ScreenAiDetection; // renamed page
                if (!view.screens.Exists(s => s.Name == target))
                    target = ScreenDashboard;
                main.AeroNavigate(target);
            }
            catch (Exception ex)
            {
                log.Error("Start screen failed - staying on Flight Data", ex);
            }
        }

        /// <summary>Fired when the main window loses focus: all held movement must stop.</summary>
        public static event Action MainFormDeactivated;

        private static void OnMainFormDeactivated()
        {
            try
            {
                MainFormDeactivated?.Invoke();
            }
            catch (Exception ex)
            {
                log.Error("MainFormDeactivated handler failed", ex);
            }
        }

        private static Input.ShortcutAction MoveToAction(Flight.MoveDirection d)
        {
            switch (d)
            {
                case Flight.MoveDirection.Forward: return Input.ShortcutAction.Forward;
                case Flight.MoveDirection.Backward: return Input.ShortcutAction.Backward;
                case Flight.MoveDirection.Left: return Input.ShortcutAction.Left;
                case Flight.MoveDirection.Right: return Input.ShortcutAction.Right;
                case Flight.MoveDirection.Up: return Input.ShortcutAction.Up;
                case Flight.MoveDirection.Down: return Input.ShortcutAction.Down;
                case Flight.MoveDirection.YawLeft: return Input.ShortcutAction.YawLeft;
                default: return Input.ShortcutAction.YawRight;
            }
        }

        private static void RegisterScreens(MainSwitcher view)
        {
            view.AddScreen(new MainSwitcher.Screen(ScreenShortcuts, typeof(ShortcutsView), false));
            // persistent screens keep their state (map position, logs) between visits
            view.AddScreen(new MainSwitcher.Screen(ScreenDashboard, typeof(DashboardView), true));
            view.AddScreen(new MainSwitcher.Screen(ScreenFlightControl, typeof(FlightControlView), true));
            view.AddScreen(new MainSwitcher.Screen(ScreenTelemetry, typeof(TelemetryView), true));
            view.AddScreen(new MainSwitcher.Screen(ScreenLiveCamera, typeof(LiveCameraView), true));
            view.AddScreen(new MainSwitcher.Screen(ScreenCameraControl, typeof(CameraControlView), true));
            view.AddScreen(new MainSwitcher.Screen(ScreenAiDetection, typeof(AiDetectionView), true));
            view.AddScreen(new MainSwitcher.Screen(ScreenVoice, typeof(VoiceView), true));
            view.AddScreen(new MainSwitcher.Screen(ScreenSettings, typeof(SettingsView), false));
            view.AddScreen(new MainSwitcher.Screen(ScreenAbout, typeof(AboutView), false));
        }

        private static void BuildNavigation(Sidebar bar, MainSwitcher view)
        {
            bar.AddSection("Operations");
            bar.AddItem(ScreenDashboard, "Dashboard", AeroIcons.Dashboard, () => view.ShowScreen(ScreenDashboard));
            bar.AddItem(ScreenFlightControl, "Flight Control", AeroIcons.FlightControl, () => view.ShowScreen(ScreenFlightControl));
            bar.AddItem("FlightPlanner", "Mission Planner", AeroIcons.Mission, () => main.AeroNavigate("FlightPlanner"));
            bar.AddItem(ScreenLiveCamera, "Live Camera", AeroIcons.Camera, () => view.ShowScreen(ScreenLiveCamera));
            bar.AddItem(ScreenAiDetection, "AI Detection", AeroIcons.AiDetection, () => view.ShowScreen(ScreenAiDetection),
                () => Vision.AiDetectionManager.Instance.UnacknowledgedAlert);
            bar.AddItem(ScreenTelemetry, "Telemetry", AeroIcons.Telemetry, () => view.ShowScreen(ScreenTelemetry));
            bar.AddItem(ScreenCameraControl, "Camera Control", AeroIcons.CameraControl, () => view.ShowScreen(ScreenCameraControl));
            bar.AddItem(ScreenVoice, "Voice & Microphone", AeroIcons.Mic, () => view.ShowScreen(ScreenVoice));

            bar.AddSection("Mission Planner tools");
            bar.AddItem("FlightData", "Flight Data (HUD)", AeroIcons.Hud, () => main.AeroNavigate("FlightData"));
            bar.AddItem("HWConfig", "Vehicle Setup", AeroIcons.Setup, () => main.AeroNavigate("HWConfig"));
            bar.AddItem("SWConfig", "Config / Tuning", AeroIcons.Tuning, () => main.AeroNavigate("SWConfig"));
            bar.AddItem("Simulation", "Simulation (SITL)", AeroIcons.Simulation, () => main.AeroNavigate("Simulation"));
            bar.AddItem("Help", "Help", AeroIcons.Help, () => main.AeroNavigate("Help"));

            bar.AddSection("Application");
            bar.AddItem(ScreenSettings, "Settings", AeroIcons.Settings, () => view.ShowScreen(ScreenSettings));
            bar.AddItem(ScreenShortcuts, "Controls & Shortcuts", AeroIcons.Keyboard, () => view.ShowScreen(ScreenShortcuts));
            bar.AddItem(ScreenAbout, "About Application", AeroIcons.About, () => view.ShowScreen(ScreenAbout));
        }
    }
}
