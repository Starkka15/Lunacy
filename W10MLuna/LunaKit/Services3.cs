using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Geolocation;
using Windows.Storage;

namespace LunaKit
{
    /// palm://com.palm.audio (AndroidLuna's AudioService.kt): the volumes of webOS's three
    /// classes - media, system and ringtone - in the TouchPad's shapes, and systemsounds'
    /// feedback sounds.
    ///
    /// A UWP app can't set Windows' own volume, so these are Lunacy's: system and ringtone
    /// scale the sounds Lunacy itself plays (feedback, alerts, ringtones: the shell's audio),
    /// and media scales every webOS app's audio and video (the shell sets it on each frame's
    /// media). Windows' volume keys still set the phone's volume over all of it, as the
    /// TouchPad's hardware volume sat over each class. Kept across starts.
    internal sealed class AudioService
    {
        private const string Service = "com.palm.audio";
        private static readonly string SchemaError = new JObj().Put("returnValue", false).Put("errorCode", 1).Put("errorText", "Could not validate json message against schema").ToString();
        private static readonly HashSet<string> Feedback = new HashSet<string> { "appclose", "birdappclose", "carddrag", "LauncherOpenApp", "LauncherCloseApp", "AdjustVolume" };
        private readonly LunaShell shell;
        private readonly ApplicationDataContainer prefs = ApplicationData.Current.LocalSettings.CreateContainer("audio", ApplicationDataCreateDisposition.Always);
        private readonly Subscribers mediaWatchers = new Subscribers(), systemWatchers = new Subscribers(), ringWatchers = new Subscribers();

        public AudioService(LunaShell shell) { this.shell = shell; }

        private int Get(string key, int fallback) { object v; return prefs.Values.TryGetValue(key, out v) && v is int ? (int)v : fallback; }
        private bool GetB(string key) { object v; return prefs.Values.TryGetValue(key, out v) && v is bool && (bool)v; }

        public int MediaVolume { get { return Get("media", 70); } }
        public int SystemVolume { get { return Get("system", 70); } }
        public int RingtoneVolume { get { return Get("ringtone", 80); } }
        public bool SystemMuted { get { return GetB("systemMuted"); } }
        public bool RingtoneMuted { get { return GetB("ringtoneMuted"); } }

        public void Register(Bus bus)
        {
            bus.Register(Service, "systemsounds/playFeedback", c =>
            {
                var name = Jx.ObjOrEmpty(c.Raw)["name"] as string;
                if (name == null) { c.Reply(SchemaError); return; }
                // A name LunaSysMgr has no sample for plays nothing, as on webOS.
                if (!SystemMuted && Feedback.Contains(name)) shell.Evaluate("__lunacyShell.playSound('/__lunacy/luna/sounds/feedback/" + name + ".wav'," + SystemVolume / 100.0 + ")");
                c.Reply(Bus.Ok());
            });
            bus.Register(Service, "media/status", c =>
            {
                c.Reply(MediaState().Put("action", "requested").Put("subscribed", c.Subscribe).ToString());
                if (c.Subscribe && !c.Cancelled) mediaWatchers.Add(c);
            });
            bus.Register(Service, "media/getVolume", c => c.Reply(new JObj().Put("returnValue", true).Put("scenario", "media_back_speaker").Put("volume", MediaVolume).ToString()));
            bus.Register(Service, "media/setVolume", c =>
            {
                var v = Integer(Jx.ObjOrEmpty(c.Raw), "volume");
                if (v == null) { c.Reply(SchemaError); return; }
                if (v < 0 || v > 100) { c.Reply(Invalid()); return; }
                bool changed = v != MediaVolume;
                prefs.Values["media"] = v.Value;
                Apply();
                c.Reply(Bus.Ok());
                if (changed) mediaWatchers.Send(MediaState().Put("action", "changed").Put("changed", new JArr().Put("volume")).ToString());
            });
            // The volume keys go to Windows whatever an app asks; the lock is answered as webOS's was.
            bus.Register(Service, "media/lockVolumeKeys", c => c.Reply(Bus.Ok()));
            Class(bus, "system", "system_default", systemWatchers);
            Class(bus, "ringtone", "ringtone_default", ringWatchers);
            Apply();
        }

        private static string Invalid() { return new JObj().Put("returnValue", false).Put("errorCode", 3).Put("errorText", "Invalid 'volume' integer parameter value.").ToString(); }

        private JObj MediaState()
        {
            return new JObj().Put("returnValue", true).Put("scenario", "media_back_speaker").Put("volume", MediaVolume).Put("mic_gain", -1)
                .Put("active", true).Put("ringer switch", true).Put("muted", false).Put("slider", false).Put("hac", false);
        }

        private JObj ClassState(string category, string scenario)
        {
            return new JObj().Put("returnValue", true).Put("scenario", scenario).Put("volume", Get(category, category == "system" ? 70 : 80))
                .Put("active", false).Put("ringer switch", true).Put("muted", GetB(category + "Muted")).Put("slider", false).Put("hac", false);
        }

        /// One of the private volume classes (registerPrivate on Android: system callers and
        /// Lunacy's settings apps; anything on this bus here).
        private void Class(Bus bus, string category, string scenario, Subscribers watchers)
        {
            bus.Register(Service, category + "/status", c =>
            {
                c.Reply(ClassState(category, scenario).Put("action", "requested").Put("subscribed", c.Subscribe).ToString());
                if (c.Subscribe && !c.Cancelled) watchers.Add(c);
            });
            bus.Register(Service, category + "/getVolume", c => c.Reply(new JObj().Put("returnValue", true).Put("scenario", scenario).Put("volume", Get(category, 70)).ToString()));
            bus.Register(Service, category + "/setVolume", c =>
            {
                var v = Integer(Jx.ObjOrEmpty(c.Raw), "volume");
                if (v == null) { c.Reply(SchemaError); return; }
                if (v < 0 || v > 100) { c.Reply(Invalid()); return; }
                bool changed = v != Get(category, -1);
                prefs.Values[category] = v.Value;
                Apply();
                c.Reply(Bus.Ok());
                if (changed) watchers.Send(ClassState(category, scenario).Put("action", "changed").Put("changed", new JArr().Put("volume")).ToString());
            });
            bus.Register(Service, category + "/setMuted", c =>
            {
                var m = Jx.ObjOrEmpty(c.Raw)["muted"];
                if (!(m is bool)) { c.Reply(SchemaError); return; }
                bool was = GetB(category + "Muted");
                prefs.Values[category + "Muted"] = (bool)m;
                Apply();
                c.Reply(Bus.Ok());
                if (was != (bool)m) watchers.Send(ClassState(category, scenario).Put("action", "changed").Put("changed", new JArr().Put("muted")).ToString());
            });
        }

        /// The shell's volumes: media for the apps' frames, system and ringtone for its own sounds.
        public void Apply()
        {
            shell.Evaluate("window.__lunacyShell&&__lunacyShell.setVolumes(" + new JObj()
                .Put("media", MediaVolume / 100.0)
                .Put("system", SystemMuted ? 0 : SystemVolume / 100.0)
                .Put("ringtone", RingtoneMuted ? 0 : RingtoneVolume / 100.0) + ")");
        }

        private static int? Integer(JObj p, string key)
        {
            var v = p[key];
            if (v is long) return (int)(long)v;
            if (v is double && (double)v == Math.Floor((double)v)) return (int)(double)v;
            return null;
        }
    }

    /// palm://com.palm.location (AndroidLuna's LocationService.kt): positions from Windows'
    /// Geolocator, behind webOS's own question. An app asks the first time in a session (the
    /// system alerts app's LocationAlert, as LunaSysMgr's prompt); Location Services' settings
    /// (auto-locate, GPS, web pages) are kept here; Windows' own consent sits under it all, asked
    /// once (First Use's Location page, or here when an allowed app first needs it).
    internal sealed class LocationService
    {
        private const string Service = "com.palm.location";
        public const string AlertsApp = "org.webosarchive.lunacy.systemalerts";
        private const int Ok = 0, TimeoutCode = 1, Off = 5, Denied = 6, Pending = 7;
        private const int PromptTimeoutMs = 120000;
        private readonly LunaShell shell;
        private readonly ApplicationDataContainer prefs = ApplicationData.Current.LocalSettings.CreateContainer("location", ApplicationDataCreateDisposition.Always);
        private readonly HashSet<string> sessionAllowed = new HashSet<string>(), sessionDenied = new HashSet<string>();
        private readonly object gate = new object();

        private sealed class Ask { public string Type, Id, Name; public readonly List<Action<bool>> Waiters = new List<Action<bool>>(); }
        private readonly Queue<Ask> queue = new Queue<Ask>();
        private Ask showing;
        private Timer promptTimer;

        public LocationService(LunaShell shell) { this.shell = shell; }

        private bool Pref(string key, bool fallback) { object v; return prefs.Values.TryGetValue(key, out v) && v is bool ? (bool)v : fallback; }

        public void Register(Bus bus)
        {
            bus.Register(Service, "getCurrentPosition", GetCurrentPosition);
            bus.Register(Service, "startTracking", StartTracking);
            bus.Register(Service, "getLocationServicePrefs", c => c.Reply(PrefsJson().ToString()));
            Getter(bus, "getAutoLocate", "autoLocate", false);
            Getter(bus, "getUseGps", "useGps", true);
            Getter(bus, "getGeotagPhotos", "geotagPhotos", false);
            Getter(bus, "getUseBackgroundDataCollection", "useBackgroundDataCollection", false);
            Getter(bus, "getWebSetting", "webSetting", true);
            foreach (var kv in new[] { "setAutoLocate:autoLocate", "setUseGps:useGps", "setUseGoogle:useGoogle", "setWebSetting:webSetting", "setGeotagPhotos:geotagPhotos", "setUseBackgroundDataCollection:useBackgroundDataCollection" })
            {
                var parts = kv.Split(':');
                var key = parts[1];
                Setting(bus, parts[0], p => { if (p.Has(key)) prefs.Values[key] = p.Bool(key); });
            }
            Setting(bus, "acceptTermsOfUse", p => { });
            Setting(bus, "rejectTermsOfUse", p => { });
            Setting(bus, "clearWebSetting", p => prefs.Values.Remove("webAllowed"));
            foreach (var kv in new[] { "acceptLocationRequest:accept", "acceptAlwaysLocationRequest:always", "rejectLocationRequest:reject", "ignoreLocationRequest:ignore" })
            {
                var parts = kv.Split(':');
                var verdict = parts[1];
                bus.Register(Service, parts[0], c =>
                {
                    if (c.AppId != AlertsApp) { c.Reply(Bus.Error("Only the system's location alert answers a location request", -1)); return; }
                    var p = Jx.ObjOrEmpty(c.Raw);
                    if (p.Str("appId").Length > 0) Answer("app", p.Str("appId"), verdict);
                    else if (p.Str("url").Length > 0) Answer("web", p.Str("url"), verdict);
                    c.Reply(Bus.Ok());
                });
            }
            // Web pages' navigator.geolocation: the WebView asks for each frame's origin.
            shell.View.PermissionRequested += (s, e) =>
            {
                if (e.PermissionRequest.PermissionType != Windows.UI.Xaml.Controls.WebViewPermissionType.Geolocation) return;
                var req = e.PermissionRequest;
                req.Defer();
                var uri = req.Uri;
                var appMatch = System.Text.RegularExpressions.Regex.Match(uri.AbsolutePath, "^/" + AppServer.AppsPath.Replace(".", "\\.") + "([^/]+)/");
                string type = appMatch.Success ? "app" : "web";
                string id = appMatch.Success ? appMatch.Groups[1].Value : uri.Scheme + "://" + uri.Host + (uri.IsDefaultPort ? "" : ":" + uri.Port);
                string name = appMatch.Success ? Title(id) : uri.Host;
                Authorize(type, id, name, true, code => shell.Ui(() => { if (code == Ok) req.Allow(); else req.Deny(); }));
            };
        }

        /// An app's card closed: it asks again next time (LunaShell, as Android's appEnded).
        public void AppEnded(string appId) { lock (gate) { sessionAllowed.Remove(appId); sessionDenied.Remove(appId); } }

        private string Title(string id) { var a = shell.Apps.Get(id); return a == null ? id : a.Title; }

        private JObj PrefsJson()
        {
            return new JObj().Put("autoLocate", Pref("autoLocate", false)).Put("cameraAvailable", false).Put("geotagPhotos", Pref("geotagPhotos", false))
                .Put("gpsAvailable", true).Put("isTermsOfUseAccepted", true).Put("useBackgroundDataCollection", Pref("useBackgroundDataCollection", false))
                .Put("useGoogle", Pref("useGoogle", true)).Put("useGps", Pref("useGps", true)).Put("webSetting", Pref("webSetting", true));
        }

        private void Getter(Bus bus, string method, string key, bool fallback)
        {
            bus.Register(Service, method, c => c.Reply(new JObj().Put(key, Pref(key, fallback)).ToString()));
        }

        private void Setting(Bus bus, string method, Action<JObj> f)
        {
            bus.Register(Service, method, c =>
            {
                if (!c.AppId.StartsWith("org.webosarchive.lunacy", StringComparison.Ordinal) && c.AppId != "com.palm.app.location" && !bus.Has(c.AppId))
                {
                    c.Reply(Bus.Error("Only Location Services changes location settings", -1));
                    return;
                }
                f(Jx.ObjOrEmpty(c.Raw));
                c.Reply(Bus.Ok());
            });
        }

        private HashSet<string> WebAllowed()
        {
            object v;
            return new HashSet<string>(prefs.Values.TryGetValue("webAllowed", out v) && v is string ? ((string)v).Split('\n').Where(x => x.Length > 0) : new string[0]);
        }

        private void Authorize(string type, string id, string name, bool joinPending, Action<int> done)
        {
            lock (gate)
            {
                if (type == "app")
                {
                    if (Pref("autoLocate", false) || sessionAllowed.Contains(id)) { WithWindows(done); return; }
                    if (sessionDenied.Contains(id)) { done(Denied); return; }
                }
                else
                {
                    if (!Pref("webSetting", true)) { done(Denied); return; }
                    if (WebAllowed().Contains(id)) { WithWindows(done); return; }
                }
                var pending = (showing != null ? new[] { showing } : new Ask[0]).Concat(queue).FirstOrDefault(a => a.Type == type && a.Id == id);
                if (pending != null && !joinPending) { done(Pending); return; }
                var ask = pending;
                if (ask == null) { ask = new Ask { Type = type, Id = id, Name = name }; queue.Enqueue(ask); }
                ask.Waiters.Add(ok => { if (ok) WithWindows(done); else done(Denied); });
            }
            ShowNext();
        }

        private void ShowNext()
        {
            Ask ask;
            lock (gate)
            {
                if (showing != null || queue.Count == 0) return;
                ask = showing = queue.Dequeue();
            }
            if (shell.Apps.Get(AlertsApp) == null) { Finish(ask, false); return; }
            shell.Launch(AlertsApp, J.Obj(J.P("alert", "LocationAlert"), J.P("type", ask.Type), J.P("appId", ask.Id), J.P("appName", ask.Name)), null);
            lock (gate) promptTimer = new Timer(_ => Answer(ask.Type, ask.Id, "ignore"), null, PromptTimeoutMs, -1);
        }

        private void Answer(string type, string id, string verdict)
        {
            Ask ask;
            bool allowed = verdict == "accept" || verdict == "always";
            lock (gate)
            {
                ask = showing != null && showing.Type == type && showing.Id == id ? showing : null;
                if (type == "app")
                {
                    if (allowed) { sessionAllowed.Add(id); sessionDenied.Remove(id); }
                    else if (verdict == "reject") { sessionDenied.Add(id); sessionAllowed.Remove(id); }
                }
                else if (verdict == "always")
                {
                    var set = WebAllowed(); set.Add(id);
                    prefs.Values["webAllowed"] = string.Join("\n", set);
                }
            }
            if (ask != null) Finish(ask, allowed);
        }

        private void Finish(Ask ask, bool allowed)
        {
            List<Action<bool>> waiters;
            lock (gate)
            {
                if (promptTimer != null) { promptTimer.Dispose(); promptTimer = null; }
                if (showing == ask) showing = null;
                waiters = ask.Waiters.ToList();
            }
            foreach (var w in waiters) w(allowed);
            ShowNext();
        }

        /// Windows' own consent, asked once if it hasn't been.
        private void WithWindows(Action<int> done)
        {
            if (LunacyService.LocationGranted()) { done(Ok); return; }
            shell.Ui(async () =>
            {
                LunacyService.SetFlag(LunacyService.LocationAskedKey);
                GeolocationAccessStatus s;
                try { s = await Geolocator.RequestAccessAsync(); } catch (Exception) { s = GeolocationAccessStatus.Denied; }
                done(s == GeolocationAccessStatus.Allowed ? Ok : Denied);
            });
        }

        private static string Error(int code) { return new JObj().Put("errorCode", code).ToString(); }

        private static string Result(Geoposition pos)
        {
            var c = pos.Coordinate;
            return new JObj().Put("errorCode", Ok).Put("timestamp", (double)c.Timestamp.ToUnixTimeMilliseconds())
                .Put("latitude", c.Point.Position.Latitude).Put("longitude", c.Point.Position.Longitude)
                .Put("horizAccuracy", c.Accuracy).Put("heading", c.Heading ?? -1.0).Put("velocity", c.Speed ?? -1.0)
                .Put("altitude", c.Point.AltitudeReferenceSystem == AltitudeReferenceSystem.Unspecified ? -1.0 : c.Point.Position.Altitude)
                .Put("vertAccuracy", c.AltitudeAccuracy ?? -1.0).ToString();
        }

        private Geolocator Locator(int accuracy)
        {
            var g = new Geolocator();
            // webOS's accuracy 1 is "high": GPS, when Location Services allows it.
            g.DesiredAccuracy = accuracy == 1 && Pref("useGps", true) ? PositionAccuracy.High : PositionAccuracy.Default;
            return g;
        }

        private void GetCurrentPosition(Bus.Call c)
        {
            var p = Jx.ObjOrEmpty(c.Raw);
            int accuracy = p.Has("accuracy") ? p.Int("accuracy") : 3;
            long maxAgeMs = (p.Has("maximumAge") ? p.Long("maximumAge") : 0) * 1000;
            int responseTime = p.Has("responseTime") ? p.Int("responseTime") : 2;
            int timeoutMs = responseTime == 1 ? 5000 : responseTime == 3 ? 60000 : 20000;
            Authorize("app", c.AppId, Title(c.AppId), false, code =>
            {
                if (code != Ok) { c.Reply(Error(code)); return; }
                if (!Pref("useGps", true) && !Pref("useGoogle", true)) { c.Reply(Error(Off)); return; }
                Task.Run(async () =>
                {
                    try
                    {
                        var g = Locator(accuracy);
                        if (g.LocationStatus == PositionStatus.Disabled || g.LocationStatus == PositionStatus.NotAvailable) { c.Reply(Error(Off)); return; }
                        var pos = await g.GetGeopositionAsync(TimeSpan.FromMilliseconds(Math.Max(0, maxAgeMs)), TimeSpan.FromMilliseconds(timeoutMs));
                        c.Reply(Result(pos));
                    }
                    catch (TimeoutException) { c.Reply(Error(TimeoutCode)); }
                    catch (UnauthorizedAccessException) { c.Reply(Error(Denied)); }
                    catch (Exception e) { LunaLog.Write("[location] " + e.Message); c.Reply(Error(TimeoutCode)); }
                });
            });
        }

        private void StartTracking(Bus.Call c)
        {
            Authorize("app", c.AppId, Title(c.AppId), false, code =>
            {
                if (code != Ok) { c.Reply(Error(code)); return; }
                if (!Pref("useGps", true) && !Pref("useGoogle", true)) { c.Reply(Error(Off)); return; }
                var g = Locator(1);
                g.ReportInterval = 1000;
                if (g.LocationStatus == PositionStatus.Disabled || g.LocationStatus == PositionStatus.NotAvailable) { c.Reply(Error(Off)); return; }
                Windows.Foundation.TypedEventHandler<Geolocator, PositionChangedEventArgs> h = (s, e) => c.Reply(Result(e.Position));
                g.PositionChanged += h;
                c.OnCancel(() => g.PositionChanged -= h);
            });
        }
    }

    /// palm://com.palm.downloadmanager (LunaCore's DownloadManager.kt): fetches a file into the
    /// webOS tree for an app, reporting progress to whoever subscribed, in the reference
    /// TouchPad's shapes (measured 2026-09-22). /media/internal/downloads is the phone's own
    /// Downloads (UserFiles). The file is written as ".name" and renamed when complete, as the
    /// device did. Records live with the process: an interrupted download is not resumable,
    /// and canHandlePause is false, as the device reported.
    internal sealed class DownloadManager
    {
        private const string Service = "com.palm.downloadmanager";
        private const string DefaultDir = "/media/internal/downloads";
        private const string TempPrefix = ".";
        private const int CancelledCode = 12;
        private const int ReportMs = 250;

        private sealed class Record
        {
            public int Ticket;
            public string Url, DestPath, DestFile, Target;
            public long Received, Total;
            public string Mime = "";
            public int Http;
            public volatile bool Completed, Aborted, Interrupted, Finished;
            public readonly List<Bus.Call> Watchers = new List<Bus.Call>();
        }

        private readonly WebosFs fs;
        private readonly Dictionary<int, Record> records = new Dictionary<int, Record>();
        private int nextTicket = 1;

        public DownloadManager(WebosFs fs) { this.fs = fs; }

        public void Register(Bus bus)
        {
            bus.Register(Service, "download", Download);
            bus.Register(Service, "listPending", c => { int n; lock (records) n = records.Values.Count(r => !r.Finished); c.Reply(new JObj().Put("returnValue", true).Put("count", n).ToString()); });
            bus.Register(Service, "downloadStatusQuery", StatusQuery);
            bus.Register(Service, "resumeDownload", c => c.Reply(Jx.ObjOrEmpty(c.Raw).Has("ticket") ? Refused("Ticket provided does not correspond to an interrupted transfer in history") : Refused("Missing ticket parameter")));
            bus.Register(Service, "pauseDownload", c =>
            {
                var p = Jx.ObjOrEmpty(c.Raw);
                if (!p.Has("ticket")) { c.Reply(Refused("Missing ticket parameter")); return; }
                var r = Get(p.Int("ticket", -1));
                if (r != null && !r.Finished) r.Aborted = true;
                c.Reply(Refused("Ticket provided does not correspond to a downloading transfer"));
            });
            bus.Register(Service, "cancelDownload", c =>
            {
                int ticket = Jx.ObjOrEmpty(c.Raw).Int("ticket", -1);
                var r = Get(ticket);
                if (r != null) r.Aborted = true;
                c.Reply(new JObj().Put("ticket", ticket).Put("returnValue", true).Put("subscribed", false).Put("aborted", true).Put("completed", false).Put("completionStatusCode", CancelledCode).ToString());
            });
            bus.Register(Service, "deleteDownloadedFile", c =>
            {
                int ticket = Jx.ObjOrEmpty(c.Raw).Int("ticket", -1);
                var r = Get(ticket);
                if (r == null) { c.Reply(new JObj().Put("ticket", ticket).Put("returnValue", false).Put("errorCode", "requested download record not found").ToString()); return; }
                try { if (fs.Exists(r.Target)) fs.Remove(r.Target, false, false); } catch (Exception) { }
                lock (records) records.Remove(ticket);
                c.Reply(new JObj().Put("ticket", ticket).Put("returnValue", true).ToString());
            });
            bus.Register(Service, "allow1x", c => c.Reply(new JObj().Put("returnValue", true).Put("value", !Jx.ObjOrEmpty(c.Raw).Has("value") || Jx.ObjOrEmpty(c.Raw).Bool("value")).Put("subscribed", false).ToString()));
        }

        private Record Get(int ticket) { lock (records) { Record r; return records.TryGetValue(ticket, out r) ? r : null; } }

        private static string BadRequest(string text) { return new JObj().Put("returnValue", false).Put("errorCode", "-1").Put("errorText", text).Put("subscribed", false).ToString(); }
        private static string Refused(string text) { return new JObj().Put("subscribed", false).Put("returnValue", false).Put("errorCode", text).ToString(); }

        private void Download(Bus.Call c)
        {
            var p = Jx.ObjOrEmpty(c.Raw);
            var target = p.Str("target");
            if (target.Length == 0) { c.Reply(BadRequest("Failed to find param target in message")); return; }
            var dir = p.Str("targetDir").Length > 0 ? p.Str("targetDir") : DefaultDir;
            var name = p.Str("targetFilename");
            if (name.Length == 0) { var t = target.Split('?')[0]; name = t.Substring(t.LastIndexOf('/') + 1); }
            if (name.Length == 0 || name == "." || name == ".." || name.Contains("/")) { c.Reply(BadRequest("Invalid target filename")); return; }
            var destPath = dir.EndsWith("/", StringComparison.Ordinal) ? dir : dir + "/";
            var full = WebosFs.Normal(destPath + name);
            if (!full.StartsWith("/media/internal/", StringComparison.Ordinal) || (fs.Exists(full) && fs.IsDir(full))) { c.Reply(BadRequest("Invalid target directory")); return; }
            Record rec;
            lock (records)
            {
                rec = new Record { Ticket = nextTicket++, Url = target, DestPath = destPath, DestFile = name, Target = destPath + name };
                records[rec.Ticket] = rec;
            }
            c.Reply(new JObj().Put("returnValue", true).Put("ticket", rec.Ticket).Put("url", target).Put("target", rec.Target).Put("subscribed", c.Subscribe).ToString());
            if (c.Subscribe && !c.Cancelled) Watch(rec, c);
            Task.Run(() => Fetch(rec));
        }

        private void Watch(Record rec, Bus.Call c)
        {
            lock (rec.Watchers) rec.Watchers.Add(c);
            c.OnCancel(() => { lock (rec.Watchers) rec.Watchers.Remove(c); });
            if (rec.Finished) c.Reply(Completion(rec));
        }

        private void Send(Record rec, string msg)
        {
            Bus.Call[] all;
            lock (rec.Watchers) all = rec.Watchers.ToArray();
            foreach (var w in all) w.Reply(msg);
        }

        private async Task Fetch(Record rec)
        {
            var dir = WebosFs.Normal(rec.DestPath);
            var temp = dir + "/" + TempPrefix + rec.DestFile;
            try
            {
                if (!fs.Exists(dir)) fs.MakeDir(dir, true);
                using (var http = new Windows.Web.Http.HttpClient())
                {
                    var res = await http.GetAsync(new Uri(rec.Url), Windows.Web.Http.HttpCompletionOption.ResponseHeadersRead);
                    rec.Http = (int)res.StatusCode;
                    var ct = res.Content.Headers.ContentType;
                    rec.Mime = ct == null ? "" : ct.MediaType ?? "";
                    if (rec.Http < 200 || rec.Http > 299) throw new IOException("HTTP " + rec.Http);
                    rec.Total = (long)(res.Content.Headers.ContentLength ?? 0);
                    var input = (await res.Content.ReadAsInputStreamAsync()).AsStreamForRead();
                    using (var output = fs.OpenWrite(temp))
                    {
                        var buf = new byte[32 * 1024];
                        long lastReport = 0;
                        while (true)
                        {
                            if (rec.Aborted) throw new IOException("cancelled");
                            int n = await input.ReadAsync(buf, 0, buf.Length);
                            if (n <= 0) break;
                            await output.WriteAsync(buf, 0, n);
                            rec.Received += n;
                            long now = Environment.TickCount;
                            if (now - lastReport >= ReportMs) { lastReport = now; Report(rec); }
                        }
                    }
                }
                if (rec.Total == 0) rec.Total = rec.Received;
                if (fs.Exists(rec.Target)) fs.Remove(rec.Target, false, false);
                fs.Rename(temp, rec.Target);
                rec.Completed = true;
            }
            catch (Exception e)
            {
                try { if (fs.Exists(temp)) fs.Remove(temp, false, false); } catch (Exception) { }
                rec.Interrupted = !rec.Aborted;
                LunaLog.Write("[download] " + rec.Ticket + " " + rec.Url + ": " + e.Message);
            }
            rec.Finished = true;
            Send(rec, Completion(rec));
        }

        private void Report(Record rec)
        {
            Send(rec, new JObj().Put("ticket", rec.Ticket).Put("amountReceived", rec.Received).Put("e_amountReceived", rec.Received.ToString())
                .Put("amountTotal", rec.Total).Put("e_amountTotal", rec.Total.ToString()).ToString());
        }

        private static string Completion(Record rec)
        {
            return new JObj().Put("ticket", rec.Ticket).Put("url", rec.Url).Put("sourceUrl", rec.Url).Put("deviceId", "").Put("authToken", "")
                .Put("destTempPrefix", TempPrefix).Put("destFile", rec.DestFile).Put("destPath", rec.DestPath).Put("mimetype", rec.Mime)
                .Put("amountReceived", rec.Received).Put("e_amountReceived", rec.Received.ToString())
                .Put("amountTotal", rec.Total).Put("e_amountTotal", rec.Total.ToString())
                .Put("initialOffset", 0).Put("e_initialOffsetBytes", "0").Put("e_rangeLow", "0").Put("e_rangeHigh", "0")
                .Put("canHandlePause", false).Put("cookieHeader", "")
                .Put("completionStatusCode", rec.Aborted ? CancelledCode : rec.Http).Put("httpStatus", rec.Http)
                .Put("interrupted", rec.Interrupted).Put("completed", rec.Completed).Put("aborted", rec.Aborted).Put("target", rec.Target).ToString();
        }

        private void StatusQuery(Bus.Call c)
        {
            int ticket = Jx.ObjOrEmpty(c.Raw).Int("ticket", -1);
            var rec = Get(ticket);
            if (rec == null) { c.Reply(new JObj().Put("ticket", ticket).Put("returnValue", false).Put("errorCode", "ticket_not_found").Put("subscribed", false).ToString()); return; }
            if (rec.Finished) c.Reply(Completion(rec));
            else c.Reply(new JObj().Put("ticket", rec.Ticket).Put("amountReceived", rec.Received).Put("e_amountReceived", rec.Received.ToString())
                .Put("amountTotal", rec.Total).Put("e_amountTotal", rec.Total.ToString()).ToString());
            if (c.Subscribe && !c.Cancelled && !rec.Finished) Watch(rec, c);
        }
    }

    /// palm://com.palm.universalsearch (LunaCore's UniversalSearch.kt): Just Type's lists - the
    /// web search engines, and the apps that declare universalSearch (search, action and
    /// dbsearch) - with each one switched on or off, and the preferences Search Preferences sets.
    internal sealed class UniversalSearch
    {
        private const string Service = "com.palm.universalsearch";
        private const string Icons = "/usr/lib/luna/system/luna-applauncher/images/";
        private const string Search = "search", Action = "action", DbSearch = "dbsearch";
        private static readonly string[][] Engines =
        {
            new[] { "duckduckgo", "DuckDuckGo", "https://lite.duckduckgo.com/lite/?q=#{searchTerms}", "1" },
            new[] { "wikipedia", "Wikipedia", "http://en.wikipedia.org/wiki/Special:Search?search=#{searchTerms}", "1" },
            new[] { "cnn", "CNN", "http://www.cnn.com/search/?query=#{searchTerms}", "0" },
            new[] { "amazon", "Amazon", "http://www.amazon.com/s/?k=#{searchTerms}", "0" },
            new[] { "imdb", "IMDb", "http://www.imdb.com/find?q=#{searchTerms}", "0" },
        };
        private static readonly string[][] DefaultPrefs =
        {
            new[] { "AppSearch", "true" }, new[] { "ContactSearch", "true" }, new[] { "GAL", "false" },
            new[] { "defaultSearch", "true" }, new[] { "defaultSearchEngine", "duckduckgo" },
        };

        private readonly string file;
        private readonly AppRegistry apps;
        private readonly JObj store;
        private readonly Subscribers listSubscribers = new Subscribers(), prefSubscribers = new Subscribers();

        public UniversalSearch(string file, AppRegistry apps)
        {
            this.file = file;
            this.apps = apps;
            try { store = File.Exists(file) ? Jx.ParseObj(File.ReadAllText(file)) : new JObj(); } catch (Exception) { store = new JObj(); }
        }

        private string Saved(string key) { lock (store) return store[key] as string; }
        private void Put(string key, string value)
        {
            lock (store)
            {
                store.Put(key, value);
                try { File.WriteAllText(file, store.ToString()); } catch (Exception e) { LunaLog.Write("[universalsearch] can't save: " + e.Message); }
            }
        }

        public void Register(Bus bus)
        {
            if (Saved("seen") == null) Put("seen", string.Join("\n", Integrated()));
            bus.Register(Service, "getUniversalSearchList", c =>
            {
                c.Reply(ListReply().Put("subscribed", c.Subscribe).ToString());
                if (c.Subscribe && !c.Cancelled) listSubscribers.Add(c);
            });
            bus.Register(Service, "getAllSearchPreference", c =>
            {
                c.Reply(new JObj().Put("SearchPreference", PrefsJson()).Put("subscribed", c.Subscribe).Put("returnValue", true).ToString());
                if (c.Subscribe && !c.Cancelled) prefSubscribers.Add(c);
            });
            bus.Register(Service, "setSearchPreference", c =>
            {
                var p = Jx.ObjOrEmpty(c.Raw);
                var key = p.Str("key");
                if (key.Length == 0 || !p.Has("value")) { c.Reply(Bus.Error("Unable to get data")); return; }
                var v = p["value"];
                Put("pref:" + key, v is string ? (string)v : Jx.Stringify(v));
                c.Reply(Bus.Ok());
                PrefsChanged();
                if (key == "defaultSearchEngine" || key == "defaultSearch") ListChanged("update");
            });
            bus.Register(Service, "updateSearchItem", c =>
            {
                var p = Jx.ObjOrEmpty(c.Raw);
                var id = p.Str("id"); var category = p.Str("category");
                if (id.Length == 0 || !IsCategory(category)) { c.Reply(Bus.Error("Unable to get data")); return; }
                if (p.Has("enabled")) Put(EnabledKey(category, id), p.Bool("enabled") ? "true" : "false");
                if (p.Bool("setDefault")) Put("pref:defaultSearchEngine", id);
                c.Reply(Bus.Ok());
                ListChanged("update");
                if (p.Bool("setDefault")) PrefsChanged();
            });
            bus.Register(Service, "updateAllSearchItems", c =>
            {
                var p = Jx.ObjOrEmpty(c.Raw);
                var category = p.Str("category");
                if (!IsCategory(category) || !p.Has("enabled")) { c.Reply(Bus.Error("Unable to get data")); return; }
                bool on = p.Bool("enabled");
                foreach (var item in List(category))
                    if (!(category == Search && item.Str("id") == DefaultEngine())) Put(EnabledKey(category, item.Str("id")), on ? "true" : "false");
                c.Reply(Bus.Ok());
                ListChanged("update");
            });
            bus.Register(Service, "addSearchItem", c =>
            {
                var p = Jx.ObjOrEmpty(c.Raw);
                var id = p.Str("id");
                if (id.Length == 0 || p.Str("url").Length == 0) { c.Reply(Bus.Error("Unable to get data")); return; }
                var added = AddedEngines();
                added.Put(id, new JObj().Put("id", id).Put("displayName", p.Has("displayName") ? p.Str("displayName") : id)
                    .Put("iconFilePath", p.Str("iconFilePath")).Put("url", p.Str("url")).Put("suggestURL", p.Str("suggestURL"))
                    .Put("launchParam", "").Put("type", p.Has("type") ? p.Str("type") : "opensearch"));
                Put("added", added.ToString());
                Put(EnabledKey(Search, id), !p.Has("enabled") || p.Bool("enabled") ? "true" : "false");
                if (p.Bool("setDefault")) Put("pref:defaultSearchEngine", id);
                c.Reply(Bus.Ok());
                ListChanged("newapp");
                if (p.Bool("setDefault")) PrefsChanged();
            });
            bus.Register(Service, "getOptionalSearchList", c => c.Reply(new JObj().Put("Options", new JArr()).Put("subscribed", c.Subscribe).Put("returnValue", true).ToString()));
            bus.Register(Service, "clearOptionalSearchList", c => c.Reply(Bus.Ok()));
        }

        private static bool IsCategory(string c) { return c == Search || c == Action || c == DbSearch; }
        private static string EnabledKey(string category, string id) { return "on:" + category + ":" + id; }
        private bool IsOn(string category, string id, bool initially) { var s = Saved(EnabledKey(category, id)); return s == null ? initially : s == "true"; }

        public string Preference(string key)
        {
            var s = Saved("pref:" + key);
            if (s != null) return s;
            var d = DefaultPrefs.FirstOrDefault(x => x[0] == key);
            return d == null ? "" : d[1];
        }

        public string DefaultEngine() { return Preference("defaultSearchEngine"); }

        private JObj AddedEngines() { try { return Jx.ParseObj(Saved("added") ?? "{}"); } catch (Exception) { return new JObj(); } }

        private List<JObj> List(string category)
        {
            var out_ = new List<JObj>();
            if (category == Search)
            {
                foreach (var e in Engines)
                    out_.Add(new JObj().Put("id", e[0]).Put("displayName", e[1]).Put("iconFilePath", Icons + "search-icon-" + e[0] + ".png").Put("url", e[2])
                        .Put("suggestURL", "").Put("launchParam", "").Put("type", "web").Put("enabled", IsOn(Search, e[0], e[3] == "1")));
                var added = AddedEngines();
                foreach (var kv in added) { var j = (kv.Value as JObj ?? new JObj()).Copy(); out_.Add(j.Put("enabled", IsOn(Search, kv.Key, true))); }
            }
            out_.AddRange(AppItems(category));
            return out_;
        }

        private List<string> Integrated()
        {
            return apps.All().Where(a => Jx.ObjOrEmpty(a.Raw.Stringify()).Obj("universalSearch") != null).Select(a => a.Id).ToList();
        }

        private List<string> SavedSeen() { return (Saved("seen") ?? "").Split('\n').Where(x => x.Length > 0).ToList(); }

        private List<JObj> AppItems(string category)
        {
            var seen = SavedSeen();
            var order = seen.Concat(Integrated().Where(id => !seen.Contains(id))).ToList();
            var out_ = new List<JObj>();
            foreach (var id in order)
            {
                var app = apps.Get(id);
                if (app == null) continue;
                var us = Jx.ObjOrEmpty(app.Raw.Stringify()).Obj("universalSearch");
                var decl = us == null ? null : us.Obj(category);
                if (decl == null) continue;
                var param = decl.Has("launchParam") ? (decl["launchParam"] is string ? (string)decl["launchParam"] : Jx.Stringify(decl["launchParam"])) : "";
                var j = new JObj().Put("id", app.Id).Put("displayName", decl.Has("displayName") ? decl.Str("displayName") : app.Title).Put("iconFilePath", app.Url(app.Icon));
                if (category == Search) j.Put("url", app.Id).Put("suggestURL", "").Put("launchParam", param).Put("type", "app");
                else if (category == Action) j.Put("url", app.Id).Put("launchParam", param);
                else
                {
                    j.Put("launchParam", param).Put("launchParamDbField", decl.Str("launchParamDbField"));
                    if (decl.Has("dbQuery")) j.Put("dbQuery", decl["dbQuery"]);
                    j.Put("displayFields", decl.Arr("displayFields") ?? new JArr()).Put("batchQuery", decl.Bool("batchQuery"));
                }
                out_.Add(j.Put("enabled", IsOn(category, app.Id, true)));
            }
            return out_;
        }

        /// The app list changed: an app that integrates with Just Type came or went.
        public void AppsChanged()
        {
            var seen = SavedSeen();
            var known = Integrated();
            var added = known.Where(k => !seen.Contains(k)).ToList();
            var removed = seen.Where(s => !known.Contains(s)).ToList();
            if (added.Count == 0 && removed.Count == 0) return;
            Put("seen", string.Join("\n", seen.Where(known.Contains).Concat(added)));
            ListChanged(added.Count > 0 ? "newapp" : "remove");
        }

        private JObj PrefsJson()
        {
            var out_ = new JObj();
            foreach (var d in DefaultPrefs) out_.Put(d[0], d[1]);
            lock (store) foreach (var kv in store) if (kv.Key.StartsWith("pref:", StringComparison.Ordinal)) out_.Put(kv.Key.Substring(5), kv.Value);
            return out_;
        }

        private JObj ListReply()
        {
            Func<string, JArr> arr = cat => new JArr(List(cat).Cast<object>());
            return new JObj().Put("returnValue", true).Put("UniversalSearchList", arr(Search)).Put("ActionList", arr(Action))
                .Put("DBSearchItemList", arr(DbSearch)).Put("defaultSearchEngine", DefaultEngine());
        }

        private void ListChanged(string ev) { listSubscribers.Send(ListReply().Put("event", ev).ToString()); }
        private void PrefsChanged() { prefSubscribers.Send(new JObj().Put("returnValue", true).Put("SearchPreference", PrefsJson()).ToString()); }
    }
}
