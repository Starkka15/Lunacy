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
        private readonly DisplayService display = new DisplayService();
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
            Apps = new AppRegistry(Path.Combine(assets, "apps"), Path.Combine(ApplicationData.Current.LocalFolder.Path, "apps"));
            server = new AppServer(Apps, assets, StaticsFor);
            bridge = new LunaHostBridge(this);
            new SystemProperties(Profile).Register(Bus);
            new ConnectionManager().Register(Bus);
            new Keys(Dispatcher).Register(Bus);
            display.Register(Bus);
            new ApplicationManager(this).Register(Bus);
            new LunacyService(this).Register(Bus);
            SystemPrefs = new SystemService(Path.Combine(ApplicationData.Current.LocalFolder.Path, "preferences.json"), Profile);
            SystemPrefs.Register(Bus);
            Rom = Path.Combine(assets, "rootfs");
            Db = new Db8("com.palm.db", Path.Combine(ApplicationData.Current.LocalFolder.Path, "db8.sqlite"));
            Db.Register(Bus);
            TempDb = new Db8("com.palm.tempdb", null);
            TempDb.Register(Bus);
        }

        /// The webOS root's read-only part (assemble-assets.ps1: the ROM's /usr/palm and /etc).
        internal readonly string Rom;
        internal readonly Db8 Db, TempDb;
        internal readonly SystemService SystemPrefs;

        /// Shows the shell. On the UI thread.
        public void Start()
        {
            Apps.Scan();
            new Configurator(Apps, Db, TempDb, Rom, Bus).Run();
            Measure();
            LunaLog.Write("[LunaKit] shell starting as " + Profile.Name + " " + deviceInfo);
            Windows.UI.Xaml.Window.Current.VisibilityChanged += (s, e) => display.Visible(e.Visible);
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
            return J.Obj(J.P("appId", appId), J.P("deviceInfo", deviceInfo), J.P("localeInfo", localeInfo), J.P("processId", pid));
        }

        /// What the shell's page needs at start: the apps, and how pages are laid out.
        internal string ShellInfo()
        {
            return J.Obj(J.P("phone", Profile.Name == "PRE3"), J.P("layoutWidth", FixedViewport.LayoutWidth),
                J.P("zoomApps", new J.Raw("[" + string.Join(",", FixedViewport.OnIds(Apps.All()).Select(J.Str)) + "]")),
                J.P("apps", new J.Raw(Apps.Json())));
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
            if (windows.TryRemove(id, out w)) { w.Close(); LunaLog.Write("[LunaKit] window " + id + " closed (" + w.AppId + ")"); }
        }

        /// Starts an app, or brings it forward with new params (the shell's launch: its root
        /// window's relaunch, or a new card). `paramsJson`: "" for none. On any thread.
        internal void Launch(string appId, string paramsJson, string launchedBy)
        {
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
