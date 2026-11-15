using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using MissionPlanner.AeroAid.Branding;

namespace MissionPlanner.AeroAid.UI
{
    public enum AeroButtonStyle
    {
        /// <summary>Mustard filled - primary actions.</summary>
        Primary,
        /// <summary>Dark surface with border - secondary actions.</summary>
        Secondary,
        /// <summary>Transparent until hovered - navigation / toolbars.</summary>
        Ghost,
        /// <summary>Red - dangerous actions (disarm, land...).</summary>
        Danger
    }

    /// <summary>
    /// Rounded, animated button: smooth hover fade, subtle press "sink" animation, active state,
    /// disabled state, optional icon glyph. Raises <see cref="PressStarted"/>/<see cref="PressEnded"/>
    /// for hold-to-act controls (movement pads); PressEnded is ALWAYS raised when a press ends,
    /// including on mouse capture loss, disable or hide, so movement can never get "stuck".
    /// </summary>
    [ToolboxItem(true)]
    public class AeroButton : Control
    {
        private float hover; // 0..1
        private float press; // 0..1
        private bool mouseInside;
        private bool pressed;
        private bool isActive;
        private AeroButtonStyle style = AeroButtonStyle.Secondary;
        private string glyph = "";
        private readonly Timer animTimer = new Timer { Interval = 15 };

        public event EventHandler PressStarted;
        public event EventHandler PressEnded;

        public AeroButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
            // Click is raised by this control on mouse-up inside its bounds (see OnMouseUp)
            SetStyle(ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, false);
            BackColor = Color.Transparent;
            ForeColor = BrandingManager.Palette.Text;
            Font = new Font("Segoe UI Semibold", 10f);
            Cursor = Cursors.Hand;
            Size = new Size(120, 40);
            Padding = new Padding(12, 6, 12, 6);
            animTimer.Tick += AnimTick;
        }

        [Category("AeroAid")]
        public AeroButtonStyle ButtonStyle
        {
            get => style;
            set { style = value; Invalidate(); }
        }

        /// <summary>Icon glyph from <see cref="AeroIcons"/>.</summary>
        [Category("AeroAid")]
        public string Glyph
        {
            get => glyph;
            set { glyph = value ?? ""; Invalidate(); }
        }

        /// <summary>Shows the button as selected (e.g. active navigation item or current mode).</summary>
        [Category("AeroAid")]
        public bool IsActive
        {
            get => isActive;
            set { if (isActive != value) { isActive = value; Invalidate(); } }
        }

        /// <summary>When set, text and border use this colour (e.g. blinking red for an active alert).</summary>
        [Browsable(false)]
        public Color? AlertColor
        {
            get => alertColor;
            set { if (alertColor != value) { alertColor = value; Invalidate(); } }
        }

        private Color? alertColor;

        [Category("AeroAid")] public int CornerRadius { get; set; } = BrandingManager.CornerRadius;

        [Category("AeroAid")] public ContentAlignment TextAlignment { get; set; } = ContentAlignment.MiddleCenter;

        /// <summary>Icon above text (for square pad buttons).</summary>
        [Category("AeroAid")] public bool StackedLayout { get; set; }

        [Category("AeroAid")] public float GlyphSize { get; set; } = 16f;

        /// <summary>Secondary small line of text (e.g. keyboard hint).</summary>
        [Category("AeroAid")] public string Hint { get; set; } = "";

        public bool IsPressed => pressed;

        private static bool AnimationsOn
        {
            get
            {
                try { return AeroSettings.Animations; }
                catch (Exception) { return true; }
            }
        }

        private void StartAnim()
        {
            if (!AnimationsOn)
            {
                hover = mouseInside && Enabled ? 1 : 0;
                press = pressed ? 1 : 0;
                Invalidate();
                return;
            }

            if (!animTimer.Enabled)
                animTimer.Start();
        }

        private void AnimTick(object sender, EventArgs e)
        {
            var targetHover = mouseInside && Enabled ? 1f : 0f;
            var targetPress = pressed ? 1f : 0f;
            hover = Approach(hover, targetHover, 0.18f);
            press = Approach(press, targetPress, 0.35f);
            Invalidate();
            if (Math.Abs(hover - targetHover) < 0.001f && Math.Abs(press - targetPress) < 0.001f)
                animTimer.Stop();
        }

        private static float Approach(float v, float target, float step)
        {
            if (v < target) return Math.Min(target, v + step);
            if (v > target) return Math.Max(target, v - step);
            return v;
        }

        // ------------- input -------------
        protected override void OnMouseEnter(EventArgs e)
        {
            mouseInside = true;
            StartAnim();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            mouseInside = false;
            StartAnim();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || !Enabled)
                return;
            Focus();
            pressed = true;
            StartAnim();
            PressStarted?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left)
                return;
            var wasPressed = pressed;
            EndPress();
            if (wasPressed && Enabled && ClientRectangle.Contains(e.Location))
                OnClick(EventArgs.Empty);
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture)
                EndPress();
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            if (!Enabled)
                EndPress();
            StartAnim();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (!Visible)
                EndPress();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            EndPress();
        }

        private void EndPress()
        {
            if (!pressed)
                return;
            pressed = false;
            StartAnim();
            PressEnded?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Allows Enter to activate the focused button. OFF by default: in a ground station a stray key
        /// press must never trigger a vehicle command (keyboard flight control uses the gated shortcut
        /// system instead). Enable only for harmless buttons (navigation, settings).
        /// </summary>
        [Category("AeroAid")] public bool KeyboardActivation { get; set; }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (KeyboardActivation && e.KeyCode == Keys.Enter && Enabled)
                OnClick(EventArgs.Empty);
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                animTimer.Stop();
                animTimer.Dispose();
            }

            base.Dispose(disposing);
        }

        // ------------- painting -------------
        private void GetColors(out Color fill, out Color border, out Color text)
        {
            var p = BrandingManager.Palette;
            switch (style)
            {
                case AeroButtonStyle.Primary:
                    fill = AeroPalette.Blend(p.Accent, p.AccentHover, hover);
                    fill = AeroPalette.Blend(fill, p.AccentPressed, press);
                    border = fill;
                    text = p.Background;
                    break;
                case AeroButtonStyle.Danger:
                    fill = AeroPalette.Blend(AeroPalette.Blend(p.StatusCritical, p.Surface, 0.55f), p.StatusCritical, hover * 0.6f + press * 0.4f);
                    border = p.StatusCritical;
                    text = p.Text;
                    break;
                case AeroButtonStyle.Ghost:
                    fill = AeroPalette.Blend(Color.FromArgb(0, p.SurfaceAlt), p.SurfaceAlt, hover);
                    fill = AeroPalette.Blend(fill, AeroPalette.Blend(p.SurfaceAlt, p.Accent, 0.15f), press);
                    border = Color.Transparent;
                    text = AeroPalette.Blend(p.Muted, p.Text, hover);
                    break;
                default:
                    fill = AeroPalette.Blend(p.SurfaceAlt, AeroPalette.Blend(p.SurfaceAlt, p.Text, 0.08f), hover);
                    fill = AeroPalette.Blend(fill, AeroPalette.Blend(p.SurfaceAlt, p.Accent, 0.25f), press);
                    border = AeroPalette.Blend(Color.FromArgb(0x3A, 0x3A, 0x3A), p.Accent, hover * 0.6f);
                    text = p.Text;
                    break;
            }

            if (isActive)
            {
                if (style == AeroButtonStyle.Primary)
                {
                    border = p.Text;
                }
                else
                {
                    fill = AeroPalette.Blend(p.SurfaceAlt, p.Accent, 0.18f + hover * 0.07f);
                    border = p.Accent;
                    text = p.Accent;
                }
            }

            if (alertColor.HasValue && Enabled)
            {
                text = alertColor.Value;
                border = alertColor.Value;
            }

            if (!Enabled)
            {
                fill = AeroPalette.Blend(p.Surface, p.SurfaceAlt, 0.5f);
                border = Color.FromArgb(0x30, 0x30, 0x30);
                text = Color.FromArgb(0x60, 0x60, 0x60);
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // paint the parent's colour behind the rounded corners
            var parentColor = AeroDraw.OpaqueBackColor(this);
            if (BackColor.A == 255)
                parentColor = BackColor;
            using (var b = new SolidBrush(parentColor))
                e.Graphics.FillRectangle(b, ClientRectangle);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            GetColors(out var fill, out var border, out var text);

            // press animation: shrink slightly
            var inset = 1f + press * 1.5f;
            var rect = new RectangleF(inset, inset, Width - 1 - inset * 2, Height - 1 - inset * 2);
            if (rect.Width <= 2 || rect.Height <= 2)
                return;

            using (var path = AeroDraw.RoundedRect(rect, CornerRadius))
            {
                if (fill.A > 0)
                    using (var b = new SolidBrush(fill))
                        g.FillPath(b, path);
                if (border.A > 0)
                    using (var pen = new Pen(border, isActive ? 1.6f : 1f))
                        g.DrawPath(pen, path);
            }

            if (isActive && style == AeroButtonStyle.Ghost)
            {
                // left accent bar for active navigation items
                using (var b = new SolidBrush(BrandingManager.Palette.Accent))
                using (var bar = AeroDraw.RoundedRect(new RectangleF(rect.X + 3, rect.Y + rect.Height * 0.22f, 4, rect.Height * 0.56f), 2))
                    g.FillPath(b, bar);
            }

            var content = new Rectangle((int)rect.X + Padding.Left, (int)rect.Y + Padding.Top,
                (int)rect.Width - Padding.Horizontal, (int)rect.Height - Padding.Vertical);
            DrawContent(g, content, text);

            if (Focused && ShowFocusCues)
            {
                using (var pen = new Pen(Color.FromArgb(120, BrandingManager.Palette.Accent), 1f) { DashStyle = DashStyle.Dot })
                using (var path = AeroDraw.RoundedRect(RectangleF.Inflate(rect, -3, -3), Math.Max(2, CornerRadius - 3)))
                    g.DrawPath(pen, path);
            }
        }

        private void DrawContent(Graphics g, Rectangle content, Color textColor)
        {
            var hasGlyph = !string.IsNullOrEmpty(glyph);
            var iconFont = hasGlyph ? AeroIcons.CreateFont(GlyphSize) : null;
            try
            {
                using (var textBrush = new SolidBrush(textColor))
                {
                    if (StackedLayout)
                    {
                        var hintH = string.IsNullOrEmpty(Hint) ? 0 : 14;
                        var textH = string.IsNullOrEmpty(Text) ? 0 : (int)Math.Ceiling(Font.GetHeight(g)) + 2;
                        var iconH = iconFont != null ? (int)(GlyphSize * 1.2f) : 0;
                        var total = iconH + textH + hintH;
                        var y = content.Y + (content.Height - total) / 2;
                        var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                        if (iconFont != null)
                        {
                            g.DrawString(glyph, iconFont, textBrush, new RectangleF(content.X, y, content.Width, iconH), center);
                            y += iconH;
                        }

                        if (textH > 0)
                        {
                            g.DrawString(Text, Font, textBrush, new RectangleF(content.X, y, content.Width, textH), center);
                            y += textH;
                        }

                        if (hintH > 0)
                            using (var hf = new Font("Segoe UI", 7.5f))
                            using (var hb = new SolidBrush(Color.FromArgb(160, textColor)))
                                g.DrawString(Hint, hf, hb, new RectangleF(content.X, y, content.Width, hintH), center);
                        return;
                    }

                    var sf = new StringFormat
                    {
                        LineAlignment = StringAlignment.Center,
                        Trimming = StringTrimming.EllipsisCharacter,
                        FormatFlags = StringFormatFlags.NoWrap
                    };
                    var textSize = string.IsNullOrEmpty(Text) ? SizeF.Empty : g.MeasureString(Text, Font);
                    var iconW = iconFont != null ? GlyphSize + (string.IsNullOrEmpty(Text) ? 0 : 10) : 0;
                    float x;
                    switch (TextAlignment)
                    {
                        case ContentAlignment.MiddleLeft:
                        case ContentAlignment.TopLeft:
                        case ContentAlignment.BottomLeft:
                            x = content.X;
                            break;
                        default:
                            x = content.X + (content.Width - (iconW + textSize.Width)) / 2f;
                            break;
                    }

                    if (iconFont != null)
                    {
                        g.DrawString(glyph, iconFont, textBrush,
                            new RectangleF(x, content.Y, GlyphSize + 4, content.Height),
                            new StringFormat { LineAlignment = StringAlignment.Center, Alignment = StringAlignment.Near });
                        x += iconW;
                    }

                    if (!string.IsNullOrEmpty(Text))
                        g.DrawString(Text, Font, textBrush,
                            new RectangleF(x, content.Y, Math.Max(1, content.Right - x), content.Height), sf);
                }
            }
            finally
            {
                iconFont?.Dispose();
            }
        }
    }
}
