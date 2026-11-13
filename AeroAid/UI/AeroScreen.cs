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

            Body = new Panel
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
                Dock = DockStyle.Right,
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

            Controls.Add(Body);
            Controls.Add(Header);
        }

        public string Subtitle
        {
            get => subtitleLabel.Text;
            set => subtitleLabel.Text = value;
        }

        public bool IsScreenActive { get; private set; }

        public void Activate()
        {
            IsScreenActive = true;
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
