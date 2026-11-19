using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Graphics.Display;
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
        /// Phone's and the setup apps' call to bring up a carrier's data service: Windows runs
        /// the phone's cellular data itself, so no APN is Lunacy's to start (as on a TouchPad
        /// with no modem, whose CarrierDB had none).
        public static string CellularData() { return Bus.Error("APN Not found in CarrierDB"); }
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
            bus.Register("com.palm.connectionmanager", "connectCellularDataService", c => c.Reply(CellularData()));
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
        private readonly Subscribers headset = new Subscribers();
        private bool watchingHeadset;
        private string headsetState;

        /// The headset, as webOS's keys service reported it: "headset", "headset_mic_less" or
        /// "none". Windows says which audio device is the default; a phone's jack shows as a
        /// device named for headphones or a headset, its microphone as a capture device too.
        private void WatchHeadset()
        {
            if (watchingHeadset) return;
            watchingHeadset = true;
            Windows.Media.Devices.MediaDevice.DefaultAudioRenderDeviceChanged += (s, e) => CheckHeadset();
            Windows.Media.Devices.MediaDevice.DefaultAudioCaptureDeviceChanged += (s, e) => CheckHeadset();
            CheckHeadset();
        }

        private async void CheckHeadset()
        {
            string state = "none";
            try
            {
                var render = Windows.Media.Devices.MediaDevice.GetDefaultAudioRenderId(Windows.Media.Devices.AudioDeviceRole.Default);
                var capture = Windows.Media.Devices.MediaDevice.GetDefaultAudioCaptureId(Windows.Media.Devices.AudioDeviceRole.Default);
                var r = string.IsNullOrEmpty(render) ? null : await Windows.Devices.Enumeration.DeviceInformation.CreateFromIdAsync(render);
                var cap = string.IsNullOrEmpty(capture) ? null : await Windows.Devices.Enumeration.DeviceInformation.CreateFromIdAsync(capture);
                Func<Windows.Devices.Enumeration.DeviceInformation, bool> worn = d => d != null && (d.Name.IndexOf("head", StringComparison.OrdinalIgnoreCase) >= 0 || d.Name.IndexOf("earphone", StringComparison.OrdinalIgnoreCase) >= 0);
                if (worn(r)) state = worn(cap) ? "headset" : "headset_mic_less";
            }
            catch (Exception e) { LunaLog.Write("[keys] headset: " + e.Message); }
            if (state == headsetState) return;
            bool first = headsetState == null;
            headsetState = state;
            if (!first || state != "none") headset.Send(J.Obj(J.P("returnValue", true), J.P("state", state)));
        }
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
            bus.Register("com.palm.keys", "headset/status", c =>
            {
                if (!c.Subscribe) { c.Reply(NeedsSubscription); return; }
                c.Reply(Subscribed);
                if (c.Cancelled) return;
                headset.Add(c);
                WatchHeadset();
            });
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
    /// palm://com.palm.display/control (AndroidLuna's DisplayService.kt): whether the screen is
    /// on, its brightness and timeout, and webOS's display states.
    ///
    /// maximumBrightness is Windows' BrightnessOverride: the level Lunacy sets holds while
    /// Lunacy is in front, and is set again at each start (Windows lets an app set brightness
    /// only for itself). Windows gives an app no way to read or set the screen timeout: it
    /// answers the one last set here (the TouchPad's 1800 until then), and a set says Windows
    /// keeps its own.
    internal sealed class DisplayService
    {
        private const string Service = "com.palm.display";
        private readonly LunaShell shell;
        private readonly Subscribers subscribers = new Subscribers();
        private readonly Windows.Storage.ApplicationDataContainer prefs = Windows.Storage.ApplicationData.Current.LocalSettings.CreateContainer("display", Windows.Storage.ApplicationDataCreateDisposition.Always);
        private volatile bool screenOn = true;

        public DisplayService(LunaShell shell) { this.shell = shell; }

        public void Register(Bus bus)
        {
            bus.Register(Service, "control/status", c =>
            {
                c.Reply(StatusReply(c.Subscribe));
                if (c.Subscribe && !c.Cancelled) subscribers.Add(c);
            });
            bus.Register(Service, "control/getProperty", c => c.Reply(GetProperty(Jx.ObjOrEmpty(c.Raw))));
            bus.Register(Service, "control/setProperty", c => SetProperty(c, Jx.ObjOrEmpty(c.Raw)));
            bus.Register(Service, "control/setState", c => c.Reply(SetState(Jx.ObjOrEmpty(c.Raw))));
            shell.Ui(() => { int b = Brightness(); if (Stored("maximumBrightness") >= 0) Override(b); });
        }

        private int Stored(string key) { object v; return prefs.Values.TryGetValue(key, out v) && v is int ? (int)v : -1; }
        private int Timeout() { int t = Stored("timeout"); return t > 0 ? t : 1800; }

        /// The brightness Lunacy set, or Windows' own level as the override reads it.
        private int Brightness()
        {
            int b = Stored("maximumBrightness");
            if (b >= 1) return b;
            try
            {
                var o = shell.OnUi(() => BrightnessOverride.GetForCurrentView());
                if (o.IsSupported) return Math.Max(1, Math.Min(100, (int)Math.Round(o.BrightnessLevel * 100)));
            }
            catch (Exception) { }
            return 100;
        }

        private void Override(int percent)
        {
            try
            {
                var o = BrightnessOverride.GetForCurrentView();
                if (!o.IsSupported) return;
                o.SetBrightnessLevel(percent / 100.0, DisplayBrightnessOverrideOptions.None);
                if (!o.IsOverrideActive) o.StartOverride();
            }
            catch (Exception e) { LunaLog.Write("[display] brightness: " + e.Message); }
        }

        private string GetProperty(JObj p)
        {
            var wanted = (p.Arr("properties") ?? new JArr()).OfType<string>().ToList();
            if (wanted.Count == 0) wanted = new List<string> { "timeout", "maximumBrightness" };
            var out_ = new JObj().Put("returnValue", true);
            foreach (var name in wanted)
            {
                // An unknown property is left out, as the TouchPad leaves out a key it hasn't got.
                if (name == "timeout") out_.Put("timeout", Timeout());
                else if (name == "maximumBrightness") out_.Put("maximumBrightness", Brightness());
            }
            return out_.ToString();
        }

        private void SetProperty(Bus.Call c, JObj p)
        {
            var problems = new List<string>();
            if (p.Has("maximumBrightness"))
            {
                int percent = Jx.IsNumber(p["maximumBrightness"]) ? (int)Jx.Num(p["maximumBrightness"]) : -1;
                if (percent < 1 || percent > 100) problems.Add("maximumBrightness must be 1 to 100");
                else
                {
                    bool ok = shell.OnUi(() =>
                    {
                        try { if (!BrightnessOverride.GetForCurrentView().IsSupported) return false; } catch (Exception) { return false; }
                        Override(percent);
                        return true;
                    });
                    if (ok) prefs.Values["maximumBrightness"] = percent;
                    else problems.Add("Windows doesn't let Lunacy set this screen's brightness");
                }
            }
            if (p.Has("timeout"))
            {
                int seconds = Jx.IsNumber(p["timeout"]) ? (int)Jx.Num(p["timeout"]) : 0;
                if (seconds <= 0) problems.Add("timeout must be a number of seconds");
                else { prefs.Values["timeout"] = seconds; problems.Add("Windows keeps the screen timeout itself: Settings > Personalization > Lock screen"); }
            }
            c.Reply(problems.Count == 0 ? Bus.Ok() : Bus.Error(string.Join("; ", problems)));
        }

        /// webOS's display states: Exhibition's dock and undock, and "on", which wakes the
        /// screen (Windows' screen is on while Lunacy is in front to hear it); the others are
        /// said plainly to be missing. LunaSysMgr ignored "on" in Exhibition.
        private string SetState(JObj p)
        {
            var state = p.Str("state");
            switch (state)
            {
                case "dock": shell.SetExhibition(true); return Bus.Ok();
                case "undock": shell.SetExhibition(false); return Bus.Ok();
                case "on": return Bus.Ok();
                case "": return Bus.Error("state is required");
                default: return Bus.Error("Lunacy has no display state \"" + state + "\"");
            }
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
                J.P("timeout", Timeout()), J.P("blockDisplay", "false"), J.P("active", screenOn), J.P("subscribed", subscribed));
        }
    }
}
