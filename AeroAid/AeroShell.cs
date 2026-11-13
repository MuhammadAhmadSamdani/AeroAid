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
        public const string ScreenFireDetection = "AeroFireDetection";
        public const string ScreenTelemetry = "AeroTelemetry";
        public const string ScreenCameraControl = "AeroCameraControl";
        public const string ScreenSettings = "AeroSettings";
        public const string ScreenShortcuts = "AeroShortcuts";
        public const string ScreenAbout = "AeroAbout";

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

                if (!AeroSettings.UseSidebar)
                    return;

                sidebar = new Sidebar();
                BuildNavigation(sidebar, view);
                mainForm.Controls.Add(sidebar);

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

                mainForm.Deactivate += (s, e) => OnMainFormDeactivated();
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

        /// <summary>Called once the stock start-up screen has been shown.</summary>
        public static void ShowStartScreen(MainSwitcher view)
        {
            if (!AeroSettings.UseSidebar)
                return;
            try
            {
                StartScreenHook?.Invoke(view);
            }
            catch (Exception ex)
            {
                log.Error("Start screen failed", ex);
            }
        }

        /// <summary>Set by later modules (dashboard) to choose the initial screen.</summary>
        internal static Action<MainSwitcher> StartScreenHook;

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

        private static void RegisterScreens(MainSwitcher view)
        {
            view.AddScreen(new MainSwitcher.Screen(ScreenAbout, typeof(AboutView), false));
            ScreenRegistrations?.Invoke(view);
        }

        /// <summary>Extension point used by each AeroAid module to register its screens.</summary>
        internal static Action<MainSwitcher> ScreenRegistrations;

        /// <summary>Extension point used by each AeroAid module to add its sidebar entries.</summary>
        internal static Action<Sidebar, MainSwitcher> PrimaryNavigation;

        private static void BuildNavigation(Sidebar bar, MainSwitcher view)
        {
            bar.AddSection("Operations");
            PrimaryNavigation?.Invoke(bar, view);
            bar.AddItem("FlightPlanner", "Mission Planner", AeroIcons.Mission, () => main.AeroNavigate("FlightPlanner"));

            bar.AddSection("Mission Planner tools");
            bar.AddItem("FlightData", "Flight Data (HUD)", AeroIcons.Hud, () => main.AeroNavigate("FlightData"));
            bar.AddItem("HWConfig", "Vehicle Setup", AeroIcons.Setup, () => main.AeroNavigate("HWConfig"));
            bar.AddItem("SWConfig", "Config / Tuning", AeroIcons.Tuning, () => main.AeroNavigate("SWConfig"));
            bar.AddItem("Simulation", "Simulation (SITL)", AeroIcons.Simulation, () => main.AeroNavigate("Simulation"));
            bar.AddItem("Help", "Help", AeroIcons.Help, () => main.AeroNavigate("Help"));

            bar.AddSection("Application");
            SettingsNavigation?.Invoke(bar, view);
            bar.AddItem(ScreenAbout, "About Application", AeroIcons.About, () => view.ShowScreen(ScreenAbout));
        }

        /// <summary>Extension point for the Settings / Shortcuts entries.</summary>
        internal static Action<Sidebar, MainSwitcher> SettingsNavigation;
    }
}
