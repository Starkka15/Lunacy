using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Data.Json;
using Windows.Data.Xml.Dom;
using Windows.Foundation.Metadata;
using Windows.Globalization;
using Windows.Graphics.Display;
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
    /// A webOS app bundled into a UWP app, exactly as it was packaged, shown as one card filling
    /// the screen below the status bar. Owns the bus and the services, which every window of
    /// the app shares, and the root window's native half (AppWindow). iOSLuna's
    /// LunaCardViewController is the sibling; AndroidLuna's card host is the reference.
    public sealed class LunaCard
    {
        internal readonly string AppId;
        internal readonly DeviceProfile Profile = DeviceProfile.ForThisDevice;
        internal readonly Bus Bus = new Bus();
        internal readonly CoreDispatcher Dispatcher;
        internal readonly WebView View;
        private readonly string package;
        private readonly AppServer server;
        private readonly DisplayService display = new DisplayService();
        private AppWindow root;
        private string statics;

        /// `webView`: the XAML WebView the card draws in. `package`: the app's folder, holding its
        /// appinfo.json. `assets`: the folder assemble-assets.ps1 filled (lunacy/, fonts/, fw/).
        public LunaCard(WebView webView, string appId, string package, string assets)
        {
            View = webView;
            AppId = appId;
            this.package = package;
            Dispatcher = webView.Dispatcher;
            server = new AppServer(appId, package, assets, () => statics);
            new SystemProperties(Profile).Register(Bus);
            new ConnectionManager().Register(Bus);
            new Keys(Dispatcher).Register(Bus);
            display.Register(Bus);
            new ApplicationManager(appId, Dispatcher).Register(Bus);
        }

        /// Shows the app's main page. On the UI thread.
        public void Start()
        {
            statics = Statics();
            LunaLog.Write("[LunaKit] starting " + AppId + " as " + Profile.Name + " " + statics);
            Window.Current.VisibilityChanged += (s, e) => display.Visible(e.Visible);
            SystemNavigationManager.GetForCurrentView().BackRequested += (s, e) =>
            {
                // webOS's back gesture never closed an app: the card stays, as it did on webOS.
                e.Handled = true;
                if (root != null) root.SendBack();
            };
            StatusBarStyle();
            root = new AppWindow(this, View);
            View.NavigateToLocalStreamUri(View.BuildLocalStreamUri(AppId, AppServer.PagePath(AppId, Main())), server);
        }

        /// The app's own `main` page, from its appinfo.json.
        private string Main()
        {
            try
            {
                var info = J.ParseObject(File.ReadAllText(Path.Combine(package, "appinfo.json")));
                return info.GetString("main", "index.html");
            }
            catch (Exception e)
            {
                LunaLog.Write("[LunaKit] appinfo.json unreadable: " + e.Message);
                return "index.html";
            }
        }

        /// The values w10m-native.js is served with: they don't change while a page lives.
        private string Statics()
        {
            var di = DisplayInformation.GetForCurrentView();
            double z = di.RawPixelsPerViewPixel;
            double w = di.ScreenWidthInRawPixels / z, h = di.ScreenHeightInRawPixels / z;
            var lang = new Language(GlobalizationPreferences.Languages.FirstOrDefault() ?? "en-US");
            var langCode = lang.LanguageTag.Split('-')[0].ToLowerInvariant();
            var region = (GlobalizationPreferences.HomeGeographicRegion ?? "US").ToLowerInvariant();
            bool h24 = GlobalizationPreferences.Clocks.FirstOrDefault() == ClockIdentifiers.TwentyFourHour;
            var localeInfo = J.Obj(J.P("locale", langCode + "_" + region), J.P("localeRegion", region), J.P("phoneRegion", region),
                J.P("timeFormat", h24 ? "HH24" : "HH12"));
            int pid = 1000;
            try { pid = (int)Windows.System.Diagnostics.ProcessDiagnosticInfo.GetForCurrentProcess().ProcessId; } catch (Exception) { }
            return J.Obj(J.P("appId", AppId), J.P("deviceInfo", Profile.DeviceInfo(w, h)), J.P("localeInfo", localeInfo), J.P("processId", pid));
        }

        /// Luna's status bar was light text on black, and the card sits on black below it.
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

        /// Another app's origin, as this WebView names it (media.js maps file:// URLs with it).
        internal string Origin(string appId)
        {
            var u = View.BuildLocalStreamUri(appId, "/");
            return u.Scheme + "://" + u.Host;
        }
    }

    /// One webOS window: a WebView on the app's origin, and the native half of its
    /// window.LunacyNative (w10m-native.js, through LunaHostBridge). AndroidLuna's AppWindow.kt
    /// is the reference.
    internal sealed class AppWindow
    {
        private readonly LunaCard card;
        private readonly WebView view;
        private readonly LunaHostBridge bridge;
        private readonly Dictionary<int, Bus.Call> calls = new Dictionary<int, Bus.Call>();
        private readonly HashSet<string> notified = new HashSet<string>();
        public readonly NetShim Net;
        public readonly WebSql Sql;
        private DisplayRequest keepAwake;
        private int banners;
        private int threadLogged;
        // Read off the UI thread by the bridge; written on it.
        private volatile string screenSize = "{}", cardSize = "{}", orientation = "up";

        public AppWindow(LunaCard card, WebView view)
        {
            this.card = card;
            this.view = view;
            bridge = new LunaHostBridge(this);
            Net = new NetShim(card.AppId, card.Profile, id => Evaluate("window.__lunacyNetDone&&__lunacyNetDone(" + id + ")"));
            Sql = new WebSql(card.AppId, id => Evaluate("window.__lunacySqlDone&&__lunacySqlDone(" + id + ")"));
            view.NavigationStarting += (s, e) =>
            {
                // A new page: the old one's requests, statements and subscriptions end with it.
                Net.Reset(); Sql.Reset();
                Bus.Call[] old;
                lock (calls) { old = calls.Values.ToArray(); calls.Clear(); }
                foreach (var c in old) c.Cancel();
                // The page's way in, added again for every document.
                view.AddWebAllowedObject("LunaHost", bridge);
                LunaLog.Write("[LunaKit] [" + card.AppId + "] page " + e.Uri);
            };
            view.NavigationCompleted += (s, e) =>
            {
                if (!e.IsSuccess) { LunaLog.Write("[LunaKit] [" + card.AppId + "] page failed: " + e.WebErrorStatus + " " + e.Uri); return; }
                LogViewport(e.Uri);
            };
            view.NavigationFailed += (s, e) => LunaLog.Write("[LunaKit] [" + card.AppId + "] navigation failed: " + e.WebErrorStatus + " " + e.Uri);
            view.NewWindowRequested += (s, e) =>
            {
                // UWP's WebView can't hand a page a window it opened (README, "Still to do").
                e.Handled = true;
                LunaLog.Write("[LunaKit] [" + card.AppId + "] window.open " + e.Uri + " (not opened)");
            };
            view.UnsupportedUriSchemeIdentified += (s, e) =>
            {
                // A link off the app goes to the system, as webOS handed it to the browser.
                e.Handled = true;
                var _ = Windows.System.Launcher.LaunchUriAsync(e.Uri);
            };
            view.SizeChanged += (s, e) => Measure();
            DisplayInformation.GetForCurrentView().OrientationChanged += (s, e) => Measure();
            Measure();
        }

        /// What the page was laid out at, as AppWindow.kt logs it: the layout viewport, the device
        /// px per CSS px, and the viewport meta in force (layout.js writes it from cardSize).
        private async void LogViewport(Uri uri)
        {
            try
            {
                var r = await view.InvokeScriptAsync("eval", new[] {
                    "(function(){var m=document.querySelectorAll('meta[name=viewport]');" +
                    "return document.documentElement.clientWidth+'x'+document.documentElement.clientHeight+" +
                    "' dpr '+window.devicePixelRatio+' meta '+(m.length?m[m.length-1].content:'none');})()" });
                LunaLog.Write("[LunaKit] [" + card.AppId + "] loaded " + uri + " viewport " + r + " card " + cardSize);
            }
            catch (Exception ex) { LunaLog.Write("[LunaKit] [" + card.AppId + "] viewport unread: " + ex.Message); }
        }

        private void Measure()
        {
            var di = DisplayInformation.GetForCurrentView();
            double z = di.RawPixelsPerViewPixel;
            var bounds = Window.Current.Bounds;
            screenSize = J.Obj(J.P("width", (int)Math.Round(bounds.Width)), J.P("height", (int)Math.Round(bounds.Height)));
            // In page px, with `scale` the device px in each: layout.js pins the viewport at
            // scale / devicePixelRatio. Without Phone Zoom a page px is a view px; with it the
            // page is laid out larger and shown scaled down by f (AppWindow.kt's cardSize).
            double w = view.ActualWidth, h = view.ActualHeight;
            double f = FixedViewport.Fit(card.AppId, w, h);
            cardSize = J.Obj(J.P("width", (int)Math.Round(w / f)), J.P("height", (int)Math.Round(h / f)), J.P("scale", z * f));
            switch (di.CurrentOrientation)
            {
                case DisplayOrientations.PortraitFlipped: orientation = "down"; break;
                case DisplayOrientations.Landscape: orientation = "left"; break;
                case DisplayOrientations.LandscapeFlipped: orientation = "right"; break;
                default: orientation = "up"; break;
            }
        }

        private void Ui(Action a)
        {
            if (card.Dispatcher.HasThreadAccess) a();
            else { var _ = card.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => a()); }
        }

        public void Evaluate(string js)
        {
            Ui(async () =>
            {
                try { await view.InvokeScriptAsync("eval", new[] { js }); }
                catch (Exception e) { LunaLog.Write("[LunaKit] [" + card.AppId + "] script failed: " + e.Message); }
            });
        }

        /// The webOS back gesture: an ESC key event, which Enyo and Mojo turn into "back".
        public void SendBack()
        {
            Evaluate("(function(){function k(t){var e=document.createEvent('Events');e.initEvent(t,true,true);e.keyCode=27;e.which=27;(document.activeElement||document).dispatchEvent(e);}k('keydown');k('keyup');})()");
        }

        // MARK: what the bridge asks

        private void Called()
        {
            // Which thread a WebView calls an [AllowForWeb] object on isn't documented for W10M:
            // logged once, so it is measured rather than assumed.
            if (Interlocked.Exchange(ref threadLogged, 1) == 0)
                LunaLog.Write("[LunaKit] bridge calls arrive on the " + (card.Dispatcher.HasThreadAccess ? "UI" : "a background") + " thread");
        }

        public void BusCall(int token, string url, string parameters)
        {
            Called();
            Bus.Call old;
            lock (calls) { if (calls.TryGetValue(token, out old)) calls.Remove(token); }
            if (old != null) old.Cancel();
            var c = card.Bus.Dispatch(card.AppId, url, parameters, reply => Evaluate("PalmServiceBridge.__reply(" + token + "," + J.Str(reply) + ")"));
            if (c.Subscribe && !c.Cancelled) lock (calls) calls[token] = c;
        }

        public void BusCancel(int token)
        {
            Bus.Call c;
            lock (calls) { if (!calls.TryGetValue(token, out c)) return; calls.Remove(token); }
            c.Cancel();
        }

        public void Log(string kind, string msg) { LunaLog.Write("[" + card.AppId + "] " + kind + " " + msg); }

        public string AppOrigin(string appId)
        {
            if (card.Dispatcher.HasThreadAccess) return card.Origin(appId);
            string r = null;
            card.Dispatcher.RunAsync(CoreDispatcherPriority.High, () => r = card.Origin(appId)).AsTask().Wait();
            return r;
        }

        public string ScreenSize() { return screenSize; }
        public string CardSize() { return cardSize; }
        public string ScreenOrientation() { return orientation; }

        public void FullScreen(bool on) { Ui(() => card.SetFullScreen(on)); }

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
            Ui(() => DisplayInformation.AutoRotationPreferences = pref);
        }

        public void BlockScreenTimeout(bool block)
        {
            Ui(() =>
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
            Ui(() =>
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
                    if (int.TryParse(arg, out id)) Ui(() => { try { ToastNotificationManager.History.Remove("b" + id); } catch (Exception) { } });
                    return;
                case "clearBanners":
                    Ui(() => { try { ToastNotificationManager.History.Clear(); } catch (Exception) { } });
                    return;
                // The card is always the one on screen, and Windows owns the keyboard.
                case "stageReady":
                case "pageDrawn":
                case "activate":
                case "keyboard":
                case "keyboardResizes":
                    return;
            }
            bool first;
            lock (notified) first = notified.Add(what);
            if (first) LunaLog.Write("[LunaKit] [" + card.AppId + "] LunacyNative." + what + " isn't on W10M yet");
        }

        public void SetClipboard(string text)
        {
            Ui(() =>
            {
                var p = new DataPackage();
                p.SetText(text ?? "");
                Clipboard.SetContent(p);
            });
        }

        public bool ClipboardHasText()
        {
            Func<bool> f = () => { try { return Clipboard.GetContent().Contains(StandardDataFormats.Text); } catch (Exception) { return false; } };
            if (card.Dispatcher.HasThreadAccess) return f();
            bool r = false;
            card.Dispatcher.RunAsync(CoreDispatcherPriority.High, () => r = f()).AsTask().Wait();
            return r;
        }

        public void Paste()
        {
            Ui(async () =>
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
