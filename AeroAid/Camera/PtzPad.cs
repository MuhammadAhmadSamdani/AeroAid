using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using MissionPlanner.AeroAid.Branding;
using MissionPlanner.AeroAid.UI;

namespace MissionPlanner.AeroAid.Camera
{
    /// <summary>
    /// Camera PTZ pad (hold to move, release to stop). Buttons for features the camera did not report are
    /// disabled and a message explains why - no fake controls.
    /// </summary>
    public class PtzPad : TableLayoutPanel
    {
        private readonly Dictionary<PtzDirection, AeroButton> buttons = new Dictionary<PtzDirection, AeroButton>();
        private readonly AeroButton center;

        public PtzPad()
        {
            BackColor = BrandingManager.Palette.Surface;
            ColumnCount = 5;
            RowCount = 3;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            for (var i = 0; i < 5; i++)
                ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            Add(PtzDirection.Up, "Up", AeroIcons.Up, "↑", 1, 0);
            Add(PtzDirection.Left, "Left", AeroIcons.Left, "←", 0, 1);
            Add(PtzDirection.Right, "Right", AeroIcons.Right, "→", 2, 1);
            Add(PtzDirection.Down, "Down", AeroIcons.Down, "↓", 1, 2);
            center = Btn("Center", AeroIcons.Center, "C");
            center.ButtonStyle = AeroButtonStyle.Primary;
            center.Click += async (s, e) => await CameraPtzManager.Instance.GoHomeAsync();
            Controls.Add(center, 1, 1);
            Controls.Add(new Panel { Width = 14, Height = 10, BackColor = Color.Transparent, Margin = new Padding(0) }, 3, 0);
            Add(PtzDirection.ZoomIn, "Zoom +", AeroIcons.ZoomIn, "+", 4, 0);
            var z = AeroDraw.Caption("ZOOM", 8f);
            z.Anchor = AnchorStyles.None;
            Controls.Add(z, 4, 1);
            Add(PtzDirection.ZoomOut, "Zoom −", AeroIcons.ZoomOut, "−", 4, 2);

            CameraPtzManager.Instance.MovementChanged += OnMovementChanged;
            CameraConnectionManager.CapabilitiesChanged += OnCapabilities;
            OnCapabilities();
        }

        private static AeroButton Btn(string text, string glyph, string hint) => new AeroButton
        {
            Text = text,
            Glyph = glyph,
            Hint = hint,
            StackedLayout = true,
            GlyphSize = 20f,
            Size = new Size(80, 72),
            Margin = new Padding(4),
            Font = AeroDraw.UiFont(8.5f, FontStyle.Bold)
        };

        private void Add(PtzDirection d, string text, string glyph, string hint, int col, int row)
        {
            var b = Btn(text, glyph, hint);
            b.PressStarted += (s, e) => CameraPtzManager.Instance.BeginMove(d);
            b.PressEnded += (s, e) => CameraPtzManager.Instance.EndMove(d);
            buttons[d] = b;
            Controls.Add(b, col, row);
        }

        public void SetHint(PtzDirection d, string hint)
        {
            if (buttons.TryGetValue(d, out var b))
            {
                b.Hint = hint;
                b.Invalidate();
            }
        }

        public void SetCenterHint(string hint)
        {
            center.Hint = hint;
            center.Invalidate();
        }

        private void OnCapabilities()
        {
            AeroDraw.SafeInvoke(this, () =>
            {
                var caps = CameraConnectionManager.Capabilities;
                foreach (var kv in buttons)
                    kv.Value.Enabled = CameraPtzManager.IsSupported(kv.Key, caps);
                center.Enabled = caps.PtzAvailable && caps.HomeAvailable;
            });
        }

        private void OnMovementChanged()
        {
            AeroDraw.SafeInvoke(this, () =>
            {
                var held = new HashSet<PtzDirection>(CameraPtzManager.Instance.Held);
                foreach (var kv in buttons)
                    kv.Value.IsActive = held.Contains(kv.Key);
            });
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            OnCapabilities();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                CameraPtzManager.Instance.MovementChanged -= OnMovementChanged;
                CameraConnectionManager.CapabilitiesChanged -= OnCapabilities;
            }

            base.Dispose(disposing);
        }
    }
}
