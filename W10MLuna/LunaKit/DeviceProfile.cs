using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;
using Windows.Storage;
using Windows.System.Profile;

namespace LunaKit
{
    /// What a ported app is told about the device it runs on: a reference device's values
    /// (measured for Lunacy: AndroidLuna's DeviceProfile.kt, as iOSLuna's DeviceProfile.swift
    /// carries them), with this device's own screen and ids. As in Lunacy, a phone is told it is
    /// a Pre3 and anything else that it is a TouchPad.
    ///
    /// One CSS px is one effective (view) pixel, as Lunacy lays a page out in Android's dips.
    internal sealed class DeviceProfile
    {
        public static readonly DeviceProfile TouchPad = new DeviceProfile
        {
            Name = "TOUCHPAD", ModelName = "TouchPad", ModelNameAscii = "TouchPad", DeviceName = "HP TouchPad",
            Model = "HSTNH-I29C", BoardType = "topaz-Wifi-pvt\n", Major = 3, Minor = 1, Dot = 0, VersionPrefix = "webOS CE",
            UserAgent = "Mozilla/5.0 (hp-tablet; Linux; hpwOS/3.1.0; U; en-US) AppleWebKit/534.6 " +
                "(KHTML, like Gecko) wOSSystem/234.83 Safari/534.6 TouchPad/1.0",
            CarrierCode = "c090-01", CarrierName = "", ProductLineVersion = "1.0", BrowserOsName = "hpwOS",
            BuildName = "Nova-HP-Topaz", BuildNumber = "86", SerialPrefix = "5CL", SerialBodyLength = 7,
            Keyboard = null, SwappableBattery = false, NaturalLandscape = true,
            MinimumCardHeight = 318, TouchableRows = 14, PositiveSpaceTopPadding = 28, ReportsCarrierAvailable = true,
        };

        /// The reference Pre3, HP webOS 2.2.4 (an AT&T unit: its carrier values are that unit's).
        public static readonly DeviceProfile Pre3 = new DeviceProfile
        {
            Name = "PRE3", ModelName = "Prē3", ModelNameAscii = "Pre3", DeviceName = "HP Pre3",
            Model = "HSTNH-F30CN", BoardType = "mantaray-pvt", Major = 2, Minor = 2, Dot = 4, VersionPrefix = "HP webOS",
            UserAgent = "Mozilla/5.0 (Linux; webOS/2.2.4; U; en-US) AppleWebKit/534.6 " +
                "(KHTML, like Gecko) webOSSystem/221.56 Safari/534.6 Pre/3.0",
            CarrierCode = "c001-01", CarrierName = "ATT", ProductLineVersion = "3.0", BrowserOsName = null,
            BuildName = "Nova-ATT-Mantaray", BuildNumber = "2211", SerialPrefix = "MTRE", SerialBodyLength = 10,
            Keyboard = "QWERTY", SwappableBattery = true, NaturalLandscape = false,
            MinimumCardHeight = 318, TouchableRows = 14, PositiveSpaceTopPadding = 42, ReportsCarrierAvailable = false,
        };

        /// The one Lunacy would choose for this device: a Pre3 on a phone, a TouchPad otherwise.
        public static DeviceProfile ForThisDevice
        {
            get { return AnalyticsInfo.VersionInfo.DeviceFamily == "Windows.Mobile" ? Pre3 : TouchPad; }
        }

        public string Name, ModelName, ModelNameAscii, DeviceName, Model, BoardType, VersionPrefix, UserAgent;
        public int Major, Minor, Dot;
        /// X-Palm-Carrier, which webOS put on every request.
        public string CarrierCode, CarrierName, ProductLineVersion;
        /// 2.2.4 hasn't got this property at all.
        public string BrowserOsName;
        public string BuildName, BuildNumber, SerialPrefix;
        public int SerialBodyLength;
        /// The hardware keyboard's type, if the device has one (a Pre3's slides out).
        public string Keyboard;
        public bool SwappableBattery;
        /// Which side deviceInfo calls the screen's width: a TouchPad names its screen 1024 x 768,
        /// a Pre3 its own 480 x 800.
        public bool NaturalLandscape;
        public int MinimumCardHeight, TouchableRows, PositiveSpaceTopPadding;
        /// The Pre3's deviceInfo has no carrierAvailable member at all.
        public bool ReportsCarrierAvailable;

        public string PlatformVersion { get { return Major + "." + Minor + "." + Dot; } }
        public string VersionString { get { return VersionPrefix + " " + PlatformVersion; } }

        /// PalmSystem.deviceInfo, in the device's member order. The screen is named in its natural
        /// orientation and never changes as it turns.
        public string DeviceInfo(double screenWidth, double screenHeight)
        {
            int longSide = (int)Math.Round(Math.Max(screenWidth, screenHeight)), shortSide = (int)Math.Round(Math.Min(screenWidth, screenHeight));
            int w = NaturalLandscape ? longSide : shortSide, h = NaturalLandscape ? shortSide : longSide;
            var pairs = new List<KeyValuePair<string, object>>
            {
                J.P("modelName", ModelName), J.P("modelNameAscii", ModelNameAscii),
                J.P("platformVersion", PlatformVersion),
                J.P("platformVersionMajor", Major), J.P("platformVersionMinor", Minor), J.P("platformVersionDot", Dot),
                J.P("carrierName", CarrierName), J.P("serialNumber", Serial),
                J.P("screenWidth", w), J.P("screenHeight", h),
                J.P("minimumCardWidth", w), J.P("minimumCardHeight", MinimumCardHeight),
                J.P("maximumCardWidth", w), J.P("maximumCardHeight", h - PositiveSpaceTopPadding),
                J.P("touchableRows", TouchableRows),
                J.P("keyboardAvailable", Keyboard != null), J.P("keyboardSlider", Keyboard != null), J.P("keyboardType", Keyboard ?? "Unknown"),
                J.P("wifiAvailable", true), J.P("bluetoothAvailable", true),
            };
            if (ReportsCarrierAvailable) pairs.Add(J.P("carrierAvailable", false));
            pairs.Add(J.P("coreNaviButton", false));
            pairs.Add(J.P("swappableBattery", SwappableBattery));
            pairs.Add(J.P("dockModeEnabled", true));
            return J.Obj(pairs.ToArray());
        }

        /// com.palm.preferences/systemProperties values.
        public Dictionary<string, string> SystemProperties
        {
            get
            {
                var p = new Dictionary<string, string>
                {
                    { "com.palm.properties.nduid", Nduid },
                    { "com.palm.properties.ProdSN", Serial },
                    { "com.palm.properties.version", VersionString },
                    { "com.palm.properties.deviceName", DeviceName },
                    { "com.palm.properties.deviceNameShort", ModelNameAscii },
                    { "com.palm.properties.DMMODEL", Model },
                    { "com.palm.properties.PRODoID", Model },
                    { "com.palm.properties.boardType", BoardType },
                    { "com.palm.properties.DMCARRIER", CarrierName },
                    { "com.palm.properties.productLineVersion", ProductLineVersion },
                    { "com.palm.properties.buildName", BuildName },
                    { "com.palm.properties.buildNumber", BuildNumber },
                };
                if (BrowserOsName != null) p["com.palm.properties.browserOsName"] = BrowserOsName;
                return p;
            }
        }

        /// webOS's device id: 40 hex digits, a SHA-1. Made once per install and kept, so it stays
        /// the same across launches and updates.
        public static string Nduid
        {
            get
            {
                var settings = ApplicationData.Current.LocalSettings.Values;
                var id = settings["LunaKit.nduid"] as string;
                if (id != null) return id;
                id = Hex(Sha1("lunacy-nduid:" + Guid.NewGuid().ToString()));
                settings["LunaKit.nduid"] = id;
                return id;
            }
        }

        /// A serial in HP's shape, derived from the device id (not a copy of a real one).
        public string Serial
        {
            get
            {
                const string alphabet = "0123456789ABCDEFGHJKLMNPQRSTUVWXYZ";
                var digest = Sha1("lunacy-serial:" + Name + ":" + Nduid);
                return SerialPrefix + new string(Enumerable.Range(0, SerialBodyLength).Select(i => alphabet[digest[i] % alphabet.Length]).ToArray());
            }
        }

        private static byte[] Sha1(string s)
        {
            var sha = HashAlgorithmProvider.OpenAlgorithm(HashAlgorithmNames.Sha1);
            return sha.HashData(CryptographicBuffer.ConvertStringToBinary(s, BinaryStringEncoding.Utf8)).ToArray();
        }

        private static string Hex(byte[] b) { return string.Concat(b.Select(x => x.ToString("x2"))); }
    }
}
