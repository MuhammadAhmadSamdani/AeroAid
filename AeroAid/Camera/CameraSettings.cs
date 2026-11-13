using System;

namespace MissionPlanner.AeroAid.Camera
{
    /// <summary>
    /// Camera configuration (persisted via AeroSettings; the password is DPAPI-encrypted).
    /// Defaults follow the common IMOU/Dahua RTSP layout:
    ///   rtsp://HOST:554/cam/realmonitor?channel=1&amp;subtype=0   (subtype=1 = sub/low-quality stream)
    /// IMOU cameras usually use user "admin" and either the RTSP/ONVIF password set in the Imou Life app
    /// or the "safety code" printed on the camera label. RTSP/ONVIF may need enabling in the Imou Life app.
    /// </summary>
    public static class CameraSettings
    {
        public const string DefaultRtspPath = "/cam/realmonitor?channel=1&subtype={subtype}";

        public static string Model
        {
            get => AeroSettings.GetString("camera.model", "IMOU K2EP-5R1W");
            set => AeroSettings.SetString("camera.model", value);
        }

        public static string Host
        {
            get => AeroSettings.GetString("camera.host", "").Trim();
            set => AeroSettings.SetString("camera.host", (value ?? "").Trim());
        }

        public static int RtspPort
        {
            get => AeroSettings.GetInt("camera.rtsp_port", 554, 1, 65535);
            set => AeroSettings.SetInt("camera.rtsp_port", value);
        }

        public static int OnvifPort
        {
            get => AeroSettings.GetInt("camera.onvif_port", 80, 1, 65535);
            set => AeroSettings.SetInt("camera.onvif_port", value);
        }

        public static string Username
        {
            get => AeroSettings.GetString("camera.user", "admin");
            set => AeroSettings.SetString("camera.user", value ?? "");
        }

        /// <summary>Stored encrypted (DPAPI, current Windows user). Never logged.</summary>
        public static string Password
        {
            get => AeroSettings.GetSecret("camera.password");
            set => AeroSettings.SetSecret("camera.password", value);
        }

        public static bool HasPassword => AeroSettings.HasSecret("camera.password");

        /// <summary>RTSP path; "{subtype}" is replaced by 0 (main) or 1 (sub stream).</summary>
        public static string RtspPath
        {
            get => AeroSettings.GetString("camera.rtsp_path", DefaultRtspPath);
            set => AeroSettings.SetString("camera.rtsp_path", string.IsNullOrWhiteSpace(value) ? DefaultRtspPath : value.Trim());
        }

        /// <summary>Full RTSP URL (without credentials) - overrides host/port/path when set.</summary>
        public static string RtspUrlOverride
        {
            get => AeroSettings.GetString("camera.rtsp_url", "").Trim();
            set => AeroSettings.SetString("camera.rtsp_url", (value ?? "").Trim());
        }

        /// <summary>true = sub stream (lower resolution, lower latency/bandwidth).</summary>
        public static bool UseSubStream
        {
            get => AeroSettings.GetBool("camera.sub_stream", false);
            set => AeroSettings.SetBool("camera.sub_stream", value);
        }

        public static int LatencyMs
        {
            get => AeroSettings.GetInt("camera.latency", 200, 0, 5000);
            set => AeroSettings.SetInt("camera.latency", value);
        }

        public static bool UseTcp
        {
            get => AeroSettings.GetBool("camera.tcp", true);
            set => AeroSettings.SetBool("camera.tcp", value);
        }

        /// <summary>Plays the camera's own microphone audio (if its RTSP stream has an audio track).</summary>
        public static bool PlayCameraAudio
        {
            get => AeroSettings.GetBool("camera.audio", false);
            set => AeroSettings.SetBool("camera.audio", value);
        }

        /// <summary>PTZ speed 0.1 .. 1.0 (fraction of the camera's maximum).</summary>
        public static double PtzSpeed
        {
            get => AeroSettings.GetDouble("camera.ptz_speed", 0.5, 0.1, 1.0);
            set => AeroSettings.SetDouble("camera.ptz_speed", value);
        }

        /// <summary>
        /// Advanced: complete GStreamer source pipeline that replaces the RTSP source (e.g. a UDP/H.264
        /// stream from a companion computer). It MUST end with
        /// "videoconvert ! video/x-raw,format=BGRA ! appsink name=outsink".
        /// </summary>
        public static string CustomPipeline
        {
            get => AeroSettings.GetString("camera.pipeline", "").Trim();
            set => AeroSettings.SetString("camera.pipeline", (value ?? "").Trim());
        }

        public static bool IsConfigured =>
            !string.IsNullOrEmpty(CustomPipeline) || !string.IsNullOrEmpty(RtspUrlOverride) || !string.IsNullOrEmpty(Host);

        /// <summary>RTSP URL WITHOUT credentials (safe to display/log).</summary>
        public static string BuildRtspUrl()
        {
            if (!string.IsNullOrEmpty(RtspUrlOverride))
                return StripCredentials(RtspUrlOverride);
            if (string.IsNullOrEmpty(Host))
                return "";
            var path = RtspPath.Replace("{subtype}", UseSubStream ? "1" : "0");
            if (!path.StartsWith("/"))
                path = "/" + path;
            return $"rtsp://{Host}:{RtspPort}{path}";
        }

        /// <summary>Removes any user:password@ part from a URL.</summary>
        public static string StripCredentials(string url)
        {
            try
            {
                var at = url.IndexOf('@');
                var scheme = url.IndexOf("://", StringComparison.Ordinal);
                if (at > 0 && scheme > 0 && at > scheme)
                    return url.Substring(0, scheme + 3) + url.Substring(at + 1);
            }
            catch (Exception)
            {
                // fall through
            }

            return url;
        }

        /// <summary>Host to use for ONVIF (configured host, or the host part of the RTSP override).</summary>
        public static string EffectiveHost
        {
            get
            {
                if (!string.IsNullOrEmpty(Host))
                    return Host;
                try
                {
                    if (!string.IsNullOrEmpty(RtspUrlOverride))
                        return new Uri(StripCredentials(RtspUrlOverride)).Host;
                }
                catch (UriFormatException)
                {
                }

                return "";
            }
        }
    }
}
