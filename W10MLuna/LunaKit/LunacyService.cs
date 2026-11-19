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
    /// Android-only parts (Android's notifications, runtime permissions, home screen pins) are
    /// not registered, so the bus answers that they don't exist.
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
            bus.Register(Service, "system/setFixedViewport", c =>
            {
                var id = c.Params.GetString("id");
                if (string.IsNullOrEmpty(id) || shell.Apps.Get(id) == null) { c.Reply(Bus.Error("No app " + id)); return; }
                bool on = c.Params.GetBool("on");
                FixedViewport.Set(id, on);
                shell.Evaluate("__lunacyShell.setZoom(" + J.Str(id) + "," + (on ? "true" : "false") + ")");
                c.Reply(Bus.Ok());
            });
            bus.Register(Service, "system/setLayout", c =>
            {
                if (!c.Params.ContainsKey("appLayoutWidth")) { c.Reply(Bus.Error("Nothing to set")); return; }
                var w = c.Params.GetInt("appLayoutWidth");
                if (w != 0 && (w < 320 || w > 2048)) { c.Reply(Bus.Error("The width must be 0 or 320 to 2048 px")); return; }
                FixedViewport.LayoutWidth = w;
                shell.Evaluate("__lunacyShell.setLayoutWidth(" + w + ")");
                c.Reply(Bus.Ok());
            });
            bus.Register(Service, "system/setDeviceId", c =>
            {
                if (c.Params.GetBool("reset")) { DeviceProfile.SetNduid(null); c.Reply(Bus.Ok()); return; }
                var id = (c.Params.GetString("nduid") ?? "").Trim();
                if (!Regex.IsMatch(id, "^[0-9a-fA-F]{40}$")) { c.Reply(Bus.Error("A device id is 40 hex digits (0-9, a-f).")); return; }
                DeviceProfile.SetNduid(id);
                c.Reply(Bus.Ok());
            });
            bus.Register(Service, "system/saveLog", c => Task.Run(async () =>
            {
                try { c.Reply(await SaveLog()); }
                catch (Exception e) { c.Reply(Bus.Error("The log couldn't be saved: " + e.Message)); }
            }));
            bus.Register(Service, "system/shareLog", c =>
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
                bool ok = await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:about"));
                c.Reply(ok ? Bus.Ok() : Bus.Error("Windows couldn't open its settings"));
            }));
        }

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
