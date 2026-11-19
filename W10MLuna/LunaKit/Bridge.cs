using Windows.Foundation.Metadata;

namespace LunaKit
{
    /// window.LunaHost: the WinRT object the shell's WebView exposes to its page, which every
    /// webOS window (a frame of that page, same origin) reaches as top.LunaHost and
    /// w10m-native.js wraps as that window's window.LunacyNative. [AllowForWeb] methods are
    /// called synchronously and answer at once, as AndroidLuna's @JavascriptInterface methods
    /// are, so this follows AppWindow.kt's interface method for method (JS sees them
    /// camelCased), with the calling window's id first: Android has a WebView per window, W10M
    /// one for all of them. The work is the window's (AppWindow) or the shell's; this is only
    /// the surface a page may touch.
    [AllowForWeb]
    public sealed class LunaHostBridge
    {
        private readonly LunaShell shell;
        internal LunaHostBridge(LunaShell shell) { this.shell = shell; }

        private AppWindow W(int win) { return shell.Window(win); }

        // The shell's page.
        public string ShellInfo() { return shell.ShellInfo(); }
        public void WindowOpened(int win, string appId, string type) { shell.Opened(win, appId, type); }
        public void WindowClosed(int win) { shell.Closed(win); }
        public void ShellLog(string msg) { LunaLog.Write("[shell] " + msg); }
        /// Something the shell's page did that the host acts on (Exhibition ending).
        public void ShellEvent(string what, string arg) { shell.ShellEvent(what, arg); }

        // A window's page starting, and its log.
        public void PageStart(int win) { var w = W(win); if (w != null) w.PageStart(); }
        public void Log(int win, string msg) { var w = W(win); if (w != null) w.Log("palm", msg); else LunaLog.Write("[window " + win + "] " + msg); }
        public void Console(int win, string level, string msg) { var w = W(win); if (w != null) w.Log("console." + level, msg); else LunaLog.Write("[window " + win + "] console." + level + " " + msg); }

        // The bus.
        public bool BusCall(int win, int token, string url, string parameters) { var w = W(win); if (w != null) w.BusCall(token, url, parameters); return Bus.StaysOpen(url, parameters); }
        public void BusCancel(int win, int token) { var w = W(win); if (w != null) w.BusCancel(token); }

        // The network shim.
        public void NetSend(int win, int id, string request) { var w = W(win); if (w != null) w.Net.Send(id, request); }
        public string NetResult(int win, int id) { var w = W(win); return w == null ? "" : w.Net.Result(id); }
        public string NetSendSync(int win, string request) { var w = W(win); return w == null ? "" : w.Net.SendSync(request); }
        public void NetAbort(int win, int id) { var w = W(win); if (w != null) w.Net.Abort(id); }

        // WebSQL.
        public string SqlOpen(int win, string name, string version, string host, bool creationCallback) { var w = W(win); return w == null ? "" : w.Sql.Open(name, version, creationCallback); }
        public void SqlRequest(int win, int id, string request) { var w = W(win); if (w != null) w.Sql.Request(id, request); }
        public string SqlResult(int win, int id) { var w = W(win); return w == null ? "" : w.Sql.Result(id); }

        // Where pages run.
        // JS services (JsServices.cs, lunacy/service/): the frame's protocol messages, the
        // webOS filesystem, and hashes for node.js's crypto.
        public void ServiceSend(int win, string json) { shell.Services.FromService(win, json); }
        public string Fs(string op, string args) { return shell.Fs.Op(op, args); }
        public string Hash(string alg, string base64, string keyBase64) { return Crypto.Hash(alg, base64, keyBase64); }
        public string RandomBytes(int n) { return Crypto.Random(n); }

        public string Origin() { return shell.Origin; }
        public string ScreenOrientation() { return shell.Orientation; }

        // The window.
        public void FullScreen(int win, bool on) { var w = W(win); if (w != null) w.FullScreen(on); }
        public void RequestOrientation(int win, string orientation) { var w = W(win); if (w != null) w.RequestOrientation(orientation); }
        public void BlockScreenTimeout(int win, bool block) { var w = W(win); if (w != null) w.BlockScreenTimeout(block); }
        public int AddBanner(int win, string message, string parameters, string icon, string soundClass, string soundFile, int duration)
        {
            var w = W(win);
            return w == null ? 0 : w.AddBanner(message, icon);
        }
        /// Calls with nothing to answer that W10M has no counterpart for yet, logged once each.
        public void Notify(int win, string what, string arg) { var w = W(win); if (w != null) w.Notify(what, arg); }

        // The clipboard.
        public void SetClipboard(int win, string text) { var w = W(win); if (w != null) w.SetClipboard(text); }
        public bool ClipboardHasText(int win) { var w = W(win); return w != null && w.ClipboardHasText(); }
        public void Paste(int win) { var w = W(win); if (w != null) w.Paste(); }
    }
}
