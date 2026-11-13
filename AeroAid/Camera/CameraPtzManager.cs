using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using MissionPlanner.AeroAid.UI;

namespace MissionPlanner.AeroAid.Camera
{
    public enum PtzDirection
    {
        Up,
        Down,
        Left,
        Right,
        ZoomIn,
        ZoomOut
    }

    /// <summary>
    /// Hold-to-move ONVIF PTZ control, completely independent of drone movement.
    /// A ContinuousMove is sent while a direction is held (refreshed every second) and Stop when released.
    /// A watchdog stops the camera if a direction is held for more than 30 s.
    /// </summary>
    public sealed class CameraPtzManager
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        public static CameraPtzManager Instance { get; } = new CameraPtzManager();

        private readonly object sync = new object();
        private readonly HashSet<PtzDirection> held = new HashSet<PtzDirection>();
        private readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);
        private readonly System.Threading.Timer refresh;
        private DateTime moveStarted = DateTime.MinValue;

        public event Action<string, StatusLevel> Feedback;
        public event Action MovementChanged;

        private CameraPtzManager()
        {
            refresh = new System.Threading.Timer(_ => RefreshTick(), null, Timeout.Infinite, Timeout.Infinite);
        }

        public IReadOnlyCollection<PtzDirection> Held
        {
            get
            {
                lock (sync) return held.ToArray();
            }
        }

        private void Report(string m, StatusLevel l)
        {
            log.Info(m);
            try
            {
                Feedback?.Invoke(m, l);
            }
            catch (Exception ex)
            {
                log.Error("PTZ feedback handler failed", ex);
            }
        }

        public static bool IsSupported(PtzDirection d, CameraCapabilities c)
        {
            if (!c.PtzAvailable) return false;
            return d == PtzDirection.ZoomIn || d == PtzDirection.ZoomOut ? c.ZoomAvailable : c.PanTiltAvailable;
        }

        public void BeginMove(PtzDirection d)
        {
            var caps = CameraConnectionManager.Capabilities;
            if (!IsSupported(d, caps))
            {
                Report(caps.PtzAvailable ? d + " is not supported by this camera." : caps.PtzMessage, StatusLevel.Warning);
                return;
            }

            lock (sync)
            {
                if (!held.Add(d)) return;
                if (held.Count == 1) moveStarted = DateTime.UtcNow;
            }

            RaiseChanged();
            _ = SendCurrentAsync();
            refresh.Change(1000, 1000);
        }

        public void EndMove(PtzDirection d)
        {
            bool changed;
            lock (sync) changed = held.Remove(d);
            if (!changed) return;
            RaiseChanged();
            _ = SendCurrentAsync();
        }

        public void StopAll()
        {
            bool had;
            lock (sync)
            {
                had = held.Count > 0;
                held.Clear();
            }

            if (!had) return;
            RaiseChanged();
            _ = SendCurrentAsync();
        }

        public async Task GoHomeAsync()
        {
            StopAll();
            var caps = CameraConnectionManager.Capabilities;
            if (!caps.PtzAvailable)
            {
                Report(caps.PtzMessage, StatusLevel.Warning);
                return;
            }

            if (!caps.HomeAvailable)
            {
                Report("The camera does not report a home position (Center is unavailable).", StatusLevel.Warning);
                return;
            }

            await sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                using (var onvif = NewClient())
                    await onvif.GotoHomeAsync(caps.PtzUrl, caps.ProfileToken, CancellationToken.None).ConfigureAwait(false);
                Report("Camera centred (home position).", StatusLevel.Ok);
            }
            catch (Exception ex)
            {
                Report("Center failed: " + ex.Message, StatusLevel.Critical);
            }
            finally
            {
                sendLock.Release();
            }
        }

        private static OnvifClient NewClient() =>
            new OnvifClient(CameraSettings.EffectiveHost, CameraSettings.OnvifPort, CameraSettings.Username, CameraSettings.Password, 3000);

        private void RaiseChanged()
        {
            try
            {
                MovementChanged?.Invoke();
            }
            catch (Exception ex)
            {
                log.Error("PTZ MovementChanged handler failed", ex);
            }
        }

        private void RefreshTick()
        {
            bool any;
            lock (sync)
            {
                any = held.Count > 0;
                if (any && (DateTime.UtcNow - moveStarted).TotalSeconds > 30)
                {
                    held.Clear();
                    any = false;
                    Report("Camera movement stopped automatically after 30 s.", StatusLevel.Warning);
                    RaiseChanged();
                }
            }

            if (!any)
                refresh.Change(Timeout.Infinite, Timeout.Infinite);
            _ = SendCurrentAsync();
        }

        private async Task SendCurrentAsync()
        {
            var caps = CameraConnectionManager.Capabilities;
            if (!caps.PtzAvailable) return;
            PtzDirection[] dirs;
            lock (sync) dirs = held.ToArray();
            var speed = CameraSettings.PtzSpeed;
            double pan = 0, tilt = 0, zoom = 0;
            foreach (var d in dirs)
            {
                switch (d)
                {
                    case PtzDirection.Left: pan -= speed; break;
                    case PtzDirection.Right: pan += speed; break;
                    case PtzDirection.Up: tilt += speed; break;
                    case PtzDirection.Down: tilt -= speed; break;
                    case PtzDirection.ZoomIn: zoom += speed; break;
                    case PtzDirection.ZoomOut: zoom -= speed; break;
                }
            }

            await sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                using (var onvif = NewClient())
                {
                    if (dirs.Length == 0)
                        await onvif.StopAsync(caps.PtzUrl, caps.ProfileToken, CancellationToken.None).ConfigureAwait(false);
                    else
                        await onvif.ContinuousMoveAsync(caps.PtzUrl, caps.ProfileToken, pan, tilt, zoom, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Report("PTZ command failed: " + ex.Message, StatusLevel.Critical);
            }
            finally
            {
                sendLock.Release();
            }
        }
    }
}
