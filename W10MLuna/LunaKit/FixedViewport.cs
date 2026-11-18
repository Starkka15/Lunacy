using System;
using System.Collections.Generic;
using Windows.System.Profile;

namespace LunaKit
{
    /// The fixed-viewport fallback, AndroidLuna's Phone Zoom (Docs/phone.md, "Fixed viewport,
    /// per app"; card/FixedViewport.kt). A TouchPad app draws a fixed screen (Calculator's
    /// 500 x 690 panel, Clock's 514 px face); on a phone's card it is laid out so that the
    /// card's shorter side is LayoutWidth px, and shown whole, scaled down to fit. Only on a
    /// phone: a tablet's or PC's window is a TouchPad's size already.
    internal static class FixedViewport
    {
        /// AndroidLuna's default for the phone layout (FormFactor.appLayoutWidth).
        public const int LayoutWidth = 640;

        /// AndroidLuna's FixedViewport.DEFAULT_ON: Palm's apps known to draw a fixed screen, and
        /// App Catalog, whose chrome is a TouchPad's width. The apps themselves are untouched.
        private static readonly HashSet<string> DefaultOn = new HashSet<string>
        {
            "com.palm.app.clock", "com.palm.calculator", "com.palm.app.notes", "com.palm.app.enyo-findapps",
        };

        private static readonly bool Phone =
            AnalyticsInfo.VersionInfo.DeviceFamily == "Windows.Mobile";

        public static bool IsOn(string appId) { return Phone && DefaultOn.Contains(appId); }

        /// The page px per card view px: 1 when the app is off or the card is big enough already.
        public static double Fit(string appId, double width, double height)
        {
            if (!IsOn(appId)) return 1;
            var side = height > 0 ? Math.Min(width, height) : width;
            return side <= 0 ? 1 : Math.Min(1, side / LayoutWidth);
        }
    }
}
