using System;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MissionPlanner.AeroAid.Camera
{
    public enum RtspProbeStatus
    {
        NotConfigured,
        Unreachable,
        AuthFailed,
        StreamNotFound,
        Ok,
        Error
    }

    public sealed class RtspProbeResult
    {
        public RtspProbeStatus Status;
        public int StatusCode;
        public string Message = "";
        public bool HasVideo;
        public string VideoCodec = "";
        public bool HasAudio;
        public string AudioCodec = "";
        public bool HasBackchannel;
    }

    /// <summary>
    /// Minimal RTSP DESCRIBE client used to diagnose camera connections before starting the video pipeline:
    /// distinguishes "camera unreachable", "wrong username/password" and "wrong stream path", and reads the
    /// SDP to report the video/audio codecs. Supports Basic and Digest authentication.
    /// </summary>
    public static class RtspProbe
    {
        public static async Task<RtspProbeResult> DescribeAsync(string url, string user, string password, CancellationToken ct, int timeoutMs = 4000)
        {
            var result = new RtspProbeResult();
            if (string.IsNullOrEmpty(url))
            {
                result.Status = RtspProbeStatus.NotConfigured;
                result.Message = "No camera address configured.";
                return result;
            }

            Uri uri;
            try
            {
                uri = new Uri(url);
            }
            catch (UriFormatException)
            {
                result.Status = RtspProbeStatus.Error;
                result.Message = "Invalid RTSP URL: " + url;
                return result;
            }

            var port = uri.Port > 0 ? uri.Port : 554;
            try
            {
                using (var client = new TcpClient())
                {
                    var connect = client.ConnectAsync(uri.Host, port);
                    if (await Task.WhenAny(connect, Task.Delay(timeoutMs, ct)).ConfigureAwait(false) != connect || !client.Connected)
                    {
                        result.Status = RtspProbeStatus.Unreachable;
                        result.Message = $"Camera not reachable at {uri.Host}:{port} (check IP address, network and that RTSP is enabled in the Imou Life app).";
                        return result;
                    }

                    await connect.ConfigureAwait(false);
                    client.ReceiveTimeout = timeoutMs;
                    client.SendTimeout = timeoutMs;
                    var stream = client.GetStream();

                    var response = await SendAsync(stream, uri.ToString(), 1, null, ct).ConfigureAwait(false);
                    if (response.Code == 401)
                    {
                        if (string.IsNullOrEmpty(password))
                        {
                            result.Status = RtspProbeStatus.AuthFailed;
                            result.StatusCode = 401;
                            result.Message = "The camera requires a password (RTSP/ONVIF password or the safety code on the camera label).";
                            return result;
                        }

                        var auth = BuildAuthorization(response.Headers, uri.ToString(), user, password);
                        if (auth == null)
                        {
                            result.Status = RtspProbeStatus.Error;
                            result.Message = "Unsupported RTSP authentication scheme.";
                            return result;
                        }

                        response = await SendAsync(stream, uri.ToString(), 2, auth, ct).ConfigureAwait(false);
                    }

                    result.StatusCode = response.Code;
                    switch (response.Code)
                    {
                        case 200:
                            result.Status = RtspProbeStatus.Ok;
                            ParseSdp(response.Body, result);
                            result.Message = "RTSP OK" + (result.HasVideo ? " • video " + result.VideoCodec : "") +
                                             (result.HasAudio ? " • audio " + result.AudioCodec : " • no audio track");
                            break;
                        case 401:
                        case 403:
                            result.Status = RtspProbeStatus.AuthFailed;
                            result.Message = "Authentication failed: wrong username or password.";
                            break;
                        case 404:
                            result.Status = RtspProbeStatus.StreamNotFound;
                            result.Message = "Stream path not found on the camera (check the RTSP path / channel).";
                            break;
                        default:
                            result.Status = RtspProbeStatus.Error;
                            result.Message = "Camera answered RTSP " + response.Code + " " + response.Reason;
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is SocketException || ex is IOException)
            {
                result.Status = RtspProbeStatus.Unreachable;
                result.Message = "RTSP connection failed: " + ex.Message;
            }

            return result;
        }

        private sealed class Response
        {
            public int Code;
            public string Reason = "";
            public string Headers = "";
            public string Body = "";
        }

        private static async Task<Response> SendAsync(NetworkStream stream, string url, int cseq, string authorization, CancellationToken ct)
        {
            var sb = new StringBuilder();
            sb.Append("DESCRIBE ").Append(url).Append(" RTSP/1.0\r\n");
            sb.Append("CSeq: ").Append(cseq).Append("\r\n");
            sb.Append("Accept: application/sdp\r\n");
            sb.Append("User-Agent: AeroAid-GCS\r\n");
            if (authorization != null)
                sb.Append("Authorization: ").Append(authorization).Append("\r\n");
            sb.Append("\r\n");
            var bytes = Encoding.ASCII.GetBytes(sb.ToString());
            await stream.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);

            var buffer = new MemoryStream();
            var chunk = new byte[4096];
            int headerEnd = -1;
            while (headerEnd < 0)
            {
                var n = await stream.ReadAsync(chunk, 0, chunk.Length, ct).ConfigureAwait(false);
                if (n <= 0) break;
                buffer.Write(chunk, 0, n);
                headerEnd = IndexOf(buffer.ToArray(), Encoding.ASCII.GetBytes("\r\n\r\n"));
                if (buffer.Length > 65536) break;
            }

            var all = buffer.ToArray();
            if (headerEnd < 0)
                throw new IOException("Incomplete RTSP response");
            var headers = Encoding.ASCII.GetString(all, 0, headerEnd);
            var resp = new Response { Headers = headers };
            var first = headers.Split(new[] { "\r\n" }, StringSplitOptions.None)[0];
            var m = Regex.Match(first, @"RTSP/\d\.\d\s+(\d{3})\s*(.*)");
            if (!m.Success)
                throw new IOException("Not an RTSP response: " + first);
            resp.Code = int.Parse(m.Groups[1].Value);
            resp.Reason = m.Groups[2].Value;

            var lenMatch = Regex.Match(headers, @"Content-Length:\s*(\d+)", RegexOptions.IgnoreCase);
            var bodyLen = lenMatch.Success ? int.Parse(lenMatch.Groups[1].Value) : 0;
            var bodyStart = headerEnd + 4;
            var body = new MemoryStream();
            body.Write(all, bodyStart, all.Length - bodyStart);
            while (body.Length < bodyLen)
            {
                var n = await stream.ReadAsync(chunk, 0, chunk.Length, ct).ConfigureAwait(false);
                if (n <= 0) break;
                body.Write(chunk, 0, n);
            }

            resp.Body = Encoding.UTF8.GetString(body.ToArray());
            return resp;
        }

        private static int IndexOf(byte[] haystack, byte[] needle)
        {
            for (var i = 0; i <= haystack.Length - needle.Length; i++)
            {
                var ok = true;
                for (var j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j])
                    {
                        ok = false;
                        break;
                    }
                }

                if (ok) return i;
            }

            return -1;
        }

        private static string BuildAuthorization(string headers, string url, string user, string password)
        {
            var digest = Regex.Match(headers, @"WWW-Authenticate:\s*Digest\s+(.+)", RegexOptions.IgnoreCase);
            if (digest.Success)
            {
                var p = digest.Groups[1].Value;
                var realm = Regex.Match(p, "realm=\"([^\"]*)\"").Groups[1].Value;
                var nonce = Regex.Match(p, "nonce=\"([^\"]*)\"").Groups[1].Value;
                var ha1 = Md5($"{user}:{realm}:{password}");
                var ha2 = Md5($"DESCRIBE:{url}");
                var response = Md5($"{ha1}:{nonce}:{ha2}");
                return $"Digest username=\"{user}\", realm=\"{realm}\", nonce=\"{nonce}\", uri=\"{url}\", response=\"{response}\"";
            }

            if (Regex.IsMatch(headers, @"WWW-Authenticate:\s*Basic", RegexOptions.IgnoreCase))
                return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password));
            return null;
        }

        private static string Md5(string s)
        {
            using (var md5 = MD5.Create())
            {
                var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(s));
                var sb = new StringBuilder();
                foreach (var b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private static void ParseSdp(string sdp, RtspProbeResult r)
        {
            string media = null;
            foreach (var raw in sdp.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("m=video")) { media = "video"; r.HasVideo = true; }
                else if (line.StartsWith("m=audio")) { media = "audio"; r.HasAudio = true; }
                else if (line.StartsWith("m=")) media = null;
                else if (line.StartsWith("a=rtpmap:") && media != null)
                {
                    var codec = Regex.Match(line, @"a=rtpmap:\d+\s+([^/\s]+)").Groups[1].Value;
                    if (media == "video" && r.VideoCodec.Length == 0) r.VideoCodec = codec;
                    if (media == "audio" && r.AudioCodec.Length == 0) r.AudioCodec = codec;
                }
                else if (line.Equals("a=sendonly", StringComparison.OrdinalIgnoreCase) && media == "audio")
                {
                    // camera->client audio
                }
                else if (line.Equals("a=recvonly", StringComparison.OrdinalIgnoreCase) && media == "audio")
                {
                    // ONVIF Profile T audio backchannel (client->camera) - only offered with a "Require: backchannel" header
                    r.HasBackchannel = true;
                }
            }
        }
    }
}
