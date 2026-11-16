using System;
using System.Drawing;
using System.Windows.Forms;
using MissionPlanner.AeroAid.Branding;
using MissionPlanner.Controls;
using MissionPlanner.Utilities;

namespace MissionPlanner.AeroAid.UI
{
    /// <summary>
    /// Base class for AeroAid screens hosted by Mission Planner's MainSwitcher.
    /// Excluded from Mission Planner's automatic theming (styled by AeroAid instead).
    /// Provides a header (title + subtitle + optional right-aligned toolbar) and a scrollable body.
    /// </summary>
    [PreventTheming]
    public class AeroScreen : MyUserControl, IActivate, IDeactivate
    {
        protected readonly Panel Header;
        protected readonly FlowLayoutPanel HeaderTools;
        protected readonly Panel Body;
        private readonly Label titleLabel;
        private readonly Label subtitleLabel;

        public AeroScreen(string title, string subtitle)
        {
            var p = BrandingManager.Palette;
            DoubleBuffered = true;
            BackColor = p.Background;
            ForeColor = p.Text;
            Font = AeroDraw.UiFont(9.5f);

            Body = new NoJumpPanel
            {
                Dock = DockStyle.Fill,
                BackColor = p.Background,
                Padding = new Padding(18, 6, 18, 18),
                AutoScroll = true
            };

            Header = new Panel { Dock = DockStyle.Top, Height = 74, BackColor = p.Background, Padding = new Padding(22, 14, 18, 6) };
            titleLabel = AeroDraw.Title(title, 17f);
            titleLabel.Location = new Point(22, 12);
            subtitleLabel = AeroDraw.Caption(subtitle, 9.5f);
            subtitleLabel.Location = new Point(24, 44);
            HeaderTools = new FlowLayoutPanel
            {
                // positioned by LayoutHeader(): right-aligned next to the title, or on a second row when narrow
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                BackColor = p.Background,
                Padding = new Padding(0, 10, 0, 0)
            };
            Header.Controls.Add(HeaderTools);
            Header.Controls.Add(titleLabel);
            Header.Controls.Add(subtitleLabel);
            Header.Layout += (s, e) => LayoutHeader();

            Controls.Add(Body);
            Controls.Add(Header);
        }

        private const int HeaderBaseHeight = 74;
        private bool layingOutHeader;

        /// <summary>
        /// Keeps the toolbar from overlapping the title/subtitle on narrow windows: if it does not fit beside the
        /// text it moves to a second row under the subtitle and the header grows to fit.
        /// </summary>
        private void LayoutHeader()
        {
            if (layingOutHeader)
                return;
            layingOutHeader = true;
            try
            {
                var tools = HeaderTools.PreferredSize;
                var right = Header.ClientSize.Width - Header.Padding.Right;
                var textRight = Math.Max(titleLabel.Right, subtitleLabel.Right) + 16;
                var hasTools = HeaderTools.Controls.Count > 0;
                if (!hasTools || right - tools.Width >= textRight)
                {
                    HeaderTools.Location = new Point(Math.Max(0, right - tools.Width), Header.Padding.Top);
                    if (Header.Height != HeaderBaseHeight) Header.Height = HeaderBaseHeight;
                }
                else
                {
                    var top = subtitleLabel.Bottom - 4;
                    HeaderTools.Location = new Point(Math.Max(Header.Padding.Left, right - tools.Width), top);
                    var h = top + tools.Height + Header.Padding.Bottom;
                    if (Header.Height != h) Header.Height = h;
                }
            }
            finally
            {
                layingOutHeader = false;
            }
        }

        public string Subtitle
        {
            get => subtitleLabel.Text;
            set => subtitleLabel.Text = value;
        }

        public bool IsScreenActive { get; private set; }

        /// <summary>Scrollable panel that does not jump to whichever child receives focus.</summary>
        protected sealed class NoJumpPanel : Panel
        {
            protected override Point ScrollToControl(Control activeControl) => DisplayRectangle.Location;
        }

        public void Activate()
        {
            IsScreenActive = true;
            if (Body.AutoScroll)
                Body.AutoScrollPosition = Point.Empty;
            try
            {
                OnScreenActivated();
            }
            catch (Exception ex)
            {
                log4net.LogManager.GetLogger(GetType()).Error("Activate failed", ex);
            }
        }

        public void Deactivate()
        {
            IsScreenActive = false;
            try
            {
                OnScreenDeactivated();
            }
            catch (Exception ex)
            {
                log4net.LogManager.GetLogger(GetType()).Error("Deactivate failed", ex);
            }
        }

        /// <summary>Called when the screen becomes visible (subscribe to data sources here).</summary>
        protected virtual void OnScreenActivated()
        {
        }

        /// <summary>Called when the screen is hidden (unsubscribe, stop any held movement).</summary>
        protected virtual void OnScreenDeactivated()
        {
        }
    }
}
