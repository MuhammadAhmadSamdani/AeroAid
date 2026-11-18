using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MissionPlanner.AeroAid.Branding;
using MissionPlanner.AeroAid.UI;
using MissionPlanner.AeroAid.Views;

namespace MissionPlanner.AeroAid.Tests
{
    /// <summary>
    /// The AEROAID logo (Assets/Branding/aeroaid.png) is the single logo source and is never distorted.
    /// Set AEROAID_SCREENSHOTS to a folder to also save renders of every logo location.
    /// </summary>
    [TestClass]
    public class BrandingLogoTests
    {
        private static void RunSta(Action a)
        {
            Exception error = null;
            var t = new Thread(() =>
            {
                try { a(); }
                catch (Exception ex) { error = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (error != null) throw new AssertFailedException("UI test failed: " + error, error);
        }

        private static readonly object sync = new object();
        private static bool installed;

        /// <summary>
        /// Tests run with an isolated data/running directory: put the real branding folder (copied next to the test
        /// assembly by the build) there and reload, so the tests use exactly Assets/Branding/aeroaid.png.
        /// </summary>
        private static void InstallBranding()
        {
            TestEnv.Init();
            lock (sync)
            {
                if (installed) return;
                var src = Path.Combine(Path.GetDirectoryName(typeof(BrandingLogoTests).Assembly.Location) ?? "", "Assets", "Branding");
                Directory.CreateDirectory(BrandingManager.BrandingDirectory);
                foreach (var f in Directory.GetFiles(src))
                    File.Copy(f, Path.Combine(BrandingManager.BrandingDirectory, Path.GetFileName(f)), true);
                BrandingManager.Reload();
                installed = true;
            }
        }

        private static void Save(Bitmap bmp, string name)
        {
            var dir = Environment.GetEnvironmentVariable("AEROAID_SCREENSHOTS");
            if (string.IsNullOrEmpty(dir)) return;
            Directory.CreateDirectory(dir);
            bmp.Save(Path.Combine(dir, name), ImageFormat.Png);
        }

        [TestMethod]
        public void CentralLogoIsAeroaidPngAndLoads()
        {
            InstallBranding();
            Assert.AreEqual("aeroaid.png", BrandingManager.DefaultLogoFile);
            Assert.IsNotNull(BrandingManager.Logo, "logo not found at " + BrandingManager.LogoPath);
            StringAssert.EndsWith(BrandingManager.LogoPath, "aeroaid.png");
            Assert.IsFalse(File.Exists(Path.Combine(BrandingManager.BrandingDirectory, "logo.png")), "old placeholder logo must be gone");
            // the transparent margin is trimmed, the artwork is not: the wordmark is about 4:1
            Assert.IsTrue(BrandingManager.LogoAspect > 3.5 && BrandingManager.LogoAspect < 4.6, "aspect " + BrandingManager.LogoAspect);
        }

        [DataTestMethod]
        [DataRow(200, 31)]
        [DataRow(192, 46)]
        [DataRow(400, 96)]
        [DataRow(48, 48)]
        [DataRow(1000, 100)]
        public void FittedLogoKeepsTheAspectRatioAndFitsTheBox(int w, int h)
        {
            InstallBranding();
            var bmp = BrandingManager.GetLogo(w, h);
            Assert.IsTrue(bmp.Width <= w && bmp.Height <= h, $"{bmp.Width}x{bmp.Height} in {w}x{h}");
            Assert.IsTrue(bmp.Width == w || bmp.Height == h, "fills one dimension");
            Assert.AreEqual(BrandingManager.LogoAspect, (double)bmp.Width / bmp.Height, BrandingManager.LogoAspect * 0.05,
                "never stretched");
            Assert.AreEqual(PixelFormat.Format32bppArgb, bmp.PixelFormat);
            Assert.AreEqual(0, bmp.GetPixel(0, 0).A, "transparent background");
        }

        [TestMethod]
        public void IconSplashSidebarAndAboutShowTheLogo()
        {
            InstallBranding();
            var icon = BrandingManager.GetIconBitmap(256);
            Assert.AreEqual(256, icon.Width);
            Save(icon, "branding_icon_256.png");
            Assert.IsNotNull(BrandingManager.AppIcon);
            var splash = BrandingManager.CreateSplashImage(584, 336);
            Save(splash, "branding_splash.png");

            RunSta(() =>
            {
                foreach (var size in new[] { new Size(1024, 680), new Size(1920, 1080) })
                {
                    using (var form = new Form { Size = size, BackColor = BrandingManager.Palette.Background })
                    {
                        var bar = new Sidebar();
                        bar.AddItem("x", "Dashboard", AeroIcons.Dashboard, () => { });
                        var about = new AboutView { Dock = DockStyle.Fill };
                        form.Controls.Add(about);
                        form.Controls.Add(bar);
                        form.CreateControl();
                        form.PerformLayout();
                        using (var bmp = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
                        {
                            form.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.ClientSize));
                            Save(bmp, $"branding_sidebar_about_{size.Width}x{size.Height}.png");
                        }
                    }
                }
            });
        }
    }
}
