using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation.Metadata;
using Windows.Globalization;
using Windows.Graphics.Display;
using Windows.Storage;
using Windows.System.Display;
using Windows.System.UserProfile;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Notifications;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace LunaKit
{
    /// Lunacy on Windows 10 Mobile: the Luna shell and every webOS app in one WebView (SHELL.md,
    /// design B). The shell's own page (`/__lunacy/shell/`, HTML) draws the launcher, the cards,
    /// the card view, dashboards and popup alerts, and holds every webOS window as a frame;
    /// this side owns the services, the bus and each window's native half (AppWindow), as
    /// AndroidLuna's ShellActivity and card host do.
    public sealed class LunaShell
    {
        internal readonly DeviceProfile Profile = DeviceProfile.ForThisDevice;
        internal readonly Bus Bus = new Bus();
        internal readonly CoreDispatcher Dispatcher;
        internal readonly WebView View;
        internal readonly AppRegistry Apps;
        private readonly AppServer server;
        private readonly DisplayService display;
        private readonly LunaHostBridge bridge;
        private readonly ConcurrentDictionary<int, AppWindow> windows = new ConcurrentDictionary<int, AppWindow>();
        private string deviceInfo = "{}", localeInfo = "{}", origin = "";
        private int pid = 1000;
        private volatile string orientation = "up";

        /// `webView`: the XAML WebView the shell draws in. `assets`: the folder
        /// assemble-assets.ps1 filled (apps/, lunacy/, fonts/, fw/, shell/, luna/).
        public LunaShell(WebView webView, string assets)
        {
            View = webView;
            Dispatcher = webView.Dispatcher;
            // Installed packages live in the webOS tree's /media/cryptofs/apps, as on a device.
            var cryptofs = Path.Combine(ApplicationData.Current.LocalFolder.Path, "webos", "media", "cryptofs", "apps");
            Apps = new AppRegistry(Path.Combine(assets, "apps"), Path.Combine(cryptofs, "usr", "palm", "applications"));
            server = new AppServer(Apps, assets, StaticsFor);
            bridge = new LunaHostBridge(this);
            new SystemProperties(Profile).Register(Bus);
            new ConnectionManager().Register(Bus);
            new Keys(Dispatcher).Register(Bus);
            display = new DisplayService(this);
            display.Register(Bus);
            Lunacy = new LunacyService(this);
            Lunacy.Register(Bus);
            SystemPrefs = new SystemService(Path.Combine(ApplicationData.Current.LocalFolder.Path, "preferences.json"), Profile);
            SystemPrefs.Register(Bus);
            Rom = Path.Combine(assets, "rootfs");
            Db = new Db8("com.palm.db", Path.Combine(ApplicationData.Current.LocalFolder.Path, "db8.sqlite"));
            Db.Register(Bus);
            TempDb = new Db8("com.palm.tempdb", null);
            TempDb.Register(Bus);
            Writable = Path.Combine(ApplicationData.Current.LocalFolder.Path, "webos");
            Fs = new WebosFs(Writable, Rom, Apps);
            Fs.User = new UserFiles(Path.Combine(Writable, "media", "internal"));
            server.Fs = Fs;
            server.AndroidIcon = Notifications.DashboardIcon;
            Services = new JsServices(this, Fs);
            Handlers = new ContentHandlers(Apps, Rom);
            Packages = new Packages(cryptofs, Path.Combine(ApplicationData.Current.LocalCacheFolder.Path, "packages"), Fs);
            AddedLaunchPoints = new LaunchPoints(Path.Combine(Writable, "var", "luna", "launchpoints"));
            AppManager = new ApplicationManager(this);
            AppManager.Register(Bus);
            new AppInstallService(this).Register(Bus);
            // After db8, which it reads its persisted activities from; it runs none until Start.
            Activities = new ActivityManager(Bus);
            Activities.Register();
            // The rest of the native services webOS apps call (AndroidLuna's ShellActivity.registerServices).
            Power = new PowerService(this);
            Power.Register(Bus);
            new KeyManager(Path.Combine(ApplicationData.Current.LocalFolder.Path, "keymanager.json")).Register(Bus);
            new MediaBroadcast().Register(Bus);
            new NetTools().Register(Bus);
            Dock = new DockMode(Apps);
            Dock.Register(Bus);
            new Ringtones(Fs).Register(Bus);
            new UpdateService(this).Register(Bus);
            new ImageService(Fs).Register(Bus);
            Audio = new AudioService(this);
            Audio.Register(Bus);
            Location = new LocationService(this);
            Location.Register(Bus);
            new DownloadManager(Fs).Register(Bus);
            Search = new UniversalSearch(Path.Combine(ApplicationData.Current.LocalFolder.Path, "universalsearch.json"), Apps);
            Search.Register(Bus);
            new BrowserServer(this).Register(Bus);
            // The Android account's two sides, from Windows' People and Calendar (WindowsAccount.cs).
            Contacts = new WindowsContacts(this);
            Contacts.Attach(Bus);
            Calendar = new WindowsCalendar(this, Contacts);
            Calendar.Attach(Bus);
            SystemPrefs.Fs = Fs;
            SystemPrefs.Rom = Rom;
            lunaAssets = Path.Combine(assets, "luna");
            SystemPrefs.PreferenceChanged += (key, value) => { if (key == "wallpaper") ShowWallpaper(); };
        }

        internal readonly LunacyService Lunacy;
        internal readonly ContentHandlers Handlers;
        internal readonly Packages Packages;
        internal readonly LaunchPoints AddedLaunchPoints;
        internal readonly ApplicationManager AppManager;
        internal readonly LocationService Location;
        internal readonly WindowsNotifications Notifications = new WindowsNotifications();
        internal readonly PowerService Power;
        internal readonly DockMode Dock;
        internal readonly AudioService Audio;
        internal readonly UniversalSearch Search;
        internal readonly WindowsContacts Contacts;
        internal readonly WindowsCalendar Calendar;
        /// The webOS root's writable part (LocalState\webos).
        internal readonly string Writable;
        private Configurator configurator;

        /// The webOS filesystem JS services see, and the services themselves.
        internal readonly WebosFs Fs;
        internal readonly JsServices Services;
        internal readonly ActivityManager Activities;

        /// The webOS root's read-only part (assemble-assets.ps1: the ROM's /usr/palm and /etc).
        internal readonly string Rom;
        internal readonly Db8 Db, TempDb;
        internal readonly SystemService SystemPrefs;

        /// Shows the shell. On the UI thread.
        public void Start()
        {
            Apps.Scan();
            // The phone's libraries under /media/internal: found off the UI thread; until then a
            // path there is Lunacy's own tree.
            System.Threading.Tasks.Task.Run(async () =>
            {
                await Fs.User.Open();
                SystemPrefs.InstallMedia(lunaAssets);
                WindowsAccountTemplate.Install(Fs);
                ShowWallpaper();
            });
            configurator = new Configurator(Apps, Db, TempDb, Rom, Bus) { Writable = Writable };
            configurator.Run();
            Services.Scan();
            // Activity files once the JS services are known, then the activity manager runs them.
            configurator.Activities();
            Activities.Enable();
            Power.Enable();
            Background.Shell = this;
            var bg = Background.Start(this);
            Notifications.Changed += NotificationsChanged;
            Notifications.Start();
            Windows.UI.Xaml.Application.Current.Resuming += (s, e) => { LunaLog.Write("[app] resumed, " + Background.Memory()); Activities.Resumed(); Power.Due(); var x = Background.Extend(); };
            Windows.UI.Xaml.Application.Current.Suspending += (s, e) => LunaLog.Write("[app] suspending, " + Background.Memory());
            Windows.UI.Xaml.Application.Current.EnteredBackground += (s, e) => LunaLog.Write("[app] in the background, " + Background.Memory());
            Windows.UI.Xaml.Application.Current.LeavingBackground += (s, e) => LunaLog.Write("[app] coming to the front, " + Background.Memory());
            Windows.System.MemoryManager.AppMemoryUsageIncreased += (s, e) => LunaLog.Write("[app] memory use rising: " + Background.Memory());
            Windows.System.MemoryManager.AppMemoryUsageLimitChanging += (s, e) => LunaLog.Write("[app] memory limit changing " + (e.OldLimit >> 20) + " -> " + (e.NewLimit >> 20) + " MB, " + Background.Memory());
            current = this;
            View.NavigationCompleted += (s, e) => { if (e.IsSuccess && !shellLoaded) { shellLoaded = true; ShowWallpaper(); Audio.Apply(); ShellLoaded(); OpenPendingFiles(); } };
            Measure();
            LunaLog.Write("[LunaKit] shell starting as " + Profile.Name + " " + deviceInfo);
            Windows.UI.Xaml.Window.Current.VisibilityChanged += (s, e) => { LunaLog.Write("[app] " + (e.Visible ? "visible" : "hidden") + ", " + Background.Memory()); display.Visible(e.Visible); };
            DisplayInformation.GetForCurrentView().OrientationChanged += (s, e) => Measure();
            SystemNavigationManager.GetForCurrentView().BackRequested += (s, e) =>
            {
                // Back is webOS's back gesture for the card that is up, and closes what the
                // shell has open over the cards; it never leaves Lunacy (Start does).
                e.Handled = true;
                Evaluate("window.__lunacyShell&&__lunacyShell.back()");
            };
            StatusBarStyle();
            View.NavigationStarting += (s, e) =>
            {
                // The shell's page is the only top-level document; every window is a frame of
                // it and reaches the bridge through top.LunaHost (w10m-native.js).
                if (Services != null) Services.Reset();
                foreach (var w in windows.Values) w.Close();
                windows.Clear();
                View.AddWebAllowedObject("LunaHost", bridge);
                LunaLog.Write("[LunaKit] shell page " + e.Uri);
            };
            View.NavigationFailed += (s, e) => LunaLog.Write("[LunaKit] shell page failed: " + e.WebErrorStatus + " " + e.Uri);
            View.NewWindowRequested += (s, e) =>
            {
                // window.open is the shell's (w10m-native.js); this is a target=_blank link.
                e.Handled = true;
                Launch("com.palm.app.browser", J.Obj(J.P("target", e.Uri.ToString())), null);
            };
            View.UnsupportedUriSchemeIdentified += (s, e) =>
            {
                e.Handled = true;
                var _ = Windows.System.Launcher.LaunchUriAsync(e.Uri);
            };
            var page = View.BuildLocalStreamUri("luna", AppServer.ShellPage);
            origin = page.Scheme + "://" + page.Host;
            View.NavigateToLocalStreamUri(page, server);
        }

        private bool shellLoaded;
        private string lunaAssets;

        /// The wallpaper preference's picture behind the cards, served from the webOS tree, with
        /// the shipped copy under it while the library's is unreadable.
        private void ShowWallpaper()
        {
            var path = SystemPrefs.WallpaperPath();
            var url = string.Join("/", path.Split('/').Select(Uri.EscapeDataString));
            Evaluate("window.__lunacyShell&&__lunacyShell.setWallpaper(" + J.Str(url) + "," + J.Str("/__lunacy/luna/wallpapers/" + SystemService.DefaultWallpaper) + ")");
        }

        // ---- Exhibition (AndroidLuna's ExhibitionController, from the host's side) ----

        private volatile bool exhibiting;
        private DisplayRequest exhibitionAwake;
        internal bool Exhibiting { get { return exhibiting; } }

        /// display/control/setState dock or undock. The faces are the apps switched on in
        /// Palm's Exhibition app; the screen stays on while the mode does, as a TouchPad on its
        /// Touchstone stayed lit.
        internal void SetExhibition(bool on)
        {
            exhibiting = on;
            if (on)
            {
                var faces = new JArr();
                foreach (var a in Apps.All())
                    if (Dock.IsEnabled(a.Id)) faces.Add(new JObj().Put("appId", a.Id).Put("title", Dock.Title(a.Id)).Put("icon", a.Url(a.Icon)));
                Evaluate("window.__lunacyShell&&__lunacyShell.setExhibition(true," + J.Str(faces.ToString()) + ")");
            }
            else Evaluate("window.__lunacyShell&&__lunacyShell.setExhibition(false)");
            HoldForExhibition(on);
        }

        private void HoldForExhibition(bool on)
        {
            Ui(() =>
            {
                if (on && exhibitionAwake == null) { exhibitionAwake = new DisplayRequest(); exhibitionAwake.RequestActive(); }
                else if (!on && exhibitionAwake != null) { exhibitionAwake.RequestRelease(); exhibitionAwake = null; }
            });
        }

        /// Bridge.ShellEvent.
        internal void ShellEvent(string what, string arg)
        {
            if (what == "exhibition" && arg == "off") { exhibiting = false; HoldForExhibition(false); }
            // The launcher's Delete: an added launch point, or an installed app with its package.
            else if (what == "remove" && arg != null)
            {
                if (arg.StartsWith("lp:", StringComparison.Ordinal))
                {
                    var err = AppManager.RemoveLaunchPoint(arg.Substring(3));
                    if (!string.IsNullOrEmpty(err)) LunaLog.Write("[LunaKit] remove launch point " + arg + ": " + err);
                }
                else
                {
                    var app = Apps.Get(arg);
                    if (app != null && app.Removable)
                        RemoveApp(app, e => { if (e != null) Toast("Couldn't delete " + app.Title, e); });
                }
            }
        }

        // ---- apps and packages (AndroidLuna's ShellActivity: install, removeInstalled, appsChanged) ----

        /// Private bus only on webOS: the system, Lunacy's own and the ROM's apps, and services.
        internal bool Privileged(string appId)
        {
            if (string.IsNullOrEmpty(appId)) return false;
            if (appId == SystemCaller || Bus.Has(appId) || appId.StartsWith("org.webosarchive.lunacy", StringComparison.Ordinal)) return true;
            var a = Apps.Get(appId);
            return a != null && !a.Removable;
        }

        /// The added launch points the launcher shows: those whose app is installed.
        private string LaunchPointsJson()
        {
            var list = new JArr();
            foreach (var lp in AddedLaunchPoints.All)
            {
                var app = Apps.Get(lp.AppId);
                if (app == null) continue;
                list.Add(new JObj().Put("id", "lp:" + lp.LaunchPointId).Put("appId", lp.AppId).Put("title", lp.Title)
                    .Put("icon", IconUrl(lp.Icon, app)).Put("params", lp.Params ?? "").Put("removable", lp.Removable));
            }
            return list.ToString();
        }

        /// A launch point's icon, a webOS path, as a URL the shell can draw.
        private static string IconUrl(string path, AppInfo app)
        {
            var p = WebosFs.Normal(path ?? "");
            var appsDir = "/" + AppServer.AppsPath;
            if (p.StartsWith(appsDir, StringComparison.Ordinal)) return string.Join("/", p.Split('/').Select(Uri.EscapeDataString));
            if (AppServer.Readable(p.TrimStart('/'))) return string.Join("/", p.Split('/').Select(Uri.EscapeDataString));
            return app.Url(app.Icon);
        }

        /// The launcher and dock drawn again: the app list or the launch points changed.
        internal void RefreshLauncher()
        {
            Evaluate("window.__lunacyShell&&__lunacyShell.setApps(" + J.Str(Apps.Json()) + "," + J.Str(LaunchPointsJson()) + ")");
        }

        /// The installed apps changed on disk: rescan, configure what came, and tell whoever watches.
        internal void AppsChanged()
        {
            var before = Apps.All().ToDictionary(a => a.Id, a => a);
            Apps.Scan();
            var after = Apps.All().ToDictionary(a => a.Id, a => a);
            try { configurator.Run(); } catch (Exception e) { LunaLog.Write("[LunaKit] configure after rescan: " + e.Message); }
            Services.Scan();
            configurator.Activities();
            foreach (var a in after.Values) if (!before.ContainsKey(a.Id)) AppManager.LaunchPointChanged(a, "added");
                else if (before[a.Id].Version != a.Version) AppManager.LaunchPointChanged(a, "updated");
            foreach (var a in before.Values) if (!after.ContainsKey(a.Id)) AppManager.LaunchPointChanged(a, "removed");
            Dock.LaunchPointsChanged();
            Search.AppsChanged();
            RefreshLauncher();
        }

        /// Installs a package (an .ipk by URL or webOS path) as Preware did: toasts for its start
        /// and end when `banners`, then the rescan. `progress` hears 0..100.
        internal void Install(string source, string requester, Action<int> progress, bool banners, Action<Packages.Result> done)
        {
            LunaLog.Write("[LunaKit] install " + source + " for " + requester);
            string title = null;
            Packages.Install(source, progress, t =>
            {
                title = t;
                if (banners) Toast("Installing " + t, "");
            }, r =>
            {
                if (r.Error == null)
                {
                    Ui(() =>
                    {
                        AppsChanged();
                        if (banners) Toast((title ?? r.PackageId) + " installed", "");
                    });
                }
                else if (banners) Toast("Couldn't install " + (title ?? source.Substring(source.LastIndexOf('/') + 1)), r.Error);
                if (done != null) done(r);
            });
        }

        /// Removes an installed app: its windows closed first, then its package; then the rescan.
        internal void RemoveApp(AppInfo app, Action<string> done)
        {
            Evaluate("window.__lunacyShell&&__lunacyShell.closeApp(" + J.Str(app.Id) + ")");
            Packages.Remove(app.Id, app.Dir, error =>
            {
                if (error == null) Ui(AppsChanged);
                done(error);
            });
        }

        /// Each window that is up, by app id and its process id (the window's id here).
        internal List<KeyValuePair<string, int>> RunningWindows()
        {
            return windows.Values.OrderBy(w => w.Id).Select(w => new KeyValuePair<string, int>(w.AppId, w.Id)).ToList();
        }

        // ---- files opened from Windows (the manifest's file types) ----

        private static readonly List<Windows.Storage.IStorageItem> pendingFiles = new List<Windows.Storage.IStorageItem>();
        private static LunaShell current;

        /// Files Windows opened Lunacy with (a PDF from Outlook, an .ipk from Edge): each copied into
        /// /media/internal/downloads and opened as webOS would a download: a package installed,
        /// anything else in the app that handles its type.
        public static void FilesActivated(IReadOnlyList<Windows.Storage.IStorageItem> files)
        {
            lock (pendingFiles) pendingFiles.AddRange(files);
            var s = current;
            if (s != null && s.shellLoaded) s.OpenPendingFiles();
        }

        private void OpenPendingFiles()
        {
            List<Windows.Storage.IStorageItem> files;
            lock (pendingFiles) { files = pendingFiles.ToList(); pendingFiles.Clear(); }
            if (files.Count == 0) return;
            System.Threading.Tasks.Task.Run(async () =>
            {
                foreach (var item in files.OfType<Windows.Storage.StorageFile>())
                {
                    try
                    {
                        var buf = await Windows.Storage.FileIO.ReadBufferAsync(item);
                        var dest = "/media/internal/downloads/" + item.Name;
                        if (!Fs.Exists("/media/internal/downloads")) Fs.MakeDir("/media/internal/downloads", true);
                        Fs.Write(dest, System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(buf), false);
                        LunaLog.Write("[LunaKit] opened from Windows: " + item.Name);
                        if (item.Name.EndsWith(".ipk", StringComparison.OrdinalIgnoreCase)) Install(dest, SystemCaller, null, true, null);
                        else Bus.Dispatch(SystemCaller, "palm://com.palm.applicationManager/open", new JObj().Put("target", "file://" + dest).ToString(),
                            r => { if (!Jx.ObjOrEmpty(r).Bool("returnValue")) Toast("No app here opens " + item.Name, ""); });
                    }
                    catch (Exception e) { LunaLog.Write("[LunaKit] can't open " + item.Name + ": " + e.Message); }
                }
            });
        }

        /// A notification of Lunacy's own (an update, a download): a Windows toast.
        internal void Toast(string title, string text)
        {
            Ui(() =>
            {
                try
                {
                    var xml = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
                    var lines = xml.GetElementsByTagName("text");
                    lines[0].AppendChild(xml.CreateTextNode(title ?? ""));
                    lines[1].AppendChild(xml.CreateTextNode(text ?? ""));
                    ToastNotificationManager.CreateToastNotifier().Show(new ToastNotification(xml));
                }
                catch (Exception e) { LunaLog.Write("[LunaKit] toast failed: " + e.Message); }
            });
        }

        // ---- First Use (AndroidLuna's ShellActivity: onCreate's launch, afterFirstUse) ----

        internal const string FirstUseApp = "org.webosarchive.lunacy.firstuse";

        /// The shell's page is up: First Use if it hasn't run, otherwise what follows it each start.
        private void ShellLoaded()
        {
            if (!LunacyService.Flag(LunacyService.FirstUseKey))
            {
                if (Apps.Get(FirstUseApp) != null) { Launch(FirstUseApp, "", null); return; }
                LunaLog.Write("[LunaKit] no First Use app; taking it as done");
                LunacyService.SetFlag(LunacyService.FirstUseKey);
            }
            AfterFirstUse(true);
        }

        /// firstUse/done.
        internal void FirstUseDone() { AfterFirstUse(false); }

        /// What a webOS device did at each boot once First Use had run (ShellActivity.afterFirstUse):
        /// ran-first-use for the configurator, and the profile account First Use would have made
        /// (firstuse-createDefaultAccount asks com.palm.accountservices; the service writes
        /// first-use-profile-created). The activity files held back until both flags exist are
        /// sent then. At a start, a page First Use has since gained is shown by itself, once:
        /// Running in the Background, then Location (one page per start).
        private void AfterFirstUse(bool atStart)
        {
            if (atStart && Apps.Get(FirstUseApp) != null)
            {
                if (!LunacyService.Flag(LunacyService.BackgroundAskedKey) && !Background.Granted)
                {
                    LunacyService.SetFlag(LunacyService.BackgroundAskedKey);
                    Launch(FirstUseApp, J.Obj(J.P("only", "background")), null);
                }
                else if (!LunacyService.Flag(LunacyService.LocationAskedKey) && !LunacyService.LocationGranted())
                    Launch(FirstUseApp, J.Obj(J.P("only", "location")), null);
            }
            if (Notifications.Any()) NotificationsChanged();
            var prefs = Path.Combine(Writable, "var", "luna", "preferences");
            var ranFirstUse = Path.Combine(prefs, "ran-first-use");
            var profileMade = Path.Combine(prefs, "first-use-profile-created");
            bool configureAfter = !File.Exists(ranFirstUse) || !File.Exists(profileMade);
            try { Directory.CreateDirectory(prefs); if (!File.Exists(ranFirstUse)) File.WriteAllText(ranFirstUse, ""); }
            catch (Exception e) { LunaLog.Write("[LunaKit] first use: can't write ran-first-use: " + e.Message); }
            if (File.Exists(profileMade)) { if (configureAfter) configurator.Activities(); return; }
            Bus.Dispatch(SystemCaller, "palm://com.palm.accountservices/createNovaAccount", "{\"createDefaultAccount\":true}",
                r => LunaLog.Write("[LunaKit] firstuse-createDefaultAccount: " + r));
            // The service answers before the account is made and writes the flag as it makes it.
            ConfigureOnceProfileMade(profileMade, 60000);
        }

        private void ConfigureOnceProfileMade(string flag, int leftMs)
        {
            if (File.Exists(flag))
            {
                LunaLog.Write("[LunaKit] first use: profile account made, configuring what waited for it");
                configurator.Activities();
                return;
            }
            if (leftMs <= 0) { LunaLog.Write("[LunaKit] first use: no profile account after 60 s; the rest is configured at the next start"); return; }
            System.Threading.Tasks.Task.Delay(500).ContinueWith(_ => ConfigureOnceProfileMade(flag, leftMs - 500));
        }

        /// The system's own caller on the bus, as luna-send run by upstart was: inside every
        /// package's private methods (JsServices).
        internal const string SystemCaller = "com.palm.systemmanager";

        /// A background task's wake (Background.cs): what came due while Lunacy was away.
        internal void BackgroundWake()
        {
            Activities.Resumed();
            Power.Due();
        }

        /// Windows' notifications changed: the Notifications app hears it, and is started when
        /// there is something to show and nothing listening (ShellActivity.androidNotificationsChanged).
        private void NotificationsChanged()
        {
            Lunacy.NotificationsChanged();
            if (Lunacy.NotificationsWatched || !Notifications.Any()) return;
            if (!shellLoaded || !LunacyService.Flag(LunacyService.FirstUseKey)) return;
            if (Apps.Get(WindowsNotifications.AppId) == null) return;
            Launch(WindowsNotifications.AppId, "", null);
        }

        private void Measure()
        {
            var di = DisplayInformation.GetForCurrentView();
            double z = di.RawPixelsPerViewPixel;
            double w = di.ScreenWidthInRawPixels / z, h = di.ScreenHeightInRawPixels / z;
            deviceInfo = Profile.DeviceInfo(Math.Min(w, h), Math.Max(w, h));
            var lang = new Language(GlobalizationPreferences.Languages.FirstOrDefault() ?? "en-US");
            var langCode = lang.LanguageTag.Split('-')[0].ToLowerInvariant();
            var region = (GlobalizationPreferences.HomeGeographicRegion ?? "US").ToLowerInvariant();
            bool h24 = GlobalizationPreferences.Clocks.FirstOrDefault() == ClockIdentifiers.TwentyFourHour;
            localeInfo = J.Obj(J.P("locale", langCode + "_" + region), J.P("localeRegion", region), J.P("phoneRegion", region),
                J.P("timeFormat", h24 ? "HH24" : "HH12"));
            try { pid = (int)Windows.System.Diagnostics.ProcessDiagnosticInfo.GetForCurrentProcess().ProcessId; } catch (Exception) { }
            switch (di.CurrentOrientation)
            {
                case DisplayOrientations.PortraitFlipped: orientation = "down"; break;
                case DisplayOrientations.Landscape: orientation = "left"; break;
                case DisplayOrientations.LandscapeFlipped: orientation = "right"; break;
                default: orientation = "up"; break;
            }
        }

        /// The values w10m-native.js is served with for one app's pages.
        private string StaticsFor(string appId)
        {
            return J.Obj(J.P("appId", appId), J.P("deviceInfo", deviceInfo), J.P("localeInfo", localeInfo), J.P("processId", pid), J.P("userAgent", Profile.UserAgent));
        }

        /// What the shell's page needs at start: the apps, and how pages are laid out.
        internal string ShellInfo()
        {
            return J.Obj(J.P("phone", Profile.Name == "PRE3"), J.P("layoutWidth", FixedViewport.LayoutWidth),
                J.P("hour24", Windows.System.UserProfile.GlobalizationPreferences.Clocks.FirstOrDefault() == Windows.Globalization.ClockIdentifiers.TwentyFourHour),
                J.P("zoomApps", new J.Raw("[" + string.Join(",", FixedViewport.OnIds(Apps.All()).Select(J.Str)) + "]")),
                J.P("apps", new J.Raw(Apps.Json())), J.P("launchPoints", new J.Raw(LaunchPointsJson())));
        }

        internal string Orientation { get { return orientation; } }
        internal int ProcessId { get { return pid; } }
        internal string Origin { get { return origin; } }

        // MARK: windows

        internal AppWindow Window(int id)
        {
            AppWindow w;
            return windows.TryGetValue(id, out w) ? w : null;
        }

        internal void Opened(int id, string appId, string type)
        {
            var w = new AppWindow(this, id, appId, type);
            var old = windows.AddOrUpdate(id, w, (k, o) => { o.Close(); return w; });
            LunaLog.Write("[LunaKit] window " + id + " " + type + " " + appId);
        }

        internal void Closed(int id)
        {
            AppWindow w;
            if (windows.TryRemove(id, out w))
            {
                w.Close();
                LunaLog.Write("[LunaKit] window " + id + " closed (" + w.AppId + ")");
                if (!windows.Values.Any(x => x.AppId == w.AppId)) Location.AppEnded(w.AppId);
            }
        }

        /// Starts an app, or brings it forward with new params (the shell's launch: its root
        /// window's relaunch, or a new card). `paramsJson`: "" for none. On any thread.
        internal void Launch(string appId, string paramsJson, string launchedBy)
        {
            var lp = AddedLaunchPoints == null ? null : AddedLaunchPoints.Get(appId ?? "");
            if (lp != null) { appId = lp.AppId; paramsJson = lp.Params ?? ""; }
            Evaluate("window.__lunacyShell&&__lunacyShell.launch(" + J.Str(appId) + "," + J.Str(paramsJson ?? "") + "," + J.Str(launchedBy ?? "") + ")");
        }

        internal void Ui(Action a)
        {
            if (Dispatcher.HasThreadAccess) a();
            else { var _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => a()); }
        }

        internal T OnUi<T>(Func<T> f)
        {
            if (Dispatcher.HasThreadAccess) return f();
            T r = default(T);
            Dispatcher.RunAsync(CoreDispatcherPriority.High, () => r = f()).AsTask().Wait();
            return r;
        }

        /// Runs script in the shell's page.
        internal void Evaluate(string js)
        {
            Ui(async () =>
            {
                try { await View.InvokeScriptAsync("eval", new[] { js }); }
                catch (Exception e) { LunaLog.Write("[LunaKit] shell script failed: " + e.Message); }
            });
        }

        /// Luna's status bar was light text on black, and the cards sit on black below it.
        private static void StatusBarStyle()
        {
            if (!ApiInformation.IsTypePresent("Windows.UI.ViewManagement.StatusBar")) return;
            var bar = StatusBar.GetForCurrentView();
            bar.BackgroundColor = Colors.Black;
            bar.BackgroundOpacity = 1;
            bar.ForegroundColor = Colors.White;
        }

        internal async void SetFullScreen(bool on)
        {
            if (!ApiInformation.IsTypePresent("Windows.UI.ViewManagement.StatusBar")) return;
            var bar = StatusBar.GetForCurrentView();
            if (on) await bar.HideAsync(); else await bar.ShowAsync();
        }
    }

    /// One webOS window's native half: its bus calls, network requests and databases, and
    /// what it asks of the shell (AndroidLuna's AppWindow.kt). The window itself is a frame in
    /// the shell's page; script for it goes through the shell (`__lunacyShell.run`).
    internal sealed class AppWindow
    {
        public readonly int Id;
        public readonly string AppId, Type;
        private readonly LunaShell shell;
        private readonly Dictionary<int, Bus.Call> calls = new Dictionary<int, Bus.Call>();
        private readonly HashSet<string> notified = new HashSet<string>();
        public readonly NetShim Net;
        public readonly WebSql Sql;
        private DisplayRequest keepAwake;
        private static int banners;
        private static int threadLogged;

        public AppWindow(LunaShell shell, int id, string appId, string type)
        {
            this.shell = shell;
            Id = id;
            AppId = appId;
            Type = type;
            Net = new NetShim(appId, shell.Profile, n => Evaluate("window.__lunacyNetDone&&__lunacyNetDone(" + n + ")"));
            Sql = new WebSql(appId, n => Evaluate("window.__lunacySqlDone&&__lunacySqlDone(" + n + ")"));
        }

        /// A new page in the window: the old one's requests, statements and subscriptions end.
        public void PageStart()
        {
            Net.Reset(); Sql.Reset();
            Bus.Call[] old;
            lock (calls) { old = calls.Values.ToArray(); calls.Clear(); }
            foreach (var c in old) c.Cancel();
        }

        public void Close()
        {
            PageStart();
            BlockScreenTimeout(false);
        }

        public void Evaluate(string js)
        {
            shell.Evaluate("window.__lunacyShell&&__lunacyShell.run(" + Id + "," + J.Str(js) + ")");
        }

        private void Called()
        {
            // Which thread a WebView calls an [AllowForWeb] object on isn't documented for W10M:
            // logged once, so it is measured rather than assumed.
            if (Interlocked.Exchange(ref threadLogged, 1) == 0)
                LunaLog.Write("[LunaKit] bridge calls arrive on the " + (shell.Dispatcher.HasThreadAccess ? "UI" : "a background") + " thread");
        }

        public void BusCall(int token, string url, string parameters)
        {
            Called();
            Bus.Call old;
            lock (calls) { if (calls.TryGetValue(token, out old)) calls.Remove(token); }
            if (old != null) old.Cancel();
            var c = shell.Bus.Dispatch(AppId, url, parameters, reply => Evaluate("PalmServiceBridge.__reply(" + token + "," + J.Str(reply) + ")"));
            if (c.Subscribe && !c.Cancelled) lock (calls) calls[token] = c;
        }

        public void BusCancel(int token)
        {
            Bus.Call c;
            lock (calls) { if (!calls.TryGetValue(token, out c)) return; calls.Remove(token); }
            c.Cancel();
        }

        public void Log(string kind, string msg) { LunaLog.Write("[" + AppId + "] " + kind + " " + msg); }

        public void FullScreen(bool on) { shell.Ui(() => shell.SetFullScreen(on)); }

        public void RequestOrientation(string o)
        {
            DisplayOrientations pref;
            switch (o)
            {
                case "up": pref = DisplayOrientations.Portrait; break;
                case "down": pref = DisplayOrientations.PortraitFlipped; break;
                case "left": pref = DisplayOrientations.Landscape; break;
                case "right": pref = DisplayOrientations.LandscapeFlipped; break;
                default: pref = DisplayOrientations.None; break;  // "free"
            }
            shell.Ui(() => DisplayInformation.AutoRotationPreferences = pref);
        }

        public void BlockScreenTimeout(bool block)
        {
            shell.Ui(() =>
            {
                if (block && keepAwake == null) { keepAwake = new DisplayRequest(); keepAwake.RequestActive(); }
                else if (!block && keepAwake != null) { keepAwake.RequestRelease(); keepAwake = null; }
            });
        }

        /// A banner, as Windows' own: a toast, which on a phone drops in at the top of the screen
        /// as Luna's banners rose at the bottom (README, "Where W10M differs").
        public int AddBanner(string message, string icon)
        {
            int id = Interlocked.Increment(ref banners);
            shell.Ui(() =>
            {
                try
                {
                    var xml = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText01);
                    xml.GetElementsByTagName("text")[0].AppendChild(xml.CreateTextNode(message ?? ""));
                    var audio = xml.CreateElement("audio");
                    audio.SetAttribute("silent", "true");
                    xml.DocumentElement.AppendChild(audio);
                    ToastNotificationManager.CreateToastNotifier().Show(new ToastNotification(xml) { Tag = "b" + id });
                }
                catch (Exception e) { LunaLog.Write("[LunaKit] banner failed: " + e.Message); }
            });
            return id;
        }

        public void Notify(string what, string arg)
        {
            switch (what)
            {
                case "removeBanner":
                    int id;
                    if (int.TryParse(arg, out id)) shell.Ui(() => { try { ToastNotificationManager.History.Remove("b" + id); } catch (Exception) { } });
                    return;
                case "clearBanners":
                    shell.Ui(() => { try { ToastNotificationManager.History.Clear(); } catch (Exception) { } });
                    return;
                // Windows owns the keyboard: it shows for a focused field on its own.
                case "keyboard":
                case "keyboardResizes":
                    return;
            }
            bool first;
            lock (notified) first = notified.Add(what);
            if (first) LunaLog.Write("[LunaKit] [" + AppId + "] LunacyNative." + what + " isn't on W10M yet");
        }

        public void SetClipboard(string text)
        {
            shell.Ui(() =>
            {
                var p = new DataPackage();
                p.SetText(text ?? "");
                Clipboard.SetContent(p);
            });
        }

        public bool ClipboardHasText()
        {
            return shell.OnUi(() => { try { return Clipboard.GetContent().Contains(StandardDataFormats.Text); } catch (Exception) { return false; } });
        }

        public void Paste()
        {
            shell.Ui(async () =>
            {
                try
                {
                    var content = Clipboard.GetContent();
                    if (!content.Contains(StandardDataFormats.Text)) return;
                    var text = await content.GetTextAsync();
                    Evaluate("window.__lunacyPaste&&__lunacyPaste(" + J.Str(text) + ")");
                }
                catch (Exception e) { LunaLog.Write("[LunaKit] paste failed: " + e.Message); }
            });
        }
    }
}
