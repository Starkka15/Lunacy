using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Foundation.Collections;
using Windows.Storage;
using Windows.System.Profile;

namespace LunaKit
{
    /// The fixed-viewport fallback, AndroidLuna's Phone Zoom (Docs/phone.md, "Fixed viewport,
    /// per app"; card/FixedViewport.kt). A TouchPad app draws a fixed screen (Calculator's
    /// 500 x 690 panel, Clock's 514 px face); on a phone's card it is laid out so that the
    /// card's shorter side is LayoutWidth px, and shown whole, scaled down to fit. Only on a
    /// phone: a tablet's or PC's window is a TouchPad's size already. Device Info switches it
    /// per app and sets the width (Lunacy's system/setFixedViewport and system/setLayout).
    internal static class FixedViewport
    {
        /// AndroidLuna's default for the phone layout (FormFactor.appLayoutWidth).
        public const int DefaultLayoutWidth = 640;

        /// AndroidLuna's FixedViewport.DEFAULT_ON: Palm's apps known to draw a fixed screen, and
        /// App Catalog, whose chrome is a TouchPad's width. The apps themselves are untouched.
        private static readonly HashSet<string> DefaultOn = new HashSet<string>
        {
            "com.palm.app.clock", "com.palm.calculator", "com.palm.app.notes", "com.palm.app.enyo-findapps",
        };

        public static readonly bool Phone =
            AnalyticsInfo.VersionInfo.DeviceFamily == "Windows.Mobile";

        private static IPropertySet Settings { get { return ApplicationData.Current.LocalSettings.Values; } }

        /// The width a zoomed app is laid out at; 0 is the card's own (no zoom at all).
        public static int LayoutWidth
        {
            get { var v = Settings["LunaKit.appLayoutWidth"]; return v is int ? (int)v : DefaultLayoutWidth; }
            set { Settings["LunaKit.appLayoutWidth"] = Math.Max(0, value); }
        }

        /// Whether an app is zoomed: the user's choice for it, else the default list.
        public static bool Chosen(string appId)
        {
            var v = Settings["LunaKit.zoom." + appId];
            return v is bool ? (bool)v : DefaultOn.Contains(appId);
        }

        public static void Set(string appId, bool on) { Settings["LunaKit.zoom." + appId] = on; }

        public static bool IsOn(string appId) { return Phone && LayoutWidth > 0 && Chosen(appId); }

        /// The ids the shell zooms, of these apps.
        public static IEnumerable<string> OnIds(IEnumerable<AppInfo> apps) { return apps.Where(a => Chosen(a.Id)).Select(a => a.Id); }

        /// The page px per card view px: 1 when the app is off or the card is big enough already.
        public static double Fit(string appId, double width, double height)
        {
            if (!IsOn(appId)) return 1;
            var side = height > 0 ? Math.Min(width, height) : width;
            return side <= 0 ? 1 : Math.Min(1, side / LayoutWidth);
        }
    }
}
