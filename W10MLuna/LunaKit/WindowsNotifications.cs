using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Foundation;
using Windows.Foundation.Metadata;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace LunaKit
{
    /// Windows' notifications as webOS dashboards (AndroidLuna's AndroidNotifications.kt), for
    /// the bundled Notifications app (org.webosarchive.lunacy.notifications), which reads them
    /// through org.webosarchive.lunacy/android/notifications in the same shape: by app, each with
    /// its notifications, and `alerts` for the ones just come in, which get a banner.
    ///
    /// Windows' UserNotificationListener reads other apps' toasts once the user allows it (First
    /// Use's Notifications page). Its NotificationChanged event is not raised on every device
    /// family, so while access is granted the list is also read every few seconds.
    ///
    /// Differences from Android's, each Windows' own: a toast has no priority, so every new one
    /// is an alert (Windows popped it up too); and no app may activate another app's toast, so
    /// opening one answers that it can't be opened, as Android's did for a notification with
    /// no intent and no launchable app. Dismissing removes it from Windows' Action Center too.
    /// There are no other apps' media sessions to read before Windows 10 1809: media is empty.
    internal sealed class WindowsNotifications
    {
        public const string AppId = "org.webosarchive.lunacy.notifications";
        public const string IconPath = "/__lunacy/android-icon/";
        private const int IconPx = 144;
        private const int PollMs = 3000;

        private sealed class Entry
        {
            public uint Id;
            public string Pkg, AppTitle, Title, Text;
            public long Posted;
            public bool Fresh;
        }

        private readonly Dictionary<uint, Entry> entries = new Dictionary<uint, Entry>();
        private readonly Dictionary<string, byte[]> icons = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        private UserNotificationListener listener;
        private Timer poll;
        private int refreshing;
        public event Action Changed;

        public static bool Supported { get { return ApiInformation.IsTypePresent("Windows.UI.Notifications.Management.UserNotificationListener"); } }

        private UserNotificationListener Listener
        {
            get
            {
                if (listener == null && Supported) { try { listener = UserNotificationListener.Current; } catch (Exception e) { LunaLog.Write("[notifications] no listener: " + e.Message); } }
                return listener;
            }
        }

        public bool Granted
        {
            get
            {
                var l = Listener;
                try { return l != null && l.GetAccessStatus() == UserNotificationListenerAccessStatus.Allowed; } catch (Exception) { return false; }
            }
        }

        public bool Connected { get { return poll != null; } }

        /// Windows' question. On the UI thread.
        public async Task<bool> Request()
        {
            var l = Listener;
            if (l == null) return false;
            UserNotificationListenerAccessStatus s;
            try { s = await l.RequestAccessAsync(); }
            catch (Exception e) { LunaLog.Write("[notifications] access request failed: " + e.Message); return false; }
            LunaLog.Write("[notifications] access: " + s);
            if (s == UserNotificationListenerAccessStatus.Allowed) Start();
            return s == UserNotificationListenerAccessStatus.Allowed;
        }

        /// Starts listening, if allowed.
        public void Start()
        {
            if (poll != null || !Granted) return;
            try { Listener.NotificationChanged += (s, e) => Refresh(); }
            catch (Exception e) { LunaLog.Write("[notifications] no change events here (" + e.Message + "): reading every " + PollMs / 1000 + " s"); }
            poll = new Timer(_ => Refresh(), null, 0, PollMs);
            LunaLog.Write("[notifications] listening");
        }

        private async void Refresh()
        {
            if (Interlocked.Exchange(ref refreshing, 1) == 1) return;
            try
            {
                var list = await Listener.GetNotificationsAsync(NotificationKinds.Toast);
                var mine = Package.Current.Id.FamilyName;
                var now = new Dictionary<uint, Entry>();
                foreach (var n in list)
                {
                    var e = EntryOf(n, mine);
                    if (e != null) now[e.Id] = e;
                }
                bool changed;
                lock (entries)
                {
                    changed = now.Count != entries.Count || now.Keys.Any(k => !entries.ContainsKey(k));
                    foreach (var e in now.Values)
                    {
                        Entry old;
                        if (entries.TryGetValue(e.Id, out old)) { e.Fresh = old.Fresh; if (old.Title != e.Title || old.Text != e.Text) changed = true; }
                        else e.Fresh = true;
                    }
                    entries.Clear();
                    foreach (var e in now.Values) entries[e.Id] = e;
                }
                if (changed) { var c = Changed; if (c != null) c(); }
            }
            catch (Exception e) { LunaLog.Write("[notifications] read failed: " + e.Message); }
            finally { Interlocked.Exchange(ref refreshing, 0); }
        }

        private static Entry EntryOf(UserNotification n, string mine)
        {
            try
            {
                var app = n.AppInfo;
                if (app != null && app.PackageFamilyName == mine) return null;
                var binding = n.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
                var texts = binding == null ? new List<string>() : binding.GetTextElements().Select(t => (t.Text ?? "").Trim()).Where(t => t.Length > 0).ToList();
                if (texts.Count == 0) return null;
                var pkg = app == null ? "unknown" : app.AppUserModelId;
                var appTitle = app == null ? pkg : app.DisplayInfo.DisplayName;
                return new Entry
                {
                    Id = n.Id, Pkg = pkg, AppTitle = appTitle,
                    Title = texts.Count > 1 ? texts[0] : appTitle,
                    Text = texts.Count > 1 ? string.Join(" ", texts.Skip(1)) : texts[0],
                    Posted = n.CreationTime.ToUnixTimeMilliseconds()
                };
            }
            catch (Exception) { return null; }
        }

        /// What android/notifications answers. `consume`: the alerts are handed out once.
        public string Snapshot(bool consume)
        {
            List<Entry> all, fresh;
            lock (entries)
            {
                all = entries.Values.ToList();
                fresh = all.Where(e => e.Fresh).ToList();
                if (consume) foreach (var e in fresh) e.Fresh = false;
            }
            var apps = new JArr();
            foreach (var g in all.GroupBy(e => e.Pkg).OrderBy(g => g.Max(e => e.Posted)))
            {
                var notes = new JArr();
                foreach (var e in g.OrderBy(e => e.Posted)) notes.Add(new JObj().Put("key", e.Id.ToString()).Put("title", e.Title).Put("text", e.Text));
                var first = g.First();
                apps.Add(new JObj().Put("package", g.Key).Put("title", first.AppTitle).Put("icon", IconUrl(first))
                    .Put("image", IconPath + Uri.EscapeDataString(g.Key) + ".svg").Put("notifications", notes));
            }
            var alerts = new JArr();
            foreach (var e in fresh) alerts.Add(new JObj().Put("key", e.Id.ToString()).Put("package", e.Pkg).Put("title", e.Title).Put("text", e.Text).Put("icon", IconUrl(e)));
            return new JObj().Put("returnValue", true).Put("access", Granted).Put("connected", Connected)
                .Put("apps", apps).Put("alerts", alerts).Put("media", new JArr()).ToString();
        }

        public bool Any() { lock (entries) return entries.Count > 0; }

        private string IconUrl(Entry e)
        {
            var png = Icon(e.Pkg);
            return png == null ? "" : "data:image/png;base64," + Convert.ToBase64String(png);
        }

        /// The app's logo as a PNG, from Windows' app info; once per app.
        private byte[] Icon(string pkg)
        {
            lock (icons) { byte[] b; if (icons.TryGetValue(pkg, out b)) return b; }
            byte[] png = null;
            try
            {
                var n = Listener.GetNotificationsAsync(NotificationKinds.Toast).AsTask().GetAwaiter().GetResult()
                    .FirstOrDefault(x => x.AppInfo != null && x.AppInfo.AppUserModelId == pkg);
                if (n != null)
                {
                    var stream = n.AppInfo.DisplayInfo.GetLogo(new Size(IconPx, IconPx)).OpenReadAsync().AsTask().GetAwaiter().GetResult();
                    var bytes = new byte[stream.Size];
                    stream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, Windows.Storage.Streams.InputStreamOptions.None).AsTask().GetAwaiter().GetResult();
                    png = bytes;
                }
            }
            catch (Exception e) { LunaLog.Write("[notifications] no icon for " + pkg + ": " + e.Message); }
            lock (icons) icons[pkg] = png;
            return png;
        }

        /// The dashboard's icon (an SVG holding the logo, as Android's), for AppServer.
        public byte[] DashboardIcon(string pkg)
        {
            lock (entries) if (!entries.Values.Any(e => e.Pkg == pkg)) return null;
            var png = Icon(pkg);
            if (png == null) return null;
            return System.Text.Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" width=\"48\" height=\"48\">" +
                "<image x=\"3\" y=\"3\" width=\"42\" height=\"42\" xlink:href=\"data:image/png;base64," + Convert.ToBase64String(png) + "\"/></svg>");
        }

        /// A notification tapped. Windows lets no app activate another's toast.
        public string Open(string key)
        {
            uint id;
            lock (entries) if (!uint.TryParse(key, out id) || !entries.ContainsKey(id)) return "That notification has gone";
            return "This notification can't be opened";
        }

        public void Dismiss(IEnumerable<string> keys)
        {
            var gone = new List<uint>();
            lock (entries)
                foreach (var k in keys) { uint id; if (uint.TryParse(k, out id) && entries.Remove(id)) gone.Add(id); }
            foreach (var id in gone)
            {
                try { Listener.RemoveNotification(id); }
                catch (Exception e) { LunaLog.Write("[notifications] Windows refused to clear " + id + ": " + e.Message); }
            }
            if (gone.Count > 0) { var c = Changed; if (c != null) c(); }
        }
    }
}
