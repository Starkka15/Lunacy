using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LunaKit
{
    /// webOS JS services (AndroidLuna's JsServices.kt and ServiceProcess.kt). Each service
    /// package (usr/palm/services/<id>/, with services.json) runs in a hidden frame of the
    /// shell's page, where Lunacy's node.js gives it the part of Node 0.4 Palm's frameworks use,
    /// and service-host.js does what host.js does on Android: webOS's palmbus, webos and
    /// pmloglib modules, then Palm's own jsservicelauncher bootstrap (SHELL.md, step 4). The
    /// package's bus names are registered at startup; the first call starts its frame, and the
    /// frame ends when the service exits (mojoservice's activity timeout).
    ///
    /// The frame and this side speak host.js's line protocol, one JSON message at a time:
    /// request, cancel and callResponse go in; response, call, cancelCall, ready and exit come
    /// out (ServiceSend). A frame runs on the WebView's thread, beside the apps: a service that
    /// computes for long holds the screen meanwhile, where Android's run in processes.
    internal sealed class JsServices
    {
        private readonly LunaShell shell;
        private readonly WebosFs fs;
        private readonly object gate = new object();
        /// Bus name to its package's directory, as a webOS path.
        private readonly Dictionary<string, string> names = new Dictionary<string, string>();
        /// Running packages, by directory.
        private readonly Dictionary<string, Proc> running = new Dictionary<string, Proc>();
        private readonly Dictionary<int, Proc> byWindow = new Dictionary<int, Proc>();
        private int nextRequest;

        private sealed class Proc
        {
            public string Dir, Name;
            public List<string> Names;
            public int Window;
            public bool Ready;
            public readonly List<string> Queue = new List<string>();
            public readonly Dictionary<string, Bus.Call> Incoming = new Dictionary<string, Bus.Call>();
            public readonly Dictionary<string, Bus.Call> Outgoing = new Dictionary<string, Bus.Call>();
        }

        public JsServices(LunaShell shell, WebosFs fs)
        {
            this.shell = shell; this.fs = fs;
        }

        /// Registers every package's bus names: the ROM's /usr/palm/services, then installed ones.
        public void Scan()
        {
            lock (gate)
            {
                foreach (var n in names.Keys) shell.Bus.UnregisterService(n);
                names.Clear();
                foreach (var root in new[] { "/usr/palm/services", "/media/cryptofs/apps/usr/palm/services" })
                {
                    List<string> dirs;
                    try { dirs = fs.List(root); } catch (WebosFs.FsError) { continue; }
                    foreach (var d in dirs)
                    {
                        var dir = root + "/" + d;
                        if (!fs.Exists(dir + "/services.json")) continue;
                        try
                        {
                            var json = Jx.ParseObj(System.Text.Encoding.UTF8.GetString(fs.Read(dir + "/services.json")));
                            foreach (var s in (json.Arr("services") ?? new JArr()).OfType<JObj>())
                            {
                                var name = s.Str("name");
                                if (name.Length == 0) continue;
                                names[name] = dir;
                                var n = name;
                                shell.Bus.RegisterService(name, c => Request(n, c));
                            }
                        }
                        catch (Exception e) { LunaLog.Write("[services] " + dir + "/services.json unreadable: " + e.Message); }
                    }
                }
                LunaLog.Write("[services] js services: " + string.Join(", ", names.Keys.OrderBy(x => x, StringComparer.Ordinal)));
            }
        }

        /// A call to one of a package's names: to its frame, started if it isn't running.
        private void Request(string service, Bus.Call call)
        {
            Proc p;
            string id;
            lock (gate)
            {
                string dir;
                if (!names.TryGetValue(service, out dir)) { call.Reply(Bus.Error("Service does not exist: " + service + ".")); return; }
                if (!running.TryGetValue(dir, out p)) p = Start(dir);
                id = "r" + (++nextRequest);
                p.Incoming[id] = call;
            }
            // The method's category is what's before its last slash, as ls-hubd split it.
            var m = call.Method ?? "";
            int slash = m.LastIndexOf('/');
            var category = slash < 0 ? "/" : "/" + m.Substring(0, slash);
            var method = slash < 0 ? m : m.Substring(slash + 1);
            // A caller from outside the package reaches its public methods only, as ls-hubd's
            // public bus did: the package's own names and the shell's own calls are inside.
            bool outside = !(p.Names.Contains(call.AppId) || call.AppId.StartsWith(Path.GetFileName(p.Dir) + ".", StringComparison.Ordinal)
                || call.AppId == Db8.Configurator);
            var msg = new JObj().Put("t", "request").Put("id", id).Put("service", service).Put("category", category).Put("method", method)
                .Put("payload", call.Raw).Put("sender", call.AppId).Put("outside", outside).Put("subscribe", call.Subscribe);
            if (names.ContainsKey(call.AppId)) msg.Put("fromService", true);
            call.OnCancel(() =>
            {
                lock (gate) { if (!p.Incoming.Remove(id)) return; }
                Send(p, new JObj().Put("t", "cancel").Put("id", id).ToString());
            });
            Send(p, msg.ToString());
        }

        /// The package's frame, started. Under the gate.
        private Proc Start(string dir)
        {
            var pkgNames = names.Where(kv => kv.Value == dir).Select(kv => kv.Key).ToList();
            var p = new Proc { Dir = dir, Name = pkgNames.FirstOrDefault() ?? Path.GetFileName(dir), Names = pkgNames };
            running[dir] = p;
            LunaLog.Write("[services] starting " + dir);
            var url = AppServer.ServicePage + "?dir=" + Uri.EscapeDataString(dir) + "&name=" + Uri.EscapeDataString(p.Name);
            shell.Ui(async () =>
            {
                try
                {
                    var r = await shell.View.InvokeScriptAsync("eval", new[] { "String(__lunacyShell.startService(" + J.Str(p.Name) + "," + J.Str(url) + "))" });
                    int win;
                    if (!int.TryParse(r, out win)) { LunaLog.Write("[services] " + dir + ": no frame (" + r + ")"); return; }
                    lock (gate) { p.Window = win; byWindow[win] = p; }
                }
                catch (Exception e) { LunaLog.Write("[services] " + dir + ": frame failed: " + e.Message); }
            });
            return p;
        }

        /// A message for a package's frame: held until its bootstrap has run.
        private void Send(Proc p, string json)
        {
            int win;
            lock (gate)
            {
                if (!p.Ready || p.Window == 0) { p.Queue.Add(json); return; }
                win = p.Window;
            }
            var w = shell.Window(win);
            if (w != null) w.Evaluate("window.__lunacyHost&&__lunacyHost.receive(" + J.Str(json) + ")");
        }

        /// A message from a package's frame (Bridge.ServiceSend).
        public void FromService(int win, string json)
        {
            Proc p;
            lock (gate)
            {
                if (!byWindow.TryGetValue(win, out p))
                {
                    // The frame's first word can come before its id is known here: find it by window.
                    p = running.Values.FirstOrDefault(x => x.Window == 0);
                    if (p == null) { LunaLog.Write("[services] message from unknown window " + win); return; }
                    p.Window = win; byWindow[win] = p;
                }
            }
            JObj m;
            try { m = Jx.ParseObj(json); } catch (FormatException) { LunaLog.Write("[services] unreadable message from " + p.Dir); return; }
            switch (m.Str("t"))
            {
                case "ready":
                    {
                        List<string> q;
                        lock (gate) { p.Ready = true; q = p.Queue.ToList(); p.Queue.Clear(); }
                        LunaLog.Write("[services] " + p.Dir + " ready");
                        foreach (var x in q) Send(p, x);
                        break;
                    }
                case "response":
                    {
                        Bus.Call c;
                        var id = m.Str("id");
                        lock (gate) p.Incoming.TryGetValue(id, out c);
                        if (c == null) return;
                        c.Reply(m.Str("payload"));
                        if (!c.Subscribe) lock (gate) p.Incoming.Remove(id);
                        break;
                    }
                case "call":
                    {
                        var id = m.Str("id");
                        var url = m.Str("url");
                        var target = url;
                        var mm = System.Text.RegularExpressions.Regex.Match(url, "^(?:palm|luna)://([^/]+)/");
                        if (mm.Success) target = mm.Groups[1].Value;
                        var payload = m.Str("payload");
                        if (m.Bool("subscribe"))
                        {
                            // A subscription asked through Handle.subscribe: the bus sees it as one.
                            var po = Jx.ObjOrEmpty(payload);
                            if (!po.Has("subscribe") && !po.Has("watch")) { po.Put("subscribe", true); payload = po.ToString(); }
                        }
                        var call = shell.Bus.Dispatch(p.Name, url, payload, reply =>
                            Send(p, new JObj().Put("t", "callResponse").Put("id", id).Put("payload", reply).Put("sender", target).Put("subscribe", m.Bool("subscribe")).ToString()));
                        lock (gate) p.Outgoing[id] = call;
                        break;
                    }
                case "cancelCall":
                    {
                        Bus.Call c;
                        lock (gate) { p.Outgoing.TryGetValue(m.Str("id"), out c); p.Outgoing.Remove(m.Str("id")); }
                        if (c != null) c.Cancel();
                        break;
                    }
                case "exit":
                    Stop(p, "exited (" + m.Long("code") + ")");
                    break;
            }
        }

        private void Stop(Proc p, string why)
        {
            List<Bus.Call> calls, outgoing;
            int win;
            lock (gate)
            {
                if (!running.ContainsKey(p.Dir) || running[p.Dir] != p) return;
                running.Remove(p.Dir);
                byWindow.Remove(p.Window);
                win = p.Window;
                calls = p.Incoming.Values.ToList(); p.Incoming.Clear();
                outgoing = p.Outgoing.Values.ToList(); p.Outgoing.Clear();
            }
            LunaLog.Write("[services] " + p.Dir + " " + why);
            // Callers still waiting hear that the service went, as when its process ended.
            foreach (var c in calls) { c.Reply(Bus.Error("Service exited: " + p.Name)); c.Cancel(); }
            foreach (var c in outgoing) c.Cancel();
            if (win != 0) shell.Evaluate("__lunacyShell.stopService(" + win + ")");
        }

        /// The shell's page went (a reload): every frame with it.
        public void Reset()
        {
            List<Proc> all;
            lock (gate) all = running.Values.ToList();
            foreach (var p in all) Stop(p, "stopped with the shell's page");
        }
    }
}
