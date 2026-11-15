using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MissionPlanner.AeroAid.Input;
using MissionPlanner.Utilities;

namespace MissionPlanner.AeroAid.Tests
{
    [TestClass]
    public class KeyboardShortcutTests
    {
        [ClassInitialize]
        public static void Init(TestContext _) => TestEnv.Init();

        [TestInitialize]
        public void Reset() => ShortcutMap.RestoreDefaults();

        [TestMethod]
        public void Defaults_MatchSpecification()
        {
            Assert.AreEqual(Keys.W, ShortcutMap.KeyFor(ShortcutAction.Forward));
            Assert.AreEqual(Keys.S, ShortcutMap.KeyFor(ShortcutAction.Backward));
            Assert.AreEqual(Keys.A, ShortcutMap.KeyFor(ShortcutAction.Left));
            Assert.AreEqual(Keys.D, ShortcutMap.KeyFor(ShortcutAction.Right));
            Assert.AreEqual(Keys.R, ShortcutMap.KeyFor(ShortcutAction.Up));
            Assert.AreEqual(Keys.F, ShortcutMap.KeyFor(ShortcutAction.Down));
            Assert.AreEqual(Keys.Q, ShortcutMap.KeyFor(ShortcutAction.YawLeft));
            Assert.AreEqual(Keys.E, ShortcutMap.KeyFor(ShortcutAction.YawRight));
            Assert.AreEqual(Keys.Space, ShortcutMap.KeyFor(ShortcutAction.Stop));
            Assert.AreEqual(Keys.D6, ShortcutMap.KeyFor(ShortcutAction.ModeRtl));
            Assert.AreEqual(Keys.D7, ShortcutMap.KeyFor(ShortcutAction.ModeLand));
            Assert.AreEqual(Keys.Up, ShortcutMap.KeyFor(ShortcutAction.CameraUp));
            Assert.AreEqual(Keys.C, ShortcutMap.KeyFor(ShortcutAction.CameraCenter));
        }

        [TestMethod]
        public void DefaultsHaveNoDuplicateKeys()
        {
            var seen = new System.Collections.Generic.HashSet<Keys>();
            foreach (ShortcutAction a in System.Enum.GetValues(typeof(ShortcutAction)))
                Assert.IsTrue(seen.Add(ShortcutMap.Normalize(ShortcutMap.KeyFor(a))), "duplicate key for " + a);
        }

        [TestMethod]
        public void NumpadKeysAreEquivalent()
        {
            Assert.AreEqual(ShortcutAction.ZoomIn, ShortcutMap.ActionFor(Keys.Add));
            Assert.AreEqual(ShortcutAction.ZoomOut, ShortcutMap.ActionFor(Keys.Subtract));
            Assert.AreEqual(ShortcutAction.ModeRtl, ShortcutMap.ActionFor(Keys.NumPad6));
            Assert.IsNull(ShortcutMap.ActionFor(Keys.F9));
        }

        [TestMethod]
        public void ConflictsAndReservedKeysAreRejected()
        {
            StringAssert.Contains(ShortcutMap.TryAssign(ShortcutAction.Forward, Keys.S), "already used");
            StringAssert.Contains(ShortcutMap.TryAssign(ShortcutAction.Forward, Keys.F12), "reserved");
            StringAssert.Contains(ShortcutMap.TryAssign(ShortcutAction.Forward, Keys.Enter), "reserved");
            Assert.AreEqual(Keys.W, ShortcutMap.KeyFor(ShortcutAction.Forward), "unchanged after rejected assignments");
            Assert.IsNull(ShortcutMap.TryAssign(ShortcutAction.Forward, Keys.I));
        }

        [TestMethod]
        public void CustomShortcutsPersistAndRestoreDefaults()
        {
            Assert.IsNull(ShortcutMap.TryAssign(ShortcutAction.Forward, Keys.I));
            ShortcutMap.Save();
            ShortcutMap.Load(); // re-read from settings
            Assert.AreEqual(Keys.I, ShortcutMap.KeyFor(ShortcutAction.Forward));
            StringAssert.Contains(System.IO.File.ReadAllText(TestEnv.ConfigFile), "aeroaid.keys.map");
            ShortcutMap.RestoreDefaults();
            Assert.AreEqual(Keys.W, ShortcutMap.KeyFor(ShortcutAction.Forward));
        }

        [TestMethod]
        public void CorruptShortcutSettingFallsBackToDefaults()
        {
            Settings.Instance["aeroaid.keys.map"] = "{ this is not json";
            ShortcutMap.Load();
            Assert.AreEqual(Keys.W, ShortcutMap.KeyFor(ShortcutAction.Forward));
            Settings.Instance["aeroaid.keys.map"] = "{\"Forward\":\"F12\"}"; // reserved key in stored settings
            ShortcutMap.Load();
            Assert.AreEqual(Keys.W, ShortcutMap.KeyFor(ShortcutAction.Forward));
        }

        [TestMethod]
        public void KeyboardFilter_IgnoresKeysWhenWindowIsNotActive()
        {
            using (var form = new Form())
            using (var switcher = new MissionPlanner.Controls.MainSwitcher(form))
            {
                KeyboardControlManager.Instance.Install(form, switcher);
                try
                {
                    var m = Message.Create(form.Handle, 0x100, (System.IntPtr)(int)Keys.W, (System.IntPtr)0x00110001);
                    Assert.IsFalse(KeyboardControlManager.Instance.PreFilterMessage(ref m), "key must not be consumed");
                    Assert.IsFalse(MissionPlanner.AeroAid.Flight.FlightControlManager.Instance.IsMoving);
                    StringAssert.Contains(KeyboardControlManager.Instance.Availability(out var f, out var c), "Inactive");
                    Assert.IsFalse(f);
                    Assert.IsFalse(c);
                }
                finally
                {
                    Application.RemoveMessageFilter(KeyboardControlManager.Instance);
                }
            }
        }
    }
}
