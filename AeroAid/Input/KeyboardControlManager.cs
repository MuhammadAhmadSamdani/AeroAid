using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using log4net;
using MissionPlanner.AeroAid.Camera;
using MissionPlanner.AeroAid.Flight;
using MissionPlanner.Controls;

namespace MissionPlanner.AeroAid.Input
{
    /// <summary>
    /// Keyboard flight and camera control.
    /// Safety rules:
    ///  - active only while the main AeroAid window is the active window (no dialogs open) and an
    ///    AeroAid operations screen is shown;
    ///  - never while a text box / combo box / numeric field has focus;
    ///  - Ctrl/Alt combinations are passed through untouched (Mission Planner shortcuts keep working);
    ///  - movement uses key-down/key-up: releasing a key stops that motion; key auto-repeat is ignored;
    ///  - window deactivation releases every held key (main form Deactivate -> ReleaseAll);
    ///  - mode keys go through the normal confirmation dialogs (RTL/Land/RC-dependent modes).
    /// </summary>
    public sealed class KeyboardControlManager : IMessageFilter
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        public static KeyboardControlManager Instance { get; } = new KeyboardControlManager();

        private const int WM_KEYDOWN = 0x100;
        private const int WM_KEYUP = 0x101;
        private const int WM_SYSKEYDOWN = 0x104;
        private const int WM_SYSKEYUP = 0x105;

        [DllImport("user32.dll")]
        private static extern IntPtr GetFocus();

        private readonly HashSet<Keys> held = new HashSet<Keys>();
        private Form mainForm;
        private MainSwitcher view;

        public static readonly HashSet<string> FlightScreens = new HashSet<string>
        {
            AeroShell.ScreenFlightControl, AeroShell.ScreenDashboard, AeroShell.ScreenLiveCamera,
            AeroShell.ScreenCameraControl, AeroShell.ScreenFireDetection
        };

        public static readonly HashSet<string> CameraScreens = new HashSet<string>
        {
            AeroShell.ScreenLiveCamera, AeroShell.ScreenCameraControl, AeroShell.ScreenFireDetection,
            AeroShell.ScreenFlightControl, AeroShell.ScreenDashboard
        };

        /// <summary>Raised when a key triggers an action (for on-screen feedback).</summary>
        public event Action<ShortcutAction, bool> ActionTriggered;

        public void Install(Form main, MainSwitcher switcher)
        {
            mainForm = main;
            view = switcher;
            Application.AddMessageFilter(this);
        }

        public string CurrentScreen => view?.current?.Name ?? "";

        /// <summary>Why keyboard control is (in)active right now - shown on the shortcuts screen.</summary>
        public string Availability(out bool flightKeys, out bool cameraKeys)
        {
            flightKeys = cameraKeys = false;
            if (mainForm == null) return "Not installed.";
            if (Form.ActiveForm != mainForm) return "Inactive: the AeroAid main window is not focused (or a dialog is open).";
            if (IsTextInputFocused()) return "Inactive while typing in a text field.";
            var s = CurrentScreen;
            flightKeys = AeroSettings.KeyboardFlightEnabled && FlightScreens.Contains(s);
            cameraKeys = CameraScreens.Contains(s);
            if (!flightKeys && !cameraKeys)
                return "Inactive on this screen. Keys work on Dashboard, Flight Control, Live Camera, Camera Control and Fire Detection.";
            return !AeroSettings.KeyboardFlightEnabled ? "Camera keys active; drone keys disabled in Settings › Flight controls." : "Active.";
        }

        private static bool IsTextInputFocused()
        {
            var c = Control.FromHandle(GetFocus());
            while (c != null)
            {
                if (c is TextBoxBase || c is UpDownBase || (c is ComboBox cb && cb.DropDownStyle != ComboBoxStyle.DropDownList && cb.Focused) ||
                    c is DataGridViewTextBoxEditingControl || c is WebBrowser)
                    return true;
                if (c is ComboBox list && list.DroppedDown)
                    return true;
                c = c.Parent;
            }

            return false;
        }

        /// <summary>Releases all keyboard-held motion (called on window deactivation and screen changes).</summary>
        public void ReleaseAll()
        {
            if (held.Count == 0) return;
            held.Clear();
            FlightControlManager.Instance.StopAll();
            CameraPtzManager.Instance.StopAll();
        }

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != WM_KEYDOWN && m.Msg != WM_KEYUP && m.Msg != WM_SYSKEYDOWN && m.Msg != WM_SYSKEYUP)
                return false;
            try
            {
                var key = (Keys)((int)m.WParam & 0xFFFF);
                var isDown = m.Msg == WM_KEYDOWN || m.Msg == WM_SYSKEYDOWN;

                // key-up of a held key ALWAYS stops its motion, whatever has focus now
                if (!isDown)
                {
                    if (held.Remove(key))
                    {
                        var a = ShortcutMap.ActionFor(key);
                        if (a.HasValue) Release(a.Value);
                        return true;
                    }

                    return false;
                }

                var mods = Control.ModifierKeys;
                if ((mods & (Keys.Control | Keys.Alt)) != 0)
                    return false;

                var action = ShortcutMap.ActionFor(key);
                if (!action.HasValue)
                    return false;

                Availability(out var flightKeys, out var cameraKeys);
                var cat = ShortcutMap.Definitions[action.Value].cat;
                var allowed = cat == ShortcutCategory.Camera ? cameraKeys : flightKeys;
                if (!allowed)
                    return false;

                var repeat = ((long)m.LParam & (1L << 30)) != 0;
                if (repeat || held.Contains(key))
                    return true; // swallow auto-repeat

                Press(action.Value, key);
                return true;
            }
            catch (Exception ex)
            {
                log.Error("Keyboard handling failed", ex);
                return false;
            }
        }

        private static MoveDirection? Move(ShortcutAction a)
        {
            switch (a)
            {
                case ShortcutAction.Forward: return MoveDirection.Forward;
                case ShortcutAction.Backward: return MoveDirection.Backward;
                case ShortcutAction.Left: return MoveDirection.Left;
                case ShortcutAction.Right: return MoveDirection.Right;
                case ShortcutAction.Up: return MoveDirection.Up;
                case ShortcutAction.Down: return MoveDirection.Down;
                case ShortcutAction.YawLeft: return MoveDirection.YawLeft;
                case ShortcutAction.YawRight: return MoveDirection.YawRight;
                default: return null;
            }
        }

        private static PtzDirection? Ptz(ShortcutAction a)
        {
            switch (a)
            {
                case ShortcutAction.CameraUp: return PtzDirection.Up;
                case ShortcutAction.CameraDown: return PtzDirection.Down;
                case ShortcutAction.CameraLeft: return PtzDirection.Left;
                case ShortcutAction.CameraRight: return PtzDirection.Right;
                case ShortcutAction.ZoomIn: return PtzDirection.ZoomIn;
                case ShortcutAction.ZoomOut: return PtzDirection.ZoomOut;
                default: return null;
            }
        }

        private void Press(ShortcutAction a, Keys key)
        {
            ActionTriggered?.Invoke(a, true);
            var mv = Move(a);
            if (mv.HasValue)
            {
                if (FlightControlManager.Instance.BeginMove(mv.Value))
                    held.Add(key);
                return;
            }

            var ptz = Ptz(a);
            if (ptz.HasValue)
            {
                held.Add(key);
                CameraPtzManager.Instance.BeginMove(ptz.Value);
                return;
            }

            switch (a)
            {
                case ShortcutAction.Stop:
                    ReleaseAll();
                    FlightControlManager.Instance.StopAll();
                    break;
                case ShortcutAction.CameraCenter:
                    _ = CameraPtzManager.Instance.GoHomeAsync();
                    break;
                default:
                    var mode = ModeFor(a);
                    if (mode != null)
                    {
                        // run after this key message is processed; dialogs inside must not run in the filter
                        mainForm.BeginInvoke(new Action(async () => await FlightActions.SetModeWithConfirmation(mode)));
                    }

                    break;
            }
        }

        private void Release(ShortcutAction a)
        {
            ActionTriggered?.Invoke(a, false);
            var mv = Move(a);
            if (mv.HasValue)
            {
                FlightControlManager.Instance.EndMove(mv.Value);
                return;
            }

            var ptz = Ptz(a);
            if (ptz.HasValue)
                CameraPtzManager.Instance.EndMove(ptz.Value);
        }

        private static string ModeFor(ShortcutAction a)
        {
            switch (a)
            {
                case ShortcutAction.ModeStabilize: return "Stabilize";
                case ShortcutAction.ModeAltHold: return "AltHold";
                case ShortcutAction.ModeLoiter: return "Loiter";
                case ShortcutAction.ModeGuided: return "Guided";
                case ShortcutAction.ModeAuto: return "Auto";
                case ShortcutAction.ModeRtl: return "RTL";
                case ShortcutAction.ModeLand: return "Land";
                default: return null;
            }
        }
    }
}
