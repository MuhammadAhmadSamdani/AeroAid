using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MissionPlanner.AeroAid.Views;
using MissionPlanner.AeroAid.Voice;

namespace MissionPlanner.AeroAid.Tests
{
    /// <summary>Builds the Voice &amp; Microphone page off-screen, feeds it recognition results and renders it.</summary>
    [TestClass]
    public class VoiceViewSmokeTests
    {
        private static void RunSta(Action a)
        {
            Exception error = null;
            var t = new Thread(() =>
            {
                try
                {
                    a();
                }
                catch (Exception ex)
                {
                    error = ex;
                }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (error != null) throw new AssertFailedException("UI smoke test failed: " + error, error);
        }

        [TestMethod]
        public void VoicePageBuildsAndShowsDetectedCommands()
        {
            TestEnv.Init();
            VoiceSettings.MinConfidence = 0.65;
            RunSta(() =>
            {
                using (var view = new VoiceView { Size = new Size(1000, 1250) })
                {
                    view.CreateControl();
                    var refresh = typeof(VoiceView).GetMethod("RefreshUi", BindingFlags.NonPublic | BindingFlags.Instance);
                    Assert.IsNotNull(refresh);
                    refresh.Invoke(view, null);

                    var shots = Environment.GetEnvironmentVariable("AEROAID_SCREENSHOTS");
                    var n = 0;
                    foreach (var (text, conf) in new[] { ("drone please arm", 0.93f), ("do not take off", 0.93f), ("disarm", 0.40f) })
                    {
                        var u = VoiceCommandEngine.Instance.ProcessFinal(text, conf, VoiceCommandEngine.CommandGrammarName,
                            TimeSpan.FromSeconds(100 + 10 * ++n));
                        Assert.IsNotNull(u);
                        refresh.Invoke(view, null);
                        if (string.IsNullOrEmpty(shots)) continue;
                        Directory.CreateDirectory(shots);
                        using (var bmp = new Bitmap(view.Width, view.Height))
                        {
                            view.DrawToBitmap(bmp, new Rectangle(Point.Empty, view.Size));
                            bmp.Save(Path.Combine(shots, $"voice_page_{n}.png"), ImageFormat.Png);
                        }
                    }
                }
            });
        }
    }
}
