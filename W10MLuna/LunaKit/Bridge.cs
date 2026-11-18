using Windows.Foundation.Metadata;

namespace LunaKit
{
    /// window.LunaHost: the WinRT object a card's WebView exposes to the page, which
    /// w10m-native.js wraps as window.LunacyNative. [AllowForWeb] methods are called
    /// synchronously and answer at once, as AndroidLuna's @JavascriptInterface methods are, so
    /// this follows AppWindow.kt's interface method for method (JS sees them camelCased). The
    /// work is the window's (AppWindow); this is only the surface the page may touch.
    [AllowForWeb]
    public sealed class LunaHostBridge
    {
        private readonly AppWindow w;
        internal LunaHostBridge(AppWindow w) { this.w = w; }

        // The bus.
        public void BusCall(int token, string url, string parameters) { w.BusCall(token, url, parameters); }
        public void BusCancel(int token) { w.BusCancel(token); }
        public void Log(string msg) { w.Log("palm", msg); }
        public void Console(string level, string msg) { w.Log("console." + level, msg); }

        // The network shim.
        public void NetSend(int id, string request) { w.Net.Send(id, request); }
        public string NetResult(int id) { return w.Net.Result(id); }
        public string NetSendSync(string request) { return w.Net.SendSync(request); }
        public void NetAbort(int id) { w.Net.Abort(id); }

        // WebSQL.
        public string SqlOpen(string name, string version, string host, bool creationCallback) { return w.Sql.Open(name, version, creationCallback); }
        public void SqlRequest(int id, string request) { w.Sql.Request(id, request); }
        public string SqlResult(int id) { return w.Sql.Result(id); }

        // Where the page runs.
        public string AppOrigin(string appId) { return w.AppOrigin(appId); }
        public string ScreenSize() { return w.ScreenSize(); }
        public string CardSize() { return w.CardSize(); }
        public string ScreenOrientation() { return w.ScreenOrientation(); }

        // The window.
        public void FullScreen(bool on) { w.FullScreen(on); }
        public void RequestOrientation(string orientation) { w.RequestOrientation(orientation); }
        public void BlockScreenTimeout(bool block) { w.BlockScreenTimeout(block); }
        public int AddBanner(string message, string parameters, string icon, string soundClass, string soundFile, int duration)
        {
            return w.AddBanner(message, icon);
        }
        /// Calls with nothing to answer that W10M has no counterpart for yet, logged once each.
        public void Notify(string what, string arg) { w.Notify(what, arg); }

        // The clipboard.
        public void SetClipboard(string text) { w.SetClipboard(text); }
        public bool ClipboardHasText() { return w.ClipboardHasText(); }
        public void Paste() { w.Paste(); }
    }
}
