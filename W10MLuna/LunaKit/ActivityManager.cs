using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LunaKit
{
    /// palm://com.palm.activitymanager, as LunaCore's ActivityManager.kt has it: webOS's scheduler
    /// of work. An activity runs when its trigger has fired (a bus subscription whose reply
    /// matches), its schedule is due (a start time, an interval) and its requirements are met
    /// (internet, a connection's confidence, charging...); running, it tells its subscribers
    /// "start" and calls its callback with "$activity", and whoever handles it adopts it and
    /// completes it, perhaps with restart, which arms it again. Persistent activities live in db8
    /// (com.palm.activity:1) and come back when Lunacy starts. Every Mojo service command runs in
    /// one (mojoservice creates it when the caller passed none), so no JS service answers without
    /// this.
    ///
    /// Replies are the reference TouchPad's (webOS CE 3.1.0), as ActivityManager.kt measured them.
    /// What Android adds for its own sleep (RTC wakeups, Doze's network blocks) has no W10M
    /// counterpart: an app that is suspended runs nothing, so a schedule falls due on a plain
    /// timer while Lunacy runs, and at once when it comes back.
    ///
    /// One thread at a time throughout: every entry (a call, a reply, a timer) is posted to a
    /// serial queue, as Android's ran on its main thread.
    internal sealed class ActivityManager
    {
        public const string Service = "com.palm.activitymanager";
        private const string Kind = "com.palm.activity:1";
        private const int RetryMs = 5000;
        private static readonly string[] Priorities = { "none", "lowest", "low", "normal", "high", "highest" };
        private static readonly HashSet<string> WhereOps = new HashSet<string> { "<", "<=", "=", "!=", ">=", ">", "where" };

        private readonly Bus bus;
        private readonly Serial main = new Serial();
        private readonly ActivityRequirements requirements;
        private static readonly Random random = new Random();

        private sealed class Sub
        {
            public readonly Bus.Call Call; public readonly bool Detailed; public readonly JObj Who;
            public Sub(Bus.Call call, bool detailed, JObj who) { Call = call; Detailed = detailed; Who = who; }
        }

        private sealed class DbRecord
        {
            public string Id; public long? Rev; public bool Saving;
            public readonly Queue<Action> Waiting = new Queue<Action>();
        }

        private sealed class Activity
        {
            public int Id; public string Name; public readonly JObj Creator;
            public string Description = "";
            public JObj Metadata;
            public bool Persist, Explicit, Continuous, UserInitiated, Power, PowerDebounce;
            public bool Immediate; public string Priority = "low";
            public string SimpleType = "background";
            public string BusType = "private";
            public JObj Callback, Trigger;
            public Schedule Schedule;
            public JObj Requirements = new JObj();

            public readonly List<Sub> Subs = new List<Sub>();
            public Sub Parent, ReleasedParent;
            public readonly LinkedList<Sub> Adopters = new LinkedList<Sub>();

            public bool Initialized, Running, Paused, Queued;
            public string Ending;
            public bool Terminate, Restart, Focused;
            public Bus.Call TriggerCall;
            public JObj Fired;
            public long Serial;
            public DbRecord Record = new DbRecord();

            public Activity(int id, string name, JObj creator) { Id = id; Name = name; Creator = creator; }
            public string Key { get { return Name + "\0" + CreatorKey(Creator); } }
        }


        private readonly Dictionary<int, Activity> activities = new Dictionary<int, Activity>();
        private readonly List<int> order = new List<int>();   // creation order, as a LinkedHashMap kept it
        private readonly Dictionary<string, Activity> byName = new Dictionary<string, Activity>();
        private int nextId = 1;
        private bool loaded;
        private readonly List<Action> held = new List<Action>();
        private bool started, external = true;
        private bool Enabled { get { return started && external; } }
        private readonly List<Activity> waitingForEnable = new List<Activity>();
        private readonly List<Activity> queue = new List<Activity>();
        private readonly Dictionary<int, long> due = new Dictionary<int, long>();
        private Timer timer;

        public ActivityManager(Bus bus)
        {
            this.bus = bus;
            requirements = new ActivityRequirements(bus, Service, main, change =>
            {
                var before = All().ToDictionary(a => a, a => RequirementsMet(a));
                change();
                RequirementsChanged(before);
            });
        }

        private IEnumerable<Activity> All() { return order.Where(activities.ContainsKey).Select(i => activities[i]).ToList(); }
        private void Add(Activity a) { if (!activities.ContainsKey(a.Id)) order.Add(a.Id); activities[a.Id] = a; }
        private void Forget(Activity a) { activities.Remove(a.Id); order.Remove(a.Id); }
        private Activity Get(int id) { Activity a; return activities.TryGetValue(id, out a) ? a : null; }

        public void Register()
        {
            var methods = new Dictionary<string, Action<Bus.Call>>
            {
                { "create", Create }, { "join", Join }, { "monitor", Monitor }, { "release", Release }, { "adopt", Adopt },
                { "complete", Complete },
                { "schedule", c => WithActivity(c, a => External(a, "start", c)) },
                { "start", c => WithActivity(c, a => External(a, "start", c)) },
                { "stop", c => WithActivity(c, a => End(a, "stop", c)) },
                { "cancel", c => WithActivity(c, a => End(a, "cancel", c)) },
                { "pause", c => WithActivity(c, a => External(a, "pause", c)) },
                { "focus", Focus }, { "unfocus", Unfocus }, { "addFocus", AddFocus },
                { "list", List }, { "getDetails", GetDetails },
                { "enable", c => SwitchExternal(c, true) }, { "disable", c => SwitchExternal(c, false) },
            };
            foreach (var kv in methods)
            {
                var h = kv.Value;
                bus.Register(Service, kv.Key, c => main.Post(() => { if (loaded) h(c); else held.Add(() => h(c)); }));
            }
            foreach (var m in new[] { "associateApp", "associateService", "associateProcess", "associateNetworkFlow",
                "dissociateApp", "dissociateService", "dissociateProcess", "dissociateNetworkFlow", "unmapProcess" })
                bus.Register(Service, m, c => c.Reply(Bus.Ok()));
            main.Post(() => { requirements.Watch(); Load(); });
        }

        // ---- identity and lookup ----

        private static JObj P(Bus.Call c) { return Jx.ObjOrEmpty(c.Raw); }

        /// The caller as webOS's BusId: a service by its name, an app by its id.
        private JObj BusId(Bus.Call call)
        {
            return bus.Has(call.AppId) ? new JObj().Put("serviceId", call.AppId) : new JObj().Put("appId", call.AppId);
        }

        private static string CreatorKey(JObj o)
        {
            var s = o.Str("serviceId"); if (s.Length > 0) return s;
            s = o.Str("appId"); if (s.Length > 0) return s;
            return o.Str("anonId");
        }

        private Activity ActivityOrReplyError(Bus.Call call)
        {
            var p = P(call);
            Func<string, Activity> fail = text => { call.Reply(Bus.Error(text, 2)); return null; };
            if (p.Has("activityId"))
            {
                var v = p["activityId"];
                if (!Jx.IsNumber(v)) return fail("Error retrieving activityId of Activity to operate on");
                return Get((int)Jx.Num(v)) ?? fail("activityId not found");
            }
            if (p.Has("activityName"))
            {
                var name = p["activityName"] as string;
                if (name == null) return fail("Error retrieving activityName of Activity to operate on");
                Activity a;
                return byName.TryGetValue(name + "\0" + CreatorKey(BusId(call)), out a) ? a : fail("Activity name/creator pair not found");
            }
            return fail("Activity ID or name not present in request");
        }

        private void WithActivity(Bus.Call call, Action<Activity> f) { var a = ActivityOrReplyError(call); if (a != null) f(a); }

        private bool SerialOk(Activity a, Bus.Call call)
        {
            var p = P(call);
            if (!p.Has("serial")) return true;
            if (a.Callback == null) { call.Reply(Bus.Error("Attempt to use callback sequence serial number match on Activity that does not have a callback", -1000)); return false; }
            if (p.Long("serial") != a.Serial) { call.Reply(Bus.Error("Call sequence serial numbers do not match", -1000)); return false; }
            return true;
        }

        // ---- create ----

        private void Create(Bus.Call call)
        {
            var p = P(call);
            var spec = p.Obj("activity");
            if (spec == null) { call.Reply(Bus.Error("Activity specification not present", 22)); return; }
            Activity a;
            try { a = Parse(spec, BusId(call)); } catch (SpecError e) { call.Reply(Bus.Error(e.Message, 12)); return; }
            if (!p.Bool("subscribe") && !(p.Bool("start") && a.Callback != null))
            { call.Reply(Bus.Error("Created Activity must specify \"start\" and a Callback if not subscribed", 22)); return; }
            Activity old;
            byName.TryGetValue(a.Key, out old);
            if (old != null && !p.Bool("replace")) { call.Reply(Bus.Error("Activity with that name already exists", 17)); return; }
            a.Id = NewId();
            if (old != null)
            {
                // The new one takes the old one's place in db8; the old one is cancelled.
                if (a.Persist) a.Record = old.Record; else if (old.Record.Id != null) DbDel(old.Record.Id);
                old.Record = new DbRecord();
                old.Terminate = true;
                byName.Remove(old.Key);
                Command(old, "cancel");
            }
            Add(a);
            byName[a.Key] = a;
            if (p.Bool("subscribe")) a.Parent = Subscribe(a, call);
            Action go = () =>
            {
                call.Reply(new JObj().Put("activityId", a.Id).Put("returnValue", true).ToString());
                if (p.Bool("start"))
                {
                    Start(a);
                    if (!a.Running) Update(a);
                }
            };
            if (a.Persist) Persist(a, go); else go();
        }

        private Activity Parse(JObj spec, JObj caller)
        {
            var name = spec["name"] as string;
            if (name == null) throw new SpecError("Activity name is required");
            var description = spec["description"] as string;
            if (description == null) throw new SpecError("Activity description is required");
            var creator = caller;
            if (spec.Has("creator"))
            {
                var c = spec["creator"];
                creator = null;
                if (c is JObj) creator = (JObj)c;
                else if (c is string)
                {
                    var parts = ((string)c).Split(new[] { ':' }, 2);
                    if (parts.Length == 2) creator = new JObj().Put(parts[0], parts[1]);
                }
                if (creator == null) throw new SpecError("Invalid creator specified");
            }
            var a = new Activity(0, name, creator) { Description = description };
            if (spec.Has("metadata"))
            {
                a.Metadata = spec["metadata"] as JObj;
                if (a.Metadata == null) throw new SpecError("Object metadata should be set to a JSON object");
            }
            if (spec.Obj("type") != null) ParseType(a, spec.Obj("type"));
            if (spec.Obj("callback") != null) a.Callback = Url(spec.Obj("callback"), "Callback");
            if (spec.Obj("trigger") != null) a.Trigger = ParseTrigger(spec.Obj("trigger"));
            if (spec.Obj("schedule") != null) a.Schedule = Schedule.Parse(spec.Obj("schedule"));
            var r = spec.Obj("requirements");
            if (r != null) foreach (var kv in r) SetRequirement(a, kv.Key, kv.Value);
            return a;
        }

        private static void ParseType(Activity a, JObj t)
        {
            bool fg = t.Has("foreground"), bg = t.Has("background");
            if (fg && !t.Bool("foreground")) throw new SpecError("If present, 'foreground' should be specified as 'true'");
            if (bg && !t.Bool("background")) throw new SpecError("If present, 'background' should be specified as 'true'");
            if (fg && bg) throw new SpecError("Only one of 'foreground' or 'background' should be specified");
            if ((fg || bg) && t.Has("immediate")) throw new SpecError("Only one of 'foreground', 'background', or 'immediate' should be specified");
            if ((fg || bg) && t.Has("priority")) throw new SpecError("Only one of 'foreground', 'background', or 'priority' should be set");
            if (fg) { a.Immediate = true; a.Priority = "normal"; a.SimpleType = "foreground"; }
            else if (bg) { a.Immediate = false; a.Priority = "low"; a.SimpleType = "background"; }
            else
            {
                a.SimpleType = null;
                a.Immediate = t.Bool("immediate");
                a.Priority = t.Str("priority", "low");
                if (Array.IndexOf(Priorities, a.Priority) < 0) throw new SpecError("Invalid priority specified");
            }
            a.Persist = t.Bool("persist"); a.Explicit = t.Bool("explicit"); a.Continuous = t.Bool("continuous");
            a.UserInitiated = t.Bool("userInitiated"); a.Power = t.Bool("power"); a.PowerDebounce = t.Bool("powerDebounce");
            var b = t.Str("bus");
            if (b.Length > 0) { if (b != "public" && b != "private") throw new SpecError("Illegal bus type seen"); a.BusType = b; }
        }

        /// A callback or trigger's method, as webOS checked it, reported back in palm:// form.
        private static JObj Url(JObj o, string what)
        {
            var m = o["method"] as string;
            if (m == null) throw new SpecError("Method URL for " + what + " is required");
            if (m.Length < 8) throw new SpecError("URL string too short to be valid.");
            if (!m.StartsWith("palm://", StringComparison.Ordinal) && !m.StartsWith("luna://", StringComparison.Ordinal))
                throw new SpecError("URL does not start with \"palm://\" or \"luna://\".");
            var rest = m.Substring(7);
            if (!rest.Contains("/") || rest.EndsWith("/") && rest.IndexOf('/') == rest.Length - 1) throw new SpecError("URL does not specify a method.");
            return o.Copy().Put("method", "palm://" + rest);
        }

        private static JObj ParseTrigger(JObj t)
        {
            if (!t.Has("method")) throw new SpecError("Method URL for Trigger is required");
            var o = Url(t, "Trigger");
            var c = t.Obj("compare");
            if (c != null && (!c.Has("key") || !c.Has("value"))) throw new SpecError("Compare Trigger requires key to compare and value to compare against be specified.");
            if (t.Has("where")) ValidWhere(t["where"]);
            return o;
        }

        private static void ValidWhere(object w)
        {
            const string clause = "Each where clause must contain \"or\", \"and\", or a \"prop\"erty to compare against";
            var arr = w as JArr;
            if (arr != null) { foreach (var x in arr) ValidWhere(x); return; }
            var o = w as JObj;
            if (o == null) throw new SpecError(clause);
            if (o.Has("and")) ValidWhere(o["and"]);
            else if (o.Has("or")) ValidWhere(o["or"]);
            else if (o.Has("prop")) { if (!WhereOps.Contains(o.Str("op"))) throw new SpecError("Operation must be one of '<', '<=', '=', '>=', '>', '!=', and 'where'"); }
            else throw new SpecError(clause);
        }

        private int NewId()
        {
            while (activities.ContainsKey(nextId)) nextId++;
            return nextId++;
        }

        // ---- subscriptions ----

        private Sub Subscribe(Activity a, Bus.Call call)
        {
            var s = new Sub(call, P(call).Bool("detailedEvents"), BusId(call));
            a.Subs.Add(s);
            // Posted: a call cancelled already runs this at once, before create has made the
            // subscription the activity's parent.
            call.OnCancel(() => main.Post(() => Dropped(a, s)));
            return s;
        }

        private void Dropped(Activity a, Sub s)
        {
            if (!a.Subs.Remove(s)) return;
            a.Adopters.Remove(s);
            if (a.ReleasedParent == s) a.ReleasedParent = null;
            if (a.Parent == s)
            {
                a.Parent = null;
                var next = a.Adopters.First;
                if (next != null) { a.Adopters.RemoveFirst(); a.Parent = next.Value; Event(a, "orphan", new List<Sub> { next.Value }); }
                else if (a.Subs.Count > 0) Orphaned(a);
            }
            if (a.Subs.Count == 0 && (a.Running || a.Ending != null || a.Initialized))
            {
                // Abandoned: nobody is left to finish it, so it ends (and may be armed again).
                if (a.Ending == null && (a.Running || a.Callback == null)) a.Ending = "cancel";
                if (a.Ending != null) FinishIfDone(a);
            }
        }

        private void Orphaned(Activity a)
        {
            if (a.Running) Command(a, "cancel");
            else if (a.Initialized && a.Callback != null) { }
            else Command(a, "cancel");
        }

        private void Join(Bus.Call call)
        {
            WithActivity(call, a =>
            {
                if (!call.Subscribe) { call.Reply(Bus.Error("Join method calls must subscribe to the Activity", 22)); return; }
                if (!SerialOk(a, call)) return;
                Subscribe(a, call);
                call.Reply(Bus.Ok());
            });
        }

        private void Monitor(Bus.Call call)
        {
            WithActivity(call, a =>
            {
                if (!SerialOk(a, call)) return;
                if (call.Subscribe) Subscribe(a, call);
                call.Reply(new JObj().Put("state", State(a)).Put("returnValue", true).ToString());
            });
        }

        private void Adopt(Bus.Call call)
        {
            WithActivity(call, a =>
            {
                if (!call.Subscribe) { call.Reply(Bus.Error("AdoptActivity requires subscription", 22)); return; }
                if (!SerialOk(a, call)) return;
                var s = Subscribe(a, call);
                if (a.Parent != null)
                {
                    if (!P(call).Bool("wait")) { call.Reply(Bus.Error("Resource temporarily unavailable", 11)); return; }
                    a.Adopters.AddLast(s);
                    call.Reply(new JObj().Put("adopted", false).Put("returnValue", true).ToString());
                }
                else
                {
                    a.Parent = s;
                    Event(a, "orphan", new List<Sub> { s });
                    call.Reply(new JObj().Put("adopted", true).Put("returnValue", true).ToString());
                    if (a.ReleasedParent != null) Event(a, "adopted", new List<Sub> { a.ReleasedParent });
                }
            });
        }

        private void Release(Bus.Call call)
        {
            WithActivity(call, a =>
            {
                if (a.Parent == null) { call.Reply(Bus.Error("Invalid argument", 22)); return; }
                if (CreatorKey(a.Parent.Who) != CreatorKey(BusId(call))) { call.Reply(Bus.Error("Permission denied", 13)); return; }
                a.ReleasedParent = a.Parent;
                a.Parent = null;
                var next = a.Adopters.First;
                if (next != null) { a.Adopters.RemoveFirst(); a.Parent = next.Value; Event(a, "orphan", new List<Sub> { next.Value }); }
                call.Reply(Bus.Ok());
            });
        }

        // ---- commands ----

        private void External(Activity a, string cmd, Bus.Call call)
        {
            if (a.Ending != null) { call.Reply(Bus.Error("The Activity Manager has already received a command to end the Activity", -1000)); return; }
            if (cmd == "start") { if (a.Paused) { a.Paused = false; Event(a, "start"); Evaluate(a); } else Start(a); }
            else if (cmd == "pause") { a.Paused = true; Event(a, "pause"); }
            call.Reply(Bus.Ok());
        }

        private void End(Activity a, string cmd, Bus.Call call)
        {
            if (a.Ending != null) { call.Reply(Bus.Error("The Activity Manager has already received a command to end the Activity", -1000)); return; }
            a.Terminate = true;
            byName.Remove(a.Key);
            Command(a, cmd);
            var id = a.Record.Id;
            a.Record.Id = null;
            if (id != null) DbDel(id, () => call.Reply(Bus.Ok())); else call.Reply(Bus.Ok());
        }

        private void Complete(Bus.Call call)
        {
            WithActivity(call, a =>
            {
                if (a.Ending != null) { call.Reply(Bus.Error("The Activity Manager has already received a command to end the Activity", -1000)); return; }
                if (!SerialOk(a, call)) return;
                var p = P(call);
                var caller = CreatorKey(BusId(call));
                if (!p.Bool("force") && caller != CreatorKey(a.Creator) && (a.Parent == null || CreatorKey(a.Parent.Who) != caller))
                { call.Reply(Bus.Error("Permission denied", 13)); return; }
                if (p.Bool("restart"))
                {
                    try { ApplyUpdates(a, p); } catch (SpecError e) { call.Reply(Bus.Error(e.Message, -1000)); return; }
                    a.Restart = true;
                    Command(a, "complete");
                    if (a.Persist) Persist(a, () => call.Reply(Bus.Ok())); else call.Reply(Bus.Ok());
                }
                else
                {
                    a.Terminate = true;
                    Command(a, "complete");
                    var id = a.Record.Id; a.Record.Id = null;
                    Action done = () => { Activity cur; if (byName.TryGetValue(a.Key, out cur) && cur == a) byName.Remove(a.Key); call.Reply(Bus.Ok()); };
                    if (id != null) DbDel(id, done); else done();
                }
            });
        }

        private static bool IsFalse(object v) { return v is bool && !(bool)v; }

        /// complete's changes to a restarting activity, all or none.
        private void ApplyUpdates(Activity a, JObj p)
        {
            JObj trigger = a.Trigger;
            if (p.Has("trigger"))
            {
                var t = p["trigger"];
                if (IsFalse(t)) trigger = null;
                else if (t is JObj) trigger = ParseTrigger((JObj)t);
                else throw new SpecError("\"trigger\":false to clear, or valid trigger property must be specified");
            }
            Schedule schedule = a.Schedule;
            if (p.Has("schedule"))
            {
                var s = p["schedule"];
                if (IsFalse(s)) schedule = null;
                else if (s is JObj) schedule = Schedule.Parse((JObj)s);
                else throw new SpecError("Invalid schedule");
            }
            JObj callback = a.Callback;
            if (p.Has("callback"))
            {
                if (p["callback"] is JObj) callback = Url((JObj)p["callback"], "Callback");
                else throw new SpecError("Activity callback may not be removed by Complete");
            }
            JObj metadata = a.Metadata;
            if (p.Has("metadata"))
            {
                var m = p["metadata"];
                if (IsFalse(m)) metadata = null;
                else if (m is JObj) metadata = (JObj)m;
                else throw new SpecError("Attempt to set metadata to a non-object type");
            }
            var reqs = a.Requirements.Copy();
            var r = p.Obj("requirements");
            if (r != null) foreach (var kv in r) { if (IsFalse(kv.Value) || kv.Value is JNull) reqs.Remove(kv.Key); else reqs.Put(kv.Key, kv.Value); }
            var probe = new Activity(0, a.Name, a.Creator);
            foreach (var kv in reqs) SetRequirement(probe, kv.Key, kv.Value);
            if (schedule != a.Schedule && schedule != null) schedule.LastFinished = a.Schedule != null ? a.Schedule.LastFinished : null;
            a.Trigger = trigger; a.Schedule = schedule; a.Callback = callback; a.Metadata = metadata; a.Requirements = probe.Requirements;
        }

        /// A command takes effect: its event goes out, and an ending one starts the end.
        private void Command(Activity a, string cmd)
        {
            Event(a, cmd);
            a.Ending = cmd;
            Disarm(a);
            if (a.Queued) { a.Queued = false; queue.Remove(a); }
            FinishIfDone(a);
        }

        // ---- running ----

        private void Start(Activity a)
        {
            if (!Enabled) { if (!waitingForEnable.Contains(a)) waitingForEnable.Add(a); return; }
            if (a.Initialized || a.Ending != null) { Evaluate(a); return; }
            a.Initialized = true;
            Arm(a);
            Evaluate(a);
        }

        private void Arm(Activity a)
        {
            a.Fired = null;
            if (a.Trigger != null) ArmTrigger(a, a.Trigger);
            if (a.Schedule != null) { a.Schedule.Queue(); ScheduleTimer(a); }
        }

        private void Disarm(Activity a)
        {
            if (a.TriggerCall != null) { a.TriggerCall.Cancel(); a.TriggerCall = null; }
            if (due.Remove(a.Id)) ArmTimer();
        }

        /// Whether the activity may run now, and if so, runs or queues it.
        private void Evaluate(Activity a)
        {
            if (!a.Initialized || a.Running || a.Queued || a.Paused || a.Ending != null) return;
            if (a.Trigger != null && a.Fired == null) return;
            if (a.Schedule != null && !a.Schedule.Due) return;
            if (!RequirementsMet(a)) return;
            if (a.Immediate) Run(a); else { a.Queued = true; queue.Add(a); RunQueue(); }
        }

        /// One background activity at a time, and two that a user started, as webOS's queues ran them.
        private void RunQueue()
        {
            while (true)
            {
                var all = All().ToList();
                int bg = all.Count(x => x.Running && !x.Immediate && !x.UserInitiated);
                int ui = all.Count(x => x.Running && !x.Immediate && x.UserInitiated);
                var next = queue.OrderByDescending(x => Array.IndexOf(Priorities, x.Priority))
                    .FirstOrDefault(x => x.UserInitiated ? ui < 2 : bg < 1);
                if (next == null) return;
                queue.Remove(next); next.Queued = false;
                Run(next);
            }
        }

        private void Run(Activity a)
        {
            a.Running = true;
            Event(a, "start");
            if (a.Callback != null) CallCallback(a, a.Callback);
        }

        private void CallCallback(Activity a, JObj cb)
        {
            lock (random) a.Serial = (long)(random.NextDouble() * 0xffffffffL);
            var prm = (cb.Obj("params") ?? new JObj()).Copy().Put("$activity", Info(a));
            bus.Dispatch(Service, cb.Str("method"), prm.ToString(), reply => main.Post(() => CallbackReplied(a, reply)));
        }

        private void CallbackReplied(Activity a, string reply)
        {
            if (Get(a.Id) != a || !a.Running) return;
            var r = Jx.ObjOrEmpty(reply);
            if (!r.Has("returnValue") || r.Bool("returnValue")) return;
            var text = r.Str("errorText");
            if (text.StartsWith("Service exited", StringComparison.Ordinal))
            {
                // The service went away mid-call: run it again once it can, after a moment so a
                // service that dies at once doesn't spin.
                LunaLog.Write("[activities] " + a.Id + " " + a.Name + ": callback failed (" + text + "); trying again");
                a.Running = false;
                main.PostDelayed(RetryMs, () =>
                {
                    if (Get(a.Id) == a && a.Ending == null) { Event(a, "start"); a.Running = true; if (a.Callback != null) CallCallback(a, a.Callback); }
                });
                return;
            }
            LunaLog.Write("[activities] " + a.Id + " " + a.Name + ": callback failed: " + reply);
            a.Terminate = true;
            Command(a, "cancel");
        }

        /// An ending activity is done when nobody is subscribed any more; then it is armed again or forgotten.
        private void FinishIfDone(Activity a)
        {
            if (a.Ending == null || a.Subs.Count > 0 || Get(a.Id) != a) return;
            var wasRunning = a.Running;
            a.Running = false;
            if (a.Schedule != null && a.Schedule.Interval != null) a.Schedule.LastFinished = Schedule.Now();
            // A bootup requirement is met once a boot: an activity that needed it and has run is
            // over until the next start (ActivityManager.kt, measured on the reference TouchPad).
            var again = a.Callback != null && !a.Terminate && !a.Requirements.Has("bootup") &&
                (a.Restart || a.Persist || a.Explicit || (a.Schedule != null && a.Schedule.Interval != null && !a.Schedule.Past()));
            if (again)
            {
                a.Ending = null; a.Restart = false; a.Initialized = false; a.Paused = false;
                a.Parent = null; a.ReleasedParent = null; a.Adopters.Clear();
                Start(a);
            }
            else
            {
                Disarm(a);
                if (a.Requirements.Has("bootup")) { if (a.Record.Id != null) DbDel(a.Record.Id); a.Record.Id = null; }
                Forget(a);
                Activity cur;
                if (byName.TryGetValue(a.Key, out cur) && cur == a) byName.Remove(a.Key);
                waitingForEnable.Remove(a);
            }
            if (wasRunning) RunQueue();
        }

        // ---- triggers ----

        private void ArmTrigger(Activity a, JObj t)
        {
            // On the creator's behalf, so db8 grants it what it grants the creator.
            var caller = CreatorKey(a.Creator);
            if (caller.Length == 0) caller = Service;
            bool first = true;
            Bus.Call call = null;
            call = bus.Dispatch(caller, t.Str("method"), (t.Obj("params") ?? new JObj()).ToString(), reply => main.Post(() =>
            {
                if (a.TriggerCall == null || a.TriggerCall != call || a.Fired != null) return;
                var r = Jx.ObjOrEmpty(reply);
                var match = TriggerMatch.Matches(t, r, first);
                first = false;
                if (!match) return;
                a.Fired = r;
                a.TriggerCall.Cancel(); a.TriggerCall = null;
                Evaluate(a);
            }));
            a.TriggerCall = call;
        }

        // ---- schedules ----

        private void ScheduleTimer(Activity a)
        {
            var s = a.Schedule;
            if (s == null) return;
            due.Remove(a.Id);
            var at = s.NextStart;
            if (at != null) due[a.Id] = at.Value;
            if (at != null && at.Value <= Schedule.Now()) main.Post(Wakeup);
            else ArmTimer();
        }

        /// One timer, for the earliest schedule, as webOS kept one wakeup with powerd.
        private void ArmTimer()
        {
            if (timer != null) { timer.Dispose(); timer = null; }
            if (due.Count == 0) return;
            var wait = Math.Max(0, due.Values.Min() - Schedule.Now());
            timer = new Timer(_ => main.Post(Wakeup), null, (int)Math.Min(wait, int.MaxValue), Timeout.Infinite);
        }

        /// Everything that has fallen due runs, and the next one is armed: when the timer goes
        /// off, and when Lunacy comes back from suspension (Resumed).
        private void Wakeup()
        {
            var now = Schedule.Now();
            foreach (var kv in due.ToList())
            {
                if (kv.Value > now) continue;
                due.Remove(kv.Key);
                var a = Get(kv.Key);
                if (a != null && a.Schedule != null) { a.Schedule.Due = true; Evaluate(a); }
            }
            ArmTimer();
        }

        /// Lunacy came back from suspension: what fell due meanwhile runs now, once.
        public void Resumed() { main.Post(Wakeup); }

        // ---- requirements ----

        private static void SetRequirement(Activity a, string name, object v)
        {
            switch (name)
            {
                case "internet": case "wifi": case "wan": case "charging": case "docked": case "bootup": case "never":
                    // false asks for nothing, and is dropped (measured on the reference TouchPad).
                    if (IsFalse(v)) { a.Requirements.Remove(name); return; }
                    if (!(v is bool)) throw new SpecError("If an '" + name + "' requirement is specified, the only legal value is 'true'");
                    break;
                case "internetConfidence": case "wifiConfidence": case "wanConfidence":
                    if (!(v is string) || Array.IndexOf(ActivityRequirements.Confidence, (string)v) < 0) throw new SpecError("Invalid connection confidence level specified");
                    break;
                case "battery":
                    if (!(v is long) || (long)v < 0 || (long)v > 100) throw new SpecError("A \"battery\" requirement must specify a value between 0 and 100");
                    break;
                default:
                    throw new SpecError("Manager for requirement not found");
            }
            a.Requirements.Put(name, v);
        }

        private bool RequirementsMet(Activity a) { return a.Requirements.All(kv => requirements.Met(kv.Key, kv.Value)); }

        private void RequirementsChanged(Dictionary<Activity, bool> before)
        {
            foreach (var a in All())
            {
                if (a.Requirements.Count == 0) continue;
                Update(a);
                bool was;
                if (before.TryGetValue(a, out was) && !was && RequirementsMet(a)) Evaluate(a);
            }
        }

        // ---- focus ----

        private void Focus(Bus.Call call)
        {
            WithActivity(call, a =>
            {
                if (!a.Focused)
                {
                    foreach (var o in All().Where(x => x.Focused)) { o.Focused = false; Event(o, "unfocused"); }
                    a.Focused = true; Event(a, "focused");
                }
                call.Reply(Bus.Ok());
            });
        }

        private void Unfocus(Bus.Call call)
        {
            WithActivity(call, a =>
            {
                if (!a.Focused) { call.Reply(Bus.Error("Failed to unfocus Activity", 22)); return; }
                a.Focused = false; Event(a, "unfocused");
                call.Reply(Bus.Ok());
            });
        }

        private void AddFocus(Bus.Call call)
        {
            WithActivity(call, a =>
            {
                var t = P(call)["targetActivityId"];
                if (!Jx.IsNumber(t)) { call.Reply(Bus.Error("'targetActivityId' must be specified", 22)); return; }
                var target = Get((int)Jx.Num(t));
                if (target == null) { call.Reply(Bus.Error("Target Activity not found", 2)); return; }
                if (!a.Focused) { call.Reply(Bus.Error("Failed to add focus", 22)); return; }
                if (!target.Focused) { target.Focused = true; Event(target, "focused"); }
                call.Reply(Bus.Ok());
            });
        }

        // ---- events and reports ----

        private void Event(Activity a, string name, List<Sub> to = null)
        {
            foreach (var s in to ?? a.Subs.ToList())
            {
                var o = new JObj().Put("activityId", a.Id).Put("event", name).Put("returnValue", true);
                if (s.Detailed) o.Put("$activity", Info(a));
                s.Call.Reply(o.ToString());
            }
        }

        /// "update", to detailed subscribers only.
        private void Update(Activity a) { Event(a, "update", a.Subs.Where(s => s.Detailed).ToList()); }

        private JObj CurrentRequirements(Activity a)
        {
            var r = new JObj();
            foreach (var kv in a.Requirements) r.Put(kv.Key, requirements.Current(kv.Key));
            return r;
        }

        /// "$activity", as a callback and a detailed event carry it.
        private JObj Info(Activity a)
        {
            var o = new JObj().Put("activityId", a.Id);
            if (a.Callback != null) o.Put("callback", new JObj().Put("serial", a.Serial));
            if (a.Trigger != null) o.Put("trigger", (object)a.Fired ?? false);
            if (a.Requirements.Count > 0) o.Put("requirements", CurrentRequirements(a));
            o.Put("creator", a.Creator).Put("name", a.Name);
            if (a.Metadata != null) o.Put("metadata", a.Metadata);
            return o;
        }

        private string State(Activity a)
        {
            if (a.Ending != null)
            {
                if (a.Ending == "cancel") return a.Subs.Count == 0 ? "cancelled" : "cancelling";
                if (a.Ending == "stop") return a.Subs.Count == 0 ? "stopped" : "stopping";
                return "complete";
            }
            if (a.Paused) return "paused";
            if (a.Running) return "running";
            if (a.Queued) return "queued";
            if (!a.Initialized) return "init";
            if (a.Requirements.Count > 0 && !RequirementsMet(a) && (a.Trigger == null || a.Fired != null) && (a.Schedule == null || a.Schedule.Due)) return "blocked";
            return "waiting";
        }

        private JObj ToJson(Activity a, bool detail, bool subscribers, bool current, bool persisting = false)
        {
            var o = new JObj().Put("activityId", a.Id).Put("name", a.Name).Put("description", a.Description);
            if (a.Metadata != null) o.Put("metadata", a.Metadata);
            o.Put("creator", a.Creator);
            if (!persisting) o.Put("focused", a.Focused).Put("state", State(a));
            if (subscribers)
            {
                if (a.Parent != null) o.Put("parent", a.Parent.Who);
                o.Put("subscribers", new JArr(a.Subs.Select(s => (object)s.Who)));
                o.Put("adopters", new JArr(a.Adopters.Select(s => (object)s.Who)));
            }
            if (detail)
            {
                var t = new JObj().Put("bus", a.BusType);
                if (a.Persist) t.Put("persist", true); if (a.Explicit) t.Put("explicit", true);
                if (a.Continuous) t.Put("continuous", true); if (a.UserInitiated) t.Put("userInitiated", true);
                if (a.Power) t.Put("power", true); if (a.PowerDebounce) t.Put("powerDebounce", true);
                if (a.SimpleType == "foreground") t.Put("foreground", true);
                else if (a.SimpleType == "background") t.Put("background", true);
                else t.Put("immediate", a.Immediate).Put("priority", a.Priority);
                o.Put("type", t);
                if (a.Callback != null) o.Put("callback", current ? (a.Serial != 0 ? new JObj().Put("serial", a.Serial) : new JObj()) : a.Callback);
                if (a.Schedule != null) o.Put("schedule", a.Schedule.ToJson(current));
                if (a.Trigger != null) o.Put("trigger", current ? ((object)a.Fired ?? false) : a.Trigger);
                if (a.Requirements.Count > 0) o.Put("requirements", current ? CurrentRequirements(a) : a.Requirements);
            }
            return o;
        }

        private void List(Bus.Call call)
        {
            var p = P(call);
            var outp = new JArr();
            foreach (var a in All()) outp.Add(ToJson(a, p.Bool("details"), p.Bool("subscribers"), p.Bool("current")));
            call.Reply(new JObj().Put("activities", outp).Put("returnValue", true).ToString());
        }

        private void GetDetails(Bus.Call call)
        {
            WithActivity(call, a => call.Reply(new JObj().Put("activity", ToJson(a, true, true, P(call).Bool("current"))).Put("returnValue", true).ToString()));
        }

        // ---- persistence ----

        /// Saves the activity to db8, one save per record at a time: each sends the revision the
        /// last brought back (ActivityManager.kt: two at once and db8 refuses the second).
        private void Persist(Activity a, Action done = null) { PersistTo(a.Record, a, done ?? (() => { })); }

        private void PersistTo(DbRecord record, Activity a, Action done)
        {
            if (record.Saving) { record.Waiting.Enqueue(() => PersistTo(record, a, done)); return; }
            if (a.Record != record) { done(); if (record.Waiting.Count > 0) record.Waiting.Dequeue()(); return; }
            record.Saving = true;
            var rep = ToJson(a, true, false, false, true).Put("_kind", Kind);
            if (record.Id != null) rep.Put("_id", record.Id);
            if (record.Rev != null) rep.Put("_rev", record.Rev.Value);
            bus.Dispatch(Service, "palm://com.palm.db/put", new JObj().Put("objects", new JArr().Put(rep)).ToString(), reply => main.Post(() =>
            {
                var r = Jx.ObjOrEmpty(reply);
                var results = r.Arr("results");
                var res = results != null && results.Count > 0 ? results[0] as JObj : null;
                if (res != null) { record.Id = res.Str("id"); record.Rev = res.Long("rev"); }
                else LunaLog.Write("[activities] " + a.Id + " " + a.Name + ": not persisted: " + reply);
                record.Saving = false;
                done();
                if (record.Waiting.Count > 0) record.Waiting.Dequeue()();
            }));
        }

        private void DbDel(string id, Action done = null)
        {
            bus.Dispatch(Service, "palm://com.palm.db/del", new JObj().Put("ids", new JArr().Put(id)).ToString(), _ => { if (done != null) main.Post(done); });
        }

        /// Lets activities run: once the webOS root's services are on the bus (after the
        /// configurator and the JS services' names), as webOS ran none until boot had finished.
        public void Enable()
        {
            main.Post(() =>
            {
                if (started) return;
                started = true;
                LunaLog.Write("[activities] running");
                StartWaiting();
            });
        }

        private void StartWaiting()
        {
            if (!Enabled) return;
            var w = waitingForEnable.ToList();
            waitingForEnable.Clear();
            foreach (var a in w) if (Get(a.Id) == a) Start(a);
        }

        /// enable and disable: the external switch, for system callers (services) only.
        private void SwitchExternal(Bus.Call c, bool on)
        {
            if (!bus.Has(c.AppId)) { c.Reply(Bus.Error("Only callers on the private bus are allowed to enable or disable Activity dispatch", 13)); return; }
            if (external != on)
            {
                external = on;
                LunaLog.Write("[activities] dispatch " + (on ? "enabled" : "disabled") + " by " + c.AppId);
                StartWaiting();
            }
            c.Reply(Bus.Ok());
        }

        /// Reads the persisted activities back, with their ids, then answers the calls held meanwhile.
        private void Load()
        {
            var found = new List<JObj>();
            Action<string> page = null;
            page = token =>
            {
                var q = new JObj().Put("from", Kind);
                if (token != null) q.Put("page", token);
                bus.Dispatch(Service, "palm://com.palm.db/find", new JObj().Put("query", q).ToString(), reply => main.Post(() =>
                {
                    var r = Jx.ObjOrEmpty(reply);
                    var arr = r.Arr("results");
                    if (arr != null) foreach (var x in arr.OfType<JObj>()) found.Add(x);
                    var next = r.Str("next");
                    if (next.Length > 0) page(next); else Restored(found, r);
                }));
            };
            page(null);
        }

        private void Restored(List<JObj> found, JObj last)
        {
            if (last != null && !last.Bool("returnValue")) LunaLog.Write("[activities] can't read the persisted ones: " + last);
            int n = 0;
            foreach (var rec in found.OrderByDescending(x => x.Long("_rev")))
            {
                Activity a;
                try { a = Parse(rec, rec.Obj("creator") ?? new JObj()); } catch (SpecError) { a = null; }
                var id = rec.Int("activityId", 0);
                if (a == null || id <= 0 || activities.ContainsKey(id) || byName.ContainsKey(a.Key)) { DbDel(rec.Str("_id")); continue; }
                a.Id = id;
                a.Persist = true;
                a.Record.Id = rec.Str("_id"); a.Record.Rev = rec.Long("_rev");
                var lf = rec.Obj("schedule") != null ? rec.Obj("schedule").Str("lastFinished") : "";
                if (lf.Length > 0 && a.Schedule != null) { try { a.Schedule.LastFinished = Schedule.Time(lf, a.Schedule.Local); } catch (Exception) { } }
                Add(a); byName[a.Key] = a;
                waitingForEnable.Add(a);
                n++;
            }
            loaded = true;
            LunaLog.Write("[activities] " + n + " restored");
            var h = held.ToList(); held.Clear();
            foreach (var f in h) f();
            StartWaiting();
        }

        // ---- the serial queue ----

        /// Runs posted actions one at a time, in order, off the caller's thread.
        internal sealed class Serial
        {
            private readonly Queue<Action> q = new Queue<Action>();
            private bool running;

            public void Post(Action a)
            {
                lock (q)
                {
                    q.Enqueue(a);
                    if (running) return;
                    running = true;
                }
                Task.Run(() => Drain());
            }

            public void PostDelayed(int ms, Action a) { Task.Delay(ms).ContinueWith(_ => Post(a)); }

            private void Drain()
            {
                while (true)
                {
                    Action a;
                    lock (q)
                    {
                        if (q.Count == 0) { running = false; return; }
                        a = q.Dequeue();
                    }
                    try { a(); } catch (Exception e) { LunaLog.Write("[activities] threw: " + e); }
                }
            }
        }
    }

    /// An activity spec the activity manager refuses, with the text the TouchPad answers.
    internal sealed class SpecError : Exception { public SpecError(string text) : base(text) { } }

    /// An activity's schedule, as webOS's activity manager read and ran it (ActivitySchedule.kt).
    internal sealed class Schedule
    {
        public readonly JObj Spec;
        public long? Start, End, Interval;
        public bool Precise, Relative, Skip, Local;
        public long? LastFinished;
        public long? NextStart;
        public long SetAt;
        public bool Due;

        private const long DayOne = 86400000L;
        private static readonly long SmartBase = (23 * 3600L + (long)(new Random().NextDouble() * 6 * 3600)) * 1000L;
        private static readonly HashSet<long> Smart = new HashSet<long>(new long[] { 5, 10, 15, 20, 30, 60, 180, 360, 720 }.Select(m => m * 60000L));

        private Schedule(JObj spec) { Spec = spec; }

        public static long Now() { return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }

        public bool Past() { return End != null && (NextStart ?? Now()) >= End.Value; }

        /// Works out the next start and clears due, as each queueing did.
        public void Queue()
        {
            Due = false;
            var now = Now();
            SetAt = now;
            if (Interval == null) { NextStart = Start; return; }
            var iv = Interval.Value;
            // A precise interval with no start ticks from DAY_ONE in local time: the first run is
            // the next tick, never at once.
            var dayOne = DayOne - (long)TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow).TotalMilliseconds;
            long baseT = Relative ? (LastFinished ?? Start ?? dayOne) : Precise ? (Start ?? dayOne) : SmartBase;
            var next = now <= baseT ? baseT : baseT + ((now - baseT) / iv + 1) * iv;
            long? missedFrom = LastFinished ?? (Spec.Has("start") ? Start : null);
            if (!Skip && missedFrom != null && next - missedFrom.Value > iv) next = now;
            if (End != null && End.Value - next <= 0) { NextStart = null; return; }
            NextStart = next;
        }

        public JObj ToJson(bool current)
        {
            var o = new JObj();
            if (Precise) o.Put("precise", true);
            if (Relative) o.Put("relative", true);
            if (current && NextStart != null) o.Put("nextStart", Format(NextStart.Value, Local));
            if (End != null) o.Put("end", Format(End.Value, Local));
            if (Skip) o.Put("skip", true);
            if (LastFinished != null) o.Put("lastFinished", Format(LastFinished.Value, Local));
            var iv = Spec.Str("interval");
            if (iv.Length > 0) o.Put("interval", iv);
            if (current) o.Put("scheduled", Due);
            if (Local) o.Put("local", true);
            if (Start != null) o.Put("start", Format(Start.Value, Local));
            return o;
        }

        public static Schedule Parse(JObj s)
        {
            var sc = new Schedule(s) { Precise = s.Bool("precise"), Relative = s.Bool("relative"), Skip = s.Bool("skip") };
            var startText = s["start"] as string; var endText = s["end"] as string;
            var utc = new[] { startText, endText }.Where(x => x != null).Select(x => x.EndsWith("Z")).Distinct().ToList();
            if (utc.Count > 1) throw new SpecError("Start and end time must both be specified in UTC or local time");
            sc.Local = s.Bool("local") || (utc.Count == 1 && !utc[0]);
            if (startText != null) sc.Start = Time(startText, sc.Local);
            if (endText != null) sc.End = Time(endText, sc.Local);
            var iv = s["interval"] as string;
            if (iv != null)
            {
                var ms = Duration(iv);
                if (!sc.Precise)
                {
                    if (ms % 60000L != 0) throw new SpecError("Only durations of even minutes may be specified");
                    if (!Smart.Contains(ms) && ms % 86400000L != 0) throw new SpecError("Interval must be a number of days<n>d, or one of: 12h, 6h, 3h, 1h, 20m, 30m, 15m, 10m or 5m");
                    if (sc.Start != null || sc.End != null) throw new SpecError("Unless precise time is specified, time intervals may not specify a start or end time");
                    if (sc.Relative) throw new SpecError("Relative schedules must be precise");
                }
                sc.Interval = ms;
            }
            else if (sc.Start == null) throw new SpecError("Non Interval Schedules must specify a start time");
            var lf = s["lastFinished"] as string;
            if (lf != null)
            {
                var t = Time(lf, sc.Local);
                if (t < Now() && (sc.Start == null || t > sc.Start.Value)) sc.LastFinished = t;
            }
            return sc;
        }


        public static string Format(long t, bool local)
        {
            var d = DateTimeOffset.FromUnixTimeMilliseconds(t);
            return local ? d.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                         : d.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "Z";
        }

        public static long Time(string s, bool local)
        {
            var z = s.EndsWith("Z");
            if (!z && s.Length > 0 && !char.IsDigit(s[s.Length - 1])) throw new SpecError("Start time must end in 'Z' for UTC, or nothing");
            DateTime d;
            if (!DateTime.TryParseExact(z ? s.Substring(0, s.Length - 1) : s, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                    !z && local ? DateTimeStyles.AssumeLocal : DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out d))
                throw new SpecError("Failed to parse start time");
            return new DateTimeOffset(d.ToUniversalTime()).ToUnixTimeMilliseconds();
        }

        public static long Duration(string s)
        {
            var m = Regex.Match(s.Trim(), "^(?:(\\d+)D)?(?:(\\d+)H)?(?:(\\d+)M)?(?:(\\d+)S)?$", RegexOptions.IgnoreCase);
            if (!m.Success) throw new SpecError("Failed to parse scheduling interval");
            Func<int, long> g = i => m.Groups[i].Success ? long.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture) : 0;
            var ms = (g(1) * 86400 + g(2) * 3600 + g(3) * 60 + g(4)) * 1000;
            if (ms == 0) throw new SpecError("Duration must be non-zero");
            return ms;
        }
    }

    /// Whether an activity's trigger fires on a reply (TriggerMatch.kt).
    internal static class TriggerMatch
    {
        public static bool Matches(JObj t, JObj r, bool first)
        {
            bool isBasic = !t.Has("key") && !t.Has("where") && !t.Has("compare");
            if (r.Has("returnValue") && !r.Bool("returnValue")) return true;   // an error fires it, with the error
            if (t.Has("key")) return r.Has(t.Str("key"));
            if (t.Has("compare")) { var c = t.Obj("compare"); return r.Has(c.Str("key")) && !Same(r[c.Str("key")], c["value"]); }
            if (t.Has("where")) return Where(r, t["where"]);
            return isBasic && !first;
        }

        public static bool Same(object x, object y)
        {
            if (x == null || y == null) return x == y;
            if (x is long && y is long) return (long)x == (long)y;
            if (x is double && y is double) return (double)x == (double)y;
            if (x is string || x is bool) return x.Equals(y);
            if (x is JNull) return y is JNull;
            return ReferenceEquals(x, y);
        }

        public static bool Where(JObj o, object w)
        {
            var arr = w as JArr;
            if (arr != null) return arr.All(x => Where(o, x));
            var wo = w as JObj;
            if (wo == null) return false;
            if (wo.Has("and")) return Where(o, wo["and"]);
            if (wo.Has("or")) { var or = wo["or"]; var oa = or as JArr; return oa != null ? oa.Any(x => Where(o, x)) : Where(o, or); }
            object v;
            if (!Path(o, wo["prop"], out v)) return false;
            return wo.Str("op") == "where" || CompareOp(v, wo.Str("op"), wo["val"]);
        }

        private static bool Path(JObj o, object prop, out object v)
        {
            v = null;
            IEnumerable<string> parts;
            if (prop is string) parts = ((string)prop).Split('.');
            else if (prop is JArr) parts = ((JArr)prop).Select(x => x as string ?? "");
            else return false;
            object cur = o;
            foreach (var p in parts)
            {
                var co = cur as JObj;
                if (co == null || !co.Has(p) || co[p] is JNull) return false;
                cur = co[p];
            }
            v = cur;
            return true;
        }

        /// MojObject's order: different types order by type, so int 1 and decimal 1.0 differ.
        private static bool CompareOp(object a, string op, object b)
        {
            Func<object, int> rank = v => v == null || v is JNull ? 1 : v is JObj ? 2 : v is JArr ? 3 : v is string ? 4 : v is bool ? 5 : v is double ? 6 : v is long ? 7 : 0;
            int c;
            if (rank(a) != rank(b)) c = rank(a).CompareTo(rank(b));
            else if (a is string) c = string.CompareOrdinal((string)a, (string)b);
            else if (a is bool) c = ((bool)a).CompareTo((bool)b);
            else if (Jx.IsNumber(a)) c = Jx.Num(a).CompareTo(Jx.Num(b));
            else c = (a ?? "").ToString() == (b ?? "").ToString() ? 0 : 1;
            switch (op)
            {
                case "<": return c < 0;
                case "<=": return c <= 0;
                case "=": return c == 0;
                case "!=": return c != 0;
                case ">=": return c >= 0;
                case ">": return c > 0;
                default: return false;
            }
        }
    }

    /// What requirements are measured against (ActivityRequirements.kt): the connection
    /// manager's status, and the phone's battery through Windows' aggregate battery.
    internal sealed class ActivityRequirements
    {
        public static readonly string[] Confidence = { "none", "poor", "fair", "excellent" };

        private readonly Bus bus;
        private readonly string service;
        private readonly ActivityManager.Serial main;
        private readonly Action<Action> onChange;
        private JObj netStatus;
        private int batteryPercent = -1;
        private string charger;
        private Bus.Call networkWatch;

        public ActivityRequirements(Bus bus, string service, ActivityManager.Serial main, Action<Action> onChange)
        {
            this.bus = bus; this.service = service; this.main = main; this.onChange = onChange;
        }

        private static int Conf(JObj o)
        {
            if (o == null || o.Str("state") != "connected" || o.Str("onInternet") != "yes") return -1;
            return Array.IndexOf(Confidence, o.Str("networkConfidenceLevel"));
        }

        public bool Met(string name, object v)
        {
            var s = netStatus;
            var wifi = s != null ? s.Obj("wifi") : null; var wan = s != null ? s.Obj("wan") : null;
            switch (name)
            {
                case "internet": return s != null && s.Bool("isInternetConnectionAvailable");
                case "wifi": return wifi != null && wifi.Str("state") == "connected" && wifi.Str("onInternet") == "yes";
                case "wan": return wan != null && wan.Str("state") == "connected" && wan.Str("network") != "unusable" && wan.Str("onInternet") == "yes";
                case "internetConfidence": { var c = Math.Max(Conf(wifi), Conf(wan)); return c >= 0 && c >= Array.IndexOf(Confidence, v as string); }
                case "wifiConfidence": { var c = Conf(wifi); return c >= 0 && c >= Array.IndexOf(Confidence, v as string); }
                case "wanConfidence": { var c = Conf(wan); return c >= 0 && c >= Array.IndexOf(Confidence, v as string); }
                case "charging": return charger != null;
                case "docked": return charger == "puck";
                case "battery": return batteryPercent >= (long)v;
                case "bootup": return true;
                default: return false;
            }
        }

        public object Current(string name)
        {
            var s = netStatus;
            Func<int, string> level = c => c < 0 ? "unknown" : Confidence[c];
            switch (name)
            {
                case "internet": return (object)s ?? false;
                case "wifi": return (object)(s != null ? s.Obj("wifi") : null) ?? false;
                case "wan": return (object)(s != null ? s.Obj("wan") : null) ?? false;
                case "internetConfidence": return level(Math.Max(Conf(s != null ? s.Obj("wifi") : null), Conf(s != null ? s.Obj("wan") : null)));
                case "wifiConfidence": return level(Conf(s != null ? s.Obj("wifi") : null));
                case "wanConfidence": return level(Conf(s != null ? s.Obj("wan") : null));
                case "battery": return (long)batteryPercent;
                default: return Met(name, true);
            }
        }

        public void Watch()
        {
            networkWatch = bus.Dispatch(service, "palm://com.palm.connectionmanager/getstatus", "{\"subscribe\":true}", reply => main.Post(() =>
            {
                JObj r;
                try { r = Jx.ParseObj(reply); } catch (FormatException) { return; }
                onChange(() => netStatus = r);
            }));
            try
            {
                var b = Windows.Devices.Power.Battery.AggregateBattery;
                Action read = () =>
                {
                    var rep = b.GetReport();
                    int pct = -1;
                    if (rep.FullChargeCapacityInMilliwattHours.HasValue && rep.RemainingCapacityInMilliwattHours.HasValue && rep.FullChargeCapacityInMilliwattHours.Value > 0)
                        pct = (int)Math.Round(100.0 * rep.RemainingCapacityInMilliwattHours.Value / rep.FullChargeCapacityInMilliwattHours.Value);
                    // Charging, or idle on the charger when full; webOS's chargers were "usb",
                    // "wall" and "puck" (the Touchstone), and Windows doesn't say which.
                    var plugged = rep.Status == Windows.System.Power.BatteryStatus.Charging || rep.Status == Windows.System.Power.BatteryStatus.Idle ? "wall" : null;
                    main.Post(() => onChange(() => { batteryPercent = pct; charger = plugged; }));
                };
                b.ReportUpdated += (s, e) => read();
                read();
            }
            catch (Exception e) { LunaLog.Write("[activities] no battery to watch: " + e.Message); }
        }
    }
}
