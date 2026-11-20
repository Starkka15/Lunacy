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
            /// On webOS's private bus: the system's own callers (JS services, the activity manager,
            /// db8, package scripts) and apps whose ids are privileged (Bus.kt's privateBus).
            public bool PrivateBus { get; internal set; }

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
            Register("com.palm.bus", "signal/addmatch", AddMatch);
        }

        /// Open registerServerStatus calls, with the service each watches; open addmatch calls,
        /// with the category and method each matches (Bus.kt).
        private readonly List<KeyValuePair<Call, string>> statusWatchers = new List<KeyValuePair<Call, string>>();
        private readonly List<Tuple<Call, string, string>> signalWatchers = new List<Tuple<Call, string, string>>();

        /// palm://com.palm.bus/signal/addmatch: hear a signal a service sends, by category and,
        /// optionally, method. Measured on the reference TouchPad: {"returnValue":true} with or
        /// without subscribe, then the signals; with no category it never answers.
        private void AddMatch(Call c)
        {
            var category = c.Params.GetString("category") ?? "";
            if (category.Length == 0) return;
            c.Reply(Ok());
            var w = Tuple.Create(c, category, c.Params.GetString("method") ?? "");
            lock (signalWatchers) signalWatchers.Add(w);
            c.OnCancel(() => { lock (signalWatchers) signalWatchers.Remove(w); });
        }

        /// Sends a signal to whoever matched it with addmatch.
        public void Signal(string category, string method, string payload)
        {
            Tuple<Call, string, string>[] all;
            lock (signalWatchers) all = signalWatchers.ToArray();
            foreach (var w in all) if (w.Item2 == category && (w.Item3.Length == 0 || w.Item3 == method)) w.Item1.Reply(payload);
        }

        /// A service came or went (a package's JS services do): tell whoever is watching it.
        private void ServerStatusChanged(string service)
        {
            var connected = Has(service);
            KeyValuePair<Call, string>[] all;
            lock (statusWatchers) all = statusWatchers.ToArray();
            foreach (var w in all) if (w.Value == service) w.Key.Reply(J.Obj(J.P("serviceName", service), J.P("connected", connected)));
        }

        public void Register(string service, string method, Action<Call> h)
        {
            lock (handlers) handlers[service + "/" + method] = h;
        }

        /// A method only the private bus reaches (Bus.kt's registerPrivate). A public caller gets
        /// what the TouchPad gave: for a service with no public side at all (wholeService),
        /// "Service does not exist" (the systemmanager, the device profile, the installers); for a
        /// private method on a public service, the router's own "Unknown method".
        public void RegisterPrivate(string service, string method, bool wholeService, Action<Call> h)
        {
            Register(service, method, c =>
            {
                if (c.PrivateBus) { h(c); return; }
                c.Reply(wholeService ? NoService(service) : UnknownMethod(method));
            });
        }

        public static string NoService(string service) { return Error("Service does not exist: " + service + "."); }
        public static string UnknownMethod(string method)
        {
            int slash = method.LastIndexOf('/');
            var leaf = slash < 0 ? method : method.Substring(slash + 1);
            var category = "/" + (slash < 0 ? "" : method.Substring(0, slash));
            return Error("Unknown method \"" + leaf + "\" for category \"" + category + "\"");
        }

        /// The ids webOS let onto the private bus whatever process they were in (Bus.kt): every
        /// com.palm.* app and service. Palm's own apps reached private methods this way, and the
        /// community's apps that use them took com.palm.* ids for exactly that reason.
        public static bool Privileged(string appId) { return appId != null && appId.StartsWith("com.palm.", StringComparison.Ordinal); }

        /// A call from the system itself, on the private bus.
        public Call DispatchPrivate(string appId, string url, string parameters, Action<string> reply)
        {
            return Dispatch(appId, url, parameters, reply, true);
        }

        // Passwords and tokens out of the log's copy of a call's parameters (Bus.kt's redact).
        private static readonly Regex Secret = new Regex(@"(""(?:password|passwd|pin|secret|token|authToken|accessToken|refreshToken|client_secret)""\s*:\s*)""(?:[^""\\]|\\.)*""", RegexOptions.IgnoreCase);
        private static readonly Regex UrlSecret = new Regex(@"([?&#](?:code|access_token|id_token|refresh_token)=)[^&#""\\\s]*");
        public static string Redact(string parameters)
        {
            if (string.IsNullOrEmpty(parameters)) return "";
            return UrlSecret.Replace(Secret.Replace(parameters, m => m.Groups[1].Value + "\"â€¦\""), m => m.Groups[1].Value + "â€¦");
        }

        private readonly Dictionary<string, Action<Call>> services = new Dictionary<string, Action<Call>>();

        /// Every method of a service, for a JS service package: the package answers what it has
        /// and errors for what it hasn't, as ls-hubd passed it every call to its name.
        public void RegisterService(string service, Action<Call> h)
        {
            lock (handlers) services[service] = h;
            ServerStatusChanged(service);
        }

        public void UnregisterService(string service)
        {
            lock (handlers) services.Remove(service);
            ServerStatusChanged(service);
        }

        public bool Has(string service)
        {
            lock (handlers) return services.ContainsKey(service) || handlers.Keys.Any(k => k.StartsWith(service + "/", StringComparison.Ordinal));
        }

        public Call Dispatch(string appId, string url, string parameters, Action<string> reply, bool privateBus = false)
        {
            string service = "", method = "";
            var m = Uri.Match(url ?? "");
            if (m.Success) { service = m.Groups[1].Value; method = m.Groups[2].Value; }
            var c = new Call(appId, service, method, J.ParseObject(parameters), reply) { Raw = string.IsNullOrEmpty(parameters) ? "{}" : parameters, PrivateBus = privateBus || Privileged(appId) };
            Action<Call> h;
            lock (handlers) { if (!handlers.TryGetValue(service + "/" + method, out h)) services.TryGetValue(service, out h); }
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
                c.Reply(Has(service) ? UnknownMethod(method) : NoService(service));
            }
            return c;
        }

        /// ls-hubd's own signal: is a service on the bus? {"serviceName", "connected"}, no returnValue.
        private void ServerStatus(Call c)
        {
            var name = c.Params.GetString("serviceName");
            if (string.IsNullOrEmpty(name)) { c.Reply(Error("Invalid payload.")); return; }
            c.Reply(J.Obj(J.P("serviceName", name), J.P("connected", Has(name))));
            // A subscription answers again when that changes (measured on the reference TouchPad).
            if (c.Subscribe && !c.Cancelled)
            {
                var w = new KeyValuePair<Call, string>(c, name);
                lock (statusWatchers) statusWatchers.Add(w);
                c.OnCancel(() => { lock (statusWatchers) statusWatchers.Remove(w); });
            }
        }

        public static string Error(string text, int code = -1)
        {
            return J.Obj(J.P("returnValue", false), J.P("errorCode", code), J.P("errorText", text));
        }

        public static string Ok() { return "{\"returnValue\":true}"; }

        /// Whether a call keeps answering, by its URL and payload, before it is made (Bus.kt's
        /// staysOpen): "subscribe" or "watch" in the payload, or db8's watch. The pages' bridge
        /// keeps such a call's callback for the replies after the first.
        public static bool StaysOpen(string url, string parameters)
        {
            var m = Uri.Match(url ?? "");
            var method = m.Success ? m.Groups[2].Value : "";
            var p = J.ParseObject(string.IsNullOrEmpty(parameters) ? "{}" : parameters);
            return p.GetBool("subscribe") || p.GetBool("watch") || method == "watch";
        }
    }
}
