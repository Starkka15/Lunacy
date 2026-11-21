using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Radios;
using Windows.Devices.WiFi;
using Windows.Networking;
using Windows.Networking.Connectivity;

namespace LunaKit
{
    /// palm://com.palm.deviceprofile (DeviceProfileService.kt): the device's identity for the
    /// system's own services; palmprofile sends it with every sign-in. Private bus only, as on the
    /// reference TouchPad. The values are the ones Lunacy answers with elsewhere (the nduid, the
    /// serial, the model), so they can't disagree; what Lunacy has no value for is empty, as a wifi
    /// TouchPad's phone number and carrier were, and HP's device-management credentials were never
    /// issued to Lunacy.
    internal sealed class DeviceProfileService
    {
        public const string Service = "com.palm.deviceprofile";
        private readonly DeviceProfile profile;
        public DeviceProfileService(DeviceProfile profile) { this.profile = profile; }

        public void Register(Bus bus)
        {
            bus.RegisterPrivate(Service, "getDeviceProfile", true, c =>
                c.Reply(new JObj().Put("deviceInfo", DeviceInfo()).Put("returnValue", true).ToString()));
            bus.RegisterPrivate(Service, "getDeviceId", true, c =>
                c.Reply(new JObj().Put("deviceId", DeviceProfile.Nduid).Put("returnValue", true).ToString()));
        }

        private JObj DeviceInfo()
        {
            var build = profile.BuildName + "-" + profile.BuildNumber;
            var board = (profile.BoardType ?? "").Trim();
            int dash = board.IndexOf('-');
            if (dash >= 0) board = board.Substring(0, dash);
            var j = new JObj()
                .Put("deviceId", "").Put("nduId", DeviceProfile.Nduid).Put("phoneNumber", "").Put("carrier", "")
                .Put("carrierROM", build).Put("network", "none").Put("dataNetwork", "unknown").Put("firmwareVersion", "")
                .Put("buildTime", "").Put("softwareVersion", build).Put("deviceModel", profile.Model)
                .Put("hardwareType", board).Put("hardwareVersion", "")
                .Put("platform", "Nova").Put("platformVersion", "")
                .Put("serialNumber", profile.Serial).Put("HPSerialNumber", "").Put("productSku", "");
            foreach (var k in new[] { "WIFIoADDR", "BToADDR", "dmSets", "serverAuthType", "serverNonce", "serverPwd",
                "clientNonce", "clientPwd", "clientCredential", "softwareBuildBranch", "swUpdateTarget" }) j.Put(k, "");
            return j;
        }
    }

    /// The phone's radios, as Windows.Devices.Radios gives them (the "radios" capability): the
    /// same switches the action center works, heard as they change from anywhere.
    internal static class Radios
    {
        private static Task<Radio[]> all;
        public static Task<Radio[]> All { get { return all ?? (all = Find()); } }

        private static async Task<Radio[]> Find()
        {
            try
            {
                // Asked on the UI thread: from another one Windows refuses without asking.
                var access = RadioAccessStatus.Unspecified;
                var ui = Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher;
                var asked = new TaskCompletionSource<bool>();
                var ignored = ui.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, async () =>
                {
                    try { access = await Radio.RequestAccessAsync(); }
                    catch (Exception e) { LunaLog.Write("[radios] access: " + e.Message); }
                    asked.TrySetResult(true);
                });
                await asked.Task;
                var radios = (await Radio.GetRadiosAsync()).ToArray();
                LunaLog.Write("[radios] access " + access + "; " + string.Join(", ", radios.Select(r => r.Kind + " " + r.State)));
                return radios;
            }
            catch (Exception e) { LunaLog.Write("[radios] " + e.Message); return new Radio[0]; }
        }

        public static async Task<Radio> Of(RadioKind kind)
        {
            return (await All).FirstOrDefault(r => r.Kind == kind);
        }

        /// Windows's switch. Disabled is the system's own (airplane mode): only its settings move it.
        public static async Task<string> Switch(Radio r, bool on, string name)
        {
            if (r.State == RadioState.Disabled) return Bus.Error("Windows lets only its own settings switch " + name + " in airplane mode");
            RadioAccessStatus s;
            try { s = await r.SetStateAsync(on ? RadioState.On : RadioState.Off); }
            catch (Exception e) { return Bus.Error("Windows wouldn't switch " + name + (on ? " on: " : " off: ") + e.Message); }
            return s == RadioAccessStatus.Allowed ? null : Bus.Error("Windows wouldn't switch " + name + (on ? " on" : " off") + " (" + s + ")");
        }
    }

    /// palm://com.palm.btmonitor (BtMonitorService.kt): the Bluetooth radio and its switch
    /// (Night Moves turns it off for the night). monitor/getradiostate, subscribenotifications's
    /// radio part, radioon and radiooff; the rest of btmonitor isn't here and the bus says
    /// "Unknown method". The whole service is the private bus's.
    ///
    /// Windows tells a radio's end state, never the way there: the turning notification goes out
    /// when Lunacy starts a switch itself, the end one when Windows says it got there.
    internal sealed class BtMonitorService
    {
        public const string Service = "com.palm.btmonitor";
        private const int BadBoolean = 134217729;
        private const int EasAllowBluetooth = 2;
        private readonly Subscribers watchers = new Subscribers();
        private Task<Radio> radio;
        private RadioState last = RadioState.Unknown;

        private Task<Radio> Bt()
        {
            return radio ?? (radio = Watch());
        }

        private async Task<Radio> Watch()
        {
            var r = await Radios.Of(RadioKind.Bluetooth);
            if (r != null)
            {
                last = r.State;
                r.StateChanged += (s, e) =>
                {
                    var now = s.State;
                    if (now == last) return;
                    last = now;
                    watchers.Send(Note(now == RadioState.On ? "notifnradioon" : "notifnradiooff"));
                };
            }
            return r;
        }

        private static string Note(string n) { return new JObj().Put("notification", n).Put("error", 0).ToString(); }
        private static string State(Radio r) { return r != null && r.State == RadioState.On ? "on" : "off"; }

        public void Register(Bus bus)
        {
            Bt();
            bus.RegisterPrivate(Service, "monitor/getradiostate", true, async c =>
            {
                var r = await Bt();
                if (r == null) { c.Reply(Bus.NoService(Service)); return; }
                c.Reply(new JObj().Put("returnValue", true).Put("radio", State(r)).ToString());
            });
            bus.RegisterPrivate(Service, "monitor/subscribenotifications", true, async c =>
            {
                var r = await Bt();
                if (r == null) { c.Reply(Bus.NoService(Service)); return; }
                c.Reply(new JObj().Put("returnValue", true).Put("subscribed", c.Subscribe).Put("radio", State(r))
                    .Put("bteaspolicy", EasAllowBluetooth).ToString());
                if (c.Subscribe && !c.Cancelled) watchers.Add(c);
            });
            bus.RegisterPrivate(Service, "monitor/radioon", true, async c =>
            {
                var r = await Bt();
                if (r == null) { c.Reply(Bus.NoService(Service)); return; }
                if (r.State == RadioState.On) { c.Reply(Bus.Error("BtMtrRadioOn: BT is already ON", 12)); return; }
                foreach (var k in new[] { new[] { "visible", "Visible" }, new[] { "connectable", "Connectable" } })
                {
                    if (!c.Params.ContainsKey(k[0]) || c.Params[k[0]].ValueType != Windows.Data.Json.JsonValueType.Boolean)
                    { c.Reply(Bus.Error("BtMtrRadioOn: Error getting boolean - " + k[1], BadBoolean)); return; }
                }
                watchers.Send(Note("notifnradioturningon"));
                var err = await Radios.Switch(r, true, "Bluetooth");
                c.Reply(err ?? Bus.Ok());
            });
            bus.RegisterPrivate(Service, "monitor/radiooff", true, async c =>
            {
                var r = await Bt();
                if (r == null) { c.Reply(Bus.NoService(Service)); return; }
                if (r.State != RadioState.On) { c.Reply(Bus.Error("BtMtrRadioOff: BT is already OFF", 12)); return; }
                watchers.Send(Note("notifnradioturningoff"));
                var err = await Radios.Switch(r, false, "Bluetooth");
                c.Reply(err ?? Bus.Ok());
            });
        }
    }

    /// palm://com.palm.wifi (WifiService.kt): the Wi-Fi state and its switch. getstatus (with its
    /// subscription) and setstate; findnetworks, connect and the profiles aren't here and the bus
    /// says "Unknown method". The whole service is the private bus's. The shapes are the reference
    /// TouchPad's, measured 2026-11-15 (WifiService.kt has them).
    ///
    /// Windows's differences, each the truth rather than a copy: Windows tells connected or not,
    /// not the steps between, so connectState is "ipConfigured" or "notAssociated"; the interface is
    /// "eth0", as connectionmanager names it; Windows gives a UWP app no gateway, name servers or
    /// numeric profile id, so those are left out; the access point's BSSID, channel, signal and
    /// security come from the adapter's last scan (the "wiFiControl" capability) when it allows it.
    internal sealed class WifiService
    {
        public const string Service = "com.palm.wifi";
        private readonly Subscribers watchers = new Subscribers();
        private Task<Radio> radio;
        private WiFiAdapter adapter;
        private string lastSent = "";
        private bool lastEnabled;
        private string lastConnect = "";
        private int lastBars = -1;

        private Task<Radio> Wifi() { return radio ?? (radio = Watch()); }

        private async Task<Radio> Watch()
        {
            var r = await Radios.Of(RadioKind.WiFi);
            if (r == null) return null;
            try
            {
                if (await WiFiAdapter.RequestAccessAsync() == WiFiAccessStatus.Allowed)
                {
                    adapter = (await WiFiAdapter.FindAllAdaptersAsync()).FirstOrDefault();
                    if (adapter != null) adapter.AvailableNetworksChanged += (s, e) => SignalChanged();
                }
            }
            catch (Exception e) { LunaLog.Write("[wifi] adapter: " + e.Message); }
            lastEnabled = r.State == RadioState.On;
            if (lastEnabled) lastConnect = ConnectState(Profile());
            r.StateChanged += (s, e) => EnabledChanged(s);
            NetworkInformation.NetworkStatusChanged += _ => ConnectionChanged();
            return r;
        }

        public void Register(Bus bus)
        {
            Wifi();
            bus.RegisterPrivate(Service, "getstatus", true, async c =>
            {
                var r = await Wifi();
                if (r == null) { c.Reply(Bus.NoService(Service)); return; }
                c.Reply(Status(r).ToString());
                if (c.Subscribe && !c.Cancelled) watchers.Add(c);
            });
            bus.RegisterPrivate(Service, "setstate", true, async c =>
            {
                var r = await Wifi();
                if (r == null) { c.Reply(Bus.NoService(Service)); return; }
                if (!c.Params.ContainsKey("state") || c.Params["state"].ValueType != Windows.Data.Json.JsonValueType.String)
                { c.Reply(Bus.Error("BadJsonString", 11)); return; }
                var state = c.Params.GetNamedString("state");
                bool on;
                if (state == "enabled") on = true;
                else if (state == "disabled") on = false;
                else { c.Reply(Bus.Error("BadArgument", 9)); return; }
                if ((r.State == RadioState.On) == on) { c.Reply(on ? Bus.Error("AlreadyEnabled", 15) : Bus.Error("AlreadyDisabled", 16)); return; }
                var err = await Radios.Switch(r, on, "Wi-Fi");
                c.Reply(err ?? Bus.Ok());
            });
        }

        // ---- what Windows says, in webOS's words ----

        private static JObj Base() { return new JObj().Put("returnValue", true).Put("wakeOnWlan", "enabled"); }

        private JObj Status(Radio r)
        {
            if (r.State != RadioState.On) return Base().Put("status", "serviceDisabled");
            var p = Profile();
            var connect = ConnectState(p);
            if (connect == "notAssociated") return Base().Put("status", "serviceEnabled");
            return Connection(p, connect, null);
        }

        /// The connected Wi-Fi network's profile, or null.
        private static ConnectionProfile Profile()
        {
            try
            {
                return NetworkInformation.GetConnectionProfiles().FirstOrDefault(p =>
                    p.IsWlanConnectionProfile && p.GetNetworkConnectivityLevel() != NetworkConnectivityLevel.None);
            }
            catch (Exception) { return null; }
        }

        private static string ConnectState(ConnectionProfile p) { return p == null ? "notAssociated" : "ipConfigured"; }

        private static string Ssid(ConnectionProfile p)
        {
            try { return p == null || p.WlanConnectionProfileDetails == null ? "" : p.WlanConnectionProfileDetails.GetConnectedSsid() ?? ""; }
            catch (Exception) { return ""; }
        }

        /// The access point the phone is on, from the adapter's last scan: the strongest with its ssid.
        private WiFiAvailableNetwork AccessPoint(string ssid)
        {
            if (adapter == null || ssid.Length == 0) return null;
            try
            {
                return adapter.NetworkReport.AvailableNetworks.Where(n => n.Ssid == ssid)
                    .OrderByDescending(n => n.NetworkRssiInDecibelMilliwatts).FirstOrDefault();
            }
            catch (Exception) { return null; }
        }

        /// webOS's three bars from Windows's five.
        private static int Bars(ConnectionProfile p)
        {
            byte? b = null;
            try { b = p == null ? null : p.GetSignalBars(); } catch (Exception) { }
            if (b == null) return -1;
            return Math.Min(3, (int)Math.Round(b.Value * 3 / 5.0));
        }

        private static string Security(WiFiAvailableNetwork n)
        {
            switch (n.SecuritySettings.NetworkAuthenticationType)
            {
                case Windows.Networking.Connectivity.NetworkAuthenticationType.Rsna:
                case Windows.Networking.Connectivity.NetworkAuthenticationType.Wpa:
                    return "enterprise";
                case Windows.Networking.Connectivity.NetworkAuthenticationType.RsnaPsk:
                case Windows.Networking.Connectivity.NetworkAuthenticationType.WpaPsk:
                    return "wpa-personal";
                case Windows.Networking.Connectivity.NetworkAuthenticationType.SharedKey80211:
                    return "wep";
                default:
                    return n.SecuritySettings.NetworkEncryptionType == Windows.Networking.Connectivity.NetworkEncryptionType.Wep ? "wep" : null;
            }
        }

        private static int Channel(int mhz)
        {
            if (mhz == 2484) return 14;
            if (mhz >= 2412 && mhz <= 2472) return (mhz - 2407) / 5;
            if (mhz >= 5000 && mhz <= 5900) return (mhz - 5000) / 5;
            if (mhz >= 5925 && mhz <= 7125) return (mhz - 5950) / 5;
            return 0;
        }

        /// A connectionStateChanged answer: networkInfo always, apInfo and ipInfo once connected.
        private JObj Connection(ConnectionProfile p, string connect, string lastError)
        {
            var net = new JObj();
            var ssid = Ssid(p);
            var ap = AccessPoint(ssid);
            if (ssid.Length > 0)
            {
                net.Put("ssid", ssid);
                var sec = ap == null ? null : Security(ap);
                if (sec != null) net.Put("securityType", sec);
            }
            net.Put("connectState", connect);
            if (lastError != null) net.Put("lastConnectError", lastError);
            int bars = Bars(p);
            if (bars >= 0)
            {
                net.Put("signalBars", bars);
                if (ap != null) net.Put("signalLevel", (int)Math.Round(ap.NetworkRssiInDecibelMilliwatts));
            }
            var j = Base().Put("status", "connectionStateChanged").Put("networkInfo", net);
            if (connect == "ipConfigured" && p != null)
            {
                var ip = IpInfo(p);
                if (ip != null) j.Put("ipInfo", ip);
                if (ap != null && !string.IsNullOrEmpty(ap.Bssid))
                {
                    int mhz = ap.ChannelCenterFrequencyInKilohertz / 1000;
                    j.Put("apInfo", new JObj().Put("bssid", ap.Bssid.ToUpperInvariant()).Put("channel", Channel(mhz)).Put("frequency", mhz));
                }
            }
            return j;
        }

        private static JObj IpInfo(ConnectionProfile p)
        {
            try
            {
                var id = p.NetworkAdapter == null ? (Guid?)null : p.NetworkAdapter.NetworkAdapterId;
                var host = NetworkInformation.GetHostNames().FirstOrDefault(h => h.Type == HostNameType.Ipv4 && h.IPInformation != null
                    && h.IPInformation.NetworkAdapter != null && h.IPInformation.NetworkAdapter.NetworkAdapterId == id);
                if (host == null) return null;
                var j = new JObj().Put("interface", "eth0").Put("ip", host.CanonicalName);
                var prefix = host.IPInformation.PrefixLength;
                if (prefix != null) j.Put("subnet", Mask(prefix.Value));
                return j;
            }
            catch (Exception) { return null; }
        }

        private static string Mask(int prefix)
        {
            long m = prefix <= 0 ? 0 : (0xFFFFFFFFL << (32 - prefix)) & 0xFFFFFFFFL;
            return (m >> 24 & 255) + "." + (m >> 16 & 255) + "." + (m >> 8 & 255) + "." + (m & 255);
        }

        // ---- subscribers ----

        private void Send(JObj j)
        {
            var s = j.ToString();
            lock (watchers) { if (s == lastSent) return; lastSent = s; }
            watchers.Send(s);
        }

        private void EnabledChanged(Radio r)
        {
            bool on = r.State == RadioState.On;
            if (on == lastEnabled) return;
            lastEnabled = on;
            if (!on)
            {
                // The TouchPad said the connection went before the service did.
                if (lastConnect.Length > 0 && lastConnect != "notAssociated")
                {
                    var net = new JObj();
                    var j = Base().Put("status", "connectionStateChanged")
                        .Put("networkInfo", net.Put("connectState", "notAssociated").Put("lastConnectError", "DisconnectRequest"));
                    Send(j);
                }
                lastConnect = ""; lastBars = -1;
                Send(Base().Put("status", "serviceDisabled"));
            }
            else Send(Base().Put("status", "serviceEnabled"));
        }

        private void ConnectionChanged()
        {
            if (!lastEnabled) return;
            var p = Profile();
            var connect = ConnectState(p);
            if (connect == lastConnect) return;
            // Windows reports no network as Wi-Fi comes on, before anything was ever connected.
            if (connect == "notAssociated" && lastConnect.Length == 0) return;
            lastConnect = connect;
            lastBars = Bars(p);
            Send(Connection(p, connect, null));
        }

        private void SignalChanged()
        {
            if (lastConnect != "ipConfigured") return;
            var p = Profile();
            int b = Bars(p);
            if (b < 0 || b == lastBars) return;
            lastBars = b;
            // The TouchPad's own shape: no wakeOnWlan here.
            var j = new JObj().Put("returnValue", true).Put("status", "signalStrengthChanged").Put("signalBars", b);
            var ap = AccessPoint(Ssid(p));
            if (ap != null) j.Put("signalLevel", (int)Math.Round(ap.NetworkRssiInDecibelMilliwatts));
            Send(j);
        }
    }
}
