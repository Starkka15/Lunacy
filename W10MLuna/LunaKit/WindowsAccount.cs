using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.Appointments;
using Windows.ApplicationModel.Contacts;
using Windows.Foundation.Collections;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;
using Windows.Storage;
using Windows.Storage.Streams;

namespace LunaKit
{
    /// The "Android" account's two sides on Windows 10 Mobile (AndroidLuna's AndroidAccountSide,
    /// AndroidContacts and AndroidCalendar): Windows' People and Calendar stores, every account's,
    /// copied one way into db8 while the account's capability is on. The account keeps its id,
    /// service and kinds, which Contacts, Calendar and the accounts service know it by; what the
    /// person sees is named for Windows (WindowsAccountTemplate, the setup page's hostName).
    ///
    /// Nothing is ever written to Windows. Counts only in the log: these are people's details.
    internal abstract class AccountSide
    {
        public const string Service = "org.webosarchive.lunacy.androidaccount";
        private const string SyncStateKind = "com.palm.account.syncstate:1";
        private const string AccountsService = "com.palm.service.accounts";
        private const int ChangeDelayMs = 3000, RemovalDelayMs = 8000, WaitStepMs = 500, MaxWaitMs = 120000;

        protected readonly LunaShell shell;
        protected readonly Db8 db;
        private readonly string capabilityProvider, settingsName;
        private readonly int startDelayMs;
        /// A side whose passes go first: Calendar waits for Contacts, which are quick, so the
        /// person sees something arrive (AndroidAccountSide's `after`).
        private readonly AccountSide after;
        private readonly Timer timer, removal;
        private readonly SemaphoreSlim running = new SemaphoreSlim(1);
        private volatile bool busy;
        private DateTime waitingSince = DateTime.MinValue;
        private string initialFor, removalFor;
        private bool watching;
        private Bus bus;

        protected AccountSide(LunaShell shell, Db8 db, string settingsName, string capabilityProvider, int startDelayMs, AccountSide after)
        {
            this.shell = shell; this.db = db; this.settingsName = settingsName;
            this.capabilityProvider = capabilityProvider; this.startDelayMs = startDelayMs; this.after = after;
            timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
            removal = new Timer(_ => { var id = removalFor; removalFor = null; if (id != null) Guarded("remove", () => Remove(id)); }, null, Timeout.Infinite, Timeout.Infinite);
        }

        /// One pass: Windows' data into db8.
        protected abstract Task Sync();
        /// The account `id`'s copy taken out of db8.
        protected abstract void Remove(string id);
        /// Windows' store, or null when Lunacy may not read it (Settings > Privacy).
        protected abstract Task<bool> Open();
        /// Windows' change notices on or off.
        protected abstract void Watch(bool on);
        protected abstract string Name { get; }

        protected IPropertySet Settings { get { return ApplicationData.Current.LocalSettings.CreateContainer(settingsName, ApplicationDataCreateDisposition.Always).Values; } }
        protected string AccountId() { object v; return Settings.TryGetValue("accountId", out v) ? v as string : null; }

        /// What Windows said the last time it was asked: permissions/status reads this.
        public bool Granted { get { object v; return Settings.TryGetValue("granted", out v) && v is bool && (bool)v; } }
        public bool Asked { get { return Settings.ContainsKey("granted"); } }
        protected void SetGranted(bool on) { Settings["granted"] = on; }

        /// First Use and the setup page's question: Windows has no dialog for these, only its
        /// privacy settings, so asking is opening the store and seeing whether it answers.
        public async Task<bool> Request()
        {
            bool ok;
            try { ok = await Open(); if (!ok) LunaLog.Write("[" + Name + "] Windows gave no store"); }
            catch (Exception e) { ok = false; LunaLog.Write("[" + Name + "] Windows refused: " + e.GetType().Name + ": " + e.Message); }
            SetGranted(ok);
            return ok;
        }

        protected void RegisterAccount(Bus bus, string enabledMethod, string deleteMethod)
        {
            this.bus = bus;
            bus.Register(Service, enabledMethod, OnEnabled);
            bus.Register(Service, deleteMethod, OnDelete);
            if (AccountId() != null) { StartWatching(); Schedule(startDelayMs); }
        }

        /// The accounts service, as the capability is turned on or off. It always gets true: a
        /// failed answer leaves its chain of calls waiting, with the account stuck.
        private void OnEnabled(Bus.Call c)
        {
            if (c.AppId != AccountsService) { c.Reply(Bus.Error("Only the accounts service calls this")); return; }
            var p = Jx.ObjOrEmpty(c.Raw);
            var id = p.Str("accountId");
            if (id.Length == 0) { c.Reply(Bus.Error("accountId is required")); return; }
            if (p.Bool("enabled"))
            {
                Settings["accountId"] = id;
                removalFor = null; removal.Change(Timeout.Infinite, Timeout.Infinite);
                if (Granted) SetSyncState(id, "INITIAL_SYNC");
                initialFor = id;
                StartWatching();
                Schedule(after != null ? ChangeDelayMs : 0);
            }
            else Stop(id);
            c.Reply(Bus.Ok());
        }

        private void OnDelete(Bus.Call c)
        {
            if (c.AppId != AccountsService) { c.Reply(Bus.Error("Only the accounts service calls this")); return; }
            var p = Jx.ObjOrEmpty(c.Raw);
            var id = p.Str("accountId");
            removalFor = null; removal.Change(Timeout.Infinite, Timeout.Infinite);
            // Remove Account's "keep data" box: the copy stays, as it is, with no account.
            if (id.Length > 0 && !p.Bool("keepData")) Guarded("remove", () => Remove(id));
            c.Reply(Bus.Ok());
        }

        /// Off: no more copying, and the copy goes a moment later, unless onDelete says to keep it.
        private void Stop(string id)
        {
            Settings.Remove("accountId");
            timer.Change(Timeout.Infinite, Timeout.Infinite);
            busy = false;
            if (initialFor != null) { initialFor = null; ClearSyncState(id, null); }
            StopWatching();
            removalFor = id;
            removal.Change(RemovalDelayMs, Timeout.Infinite);
        }

        protected void Schedule(int ms) { busy = true; timer.Change(ms, Timeout.Infinite); }

        private void Tick()
        {
            if (after != null && after.busy)
            {
                if (waitingSince == DateTime.MinValue) waitingSince = DateTime.UtcNow;
                if ((DateTime.UtcNow - waitingSince).TotalMilliseconds < MaxWaitMs) { timer.Change(WaitStepMs, Timeout.Infinite); return; }
            }
            waitingSince = DateTime.MinValue;
            if (!running.Wait(0)) { timer.Change(ChangeDelayMs, Timeout.Infinite); return; }
            try { Sync().GetAwaiter().GetResult(); }
            catch (Exception e) { LunaLog.Write("[" + Name + "] pass failed: " + e.GetType().Name + ": " + e.Message); }
            finally { busy = false; running.Release(); }
            // A first pass's records are in db8's queue: its sync state ends once they're written.
            var first = initialFor;
            if (first != null) { initialFor = null; db.After(() => ClearSyncState(first, null)); }
        }

        private void Guarded(string what, Action f)
        {
            Task.Run(() =>
            {
                running.Wait();
                try { f(); } catch (Exception e) { LunaLog.Write("[" + Name + "] " + what + " failed: " + e.Message); }
                finally { running.Release(); }
            });
        }

        protected void StartWatching()
        {
            if (watching) return;
            watching = true;
            try { Watch(true); } catch (Exception e) { LunaLog.Write("[" + Name + "] can't watch Windows' changes: " + e.Message); }
        }

        private void StopWatching()
        {
            if (!watching) return;
            watching = false;
            try { Watch(false); } catch (Exception) { }
        }

        /// Windows sends a burst of changes while an account syncs: one pass, once it settles.
        protected void Changed() { if (AccountId() != null) Schedule(ChangeDelayMs); }

        /// The side's com.palm.account.syncstate:1 record in tempdb, as webOS's transports wrote
        /// it: the accounts list and Sync Status read it.
        private void SetSyncState(string accountId, string state)
        {
            var record = new JObj().Put("_kind", SyncStateKind).Put("accountId", accountId).Put("capabilityProvider", capabilityProvider)
                .Put("busAddress", Service).Put("collectionId", JNull.Value).Put("syncState", state);
            ClearSyncState(accountId, () => bus.DispatchPrivate(Service, "palm://com.palm.tempdb/put", new JObj().Put("objects", new JArr().Put(record)).ToString(), _ => { }));
        }

        private void ClearSyncState(string accountId, Action then)
        {
            if (bus == null) return;
            var where = new JArr()
                .Put(new JObj().Put("prop", "accountId").Put("op", "=").Put("val", accountId))
                .Put(new JObj().Put("prop", "capabilityProvider").Put("op", "=").Put("val", capabilityProvider));
            bus.DispatchPrivate(Service, "palm://com.palm.tempdb/del", new JObj().Put("query", new JObj().Put("from", SyncStateKind).Put("where", where)).ToString(), _ => { if (then != null) then(); });
        }

        /// A kind of the account's own, extending webOS's, with its accountId index (KindSpec.kind).
        protected static JObj Kind(string id, string extends, params string[] indexes)
        {
            var ix = new JArr();
            foreach (var n in indexes) ix.Put(new JObj().Put("name", n).Put("props", new JArr().Put(new JObj().Put("name", n))));
            return new JObj().Put("id", id).Put("owner", Service).Put("indexes", ix).Put("extends", new JArr().Put(extends));
        }
    }

    /// Windows' People store as the account's Contacts side, into org.webosarchive.contact.android:1.
    internal sealed class WindowsContacts : AccountSide
    {
        public const string KindId = "org.webosarchive.contact.android:1";
        private const string PhotoDir = "/media/internal/.contactphotos";
        private const string PhotoPrefix = "windows-";
        private const int Big = 196, Square = 50;
        private ContactStore store;

        public WindowsContacts(LunaShell shell) : base(shell, shell.Db, "windowscontacts", "org.webosarchive.lunacy.androidaccount.contacts", 5000, null) { }
        protected override string Name { get { return "windowscontacts"; } }

        public void Attach(Bus bus)
        {
            db.Configure(new List<JObj> { Kind(KindId, "com.palm.contact:1", "accountId") }, new List<JArr>());
            RegisterAccount(bus, "onEnabled", "onDelete");
        }

        protected override async Task<bool> Open()
        {
            if (store != null) return true;
            store = await ContactManager.RequestStoreAsync(ContactStoreAccessType.AllContactsReadOnly);
            return store != null;
        }

        protected override void Watch(bool on)
        {
            if (store == null) return;
            if (on) { try { store.ChangeTracker.Enable(); } catch (Exception) { } store.ContactChanged += OnChanged; }
            else store.ContactChanged -= OnChanged;
        }
        private void OnChanged(ContactStore s, ContactChangedEventArgs e) { Changed(); }

        protected override void Remove(string id)
        {
            db.SyncAccount(KindId, id, new List<JObj>(), (a, u, r) => LunaLog.Write("[windowscontacts] removed " + r));
            CleanPhotos(new HashSet<string>());
        }

        protected override async Task Sync()
        {
            var id = AccountId();
            if (id == null) return;
            bool ok;
            try { ok = await Open(); } catch (Exception) { ok = false; }
            SetGranted(ok);
            if (!ok) { LunaLog.Write("[windowscontacts] Windows won't let Lunacy read its contacts; the copy stays as it was"); return; }
            StartWatching();
            var wanted = new HashSet<string>();
            var records = new List<JObj>();
            var reader = store.GetContactReader();
            for (var batch = await reader.ReadBatchAsync(); batch.Contacts.Count > 0; batch = await reader.ReadBatchAsync())
            {
                if (batch.Status != ContactBatchStatus.Success) { LunaLog.Write("[windowscontacts] Windows stopped reading: " + batch.Status + "; the copy stays as it was"); return; }
                foreach (var c in batch.Contacts) records.Add(await Record(c, wanted));
            }
            if (AccountId() != id) return;
            var n = records.Count;
            db.SyncAccount(KindId, id, records, (a, u, r) => LunaLog.Write("[windowscontacts] " + n + " contacts; added " + a + ", changed " + u + ", removed " + r));
            CleanPhotos(wanted);
        }

        private static string T(string s) { return (s ?? "").Trim(); }

        /// Every property, always, even when empty: db8 merges an update into the record, so one
        /// left out would keep its old value after it was removed in Windows.
        private async Task<JObj> Record(Contact c, HashSet<string> wanted)
        {
            var seen = new HashSet<string>();
            Func<JArr, string, JObj, bool> add = (to, what, v) => { if (seen.Add(what + "|" + v)) to.Put(v); return true; };
            Func<string, string, JObj> line = (v, type) => new JObj().Put("value", v).Put("type", type).Put("primary", false);

            var phones = new JArr();
            foreach (var p in c.Phones) if (T(p.Number).Length > 0) add(phones, "phone", line(T(p.Number), PhoneType(p.Kind)));
            var emails = new JArr();
            foreach (var e in c.Emails) if (T(e.Address).Length > 0)
                    add(emails, "email", line(T(e.Address), e.Kind == ContactEmailKind.Personal ? "type_home" : e.Kind == ContactEmailKind.Work ? "type_work" : "type_other"));
            var addresses = new JArr();
            foreach (var a in c.Addresses)
            {
                if (T(a.StreetAddress).Length == 0 && T(a.Locality).Length == 0) continue;
                add(addresses, "address", new JObj().Put("streetAddress", T(a.StreetAddress)).Put("locality", T(a.Locality)).Put("region", T(a.Region))
                    .Put("postalCode", T(a.PostalCode)).Put("country", T(a.Country))
                    .Put("type", a.Kind == ContactAddressKind.Home ? "type_home" : a.Kind == ContactAddressKind.Work ? "type_work" : "type_other").Put("primary", false));
            }
            var orgs = new JArr();
            foreach (var j in c.JobInfo)
            {
                if (T(j.CompanyName).Length == 0 && T(j.Title).Length == 0) continue;
                add(orgs, "organization", new JObj().Put("name", T(j.CompanyName)).Put("department", T(j.Department)).Put("title", T(j.Title))
                    .Put("type", "").Put("startDate", "").Put("endDate", "").Put("description", ""));
            }
            var urls = new JArr();
            foreach (var w in c.Websites)
            {
                var v = T(w.RawValue);
                if (v.Length == 0 && w.Uri != null) v = w.Uri.OriginalString;
                if (v.Length > 0) add(urls, "url", line(v, "type_other"));
            }
            var ims = new JArr();
            foreach (var s in c.ConnectedServiceAccounts)
                if (T(s.Id).Length > 0) add(ims, "im", new JObj().Put("value", T(s.Id)).Put("type", ImType(s.ServiceName)).Put("label", "").Put("primary", false));
            var relations = new JArr();
            foreach (var o in c.SignificantOthers) if (T(o.Name).Length > 0) add(relations, "relation", line(T(o.Name), RelationType(o.Relationship)));
            string birthday = "", anniversary = "";
            foreach (var d in c.ImportantDates)
            {
                if (!d.Month.HasValue || !d.Day.HasValue) continue;
                // webOS's YYYY-MM-DD, with the year 1900 for none.
                var text = string.Format("{0:0000}-{1:00}-{2:00}", d.Year ?? 1900, d.Month.Value, d.Day.Value);
                if (d.Kind == ContactDateKind.Birthday && birthday.Length == 0) birthday = text;
                else if (d.Kind == ContactDateKind.Anniversary && anniversary.Length == 0) anniversary = text;
            }
            var name = new JObj().Put("givenName", T(c.FirstName)).Put("middleName", T(c.MiddleName)).Put("familyName", T(c.LastName))
                .Put("honorificPrefix", T(c.HonorificNamePrefix)).Put("honorificSuffix", T(c.HonorificNameSuffix));
            var photo = await Photo(c, wanted);
            return new JObj().Put("remoteId", c.Id).Put("displayName", T(c.DisplayName)).Put("name", name)
                .Put("nickname", T(c.Nickname)).Put("note", T(c.Notes)).Put("birthday", birthday).Put("anniversary", anniversary)
                .Put("phoneNumbers", phones).Put("emails", emails).Put("addresses", addresses).Put("organizations", orgs)
                .Put("urls", urls).Put("ims", ims).Put("relations", relations).Put("photos", photo ?? new JArr());
        }

        private static string PhoneType(ContactPhoneKind k)
        {
            switch (k)
            {
                case ContactPhoneKind.Mobile: return "type_mobile";
                case ContactPhoneKind.Home: return "type_home";
                case ContactPhoneKind.Work: return "type_work";
                case ContactPhoneKind.Pager: return "type_pager";
                case ContactPhoneKind.HomeFax: return "type_personal_fax";
                case ContactPhoneKind.BusinessFax: return "type_work_fax";
                case ContactPhoneKind.Company: return "type_company";
                case ContactPhoneKind.Assistant: return "type_assistant";
                case ContactPhoneKind.Radio: return "type_radio";
                default: return "type_other";
            }
        }

        private static string ImType(string service)
        {
            var s = (service ?? "").ToLowerInvariant();
            if (s.Contains("skype")) return "type_skype";
            if (s.Contains("msn") || s.Contains("messenger") || s.Contains("live")) return "type_msn";
            if (s.Contains("yahoo")) return "type_yahoo";
            if (s.Contains("aim") || s.Contains("aol")) return "type_aim";
            if (s.Contains("google") || s.Contains("gtalk") || s.Contains("hangouts")) return "type_gtalk";
            if (s.Contains("icq")) return "type_icq";
            if (s.Contains("jabber") || s.Contains("xmpp")) return "type_jabber";
            if (s.Contains("qq")) return "type_qq";
            return "type_default";
        }

        private static string RelationType(ContactRelationship r)
        {
            switch (r)
            {
                case ContactRelationship.Spouse: return "type_spouse";
                case ContactRelationship.Partner: return "type_partner";
                case ContactRelationship.Parent: return "type_parent";
                case ContactRelationship.Child: return "type_child";
                case ContactRelationship.Sibling: return "type_sibling";
                default: return "type_other";
            }
        }

        /// A contact's picture, cut to the two squares webOS keeps (ContactPhoto's sizes),
        /// centred. The file names carry a hash of the picture, so a pass only cuts one it hasn't seen.
        private async Task<JArr> Photo(Contact c, HashSet<string> wanted)
        {
            if (c.Thumbnail == null) return null;
            try
            {
                byte[] data;
                using (var s = await c.Thumbnail.OpenReadAsync())
                {
                    var buf = new Windows.Storage.Streams.Buffer((uint)s.Size);
                    await s.ReadAsync(buf, (uint)s.Size, InputStreamOptions.None);
                    CryptographicBuffer.CopyToByteArray(buf, out data);
                }
                if (data == null || data.Length == 0) return null;
                var stem = PhotoPrefix + Hash(c.Id) + "-" + Hash(data).Substring(0, 8);
                string big = stem + "-big.jpg", square = stem + "-square.jpg";
                var fs = shell.Fs;
                if (!fs.Exists(PhotoDir + "/" + big) || !fs.Exists(PhotoDir + "/" + square))
                {
                    var b = await Images.CoverThumbnail(data, Big);
                    var q = await Images.CoverThumbnail(data, Square);
                    if (b == null || q == null) return null;
                    if (!fs.Exists(PhotoDir)) fs.MakeDir(PhotoDir, true);
                    WriteNew(PhotoDir + "/" + big, b); WriteNew(PhotoDir + "/" + square, q);
                }
                wanted.Add(big); wanted.Add(square);
                Func<string, string, JObj> entry = (file, type) => new JObj().Put("value", PhotoDir + "/" + file).Put("localPath", PhotoDir + "/" + file).Put("type", type).Put("primary", false);
                return new JArr().Put(entry(square, "type_square")).Put(entry(big, "type_big"));
            }
            catch (Exception e) { LunaLog.Write("[windowscontacts] a photo failed: " + e.GetType().Name); return null; }
        }

        private void WriteNew(string path, byte[] data)
        {
            var tmp = path.Substring(0, path.LastIndexOf('/') + 1) + "." + path.Substring(path.LastIndexOf('/') + 1);
            shell.Fs.Write(tmp, data, false);
            shell.Fs.Rename(tmp, path);
        }

        /// Only this class's files: the folder also holds photos people chose in Contacts.
        private void CleanPhotos(HashSet<string> wanted)
        {
            try
            {
                if (!shell.Fs.Exists(PhotoDir)) return;
                foreach (var n in shell.Fs.List(PhotoDir))
                    if (n.StartsWith(PhotoPrefix, StringComparison.Ordinal) && !wanted.Contains(n)) shell.Fs.Remove(PhotoDir + "/" + n, false, false);
            }
            catch (Exception e) { LunaLog.Write("[windowscontacts] photo cleanup: " + e.Message); }
        }

        private static string Hash(string s) { return Hash(System.Text.Encoding.UTF8.GetBytes(s ?? "")).Substring(0, 16); }
        private static string Hash(byte[] data)
        {
            var h = HashAlgorithmProvider.OpenAlgorithm(HashAlgorithmNames.Sha1).HashData(CryptographicBuffer.CreateFromByteArray(data));
            return CryptographicBuffer.EncodeToHexString(h);
        }
    }

    /// Windows' Calendar store as the account's Calendar side, into
    /// org.webosarchive.calendar.android:1 and org.webosarchive.calendarevent.android:1.
    ///
    /// The events are a window, a year back and two forward, plus anything repeating that reaches
    /// it. A repeat webOS can't hold (several days in one "first weekday of the month") isn't
    /// dropped: its occurrences in the window come from Windows' own expansion, as single events.
    /// Reminders are the owner's to choose (Calendar's preferences: setReminders), as on Android.
    /// Times are the device's zone: Windows gives each appointment as an instant, and its zone
    /// name (a Windows one) isn't one Calendar knows.
    internal sealed class WindowsCalendar : AccountSide
    {
        public const string CalendarKind = "org.webosarchive.calendar.android:1";
        public const string EventKind = "org.webosarchive.calendarevent.android:1";
        private static readonly HashSet<string> Pages = new HashSet<string> { "com.palm.app.calendar", "com.palm.app.contacts", "com.palm.app.accounts", Service };
        private const long DayMs = 86400000L, HourMs = 3600000L;
        private const int DefaultReminder = 15;
        private AppointmentStore store;

        public WindowsCalendar(LunaShell shell, WindowsContacts contacts) : base(shell, shell.Db, "windowscalendar", "org.webosarchive.lunacy.androidaccount.calendar", 6000, contacts) { }
        protected override string Name { get { return "windowscalendar"; } }

        public void Attach(Bus bus)
        {
            db.Configure(new List<JObj> { Kind(CalendarKind, "com.palm.calendar:1", "accountId"), Kind(EventKind, "com.palm.calendarevent:1", "accountId", "calendarId") }, new List<JArr>());
            bus.Register(Service, "getReminders", c => IfPage(c, () => c.Reply(RemindersReply())));
            bus.Register(Service, "setReminders", c => IfPage(c, () =>
            {
                var p = Jx.ObjOrEmpty(c.Raw);
                Settings["reminders"] = !p.Has("on") || p.Bool("on");
                if (AccountId() != null) Schedule(0);
                c.Reply(RemindersReply());
            }));
            bus.Register(Service, "listCalendars", c => IfPage(c, () => Task.Run(async () =>
            {
                try
                {
                    if (!await Open()) { c.Reply(Bus.Error("Windows won't let Lunacy read its calendars")); return; }
                    var ex = Excluded();
                    var list = new JArr();
                    foreach (var cal in await Calendars()) list.Put(new JObj().Put("id", cal.LocalId).Put("name", cal.DisplayName).Put("on", !ex.Contains(cal.LocalId)));
                    c.Reply(new JObj().Put("returnValue", true).Put("calendars", list).ToString());
                }
                catch (Exception e) { c.Reply(Bus.Error("Windows' calendars can't be read: " + e.Message)); }
            })));
            bus.Register(Service, "chooseCalendar", c => IfPage(c, () =>
            {
                var p = Jx.ObjOrEmpty(c.Raw);
                var set = Excluded();
                var id = p.Str("id");
                if (!p.Has("on") || p.Bool("on")) set.Remove(id); else set.Add(id);
                Settings["excluded"] = new JArr(set.Cast<object>()).ToString();
                if (AccountId() != null) Schedule(0);
                c.Reply(Bus.Ok());
            }));
            RegisterAccount(bus, "onCalendarEnabled", "onCalendarDelete");
        }

        private void IfPage(Bus.Call c, Action then) { if (Pages.Contains(c.AppId)) then(); else c.Reply(Bus.Error("Not for " + c.AppId)); }
        private bool RemindersOn() { object v; return !Settings.TryGetValue("reminders", out v) || !(v is bool) || (bool)v; }
        private string RemindersReply() { return new JObj().Put("returnValue", true).Put("on", RemindersOn()).Put("active", AccountId() != null).Put("hostName", WindowsAccountTemplate.HostName).ToString(); }
        private HashSet<string> Excluded()
        {
            object v;
            var a = Settings.TryGetValue("excluded", out v) && v is string ? Jx.Parse((string)v) as JArr : null;
            return new HashSet<string>(a == null ? Enumerable.Empty<string>() : a.OfType<string>());
        }

        protected override async Task<bool> Open()
        {
            if (store != null) return true;
            store = await AppointmentManager.RequestStoreAsync(AppointmentStoreAccessType.AllCalendarsReadOnly);
            return store != null;
        }

        protected override void Watch(bool on)
        {
            if (store == null) return;
            if (on) { try { store.ChangeTracker.Enable(); } catch (Exception) { } store.StoreChanged += OnChanged; }
            else store.StoreChanged -= OnChanged;
        }
        private void OnChanged(AppointmentStore s, AppointmentStoreChangedEventArgs e) { Changed(); }

        protected override void Remove(string id)
        {
            db.SyncAccount(EventKind, id, new List<JObj>());
            db.SyncAccount(CalendarKind, id, new List<JObj>(), (a, u, r) => LunaLog.Write("[windowscalendar] removed " + r + " calendars"));
        }

        private async Task<List<AppointmentCalendar>> Calendars()
        {
            return (await store.FindAppointmentCalendarsAsync(FindAppointmentCalendarsOptions.IncludeHidden)).ToList();
        }

        protected override async Task Sync()
        {
            var id = AccountId();
            if (id == null) return;
            bool ok;
            try { ok = await Open(); } catch (Exception) { ok = false; }
            SetGranted(ok);
            if (!ok) { LunaLog.Write("[windowscalendar] Windows won't let Lunacy read its calendars; the copy stays as it was"); return; }
            StartWatching();
            var ex = Excluded();
            var cals = (await Calendars()).Where(c => !ex.Contains(c.LocalId)).ToList();
            var events = new List<JObj>();
            foreach (var cal in cals) events.AddRange(await Read(cal));
            if (AccountId() != id) return;
            // No colour or visible: Calendar gives one and the owner may change it, and they stay.
            db.SyncAccount(CalendarKind, id, cals.Select(c => new JObj().Put("_id", "android-cal-" + Safe(c.LocalId)).Put("remoteId", "cal-" + c.LocalId)
                .Put("name", c.DisplayName ?? "").Put("isReadOnly", true).Put("syncSource", "windows").Put("excludeFromAll", false)).ToList());
            int nc = cals.Count, ne = events.Count;
            db.SyncAccount(EventKind, id, events, (a, u, r) =>
                LunaLog.Write("[windowscalendar] " + nc + " calendars, " + ne + " events; added " + a + ", changed " + u + ", removed " + r));
        }

        /// A Windows id as part of a db8 _id: Windows' are long and may hold any character.
        private static string Safe(string s) { return Regex.Replace(s ?? "", "[^A-Za-z0-9._-]", m => ((int)m.Value[0]).ToString("x2")); }

        private static FindAppointmentsOptions Options()
        {
            var o = new FindAppointmentsOptions { IncludeHidden = true };
            foreach (var p in AppointmentProperties.DefaultProperties) o.FetchProperties.Add(p);
            foreach (var p in new[] { AppointmentProperties.Subject, AppointmentProperties.Location, AppointmentProperties.StartTime, AppointmentProperties.Duration,
                AppointmentProperties.AllDay, AppointmentProperties.Details, AppointmentProperties.DetailsKind, AppointmentProperties.Reminder,
                AppointmentProperties.BusyStatus, AppointmentProperties.Recurrence, AppointmentProperties.OriginalStartTime, AppointmentProperties.IsCanceledMeeting })
                if (!o.FetchProperties.Contains(p)) o.FetchProperties.Add(p);
            return o;
        }

        private async Task<List<JObj>> Read(AppointmentCalendar cal)
        {
            var now = DateTimeOffset.Now;
            var from = now.AddDays(-366); var length = TimeSpan.FromDays(366 + 731);
            var calId = "android-cal-" + Safe(cal.LocalId);
            var found = await cal.FindAppointmentsAsync(from, length, Options());
            var out_ = new List<JObj>();
            // Occurrences of one repeat share its master's id.
            var series = new Dictionary<string, List<Appointment>>();
            foreach (var a in found)
            {
                if (a.Recurrence != null || a.OriginalStartTime.HasValue)
                {
                    List<Appointment> l;
                    if (!series.TryGetValue(a.LocalId, out l)) series[a.LocalId] = l = new List<Appointment>();
                    l.Add(a);
                }
                else out_.Add(Event(a, calId, "ev-" + a.LocalId, "android-ev-" + Safe(a.LocalId)));
            }
            foreach (var kv in series)
            {
                Appointment master = null;
                try { master = await cal.GetAppointmentAsync(kv.Key); } catch (Exception) { }
                var rule = master == null || master.Recurrence == null ? null : Rule(master);
                if (rule == null)
                {
                    // Windows' own occurrences in the window, each as a single event.
                    foreach (var a in kv.Value)
                    {
                        if (a.IsCanceledMeeting) continue;
                        var at = (a.OriginalStartTime ?? a.StartTime).ToUnixTimeMilliseconds();
                        out_.Add(Event(a, calId, "ev-" + a.LocalId + "#" + at, "android-ev-" + Safe(a.LocalId) + "-" + at).Put("rrule", JNull.Value));
                    }
                    continue;
                }
                var parentId = "android-ev-" + Safe(master.LocalId);
                var p = Event(master, calId, "ev-" + master.LocalId, parentId);
                var exdates = new List<string>();
                await Bound(cal, master, rule, p, exdates);
                p.Put("rrule", rule);
                IReadOnlyList<AppointmentException> exceptions = null;
                try { exceptions = await cal.FindExceptionsFromMasterAsync(master.LocalId); } catch (Exception) { }
                foreach (var x in exceptions ?? new List<AppointmentException>())
                {
                    var xa = x.Appointment;
                    if (xa == null || !xa.OriginalStartTime.HasValue) continue;
                    var instant = xa.OriginalStartTime.Value;
                    exdates.Add(master.AllDay ? OwnDate(instant) : LocalDate(instant.ToUnixTimeMilliseconds()));
                    if (x.IsDeleted || xa.IsCanceledMeeting) continue;
                    var ms = instant.ToUnixTimeMilliseconds();
                    out_.Add(Event(xa, calId, "ev-" + master.LocalId + "#" + ms, parentId + "-" + ms).Put("rrule", JNull.Value)
                        .Put("parentId", parentId).Put("parentDtstart", p.Long("dtstart")).Put("recurrenceId", UtcStamp(ms)));
                }
                p.Put("exdates", new JArr(exdates.Distinct().Cast<object>()));
                out_.Add(p);
            }
            return out_;
        }

        private JObj Event(Appointment a, string calId, string remoteId, string id)
        {
            bool allDay = a.AllDay;
            long start = a.StartTime.ToUnixTimeMilliseconds();
            long end = start + (long)a.Duration.TotalMilliseconds;
            long from, until;
            if (allDay)
            {
                // Windows' all-day days are midnights; webOS's importer keeps the day at local noon.
                var s = a.StartTime; var e = a.StartTime + a.Duration;
                from = Noon(s.Year, s.Month, s.Day);
                var last = a.Duration.TotalDays >= 1 ? e.AddDays(-1) : s;
                until = Math.Max(from, Noon(last.Year, last.Month, last.Day)) + 1000;
            }
            else { from = start; until = Math.Max(end, start); }
            var note = a.Details ?? "";
            if (a.DetailsKind == AppointmentDetailsKind.Html) note = WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(note, @"<(br|/p|/div)\s*/?>", "\n", RegexOptions.IgnoreCase), "<[^>]+>", "")).Trim();
            int? minutes = RemindersOn() && a.Reminder.HasValue ? (int)a.Reminder.Value.TotalMinutes : (int?)null;
            return new JObj().Put("_id", id).Put("remoteId", remoteId).Put("calendarId", calId)
                .Put("subject", a.Subject ?? "").Put("location", a.Location ?? "").Put("note", note)
                .Put("dtstart", from).Put("dtend", until).Put("tzId", Zone()).Put("allDay", allDay)
                .Put("transp", a.BusyStatus == AppointmentBusyStatus.Free ? "TRANSPARENT" : "OPAQUE")
                .Put("rrule", JNull.Value).Put("exdates", new JArr()).Put("alarm", Alarm(minutes, allDay));
        }

        /// The device's zone as Calendar knows zones (Olson's names, which Windows.Globalization uses).
        private static string Zone()
        {
            try { return new Windows.Globalization.Calendar().GetTimeZone(); } catch (Exception) { return "UTC"; }
        }

        private static readonly string[] Days = { "SU", "MO", "TU", "WE", "TH", "FR", "SA" };

        private static List<int> DaysOf(AppointmentDaysOfWeek d)
        {
            var l = new List<int>();
            for (int i = 0; i < 7; i++) if (((int)d & (1 << i)) != 0) l.Add(i);
            return l;
        }

        /// Windows' repeat as Calendar's rule object, or null if webOS can't hold it.
        private static JObj Rule(Appointment m)
        {
            var r = m.Recurrence;
            var rule = new JObj().Put("interval", (long)Math.Max(1u, r.Interval)).Put("wkst", 0);
            var rules = new JArr();
            Func<int, JObj> ord = n => new JObj().Put("ord", n);
            int week = r.WeekOfMonth == AppointmentWeekOfMonth.Last ? -1 : (int)r.WeekOfMonth + 1;
            var days = DaysOf(r.DaysOfWeek);
            switch (r.Unit)
            {
                case AppointmentRecurrenceUnit.Daily:
                    rule.Put("freq", "DAILY");
                    break;
                case AppointmentRecurrenceUnit.Weekly:
                    rule.Put("freq", "WEEKLY");
                    if (days.Count > 0) rules.Put(new JObj().Put("ruleType", "BYDAY").Put("ruleValue", new JArr(days.Select(d => (object)new JObj().Put("day", d)))));
                    break;
                case AppointmentRecurrenceUnit.Monthly:
                    rule.Put("freq", "MONTHLY");
                    rules.Put(new JObj().Put("ruleType", "BYMONTHDAY").Put("ruleValue", new JArr().Put(ord((int)r.Day))));
                    break;
                case AppointmentRecurrenceUnit.MonthlyOnDay:
                    // Several days here are Outlook's "first weekday": one of them, not each (BYSETPOS).
                    if (days.Count != 1) return null;
                    rule.Put("freq", "MONTHLY");
                    rules.Put(new JObj().Put("ruleType", "BYDAY").Put("ruleValue", new JArr().Put(new JObj().Put("day", days[0]).Put("ord", week))));
                    break;
                case AppointmentRecurrenceUnit.Yearly:
                    rule.Put("freq", "YEARLY");
                    rules.Put(new JObj().Put("ruleType", "BYMONTH").Put("ruleValue", new JArr().Put(ord((int)r.Month))));
                    rules.Put(new JObj().Put("ruleType", "BYMONTHDAY").Put("ruleValue", new JArr().Put(ord((int)r.Day))));
                    break;
                case AppointmentRecurrenceUnit.YearlyOnDay:
                    if (days.Count != 1) return null;
                    rule.Put("freq", "YEARLY");
                    rules.Put(new JObj().Put("ruleType", "BYMONTH").Put("ruleValue", new JArr().Put(ord((int)r.Month))));
                    rules.Put(new JObj().Put("ruleType", "BYDAY").Put("ruleValue", new JArr().Put(new JObj().Put("day", days[0]).Put("ord", week))));
                    break;
                default: return null;
            }
            if (rules.Count > 0) rule.Put("rules", rules);
            return rule;
        }

        /// How the repeat ends, as Calendar needs it (AndroidCalendar.row): a count becomes an
        /// until, since Calendar lets exceptions use a count up, and an until reaches the end of
        /// the last day; the days past the last occurrence that it reaches into are exceptions.
        private async Task Bound(AppointmentCalendar cal, Appointment m, JObj rule, JObj p, List<string> exdates)
        {
            var r = m.Recurrence;
            long start = m.StartTime.ToUnixTimeMilliseconds(), length = (long)m.Duration.TotalMilliseconds;
            long lastStart;
            if (r.Occurrences.HasValue)
            {
                lastStart = 0;
                try
                {
                    int perStep = r.Unit == AppointmentRecurrenceUnit.Daily ? 1 : r.Unit == AppointmentRecurrenceUnit.Weekly ? 7
                        : r.Unit == AppointmentRecurrenceUnit.Yearly || r.Unit == AppointmentRecurrenceUnit.YearlyOnDay ? 366 : 31;
                    var span = TimeSpan.FromDays(Math.Min(36500.0, (double)r.Occurrences.Value * Math.Max(1u, r.Interval) * perStep + 31));
                    foreach (var i in await cal.FindAllInstancesAsync(m.LocalId, m.StartTime, span, Options()))
                        lastStart = Math.Max(lastStart, (i.OriginalStartTime ?? i.StartTime).ToUnixTimeMilliseconds());
                }
                catch (Exception) { lastStart = 0; }
                if (lastStart == 0) { rule.Put("count", (long)r.Occurrences.Value); return; }
                if (m.AllDay) { var l = DateTimeOffset.FromUnixTimeMilliseconds(lastStart).ToOffset(m.StartTime.Offset); lastStart = Noon(l.Year, l.Month, l.Day); }
            }
            else if (r.Until.HasValue)
            {
                var u = r.Until.Value;
                if (m.AllDay) lastStart = Noon(u.Year, u.Month, u.Day);
                else
                {
                    // The until's day at the first occurrence's time of day; the day before if that is past it.
                    var first = m.StartTime.ToLocalTime(); var ul = u.ToLocalTime();
                    var at = new DateTimeOffset(new DateTime(ul.Year, ul.Month, ul.Day, first.Hour, first.Minute, first.Second, DateTimeKind.Local));
                    if (at > ul) at = at.AddDays(-1);
                    lastStart = at.ToUnixTimeMilliseconds();
                }
            }
            else return;
            if (m.AllDay) { rule.Put("until", lastStart + DayMs / 2 - 1000); return; }
            var until = Math.Max(EndOfDay(lastStart), lastStart + length);
            rule.Put("until", until);
            // The dates after the last that until reaches into would gain an occurrence.
            var d = DateTimeOffset.FromUnixTimeMilliseconds(lastStart).ToLocalTime().Date.AddDays(1);
            while (new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Local)).ToUnixTimeMilliseconds() <= until)
            {
                exdates.Add(d.ToString("yyyyMMdd"));
                d = d.AddDays(1);
            }
        }

        private static long Noon(int y, int m, int d) { return new DateTimeOffset(new DateTime(y, m, d, 12, 0, 0, DateTimeKind.Local)).ToUnixTimeMilliseconds(); }
        private static long EndOfDay(long ms) { var l = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime(); return new DateTimeOffset(new DateTime(l.Year, l.Month, l.Day, 23, 59, 59, DateTimeKind.Local)).ToUnixTimeMilliseconds(); }
        private static string LocalDate(long ms) { return DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("yyyyMMdd"); }
        private static string OwnDate(DateTimeOffset t) { return t.ToString("yyyyMMdd"); }
        private static string UtcStamp(long ms) { return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'"); }

        /// webOS's alarm (AndroidCalendar.alarm): days, hours and minutes, since its parser takes
        /// up to three digits for a unit and keeps only the last of weeks and days. An all-day
        /// event sits at noon here and its reminder counts from midnight, hence the 12 hours.
        private static JArr Alarm(int? minutes, bool allDay)
        {
            var value = minutes == null ? "none" : Trigger(Math.Max(0, allDay ? minutes.Value + 720 : minutes.Value));
            return new JArr().Put(new JObj().Put("action", "display").Put("alarmTrigger", new JObj().Put("value", value).Put("valueType", "DURATION")));
        }

        private static string Trigger(int total)
        {
            int days = total / 1440, hours = total % 1440 / 60, minutes = total % 60;
            var time = (hours > 0 ? hours + "H" : "") + (minutes > 0 ? minutes + "M" : "");
            if (days == 0 && time.Length == 0) return "-PT0M";
            if (days == 0) return "-PT" + time;
            if (time.Length == 0) return "-P" + days + "D";
            return "-P" + days + "DT" + time;
        }
    }

    /// The account as the person sees it on this phone: the template's name and pictures are
    /// Windows', written over the ROM's in the writable layer (WebosFs reads it first), which the
    /// accounts service lists like any other. Id, capabilities and kinds stay as they are.
    internal static class WindowsAccountTemplate
    {
        private const string Dir = "/usr/palm/public/accounts/org.webosarchive.lunacy.androidaccount";

        public static void Install(WebosFs fs)
        {
            try
            {
                var file = Dir + "/org.webosarchive.lunacy.androidaccount.json";
                var rom = fs.Find(file);
                if (rom == null) { LunaLog.Write("[windowsaccount] no account template in the ROM"); return; }
                var t = Jx.ParseObj(System.Text.Encoding.UTF8.GetString(fs.Read(file)));
                if (t.Str("loc_name") == HostName && fs.Exists(Dir + "/images/windows-48x48.png")) return;
                var icon = new JObj().Put("loc_32x32", "images/windows-32x32.png").Put("loc_48x48", "images/windows-48x48.png");
                t.Put("loc_name", HostName).Put("icon", icon);
                var providers = t.Arr("capabilityProviders");
                if (providers != null) foreach (var p in providers.OfType<JObj>()) p.Put("icon", icon.Copy());
                if (!fs.Exists(Dir + "/images")) fs.MakeDir(Dir + "/images", true);
                fs.Write(Dir + "/images/windows-32x32.png", Icon(32), false);
                fs.Write(Dir + "/images/windows-48x48.png", Icon(48), false);
                fs.Write(file, System.Text.Encoding.UTF8.GetBytes(t.ToString()), false);
            }
            catch (Exception e) { LunaLog.Write("[windowsaccount] template: " + e.Message); }
        }

        public static string HostName { get { return "Windows"; } }

        /// Four panes on a rounded tile: a plain picture of a window, drawn here.
        private static byte[] Icon(int size)
        {
            var px = new byte[size * size * 4];
            double r = size * 0.18, pad = size * 0.22, gap = Math.Max(1, size / 16.0);
            double mid = size / 2.0;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    // Rounded square coverage, softened at the corners.
                    double dx = Math.Max(0, Math.Max(r - x - 0.5, x + 0.5 - (size - r))), dy = Math.Max(0, Math.Max(r - y - 0.5, y + 0.5 - (size - r)));
                    double a = Math.Max(0, Math.Min(1, r - Math.Sqrt(dx * dx + dy * dy) + 0.5));
                    bool pane = x >= pad && x < size - pad && y >= pad && y < size - pad && Math.Abs(x + 0.5 - mid) > gap / 2 && Math.Abs(y + 0.5 - mid) > gap / 2;
                    byte cr = pane ? (byte)255 : (byte)0x1E, cg = pane ? (byte)255 : (byte)0x78, cb = pane ? (byte)255 : (byte)0xD4;
                    int i = (y * size + x) * 4;
                    px[i] = (byte)(cb * a); px[i + 1] = (byte)(cg * a); px[i + 2] = (byte)(cr * a); px[i + 3] = (byte)(255 * a);
                }
            return Images.Encode(px, size, size, false).GetAwaiter().GetResult();
        }
    }
}
