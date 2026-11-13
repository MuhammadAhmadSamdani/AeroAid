using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using log4net;

namespace MissionPlanner.AeroAid.Camera
{
    /// <summary>What the configured camera actually supports, as detected at runtime (never assumed).</summary>
    public sealed class CameraCapabilities
    {
        public DateTime ProbedAt = DateTime.MinValue;
        public RtspProbeResult Rtsp = new RtspProbeResult { Status = RtspProbeStatus.NotConfigured, Message = "Not tested yet." };
        public bool OnvifAvailable;
        public string OnvifMessage = "Not tested yet.";
        public string Manufacturer = "";
        public string DeviceModel = "";
        public string Firmware = "";
        public string MediaUrl;
        public string PtzUrl;
        public string ProfileToken;
        public string OnvifStreamUri = "";
        public bool PtzAvailable;
        public bool PanTiltAvailable;
        public bool ZoomAvailable;
        public bool HomeAvailable;
        public string PtzMessage = "Not tested yet.";

        public bool Tested => ProbedAt != DateTime.MinValue;
    }

    /// <summary>
    /// Probes the configured camera: RTSP reachability/authentication/stream, then ONVIF device, media
    /// profiles and PTZ capabilities. The result drives which camera controls are enabled.
    /// </summary>
    public static class CameraConnectionManager
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly SemaphoreSlim probeLock = new SemaphoreSlim(1, 1);

        public static CameraCapabilities Capabilities { get; private set; } = new CameraCapabilities();

        public static bool Probing { get; private set; }

        /// <summary>Raised (on a worker thread) when a probe starts or completes.</summary>
        public static event Action CapabilitiesChanged;

        private static void Raise()
        {
            try
            {
                CapabilitiesChanged?.Invoke();
            }
            catch (Exception ex)
            {
                log.Error("CapabilitiesChanged handler failed", ex);
            }
        }

        public static async Task<CameraCapabilities> ProbeAsync(CancellationToken ct = default(CancellationToken))
        {
            if (!await probeLock.WaitAsync(0, ct).ConfigureAwait(false))
                return Capabilities; // a probe is already running
            Probing = true;
            Raise();
            var caps = new CameraCapabilities();
            try
            {
                var user = CameraSettings.Username;
                var password = CameraSettings.Password;

                // 1) RTSP
                if (!string.IsNullOrEmpty(CameraSettings.CustomPipeline))
                    caps.Rtsp = new RtspProbeResult { Status = RtspProbeStatus.NotConfigured, Message = "Custom GStreamer pipeline configured - RTSP probe skipped." };
                else
                    caps.Rtsp = await RtspProbe.DescribeAsync(CameraSettings.BuildRtspUrl(), user, password, ct).ConfigureAwait(false);
                log.Info("Camera RTSP probe: " + caps.Rtsp.Status + " " + caps.Rtsp.Message);

                // 2) ONVIF
                var host = CameraSettings.EffectiveHost;
                if (string.IsNullOrEmpty(host))
                {
                    caps.OnvifMessage = "No camera host configured.";
                    caps.PtzMessage = "PTZ unavailable: no camera host configured.";
                }
                else
                {
                    using (var onvif = new OnvifClient(host, CameraSettings.OnvifPort, user, password))
                    {
                        try
                        {
                            await onvif.SyncClockAsync(ct).ConfigureAwait(false);
                            var (media, ptz) = await onvif.GetCapabilitiesAsync(ct).ConfigureAwait(false);
                            caps.OnvifAvailable = true;
                            caps.MediaUrl = media;
                            caps.PtzUrl = ptz;
                            try
                            {
                                var info = await onvif.GetDeviceInformationAsync(ct).ConfigureAwait(false);
                                caps.Manufacturer = info.manufacturer;
                                caps.DeviceModel = info.model;
                                caps.Firmware = info.firmware;
                            }
                            catch (OnvifException ex)
                            {
                                log.Info("GetDeviceInformation: " + ex.Message);
                            }

                            if (media != null)
                            {
                                var profiles = await onvif.GetProfilesAsync(media, ct).ConfigureAwait(false);
                                var main = profiles.FirstOrDefault(p => p.HasPtzConfiguration) ?? profiles.FirstOrDefault();
                                caps.ProfileToken = main?.Token;
                                if (main != null)
                                {
                                    try
                                    {
                                        caps.OnvifStreamUri = CameraSettings.StripCredentials(await onvif.GetStreamUriAsync(media, main.Token, ct).ConfigureAwait(false));
                                    }
                                    catch (OnvifException ex)
                                    {
                                        log.Info("GetStreamUri: " + ex.Message);
                                    }
                                }

                                caps.OnvifMessage = $"ONVIF OK • {profiles.Count} media profile(s)" +
                                                    (main != null && main.Width > 0 ? $" • {main.Width}x{main.Height} {main.Encoding}" : "");
                            }
                            else
                            {
                                caps.OnvifMessage = "ONVIF device found but it reports no media service.";
                            }

                            if (ptz == null)
                            {
                                caps.PtzMessage = "This camera does not report an ONVIF PTZ service. Pan/tilt/zoom control is not available (the Imou cloud API is not used).";
                            }
                            else if (caps.ProfileToken == null)
                            {
                                caps.PtzMessage = "PTZ service found but no media profile to control.";
                            }
                            else
                            {
                                var node = await onvif.GetNodeCapabilitiesAsync(ptz, ct).ConfigureAwait(false);
                                caps.PanTiltAvailable = node.ContinuousPanTilt;
                                caps.ZoomAvailable = node.ContinuousZoom;
                                caps.HomeAvailable = node.HomeSupported;
                                caps.PtzAvailable = node.ContinuousPanTilt || node.ContinuousZoom;
                                caps.PtzMessage = caps.PtzAvailable
                                    ? "PTZ available • pan/tilt " + (node.ContinuousPanTilt ? "yes" : "no") +
                                      " • zoom " + (node.ContinuousZoom ? "yes" : "no") +
                                      " • home " + (node.HomeSupported ? "yes" : "no")
                                    : "PTZ service present but no continuous pan/tilt/zoom support was reported.";
                            }
                        }
                        catch (OnvifException ex)
                        {
                            caps.OnvifAvailable = false;
                            caps.OnvifMessage = ex.Message + " (ONVIF may need enabling in the Imou Life app; check the ONVIF port).";
                            caps.PtzMessage = "PTZ unavailable: " + ex.Message;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                caps.OnvifMessage = "Probe cancelled.";
            }
            catch (Exception ex)
            {
                log.Error("Camera probe failed", ex);
                caps.OnvifMessage = "Camera probe failed: " + ex.Message;
            }
            finally
            {
                caps.ProbedAt = DateTime.Now;
                Capabilities = caps;
                Probing = false;
                probeLock.Release();
                Raise();
            }

            return caps;
        }
    }
}
