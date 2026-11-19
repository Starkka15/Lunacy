using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace LunaKit
{
    /// Reads a webOS .ipk (LunaCore's Ipk.kt): an `ar` archive (or, from some packagers, a
    /// tar.gz) holding debian-binary, control.tar.gz and data.tar.gz. data.tar.gz's tree is the
    /// package's files relative to /media/cryptofs/apps; control.tar.gz holds its control file
    /// and the scripts ipkg ran around the install.
    internal static class Ipk
    {
        public sealed class BadPackage : IOException { public BadPackage(string m) : base(m) { } }
        private const long MaxRead = 8L << 20;

        /// Unpacks data.tar.gz into dest. Returns the relative paths of the files written.
        public static List<string> Extract(string ipk, string dest)
        {
            using (var input = File.OpenRead(ipk))
            {
                var data = Member(input, "data.tar.gz");
                if (data == null) throw new BadPackage("no data.tar.gz");
                return Untar(new GZipStream(data, CompressionMode.Decompress), dest);
            }
        }

        /// The title in the package's first app's appinfo.json, or null.
        public static string AppTitle(string ipk)
        {
            try
            {
                using (var input = File.OpenRead(ipk))
                {
                    var data = Member(input, "data.tar.gz");
                    if (data == null) return null;
                    string title = null;
                    ReadTar(new GZipStream(data, CompressionMode.Decompress), (path, type, size, body) =>
                    {
                        var parts = path.Split('/').Where(p => p.Length > 0 && p != ".").ToArray();
                        if (type == '0' && parts.Length == 5 && parts[0] == "usr" && parts[1] == "palm" && parts[2] == "applications" && parts[4] == "appinfo.json" && size < 1000000)
                        {
                            try { var t = Jx.ParseObj(Encoding.UTF8.GetString(ReadBytes(body, size)).TrimStart('﻿')).Str("title"); if (t.Trim().Length > 0) title = t; } catch (Exception) { }
                            return false;
                        }
                        return true;
                    });
                    return title;
                }
            }
            catch (IOException) { return null; }
        }

        /// control.tar.gz's files by name: control, and preinst, postinst, prerm, postrm when there.
        public static Dictionary<string, byte[]> Control(string ipk)
        {
            var out_ = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            using (var input = File.OpenRead(ipk))
            {
                var control = Member(input, "control.tar.gz");
                if (control == null) return out_;
                ReadTar(new GZipStream(control, CompressionMode.Decompress), (path, type, size, body) =>
                {
                    var name = path.Substring(path.LastIndexOf('/') + 1);
                    if (type == '0' && name.Length > 0 && size < 1000000) out_[name] = ReadBytes(body, size);
                    return true;
                });
            }
            return out_;
        }

        private static Stream Member(Stream input, string name)
        {
            var magic = new byte[8];
            if (!ReadFullyOrEof(input, magic)) return null;
            if (Encoding.GetEncoding("ISO-8859-1").GetString(magic) == "!<arch>\n") return ArMember(input, name);
            if (magic[0] == 0x1f && magic[1] == 0x8b)
            {
                input.Seek(0, SeekOrigin.Begin);
                return TarMember(new GZipStream(input, CompressionMode.Decompress, true), name);
            }
            return null;
        }

        private static Stream ArMember(Stream input, string name)
        {
            var h = new byte[60];
            var latin = Encoding.GetEncoding("ISO-8859-1");
            while (true)
            {
                if (!ReadFullyOrEof(input, h)) return null;
                var member = latin.GetString(h, 0, 16).Trim().TrimEnd('/');
                long size;
                if (!long.TryParse(latin.GetString(h, 48, 10).Trim(), out size)) throw new BadPackage("bad ar header");
                if (member == name) return new Bounded(input, size);
                Skip(input, size + (size & 1));
            }
        }

        private static Stream TarMember(Stream input, string name)
        {
            Stream found = null;
            ReadTar(input, (path, type, size, body) =>
            {
                if (type == '0' && path.TrimStart('.', '/') == name) { found = new MemoryStream(ReadBytesLarge(body, size)); return false; }
                return true;
            });
            return found;
        }

        private static List<string> Untar(Stream input, string dest)
        {
            var written = new List<string>();
            ReadTar(input, (path, type, size, body) =>
            {
                var parts = path.Split('/').Where(p => p.Length > 0 && p != ".").ToList();
                if (parts.Count == 0) return true;
                // Entries are ./usr/palm/...; anything climbing out is refused.
                if (parts.Any(p => p == "..")) throw new BadPackage("unsafe path in package: " + path);
                var rel = string.Join("/", parts);
                var out_ = Path.Combine(dest, rel.Replace('/', '\\'));
                if (type == '5') Directory.CreateDirectory(out_);
                else if (type == '0')
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(out_));
                    using (var o = File.Create(out_)) body.CopyTo(o);
                    written.Add(rel);
                }
                return true;
            });
            return written;
        }

        /// Walks a tar stream (ustar, GNU long names, pax paths).
        private static void ReadTar(Stream input, Func<string, char, long, Bounded, bool> visit)
        {
            var h = new byte[512];
            string longName = null;
            while (true)
            {
                if (!ReadFullyOrEof(input, h) || h.All(b => b == 0)) return;
                char type = (char)h[156];
                if (type == '\0' || type == '7') type = '0';
                long size = Octal(h, 124, 12);
                long padded = (size + 511) / 512 * 512;
                if (type == 'L') { longName = Encoding.UTF8.GetString(ReadBytes(input, size)).TrimEnd('\0'); Skip(input, padded - size); continue; }
                if (type == 'x')
                {
                    var pax = Encoding.UTF8.GetString(ReadBytes(input, size));
                    var m = Regex.Match(pax, "\\d+ path=([^\n]*)\n");
                    if (m.Success) longName = m.Groups[1].Value;
                    Skip(input, padded - size);
                    continue;
                }
                if (type == 'g' || type == 'K') { Skip(input, padded); continue; }
                string name = longName;
                if (name == null)
                {
                    var n = CString(h, 0, 100);
                    var prefix = Encoding.ASCII.GetString(h, 257, 5) == "ustar" ? CString(h, 345, 155) : "";
                    name = prefix.Length == 0 ? n : prefix + "/" + n;
                }
                longName = null;
                var body = new Bounded(input, size);
                if (!visit(name, type, size, body)) return;
                body.SkipRest();
                Skip(input, padded - size);
            }
        }

        private static string CString(byte[] b, int off, int len)
        {
            int end = off;
            while (end < off + len && b[end] != 0) end++;
            return Encoding.UTF8.GetString(b, off, end - off);
        }

        private static long Octal(byte[] b, int off, int len)
        {
            if ((b[off] & 0x80) != 0) { long v = 0; for (int i = off + 1; i < off + len; i++) v = (v << 8) | b[i]; return v; }
            var s = CString(b, off, len).Trim();
            return s.Length == 0 ? 0 : Convert.ToInt64(s, 8);
        }

        private static bool ReadFullyOrEof(Stream s, byte[] b)
        {
            int n = 0;
            while (n < b.Length)
            {
                int r = s.Read(b, n, b.Length - n);
                if (r <= 0) { if (n == 0) return false; throw new EndOfStreamException(); }
                n += r;
            }
            return true;
        }

        private static byte[] ReadBytes(Stream s, long n)
        {
            if (n < 0 || n > MaxRead) throw new IOException("archive entry of " + n + " bytes");
            var b = new byte[n];
            if (n > 0 && !ReadFullyOrEof(s, b)) throw new EndOfStreamException();
            return b;
        }

        private static byte[] ReadBytesLarge(Stream s, long n)
        {
            var b = new byte[n];
            if (n > 0 && !ReadFullyOrEof(s, b)) throw new EndOfStreamException();
            return b;
        }

        private static void Skip(Stream s, long n)
        {
            var buf = new byte[8192];
            while (n > 0) { int r = s.Read(buf, 0, (int)Math.Min(n, buf.Length)); if (r <= 0) throw new EndOfStreamException(); n -= r; }
        }

        /// A view of the next `size` bytes of a stream.
        private sealed class Bounded : Stream
        {
            private readonly Stream s; private long left;
            public Bounded(Stream s, long size) { this.s = s; left = size; }
            public override int Read(byte[] b, int off, int len)
            {
                if (left <= 0) return 0;
                int r = s.Read(b, off, (int)Math.Min(len, left));
                if (r > 0) left -= r;
                return r;
            }
            public void SkipRest() { var buf = new byte[8192]; while (Read(buf, 0, buf.Length) > 0) { } }
            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
            public override void Flush() { }
            public override long Seek(long o, SeekOrigin so) { throw new NotSupportedException(); }
            public override void SetLength(long v) { throw new NotSupportedException(); }
            public override void Write(byte[] b, int o, int c) { throw new NotSupportedException(); }
        }
    }

    /// Installs .ipk packages into Lunacy's /media/cryptofs/apps (LocalState\webos), where the
    /// app registry finds them and the shell serves them, as Preware did through ipkg (LunaCore's
    /// Packages.kt): records in usr/lib/ipkg/info, each app folder replaced whole, the rest
    /// merged. One install at a time, off the UI thread.
    ///
    /// A package's preinst, postinst, prerm and postrm scripts are shell scripts, which ipkg ran
    /// with /bin/sh; Windows 10 Mobile lets an app start no process, so they are kept with the
    /// record but not run, and the log says so for each. What they did on a device is mostly
    /// done here anyway: services and db kinds are picked up by the rescan after an install.
    internal sealed class Packages
    {
        public sealed class Result
        {
            public string Source, Error, PackageId = "";
            public List<string> AppIds = new List<string>();
        }

        public const string AppsRel = "usr/palm/applications";
        private const string PackagesRel = "usr/palm/packages", ServicesRel = "usr/palm/services", InfoRel = "usr/lib/ipkg/info";
        private static readonly Regex PackageName = new Regex("^[A-Za-z0-9][A-Za-z0-9._+-]{0,127}$");
        private readonly string root, cache;
        private readonly WebosFs fs;
        private readonly SemaphoreSlim one = new SemaphoreSlim(1);

        /// `root`: Lunacy's /media/cryptofs/apps. `cache`: where downloads wait.
        public Packages(string root, string cache, WebosFs fs) { this.root = root; this.cache = cache; this.fs = fs; Directory.CreateDirectory(Path.Combine(root, AppsRel.Replace('/', '\\'))); }

        private string P(string rel) { return Path.Combine(root, rel.Replace('/', '\\')); }

        /// source: an http(s) URL, a file:// URL or a webOS path. progress: 0..100, the download
        /// being 0..90. title: the package's app's title once it is here.
        public void Install(string source, Action<int> progress, Action<string> title, Action<Result> done)
        {
            Task.Run(async () =>
            {
                await one.WaitAsync();
                Result r;
                try { r = await InstallNow(source, progress ?? (_ => { }), title ?? (_ => { })); }
                catch (Exception e)
                {
                    LunaLog.Write("[packages] install " + source + " failed: " + e.GetType().Name + ": " + e.Message);
                    r = new Result { Source = source, Error = e.Message };
                }
                finally { one.Release(); }
                done(r);
            });
        }

        private async Task<Result> InstallNow(string source, Action<int> progress, Action<string> title)
        {
            var fetched = await Fetch(source, progress);
            var ipk = fetched.Item1;
            var staging = Path.Combine(Path.GetDirectoryName(root.TrimEnd('\\')), "staging");
            try
            {
                var t = Ipk.AppTitle(ipk);
                if (t != null) title(t);
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
                Directory.CreateDirectory(staging);
                var control = Ipk.Control(ipk);
                byte[] c;
                var fields = control.TryGetValue("control", out c) ? Encoding.UTF8.GetString(c) : "";
                var m = Regex.Match(fields, "(?m)^Package:\\s*(\\S+)");
                var pkg = m.Success ? m.Groups[1].Value : source.Substring(source.LastIndexOf('/') + 1).Split('_')[0];
                if (!PackageName.IsMatch(pkg) || pkg.Contains("..")) throw new IOException("its package name isn't one: " + pkg);
                var files = Ipk.Extract(ipk, staging);
                LunaLog.Write("[packages] install " + source + ": package " + pkg + ", " + files.Count + " files");
                NotRun(pkg, "preinst", control);
                var appsDir = Path.Combine(staging, AppsRel.Replace('/', '\\'));
                var apps = Directory.Exists(appsDir) ? Directory.GetDirectories(appsDir).Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).ToList() : new List<string>();
                // Each app, package and service folder replaces any earlier version whole.
                foreach (var rel in new[] { AppsRel, PackagesRel, ServicesRel })
                {
                    var from = Path.Combine(staging, rel.Replace('/', '\\'));
                    if (!Directory.Exists(from)) continue;
                    foreach (var d in Directory.GetDirectories(from))
                    {
                        var dest = Path.Combine(P(rel), Path.GetFileName(d));
                        if (Directory.Exists(dest)) Directory.Delete(dest, true);
                        Directory.CreateDirectory(P(rel));
                        Directory.Move(d, dest);
                    }
                    foreach (var f in Directory.GetFiles(from))
                    {
                        var dest = Path.Combine(P(rel), Path.GetFileName(f));
                        Directory.CreateDirectory(P(rel));
                        if (File.Exists(dest)) File.Delete(dest);
                        File.Move(f, dest);
                    }
                }
                Merge(staging, root);
                // ipkg's record: the control file, the scripts and the list of files.
                Directory.CreateDirectory(P(InfoRel));
                foreach (var name in new[] { "control", "preinst", "postinst", "prerm", "postrm", "conffiles" })
                {
                    var f = Path.Combine(P(InfoRel), pkg + "." + name);
                    byte[] body;
                    if (control.TryGetValue(name, out body)) File.WriteAllBytes(f, body); else if (File.Exists(f)) File.Delete(f);
                }
                File.WriteAllText(Path.Combine(P(InfoRel), pkg + ".list"), string.Concat(files.Select(x => "/media/cryptofs/apps/" + x + "\n")));
                NotRun(pkg, "postinst", control);
                progress(100);
                return new Result { Source = source, AppIds = apps, PackageId = pkg };
            }
            finally
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch (Exception) { }
                if (fetched.Item2) try { File.Delete(ipk); } catch (Exception) { }
            }
        }

        private static void NotRun(string pkg, string name, Dictionary<string, byte[]> control)
        {
            if (control.ContainsKey(name)) LunaLog.Write("[packages] script [" + pkg + " " + name + "] not run: Windows 10 Mobile starts no shell for it");
        }

        private static void Merge(string from, string to)
        {
            foreach (var d in Directory.GetDirectories(from)) { var dest = Path.Combine(to, Path.GetFileName(d)); Directory.CreateDirectory(dest); Merge(d, dest); }
            foreach (var f in Directory.GetFiles(from)) { var dest = Path.Combine(to, Path.GetFileName(f)); if (File.Exists(dest)) File.Delete(dest); File.Move(f, dest); }
        }

        /// The package as a local file, and whether it is a download to delete afterwards.
        private async Task<Tuple<string, bool>> Fetch(string source, Action<int> progress)
        {
            int colon = source.IndexOf("://", StringComparison.Ordinal);
            var scheme = colon < 0 ? "" : source.Substring(0, colon).ToLowerInvariant();
            Directory.CreateDirectory(cache);
            var tmp = Path.Combine(cache, "pkg-" + Guid.NewGuid().ToString("N") + ".ipk");
            if (scheme == "" || scheme == "file")
            {
                var path = scheme == "file" ? Uri.UnescapeDataString(new Uri(source).AbsolutePath) : source;
                // A webOS path: Lunacy's tree or the phone's libraries under /media/internal.
                File.WriteAllBytes(tmp, fs.Read(path));
                return Tuple.Create(tmp, true);
            }
            if (scheme != "http" && scheme != "https") throw new IOException("can't fetch " + scheme + ":// packages");
            using (var http = new Windows.Web.Http.HttpClient())
            {
                var res = await http.GetAsync(new Uri(source), Windows.Web.Http.HttpCompletionOption.ResponseHeadersRead);
                if ((int)res.StatusCode != 200) throw new IOException("HTTP " + (int)res.StatusCode + " for " + source);
                long total = (long)(res.Content.Headers.ContentLength ?? 0);
                using (var input = (await res.Content.ReadAsInputStreamAsync()).AsStreamForRead())
                using (var o = File.Create(tmp))
                {
                    var buf = new byte[16384];
                    long got = 0; int last = -1;
                    while (true)
                    {
                        int n = await input.ReadAsync(buf, 0, buf.Length);
                        if (n <= 0) break;
                        o.Write(buf, 0, n); got += n;
                        if (total > 0) { int p = (int)(got * 90 / total); if (p != last) { last = p; progress(p); } }
                    }
                }
            }
            progress(90);
            return Tuple.Create(tmp, true);
        }

        /// Removes an installed app with its package: the package whose packageinfo.json names
        /// it, that package's services and its record. `done` gets the error, or null.
        public void Remove(string appId, string dir, Action<string> done)
        {
            Task.Run(async () =>
            {
                await one.WaitAsync();
                string error = null;
                try
                {
                    var app = Path.Combine(P(AppsRel), Path.GetFileName(dir));
                    if (!Directory.Exists(app)) throw new IOException(appId + " isn't installed");
                    var pkgs = Directory.Exists(P(PackagesRel)) ? Directory.GetDirectories(P(PackagesRel)).Where(pd =>
                    {
                        try { return Jx.ParseObj(File.ReadAllText(Path.Combine(pd, "packageinfo.json"))).Str("app") == appId; } catch (Exception) { return false; }
                    }).ToList() : new List<string>();
                    var ids = pkgs.Select(pd => { try { return Jx.ParseObj(File.ReadAllText(Path.Combine(pd, "packageinfo.json"))).Str("id"); } catch (Exception) { return ""; } })
                        .Where(x => x.Length > 0).ToList();
                    if (ids.Count == 0) ids.Add(appId);
                    foreach (var id in ids) if (File.Exists(Path.Combine(P(InfoRel), id + ".prerm"))) LunaLog.Write("[packages] script [" + id + " prerm] not run: Windows 10 Mobile starts no shell for it");
                    foreach (var pd in pkgs)
                    {
                        JArr services = null;
                        try { services = Jx.ParseObj(File.ReadAllText(Path.Combine(pd, "packageinfo.json"))).Arr("services"); } catch (Exception) { }
                        foreach (var s in (services ?? new JArr()).OfType<string>())
                        {
                            var sd = Path.Combine(P(ServicesRel), Path.GetFileName(s));
                            if (Directory.Exists(sd)) Directory.Delete(sd, true);
                        }
                        Directory.Delete(pd, true);
                    }
                    Directory.Delete(app, true);
                    foreach (var id in ids) { RemoveListed(id); Forget(id); }
                    LunaLog.Write("[packages] removed " + appId);
                }
                catch (Exception e) { LunaLog.Write("[packages] remove " + appId + " failed: " + e.Message); error = e.Message; }
                finally { one.Release(); }
                done(error);
            });
        }

        private void RemoveListed(string pkg)
        {
            var list = Path.Combine(P(InfoRel), pkg + ".list");
            if (!File.Exists(list)) return;
            var dirs = new HashSet<string>();
            foreach (var line in File.ReadAllLines(list))
            {
                var rel = line.Trim();
                if (rel.StartsWith("/media/cryptofs/apps/", StringComparison.Ordinal)) rel = rel.Substring("/media/cryptofs/apps/".Length);
                if (rel.Length == 0 || rel.Split('/').Any(x => x == "..")) continue;
                var f = P(rel);
                if (!File.Exists(f)) continue;
                File.Delete(f);
                for (var d = Path.GetDirectoryName(f); d != null && d.Length > root.TrimEnd('\\').Length; d = Path.GetDirectoryName(d)) dirs.Add(d);
            }
            foreach (var d in dirs.OrderByDescending(x => x.Length)) try { if (Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch (Exception) { }
        }

        private void Forget(string pkg)
        {
            foreach (var name in new[] { "control", "list", "preinst", "postinst", "prerm", "postrm", "conffiles" })
            {
                var f = Path.Combine(P(InfoRel), pkg + "." + name);
                if (File.Exists(f)) File.Delete(f);
            }
        }

        /// A package's packageinfo.json, or null.
        public JObj PackageInfo(string appId)
        {
            var f = Path.Combine(P(PackagesRel), appId, "packageinfo.json");
            try { return File.Exists(f) ? Jx.ParseObj(File.ReadAllText(f)) : null; } catch (Exception) { return null; }
        }

        public bool HasPackageIcon(string appId) { return File.Exists(Path.Combine(P(PackagesRel), appId, "icon.png")); }

        /// A service's services.json in the installed tree or the ROM, or null.
        public JObj ServiceJson(string id)
        {
            foreach (var path in new[] { "/media/cryptofs/apps/usr/palm/services/" + id + "/services.json", "/usr/palm/services/" + id + "/services.json" })
                try { if (fs.Exists(path)) return Jx.ParseObj(Encoding.UTF8.GetString(fs.Read(path))); } catch (Exception) { }
            return null;
        }

        /// Free space where packages go, in bytes.
        public static long FreeBytes()
        {
            try
            {
                var props = ApplicationData.Current.LocalFolder.Properties.RetrievePropertiesAsync(new[] { "System.FreeSpace" }).AsTask().GetAwaiter().GetResult();
                object v;
                return props.TryGetValue("System.FreeSpace", out v) && v is ulong ? (long)(ulong)v : long.MaxValue;
            }
            catch (Exception) { return long.MaxValue; }
        }
    }

    /// One added launch point (LunaCore's LaunchPoint.kt).
    internal sealed class LaunchPoint
    {
        public string AppId, LaunchPointId, Title, AppMenu, Icon, Params;
        public bool Removable;

        /// LunaCE's LaunchPoint::toJSON, as written to its file and posted to launchPointChanges.
        public JObj ToJson(AppInfo app)
        {
            var j = new JObj().Put("id", AppId);
            if (app != null) j.Put("version", app.Version).Put("appId", app.Id).Put("vendor", app.Vendor).Put("vendorUrl", Jx.ObjOrEmpty(app.Raw.Stringify()).Str("vendorurl"));
            j.Put("size", 0).Put("packageId", AppId).Put("removable", Removable).Put("launchPointId", LaunchPointId)
                .Put("title", Title).Put("appmenu", AppMenu).Put("icon", Icon);
            if (Params != null) { try { j.Put("params", Jx.Parse(Params)); } catch (Exception) { } }
            return j;
        }
    }

    /// The dynamic launch points, one file each in /var/luna/launchpoints, named by id
    /// (LunaCore's LaunchPoints.kt; LunaSysMgr's Settings::lunaLaunchPointsPath). One whose app
    /// isn't installed is kept but not shown.
    internal sealed class LaunchPoints
    {
        private readonly string dir;
        private List<LaunchPoint> all;
        private readonly object gate = new object();
        private static readonly Random random = new Random();

        public LaunchPoints(string dir) { this.dir = dir; all = Load(); }

        public List<LaunchPoint> All { get { lock (gate) return all.ToList(); } }
        public LaunchPoint Get(string id) { lock (gate) return all.FirstOrDefault(l => l.LaunchPointId == id); }

        private List<LaunchPoint> Load()
        {
            var list = new List<LaunchPoint>();
            if (!Directory.Exists(dir)) return list;
            foreach (var f in Directory.GetFiles(dir).OrderBy(x => x, StringComparer.Ordinal))
            {
                try
                {
                    var j = Jx.ParseObj(File.ReadAllText(f));
                    if (!j.Has("title") || !j.Has("icon") || !j.Has("params") || !j.Has("id")) continue;
                    list.Add(new LaunchPoint { AppId = j.Str("id"), LaunchPointId = Path.GetFileName(f), Title = j.Str("title"), AppMenu = j.Has("appmenu") ? j.Str("appmenu") : j.Str("title"),
                        Icon = j.Str("icon"), Params = Jx.Stringify(j["params"]), Removable = j.Bool("removable") });
                }
                catch (Exception e) { LunaLog.Write("[launchpoints] " + Path.GetFileName(f) + " unreadable: " + e.Message); }
            }
            return list;
        }

        public LaunchPoint Add(AppInfo app, string title, string appmenu, string icon, string params_, bool removable)
        {
            lock (gate)
            {
                Directory.CreateDirectory(dir);
                // findUniqueFileName: a random number from 1 to 1000000 that names no file yet.
                string id;
                do { id = (1 + random.Next(1000000)).ToString(); } while (File.Exists(Path.Combine(dir, id)));
                var lp = new LaunchPoint { AppId = app.Id, LaunchPointId = id, Title = title, AppMenu = appmenu, Icon = icon, Params = params_, Removable = removable };
                if (!Write(lp, app)) return null;
                all = all.Concat(new[] { lp }).ToList();
                return lp;
            }
        }

        /// ApplicationManager::removeLaunchPoint: an error's text, or null once gone.
        public string Remove(string id)
        {
            lock (gate)
            {
                int n;
                if (!int.TryParse(id, out n)) return "";
                var lp = all.FirstOrDefault(l => l.LaunchPointId == id);
                if (lp == null) return "launch point [" + id + "] not found";
                // LunaSysMgr's own wording.
                if (!lp.Removable) return "launch point [" + id + "] not marked non-removable";
                try { File.Delete(Path.Combine(dir, id)); } catch (Exception) { return "launch point deletion failed"; }
                all = all.Where(l => l != lp).ToList();
                return null;
            }
        }

        public LaunchPoint UpdateIcon(LaunchPoint lp, AppInfo app, string icon)
        {
            lock (gate)
            {
                var u = new LaunchPoint { AppId = lp.AppId, LaunchPointId = lp.LaunchPointId, Title = lp.Title, AppMenu = lp.AppMenu, Icon = icon.StartsWith("file://", StringComparison.Ordinal) ? icon.Substring(7) : icon, Params = lp.Params, Removable = lp.Removable };
                Write(u, app);
                all = all.Select(l => l == lp ? u : l).ToList();
                return u;
            }
        }

        private bool Write(LaunchPoint lp, AppInfo app)
        {
            try { File.WriteAllText(Path.Combine(dir, lp.LaunchPointId), lp.ToJson(app).ToString()); return true; }
            catch (Exception e) { LunaLog.Write("[launchpoints] can't write " + lp.LaunchPointId + ": " + e.Message); return false; }
        }
    }

    /// Which app opens a file of each type (LunaCore's ContentHandlers.kt): the installed
    /// apps' appinfo.json mimeTypes, then /usr/palm/command-resource-handlers.json's resources;
    /// its redirects and commands for URLs. Entries for an app Lunacy doesn't have are left out.
    internal sealed class ContentHandlers
    {
        public sealed class Entry { public string Mime, Extension, AppId; public bool Streamable, System; public int Index; }

        private readonly AppRegistry registry;
        private readonly string tableFile;
        private JObj table;
        private List<KeyValuePair<Regex, string>> redirects = new List<KeyValuePair<Regex, string>>(), commands = new List<KeyValuePair<Regex, string>>();

        public ContentHandlers(AppRegistry registry, string rom)
        {
            this.registry = registry;
            tableFile = Path.Combine(rom, "usr", "palm", "command-resource-handlers.json");
        }

        private JObj Table()
        {
            lock (this)
            {
                if (table != null) return table;
                try { table = Jx.ParseObj(File.ReadAllText(tableFile)); } catch (Exception e) { LunaLog.Write("[handlers] " + e.Message); table = new JObj(); }
                Func<string, List<KeyValuePair<Regex, string>>> patterns = key => (table.Arr(key) ?? new JArr()).OfType<JObj>().Select(r =>
                {
                    try { return new KeyValuePair<Regex, string>(new Regex(r.Str("url"), RegexOptions.IgnoreCase), r.Str("appId")); } catch (Exception) { return new KeyValuePair<Regex, string>(null, null); }
                }).Where(p => p.Key != null).ToList();
                redirects = patterns("redirects");
                commands = patterns("commands");
                return table;
            }
        }

        public string RedirectFor(string target)
        {
            if (string.IsNullOrEmpty(target)) return null;
            Table();
            var r = redirects.FirstOrDefault(p => p.Key.IsMatch(target)).Value;
            return string.IsNullOrEmpty(r) ? null : r;
        }

        public string CommandFor(string target)
        {
            if (string.IsNullOrEmpty(target)) return null;
            Table();
            var r = commands.FirstOrDefault(p => p.Key.IsMatch(target)).Value;
            return !string.IsNullOrEmpty(r) && registry.Get(r) != null ? r : null;
        }

        public List<Entry> Entries()
        {
            var out_ = new List<Entry>();
            Action<string, string, string, bool, bool> add = (mime, ext, app, stream, system) =>
            {
                if (string.IsNullOrEmpty(mime) || string.IsNullOrEmpty(app) || registry.Get(app) == null) return;
                out_.Add(new Entry { Mime = mime.ToLowerInvariant(), Extension = (ext ?? "").ToLowerInvariant(), AppId = app, Streamable = stream, System = system, Index = out_.Count });
            };
            foreach (var a in registry.All())
            {
                var list = Jx.ObjOrEmpty(a.Raw.Stringify()).Arr("mimeTypes");
                if (list == null) continue;
                foreach (var m in list.OfType<JObj>()) add(m.Str("mime"), m.Str("extension"), a.Id, m.Bool("stream"), false);
            }
            foreach (var r in (Table().Arr("resources") ?? new JArr()).OfType<JObj>())
                add(r.Str("mime"), r.Str("extn"), r.Str("appId"), r.Bool("streamable"), true);
            return out_;
        }

        public Entry ByExtension(string ext) { if (string.IsNullOrEmpty(ext)) return null; var e = ext.ToLowerInvariant(); return Entries().FirstOrDefault(x => x.Extension == e); }
        public Entry ByMime(string mime) { if (string.IsNullOrEmpty(mime)) return null; var m = mime.ToLowerInvariant(); return Entries().FirstOrDefault(x => x.Mime == m); }

        public static string ExtensionOf(string target)
        {
            var path = target.Split('#')[0].Split('?')[0];
            path = path.Substring(path.LastIndexOf('/') + 1);
            int dot = path.LastIndexOf('.');
            return dot < 0 ? "" : Uri.UnescapeDataString(path.Substring(dot + 1));
        }

        public Entry ForTarget(string target) { return ByExtension(ExtensionOf(target)); }

        public string HandlerForExtension(JObj p)
        {
            var ext = p.Str("extension");
            var e = ByExtension(ext);
            if (e == null) return new JObj().Put("subscribed", false).Put("returnValue", false).Put("errorCode", "No mime type mapped to extension " + ext).ToString();
            return Answer(e);
        }

        public string HandlerForUrl(JObj p)
        {
            var url = p.Str("url");
            var e = ForTarget(url);
            if (e != null) return Answer(e);
            var app = CommandFor(url);
            if (app != null) return new JObj().Put("subscribed", false).Put("returnValue", true).Put("appId", app).Put("download", false).ToString();
            return new JObj().Put("subscribed", false).Put("returnValue", false).Put("errorCode", "No handler found for url [" + url + "]").ToString();
        }

        private static string Answer(Entry e)
        {
            return new JObj().Put("subscribed", false).Put("returnValue", true).Put("mimeType", e.Mime).Put("appId", e.AppId).Put("download", !e.Streamable).ToString();
        }

        public JObj ActiveHandler(string mime)
        {
            var e = ByMime(mime);
            if (e == null) return null;
            var j = new JObj().Put("mime", e.Mime).Put("extension", e.Extension).Put("appId", e.AppId).Put("streamable", e.Streamable).Put("index", e.Index);
            if (e.System) j.Put("tag", "system-default");
            var a = registry.Get(e.AppId);
            return j.Put("appName", a == null ? e.AppId : a.Title);
        }
    }

    /// com.palm.applicationManager, as LunaSysMgr answered it (LunaCore's ApplicationManager.kt):
    /// launch and open (a web page to the Web app, a URL to the app the system's
    /// command-resource-handlers send it to, a file to the app that handles its type, a
    /// package to the installer, any other link to Windows), the content handlers' questions,
    /// the package and launch-point lists and their changes, rescan, running and close.
    internal sealed class ApplicationManager
    {
        private const string Service = "com.palm.applicationManager";
        public const string Browser = "com.palm.app.browser";
        public const string IpkMime = "application/vnd.webos.ipk";
        public static readonly HashSet<string> Installers = new HashSet<string> { "org.webosinternals.preware", "org.webosports.app.preware" };

        private readonly LunaShell shell;
        private readonly Subscribers launchPointWatchers = new Subscribers();
        private Bus bus;

        public ApplicationManager(LunaShell shell) { this.shell = shell; }

        private AppRegistry Apps { get { return shell.Apps; } }
        private ContentHandlers Handlers { get { return shell.Handlers; } }

        private static bool IsWebUrl(string u) { var l = (u ?? "").ToLowerInvariant(); return l.StartsWith("http://") || l.StartsWith("https://") || l.StartsWith("data:"); }
        private static string Launched() { return new JObj().Put("returnValue", true).Put("processId", "success").ToString(); }

        /// Private bus only on webOS: Lunacy's own apps, the ROM's, the system, and services.
        private bool Inside(string caller) { return shell.Privileged(caller); }

        public void Register(Bus bus)
        {
            this.bus = bus;
            bus.Register(Service, "launch", Launch);
            bus.Register(Service, "open", Launch);
            bus.Register(Service, "getAppBasePath", c =>
            {
                var id = Jx.ObjOrEmpty(c.Raw).Str("appId");
                var a = Apps.Get(id);
                c.Reply(a == null ? Bus.Error("Application not found: " + id)
                    : new JObj().Put("returnValue", true).Put("appId", a.Id).Put("basePath", "file:///" + AppServer.AppsPath + a.Id + "/" + a.Main).ToString());
            });
            bus.Register(Service, "rescan", c => { shell.AppsChanged(); c.Reply(Bus.Ok()); });
            bus.Register(Service, "listApps", c => c.Reply("{\"returnValue\":true,\"apps\":" + Apps.Json() + "}"));
            bus.Register(Service, "getAppInfo", c =>
            {
                var p = Jx.ObjOrEmpty(c.Raw);
                var a = Apps.Get(p.Has("appId") ? p.Str("appId") : p.Str("id"));
                c.Reply(a == null ? Bus.Error("Application not found") : "{\"returnValue\":true,\"appInfo\":" + a.Listed().Stringify() + "}");
            });
            bus.Register(Service, "launchPointChanges", c =>
            {
                if (!Inside(c.AppId)) { c.Reply(Bus.Error("Unknown method \"launchPointChanges\" for category \"/\"", -1)); return; }
                if (!c.Subscribe) { c.Reply(new JObj().Put("returnValue", false).Put("subscribed", false).Put("errorText", "Only supports subscriptions").ToString()); return; }
                c.Reply(new JObj().Put("returnValue", true).Put("subscribed", true).ToString());
                if (!c.Cancelled) launchPointWatchers.Add(c);
            });
            bus.Register(Service, "addLaunchPoint", c => c.Reply(AddLaunchPoint(Jx.ObjOrEmpty(c.Raw))));
            bus.Register(Service, "removeLaunchPoint", c =>
            {
                var id = Jx.ObjOrEmpty(c.Raw).Str("launchPointId");
                Func<string, string> fail = t => new JObj().Put("returnValue", false).Put("errorText", t).ToString();
                if (id.Length == 0) { c.Reply(fail("Must provide a launchPointId")); return; }
                var err = RemoveLaunchPoint(id);
                c.Reply(err == null ? Bus.Ok() : fail(err));
            });
            bus.Register(Service, "updateLaunchPointIcon", c => c.Reply(UpdateLaunchPointIcon(c.AppId, Jx.ObjOrEmpty(c.Raw))));
            bus.Register(Service, "listAllHandlersForMime", c => c.Reply(HandlersForMime(Jx.ObjOrEmpty(c.Raw))));
            bus.Register(Service, "getResourceInfo", c => c.Reply(ResourceInfo(Jx.ObjOrEmpty(c.Raw))));
            bus.Register(Service, "getHandlerForExtension", c => c.Reply(Handlers.HandlerForExtension(Jx.ObjOrEmpty(c.Raw))));
            bus.Register(Service, "getHandlerForUrl", c => c.Reply(Handlers.HandlerForUrl(Jx.ObjOrEmpty(c.Raw))));
            bus.Register(Service, "listPackages", c =>
            {
                if (!Inside(c.AppId)) { c.Reply(Bus.Error("Unknown method \"listPackages\" for category \"/\"", -1)); return; }
                c.Reply(ListPackages());
            });
            // Which windows are up, and closing one, as palm-launch -c and palm-run use them.
            bus.Register(Service, "running", c =>
            {
                if (!Inside(c.AppId)) { c.Reply(Bus.Error("Unknown method \"running\" for category \"/\"", -1)); return; }
                var list = new JArr();
                foreach (var w in shell.RunningWindows()) list.Add(new JObj().Put("id", w.Key).Put("processid", w.Value.ToString()));
                c.Reply(new JObj().Put("running", list).Put("returnValue", true).ToString());
            });
            bus.Register(Service, "close", c =>
            {
                if (!Inside(c.AppId)) { c.Reply(Bus.Error("Unknown method \"close\" for category \"/\"", -1)); return; }
                var pid = Jx.ObjOrEmpty(c.Raw)["processId"];
                var text = pid is string ? (string)pid : pid != null ? Jx.Stringify(pid) : "";
                if (text.Length == 0) { c.Reply(new JObj().Put("returnValue", false).Put("errorText", "Must provide a valid processId to close").ToString()); return; }
                int id;
                if (int.TryParse(text, out id)) shell.Evaluate("window.__lunacyShell&&__lunacyShell.close(" + id + ")");
                c.Reply(Bus.Ok());
            });
        }

        private void Launch(Bus.Call c)
        {
            var p = Jx.ObjOrEmpty(c.Raw);
            var id = p.Str("id");
            var target = p.Str("target");
            var inner = p.Obj("params");
            // An activity's callback carries "$activity" beside the params, and the application
            // manager put it into the params the app gets (luna-sysmgr).
            if (p.Has("$activity")) { inner = inner == null ? new JObj() : inner.Copy(); inner.Put("$activity", p["$activity"]); }
            var paramsJson = inner == null ? "" : inner.ToString();
            if (id.Length == 0 && target.Length > 0)
            {
                // A web page is the Web app's (^https?: and ^data:), a card of its own for each.
                if (IsWebUrl(target) && Apps.Get(Browser) != null)
                {
                    shell.Launch(Browser, new JObj().Put("target", target).ToString(), c.AppId);
                    c.Reply(Launched());
                    return;
                }
                // A bare target an installed app claims in command-resource-handlers.
                var redirect = Handlers.RedirectFor(target);
                var claimed = redirect != null && Apps.Get(redirect) != null ? redirect : Handlers.CommandFor(target);
                if (claimed != null)
                {
                    shell.Launch(claimed, new JObj().Put("target", target).ToString(), c.AppId);
                    c.Reply(Launched());
                    return;
                }
                // A package goes to Lunacy's installer, as Preware took it.
                if (ContentHandlers.ExtensionOf(target).Equals("ipk", StringComparison.OrdinalIgnoreCase))
                {
                    shell.Install(target, c.AppId, null, true, null);
                    c.Reply(Launched());
                    return;
                }
                // A file goes to the app that handles its type, with the target as given.
                var h = Handlers.ForTarget(target);
                if (h != null)
                {
                    shell.Launch(h.AppId, new JObj().Put("target", target).ToString(), c.AppId);
                    c.Reply(Launched());
                    return;
                }
                if (target.StartsWith("file:", StringComparison.OrdinalIgnoreCase) || target.StartsWith("/", StringComparison.Ordinal))
                {
                    c.Reply(new JObj().Put("returnValue", false).Put("errorText", "No handler for " + target).ToString());
                    return;
                }
                // Any other link: Windows' answer (a phone number to the dialer, maps, the store).
                Uri uri;
                if (Uri.TryCreate(target, UriKind.Absolute, out uri))
                {
                    shell.Ui(async () =>
                    {
                        bool ok = false;
                        try { ok = await Windows.System.Launcher.LaunchUriAsync(uri); } catch (Exception) { }
                        c.Reply(ok ? Launched() : Bus.Error("Windows couldn't open " + uri.Scheme + ": links"));
                    });
                    return;
                }
                c.Reply(Bus.Error("No handler for " + target));
                return;
            }
            // An added launch point launches its app with its own params.
            var lp = shell.AddedLaunchPoints.Get(id);
            if (lp != null && Apps.Get(lp.AppId) != null)
            {
                shell.Launch(lp.AppId, lp.Params ?? "", c.AppId);
                c.Reply(Launched());
                return;
            }
            // The App Museum installs through Preware; Lunacy's package manager answers for it.
            if (Installers.Contains(id) && Apps.Get(id) == null && inner != null && inner.Str("type") == "install")
            {
                var file = inner.Str("file").Length > 0 ? inner.Str("file") : inner.Str("target");
                if (file.Length == 0) { c.Reply(Bus.Error("install: no file given")); return; }
                shell.Install(file, c.AppId, null, true, null);
                c.Reply(Launched());
                return;
            }
            if (Apps.Get(id) == null) { c.Reply(Bus.Error("Application not found: " + id)); return; }
            shell.Launch(id, paramsJson, c.AppId);
            c.Reply(Launched());
        }

        private string HandlersForMime(JObj p)
        {
            var mime = p.Str("mime");
            if (mime.Length == 0 && p.Str("url").Length == 0)
                return new JObj().Put("subscribed", false).Put("returnValue", false).Put("errorCode", "Must have either an url or a mime parameter").ToString();
            bool isIpk = mime == IpkMime || (mime.Length == 0 && p.Str("url").Split('?')[0].EndsWith(".ipk", StringComparison.OrdinalIgnoreCase));
            var j = new JObj().Put("subscribed", false).Put("mime", mime.Length > 0 ? mime : IpkMime);
            if (!isIpk)
            {
                var active = Handlers.ActiveHandler(mime);
                if (active == null) return j.Put("returnValue", false).Put("errorCode", "No handlers found for " + mime).ToString();
                return j.Put("returnValue", true).Put("resourceHandlers", new JObj().Put("activeHandler", active)).ToString();
            }
            // Lunacy answers as the Preware it stands in for (codepoet's call).
            return j.Put("returnValue", true).Put("resourceHandlers", new JObj().Put("activeHandler", new JObj()
                .Put("mime", IpkMime).Put("extension", "ipk").Put("appId", "org.webosinternals.preware")
                .Put("streamable", true).Put("index", 0).Put("appName", "Preware"))).ToString();
        }

        private string ResourceInfo(JObj p)
        {
            var uri = p.Str("uri");
            if (uri.Length == 0) return Bus.Error("getResourceInfo: no uri");
            var byMime = Handlers.ByMime(p.Str("mime"));
            var e = byMime ?? Handlers.ForTarget(uri);
            var j = new JObj().Put("returnValue", true).Put("uri", uri);
            if (e != null)
            {
                j.Put("appIdByExtension", e.AppId);
                if (byMime == null) j.Put("mimeByExtension", e.Mime);
                return j.Put("canStream", e.Streamable).ToString();
            }
            if (IsWebUrl(uri)) return j.Put("appIdByExtension", Browser).Put("canStream", false).ToString();
            var cmd = Handlers.CommandFor(uri);
            if (cmd != null) return j.Put("appIdByExtension", cmd).Put("canStream", false).ToString();
            return new JObj().Put("returnValue", false).Put("errorText", "can't find a handler for the given uri").ToString();
        }

        /// listPackages, which App Catalog reads before it follows its installs: every package,
        /// the system's with userInstalled false, each with its apps and services.
        private string ListPackages()
        {
            var list = new JArr();
            foreach (var a in Apps.All())
            {
                var info = shell.Packages.PackageInfo(a.Id);
                var services = new JArr();
                foreach (var id in ((info == null ? null : info.Arr("services")) ?? new JArr()).OfType<string>())
                {
                    var sj = shell.Packages.ServiceJson(id);
                    if (sj == null) continue;
                    var o = new JObj().Put("id", id);
                    foreach (var kv in sj) o.Put(kv.Key, kv.Value);
                    services.Add(o);
                }
                var entry = Jx.ObjOrEmpty(a.Listed().Stringify());
                var icon = shell.Packages.HasPackageIcon(a.Id) ? "/media/cryptofs/apps/usr/palm/packages/" + a.Id + "/icon.png" : entry.Str("icon");
                list.Add(new JObj().Put("id", info != null && info.Str("id").Length > 0 ? info.Str("id") : a.Id).Put("loc_name", a.Title)
                    .Put("package_format_version", 2).Put("vendor", a.Vendor).Put("version", a.Version).Put("size", 0).Put("icon", icon)
                    .Put("userInstalled", a.Removable).Put("apps", new JArr().Put(entry)).Put("services", services));
            }
            return new JObj().Put("returnValue", true).Put("packages", list).ToString();
        }

        /// An app's default launch point came or went (the shell's rescan).
        public void LaunchPointChanged(AppInfo app, string change)
        {
            if (launchPointWatchers.Count == 0) return;
            var raw = Jx.ObjOrEmpty(app.Raw.Stringify());
            launchPointWatchers.Send(new JObj().Put("id", app.Id).Put("version", app.Version).Put("appId", app.Id).Put("vendor", app.Vendor)
                .Put("vendorUrl", raw.Str("vendorurl")).Put("size", 0).Put("packageId", app.Id).Put("removable", app.Removable)
                .Put("launchPointId", app.Id + "_default").Put("title", app.Title).Put("appmenu", app.Title).Put("icon", app.Url(app.Icon))
                .Put("change", change).ToString());
        }

        private void LaunchPointChanged(LaunchPoint lp, string change)
        {
            if (launchPointWatchers.Count == 0) return;
            launchPointWatchers.Send(lp.ToJson(Apps.Get(lp.AppId)).Put("change", change).ToString());
        }

        /// addLaunchPoint: any app may add one for any installed app. id, title, icon and params
        /// are required; a relative icon is the app's own file, and file:// is taken off.
        private string AddLaunchPoint(JObj p)
        {
            Func<string, string> fail = t => new JObj().Put("returnValue", false).Put("errorText", t).ToString();
            if (!p.Has("id") || !p.Has("title") || !p.Has("icon") || !p.Has("params")) return fail("Invalid arguments");
            var id = p.Str("id");
            var app = Apps.Get(id);
            if (app == null) return fail("Unable to find id: " + id);
            var title = p.Str("title");
            var icon = p.Str("icon");
            if (icon.Contains("://")) icon = icon.Substring(icon.IndexOf("://", StringComparison.Ordinal) + 3);
            else if (!icon.StartsWith("/", StringComparison.Ordinal)) icon = "/" + AppServer.AppsPath + app.Id + "/" + icon;
            var v = p["params"];
            string params_ = v is JObj || v is JArr ? Jx.Stringify(v) : v == null || v is JNull ? null : Jx.Stringify(v is string ? v : Jx.Stringify(v));
            var appmenu = p.Has("appmenu") ? p.Str("appmenu") : title;
            var lp = shell.AddedLaunchPoints.Add(app, title, appmenu, icon, params_, !p.Has("removable") || p.Bool("removable"));
            if (lp == null) return new JObj().Put("returnValue", true).Put("errorText", "Failed to save launch point").ToString();
            shell.RefreshLauncher();
            LaunchPointChanged(lp, "added");
            return new JObj().Put("returnValue", true).Put("launchPointId", lp.LaunchPointId).ToString();
        }

        /// removeLaunchPoint, and the launcher's Delete on a shortcut. Null once removed.
        public string RemoveLaunchPoint(string id)
        {
            var lp = shell.AddedLaunchPoints.Get(id);
            var err = shell.AddedLaunchPoints.Remove(id);
            if (err != null) return err;
            if (lp != null) { shell.RefreshLauncher(); LaunchPointChanged(lp, "removed"); }
            return null;
        }

        /// An app may change only its own launch points' icons: the caller from the bus, never
        /// from the params (rule 10). Calendar draws today's date on its icon.
        private string UpdateLaunchPointIcon(string caller, JObj p)
        {
            var id = p.Str("launchPointId");
            if (id.Length == 0) return Bus.Error("Must provide launchPointId");
            if (!p.Has("icon")) return Bus.Error("Must provide icon path");
            var icon = p.Str("icon");
            if (icon.StartsWith("file://", StringComparison.Ordinal)) icon = icon.Substring(7);
            if (id.EndsWith("_default", StringComparison.Ordinal))
            {
                var app = Apps.Get(id.Substring(0, id.Length - 8));
                if (app == null) return Bus.Error("Unable to update launch point's icon");
                if (app.Id != caller) return Bus.Error("Attempted to change another application's launch point icon");
                var basePath = "/" + AppServer.AppsPath + app.Id + "/";
                if (icon.StartsWith(basePath, StringComparison.Ordinal)) icon = icon.Substring(basePath.Length);
                if (icon.StartsWith("/", StringComparison.Ordinal) || icon.Contains("..") || !File.Exists(Path.Combine(app.Dir, icon.Replace('/', '\\'))))
                    return Bus.Error("Unable to update launch point's icon");
                app.Icon = icon;
                shell.Evaluate("window.__lunacyShell&&__lunacyShell.setIcon(" + J.Str(app.Id) + "," + J.Str(app.Url(icon)) + ")");
                LaunchPointChanged(app, "updated");
                return Bus.Ok();
            }
            var lp = shell.AddedLaunchPoints.Get(id);
            if (lp == null) return Bus.Error("launchPointId \"" + id + "\" was not found");
            if (lp.AppId != caller) return Bus.Error("Attempted to change another application's launch point icon");
            var owner = Apps.Get(lp.AppId);
            if (!icon.Contains("://") && !icon.StartsWith("/", StringComparison.Ordinal) && owner != null) icon = "/" + AppServer.AppsPath + owner.Id + "/" + icon;
            var u = shell.AddedLaunchPoints.UpdateIcon(lp, owner, icon);
            shell.RefreshLauncher();
            LaunchPointChanged(u, "updated");
            return Bus.Ok();
        }
    }

    /// palm://com.palm.appInstallService (LunaCore's AppInstallService.kt): HP's installer for
    /// App Catalog, which hands it a package's URL and follows the install through `status`.
    /// Here it is Lunacy's package manager; packages install unsigned, as Preware's did.
    /// Private bus only, as measured on the reference TouchPad.
    internal sealed class AppInstallService
    {
        public const string Service = "com.palm.appInstallService";
        public const string Installer = "com.palm.appinstaller";
        private readonly LunaShell shell;
        private readonly Dictionary<string, JObj> apps = new Dictionary<string, JObj>();
        private readonly HashSet<string> active = new HashSet<string>();
        private readonly Subscribers watchers = new Subscribers();
        private readonly object gate = new object();
        private int tickets;

        public AppInstallService(LunaShell shell) { this.shell = shell; }

        public void Register(Bus bus)
        {
            foreach (var m in new[] { "install", "installLocal", "status", "cancel", "remove", "pause", "resume" })
            {
                var method = m;
                bus.Register(Service, method, c =>
                {
                    if (!shell.Privileged(c.AppId)) { c.Reply(Bus.Error("Service does not exist: " + Service + ".", -1)); return; }
                    switch (method)
                    {
                        case "install": Install(c, false); break;
                        case "installLocal": Install(c, true); break;
                        case "status": Status(c); break;
                        case "cancel": Cancel(c); break;
                        case "remove": Remove(c); break;
                        default: PauseOrResume(c, method); break;
                    }
                });
            }
            bus.Register(Installer, "installNoVerify", c => { if (Gate(c)) InstallNoVerify(c); });
            bus.Register(Installer, "remove", c => { if (Gate(c)) RemovePackage(c); });
            bus.Register(Installer, "queryInstallCapacity", c =>
            {
                if (!Gate(c)) return;
                var p = Jx.ObjOrEmpty(c.Raw);
                if (!p.Has("size")) { c.Reply(new JObj().Put("returnValue", false).Put("errorCode", "appinstaller_error").Put("errorText", "missing size parameter").ToString()); return; }
                long size = p.Long("size");
                long unpacked = p.Has("uncompressedSize") ? p.Long("uncompressedSize") : 2 * size;
                long needed = size + unpacked + 36;
                bool fits = needed * 1024 < Packages.FreeBytes();
                c.Reply(new JObj().Put("returnValue", true).Put("result", fits ? 0 : 3).Put("spaceNeededInKB", needed.ToString()).ToString());
            });
        }

        private bool Gate(Bus.Call c)
        {
            if (shell.Privileged(c.AppId)) return true;
            c.Reply(Bus.Error("Service does not exist: " + Installer + ".", -1));
            return false;
        }

        private static string Answer(bool ok, string reason = null, int code = -1)
        {
            var j = new JObj().Put("returnValue", ok);
            if (!ok) j.Put("errorCode", code).Put("reason", reason);
            return j.Put("subscribed", false).ToString();
        }

        private void Install(Bus.Call c, bool local)
        {
            var p = Jx.ObjOrEmpty(c.Raw);
            var required = local ? new[] { "id", "version", "ipkUrl" } : new[] { "id", "version", "ipkUrl", "authToken" };
            bool empty = p.Keys.Any(k => p[k] is string && ((string)p[k]).Length == 0);
            if (required.Any(k => p.Str(k).Length == 0) || empty) { c.Reply(Answer(false, "Bad parameter")); return; }
            var id = p.Str("id");
            JObj d;
            lock (gate)
            {
                // "duplicate install command while current install has not completed, ignoring"
                if (active.Contains(id)) { c.Reply(Answer(true)); return; }
                d = p.Copy();
                d.Remove("subscribe");
                d.Put("client", c.AppId).Put("icon", p.Str("iconUrl")).Put("progress", 0);
                apps[id] = d;
                active.Add(id);
            }
            c.Reply(Answer(true));
            var iconUrl = p.Str("iconUrl");
            if (local || iconUrl.Length == 0) { FetchPackage(id, d); return; }
            Post(id, d, "icon download current", 0);
            Task.Run(async () =>
            {
                bool ok = await Fetchable(iconUrl);
                if (!ok) { lock (gate) active.Remove(id); Post(id, d.Put("errorCode", -5).Put("reason", "Http error"), "download failed", 0); }
                else { Post(id, d, "icon download complete", 100); FetchPackage(id, d); }
            });
        }

        private static async Task<bool> Fetchable(string url)
        {
            try
            {
                using (var http = new Windows.Web.Http.HttpClient())
                {
                    var r = await http.GetAsync(new Uri(url));
                    return (int)r.StatusCode >= 200 && (int)r.StatusCode <= 299;
                }
            }
            catch (Exception) { return false; }
        }

        private void FetchPackage(string id, JObj d)
        {
            bool downloaded = false;
            int last = -1;
            shell.Install(d.Str("ipkUrl"), d.Str("client"), raw =>
            {
                if (downloaded) return;
                int pct = Math.Min(100, raw * 100 / 90);
                if (pct != last && (pct - last >= 5 || pct == 100)) { last = pct; Post(id, d, "ipk download current", pct); }
                if (raw >= 90) { downloaded = true; Post(id, d, "ipk download complete", 100); Post(id, d, "installing", 0); }
            }, true, r =>
            {
                lock (gate) active.Remove(id);
                if (r.Error == null) { Post(id, d, "installed", 100); lock (gate) apps.Remove(id); }
                else if (!downloaded) Post(id, d.Put("errorCode", -5).Put("reason", "Http error"), "download failed", 0);
                else Post(id, d.Put("reason", "FAILED_IPKG_INSTALL").Put("lunacyError", r.Error), "install failed", 0);
            });
        }

        private void Post(string id, JObj d, string state, int progress)
        {
            string update;
            lock (gate) { d.Put("state", state).Put("progress", progress); update = new JObj().Put("id", id).Put("details", d).ToString(); }
            watchers.Send(update);
        }

        private void Status(Bus.Call c)
        {
            var list = new JArr();
            lock (gate) foreach (var kv in apps) list.Add(new JObj().Put("id", kv.Key).Put("details", kv.Value.Copy()));
            var j = new JObj().Put("status", new JObj().Put("apps", list)).Put("returnValue", true);
            if (c.Subscribe) j.Put("subscribed", true);
            c.Reply(j.ToString());
            if (c.Subscribe && !c.Cancelled) watchers.Add(c);
        }

        private void Cancel(Bus.Call c)
        {
            var id = Jx.ObjOrEmpty(c.Raw).Str("id");
            if (id.Length == 0) { c.Reply(Answer(false, "No id specified")); return; }
            JObj d;
            lock (gate)
            {
                if (!apps.TryGetValue(id, out d)) { c.Reply(Answer(false, "unknown app id")); return; }
                // A download already handed to the package manager runs to its end.
                if (active.Contains(id)) { c.Reply(Answer(false, "Cannot cancel in current state")); return; }
                apps.Remove(id);
            }
            Post(id, d, "canceled", 0);
            c.Reply(Answer(true));
        }

        private void Remove(Bus.Call c)
        {
            var id = Jx.ObjOrEmpty(c.Raw).Str("id");
            if (id.Length == 0) { c.Reply(Answer(false, "No id specified")); return; }
            c.Reply(Answer(true));
            var app = shell.Apps.Get(id);
            if (app == null || !app.Removable) return;
            JObj d;
            lock (gate) { if (!apps.TryGetValue(id, out d)) d = new JObj().Put("id", id); }
            Post(id, d, "removing", 0);
            shell.RemoveApp(app, error =>
            {
                if (error == null) { lock (gate) apps.Remove(id); Post(id, d, "removed", 0); }
                else Post(id, d.Put("reason", error), "remove failed", 0);
            });
        }

        private void PauseOrResume(Bus.Call c, string m)
        {
            var id = Jx.ObjOrEmpty(c.Raw).Str("id");
            if (id.Length == 0) { c.Reply(Answer(false, "No id specified")); return; }
            bool known; lock (gate) known = apps.ContainsKey(id);
            if (!known) { c.Reply(new JObj().Put("returnValue", false).Put("errorCode", 0).Put("reason", "").ToString()); return; }
            c.Reply(Answer(false, m == "pause" ? "Pause failed" : "Resume failed"));
        }

        /// appinstaller/installNoVerify, which palm-install calls.
        private void InstallNoVerify(Bus.Call c)
        {
            var target = Jx.ObjOrEmpty(c.Raw).Str("target");
            if (target.Length == 0) { c.Reply(new JObj().Put("returnValue", false).Put("errorCode", "Failed to find param target in message").ToString()); return; }
            int ticket = Interlocked.Increment(ref tickets);
            c.Reply(new JObj().Put("returnValue", true).Put("ticket", ticket).Put("subscribed", c.Subscribe).ToString());
            Action<string> status = s => { if (c.Subscribe) c.Reply(new JObj().Put("ticket", ticket).Put("status", s).ToString()); };
            status("STARTING");
            if (!shell.Fs.Exists(target) || shell.Fs.IsDir(target)) { status("FAILED_PACKAGEFILE_NOT_FOUND"); status("FAILED_IPKG_INSTALL"); return; }
            status("CREATE_TMP"); status("VERIFYING"); status("IPKG_INSTALL");
            shell.Install(target, c.AppId, null, false, r => status(r.Error == null ? "SUCCESS" : "FAILED_IPKG_INSTALL"));
        }

        /// appinstaller/remove, palm-install -r's.
        private void RemovePackage(Bus.Call c)
        {
            var id = Jx.ObjOrEmpty(c.Raw).Str("packageName");
            var app = id.Length == 0 ? null : shell.Apps.Get(id);
            if (app == null || !app.Removable) { c.Reply(new JObj().Put("returnValue", false).ToString()); return; }
            int ticket = Interlocked.Increment(ref tickets);
            c.Reply(new JObj().Put("ticket", ticket).Put("returnValue", true).Put("version", app.Version).Put("subscribed", c.Subscribe).ToString());
            Action<string> status = s => { if (c.Subscribe) c.Reply(new JObj().Put("ticket", ticket).Put("status", s).ToString()); };
            status("IPKG_REMOVE");
            shell.RemoveApp(app, error => status(error == null ? "SUCCESS" : "FAILED_IPKG_REMOVE"));
        }
    }
}
