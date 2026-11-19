using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Media;
using Windows.Networking.Connectivity;
using Windows.UI.Core;

namespace LunaKit
{
    // The bus services a ported app gets, each backed by Windows and answering in the reference
    // TouchPad's shapes (AndroidLuna's SystemProperties.kt, ConnectionManager.kt, Keys.kt,
    // DisplayService.kt; iOSLuna's Services.swift). Only what an app has needed is here; anything
    // else gets the bus's honest error.

    /// A set of subscriptions that end themselves when cancelled.
    internal sealed class Subscribers
    {
        private readonly List<Bus.Call> calls = new List<Bus.Call>();
        public void Add(Bus.Call c)
        {
            lock (calls) calls.Add(c);
            c.OnCancel(() => { lock (calls) calls.Remove(c); });
        }
        public int Count { get { lock (calls) return calls.Count; } }
        public void Send(string json)
        {
            Bus.Call[] all;
            lock (calls) all = calls.ToArray();
            foreach (var c in all) c.Reply(json);
        }
    }

    /// palm://com.palm.preferences/systemProperties/Get (and getSysProperty, which the TouchPad
    /// answers the same way). A subscription gets the one reply, as on the TouchPad.
    internal sealed class SystemProperties
    {
        private readonly DeviceProfile profile;
        public SystemProperties(DeviceProfile profile) { this.profile = profile; }

        public void Register(Bus bus)
        {
            foreach (var m in new[] { "systemProperties/Get", "systemProperties/getSysProperty" })
            {
                bus.Register("com.palm.preferences", m, c =>
                {
                    var key = c.Params.GetString("key", "");
                    // The TouchPad's errors here have no errorCode.
                    if (key.Length == 0) { c.Reply(J.Obj(J.P("returnValue", false), J.P("errorText", "missing parameter key"))); return; }
                    string v;
                    if (!profile.SystemProperties.TryGetValue(key, out v)) { c.Reply(J.Obj(J.P("returnValue", false), J.P("errorText", "no such key"))); return; }
                    c.Reply(J.Obj(J.P(key, v), J.P("returnValue", true)));
                });
            }
        }
    }

    /// palm://com.palm.connectionmanager/getstatus (and getStatus), from Windows' network
    /// information. A subscription gets the status at once and again whenever it changes.
    internal sealed class ConnectionManager
    {
        private readonly Subscribers subscribers = new Subscribers();
        private string last = "";

        public ConnectionManager()
        {
            NetworkInformation.NetworkStatusChanged += _ =>
            {
                var s = Status();
                if (s == last) return;
                last = s;
                subscribers.Send(s);
            };
        }

        public void Register(Bus bus)
        {
            Action<Bus.Call> h = c =>
            {
                var s = Status();
                c.Reply(s);
                if (c.Subscribe && !c.Cancelled) { last = s; subscribers.Add(c); }
            };
            bus.Register("com.palm.connectionmanager", "getstatus", h);
            bus.Register("com.palm.connectionmanager", "getStatus", h);
        }

        public static string Status()
        {
            var p = NetworkInformation.GetInternetConnectionProfile();
            var level = p == null ? NetworkConnectivityLevel.None : p.GetNetworkConnectivityLevel();
            bool online = level == NetworkConnectivityLevel.InternetAccess;
            bool wifi = p != null && p.IsWlanConnectionProfile;
            bool cellular = p != null && p.IsWwanConnectionProfile;
            // eth0 is the TouchPad's Wi-Fi interface, as an app that shows it would have shown.
            // A connection that is neither (wired, a USB tether) is reported as Wi-Fi: webOS had no
            // third kind.
            var wifiState = online && !cellular
                ? J.Obj(J.P("state", "connected"), J.P("interfaceName", "eth0"), J.P("networkConfidenceLevel", "excellent"),
                        J.P("onInternet", "yes"), J.P("isWakeOnWifiEnabled", wifi))
                : J.Obj(J.P("state", "disconnected"));
            var wan = online && cellular ? J.Obj(J.P("state", "connected"), J.P("onInternet", "yes")) : J.Obj(J.P("state", "disconnected"));
            return "{\"returnValue\":true,\"isInternetConnectionAvailable\":" + (online ? "true" : "false") +
                ",\"wifi\":" + wifiState + ",\"wan\":" + wan +
                ",\"vpn\":{\"state\":\"disconnected\"},\"bridge\":{\"state\":\"disconnected\"}}";
        }
    }

    /// palm://com.palm.keys/media/status: the media keys, which on Windows 10 Mobile are the
    /// volume flyout's, a headset's and a car's play, pause and skip buttons, through the system
    /// media transport controls. The TouchPad answers these only to a subscriber. headset/status
    /// isn't here yet: Windows has no plain "is a headset in" event (README, "Still to do").
    internal sealed class Keys
    {
        private readonly Subscribers media = new Subscribers();
        private readonly CoreDispatcher dispatcher;
        private SystemMediaTransportControls smtc;

        public Keys(CoreDispatcher dispatcher) { this.dispatcher = dispatcher; }

        /// webOS's own wording, typo and all.
        public static readonly string NeedsSubscription = J.Obj(
            J.P("errorCode", -1), J.P("errorText", "We were expecting a subscribe type message, but we did not recieve one."),
            J.P("returnValue", false), J.P("subscribed", false));
        public static readonly string Subscribed = J.Obj(J.P("returnValue", true), J.P("subscribed", true));

        public void Register(Bus bus)
        {
            bus.Register("com.palm.keys", "media/status", c =>
            {
                if (!c.Subscribe) { c.Reply(NeedsSubscription); return; }
                c.Reply(Subscribed);
                media.Add(c);
                c.OnCancel(() => { if (media.Count == 0) Ui(Release); });
                Ui(Take);
            });
        }

        private void Ui(Action a)
        {
            if (dispatcher.HasThreadAccess) a();
            else { var _ = dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => a()); }
        }

        /// A key press as webOS's keys service sent it: a down, then an up.
        public void Press(string key)
        {
            foreach (var state in new[] { "down", "up" }) media.Send(J.Obj(J.P("key", key), J.P("state", state)));
        }

        private void Take()
        {
            if (smtc != null) return;
            smtc = SystemMediaTransportControls.GetForCurrentView();
            smtc.IsPlayEnabled = smtc.IsPauseEnabled = smtc.IsStopEnabled = smtc.IsNextEnabled = smtc.IsPreviousEnabled = true;
            smtc.ButtonPressed += Pressed;
        }

        private void Release()
        {
            if (smtc == null) return;
            smtc.ButtonPressed -= Pressed;
            smtc = null;
        }

        private void Pressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs e)
        {
            string key;
            switch (e.Button)
            {
                case SystemMediaTransportControlsButton.Play: key = "play"; break;
                case SystemMediaTransportControlsButton.Pause: key = "pause"; break;
                case SystemMediaTransportControlsButton.Stop: key = "stop"; break;
                case SystemMediaTransportControlsButton.Next: key = "next"; break;
                case SystemMediaTransportControlsButton.Previous: key = "prev"; break;
                default: return;
            }
            LunaLog.Write("[LunaKit] media key " + key);
            Press(key);
        }
    }

    /// palm://com.palm.display/control/status: the screen's state. A UWP app can't see the
    /// screen itself; it sees its window hidden when the phone locks or the app leaves the
    /// front, which is what an app asking this wants to know.
    internal sealed class DisplayService
    {
        private readonly Subscribers subscribers = new Subscribers();
        private volatile bool screenOn = true;

        public void Register(Bus bus)
        {
            bus.Register("com.palm.display", "control/status", c =>
            {
                c.Reply(StatusReply(c.Subscribe));
                if (c.Subscribe && !c.Cancelled) subscribers.Add(c);
            });
        }

        public void Visible(bool on)
        {
            if (on == screenOn) return;
            screenOn = on;
            subscribers.Send(StatusReply(true));
        }

        /// The reference TouchPad's shape; blockDisplay really is a string there.
        private string StatusReply(bool subscribed)
        {
            return J.Obj(J.P("returnValue", true), J.P("event", "request"), J.P("state", screenOn ? "on" : "off"),
                J.P("timeout", 1800), J.P("blockDisplay", "false"), J.P("active", screenOn), J.P("subscribed", subscribed));
        }
    }

    /// palm://com.palm.applicationManager/open and launch, for what Windows can open: a URL (web,
    /// mail, phone) goes to the system. Launching another webOS app by id needs the shell, which
    /// a W10MLuna card doesn't have yet, and says so.
    /// com.palm.applicationManager, as LunaSysMgr answered it (AndroidLuna's ApplicationManager.kt):
    /// launch and open - an app by id (started, or brought forward with the new params), a web
    /// page to the Web app, mailto: to Email, any other link to Windows - and the app list.
    internal sealed class ApplicationManager
    {
        private const string Browser = "com.palm.app.browser";
        private const string Email = "com.palm.app.email";
        private readonly LunaShell shell;
        public ApplicationManager(LunaShell shell) { this.shell = shell; }

        public void Register(Bus bus)
        {
            Action<Bus.Call> h = c =>
            {
                var id = c.Params.GetString("id") ?? "";
                var inner = c.Params.ContainsKey("params") && c.Params["params"].ValueType == Windows.Data.Json.JsonValueType.Object
                    ? c.Params.GetNamedObject("params") : null;
                var target = c.Params.GetString("target") ?? "";
                if (id.Length == 0 && target.Length > 0)
                {
                    // webOS's command-resource-handlers: ^https?: and ^data: to the Web app,
                    // mailto: to Email, each a card of its own.
                    var lower = target.ToLowerInvariant();
                    string app = (lower.StartsWith("http:") || lower.StartsWith("https:") || lower.StartsWith("data:")) ? Browser
                        : lower.StartsWith("mailto:") ? Email : null;
                    if (app != null && shell.Apps.Get(app) != null)
                    {
                        shell.Launch(app, J.Obj(J.P("target", target)), c.AppId);
                        c.Reply(J.Obj(J.P("returnValue", true), J.P("processId", "success")));
                        return;
                    }
                    Uri uri;
                    if (Uri.TryCreate(target, UriKind.Absolute, out uri) && !uri.IsFile)
                    {
                        var _ = shell.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
                        {
                            bool ok = await Windows.System.Launcher.LaunchUriAsync(uri);
                            c.Reply(ok ? Bus.Ok() : Bus.Error("Windows couldn't open " + uri.Scheme + ": links"));
                        });
                        return;
                    }
                    c.Reply(Bus.Error("No handler for " + target));
                    return;
                }
                if (shell.Apps.Get(id) == null) { c.Reply(Bus.Error("Application not found: " + id)); return; }
                // An activity's callback carries "$activity" beside the params, and the
                // application manager put it into the params the app gets (luna-sysmgr).
                var p = inner == null ? new Windows.Data.Json.JsonObject() : Windows.Data.Json.JsonObject.Parse(inner.Stringify());
                if (c.Params.ContainsKey("$activity")) p["$activity"] = c.Params["$activity"];
                shell.Launch(id, inner == null && !c.Params.ContainsKey("$activity") ? "" : p.Stringify(), c.AppId);
                c.Reply(J.Obj(J.P("returnValue", true), J.P("processId", "success")));
            };
            bus.Register("com.palm.applicationManager", "open", h);
            bus.Register("com.palm.applicationManager", "launch", h);
            bus.Register("com.palm.applicationManager", "listApps", c =>
                c.Reply("{\"returnValue\":true,\"apps\":" + shell.Apps.Json() + "}"));
            bus.Register("com.palm.applicationManager", "getAppInfo", c =>
            {
                var a = shell.Apps.Get(c.Params.GetString("appId") ?? c.Params.GetString("id"));
                c.Reply(a == null ? Bus.Error("Application not found") : "{\"returnValue\":true,\"appInfo\":" + a.Listed().Stringify() + "}");
            });
            // App Catalog watches this for its install buttons (measured on the reference TouchPad:
            // subscriptions only, {"returnValue":true,"subscribed":true} first). Installs from the
            // catalog aren't built yet, so nothing comes or goes after that.
            bus.Register("com.palm.applicationManager", "launchPointChanges", c =>
            {
                if (!c.Subscribe) { c.Reply(J.Obj(J.P("returnValue", false), J.P("subscribed", false), J.P("errorText", "Only supports subscriptions"))); return; }
                c.Reply(J.Obj(J.P("returnValue", true), J.P("subscribed", true)));
            });
            // An app may change only its own launch point's icon: the caller from the bus, never
            // from the params (Lunacy's rule 10). Calendar draws today's date on its icon.
            bus.Register("com.palm.applicationManager", "updateLaunchPointIcon", c =>
            {
                var id = c.Params.GetString("launchPointId") ?? "";
                if (id.Length == 0) { c.Reply(Bus.Error("Must provide launchPointId")); return; }
                if (!c.Params.ContainsKey("icon")) { c.Reply(Bus.Error("Must provide icon path")); return; }
                var app = id.EndsWith("_default", StringComparison.Ordinal) ? shell.Apps.Get(id.Substring(0, id.Length - 8)) : null;
                if (app == null) { c.Reply(Bus.Error("Unable to update launch point's icon")); return; }
                if (app.Id != c.AppId) { c.Reply(Bus.Error("Attempted to change another application's launch point icon")); return; }
                var icon = (c.Params.GetString("icon") ?? "");
                if (icon.StartsWith("file://", StringComparison.Ordinal)) icon = icon.Substring(7);
                var basePath = "/" + AppServer.AppsPath + app.Id + "/";
                if (icon.StartsWith(basePath, StringComparison.Ordinal)) icon = icon.Substring(basePath.Length);
                if (icon.StartsWith("/", StringComparison.Ordinal) || icon.Contains("..") || !System.IO.File.Exists(System.IO.Path.Combine(app.Dir, icon.Replace('/', '\\'))))
                {
                    c.Reply(Bus.Error("Unable to update launch point's icon"));
                    return;
                }
                app.Icon = icon;
                shell.Evaluate("__lunacyShell.setIcon(" + J.Str(app.Id) + "," + J.Str(app.Url(icon)) + ")");
                c.Reply(Bus.Ok());
            });
            bus.Register("com.palm.applicationManager", "getAppBasePath", c =>
            {
                var a = shell.Apps.Get(c.Params.GetString("appId"));
                c.Reply(a == null ? Bus.Error("Application not found") : J.Obj(J.P("returnValue", true), J.P("basePath", "file:///" + AppServer.AppsPath + a.Id + "/" + a.Main)));
            });
        }
    }
}
