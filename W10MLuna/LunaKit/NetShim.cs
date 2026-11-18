using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Web.Http;
using Windows.Web.Http.Filters;

namespace LunaKit
{
    /// The native half of Lunacy's network shim (lunacy/net.js), as AndroidLuna's NetShim.kt and
    /// Http.kt send it. webOS apps ran from file:// with no CORS check; a ported page lives on its
    /// own origin, so its cross-origin XHRs come here and go out as the reference TouchPad sent
    /// them (Lunacy's Workbench/probe 0.0.7): its headers, its Origin, its redirect rules.
    ///
    /// Windows.Web.Http keeps its cookies in the same store as the app's WebView, so native
    /// requests and the page share them, as on the TouchPad.
    internal sealed class NetShim
    {
        private const int MaxRedirects = 20;
        private readonly string appId;
        private readonly DeviceProfile profile;
        /// Called with a request's id when its result is ready to read (netResult).
        private readonly Action<int> ready;
        private readonly object gate = new object();
        private readonly Dictionary<int, CancellationTokenSource> running = new Dictionary<int, CancellationTokenSource>();
        private readonly Dictionary<int, string> results = new Dictionary<int, string>();
        /// Which page's requests these are: a new page numbers its requests from 1 again.
        private int page;
        private static readonly HttpClient client = MakeClient();

        static NetShim()
        {
            // The legacy code pages a 2011 server might name (ISO-8859-x, windows-125x, Shift_JIS).
            try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch (Exception) { }
        }

        public NetShim(string appId, DeviceProfile profile, Action<int> ready)
        {
            this.appId = appId; this.profile = profile; this.ready = ready;
        }

        private static HttpClient MakeClient()
        {
            var f = new HttpBaseProtocolFilter { AllowAutoRedirect = false, AllowUI = false };
            f.CacheControl.ReadBehavior = HttpCacheReadBehavior.MostRecent;
            f.CacheControl.WriteBehavior = HttpCacheWriteBehavior.NoCache;
            f.MaxConnectionsPerServer = 6;
            return new HttpClient(f);
        }

        public void Send(int id, string request)
        {
            var cts = new CancellationTokenSource();
            int from;
            lock (gate) { from = page; running[id] = cts; }
            Task.Run(async () =>
            {
                var r = await Run(request, cts.Token).ConfigureAwait(false);
                lock (gate)
                {
                    if (from != page || !running.Remove(id)) return;  // aborted, or the page is gone
                    results[id] = r;
                }
                ready(id);
            });
        }

        public string Result(int id)
        {
            lock (gate)
            {
                string r;
                if (results.TryGetValue(id, out r)) { results.Remove(id); return r; }
            }
            return Error("no result", "error");
        }

        /// A synchronous XHR: blocks its page, as it did on webOS. The request runs on the
        /// thread pool, so a bridge call on the UI thread can wait for it.
        public string SendSync(string request)
        {
            return Task.Run(() => Run(request, CancellationToken.None)).GetAwaiter().GetResult();
        }

        public void Abort(int id)
        {
            CancellationTokenSource cts;
            lock (gate) { if (!running.TryGetValue(id, out cts)) return; running.Remove(id); }
            cts.Cancel();
        }

        /// A new page in the window: earlier pages' requests are dropped, answered or not.
        public void Reset()
        {
            CancellationTokenSource[] all;
            lock (gate) { page++; all = running.Values.ToArray(); running.Clear(); results.Clear(); }
            foreach (var c in all) c.Cancel();
        }

        private sealed class Req
        {
            public string Method;
            public Uri Url;
            public List<KeyValuePair<string, string>> Headers;
            public byte[] Body;
            public int Timeout;
            public bool Binary;
            public string Charset;
        }

        private async Task<string> Run(string request, CancellationToken token)
        {
            JsonObject j;
            Uri url;
            if (!JsonObject.TryParse(request ?? "", out j) || !Uri.TryCreate(j.GetString("url", ""), UriKind.Absolute, out url))
                return Error("bad request", "error");
            var method = j.GetString("method", "GET").ToUpperInvariant();
            var headers = new List<KeyValuePair<string, string>>();
            IJsonValue hv;
            if (j.TryGetValue("headers", out hv) && hv.ValueType == JsonValueType.Array)
            {
                foreach (var h in hv.GetArray())
                {
                    if (h.ValueType != JsonValueType.Array) continue;
                    var pair = h.GetArray();
                    if (pair.Count == 2 && pair[0].ValueType == JsonValueType.String && pair[1].ValueType == JsonValueType.String)
                        headers.Add(new KeyValuePair<string, string>(pair[0].GetString(), pair[1].GetString()));
                }
            }
            var given = new HashSet<string>(headers.Select(h => h.Key.ToLowerInvariant()));
            Action<string, string> byDefault = (k, v) => { if (!given.Contains(k.ToLowerInvariant())) headers.Add(new KeyValuePair<string, string>(k, v)); };
            byDefault("User-Agent", profile.UserAgent);
            byDefault("X-Palm-Carrier", profile.CarrierCode);
            byDefault("Accept", "*/*");
            byDefault("Accept-Language", "en-us,en;q=0.5");
            byDefault("Accept-Charset", "ISO-8859-1,utf-8;q=0.7,*;q=0.3");
            // The origin of a file:// page on the TouchPad, sent with everything but GET and HEAD.
            if (method != "GET" && method != "HEAD") byDefault("Origin", "file://.media.cryptofs.apps.usr.palm.applications." + appId);
            var user = j.GetString("user");
            if (user != null)
                byDefault("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + (j.GetString("password") ?? ""))));
            byte[] body = null;
            var b = j.GetString("body");
            if (b != null) body = j.GetBool("bodyBase64") ? Convert.FromBase64String(b) : Encoding.UTF8.GetBytes(b);
            var req = new Req { Method = method, Url = url, Headers = headers, Body = body, Timeout = j.GetInt("timeout"),
                                Binary = j.GetBool("binary"), Charset = j.GetString("charset", "") };
            try { return await Hops(req, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                // "timeout" only for the page's own xhr.timeout; a server that never answers is an "error".
                return Error("timed out", "timeout");
            }
            catch (Exception e)
            {
                if (!token.IsCancellationRequested) LunaLog.Write("[LunaKit] net [" + appId + "] " + method + " " + url + " failed: " + e.Message);
                return Error(e.Message, "error");
            }
        }

        /// The hops, following redirects as the TouchPad's WebKit did: 301/302/303 turn a POST into
        /// a GET without its body (headers stay), and each hop sends the previous URL as Referer.
        private async Task<string> Hops(Req req, CancellationToken token)
        {
            var hops = new List<List<KeyValuePair<string, string>>>();
            string referer = null;
            var started = DateTime.UtcNow;
            for (int count = 0; ; count++)
            {
                if (req.Url.Scheme != "http" && req.Url.Scheme != "https") return Error("can't fetch " + req.Url.Scheme + " URLs", "error");
                using (var msg = new HttpRequestMessage(new HttpMethod(req.Method), req.Url))
                {
                    foreach (var h in req.Headers) Add(msg, h.Key, h.Value);
                    if (referer != null && !req.Headers.Any(h => h.Key.Equals("Referer", StringComparison.OrdinalIgnoreCase)))
                        msg.Headers.TryAppendWithoutValidation("Referer", referer);
                    if (req.Body != null)
                    {
                        msg.Content = new HttpBufferContent(req.Body.AsBuffer());
                        foreach (var h in req.Headers.Where(h => h.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)))
                            msg.Content.Headers.TryAppendWithoutValidation(h.Key, h.Value);
                    }
                    using (var timeout = req.Timeout > 0 ? new CancellationTokenSource(req.Timeout) : new CancellationTokenSource())
                    using (var both = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token))
                    using (var resp = await client.SendRequestAsync(msg, HttpCompletionOption.ResponseContentRead).AsTask(both.Token).ConfigureAwait(false))
                    {
                        var headers = HeadersOf(resp);
                        int status = (int)resp.StatusCode;
                        string loc;
                        Uri next;
                        if (status >= 300 && status <= 399 && status != 304 && resp.Headers.TryGetValue("Location", out loc) &&
                            Uri.TryCreate(req.Url, loc, out next))
                        {
                            if (count >= MaxRedirects) return Error("too many redirects", "error");
                            hops.Add(headers);
                            referer = req.Url.ToString();
                            var n = new Req { Method = req.Method, Url = next, Headers = req.Headers, Body = req.Body, Timeout = req.Timeout,
                                              Binary = req.Binary, Charset = req.Charset };
                            if (status >= 301 && status <= 303 && n.Method != "GET" && n.Method != "HEAD") { n.Method = "GET"; n.Body = null; }
                            req = n;
                            continue;
                        }
                        var bytes = (await resp.Content.ReadAsBufferAsync().AsTask(both.Token).ConfigureAwait(false)).ToArray();
                        LunaLog.Write("[LunaKit] net [" + appId + "] " + req.Method + " " + req.Url + " " + status + " " + bytes.Length + "b " +
                                      (int)(DateTime.UtcNow - started).TotalMilliseconds + "ms");
                        hops.Add(headers);
                        var merged = Merged(hops).Select(h => "[" + J.Str(h.Key) + "," + J.Str(h.Value) + "]");
                        string contentType;
                        resp.Content.Headers.TryGetValue("Content-Type", out contentType);
                        return J.Obj(J.P("status", status), J.P("url", req.Url.ToString()),
                            J.P("headers", new J.Raw("[" + string.Join(",", merged) + "]")),
                            req.Binary ? J.P("base64", Convert.ToBase64String(bytes)) : J.P("text", Decode(bytes, req.Charset, contentType)));
                    }
                }
            }
        }

        private static void Add(HttpRequestMessage msg, string k, string v)
        {
            if (k.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)) return;  // on the content, above
            msg.Headers.TryAppendWithoutValidation(k, v);
        }

        private static List<KeyValuePair<string, string>> HeadersOf(HttpResponseMessage r)
        {
            var l = r.Headers.Select(h => new KeyValuePair<string, string>(h.Key, h.Value)).ToList();
            if (r.Content != null) l.AddRange(r.Content.Headers.Select(h => new KeyValuePair<string, string>(h.Key, h.Value)));
            return l;
        }

        /// Every hop's headers merged by name, a later hop's value replacing an earlier one.
        private static List<KeyValuePair<string, string>> Merged(List<List<KeyValuePair<string, string>>> hops)
        {
            var order = new List<string>();
            var byName = new Dictionary<string, List<KeyValuePair<string, string>>>();
            foreach (var hop in hops)
            {
                foreach (var name in hop.Select(h => h.Key.ToLowerInvariant()).Distinct()) { byName.Remove(name); order.Remove(name); }
                foreach (var h in hop)
                {
                    var n = h.Key.ToLowerInvariant();
                    if (!byName.ContainsKey(n)) { order.Add(n); byName[n] = new List<KeyValuePair<string, string>>(); }
                    byName[n].Add(h);
                }
            }
            return order.SelectMany(n => byName[n]).ToList();
        }

        private static readonly Regex CharsetRe = new Regex("charset\\s*=\\s*\"?([\\w.:-]+)", RegexOptions.IgnoreCase);
        private static readonly Regex XmlDecl = new Regex("^\\s*<\\?xml[^>]*encoding\\s*=\\s*[\"']([\\w.:-]+)");

        /// The page's override, then the response's charset, then an XML declaration, then UTF-8.
        internal static string Decode(byte[] bytes, string over, string contentType)
        {
            string name = over;
            if (string.IsNullOrEmpty(name))
            {
                var m = contentType == null ? Match.Empty : CharsetRe.Match(contentType);
                if (m.Success) name = m.Groups[1].Value;
                else
                {
                    var head = Encoding.UTF8.GetString(bytes, 0, Math.Min(200, bytes.Length));
                    var x = XmlDecl.Match(head);
                    if (x.Success) name = x.Groups[1].Value;
                }
            }
            Encoding enc = new UTF8Encoding(false);
            if (!string.IsNullOrEmpty(name)) { try { enc = Encoding.GetEncoding(name); } catch (Exception) { } }
            var text = enc.GetString(bytes);
            return text.Length > 0 && text[0] == '\uFEFF' ? text.Substring(1) : text;
        }

        internal static string Error(string text, string kind) { return J.Obj(J.P("error", kind), J.P("errorText", text)); }
    }
}
