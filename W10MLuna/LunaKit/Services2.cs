using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Media;
using Windows.Networking;
using Windows.Networking.Sockets;
using Windows.Storage;
using Windows.Storage.Streams;

namespace LunaKit
{
    // The rest of the native services webOS apps call, each a port of AndroidLuna's (named on
    // each class), answering in the reference TouchPad's shapes, backed by Windows where the
    // TouchPad had hardware or a system service.

    /// palm://com.palm.power (LunaCore's PowerService.kt): timeout/set and timeout/clear, the
    /// wake timers apps and services schedule a call with ("in" HH:MM:SS or "at" MM/dd/yyyy
    /// HH:mm:ss UTC), kept across starts; activityStart and activityEnd, which hold the device
    /// up for a while. A timeout fires by calling its uri with its params, as the power daemon
    /// did. While Lunacy runs a timer fires it on time; while it is away, the background tasks
    /// (Background.cs) wake it within 15 minutes, and a start catches up.
    internal sealed class PowerService
    {
        public const string Service = "com.palm.power";
        private const long GraceMs = 10000;
        private static readonly Regex In = new Regex("^(\\d{2}):([0-5]\\d):([0-5]\\d)$");
        private static readonly HashSet<string> Params = new HashSet<string> { "key", "in", "at", "wakeup", "uri", "params", "keep_existing", "activity_id", "activity_duration_ms" };

        private sealed class Timeout { public string Owner, Key, Uri; public long At, HoldMs; public bool Wake; public object Params; }

        private readonly LunaShell shell;
        private readonly string store;
        private readonly Dictionary<string, Timeout> timeouts = new Dictionary<string, Timeout>();
        private readonly object gate = new object();
        private Timer timer;
        private bool enabled;
        private Bus bus;

        public PowerService(LunaShell shell)
        {
            this.shell = shell;
            store = Path.Combine(ApplicationData.Current.LocalFolder.Path, "power.json");
            try
            {
                if (File.Exists(store))
                    foreach (var kv in Jx.ParseObj(File.ReadAllText(store)))
                    {
                        var o = kv.Value as JObj;
                        if (o == null) continue;
                        timeouts[kv.Key] = new Timeout { Owner = o.Str("owner"), Key = o.Str("key"), At = o.Long("at"), Wake = o.Bool("wake"), Uri = o.Str("uri"), Params = o["params"], HoldMs = o.Has("holdMs") ? o.Long("holdMs") : GraceMs };
                    }
            }
            catch (Exception e) { LunaLog.Write("[power] timeouts unreadable: " + e.Message); }
        }

        public void Register(Bus bus)
        {
            this.bus = bus;
            bus.Register(Service, "timeout/set", Set);
            bus.Register(Service, "timeout/clear", Clear);
            bus.Register(Service, "com/palm/power/activityStart", c =>
            {
                var p = Jx.ObjOrEmpty(c.Raw);
                var id = p["id"] as string;
                if (string.IsNullOrEmpty(id) || !Jx.IsNumber(p["duration_ms"]) || Jx.Num(p["duration_ms"]) < 0) { c.Reply(Failure("Malformed json.")); return; }
                Background.Hold((long)Jx.Num(p["duration_ms"]));
                c.Reply(Bus.Ok());
            });
            bus.Register(Service, "com/palm/power/activityEnd", c =>
            {
                var id = Jx.ObjOrEmpty(c.Raw)["id"] as string;
                c.Reply(string.IsNullOrEmpty(id) ? Failure("Malformed json.") : Bus.Ok());
            });
        }

        /// Timeouts fire from now on: after the services are on the bus, as webOS's waited for boot.
        public void Enable() { lock (gate) enabled = true; Due(); }

        private static string Failure(string text) { return new JObj().Put("returnValue", false).Put("errorText", text).ToString(); }

        private static long Now() { return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }

        private void Set(Bus.Call c)
        {
            var p = Jx.ObjOrEmpty(c.Raw);
            var key = p["key"] as string;
            var uri = p["uri"] as string;
            var inText = p["in"] as string;
            var atText = p["at"] as string;
            long now = Now();
            long? at = null;
            if (inText != null && atText == null)
            {
                var m = In.Match(inText);
                if (m.Success && int.Parse(m.Groups[1].Value) < 24)
                    at = now + (long.Parse(m.Groups[1].Value) * 3600 + long.Parse(m.Groups[2].Value) * 60 + long.Parse(m.Groups[3].Value)) * 1000;
            }
            else if (atText != null && inText == null)
            {
                DateTime d;
                if (DateTime.TryParseExact(atText, "MM/dd/yyyy HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out d))
                    at = new DateTimeOffset(d, TimeSpan.Zero).ToUnixTimeMilliseconds();
            }
            var duration = p["activity_duration_ms"];
            bool validKeys = p.Keys.All(k => Params.Contains(k) || k.StartsWith("$", StringComparison.Ordinal)) && (duration == null || p.Has("activity_id"));
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(uri) || !p.Has("params") || at == null || !validKeys
                || (p.Has("wakeup") && !(p["wakeup"] is bool)) || (p.Has("keep_existing") && !(p["keep_existing"] is bool)))
            {
                c.Reply(Failure("Invalid format for 'timeout/set'."));
                return;
            }
            var id = c.AppId + "\u0000" + key;
            lock (gate)
            {
                if (p.Bool("keep_existing") && timeouts.ContainsKey(id))
                {
                    c.Reply(new JObj().Put("returnValue", true).Put("key", key).Put("kept_existing", true).ToString());
                    return;
                }
                long hold = Jx.IsNumber(duration) ? Math.Max(0, Math.Min(900000, (long)Jx.Num(duration))) : GraceMs;
                timeouts[id] = new Timeout { Owner = c.AppId, Key = key, At = at.Value, Wake = p.Bool("wakeup"), Uri = uri, Params = Jx.Clone(p["params"]), HoldMs = hold };
                Save();
            }
            c.Reply(new JObj().Put("returnValue", true).Put("key", key).ToString());
            Due();
        }

        private void Clear(Bus.Call c)
        {
            var key = Jx.ObjOrEmpty(c.Raw)["key"] as string;
            if (key == null) { c.Reply(Failure("Invalid parameters.")); return; }
            lock (gate) { if (timeouts.Remove(c.AppId + "\u0000" + key)) Save(); }
            c.Reply(new JObj().Put("returnValue", true).Put("key", key).ToString());
            Due();
        }

        /// Fires whatever has come due, and sets the timer for the next.
        public void Due()
        {
            List<Timeout> fired;
            long next;
            lock (gate)
            {
                if (!enabled) return;
                long now = Now();
                fired = timeouts.Where(kv => kv.Value.At <= now).Select(kv => kv.Value).ToList();
                foreach (var kv in timeouts.Where(kv => kv.Value.At <= now).ToList()) timeouts.Remove(kv.Key);
                if (fired.Count > 0) Save();
                next = timeouts.Count == 0 ? -1 : timeouts.Values.Min(t => t.At);
                if (timer != null) { timer.Dispose(); timer = null; }
                if (next >= 0)
                {
                    long wait = Math.Max(0, Math.Min(int.MaxValue, next - now));
                    timer = new Timer(_ => Due(), null, (int)wait, Timeout2.Infinite);
                }
            }
            foreach (var t in fired)
            {
                long late = Now() - t.At;
                LunaLog.Write("[power] timeout " + t.Key + " of " + t.Owner + " fired" + (late > 60000 ? ", " + late / 1000 + " s late" : ""));
                Background.Hold(t.HoldMs);
                var payload = t.Params is JObj ? t.Params.ToString() : "{}";
                bus.DispatchPrivate(Service, t.Uri, payload, r => { if (!Jx.ObjOrEmpty(r).Bool("returnValue")) LunaLog.Write("[power] timeout " + t.Key + ": " + r); });
            }
        }

        private static class Timeout2 { public const int Infinite = -1; }

        private void Save()
        {
            var o = new JObj();
            foreach (var kv in timeouts)
                o.Put(kv.Key, new JObj().Put("owner", kv.Value.Owner).Put("key", kv.Value.Key).Put("at", kv.Value.At).Put("wake", kv.Value.Wake)
                    .Put("uri", kv.Value.Uri).Put("params", kv.Value.Params ?? new JObj()).Put("holdMs", kv.Value.HoldMs));
            try { File.WriteAllText(store + ".new", o.ToString()); if (File.Exists(store)) File.Delete(store); File.Move(store + ".new", store); }
            catch (Exception e) { LunaLog.Write("[power] can't save timeouts: " + e.Message); }
        }
    }

    /// palm://com.palm.keymanager (AndroidLuna's KeyManager.kt): each app's own named keys,
    /// stored, fetched (only those stored with nohide), described and removed. Kept in a file of
    /// Lunacy's own, as webOS kept them in its key store.
    internal sealed class KeyManager
    {
        private const string Service = "com.palm.keymanager";
        private readonly string store;
        private readonly JObj keys;

        public KeyManager(string store)
        {
            this.store = store;
            try { keys = File.Exists(store) ? Jx.ParseObj(File.ReadAllText(store)) : new JObj(); } catch (Exception) { keys = new JObj(); }
        }

        public void Register(Bus bus)
        {
            bus.Register(Service, "store", c => c.Reply(StoreKey(c.AppId, Jx.ObjOrEmpty(c.Raw))));
            bus.Register(Service, "fetchKey", c => c.Reply(Fetch(c.AppId, Jx.ObjOrEmpty(c.Raw))));
            bus.Register(Service, "keyInfo", c => c.Reply(Info(c.AppId, Jx.ObjOrEmpty(c.Raw))));
            bus.Register(Service, "remove", c => c.Reply(Remove(c.AppId, Jx.ObjOrEmpty(c.Raw))));
        }

        private static string MissingName() { return new JObj().Put("errorCode", -986).Put("errorText", "required prop not found: 'keyname'").Put("returnValue", false).ToString(); }
        private static string Failed(string text) { return new JObj().Put("errorText", text).Put("returnValue", false).ToString(); }

        private string StoreKey(string owner, JObj p)
        {
            if (!p.Has("keyname")) return MissingName();
            var name = p.Str("keyname");
            lock (keys)
            {
                var mine = keys.Obj(owner);
                if (mine == null) { mine = new JObj(); keys.Put(owner, mine); }
                if (mine.Has(name)) return Failed("key exists");
                mine.Put(name, new JObj().Put("keydata", p.Str("keydata")).Put("type", p.Has("type") ? p.Str("type") : "ASCIIBLOB")
                    .Put("nohide", p.Bool("nohide")).Put("backup", p.Bool("backup")).Put("cloud", p.Bool("cloud")).Put("noexport", p.Bool("noexport")));
                Save();
            }
            return Bus.Ok();
        }

        private string Fetch(string owner, JObj p)
        {
            if (!p.Has("keyname")) return MissingName();
            var name = p.Str("keyname");
            lock (keys)
            {
                var k = keys.Obj(owner) == null ? null : keys.Obj(owner).Obj(name);
                if (k == null) return Failed("Key does not exist");
                if (!k.Bool("nohide")) return Failed("key is not exportable");
                return Describe(k, name).Put("keydata", k.Str("keydata")).ToString();
            }
        }

        private string Info(string owner, JObj p)
        {
            if (!p.Has("keyname")) return MissingName();
            lock (keys)
            {
                var k = keys.Obj(owner) == null ? null : keys.Obj(owner).Obj(p.Str("keyname"));
                // The device names the owner where the key's name belongs here (KeyManager.kt).
                return k == null ? new JObj().Put("returnValue", false).ToString() : Describe(k, owner).ToString();
            }
        }

        private string Remove(string owner, JObj p)
        {
            if (!p.Has("keyname")) return MissingName();
            lock (keys)
            {
                var mine = keys.Obj(owner);
                if (mine == null || !mine.Has(p.Str("keyname"))) return Failed("unknown key");
                mine.Remove(p.Str("keyname"));
                Save();
            }
            return Bus.Ok();
        }

        private static JObj Describe(JObj k, string keyname)
        {
            return new JObj().Put("backup", k.Bool("backup")).Put("cloud", k.Bool("cloud")).Put("keyname", keyname)
                .Put("noexport", k.Bool("noexport")).Put("nohide", k.Bool("nohide")).Put("returnValue", true)
                .Put("shared", k.Bool("nohide")).Put("type", k.Str("type")).Put("unwrap_only", false);
        }

        private void Save()
        {
            try { File.WriteAllText(store + ".tmp", keys.ToString()); if (File.Exists(store)) File.Delete(store); File.Move(store + ".tmp", store); }
            catch (Exception e) { LunaLog.Write("[keymanager] can't save: " + e.Message); }
        }
    }

    /// palm://com.palm.service.mediabroadcast (AndroidLuna's MediaBroadcast.kt): a player
    /// registers, then reports its song and pause state, which go to Windows' own transport
    /// controls (the volume flyout and lock screen), as Android's went to its media session.
    internal sealed class MediaBroadcast
    {
        private const string Service = "com.palm.service.mediabroadcast";
        private readonly Dictionary<string, Bus.Call> broadcasters = new Dictionary<string, Bus.Call>();
        private string title = "", artist = "", album = "";
        private bool playing;

        public void Register(Bus bus)
        {
            bus.Register(Service, "registerBroadcaster", c =>
            {
                c.Reply(new JObj().Put("registration", "complete").Put("returnValue", true).ToString());
                if (!c.Subscribe || c.Cancelled) return;
                lock (broadcasters) broadcasters[c.AppId] = c;
                c.OnCancel(() =>
                {
                    bool last;
                    lock (broadcasters) { last = broadcasters.ContainsKey(c.AppId) && broadcasters[c.AppId] == c; if (last) broadcasters.Remove(c.AppId); }
                    if (last) End();
                });
            });
            bus.Register(Service, "update", c =>
            {
                bool known;
                lock (broadcasters) known = broadcasters.ContainsKey(c.AppId);
                if (!known) { c.Reply("\"broadcasterID required\""); return; }
                Update(Jx.ObjOrEmpty(c.Raw));
                c.Reply(Bus.Ok());
            });
        }

        private void Update(JObj p)
        {
            var song = p.Obj("song");
            switch (p.Str("event"))
            {
                case "SONG_CHANGED":
                    if (song == null) return;
                    title = song.Str("title"); artist = song.Str("artist"); album = song.Str("album");
                    break;
                case "PAUSE_MODE_CHANGED":
                    playing = song != null && song.Str("pause") == "OFF";
                    break;
                default: return;
            }
            Show();
        }

        private void Show()
        {
            Ui(() =>
            {
                try
                {
                    var smtc = SystemMediaTransportControls.GetForCurrentView();
                    smtc.IsEnabled = true;
                    smtc.PlaybackStatus = playing ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
                    var d = smtc.DisplayUpdater;
                    d.Type = MediaPlaybackType.Music;
                    d.MusicProperties.Title = title;
                    d.MusicProperties.Artist = artist;
                    d.MusicProperties.AlbumTitle = album;
                    d.Update();
                }
                catch (Exception e) { LunaLog.Write("[mediabroadcast] " + e.Message); }
            });
        }

        private void End()
        {
            title = artist = album = ""; playing = false;
            Ui(() =>
            {
                try
                {
                    var smtc = SystemMediaTransportControls.GetForCurrentView();
                    smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
                    smtc.DisplayUpdater.ClearAll();
                    smtc.DisplayUpdater.Update();
                }
                catch (Exception) { }
            });
        }

        private static void Ui(Action a)
        {
            var d = Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher;
            if (d.HasThreadAccess) a(); else { var _ = d.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => a()); }
        }
    }

    /// palm://com.palm.nettools/findMxRecords (AndroidLuna's NetTools.kt): a domain's mail
    /// servers, by a DNS MX query of Lunacy's own. Windows gives an app no list of its DNS
    /// servers, so the query goes to public resolvers (Google's, then Cloudflare's).
    internal sealed class NetTools
    {
        private const string Service = "com.palm.nettools";
        private static readonly string[] Resolvers = { "8.8.8.8", "1.1.1.1" };

        public void Register(Bus bus)
        {
            bus.Register(Service, "findMxRecords", c =>
            {
                var domain = (Jx.ObjOrEmpty(c.Raw).Str("domainName") ?? "").Trim().TrimEnd('.');
                if (domain.Length == 0 || domain.Length > 253 || domain.Split('.').Any(l => l.Length == 0 || l.Length > 63)) { c.Reply(Error(8, "Error encoding domain name")); return; }
                Task.Run(async () =>
                {
                    string r;
                    try { r = await Lookup(domain); } catch (Exception) { r = Error(4, "The domain name provided was not found"); }
                    c.Reply(r);
                });
            });
        }

        private static string Error(int code, string text) { return new JObj().Put("returnValue", false).Put("errorCode", code).Put("errorText", text).ToString(); }

        private static async Task<string> Lookup(string domain)
        {
            Exception last = null;
            foreach (var server in Resolvers)
            {
                try
                {
                    var r = await Query(server, domain);
                    if (r.Item1 == 3) return Error(4, "The domain name provided was not found");
                    if (r.Item1 != 0) throw new InvalidOperationException("rcode " + r.Item1);
                    if (r.Item2.Count == 0) return Error(1, "Domain name exists but there were no mx records returned");
                    var list = new JArr();
                    foreach (var mx in r.Item2) list.Add(new JObj().Put("mxServer", mx.Key).Put("mxPreference", mx.Value));
                    return new JObj().Put("returnValue", true).Put("mxRecords", list).ToString();
                }
                catch (Exception e) { last = e; }
            }
            throw last ?? new InvalidOperationException("no resolver");
        }

        private static async Task<Tuple<int, List<KeyValuePair<string, int>>>> Query(string server, string domain)
        {
            int id = new Random().Next(65535);
            var q = new List<byte> { (byte)(id >> 8), (byte)id, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
            foreach (var label in domain.Split('.')) { var b = Encoding.ASCII.GetBytes(label); q.Add((byte)b.Length); q.AddRange(b); }
            q.AddRange(new byte[] { 0, 0, 15, 0, 1 });
            var got = new TaskCompletionSource<byte[]>();
            using (var s = new DatagramSocket())
            {
                s.MessageReceived += (sock, e) =>
                {
                    try
                    {
                        var reader = e.GetDataReader();
                        var d = new byte[reader.UnconsumedBufferLength];
                        reader.ReadBytes(d);
                        if (d.Length >= 12 && ((d[0] << 8) | d[1]) == id) got.TrySetResult(d);
                    }
                    catch (Exception x) { got.TrySetException(x); }
                };
                await s.ConnectAsync(new HostName(server), "53");
                var w = new DataWriter(s.OutputStream);
                w.WriteBytes(q.ToArray());
                await w.StoreAsync();
                var done = await Task.WhenAny(got.Task, Task.Delay(4000));
                if (done != got.Task) throw new TimeoutException();
                return Parse(got.Task.Result);
            }
        }

        private static Tuple<int, List<KeyValuePair<string, int>>> Parse(byte[] d)
        {
            Func<int, int> u16 = i => (d[i] << 8) | d[i + 1];
            int rcode = d[3] & 0x0f, qd = u16(4), an = u16(6), at = 12;
            for (int k = 0; k < qd; k++) at = SkipName(d, at) + 4;
            var out_ = new List<KeyValuePair<string, int>>();
            for (int k = 0; k < an; k++)
            {
                at = SkipName(d, at);
                int type = u16(at), len = u16(at + 8), rdata = at + 10;
                if (type == 15) out_.Add(new KeyValuePair<string, int>(Name(d, rdata + 2), u16(rdata)));
                at = rdata + len;
            }
            return Tuple.Create(rcode, out_);
        }

        private static int SkipName(byte[] d, int i)
        {
            while (true)
            {
                int len = d[i];
                if (len == 0) return i + 1;
                if ((len & 0xc0) == 0xc0) return i + 2;
                i += len + 1;
            }
        }

        private static string Name(byte[] d, int i)
        {
            var parts = new List<string>();
            for (int jumps = 0; jumps < 20;)
            {
                int len = d[i];
                if (len == 0) break;
                if ((len & 0xc0) == 0xc0) { i = ((len & 0x3f) << 8) | d[i + 1]; jumps++; continue; }
                parts.Add(Encoding.ASCII.GetString(d, i + 1, len));
                i += len + 1;
            }
            return string.Join(".", parts);
        }
    }

    /// applicationManager's exhibition (dock) mode launch points (AndroidLuna's DockMode.kt):
    /// the apps that declare dockMode or exhibitionMode, each switched on or off for
    /// Exhibition's list, as the TouchPad's shape gives them.
    internal sealed class DockMode
    {
        private const string Service = "com.palm.applicationManager";
        private readonly AppRegistry apps;
        private readonly ApplicationDataContainer prefs = ApplicationData.Current.LocalSettings.CreateContainer("dockmode", ApplicationDataCreateDisposition.Always);
        private readonly Subscribers subscribers = new Subscribers();
        private string lastIds;

        public DockMode(AppRegistry apps) { this.apps = apps; }

        public void Register(Bus bus)
        {
            bus.Register(Service, "listDockModeLaunchPoints", c =>
            {
                lastIds = ExhibitionIds();
                c.Reply(LaunchPoints());
                if (c.Subscribe && !c.Cancelled) subscribers.Add(c);
            });
            bus.Register(Service, "addDockModeLaunchPoint", c => c.Reply(SetEnabled(Jx.ObjOrEmpty(c.Raw), true)));
            bus.Register(Service, "removeDockModeLaunchPoint", c => c.Reply(SetEnabled(Jx.ObjOrEmpty(c.Raw), false)));
        }

        private static bool Declared(AppInfo a)
        {
            var r = Jx.ObjOrEmpty(a.Raw.Stringify());
            return r.Bool("dockMode") || r.Bool("exhibitionMode");
        }

        private string LaunchPoints()
        {
            var points = new JArr();
            foreach (var a in apps.All())
            {
                if (!Declared(a)) continue;
                var info = Jx.ObjOrEmpty(a.Raw.Stringify());
                var opts = info.Obj("exhibitionModeOptions");
                var title = opts != null && opts.Str("title").Length > 0 ? opts.Str("title") : info.Str("dockModeTitle").Length > 0 ? info.Str("dockModeTitle") : a.Title;
                points.Add(new JObj().Put("id", a.Id).Put("version", a.Version ?? "").Put("appId", a.Id)
                    .Put("vendor", info.Str("vendor")).Put("vendorUrl", info.Str("vendorurl")).Put("size", 0).Put("packageId", a.Id)
                    .Put("exhibitionMode", true).Put("exhibitionModeTitle", title).Put("removable", a.Removable)
                    .Put("launchPointId", a.Id + "_default").Put("title", a.Title).Put("appmenu", info.Has("appmenu") ? info.Str("appmenu") : a.Title)
                    .Put("icon", a.Url(a.Icon)).Put("enabled", IsEnabled(a.Id)));
            }
            return new JObj().Put("returnValue", true).Put("launchPoints", points).ToString();
        }

        /// The app list changed: subscribers hear it if the exhibition apps did.
        public void LaunchPointsChanged()
        {
            var ids = ExhibitionIds();
            if (ids == lastIds) return;
            lastIds = ids;
            subscribers.Send(LaunchPoints());
        }

        private string ExhibitionIds() { return string.Join(",", apps.All().Where(Declared).Select(a => a.Id).OrderBy(x => x, StringComparer.Ordinal)); }

        /// What the Exhibition menu calls an app: its exhibitionModeOptions title, dockModeTitle, or title.
        public string Title(string appId)
        {
            var a = apps.Get(appId);
            if (a == null) return "Time";
            var info = Jx.ObjOrEmpty(a.Raw.Stringify());
            var opts = info.Obj("exhibitionModeOptions");
            if (opts != null && opts.Str("title").Length > 0) return opts.Str("title");
            return info.Str("dockModeTitle").Length > 0 ? info.Str("dockModeTitle") : a.Title;
        }

        public bool IsEnabled(string appId) { object v; return prefs.Values.TryGetValue(appId, out v) && v is bool && (bool)v; }

        private string SetEnabled(JObj p, bool on)
        {
            var appId = p.Str("appId");
            if (appId.Length == 0) return Bus.Error("appId is required");
            if (apps.Get(appId) == null) return Bus.Error("Application not found: " + appId);
            prefs.Values[appId] = on;
            return Bus.Ok();
        }
    }

    /// systemservice's ringtone/addRingtone and deleteRingtone (AndroidLuna's Ringtones.kt): a
    /// picked sound copied into /media/internal/ringtones, and one removed from there. Windows 10
    /// Mobile gives an app no way into the phone's own ringtones, so they stay in Lunacy's tree.
    internal sealed class Ringtones
    {
        private const string Dir = "/media/internal/ringtones";
        private readonly WebosFs fs;
        public Ringtones(WebosFs fs) { this.fs = fs; }

        public void Register(Bus bus)
        {
            bus.Register("com.palm.systemservice", "ringtone/addRingtone", c => Off(c, () => Add(Jx.ObjOrEmpty(c.Raw))));
            bus.Register("com.palm.systemservice", "ringtone/deleteRingtone", c => Off(c, () => Delete(Jx.ObjOrEmpty(c.Raw))));
        }

        private static void Off(Bus.Call c, Func<string> work)
        {
            Task.Run(() =>
            {
                string r;
                try { r = work(); } catch (Exception e) { LunaLog.Write("[ringtone] " + e.Message); r = Fail("Unable to add ringtone."); }
                c.Reply(r);
            });
        }

        private static string Fail(string text) { return new JObj().Put("returnValue", false).Put("errorText", text).ToString(); }

        private string Add(JObj p)
        {
            var path = p.Str("filePath");
            if (path.Length == 0) return Fail("source file missing");
            if (path.Contains("://") && !path.StartsWith("file://", StringComparison.Ordinal))
                return Fail("input file specification doesn't support non-local files (use file:///path/file or /path/file format");
            path = path.StartsWith("file://", StringComparison.Ordinal) ? path.Substring(7) : path;
            if (!path.StartsWith("/", StringComparison.Ordinal) || !fs.Exists(path) || fs.IsDir(path)) return Fail("source file doesn't exist");
            var name = WebosFs.Normal(path).Split('/').Last();
            if (name.Length == 0) return Fail("source file name missing.");
            var dest = Dir + "/" + name;
            if (WebosFs.Normal(path) != dest)
            {
                if (!fs.Exists(Dir)) fs.MakeDir(Dir, true);
                fs.Write(dest, fs.Read(path), false);
            }
            return Bus.Ok();
        }

        private string Delete(JObj p)
        {
            var path = p.Str("filePath");
            if (path.Length == 0) return Fail("filePath is missing");
            path = path.StartsWith("file://", StringComparison.Ordinal) ? path.Substring(7) : path;
            if (!fs.Exists(path) || fs.IsDir(path)) return Fail("file doesn't exist");
            var n = WebosFs.Normal(path);
            if (n.Substring(0, n.LastIndexOf('/')) != Dir) return Fail("Unable to delete.");
            try { fs.Remove(n, false, false); } catch (WebosFs.FsError) { return Fail("Unable to delete ringtone."); }
            return Bus.Ok();
        }
    }

    /// palm://com.palm.update (AndroidLuna's UpdateService.kt), for the Updates app: whether a
    /// newer W10MLuna has been released, checked daily (the dailyUpdateCheck activity) and on
    /// request. Download Now opens the release page; Lunacy doesn't download or install itself.
    internal sealed class UpdateService
    {
        private const string Service = "com.palm.update";
        /// W10MLuna's releases: the fork's, tagged w10m-*.
        private const string Releases = "https://api.github.com/repos/Starkka15/Lunacy/releases?per_page=20";
        private const string TagPrefix = "w10m-";
        private const long DayMs = 24L * 3600 * 1000;
        private readonly LunaShell shell;
        private readonly ApplicationDataContainer prefs = ApplicationData.Current.LocalSettings.CreateContainer("updates", ApplicationDataCreateDisposition.Always);
        private readonly object gate = new object();
        private JObj status = new JObj().Put("status", "UpToDate").Put("networkAvailable", true);
        private readonly Subscribers watchers = new Subscribers();
        private readonly List<Bus.Call> checkReplies = new List<Bus.Call>();
        private bool checking;
        private string releasePage;
        private Bus bus;

        public UpdateService(LunaShell shell) { this.shell = shell; }

        private static string Version
        {
            get { var v = Package.Current.Id.Version; return v.Major + "." + v.Minor + "." + v.Build + "." + v.Revision; }
        }

        public void Register(Bus bus)
        {
            this.bus = bus;
            bus.Register(Service, "GetStatus", GetStatus);
            bus.Register(Service, "GetStatusApp", GetStatus);
            bus.Register(Service, "CheckForUpdate", c => { lock (gate) checkReplies.Add(c); Check(false); });
            bus.Register(Service, "TaskCheck", TaskCheck);
            bus.Register(Service, "DownloadNow", c => shell.Ui(async () =>
            {
                var page = releasePage;
                if (page == null) { c.Reply(Bus.Error("No update is waiting", -1)); return; }
                bool ok = false;
                try { ok = await Windows.System.Launcher.LaunchUriAsync(new Uri(page)); } catch (Exception) { }
                c.Reply(ok ? Bus.Ok() : Bus.Error("Couldn't open the release page", -1));
            }));
            foreach (var m in new[] { "DismissedUpdate", "InstallLater", "AlertDisplayed" }) bus.Register(Service, m, c => c.Reply(Bus.Ok()));
            foreach (var m in new[] { "InstallNow", "CancelDownload" })
                bus.Register(Service, m, c => c.Reply(Bus.Error("Lunacy doesn't download or install its updates: Download Now opens the release page", -1)));
            ScheduleDaily();
            object last;
            long lastCheck = prefs.Values.TryGetValue("lastCheck", out last) && last is long ? (long)last : 0;
            if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - lastCheck > DayMs) Task.Delay(120000).ContinueWith(_ => Check(true));
        }

        private void GetStatus(Bus.Call c)
        {
            lock (gate) c.Reply(status.Copy().Put("returnValue", true).ToString());
            if (c.Subscribe && !c.Cancelled) watchers.Add(c);
        }

        private void SetStatus(JObj s)
        {
            lock (gate) status = s;
            watchers.Send(s.Copy().Put("returnValue", true).ToString());
        }

        private void AnswerChecks()
        {
            List<Bus.Call> list;
            string out_;
            lock (gate) { list = checkReplies.ToList(); checkReplies.Clear(); out_ = status.Copy().Put("returnValue", true).ToString(); }
            foreach (var c in list) c.Reply(out_);
        }

        private void ScheduleDaily()
        {
            var activity = new JObj().Put("name", "dailyUpdateCheck").Put("description", "Daily Update Check")
                .Put("type", new JObj().Put("foreground", true).Put("persist", true))
                .Put("schedule", new JObj().Put("interval", "1d"))
                .Put("requirements", new JObj().Put("internet", true))
                .Put("callback", new JObj().Put("method", "palm://" + Service + "/TaskCheck"));
            bus.DispatchPrivate(Service, "palm://com.palm.activitymanager/create", new JObj().Put("activity", activity).Put("start", true).Put("replace", true).ToString(), _ => { });
        }

        private void TaskCheck(Bus.Call c)
        {
            c.Reply(Bus.Ok());
            Check(true);
            var act = Jx.ObjOrEmpty(c.Raw).Obj("$activity");
            if (act != null && act.Has("activityId"))
                bus.DispatchPrivate(Service, "palm://com.palm.activitymanager/complete", new JObj().Put("activityId", act["activityId"]).Put("restart", true).ToString(), _ => { });
        }

        private void Check(bool background)
        {
            lock (gate)
            {
                if (checking) { if (!background) SetStatus(new JObj().Put("status", "Checking").Put("networkAvailable", true)); return; }
                checking = true;
            }
            if (!background) SetStatus(new JObj().Put("status", "Checking").Put("networkAvailable", true));
            Task.Run(async () =>
            {
                JObj found = null;
                try { found = await Newest(); }
                catch (Exception e) { LunaLog.Write("[update] check: " + e.GetType().Name + ": " + e.Message); }
                bool replies;
                lock (gate) { checking = false; replies = checkReplies.Count > 0; }
                if (found == null)
                {
                    if (!background || replies) SetStatus(new JObj().Put("status", "NetworkFailed").Put("networkAvailable", true));
                    AnswerChecks();
                    return;
                }
                prefs.Values["lastCheck"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var tag = found.Str("tag_name");
                var version = tag.StartsWith(TagPrefix, StringComparison.Ordinal) ? tag.Substring(TagPrefix.Length) : tag;
                if (Newer(version, Version))
                {
                    releasePage = found.Str("html_url");
                    var asset = (found.Arr("assets") ?? new JArr()).OfType<JObj>().FirstOrDefault(a => a.Str("name").EndsWith(".appx", StringComparison.OrdinalIgnoreCase) || a.Str("name").EndsWith(".appxbundle", StringComparison.OrdinalIgnoreCase));
                    long mb = asset == null ? 0 : (long)Math.Round(asset.Long("size") / 1048576.0);
                    SetStatus(new JObj().Put("status", "Waiting").Put("version", "Lunacy " + version).Put("size", mb).Put("networkAvailable", true)
                        .Put("lowSpeed", false).Put("releasePage", releasePage).Put("releaseNotes", PlainNotes(found.Str("body"))));
                    object announced;
                    if (background && !(prefs.Values.TryGetValue("announced", out announced) && (announced as string) == tag))
                    {
                        prefs.Values["announced"] = tag;
                        shell.Toast("Lunacy " + version + " is available", "Open Updates to see what's new.");
                    }
                }
                else
                {
                    releasePage = null;
                    SetStatus(new JObj().Put("status", "UpToDate").Put("networkAvailable", true));
                }
                AnswerChecks();
            });
        }

        private static async Task<JObj> Newest()
        {
            using (var http = new Windows.Web.Http.HttpClient())
            {
                var req = new Windows.Web.Http.HttpRequestMessage(Windows.Web.Http.HttpMethod.Get, new Uri(Releases));
                req.Headers.TryAppendWithoutValidation("Accept", "application/vnd.github+json");
                req.Headers.TryAppendWithoutValidation("User-Agent", "Lunacy/" + Version);
                var res = await http.SendRequestAsync(req);
                if ((int)res.StatusCode != 200) throw new InvalidOperationException("HTTP " + (int)res.StatusCode);
                var list = Jx.Parse(await res.Content.ReadAsStringAsync()) as JArr ?? new JArr();
                foreach (var r in list.OfType<JObj>())
                    if (!r.Bool("draft") && r.Str("tag_name").StartsWith(TagPrefix, StringComparison.Ordinal)) return r;
                throw new InvalidOperationException("no W10MLuna releases");
            }
        }

        public static bool Newer(string tag, string current)
        {
            Func<string, List<int>> parts = v => v.Trim().TrimStart('v').Split('.', '-', ' ')
                .Select(p => { int n; return int.TryParse(new string(p.TakeWhile(char.IsDigit).ToArray()), out n) ? n : 0; }).ToList();
            var a = parts(tag); var b = parts(current);
            for (int i = 0; i < Math.Max(a.Count, b.Count); i++)
            {
                int x = i < a.Count ? a[i] : 0, y = i < b.Count ? b[i] : 0;
                if (x != y) return x > y;
            }
            return false;
        }

        public static string PlainNotes(string md)
        {
            return string.Join("\n", (md ?? "").Replace("\r", "").Split('\n').Select(line =>
            {
                var t = line.TrimEnd();
                string r = t.StartsWith("#", StringComparison.Ordinal) ? t.TrimStart('#').Trim()
                    : t.TrimStart().StartsWith("- ", StringComparison.Ordinal) || t.TrimStart().StartsWith("* ", StringComparison.Ordinal) ? "• " + t.TrimStart().Substring(2) : t;
                return r.Replace("**", "").Replace("`", "");
            })).Trim();
        }
    }

    /// The Web app's own browserServer calls, for its Preferences (AndroidLuna's ShellActivity).
    /// Every webOS window here is a frame of one WebView: its cache, cookies and IndexedDB are
    /// every app's, and Windows clears them only together, so neither clears on its own.
    internal sealed class BrowserServer
    {
        private readonly LunaShell shell;
        public BrowserServer(LunaShell shell) { this.shell = shell; }
        public void Register(Bus bus)
        {
            bus.Register("com.palm.browserServer", "clearCache", c =>
                c.Reply(Bus.Error("Lunacy's browser shares its cache with every app, and Windows clears it only with every app's stored data, so it can't be cleared on its own")));
            bus.Register("com.palm.browserServer", "clearCookies", c =>
                c.Reply(Bus.Error("Lunacy's browser shares its cookies with every app, so they can't be cleared on their own")));
        }
    }
}
