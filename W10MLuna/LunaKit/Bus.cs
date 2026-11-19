using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Windows.Data.Json;

namespace LunaKit
{
    /// The simulated Luna bus, as AndroidLuna's Bus.kt has it: `palm://service/method` to a
    /// handler, subscriptions that keep delivering until cancelled, and webOS's own errors for
    /// anything that isn't here. It never fakes a success (Lunacy's rule 2). Thread-safe: calls
    /// arrive on whatever thread the WebView calls the bridge on.
    internal sealed class Bus
    {
        internal sealed class Call
        {
            public readonly string AppId, Service, Method;
            public readonly JsonObject Params;
            /// The payload as sent: some methods take an array (timezone/getTimeZoneRules).
            public string Raw = "{}";
            private readonly Action<string> send;
            private readonly object gate = new object();
            private List<Action> onCancel = new List<Action>();
            public bool Cancelled { get; private set; }

            public Call(string appId, string service, string method, JsonObject p, Action<string> send)
            {
                AppId = appId; Service = service; Method = method; Params = p; this.send = send;
            }

            /// "subscribe": true, or a db8 watch.
            public bool Subscribe
            {
                get { return Params.GetBool("subscribe") || Params.GetBool("watch") || Method == "watch"; }
            }

            public void Reply(string json) { if (!Cancelled) send(json); }

            /// Runs when the subscription ends.
            public void OnCancel(Action f)
            {
                lock (gate) { if (!Cancelled) { onCancel.Add(f); return; } }
                f();
            }

            public void Cancel()
            {
                List<Action> fs;
                lock (gate)
                {
                    if (Cancelled) return;
                    Cancelled = true;
                    fs = onCancel; onCancel = new List<Action>();
                }
                foreach (var f in fs) f();
            }
        }

        private readonly Dictionary<string, Action<Call>> handlers = new Dictionary<string, Action<Call>>();
        private static readonly Regex Uri = new Regex("^(?:palm|luna)://([^/]+)/(.*?)/?$");

        public Bus()
        {
            Register("com.palm.bus", "signal/registerServerStatus", ServerStatus);
        }

        public void Register(string service, string method, Action<Call> h)
        {
            lock (handlers) handlers[service + "/" + method] = h;
        }

        public bool Has(string service)
        {
            lock (handlers) return handlers.Keys.Any(k => k.StartsWith(service + "/", StringComparison.Ordinal));
        }

        public Call Dispatch(string appId, string url, string parameters, Action<string> reply)
        {
            string service = "", method = "";
            var m = Uri.Match(url ?? "");
            if (m.Success) { service = m.Groups[1].Value; method = m.Groups[2].Value; }
            var c = new Call(appId, service, method, J.ParseObject(parameters), reply) { Raw = string.IsNullOrEmpty(parameters) ? "{}" : parameters };
            Action<Call> h;
            lock (handlers) handlers.TryGetValue(service + "/" + method, out h);
            LunaLog.Write("[LunaKit] bus [" + appId + "] " + service + "/" + method + (h == null ? " UNHANDLED" : ""));
            if (h != null)
            {
                try { h(c); }
                catch (Exception e)
                {
                    LunaLog.Write("[LunaKit] bus handler " + service + "/" + method + " threw: " + e);
                    c.Reply(Error("Internal error: " + e.Message));
                }
            }
            else
            {
                // webOS names the category the method sits in (measured on the reference TouchPad).
                int slash = method.LastIndexOf('/');
                var leaf = slash < 0 ? method : method.Substring(slash + 1);
                var category = "/" + (slash < 0 ? "" : method.Substring(0, slash));
                c.Reply(Error(Has(service) ? "Unknown method \"" + leaf + "\" for category \"" + category + "\"" : "Service does not exist: " + service + "."));
            }
            return c;
        }

        /// ls-hubd's own signal: is a service on the bus? {"serviceName", "connected"}, no returnValue.
        private void ServerStatus(Call c)
        {
            var name = c.Params.GetString("serviceName");
            if (string.IsNullOrEmpty(name)) { c.Reply(Error("Invalid payload.")); return; }
            c.Reply(J.Obj(J.P("serviceName", name), J.P("connected", Has(name))));
        }

        public static string Error(string text, int code = -1)
        {
            return J.Obj(J.P("returnValue", false), J.P("errorCode", code), J.P("errorText", text));
        }

        public static string Ok() { return "{\"returnValue\":true}"; }
    }
}
