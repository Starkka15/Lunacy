using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.DataTransfer;
using Windows.Data.Json;
using Windows.Devices.Power;
using Windows.Graphics.Display;
using Windows.Security.ExchangeActiveSyncProvisioning;
using Windows.Storage;
using Windows.System.Power;
using Windows.System.Profile;

namespace LunaKit
{
    /// palm://org.webosarchive.lunacy, Lunacy's own service under its own name (AndroidLuna's
    /// LunacyService.kt), with what Windows 10 Mobile has of it:
    /// - `system/getEnvironment`: what Lunacy runs on, for Device Info. The answer keeps
    ///   Android's shape, since Device Info reads it; its "android" part is Windows here.
    /// - `system/setFixedViewport` and `system/setLayout`: Phone Zoom per app, and its width.
    /// - `system/setDeviceId`: carry a TouchPad's device id over, or go back to this install's.
    /// - `system/saveLog` and `system/shareLog`: the log into Downloads, and Windows' share pane.
    /// - `android/openSettings`: the host's own settings, Windows' About page here.
    /// - `permissions/status` and `permissions/request`: First Use's questions, put to Windows
    ///   (location, background, notification access, People and Calendar), with `hostName`.
    /// - `firstUse/done`, and Windows' notifications under Android's method names, which the
    ///   Notifications app calls.
    /// What Windows 10 Mobile can't do (being the home screen, controlling another app's
    /// player) answers with an error that says so.
    internal sealed class LunacyService
    {
        private const string Service = "org.webosarchive.lunacy";
        private readonly LunaShell shell;
        private StorageFile lastLog;

        public LunacyService(LunaShell shell) { this.shell = shell; }

        public void Register(Bus bus)
        {
            bus.Register(Service, "system/getEnvironment", c => Task.Run(async () =>
            {
                string r;
                try { r = await Environment(); }
                catch (Exception e) { r = Bus.Error("system/getEnvironment: " + e.Message); }
                c.Reply(r);
            }));
            Own(bus, "system/setFixedViewport", "set an app's viewport", c =>
            {
                var id = c.Params.GetString("id");
                if (string.IsNullOrEmpty(id) || shell.Apps.Get(id) == null) { c.Reply(Bus.Error("No app " + id)); return; }
                bool on = c.Params.GetBool("on");
                FixedViewport.Set(id, on);
                shell.Evaluate("__lunacyShell.setZoom(" + J.Str(id) + "," + (on ? "true" : "false") + ")");
                c.Reply(Bus.Ok());
            });
            Own(bus, "system/setLayout", "set the layout", c =>
            {
                if (!c.Params.ContainsKey("appLayoutWidth")) { c.Reply(Bus.Error("Nothing to set")); return; }
                var w = c.Params.GetInt("appLayoutWidth");
                if (w != 0 && (w < 320 || w > 2048)) { c.Reply(Bus.Error("The width must be 0 or 320 to 2048 px")); return; }
                FixedViewport.LayoutWidth = w;
                shell.Evaluate("__lunacyShell.setLayoutWidth(" + w + ")");
                c.Reply(Bus.Ok());
            });
            Own(bus, "system/setDeviceId", "set the device id", c =>
            {
                if (c.Params.GetBool("reset")) { DeviceProfile.SetNduid(null); c.Reply(Bus.Ok()); return; }
                var id = (c.Params.GetString("nduid") ?? "").Trim();
                if (!Regex.IsMatch(id, "^[0-9a-fA-F]{40}$")) { c.Reply(Bus.Error("A device id is 40 hex digits (0-9, a-f).")); return; }
                DeviceProfile.SetNduid(id);
                c.Reply(Bus.Ok());
            });
            Own(bus, "system/saveLog", "save a log", c => Task.Run(async () =>
            {
                try { c.Reply(await SaveLog()); }
                catch (Exception e) { c.Reply(Bus.Error("The log couldn't be saved: " + e.Message)); }
            }));
            Own(bus, "system/shareLog", "share a log", c =>
            {
                var file = lastLog;
                if (file == null) { c.Reply(Bus.Error("Save the log first")); return; }
                shell.Ui(() =>
                {
                    var dtm = DataTransferManager.GetForCurrentView();
                    Windows.Foundation.TypedEventHandler<DataTransferManager, DataRequestedEventArgs> h = null;
                    h = (s, e) =>
                    {
                        dtm.DataRequested -= h;
                        e.Request.Data.Properties.Title = file.Name;
                        e.Request.Data.SetStorageItems(new IStorageItem[] { file });
                    };
                    dtm.DataRequested += h;
                    DataTransferManager.ShowShareUI();
                    c.Reply(Bus.Ok());
                });
            });
            bus.Register(Service, "android/openSettings", c => shell.Ui(async () =>
            {
                bool ok = await OpenSettingsPanel(Jx.ObjOrEmpty(c.Raw).Str("panel"));
                c.Reply(ok ? Bus.Ok() : Bus.Error("Windows couldn't open its settings"));
            }));
            bus.Register(Service, "android/settingsPanels", c =>
                c.Reply(new JObj().Put("returnValue", true).Put("panels", new JArr { "bluetooth", "date", "locale", "wifi" }).ToString()));
            // The file picker's listing (it walks folders, so off the bus's thread).
            bus.Register(Service, "files/list", c => Task.Run(() =>
            {
                string r;
                try { r = FileListing.List(shell.Fs, Jx.ObjOrEmpty(c.Raw)); }
                catch (Exception e) { r = Bus.Error("files/list: " + e.Message); }
                c.Reply(r);
            }));
            // Software Manager: every app the launcher can show, hiding one, and Start tiles.
            Own(bus, "launcher/listApps", "list the launcher's apps", ListLauncherApps);
            Own(bus, "launcher/setHidden", "hide an app", c => c.Reply(SetHidden(Jx.ObjOrEmpty(c.Raw))));
            // Lunacy is an app here, never Windows' home, and its own launcher is the one in use:
            // Pin goes beside the switch rather than in place of it (pinBeside).
            Own(bus, "launcher/pinStatus", "pin apps", c => c.Reply(new JObj().Put("returnValue", true).Put("lunacyIsHome", false)
                .Put("canPin", true).Put("pinBeside", true).ToString()));
            Own(bus, "launcher/pin", "pin apps", c => shell.Ui(async () =>
            {
                var app = shell.Apps.Get(Jx.ObjOrEmpty(c.Raw).Str("id"));
                if (app == null) { c.Reply(Bus.Error("No launcher app " + Jx.ObjOrEmpty(c.Raw).Str("id"), -1)); return; }
                var err = await StartTiles.Pin(app);
                c.Reply(err == null ? Bus.Ok() : Bus.Error(err, -1));
            }));
            Own(bus, "system/setHostWallpaper", "set Windows' background", c =>
            {
                var p = Jx.ObjOrEmpty(c.Raw);
                if (!p.Has("on")) { c.Reply(Bus.Error("on is required")); return; }
                if (!p.Bool("on")) { HostWallpaper.On = false; c.Reply(new JObj().Put("returnValue", true).Put("on", false).ToString()); return; }
                shell.Ui(async () =>
                {
                    var err = await HostWallpaper.Apply(shell.Fs, shell.WallpaperFile());
                    if (err == null) HostWallpaper.On = true;
                    c.Reply(err == null ? new JObj().Put("returnValue", true).Put("on", true).ToString() : Bus.Error(err));
                });
            });
            HiddenApps.Changed += () => { shell.RefreshLauncher(); LauncherAppsChanged(); };
            // First Use and the permissions it asks for (Windows' own questions).
            bus.Register(Service, "permissions/status", c => c.Reply(PermissionsStatus()));
            bus.Register(Service, "permissions/request", RequestPermission);
            Own(bus, "firstUse/done", "finish First Use", c => { SetFlag(FirstUseKey); SetFlag(BackgroundAskedKey); c.Reply(Bus.Ok()); shell.FirstUseDone(); });
            // Windows' notifications, for the Notifications app (the method names are Android's,
            // which the app calls).
            Own(bus, "android/notifications", "read Windows' notifications", WatchNotifications);
            Own(bus, "android/openNotification", "open Windows' notifications", c =>
            {
                var key = c.Params.GetString("key") ?? "";
                if (key.Length == 0) { c.Reply(Bus.Error("key is required")); return; }
                var err = shell.Notifications.Open(key);
                c.Reply(err == null ? Bus.Ok() : Bus.Error(err));
            });
            Own(bus, "android/dismissNotifications", "dismiss Windows' notifications", c =>
            {
                var keys = Jx.ObjOrEmpty(c.Raw).Arr("keys");
                if (keys == null) { c.Reply(Bus.Error("keys is required")); return; }
                shell.Notifications.Dismiss(keys.OfType<string>().Where(k => k.Length > 0));
                c.Reply(Bus.Ok());
            });
            Own(bus, "android/mediaControl", "control Windows' players", c =>
                c.Reply(Bus.Error("Windows 10 Mobile doesn't let Lunacy control another app's player")));
        }

        // ---- First Use's flags, kept in the app's settings ----

        public const string FirstUseKey = "firstUseDone", LocationAskedKey = "locationAsked", BackgroundAskedKey = "backgroundAsked";
        public static bool Flag(string key)
        {
            object v;
            return ApplicationData.Current.LocalSettings.Values.TryGetValue(key, out v) && v is bool && (bool)v;
        }
        public static void SetFlag(string key) { ApplicationData.Current.LocalSettings.Values[key] = true; }

        /// Lunacy's own bundled apps (AndroidLuna's ownApp): org.webosarchive.lunacy.* and the
        /// settings apps that are Lunacy's own; never an installed app with such an id.
        private static readonly HashSet<string> OwnApps = new HashSet<string> { "com.palm.app.deviceinfo", "com.palm.app.screenlock", "com.palm.app.swmanager" };
        private bool OwnApp(string appId)
        {
            var known = OwnApps.Contains(appId) || appId.StartsWith("org.webosarchive.lunacy", StringComparison.Ordinal);
            var a = shell.Apps.Get(appId);
            return known && a != null && !a.Removable;
        }

        /// An account's setup page runs inside the app that is adding the account, and calls the bus
        /// as that app (LunacyService.kt's hostsAccountPages): the Windows account's page asks for
        /// contacts and calendars through bundled Contacts, Calendar or Accounts.
        private static readonly HashSet<string> AccountPageHosts = new HashSet<string> { "com.palm.app.contacts", "com.palm.app.calendar", "com.palm.app.accounts" };
        private bool HostsAccountPages(string appId)
        {
            var a = shell.Apps.Get(appId);
            return AccountPageHosts.Contains(appId) && a != null && !a.Removable;
        }

        // ---- the launcher's apps, for Software Manager ----

        private const string SoftwareManager = "com.palm.app.swmanager";
        private readonly Subscribers launcherWatchers = new Subscribers();

        private void ListLauncherApps(Bus.Call c)
        {
            c.Reply(LauncherApps());
            if (c.Subscribe && !c.Cancelled) launcherWatchers.Add(c);
        }

        /// The launcher's apps changed (installed, removed, hidden, shown): watchers hear the new list.
        public void LauncherAppsChanged() { if (launcherWatchers.Count > 0) launcherWatchers.Send(LauncherApps()); }

        private string LauncherApps()
        {
            var hidden = HiddenApps.All();
            var list = new JArr();
            foreach (var a in shell.Apps.All().Where(x => x.Visible).OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase))
            {
                list.Add(new JObj().Put("id", a.Id).Put("title", a.Title).Put("vendor", a.Vendor ?? "").Put("version", a.Version ?? "")
                    .Put("icon", a.Url(a.Icon)).Put("kind", "webos").Put("hidden", hidden.Contains(a.Id))
                    // Software Manager is how an app comes back, so it can't hide itself.
                    .Put("canHide", a.Id != SoftwareManager));
            }
            return new JObj().Put("returnValue", true).Put("apps", list).ToString();
        }

        private string SetHidden(JObj p)
        {
            var id = p.Str("id");
            if (id.Length == 0 || !p.Has("hidden")) return Bus.Error("setHidden needs an id and hidden", -1);
            if (id == SoftwareManager) return Bus.Error("Software Manager can't be hidden: it is how hidden apps come back", -1);
            if (shell.Apps.Get(id) == null) return Bus.Error("No launcher app " + id, -1);
            HiddenApps.Set(id, p.Bool("hidden"));
            return Bus.Ok();
        }

        private void Own(Bus bus, string method, string what, Action<Bus.Call> h)
        {
            bus.Register(Service, method, c =>
            {
                if (OwnApp(c.AppId)) h(c);
                else c.Reply(Bus.Error("Only Lunacy's own apps can " + what + " (asked by " + c.AppId + ")", -1));
            });
        }

        // ---- permissions, for First Use ----

        /// What Lunacy may do here, in Android's shape (First Use reads it): each permission says
        /// whether Windows asks for it at all (`asked`) and whether it is granted now.
        /// - storage: the phone's libraries behind /media/internal, granted at install on 15063.
        /// - location: Windows' location consent, for Location Services and the Wi-Fi list.
        /// - background: BackgroundExecutionManager's access (Background.cs).
        /// - notifications: reading other apps' toasts (WindowsNotifications.cs).
        /// - systemSettings: Screen &amp; Lock's brightness and timeout need no permission here.
        /// - homeLauncher: Windows 10 Mobile lets no app be the home screen.
        private string PermissionsStatus()
        {
            return new JObj().Put("returnValue", true)
                .Put("storage", Perm(false, true))
                .Put("location", Perm(true, LocationGranted()))
                .Put("contacts", Perm(shell.Contacts.Asked, shell.Contacts.Granted))
                .Put("calendar", Perm(shell.Calendar.Asked, shell.Calendar.Granted))
                .Put("systemSettings", Perm(false, true))
                .Put("background", Perm(true, Background.Granted))
                .Put("homeLauncher", Perm(false, false))
                .Put("notifications", Perm(WindowsNotifications.Supported, shell.Notifications.Granted))
                .Put("firstUseDone", Flag(FirstUseKey))
                // What First Use and the account's page call the system Lunacy runs on.
                .Put("hostName", WindowsAccountTemplate.HostName).ToString();
        }

        private static JObj Perm(bool asked, bool granted) { return new JObj().Put("asked", asked).Put("granted", granted); }

        public static bool LocationGranted()
        {
            try
            {
                return Windows.Devices.Enumeration.DeviceAccessInformation.CreateFromDeviceClass(Windows.Devices.Enumeration.DeviceClass.Location).CurrentStatus
                    == Windows.Devices.Enumeration.DeviceAccessStatus.Allowed;
            }
            catch (Exception) { return false; }
        }

        private static string Granted(string which, bool granted, bool? askAgain = null, bool opened = false)
        {
            var o = new JObj().Put("returnValue", true).Put("permission", which).Put("granted", granted);
            if (askAgain.HasValue) o.Put("askAgain", askAgain.Value);
            if (opened) o.Put("opened", true);
            return o.ToString();
        }

        private void RequestPermission(Bus.Call c)
        {
            var which = c.Params.GetString("permission") ?? "";
            if (!OwnApp(c.AppId) && !((which == "contacts" || which == "calendar") && HostsAccountPages(c.AppId))) { c.Reply(Bus.Error("Only Lunacy's own apps can ask for Windows' permissions (asked by " + c.AppId + ")", -1)); return; }
            bool viaSettings = c.Params.GetBool("viaSettings");
            switch (which)
            {
                case "storage":
                case "systemSettings":
                    c.Reply(Granted(which, true));
                    return;
                case "location":
                    SetFlag(LocationAskedKey);
                    if (LocationGranted()) { c.Reply(Granted(which, true)); return; }
                    shell.Ui(async () =>
                    {
                        if (viaSettings) { c.Reply(await OpenSettings("ms-settings:privacy-location") ? Granted(which, false, null, true) : Bus.Error("Couldn't open Windows' location settings")); return; }
                        Windows.Devices.Geolocation.GeolocationAccessStatus s;
                        try { s = await Windows.Devices.Geolocation.Geolocator.RequestAccessAsync(); }
                        catch (Exception e) { c.Reply(Bus.Error("Windows didn't ask: " + e.Message)); return; }
                        LunaLog.Write("[lunacy] location access: " + s);
                        // Windows asks once; after a no, only its settings can change it.
                        c.Reply(Granted(which, s == Windows.Devices.Geolocation.GeolocationAccessStatus.Allowed, s == Windows.Devices.Geolocation.GeolocationAccessStatus.Unspecified));
                    });
                    return;
                case "background":
                    SetFlag(BackgroundAskedKey);
                    if (Background.Granted) { c.Reply(Granted(which, true)); return; }
                    shell.Ui(async () =>
                    {
                        if (await Background.Request()) { c.Reply(Granted(which, true)); return; }
                        // Refused, or left to the battery saver: Windows' own switch for Lunacy.
                        c.Reply(await OpenSettings("ms-settings:batterysaver-usagedetails") ? Granted(which, false, null, true) : Bus.Error("Couldn't open Windows' battery settings"));
                    });
                    return;
                case "notifications":
                    if (!WindowsNotifications.Supported) { c.Reply(Bus.Error("This version of Windows can't share its notifications with Lunacy")); return; }
                    if (shell.Notifications.Granted) { c.Reply(Granted(which, true)); return; }
                    shell.Ui(async () =>
                    {
                        if (await shell.Notifications.Request()) { c.Reply(Granted(which, true)); return; }
                        c.Reply(await OpenSettings("ms-settings:privacy-notifications") ? Granted(which, false, null, true) : Bus.Error("Couldn't open Windows' notification settings"));
                    });
                    return;
                case "contacts":
                case "calendar":
                    {
                        AccountSide side = which == "contacts" ? (AccountSide)shell.Contacts : shell.Calendar;
                        shell.Ui(async () =>
                        {
                            if (await side.Request()) { c.Reply(Granted(which, true)); return; }
                            // Windows has no dialog for these: its privacy settings are the only place to allow them.
                            if (viaSettings) { c.Reply(await OpenSettings(which == "contacts" ? "ms-settings:privacy-contacts" : "ms-settings:privacy-calendar") ? Granted(which, false, null, true) : Bus.Error("Couldn't open Windows' privacy settings")); return; }
                            c.Reply(Granted(which, false, false));
                        });
                        return;
                    }
                case "homeLauncher":                    c.Reply(Bus.Error("Windows 10 Mobile doesn't let an app be the home screen"));
                    return;
                default:
                    c.Reply(Bus.Error("No permission \"" + which + "\": storage, location, contacts, calendar, systemSettings, background, notifications, homeLauncher"));
                    return;
            }
        }

        /// One of Windows' settings pages for an Android panel name (the apps' lunacyAndroidSettings,
        /// android/openSettings's "panel"); the About page for none.
        public static Task<bool> OpenSettingsPanel(string panel)
        {
            switch (panel ?? "")
            {
                case "wifi": return OpenSettings("ms-settings:network-wifi");
                case "bluetooth": return OpenSettings("ms-settings:bluetooth");
                case "date": return OpenSettings("ms-settings:dateandtime");
                case "locale": return OpenSettings("ms-settings:regionlanguage");
                default: return OpenSettings("ms-settings:about");
            }
        }

        private static async Task<bool> OpenSettings(string uri)
        {
            try { return await Windows.System.Launcher.LaunchUriAsync(new Uri(uri)); } catch (Exception) { return false; }
        }

        // ---- Windows' notifications ----

        private readonly Subscribers notificationWatchers = new Subscribers();

        private void WatchNotifications(Bus.Call c)
        {
            c.Reply(shell.Notifications.Snapshot(true));
            if (c.Subscribe && !c.Cancelled) notificationWatchers.Add(c);
        }

        /// Windows' notifications changed: whoever is watching hears them; a banner is told once.
        public void NotificationsChanged()
        {
            if (notificationWatchers.Count == 0) return;
            notificationWatchers.Send(shell.Notifications.Snapshot(true));
        }

        public bool NotificationsWatched { get { return notificationWatchers.Count > 0; } }

        private async Task<string> Environment()
        {
            var p = shell.Profile;
            var pkg = Package.Current.Id;
            var v = pkg.Version;
            var eas = new EasClientDeviceInformation();
            var os = OsVersion();
            var build = os.Split('.').Length > 2 ? os.Split('.')[2] : "";
            var apps = shell.Apps.All();

            var lunacy = J.Obj(
                J.P("version", v.Major + "." + v.Minor + "." + v.Build), J.P("build", (int)v.Revision),
                J.P("flavour", pkg.Architecture == Windows.System.ProcessorArchitecture.Arm ? "arm" : pkg.Architecture.ToString().ToLowerInvariant()),
                J.P("type", "release"), J.P("apk", pkg.FullName), J.P("package", pkg.FamilyName),
                J.P("reportsWebOS", p.VersionString), J.P("reportsAs", p.ModelName),
                J.P("enyo", "1.0"), J.P("node", null), J.P("apps", apps.Count),
                J.P("services", new J.Raw("[]")));
            // Device Info reads this part as the host OS's: Windows here.
            var host = J.Obj(J.P("release", "Windows 10 " + (FixedViewport.Phone ? "Mobile" : AnalyticsInfo.VersionInfo.DeviceFamily)),
                J.P("sdk", os), J.P("build", os), J.P("abi", pkg.Architecture.ToString().ToLowerInvariant()));
            var webos = J.Obj(J.P("model", p.ModelName), J.P("version", p.VersionString), J.P("serial", p.Serial),
                J.P("nduid", DeviceProfile.Nduid), J.P("nduidSource", DeviceProfile.NduidSource));

            long total = 0, free = 0;
            try
            {
                var props = await ApplicationData.Current.LocalFolder.Properties.RetrievePropertiesAsync(new[] { "System.FreeSpace", "System.Capacity" });
                object f, t;
                if (props.TryGetValue("System.FreeSpace", out f) && f != null) free = Convert.ToInt64(f);
                if (props.TryGetValue("System.Capacity", out t) && t != null) total = Convert.ToInt64(t);
            }
            catch (Exception) { }
            int battery = -1; bool charging = false;
            try
            {
                var report = Battery.AggregateBattery.GetReport();
                if (report.FullChargeCapacityInMilliwattHours > 0 && report.RemainingCapacityInMilliwattHours.HasValue)
                    battery = (int)Math.Round(100.0 * report.RemainingCapacityInMilliwattHours.Value / report.FullChargeCapacityInMilliwattHours.Value);
                charging = report.Status == BatteryStatus.Charging;
            }
            catch (Exception) { }
            var device = J.Obj(J.P("manufacturer", eas.SystemManufacturer), J.P("model", eas.SystemProductName),
                J.P("serial", eas.SystemSku), J.P("ram", TotalRam()),
                J.P("storageTotal", total), J.P("storageFree", free), J.P("battery", battery), J.P("charging", charging));
            // EdgeHTML's version is the Windows build's: 15 for Creators Update builds and the
            // feature2 phones (15254), 16 for the Fall Creators Update.
            int b; int.TryParse(build, out b);
            var edge = b >= 17763 ? 18 : b >= 17134 ? 17 : b >= 16299 ? 16 : b >= 15063 ? 15 : 14;
            var webview = J.Obj(J.P("version", "EdgeHTML " + edge + "." + build), J.P("package", "Windows' own WebView"));

            var display = shell.OnUi(() =>
            {
                var di = DisplayInformation.GetForCurrentView();
                var bounds = Windows.UI.Xaml.Window.Current.Bounds;
                double z = di.RawPixelsPerViewPixel;
                return J.Obj(J.P("width", (int)di.ScreenWidthInRawPixels), J.P("height", (int)di.ScreenHeightInRawPixels),
                    J.P("dpi", (int)Math.Round(di.RawDpiX)), J.P("cardWidth", (int)bounds.Width), J.P("cardHeight", (int)bounds.Height - 28),
                    J.P("scale", Math.Round(z * 100) / 100), J.P("pixelGrid", Math.Abs(z - Math.Round(z)) < 0.01 ? "1:1" : (Math.Round(z * 100) / 100) + ":1"),
                    J.P("orientation", shell.Orientation));
            });
            var layout = J.Obj(J.P("layout", p.Name == "PRE3" ? "phone" : "tablet"), J.P("setting", "auto"),
                J.P("appLayoutWidth", FixedViewport.LayoutWidth));
            var software = new JsonArray();
            foreach (var a in apps.OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase))
            {
                var o = new JsonObject();
                o["id"] = JsonValue.CreateStringValue(a.Id);
                o["title"] = JsonValue.CreateStringValue(a.Title ?? a.Id);
                o["version"] = JsonValue.CreateStringValue(a.Version ?? "");
                o["on"] = JsonValue.CreateBooleanValue(FixedViewport.Chosen(a.Id));
                software.Add(o);
            }

            return J.Obj(J.P("returnValue", true), J.P("lunacy", new J.Raw(lunacy)), J.P("android", new J.Raw(host)),
                J.P("webos", new J.Raw(webos)), J.P("device", new J.Raw(device)), J.P("webview", new J.Raw(webview)),
                J.P("display", new J.Raw(display)), J.P("layout", new J.Raw(layout)), J.P("software", software),
                J.P("hostWallpaper", HostWallpaper.On),
                J.P("hostWallpaper", false), J.P("colourNotificationIcons", true), J.P("navigationMode", "buttons"),
                J.P("androidNotifications", false), J.P("notificationLight", false));
        }

        /// Windows' version, a.b.c.d, from the device family version.
        private static string OsVersion()
        {
            ulong v;
            if (!ulong.TryParse(AnalyticsInfo.VersionInfo.DeviceFamilyVersion, out v)) return "";
            return ((v >> 48) & 0xFFFF) + "." + ((v >> 32) & 0xFFFF) + "." + ((v >> 16) & 0xFFFF) + "." + (v & 0xFFFF);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint Length, MemoryLoad;
            public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

        private static long TotalRam()
        {
            try
            {
                var s = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
                return GlobalMemoryStatusEx(ref s) ? (long)s.TotalPhys : 0;
            }
            catch (Exception) { return 0; }
        }

        /// lunakit.log (and the one before it) into Downloads, as AndroidLuna's LogExport writes
        /// one into Android's Download folder; it lands in Downloads\Lunacy, Windows' place for
        /// an app's downloads.
        private async Task<string> SaveLog()
        {
            var name = "lunacy-log-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt";
            var file = await DownloadsFolder.CreateFileAsync(name, CreationCollisionOption.GenerateUniqueName);
            var dir = ApplicationData.Current.LocalFolder.Path;
            var text = new System.Text.StringBuilder();
            text.AppendLine("Lunacy " + Package.Current.Id.Version.Major + "." + Package.Current.Id.Version.Minor + "." +
                Package.Current.Id.Version.Build + "." + Package.Current.Id.Version.Revision + " on Windows " + OsVersion());
            foreach (var f in new[] { "lunakit.1.log", "lunakit.log" })
            {
                var path = Path.Combine(dir, f);
                if (!File.Exists(path)) continue;
                using (var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var r = new StreamReader(s)) text.Append(await r.ReadToEndAsync());
            }
            await FileIO.WriteTextAsync(file, text.ToString());
            lastLog = file;
            return J.Obj(J.P("returnValue", true), J.P("path", file.Path), J.P("name", file.Name), J.P("android", false));
        }
    }
}
