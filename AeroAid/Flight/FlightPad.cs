using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using MissionPlanner.AeroAid.Branding;
using MissionPlanner.AeroAid.UI;

namespace MissionPlanner.AeroAid.Flight
{
    /// <summary>
    /// Hold-to-move directional pad (horizontal 3x3 + vertical column). Movement lasts only while a
    /// button is held; releasing it (or losing mouse capture/focus) stops the corresponding motion.
    /// </summary>
    public class FlightPad : TableLayoutPanel
    {
        private readonly Dictionary<MoveDirection, AeroButton> buttons = new Dictionary<MoveDirection, AeroButton>();
        private readonly AeroButton stop;

        public FlightPad()
        {
            BackColor = BrandingManager.Palette.Surface;
            ColumnCount = 5;
            RowCount = 3;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(4);
            for (var i = 0; i < 5; i++)
                ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            Add(MoveDirection.YawLeft, "Yaw L", AeroIcons.RotateLeft, "Q", 0, 0);
            Add(MoveDirection.Forward, "Forward", AeroIcons.Up, "W", 1, 0);
            Add(MoveDirection.YawRight, "Yaw R", AeroIcons.RotateRight, "E", 2, 0);
            Add(MoveDirection.Left, "Left", AeroIcons.Left, "A", 0, 1);
            Add(MoveDirection.Right, "Right", AeroIcons.Right, "D", 2, 1);
            Add(MoveDirection.Backward, "Back", AeroIcons.Down, "S", 1, 2);

            stop = PadButton("STOP", AeroIcons.Stop, "Space");
            stop.ButtonStyle = AeroButtonStyle.Danger;
            stop.Click += (s, e) => FlightControlManager.Instance.StopAll();
            Controls.Add(stop, 1, 1);

            // spacer column then vertical controls
            Controls.Add(new Panel { Width = 18, Height = 10, BackColor = Color.Transparent, Margin = new Padding(0) }, 3, 0);
            Add(MoveDirection.Up, "Up", AeroIcons.ArrowUp, "R", 4, 0);
            var altLabel = AeroDraw.Caption("ALTITUDE", 8f);
            altLabel.Anchor = AnchorStyles.None;
            Controls.Add(altLabel, 4, 1);
            Add(MoveDirection.Down, "Down", AeroIcons.ArrowDown, "F", 4, 2);

            FlightControlManager.Instance.MovementChanged += OnMovementChanged;
        }

        /// <summary>Updates the keyboard hints shown under each button.</summary>
        public void SetHints(Func<MoveDirection, string> hintFor, string stopHint)
        {
            foreach (var kv in buttons)
            {
                kv.Value.Hint = hintFor(kv.Key);
                kv.Value.Invalidate();
            }

            stop.Hint = stopHint;
            stop.Invalidate();
        }

        private static AeroButton PadButton(string text, string glyph, string hint) => new AeroButton
        {
            Text = text,
            Glyph = glyph,
            Hint = hint,
            StackedLayout = true,
            GlyphSize = 22f,
            Size = new Size(86, 80),
            Margin = new Padding(4),
            Font = AeroDraw.UiFont(8.5f, FontStyle.Bold),
            ButtonStyle = AeroButtonStyle.Secondary
        };

        private void Add(MoveDirection dir, string text, string glyph, string hint, int col, int row)
        {
            var b = PadButton(text, glyph, hint);
            b.PressStarted += (s, e) => FlightControlManager.Instance.BeginMove(dir);
            b.PressEnded += (s, e) => FlightControlManager.Instance.EndMove(dir);
            buttons[dir] = b;
            Controls.Add(b, col, row);
        }

        /// <summary>Enables/disables all movement buttons (STOP always stays enabled).</summary>
        public void SetMovementEnabled(bool enabled)
        {
            foreach (var b in buttons.Values)
                if (b.Enabled != enabled)
                    b.Enabled = enabled;
        }

        private void OnMovementChanged()
        {
            AeroDraw.SafeInvoke(this, () =>
            {
                var held = new HashSet<MoveDirection>(FlightControlManager.Instance.HeldDirections);
                foreach (var kv in buttons)
                    kv.Value.IsActive = held.Contains(kv.Key);
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                FlightControlManager.Instance.MovementChanged -= OnMovementChanged;
            base.Dispose(disposing);
        }
    }
}
