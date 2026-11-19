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
    /// getPreferences returns only the keys that exist.
    ///
    /// Not here yet: the wallpaper store (wallpaper/*) and timezone/getTimeZoneFromEasData,
    /// which wait for the apps that use them (Screen & Lock, Exchange mail).
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
