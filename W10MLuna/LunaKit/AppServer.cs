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
    /// Serves every app the way AndroidLuna's AppServer.kt does: at the path it had on the device
    /// (`/media/cryptofs/apps/usr/palm/applications/<appid>/`), with the modernized frameworks at
    /// `/usr/palm/frameworks/` and Lunacy's own scripts at `/__lunacy/`. Every file goes through
    /// Lunacy's global serve-time transforms; an app's own files are never changed on disk.
    ///
    /// One origin for the shell and every app (SHELL.md, design B): a UWP WebView on W10M can't
    /// intercept http or https (WebResourceRequested came in 1803), and a frame from a second
    /// `ms-local-stream` origin never reaches the resolver (Spikes/shell-frames, T1), so every
    /// window is a frame of the shell's page on `ms-local-stream://<package>_<hex "luna">/`, as on
    /// webOS every app was `file://`. A stream resolver answers with bytes only: the WebView takes
    /// a file's type from its extension, a missing file fails the request rather than answering
    /// 404, and there are no byte ranges (README, "Where W10M differs").
    internal sealed class AppServer : IUriToStreamResolver
    {
        public const string AppsPath = "media/cryptofs/apps/usr/palm/applications/";
        public const string ShellPage = "/__lunacy/shell/index.html";
        /// A JS service package's frame (JsServices.cs).
        public const string ServicePage = "/__lunacy/shell/service/index.html";
        /// A file read through palmGetResource comes back as it is on disk.
        public const string ResourceParam = "__lunacy_res";
        /// The longest a page waits for its fonts before its own scripts run anyway.
        private static readonly TimeSpan FontWait = TimeSpan.FromSeconds(1);
        private static readonly Regex Slashes = new Regex("/{2,}");
        private static readonly Regex EnyoPath = new Regex("(?:^|/)usr/palm/frameworks/enyo/[^/]+/");
        private static readonly Regex FrameworksPath = new Regex("(?:^|/)usr/palm/frameworks/");
        private static readonly Regex Head = new Regex("<head[^>]*>", RegexOptions.IgnoreCase);
        private static readonly Regex Doctype = new Regex("^\\s*<!doctype[^>]*>", RegexOptions.IgnoreCase);
        /// A page that loads Mojo, either version (AppServer.kt's MOJO_TAG). webOS's browser had
        /// the framework compiled in and mojo.js looks for it as a global, so the builtins that
        /// carry it are served in front of the app's own tag.
        private static readonly Regex MojoTag = new Regex("<script[^>]*src=\"[^\"]*?/usr/palm/frameworks/(mojo|mojo2)/mojo\\.js[^\"]*\"[^>]*>\\s*</script>", RegexOptions.IgnoreCase);
        private static readonly Regex MojoVersion = new Regex("x-mojo-(version|submission)=\"([^\"]*)\"", RegexOptions.IgnoreCase);
        /// Mojo 1's own version table: x-mojo-version 1 is submission 506.
        private static readonly System.Collections.Generic.Dictionary<string, string> MojoSubmissions = new System.Collections.Generic.Dictionary<string, string> { { "1", "506" }, { "2", "344" } };
        /// What MojoLoader looks for: a Mojo 2 page gets them all, since it asks for mojo.core
        /// before the framework itself has loaded.
        private static readonly string[] Mojo2Libraries = { "palmunderscoreVersion1_0", "palmfoundationsVersion1_0", "palmglobalizationVersion1_0", "palmmojo_coreVersion1_0" };
        /// webOS's system UI, which apps reach at its absolute path (Enyo's FilePicker loads
        /// luna-systemui/app/FilePicker/filepicker.html in an iframe), and Just Type's icons.
        private const string SystemUi = "usr/lib/luna/system/luna-systemui/app/";
        private const string SystemUiUtils = "usr/lib/luna/system/luna-systemui/utils/";
        private const string SystemUiStyles = "usr/lib/luna/system/luna-systemui/stylesheets/";
        private const string SystemUiImages = "usr/lib/luna/system/luna-systemui/images/";
        private const string SearchIcons = "usr/lib/luna/system/luna-applauncher/images/";
        /// Mojo's images that the Pre3 drew differently from the TouchPad (luna/images/pre3/NOTICE).
        private const string MojoImages = "usr/palm/frameworks/mojocommon/images/";
        private static readonly string[] Pre3MojoImages = { "dashboard-container.png", "dashboard-container-active.png", "dashboard-container-sprite.png" };
        private const string Cryptofs = "media/cryptofs/apps/";
        /// Dashboards along the bottom, as the Pre3's: Mojo's dashboard boxes are the Pre3's
        /// (the TouchPad's are transparent, its dashboards sitting in the drop-down).
        public bool Pre3Dashboards = true;

        private readonly AppRegistry apps;
        private readonly string assets;
        private readonly Func<string, string> statics;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> fonts = new ConcurrentDictionary<string, TaskCompletionSource<bool>>();
        private int tokens;

        /// The webOS tree (WebosFs), for the paths pages read from it: set once the shell has it.
        public WebosFs Fs;
        /// Whether a path (no leading slash) is in the tree the server reads from WebosFs.
        public static bool Readable(string path) { return TreeReadable.Any(pre => path.StartsWith(pre, StringComparison.Ordinal)); }
        /// A Windows notification's dashboard icon by app (WindowsNotifications.DashboardIcon).
        public Func<string, byte[]> AndroidIcon;
        /// `?__lunacy_thumb=160`: a JPEG copy of a picture under /media/internal, its short side about that.
        public const string ThumbParam = "__lunacy_thumb";
        private const string Extract = "var/luna/data/extractfs";
        /// The webOS tree's files pages read by path on a device (AndroidLuna's AppServer
        /// rootRoute and userRoute): user storage, the Web app's data, the file cache (Email's
        /// bodies and attachments) and the ROM's package list.
        private static readonly string[] TreeReadable = { "media/internal/", "var/luna/data/browser/", "var/file-cache/", "usr/palm/ipkgs/" };

        /// `assets`: the folder assemble-assets.ps1 filled (lunacy/, fonts/, fw/, shell/, luna/).
        /// `statics`: __LUNACY_STATIC__ for an app id.
        public AppServer(AppRegistry apps, string assets, Func<string, string> statics)
        {
            this.apps = apps;
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
                .Select(q => q.Split(new[] { '=' }, 2)).GroupBy(q => q[0])
                .ToDictionary(g => g.Key, g => g.First().Length > 1 ? Uri.UnescapeDataString(g.First()[1]) : "", StringComparer.Ordinal);
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
                    string app;
                    query.TryGetValue("app", out app);
                    var native = await ReadText(Path.Combine(assets, "lunacy", "w10m-native.js")).ConfigureAwait(false);
                    return Text(native.Replace("__LUNACY_STATIC__", statics(app ?? "")));
                // Enyo's Tellurium hooks read this while starting; the TouchPad answers 200 and nothing.
                case "usr/palm/frameworks/tellurium/tellurium_config.json":
                    return Bytes(new byte[0]);
            }

            if (AndroidIcon != null && path.StartsWith("__lunacy/android-icon/", StringComparison.Ordinal) && path.EndsWith(".svg", StringComparison.Ordinal))
            {
                var pkg = path.Substring("__lunacy/android-icon/".Length, path.Length - "__lunacy/android-icon/".Length - 4);
                var svg = await Task.Run(() => AndroidIcon(pkg)).ConfigureAwait(false);
                if (svg == null) throw new FileNotFoundException(uri.ToString());
                return Bytes(svg);
            }
            if (Fs != null && path.StartsWith(Extract, StringComparison.Ordinal))
            {
                // The source's slashes come as %2F: the spec is read from the encoded path.
                var raw = uri.AbsolutePath.TrimStart('/');
                var spec = Uri.UnescapeDataString(raw.Length > Extract.Length ? raw.Substring(Extract.Length) : "");
                var r = await Extractfs.Read(spec, p => { try { return Fs.IsDir(p) ? null : Fs.Read(p); } catch (WebosFs.FsError) { return null; } }).ConfigureAwait(false);
                if (r == null) { LunaLog.Write("[LunaKit] 404 /" + path); throw new FileNotFoundException(uri.ToString()); }
                return Bytes(r.Bytes);
            }
            if (Fs != null && TreeReadable.Any(pre => path.StartsWith(pre, StringComparison.Ordinal)))
            {
                byte[] tree = null;
                try { tree = await Task.Run(() => Fs.IsDir("/" + path) ? null : Fs.Read("/" + path)).ConfigureAwait(false); }
                catch (WebosFs.FsError) { }
                catch (UnauthorizedAccessException) { }
                string size; int px;
                if (tree != null && query.TryGetValue(ThumbParam, out size) && int.TryParse(size, out px))
                    tree = await Images.Thumbnail(tree, Math.Max(16, Math.Min(1024, px))).ConfigureAwait(false);
                if (tree == null) { LunaLog.Write("[LunaKit] 404 /" + path); throw new FileNotFoundException(uri.ToString()); }
                return Bytes(Transform(tree, path, resource, null));
            }

            // The rest of the webOS tree pages read by path (AppServer.kt's ROOT_READABLE and
            // CRYPTOFS): the system's files under /usr/palm - the writable layer's over the ROM's,
            // as account templates' icons are - and what installed packages put under cryptofs
            // beyond their apps' folders. The frameworks and the apps have their own routes.
            if (Fs != null && !FrameworksPath.IsMatch(path) && !path.StartsWith(AppsPath, StringComparison.Ordinal)
                && (path.StartsWith("usr/palm/", StringComparison.Ordinal) || path.StartsWith(Cryptofs, StringComparison.Ordinal)))
            {
                byte[] tree = null;
                try { tree = await Task.Run(() => Fs.Exists("/" + path) && !Fs.IsDir("/" + path) ? Fs.Read("/" + path) : null).ConfigureAwait(false); }
                catch (WebosFs.FsError) { }
                catch (UnauthorizedAccessException) { }
                if (tree == null) { LunaLog.Write("[LunaKit] 404 /" + path); throw new FileNotFoundException(uri.ToString()); }
                return Bytes(Transform(tree, path, resource, null));
            }

            string file = null, root = assets, appId = null;
            Match enyo;
            if (path.StartsWith(SystemUi, StringComparison.Ordinal)) file = Path.Combine(assets, "luna-systemui", path.Substring(SystemUi.Length));
            else if (path.StartsWith(SystemUiUtils, StringComparison.Ordinal)) file = Path.Combine(assets, "luna-systemui", "utils", path.Substring(SystemUiUtils.Length));
            else if (path.StartsWith(SystemUiStyles, StringComparison.Ordinal)) file = Path.Combine(assets, "luna-systemui", "stylesheets", path.Substring(SystemUiStyles.Length));
            else if (path.StartsWith(SystemUiImages, StringComparison.Ordinal)) file = Path.Combine(assets, "luna-systemui", "images", path.Substring(SystemUiImages.Length));
            else if (path.StartsWith(SearchIcons, StringComparison.Ordinal)) file = Path.Combine(assets, "luna-applauncher", "images", path.Substring(SearchIcons.Length));
            else if (Pre3Dashboards && path.StartsWith(MojoImages, StringComparison.Ordinal) && Pre3MojoImages.Contains(path.Substring(MojoImages.Length)))
                file = Path.Combine(assets, "luna", "images", "pre3", "mojo", path.Substring(MojoImages.Length));
            else if (!resource && (path == "usr/palm/frameworks/mojo/mojo.js" || path == "usr/palm/frameworks/mojo2/mojo.js"))
            {
                // mojo.js with mojo-prelude.js in front, for a page that writes the tag from a
                // script (an Ares app's ares.js), where the HTML transform can't see it.
                var js = await ReadText(Path.Combine(assets, "fw", path.Contains("/mojo2/") ? Path.Combine("frameworks", "mojo2", "mojo.js") : Path.Combine("mojo", "mojo.js"))).ConfigureAwait(false);
                var prelude = await ReadText(Path.Combine(assets, "lunacy", "mojo-prelude.js")).ConfigureAwait(false);
                return Text(prelude + "\n" + JsTransforms.SloppyMode(js));
            }
            else if (path.StartsWith("__lunacy/fonts/", StringComparison.Ordinal)) file = Path.Combine(assets, "fonts", path.Substring("__lunacy/fonts/".Length));
            else if (path.StartsWith("__lunacy/shell/", StringComparison.Ordinal)) file = Path.Combine(assets, "shell", path.Substring("__lunacy/shell/".Length));
            else if (path.StartsWith("__lunacy/luna/", StringComparison.Ordinal)) file = Path.Combine(assets, "luna", path.Substring("__lunacy/luna/".Length));
            else if (path.StartsWith("__lunacy/", StringComparison.Ordinal)) file = Path.Combine(assets, "lunacy", path.Substring("__lunacy/".Length));
            else if ((enyo = EnyoPath.Match(path)).Success) file = Path.Combine(assets, "fw", "enyo", "1.0", path.Substring(enyo.Index + enyo.Length));
            else if ((enyo = FrameworksPath.Match(path)).Success)
            {
                // The rest of /usr/palm/frameworks at their own paths (AndroidLuna's FRAMEWORKS):
                // mojoloader.js and the libraries it hands out, from webos\fw\frameworks; Mojo and
                // mojocommon from their own trees.
                var rest = path.Substring(enyo.Index + enyo.Length);
                var top = rest.Split('/')[0];
                file = top == "mojo" || top == "mojocommon" ? Path.Combine(assets, "fw", rest) : Path.Combine(assets, "fw", "frameworks", rest);
            }
            else if (path.StartsWith("usr/palm/", StringComparison.Ordinal) && !path.StartsWith(AppsPath, StringComparison.Ordinal))
            {
                // The rest of the ROM's /usr/palm, which pages read by path, as they read the
                // device's files: command-resource-handlers.json, ext-timezones.json.
                root = Path.Combine(assets, "rootfs");
                file = Path.Combine(root, path);
            }
            else if (path.StartsWith(AppsPath, StringComparison.Ordinal))
            {
                var rest = path.Substring(AppsPath.Length);
                int slash = rest.IndexOf('/');
                var info = slash > 0 ? apps.Get(rest.Substring(0, slash)) : null;
                if (info != null)
                {
                    appId = info.Id;
                    root = info.Dir;
                    file = Path.Combine(info.Dir, rest.Substring(slash + 1));
                }
            }

            if (file != null) file = Path.GetFullPath(file.Replace('/', Path.DirectorySeparatorChar));
            if (file == null || !Contained(file, root) || !File.Exists(file))
            {
                LunaLog.Write("[LunaKit] 404 /" + path);
                throw new FileNotFoundException(uri.ToString());
            }
            var data = await Task.Run(() => File.ReadAllBytes(file)).ConfigureAwait(false);
            return Bytes(Transform(data, path, resource, appId));
        }

        /// Only files inside the folder a route serves, whatever `..` a path carries.
        private static bool Contained(string f, string root)
        {
            return f.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static IInputStream Bytes(byte[] b) { return new MemoryStream(b).AsInputStream(); }
        private static IInputStream Text(string s) { return Bytes(Encoding.UTF8.GetBytes(s)); }

        private static async Task<string> ReadText(string f)
        {
            return await Task.Run(() => File.ReadAllText(f)).ConfigureAwait(false);
        }

        // MARK: transforms

        /// `appId`: the app a file belongs to, or null for the shell's and Lunacy's own files,
        /// which are served as they are.
        private byte[] Transform(byte[] data, string name, bool resource, string appId)
        {
            var ext = Path.GetExtension(name).ToLowerInvariant();
            bool shell = name.StartsWith("__lunacy/shell/", StringComparison.Ordinal);
            switch (ext)
            {
                case ".html":
                case ".htm":
                {
                    if (shell) return data;
                    // Each page an app loads, its frames' included, which have no window to log them.
                    if (!resource) LunaLog.Write("[LunaKit] page /" + name + (appId == null ? "" : " (" + appId + ")"));
                    var html = HtmlTransforms.SelfClosingTags(Decode(data));
                    return Encoding.UTF8.GetBytes(resource || appId == null ? html : Inject(JsTransforms.SloppyMode(html), appId));
                }
                case ".css":
                {
                    if (shell) return data;
                    var css = Decode(data);
                    // Lunacy's fonts.css names the one origin Android serves every page's fonts
                    // from; here the one origin is this one.
                    if (name == "__lunacy/fonts.css") css = css.Replace("https://lunacy-fonts.media.cryptofs.apps/__lunacy/fonts/", "/__lunacy/fonts/");
                    return Encoding.UTF8.GetBytes(CssTransforms.Apply(css));
                }
                case ".js":
                    return resource || shell ? data : Encoding.UTF8.GetBytes(JsTransforms.SloppyMode(Decode(data)));
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
        /// own LunacyNative (which a UWP WebView can't inject at document start), served with
        /// the page's app.
        private string Inject(string html, string appId)
        {
            var token = Interlocked.Increment(ref tokens).ToString();
            // UTF-8 first: AndroidLuna answers every file as utf-8, and a stream resolver can't
            // send a charset, so without this EdgeHTML reads the page and every script it loads
            // as windows-1252 (textedit.js's [\wÀ-￿] became an invalid range).
            var tag = "<meta charset=\"utf-8\">" +
                "<script src=\"/__lunacy/w10m-native.js?app=" + Uri.EscapeDataString(appId) + "\"></script>" +
                "<link rel=\"stylesheet\" href=\"/__lunacy/fonts.css\">" + FontLoad(token) +
                "<script src=\"/__lunacy/input.js\"></script><script src=\"/__lunacy/engine.js\"></script><script src=\"/__lunacy/media.js\"></script><script src=\"/__lunacy/layout.js\"></script><script src=\"/__lunacy/bridge.js\"></script>" +
                "<script src=\"/__lunacy/crypt.js\"></script>" +
                "<script src=\"/__lunacy/textedit.js\"></script>" +
                "<script src=\"/__lunacy/net.js\"></script><script src=\"/__lunacy/websql.js\"></script>" +
                "<script src=\"/__lunacy/fonts-ready.js?t=" + token + "\"></script>";
            var m = Head.Match(html);
            if (m.Success) return MojoBuiltins(html.Substring(0, m.Index + m.Length) + tag + html.Substring(m.Index + m.Length));
            // No <head>: after the doctype, which has to stay first or the page drops to quirks mode.
            var d = Doctype.Match(html);
            return MojoBuiltins(d.Success ? html.Substring(0, d.Index + d.Length) + tag + html.Substring(d.Index + d.Length) : tag + html);
        }

        /// Mojo's builtins in front of the page's mojo.js tag, and mojo-boot.js after them
        /// (AppServer.kt's mojoBuiltins): mojo.js then finds its palmInitFramework global as on
        /// a device. The submission comes from the tag, as mojo.js reads it; Mojo 2's loader has
        /// no version map, defaults to 205 and takes only x-mojo-submission.
        private static string MojoBuiltins(string html)
        {
            var tag = MojoTag.Match(html);
            if (!tag.Success) return html;
            bool two = tag.Groups[1].Value.Equals("mojo2", StringComparison.OrdinalIgnoreCase);
            var attr = MojoVersion.Match(tag.Value);
            string submission;
            if (two)
            {
                var s = attr.Success && attr.Groups[1].Value.Equals("submission", StringComparison.OrdinalIgnoreCase) ? attr.Groups[2].Value : "";
                submission = "2" + (s.Length > 0 ? s : "205");
            }
            else
            {
                var version = attr.Success ? attr.Groups[2].Value : "";
                string mapped;
                submission = MojoSubmissions.TryGetValue(version, out mapped) ? mapped : version.Length > 0 ? version : "506";
            }
            // Mojo 2 takes Prototype from its own framework; only Mojo 1 needs the builtin copy.
            var libraries = two ? Mojo2Libraries : new[] { "InstallPrototypeBuiltIn" };
            var boot = string.Concat(libraries.Select(l => "<script src=\"/usr/palm/frameworks/mojo/builtins/" + l + ".js\"></script>")) +
                "<script src=\"/usr/palm/frameworks/mojo/builtins/palmInitFramework" + submission + ".js\"></script>" +
                "<script src=\"/__lunacy/mojo-boot.js\"></script>";
            return html.Substring(0, tag.Index) + boot + html.Substring(tag.Index);
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
