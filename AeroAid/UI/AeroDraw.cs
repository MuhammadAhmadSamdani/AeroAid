using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using MissionPlanner.AeroAid.Branding;

namespace MissionPlanner.AeroAid.UI
{
    /// <summary>Drawing and styling helpers shared by all AeroAid controls.</summary>
    public static class AeroDraw
    {
        public static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 1)
            {
                path.AddRectangle(r);
                return path;
            }

            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static Font UiFont(float size, FontStyle style = FontStyle.Regular) =>
            new Font(style.HasFlag(FontStyle.Bold) ? "Segoe UI Semibold" : "Segoe UI", size, style & ~FontStyle.Bold);

        public static Label Title(string text, float size = 18f) => new Label
        {
            Text = text,
            AutoSize = true,
            Font = UiFont(size, FontStyle.Bold),
            ForeColor = BrandingManager.Palette.Text,
            BackColor = Color.Transparent,
            UseMnemonic = false,
            Margin = new Padding(0, 0, 0, 6)
        };

        public static Label Caption(string text, float size = 9f) => new Label
        {
            Text = text,
            AutoSize = true,
            Font = UiFont(size),
            ForeColor = BrandingManager.Palette.Muted,
            BackColor = Color.Transparent,
            UseMnemonic = false,
            Margin = new Padding(0, 2, 0, 2)
        };

        public static Label Body(string text, float size = 10f) => new Label
        {
            Text = text,
            AutoSize = true,
            Font = UiFont(size),
            ForeColor = BrandingManager.Palette.Text,
            BackColor = Color.Transparent,
            UseMnemonic = false,
            Margin = new Padding(0, 2, 0, 2)
        };

        public static TextBox TextBox(string text = "", bool password = false)
        {
            var p = BrandingManager.Palette;
            return new TextBox
            {
                Text = text,
                BackColor = p.SurfaceAlt,
                ForeColor = p.Text,
                BorderStyle = BorderStyle.FixedSingle,
                Font = UiFont(10f),
                UseSystemPasswordChar = password,
                Width = 240,
                Margin = new Padding(0, 2, 0, 8)
            };
        }

        public static NumericUpDown Number(decimal value, decimal min, decimal max, int decimals = 1, decimal increment = 0.5m)
        {
            var p = BrandingManager.Palette;
            return new NumericUpDown
            {
                Minimum = min,
                Maximum = max,
                DecimalPlaces = decimals,
                Increment = increment,
                Value = Math.Max(min, Math.Min(max, value)),
                BackColor = p.SurfaceAlt,
                ForeColor = p.Text,
                BorderStyle = BorderStyle.FixedSingle,
                Font = UiFont(10f),
                Width = 110,
                Margin = new Padding(0, 2, 0, 8)
            };
        }

        public static ComboBox Combo()
        {
            var p = BrandingManager.Palette;
            return new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = p.SurfaceAlt,
                ForeColor = p.Text,
                FlatStyle = FlatStyle.Flat,
                Font = UiFont(10f),
                Width = 240,
                Margin = new Padding(0, 2, 0, 8)
            };
        }

        public static CheckBox Check(string text, bool value)
        {
            var p = BrandingManager.Palette;
            return new CheckBox
            {
                Text = text,
                Checked = value,
                AutoSize = true,
                ForeColor = p.Text,
                BackColor = Color.Transparent,
                Font = UiFont(10f),
                FlatStyle = FlatStyle.Flat,
                Margin = new Padding(0, 4, 0, 6)
            };
        }

        public static AeroButton Button(string text, string glyph = "", AeroButtonStyle style = AeroButtonStyle.Secondary,
            int width = 140, int height = 38) => new AeroButton
        {
            Text = text,
            Glyph = glyph,
            ButtonStyle = style,
            Size = new Size(width, height),
            Margin = new Padding(0, 0, 8, 8)
        };

        /// <summary>Two-column "label: control" form layout.</summary>
        public static TableLayoutPanel FormGrid()
        {
            var t = new TableLayoutPanel
            {
                ColumnCount = 2,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Color.Transparent,
                Dock = DockStyle.Top,
                Padding = new Padding(0),
                Margin = new Padding(0)
            };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            return t;
        }

        public static void AddRow(TableLayoutPanel grid, string label, Control control, string help = null)
        {
            var l = Caption(label, 9.5f);
            l.Anchor = AnchorStyles.Left;
            l.Margin = new Padding(0, 6, 8, 8);
            grid.Controls.Add(l);
            if (help != null)
            {
                var flow = new FlowLayoutPanel
                {
                    AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
                    BackColor = Color.Transparent, Margin = new Padding(0)
                };
                flow.Controls.Add(control);
                var h = Caption(help, 8.5f);
                h.Margin = new Padding(8, 6, 0, 0);
                flow.Controls.Add(h);
                grid.Controls.Add(flow);
            }
            else
            {
                grid.Controls.Add(control);
            }
        }

        public static void SafeInvoke(Control c, Action a)
        {
            if (c == null || c.IsDisposed || !c.IsHandleCreated)
                return;
            if (c.InvokeRequired)
            {
                try
                {
                    c.BeginInvoke(a);
                }
                catch (ObjectDisposedException)
                {
                }
                catch (InvalidOperationException)
                {
                }
            }
            else
            {
                a();
            }
        }
    }
}
