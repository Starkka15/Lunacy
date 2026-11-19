using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Windows.Globalization;
using Windows.System.UserProfile;

namespace LunaKit
{
    /// palm://com.palm.systemservice: webOS's preference store, the time and the time zones
    /// (AndroidLuna's SystemService.kt). A key nothing reads is still stored, as on a device.
    /// Replies are the reference TouchPad's (measured with luna-send, webOS CE 3.1.0):
    /// getPreferences returns only the keys that exist, and a wallpaper is
    /// {wallpaperName, wallpaperFile, wallpaperThumbFile} under /media/internal/.wallpapers.
    internal sealed class SystemService
    {
        private const string Service = "com.palm.systemservice";
        private readonly string store;
        private readonly DeviceProfile profile;
        private readonly object gate = new object();
        private readonly JObj prefs;
        private readonly List<KeyValuePair<Bus.Call, List<string>>> subscribers = new List<KeyValuePair<Bus.Call, List<string>>>();
        private readonly List<Bus.Call> timeSubscribers = new List<Bus.Call>();

        public SystemService(string store, DeviceProfile profile)
        {
            this.store = store;
            this.profile = profile;
            try { prefs = File.Exists(store) ? Jx.ParseObj(File.ReadAllText(store)) : new JObj(); }
            catch (Exception e) { LunaLog.Write("[systemservice] preferences unreadable: " + e.Message); prefs = new JObj(); }
        }

        public void Register(Bus bus)
        {
            bus.Register(Service, "time/getSystemTime", SystemTime);
            bus.Register(Service, "timezone/getTimeZoneRules", c => c.Reply(TimeZoneRules(c.Raw)));
            bus.Register(Service, "getPreferences", GetPreferences);
            bus.Register(Service, "setPreferences", SetPreferences);
            bus.Register(Service, "timezone/getTimeZoneFromEasData", c => c.Reply(TimeZoneFromEasData(Jx.ObjOrEmpty(c.Raw))));
            bus.Register(Service, "wallpaper/importWallpaper", ImportWallpaper);
            bus.Register(Service, "wallpaper/info", WallpaperInfo);
            bus.Register(Service, "wallpaper/deleteWallpaper", DeleteWallpaper);
            bus.Register(Service, "wallpaper/refresh", c => c.Reply(Bus.Ok()));
        }

        /// The webOS tree, for the wallpaper store.
        public WebosFs Fs;
        /// The ROM (usr/palm/ext-timezones.json).
        public string Rom;
        /// A stored preference changed (the shell draws a new wallpaper). On the bus's thread.
        public event Action<string, object> PreferenceChanged;

        /// The wallpaper Lunacy shows until one is picked, as AndroidLuna's (Luna.DEFAULT_WALLPAPER).
        public const string DefaultWallpaper = "22.jpg";
        private const string WallpaperDir = "/media/internal/.wallpapers";
        private const int Thumb = 160;

        // ---- the shipped media (SharedMedia.kt) ----

        /// The shipped wallpapers into /media/internal/wallpapers (Pictures\webOS Wallpapers) and
        /// ringtones into /media/internal/ringtones, each once: one a person deletes stays deleted.
        /// After UserFiles.Open, off the UI thread.
        public void InstallMedia(string lunaAssets)
        {
            Install(Path.Combine(lunaAssets, "wallpapers"), "/media/internal/wallpapers", "wallpapers", n => n.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
            Install(Path.Combine(lunaAssets, "ringtones"), "/media/internal/ringtones", "ringtones", n => n.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase));
        }

        private void Install(string from, string to, string what, Func<string, bool> wanted)
        {
            var settings = Windows.Storage.ApplicationData.Current.LocalSettings;
            var key = "installed-" + what;
            if (settings.Values.ContainsKey(key) || !Directory.Exists(from)) return;
            int n = 0;
            try
            {
                if (!Fs.Exists(to)) Fs.MakeDir(to, true);
                foreach (var file in Directory.EnumerateFiles(from).Where(x => wanted(Path.GetFileName(x))))
                {
                    var dest = to + "/" + Path.GetFileName(file);
                    if (Fs.Exists(dest)) continue;
                    Fs.Write(dest, File.ReadAllBytes(file), false);
                    n++;
                }
                settings.Values[key] = true;
                LunaLog.Write("[systemservice] installed " + n + " " + what + " into " + to);
            }
            catch (Exception e) { LunaLog.Write("[systemservice] can't install " + what + ": " + e.Message); }
        }

        // ---- wallpapers ----

        /// A wallpaper is named by its file name in the store; anything with a path in it names none.
        private static bool GoodName(string name) { return name.Length > 0 && name != "." && name != ".." && !name.Contains("/") && !name.Contains("\\"); }

        /// The wallpaper object for a name already in the store, or null.
        public JObj Wallpaper(string name)
        {
            if (!GoodName(name) || !Fs.Exists(WallpaperDir + "/" + name) || Fs.IsDir(WallpaperDir + "/" + name)) return null;
            return new JObj().Put("wallpaperName", name).Put("wallpaperFile", WallpaperDir + "/" + name).Put("wallpaperThumbFile", WallpaperDir + "/thumbs/" + name);
        }

        /// The webOS path of the wallpaper being shown: the preference's file, or the default.
        public string WallpaperPath()
        {
            var w = Get("wallpaper") as JObj;
            var path = w == null ? "" : w.Str("wallpaperFile");
            return path.Length > 0 ? path : "/media/internal/wallpapers/" + DefaultWallpaper;
        }

        private void WallpaperInfo(Bus.Call c)
        {
            var name = Jx.ObjOrEmpty(c.Raw).Str("wallpaperName");
            var w = Wallpaper(name);
            c.Reply(w == null ? Bus.Error("No such wallpaper: " + name) : new JObj().Put("returnValue", true).Put("wallpaper", w).ToString());
        }

        private void DeleteWallpaper(Bus.Call c)
        {
            var name = Jx.ObjOrEmpty(c.Raw).Str("wallpaperName");
            if (Wallpaper(name) == null) { c.Reply(Bus.Error("No such wallpaper: " + name)); return; }
            try
            {
                Fs.Remove(WallpaperDir + "/" + name, false, false);
                if (Fs.Exists(WallpaperDir + "/thumbs/" + name)) Fs.Remove(WallpaperDir + "/thumbs/" + name, false, false);
            }
            catch (WebosFs.FsError e) { c.Reply(Bus.Error("deleteWallpaper: " + e.Message)); return; }
            c.Reply(Bus.Ok());
        }

        /// Copies a picked image into the wallpaper store and makes its thumbnail, as webOS did.
        /// `target` is the picked file's path, URL-encoded (Screen & Lock sends it that way).
        private void ImportWallpaper(Bus.Call c)
        {
            var raw = Jx.ObjOrEmpty(c.Raw).Str("target");
            if (raw.Length == 0) { c.Reply(Bus.Error("importWallpaper: target is required")); return; }
            string path;
            try { path = Uri.UnescapeDataString(raw); } catch (Exception) { path = raw; }
            if (path.StartsWith("file://", StringComparison.Ordinal)) path = path.Substring(7);
            System.Threading.Tasks.Task.Run(() =>
            {
                string reply;
                try
                {
                    if (!path.StartsWith("/", StringComparison.Ordinal) || !Fs.Exists(path) || Fs.IsDir(path)) { c.Reply(Bus.Error("importWallpaper: can't read " + path)); return; }
                    if (!Fs.Exists(WallpaperDir + "/thumbs")) Fs.MakeDir(WallpaperDir + "/thumbs", true);
                    var source = WebosFs.Normal(path);
                    var name = UniqueName(source);
                    var dest = WallpaperDir + "/" + name;
                    var data = Fs.Read(source);
                    // Already in the store: keep one copy.
                    if (source != dest) Fs.Write(dest, data, false);
                    try { Fs.Write(WallpaperDir + "/thumbs/" + name, Images.Wait(Images.CoverThumbnail(data, Thumb)), false); }
                    catch (Exception e) { LunaLog.Write("[systemservice] thumbnail of " + name + " failed: " + e.Message); }
                    reply = new JObj().Put("returnValue", true).Put("wallpaper", Wallpaper(name)).ToString();
                }
                catch (Exception e)
                {
                    LunaLog.Write("[systemservice] importWallpaper failed: " + e.Message);
                    reply = Bus.Error("importWallpaper: " + e.Message);
                }
                c.Reply(reply);
            });
        }

        /// The picked file's own name, unless the store already has a different file by it:
        /// then name-2, name-3 and so on, so an import never writes over a wallpaper.
        private string UniqueName(string source)
        {
            var name = source.Substring(source.LastIndexOf('/') + 1);
            var taken = WallpaperDir + "/" + name;
            if (!Fs.Exists(taken) || taken == source) return name;
            int dot = name.LastIndexOf('.');
            var b = dot > 0 ? name.Substring(0, dot) : name;
            var ext = dot > 0 ? name.Substring(dot) : "";
            int n = 2;
            while (Fs.Exists(WallpaperDir + "/" + b + "-" + n + ext)) n++;
            return b + "-" + n + ext;
        }

        // ---- Exchange time zones ----

        /// timezone/getTimeZoneFromEasData: the zone an Exchange server's time zone (Windows'
        /// TIME_ZONE_INFORMATION: bias in minutes west of UTC, and the two changes as "the nth
        /// weekday of a month") stands for. mojomail-eas asks it for every calendar event it
        /// syncs, and a failed answer fails the whole collection's sync.
        ///
        /// As luna-sysservice does it (TimeZoneService::cbGetTimeZoneFromEasData): the candidates
        /// are the zones of /usr/palm/ext-timezones.json's "timeZone" list whose offsetFromUTC is
        /// -bias, in the file's order. Unless both dates are whole, the first candidate is the
        /// answer (or the first whose clock changes fall in the months named: byChangeMonths, as
        /// AndroidLuna's); otherwise the first whose daylight saving this year starts and ends at
        /// those instants. Measured on the reference TouchPad (LunaCore's SystemService.kt).
        private string TimeZoneFromEasData(JObj p)
        {
            var bias = p["bias"];
            if (!(bias is long)) return Fail("bias value missing");
            int offset = -(int)(long)bias;
            var zones = EasZones();
            var candidates = zones.Where(z => z.Value == offset).Select(z => z.Key).ToList();
            if (candidates.Count == 0)
                candidates = zones.Select(z => z.Key).Where(id => { var r = Rules(id, DateTime.UtcNow.Year); return r != null && r.Long("utcOffset") == offset * 60; }).ToList();
            if (candidates.Count == 0) return Fail("Failed to find any timezones with specified bias value");
            Func<string, string> ok = tz => new JObj().Put("returnValue", true).Put("timeZone", tz).ToString();
            var standard = EasDate(p.Obj("standardDate"));
            var daylight = EasDate(p.Obj("daylightDate"));
            if (standard == null || daylight == null) return ok(ByChangeMonths(candidates, offset, p) ?? candidates[0]);
            int year = DateTime.Now.Year;
            int stdDay = EasDayOfMonth(standard, year), dstDay = EasDayOfMonth(daylight, year);
            foreach (var id in candidates)
            {
                var rules = Rules(id, year);
                if (rules == null || !rules.Bool("hasDstChange")) continue;
                // mktime with tm_isdst forced: the standard date read as daylight time, the daylight date as standard time.
                long end = WallClock(year, standard, stdDay) - rules.Long("dstOffset");
                long start = WallClock(year, daylight, dstDay) - rules.Long("utcOffset");
                if (end == rules.Long("dstEnd") && start == rules.Long("dstStart")) return ok(id);
            }
            return Fail("Failed to find any timezones with specified parametes");
        }

        /// One zone's year by Windows' zone data, or null for a zone it doesn't know.
        private static JObj Rules(string id, int year)
        {
            var cal = new Calendar();
            try { cal.ChangeTimeZone(id); cal.ChangeClock(ClockIdentifiers.TwentyFourHour); } catch (Exception) { return null; }
            return YearRules(cal, id, year);
        }

        /// The first candidate that changes its clocks in the months the server named, with the
        /// offset it has now, or that doesn't change them if the server says so. Null when the
        /// request names no months, or nothing matches.
        private static string ByChangeMonths(List<string> candidates, int offset, JObj p)
        {
            var d = p.Obj("daylightDate"); var s = p.Obj("standardDate");
            if (d == null || s == null) return null;
            int into = d.Has("month") ? d.Int("month", -1) : -1, out_ = s.Has("month") ? s.Int("month", -1) : -1;
            if (into < 0 || into > 12 || out_ < 0 || out_ > 12) return null;
            int year = DateTime.Now.Year;
            foreach (var id in candidates)
            {
                var rules = Rules(id, year);
                if (rules == null || rules.Long("utcOffset") != offset * 60) continue;
                if (into == 0 || out_ == 0) { if (!rules.Bool("hasDstChange")) return id; continue; }
                if (rules.Bool("hasDstChange") && MonthOf(rules.Long("dstStart"), id) == into && MonthOf(rules.Long("dstEnd"), id) == out_) return id;
            }
            return null;
        }

        private static int MonthOf(long seconds, string id)
        {
            var cal = new Calendar();
            cal.ChangeTimeZone(id);
            cal.SetDateTime(DateTimeOffset.FromUnixTimeSeconds(seconds));
            return cal.Month;
        }

        private List<KeyValuePair<string, int>> easZones;

        /// ext-timezones.json's "timeZone" list, as (ZoneID, offsetFromUTC) in the file's order.
        private List<KeyValuePair<string, int>> EasZones()
        {
            if (easZones != null) return easZones;
            var list = new List<KeyValuePair<string, int>>();
            try
            {
                var all = Jx.ParseObj(File.ReadAllText(Path.Combine(Rom, "usr", "palm", "ext-timezones.json"))).Arr("timeZone") ?? new JArr();
                foreach (var z in all.OfType<JObj>())
                {
                    var id = z["ZoneID"] as string;
                    if (id == null || !Jx.IsNumber(z["offsetFromUTC"]) || !z.Has("supportsDST")) continue;
                    list.Add(new KeyValuePair<string, int>(id, (int)Jx.Num(z["offsetFromUTC"])));
                }
            }
            catch (Exception e) { LunaLog.Write("[systemservice] no ext-timezones.json: " + e.Message); }
            return easZones = list;
        }

        /// An EAS date's month, dayOfWeek, week ("day", 5 = the last), hour, minute and second, or null unless whole.
        private static int[] EasDate(JObj o)
        {
            if (o == null) return null;
            var v = new List<int>();
            foreach (var k in new[] { "year", "month", "dayOfWeek", "day", "hour", "minute", "second" })
            {
                if (!(o[k] is long)) return null;
                v.Add((int)(long)o[k]);
            }
            if (v[1] < 1 || v[1] > 12 || v[2] < 0 || v[2] > 6 || v[3] < 1 || v[3] > 5 || v[4] < 0 || v[4] > 59 || v[5] < 0 || v[5] > 59 || v[6] < 0 || v[6] > 59) return null;
            return new[] { v[1], v[2], v[3], v[4], v[5], v[6] };
        }

        /// Seconds since the epoch of the date's wall-clock time this year, read as UTC.
        private static long WallClock(int year, int[] d, int day)
        {
            return new DateTimeOffset(year, d[0], day, d[3], d[4], d[5], TimeSpan.Zero).ToUnixTimeSeconds();
        }

        /// The day of the month an EAS date's "nth weekday" falls on (updateEasDateDayOfMonth):
        /// d is month, dayOfWeek (0 = Sunday), week (5 = the last).
        internal static int EasDayOfMonth(int[] d, int year)
        {
            int firstWday = (int)new DateTime(year, d[0], 1).DayOfWeek;
            int last = DateTime.DaysInMonth(year, d[0]);
            int first = 1 + (d[1] - firstWday + 7) % 7;
            int day = first + 7 * (d[2] - 1);
            return day > last ? day - 7 : day;
        }

        // ---- the time ----

        /// time/getSystemTime: the clock, the zone and the offset, in the reference TouchPad's
        /// shape. Mojo asks for it while starting, with a subscription.
        private void SystemTime(Bus.Call call)
        {
            call.Reply(TimeReply());
            if (call.Subscribe && !call.Cancelled)
            {
                lock (gate) timeSubscribers.Add(call);
                call.OnCancel(() => { lock (gate) timeSubscribers.Remove(call); });
            }
        }

        /// The shell calls this when Windows says the time or the zone changed.
        public void TimeChanged()
        {
            var reply = TimeReply();
            List<Bus.Call> subs;
            lock (gate) subs = timeSubscribers.ToList();
            foreach (var c in subs) c.Reply(reply);
        }

        private static string TimeReply()
        {
            var now = DateTimeOffset.Now;
            var cal = new Calendar();
            var local = new JObj().Put("year", now.Year).Put("month", now.Month).Put("day", now.Day)
                .Put("hour", now.Hour).Put("minute", now.Minute).Put("second", now.Second);
            string abbr;
            try { abbr = cal.TimeZoneAsString(3); } catch (Exception) { abbr = ""; }
            return new JObj().Put("returnValue", true).Put("utc", now.ToUnixTimeSeconds()).Put("localtime", local)
                // webOS reports the offset in minutes east of UTC, as -420 for Pacific daylight.
                .Put("offset", (long)now.Offset.TotalMinutes)
                .Put("timezone", cal.GetTimeZone()).Put("TZ", abbr)
                .Put("timeZoneFile", "/var/luna/preferences/localtime").ToString();
        }

        /// timezone/getTimeZoneRules: each zone's offsets and daylight saving dates for the years
        /// asked, which Calendar's library converts event times with. The payload is an array,
        /// [{"tz":"America/New_York","years":[2026]}]. A zone Windows doesn't know is left out and
        /// the rest answered; only when nothing is known does the call fail. Windows' own zone
        /// data (WinRT's Calendar, which takes IANA names) decides the offsets, as Android's does
        /// there; each year carries Lunacy's firstOffset and changes too (LunaRuntimes/calendar).
        private static string TimeZoneRules(string raw)
        {
            object parsed;
            try { parsed = Jx.Parse(raw); } catch (FormatException) { return Fail("json root needs to be an array"); }
            var asked = parsed as JArr;
            if (asked == null) return Fail("json root needs to be an array");
            if (asked.Count == 0) return Fail(NoRules);
            var results = new JArr();
            foreach (var e in asked)
            {
                var entry = e as JObj;
                if (entry == null || !(entry["tz"] is string)) return Fail("Missing tz entry");
                var id = entry.Str("tz");
                var cal = new Calendar();
                try { cal.ChangeTimeZone(id); cal.ChangeClock(ClockIdentifiers.TwentyFourHour); } catch (Exception) { continue; }
                var years = entry.Arr("years");
                var list = years == null || years.Count == 0 ? new List<int> { DateTime.Now.Year } : years.Select(y => (int)Jx.Num(y)).ToList();
                foreach (var y in list) results.Add(YearRules(cal, id, y));
            }
            if (results.Count == 0) return Fail(NoRules);
            return new JObj().Put("returnValue", true).Put("results", results).ToString();
        }

        /// The zone's offset at an instant, in seconds east of UTC.
        private static long OffsetAt(Calendar cal, long unixSeconds)
        {
            // The calendar keeps a 24-hour clock (YearRules), so Hour is 0 to 23.
            var t = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
            cal.SetDateTime(t);
            var wall = new DateTime(cal.Year, cal.Month, cal.Day, cal.Hour, cal.Minute, cal.Second, DateTimeKind.Unspecified);
            return (long)(wall - t.UtcDateTime).TotalSeconds;
        }

        private static JObj YearRules(Calendar cal, string id, int year)
        {
            long jan1 = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
            long next = new DateTimeOffset(year + 1, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
            // The year as the zone sees it: from its own midnight on 1 January to the next.
            long from = jan1 - OffsetAt(cal, jan1), to = next - OffsetAt(cal, next);
            long first = OffsetAt(cal, from);
            var out_ = new JObj().Put("tz", id).Put("year", year).Put("firstOffset", first);
            var changes = new JArr();
            const long Day = 86400;
            for (long t = from; t < to; t += Day)
            {
                long n = Math.Min(t + Day, to);
                long a = OffsetAt(cal, t);
                if (a == OffsetAt(cal, n)) continue;
                // The last second of the old rule is lo; the change happens at hi.
                long lo = t, hi = n;
                while (hi - lo > 1) { long mid = (lo + hi) / 2; if (OffsetAt(cal, mid) == a) lo = mid; else hi = mid; }
                changes.Add(new JArr().Put(hi).Put(OffsetAt(cal, hi)));
            }
            out_.Put("changes", changes);
            // Daylight saving is the higher of a year's two offsets, which it changes into and out of.
            var offsets = changes.Select(c => (long)((JArr)c)[1]).Concat(new[] { first }).Distinct().ToList();
            if (changes.Count < 2 || offsets.Count != 2)
                return out_.Put("hasDstChange", false).Put("utcOffset", first).Put("dstOffset", -1).Put("dstStart", -1).Put("dstEnd", -1);
            long dst = offsets.Max(), std = offsets.Min();
            long dstStart = -1, dstEnd = -1;
            foreach (JArr c in changes)
            {
                if ((long)c[1] == dst && dstStart < 0) dstStart = (long)c[0];
                if ((long)c[1] == std && dstEnd < 0) dstEnd = (long)c[0];
            }
            return out_.Put("hasDstChange", true).Put("utcOffset", std).Put("dstOffset", dst).Put("dstStart", dstStart).Put("dstEnd", dstEnd);
        }

        // ---- preferences ----

        /// Preferences Windows owns, answered from its settings when nothing has set them here:
        /// locale, region and timeFormat, and the device profile's x_palm_carrier, in the reference
        /// TouchPad's shape. Services that say they are globalized read locale as they start (the
        /// accounts service does), and stop without it.
        private object HostPreference(string key)
        {
            var lang = new Language(GlobalizationPreferences.Languages.FirstOrDefault() ?? "en-US");
            var country = (GlobalizationPreferences.HomeGeographicRegion ?? "US").ToLowerInvariant();
            string countryName;
            try { countryName = new GeographicRegion(country.ToUpperInvariant()).DisplayName; } catch (Exception) { countryName = country.ToUpperInvariant(); }
            var region = new JObj().Put("countryCode", country).Put("countryName", countryName);
            switch (key)
            {
                case "locale":
                    return new JObj().Put("languageCode", lang.LanguageTag.Split('-')[0].ToLowerInvariant()).Put("countryCode", country).Put("phoneRegion", region);
                case "region": return region;
                case "timeFormat": return GlobalizationPreferences.Clocks.FirstOrDefault() == ClockIdentifiers.TwentyFourHour ? "HH24" : "HH12";
                case "x_palm_carrier": return profile.CarrierCode;
                // A device always has a wallpaper preference: its factory default until one is
                // picked, with no thumbnail (/etc/palm/defaultPreferences.txt). Email's compose
                // window draws it behind the message.
                case "wallpaper":
                    return new JObj().Put("wallpaperName", DefaultWallpaper).Put("wallpaperFile", "/media/internal/wallpapers/" + DefaultWallpaper).Put("wallpaperThumbFile", "");
                default: return null;
            }
        }

        /// The current value of a key, or null.
        public object Get(string key)
        {
            lock (gate) { if (prefs.Has(key)) return Jx.Clone(prefs[key]); }
            return HostPreference(key);
        }

        private void GetPreferences(Bus.Call call)
        {
            var p = Jx.ObjOrEmpty(call.Params.Stringify());
            var wanted = (p.Arr("keys") ?? new JArr()).OfType<string>().ToList();
            call.Reply(Reply(wanted));
            if (call.Subscribe && !call.Cancelled)
            {
                var entry = new KeyValuePair<Bus.Call, List<string>>(call, wanted);
                lock (gate) subscribers.Add(entry);
                call.OnCancel(() => { lock (gate) subscribers.Remove(entry); });
            }
        }

        /// Only the keys that exist, as the TouchPad answers.
        private string Reply(IEnumerable<string> keys)
        {
            var out_ = new JObj();
            foreach (var k in keys) { var v = Get(k); if (v != null) out_.Put(k, v); }
            return out_.Put("returnValue", true).ToString();
        }

        private void SetPreferences(Bus.Call call)
        {
            var p = Jx.ObjOrEmpty(call.Params.Stringify());
            var changed = p.Keys.Where(k => k != "subscribe").ToList();
            if (changed.Count == 0) { call.Reply(Bus.Error("setPreferences: nothing to set")); return; }
            string text;
            lock (gate)
            {
                foreach (var k in changed) prefs.Put(k, p[k]);
                text = prefs.ToString();
            }
            try { File.WriteAllText(store + ".new", text); if (File.Exists(store)) File.Delete(store); File.Move(store + ".new", store); }
            catch (Exception e) { LunaLog.Write("[systemservice] can't save preferences: " + e.Message); }
            call.Reply(Bus.Ok());
            var h = PreferenceChanged;
            if (h != null) foreach (var key in changed) { try { h(key, Get(key)); } catch (Exception e) { LunaLog.Write("[systemservice] " + key + ": " + e.Message); } }
            // A subscriber hears about a key it asked for, as webOS's does.
            List<KeyValuePair<Bus.Call, List<string>>> subs;
            lock (gate) subs = subscribers.ToList();
            foreach (var s in subs)
            {
                var keys = s.Value.Where(changed.Contains).ToList();
                if (keys.Count > 0) s.Key.Reply(Reply(keys));
            }
        }

        private const string NoRules = "Failed to find any timezones";
        private static string Fail(string text) { return new JObj().Put("returnValue", false).Put("errorText", text).ToString(); }
    }
}
