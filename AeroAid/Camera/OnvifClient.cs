using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace MissionPlanner.AeroAid.Camera
{
    public class OnvifException : Exception
    {
        public OnvifException(string message) : base(message)
        {
        }
    }

    public sealed class OnvifProfile
    {
        public string Token = "";
        public string Name = "";
        public int Width;
        public int Height;
        public string Encoding = "";
        public bool HasPtzConfiguration;
    }

    public sealed class OnvifPtzNode
    {
        public bool ContinuousPanTilt;
        public bool ContinuousZoom;
        public bool HomeSupported;
    }

    /// <summary>
    /// Small, dependency-free ONVIF SOAP 1.2 client (Device, Media ver10, PTZ ver20) with WS-Security
    /// UsernameToken digest authentication (HTTP Digest is also answered automatically).
    /// Only the operations AeroAid needs are implemented.
    /// </summary>
    public sealed class OnvifClient : IDisposable
    {
        private const string NsDevice = "http://www.onvif.org/ver10/device/wsdl";
        private const string NsMedia = "http://www.onvif.org/ver10/media/wsdl";
        private const string NsPtz = "http://www.onvif.org/ver20/ptz/wsdl";
        private const string NsSchema = "http://www.onvif.org/ver10/schema";

        private readonly HttpClient http;
        private readonly string host;
        private readonly int port;
        private readonly string user;
        private readonly string password;
        private TimeSpan clockOffset = TimeSpan.Zero;

        public OnvifClient(string host, int port, string user, string password, int timeoutMs = 5000)
        {
            this.host = host;
            this.port = port;
            this.user = user ?? "";
            this.password = password ?? "";
            var handler = new HttpClientHandler { Credentials = new NetworkCredential(this.user, this.password), PreAuthenticate = false };
            http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
        }

        public string DeviceServiceUrl => $"http://{host}:{port}/onvif/device_service";

        public void Dispose() => http.Dispose();

        /// <summary>Re-targets an XAddr at the configured host/port (cameras often report an internal address).</summary>
        public string Normalize(string xaddr)
        {
            try
            {
                var u = new Uri(xaddr);
                return $"http://{host}:{port}{u.PathAndQuery}";
            }
            catch (UriFormatException)
            {
                return xaddr;
            }
        }

        // ---------------- device ----------------
        /// <summary>Unauthenticated: reads the camera clock so the WS-Security timestamp matches it.</summary>
        public async Task SyncClockAsync(CancellationToken ct)
        {
            try
            {
                var doc = await CallAsync(DeviceServiceUrl, $"<GetSystemDateAndTime xmlns=\"{NsDevice}\"/>", false, ct).ConfigureAwait(false);
                var utc = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "UTCDateTime");
                if (utc == null) return;
                int Get(string parent, string name) =>
                    int.Parse(utc.Descendants().First(e => e.Name.LocalName == parent).Elements().First(e => e.Name.LocalName == name).Value, CultureInfo.InvariantCulture);
                var camera = new DateTime(Get("Date", "Year"), Get("Date", "Month"), Get("Date", "Day"),
                    Get("Time", "Hour"), Get("Time", "Minute"), Get("Time", "Second"), DateTimeKind.Utc);
                clockOffset = camera - DateTime.UtcNow;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                clockOffset = TimeSpan.Zero; // not fatal - many cameras accept any timestamp
            }
        }

        public async Task<(string manufacturer, string model, string firmware)> GetDeviceInformationAsync(CancellationToken ct)
        {
            var doc = await CallAsync(DeviceServiceUrl, $"<GetDeviceInformation xmlns=\"{NsDevice}\"/>", true, ct).ConfigureAwait(false);
            return (Value(doc, "Manufacturer"), Value(doc, "Model"), Value(doc, "FirmwareVersion"));
        }

        public async Task<(string mediaUrl, string ptzUrl)> GetCapabilitiesAsync(CancellationToken ct)
        {
            var doc = await CallAsync(DeviceServiceUrl,
                $"<GetCapabilities xmlns=\"{NsDevice}\"><Category>All</Category></GetCapabilities>", true, ct).ConfigureAwait(false);
            string XAddrOf(string section)
            {
                var el = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == section && e.Elements().Any(c => c.Name.LocalName == "XAddr"));
                var x = el?.Elements().First(c => c.Name.LocalName == "XAddr").Value;
                return string.IsNullOrWhiteSpace(x) ? null : Normalize(x.Trim());
            }

            return (XAddrOf("Media"), XAddrOf("PTZ"));
        }

        // ---------------- media ----------------
        public async Task<List<OnvifProfile>> GetProfilesAsync(string mediaUrl, CancellationToken ct)
        {
            var doc = await CallAsync(mediaUrl, $"<GetProfiles xmlns=\"{NsMedia}\"/>", true, ct).ConfigureAwait(false);
            var list = new List<OnvifProfile>();
            foreach (var p in doc.Descendants().Where(e => e.Name.LocalName == "Profiles"))
            {
                var prof = new OnvifProfile
                {
                    Token = (string)p.Attribute("token") ?? "",
                    Name = p.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value ?? "",
                    HasPtzConfiguration = p.Elements().Any(e => e.Name.LocalName == "PTZConfiguration")
                };
                var enc = p.Elements().FirstOrDefault(e => e.Name.LocalName == "VideoEncoderConfiguration");
                if (enc != null)
                {
                    prof.Encoding = enc.Elements().FirstOrDefault(e => e.Name.LocalName == "Encoding")?.Value ?? "";
                    var res = enc.Elements().FirstOrDefault(e => e.Name.LocalName == "Resolution");
                    if (res != null)
                    {
                        int.TryParse(res.Elements().FirstOrDefault(e => e.Name.LocalName == "Width")?.Value, out prof.Width);
                        int.TryParse(res.Elements().FirstOrDefault(e => e.Name.LocalName == "Height")?.Value, out prof.Height);
                    }
                }

                list.Add(prof);
            }

            return list;
        }

        public async Task<string> GetStreamUriAsync(string mediaUrl, string profileToken, CancellationToken ct)
        {
            var body = $"<GetStreamUri xmlns=\"{NsMedia}\"><StreamSetup><Stream xmlns=\"{NsSchema}\">RTP-Unicast</Stream>" +
                       $"<Transport xmlns=\"{NsSchema}\"><Protocol>RTSP</Protocol></Transport></StreamSetup>" +
                       $"<ProfileToken>{Esc(profileToken)}</ProfileToken></GetStreamUri>";
            var doc = await CallAsync(mediaUrl, body, true, ct).ConfigureAwait(false);
            return Value(doc, "Uri");
        }

        // ---------------- PTZ ----------------
        public async Task<OnvifPtzNode> GetNodeCapabilitiesAsync(string ptzUrl, CancellationToken ct)
        {
            var doc = await CallAsync(ptzUrl, $"<GetNodes xmlns=\"{NsPtz}\"/>", true, ct).ConfigureAwait(false);
            var node = new OnvifPtzNode
            {
                ContinuousPanTilt = doc.Descendants().Any(e => e.Name.LocalName == "ContinuousPanTiltVelocitySpace"),
                ContinuousZoom = doc.Descendants().Any(e => e.Name.LocalName == "ContinuousZoomVelocitySpace"),
                HomeSupported = doc.Descendants().Any(e => e.Name.LocalName == "HomeSupported" &&
                                                          string.Equals(e.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase))
            };
            return node;
        }

        public Task ContinuousMoveAsync(string ptzUrl, string profileToken, double pan, double tilt, double zoom, CancellationToken ct)
        {
            var sb = new StringBuilder();
            sb.Append($"<ContinuousMove xmlns=\"{NsPtz}\"><ProfileToken>{Esc(profileToken)}</ProfileToken><Velocity>");
            if (Math.Abs(pan) > 1e-6 || Math.Abs(tilt) > 1e-6)
                sb.Append($"<PanTilt xmlns=\"{NsSchema}\" x=\"{F(pan)}\" y=\"{F(tilt)}\"/>");
            if (Math.Abs(zoom) > 1e-6)
                sb.Append($"<Zoom xmlns=\"{NsSchema}\" x=\"{F(zoom)}\"/>");
            sb.Append("</Velocity></ContinuousMove>");
            return CallAsync(ptzUrl, sb.ToString(), true, ct);
        }

        public Task StopAsync(string ptzUrl, string profileToken, CancellationToken ct) =>
            CallAsync(ptzUrl, $"<Stop xmlns=\"{NsPtz}\"><ProfileToken>{Esc(profileToken)}</ProfileToken><PanTilt>true</PanTilt><Zoom>true</Zoom></Stop>", true, ct);

        public Task GotoHomeAsync(string ptzUrl, string profileToken, CancellationToken ct) =>
            CallAsync(ptzUrl, $"<GotoHomePosition xmlns=\"{NsPtz}\"><ProfileToken>{Esc(profileToken)}</ProfileToken></GotoHomePosition>", true, ct);

        // ---------------- SOAP plumbing ----------------
        private static string F(double v) => Math.Max(-1, Math.Min(1, v)).ToString("0.###", CultureInfo.InvariantCulture);

        private static string Esc(string s) => System.Security.SecurityElement.Escape(s ?? "");

        private static string Value(XDocument doc, string localName) =>
            doc.Descendants().FirstOrDefault(e => e.Name.LocalName == localName)?.Value?.Trim() ?? "";

        private string SecurityHeader()
        {
            var nonce = new byte[16];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(nonce);
            var created = (DateTime.UtcNow + clockOffset).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            byte[] digest;
            using (var sha1 = SHA1.Create())
            {
                var c = Encoding.UTF8.GetBytes(created);
                var p = Encoding.UTF8.GetBytes(password);
                var all = new byte[nonce.Length + c.Length + p.Length];
                Buffer.BlockCopy(nonce, 0, all, 0, nonce.Length);
                Buffer.BlockCopy(c, 0, all, nonce.Length, c.Length);
                Buffer.BlockCopy(p, 0, all, nonce.Length + c.Length, p.Length);
                digest = sha1.ComputeHash(all);
            }

            return "<s:Header><Security s:mustUnderstand=\"1\" xmlns=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd\">" +
                   "<UsernameToken>" +
                   $"<Username>{Esc(user)}</Username>" +
                   "<Password Type=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest\">" +
                   Convert.ToBase64String(digest) + "</Password>" +
                   "<Nonce EncodingType=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary\">" +
                   Convert.ToBase64String(nonce) + "</Nonce>" +
                   $"<Created xmlns=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd\">{created}</Created>" +
                   "</UsernameToken></Security></s:Header>";
        }

        private async Task<XDocument> CallAsync(string url, string body, bool authenticate, CancellationToken ct)
        {
            var envelope = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                           "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\">" +
                           (authenticate && user.Length > 0 ? SecurityHeader() : "") +
                           "<s:Body>" + body + "</s:Body></s:Envelope>";
            using (var content = new StringContent(envelope, Encoding.UTF8))
            {
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/soap+xml") { CharSet = "utf-8" };
                HttpResponseMessage resp;
                try
                {
                    resp = await http.PostAsync(url, content, ct).ConfigureAwait(false);
                }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new OnvifException("ONVIF request timed out (" + url + ")");
                }
                catch (HttpRequestException ex)
                {
                    throw new OnvifException("ONVIF service not reachable at " + url + ": " + ex.GetBaseException().Message);
                }

                using (resp)
                {
                    var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    XDocument doc = null;
                    try
                    {
                        doc = XDocument.Parse(text);
                    }
                    catch (Exception)
                    {
                        // non-XML error page
                    }

                    var fault = doc?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Fault");
                    if (fault != null)
                    {
                        var reason = fault.Descendants().FirstOrDefault(e => e.Name.LocalName == "Text")?.Value ??
                                     fault.Descendants().LastOrDefault(e => e.Name.LocalName == "Value")?.Value ?? "SOAP fault";
                        if (reason.IndexOf("NotAuthorized", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            fault.ToString().IndexOf("NotAuthorized", StringComparison.OrdinalIgnoreCase) >= 0)
                            throw new OnvifException("ONVIF authentication failed (wrong username/password).");
                        throw new OnvifException("ONVIF error: " + reason.Trim());
                    }

                    if (resp.StatusCode == HttpStatusCode.Unauthorized)
                        throw new OnvifException("ONVIF authentication failed (HTTP 401).");
                    if (!resp.IsSuccessStatusCode)
                        throw new OnvifException($"ONVIF HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} ({url})");
                    if (doc == null)
                        throw new OnvifException("Invalid ONVIF response from " + url);
                    return doc;
                }
            }
        }
    }
}
