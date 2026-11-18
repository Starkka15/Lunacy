using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.Web;

namespace LunaKit
{
    /// Serves a ported app the way AndroidLuna's AppServer.kt serves every app: at the path it
    /// had on the device (`/media/cryptofs/apps/usr/palm/applications/<appid>/`), with the
    /// modernized frameworks at `/usr/palm/frameworks/` and Lunacy's own scripts at `/__lunacy/`.
    /// Every file goes through Lunacy's global serve-time transforms; the app's own files are
    /// never changed on disk.
    ///
    /// UWP's WebView can't intercept http or https on Windows 10 Mobile (WebResourceRequested
    /// came in 1803, after W10M's last build), so the origin is the WebView's own
    /// `ms-local-stream://<package>_<hex of appid>/`, one per app. A stream resolver answers with
    /// bytes only: the WebView takes a file's type from its extension, a file that isn't there
    /// fails the request rather than answering 404, and there are no byte ranges (README,
    /// "Where W10M differs").
    internal sealed class AppServer : IUriToStreamResolver
    {
        public const string AppsPath = "media/cryptofs/apps/usr/palm/applications/";
        /// A file read through palmGetResource comes back as it is on disk.
        public const string ResourceParam = "__lunacy_res";
        /// The longest a page waits for its fonts before its own scripts run anyway.
        private static readonly TimeSpan FontWait = TimeSpan.FromSeconds(1);
        private static readonly Regex Slashes = new Regex("/{2,}");
        private static readonly Regex EnyoPath = new Regex("(?:^|/)usr/palm/frameworks/enyo/[^/]+/");
        private static readonly Regex Head = new Regex("<head[^>]*>", RegexOptions.IgnoreCase);
        private static readonly Regex Doctype = new Regex("^\\s*<!doctype[^>]*>", RegexOptions.IgnoreCase);

        private readonly string appId;
        private readonly string package;
        private readonly string assets;
        private readonly Func<string> statics;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> fonts = new ConcurrentDictionary<string, TaskCompletionSource<bool>>();
        private int tokens;

        /// `package`: the app's folder, holding its appinfo.json. `assets`: the folder
        /// assemble-assets.ps1 filled (lunacy/, fonts/, fw/). `statics`: __LUNACY_STATIC__.
        public AppServer(string appId, string package, string assets, Func<string> statics)
        {
            this.appId = appId;
            this.package = Path.GetFullPath(package);
            this.assets = Path.GetFullPath(assets);
            this.statics = statics;
        }

        public static string PagePath(string appId, string page) { return "/" + AppsPath + appId + "/" + page; }

        public IAsyncOperation<IInputStream> UriToStreamAsync(Uri uri)
        {
            return Serve(uri).AsAsyncOperation();
        }

        private async Task<IInputStream> Serve(Uri uri)
        {
            // A run of slashes is one, as the TouchPad's filesystem read a file:// path.
            var path = Slashes.Replace(Uri.UnescapeDataString(uri.AbsolutePath), "/").Trim('/');
            var query = uri.Query.TrimStart('?').Split('&').Where(q => q.Length > 0)
                .Select(q => q.Split(new[] { '=' }, 2)).ToDictionary(q => q[0], q => q.Length > 1 ? Uri.UnescapeDataString(q[1]) : "", StringComparer.Ordinal);
            bool resource = query.ContainsKey(ResourceParam);
            string t;

            switch (path)
            {
                case "__lunacy/fonts-loaded":
                    if (query.TryGetValue("t", out t)) FontsLoaded(t);
                    return Bytes(new byte[0]);
                case "__lunacy/fonts-ready.js":
                    query.TryGetValue("t", out t);
                    await FontsReady(t ?? "").ConfigureAwait(false);
                    return Text("/* Lunacy: this page's fonts are in. */");
                case "__lunacy/w10m-native.js":
                    var native = await ReadText(Path.Combine(assets, "lunacy", "w10m-native.js")).ConfigureAwait(false);
                    return Text(native.Replace("__LUNACY_STATIC__", statics()));
                // Enyo's Tellurium hooks read this while starting; the TouchPad answers 200 and nothing.
                case "usr/palm/frameworks/tellurium/tellurium_config.json":
                    return Bytes(new byte[0]);
            }

            string file = null;
            Match enyo;
            if (path.StartsWith("__lunacy/fonts/", StringComparison.Ordinal)) file = Path.Combine(assets, "fonts", path.Substring("__lunacy/fonts/".Length));
            else if (path.StartsWith("__lunacy/", StringComparison.Ordinal)) file = Path.Combine(assets, "lunacy", path.Substring("__lunacy/".Length));
            else if ((enyo = EnyoPath.Match(path)).Success) file = Path.Combine(assets, "fw", "enyo", "1.0", path.Substring(enyo.Index + enyo.Length));
            else if (path.StartsWith(AppsPath + appId + "/", StringComparison.Ordinal)) file = Path.Combine(package, path.Substring((AppsPath + appId + "/").Length));

            if (file != null) file = Path.GetFullPath(file.Replace('/', Path.DirectorySeparatorChar));
            if (file == null || !Contained(file) || !File.Exists(file))
            {
                LunaLog.Write("[LunaKit] 404 " + uri);
                throw new FileNotFoundException(uri.ToString());
            }
            var data = await Task.Run(() => File.ReadAllBytes(file)).ConfigureAwait(false);
            return Bytes(Transform(data, path, resource));
        }

        /// Only files inside the folders this server serves, whatever `..` a path carries.
        private bool Contained(string f)
        {
            return f.StartsWith(package + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                   f.StartsWith(assets + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static IInputStream Bytes(byte[] b) { return new MemoryStream(b).AsInputStream(); }
        private static IInputStream Text(string s) { return Bytes(Encoding.UTF8.GetBytes(s)); }

        private static async Task<string> ReadText(string f)
        {
            return await Task.Run(() => File.ReadAllText(f)).ConfigureAwait(false);
        }

        // MARK: transforms

        private byte[] Transform(byte[] data, string name, bool resource)
        {
            var ext = Path.GetExtension(name).ToLowerInvariant();
            switch (ext)
            {
                case ".html":
                case ".htm":
                {
                    var html = HtmlTransforms.SelfClosingTags(Decode(data));
                    return Encoding.UTF8.GetBytes(resource ? html : Inject(JsTransforms.SloppyMode(html)));
                }
                case ".css":
                {
                    var css = Decode(data);
                    // Lunacy's fonts.css names the one origin Android serves every page's fonts
                    // from; here each app's origin serves them itself.
                    if (name == "__lunacy/fonts.css") css = css.Replace("https://lunacy-fonts.media.cryptofs.apps/__lunacy/fonts/", "/__lunacy/fonts/");
                    return Encoding.UTF8.GetBytes(CssTransforms.Apply(css));
                }
                case ".js":
                    return resource ? data : Encoding.UTF8.GetBytes(JsTransforms.SloppyMode(Decode(data)));
                default:
                    return data;
            }
        }

        /// UTF-8, or Latin-1 for a file that isn't (as iOSLuna reads one).
        private static string Decode(byte[] data)
        {
            try { return new UTF8Encoding(false, true).GetString(data).TrimStart('﻿'); }
            catch (DecoderFallbackException) { return new string(data.Select(b => (char)b).ToArray()); }
        }

        /// Lunacy's scripts run first in every page, in AndroidLuna's order, after W10MLuna's
        /// own LunacyNative (which a UWP WebView can't inject at document start).
        private string Inject(string html)
        {
            var token = Interlocked.Increment(ref tokens).ToString();
            // UTF-8 first: AndroidLuna answers every file as utf-8, and a stream resolver can't
            // send a charset, so without this EdgeHTML reads the page and every script it loads
            // as windows-1252 (textedit.js's [\wÀ-￿] became an invalid range).
            var tag = "<meta charset=\"utf-8\">" +
                "<script src=\"/__lunacy/w10m-native.js\"></script>" +
                "<link rel=\"stylesheet\" href=\"/__lunacy/fonts.css\">" + FontLoad(token) +
                "<script src=\"/__lunacy/input.js\"></script><script src=\"/__lunacy/engine.js\"></script><script src=\"/__lunacy/media.js\"></script><script src=\"/__lunacy/layout.js\"></script><script src=\"/__lunacy/bridge.js\"></script>" +
                "<script src=\"/__lunacy/crypt.js\"></script>" +
                "<script src=\"/__lunacy/textedit.js\"></script>" +
                "<script src=\"/__lunacy/net.js\"></script><script src=\"/__lunacy/websql.js\"></script>" +
                "<script src=\"/__lunacy/fonts-ready.js?t=" + token + "\"></script>";
            var m = Head.Match(html);
            if (m.Success) return html.Substring(0, m.Index + m.Length) + tag + html.Substring(m.Index + m.Length);
            // No <head>: after the doctype, which has to stay first or the page drops to quirks mode.
            var d = Doctype.Match(html);
            return d.Success ? html.Substring(0, d.Index + d.Length) + tag + html.Substring(d.Index + d.Length) : tag + html;
        }

        // MARK: the fonts, in hand before the page's own scripts run (AppServer.kt, "the fonts")

        /// The page asks for Prelude's default faces and reports when they are ready. EdgeHTML has
        /// no document.fonts, so there it reports at once and the wait below is skipped.
        private static string FontLoad(string t)
        {
            return "<script>(function(){var t=\"" + t + "\";" +
                "function done(){var x=new XMLHttpRequest();x.open(\"GET\",\"/__lunacy/fonts-loaded?t=\"+t,true);x.send();}" +
                "try{var s=document.fonts,w=[];if(!s||!s.forEach){done();return;}" +
                "s.forEach(function(f){if(f.family.replace(/[\"']/g,\"\")===\"Prelude\"&&f.style===\"normal\")w.push(f);});" +
                "var n=w.length;if(!n){done();return;}" +
                "function one(){if(--n===0)done();}" +
                "for(var i=0;i<w.length;i++)w[i].load().then(one,one);" +
                "}catch(e){done();}})();</script>";
        }

        private TaskCompletionSource<bool> FontSlot(string t) { return fonts.GetOrAdd(t, _ => new TaskCompletionSource<bool>()); }

        private void FontsLoaded(string t) { FontSlot(t).TrySetResult(true); }

        /// Held until the page's fonts are in, or for a second at most: the parser waits on it.
        private async Task FontsReady(string t)
        {
            var slot = FontSlot(t);
            var first = await Task.WhenAny(slot.Task, Task.Delay(FontWait)).ConfigureAwait(false);
            if (first != slot.Task) LunaLog.Write("[LunaKit] fonts not ready after " + FontWait.TotalMilliseconds + " ms");
            TaskCompletionSource<bool> gone;
            fonts.TryRemove(t, out gone);
        }
    }
}
