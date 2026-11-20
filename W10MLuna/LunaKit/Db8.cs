using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LunaKit
{
    /// db8: palm://com.palm.db, and palm://com.palm.tempdb (the same API, in memory), on SQLite:
    /// AndroidLuna's Db8.kt in C#. Replies, errors, ids, revisions, paging and watches follow
    /// the reference TouchPad's (webOS CE 3.1.0; Docs/db8.md). Kinds hold objects; a query must
    /// be served by one of its kind's indexes, as on webOS. Work runs on one thread, in order;
    /// objects of a kind are cached in memory once read.
    ///
    /// Mirrored kinds (MirrorKind: the media and file indexes, from the phone's libraries),
    /// account syncs (SyncAccount: Windows' contacts and appointments) and the file picker's
    /// relay (RegisterPickerRelay) are Db8.kt's.
    internal sealed class Db8
    {
        private readonly string service;
        private readonly IntPtr db;
        private readonly BlockingCollection<Action> queue = new BlockingCollection<Action>();
        private readonly Task worker;
        private readonly Dictionary<string, Kind> kinds = new Dictionary<string, Kind>();
        private readonly List<string> kindOrder = new List<string>();
        private readonly Dictionary<string, Dictionary<string, JObj>> cache = new Dictionary<string, Dictionary<string, JObj>>();
        private readonly List<Watch> watches = new List<Watch>();
        private long rev, idCounter;
        private Bus bus;

        private sealed class IndexProp
        {
            public string Name, Collate; public object Default; public List<string> Include;
        }
        private sealed class Index { public string Name; public List<IndexProp> Props; }
        /// A revision set: Name holds the _rev at which any of Props last changed.
        private sealed class RevSet { public string Name; public List<string> Props; }
        private sealed class Kind
        {
            public string Id, Owner; public List<string> Extends; public List<Index> Indexes; public List<RevSet> RevSets;
        }
        private sealed class Clause { public string Prop, Op; public object Value; }
        private sealed class Watch { public Bus.Call Call; public HashSet<string> Kinds; public List<Clause> Where; }
        /// A reply with its db8 error, thrown from deep inside an operation.
        private sealed class DbError : Exception
        {
            public readonly int Code;
            public DbError(int code, string text) : base(text) { Code = code; }
        }

        /// `file`: the database's path, or null for one in memory (tempdb).
        public Db8(string service, string file)
        {
            this.service = service;
            int rc = Sqlite.Open(file ?? ":memory:", out db);
            if (rc != Sqlite.OK) throw new InvalidOperationException(service + ": can't open " + file + ": " + rc);
            Run("PRAGMA journal_mode=WAL");
            Run("CREATE TABLE IF NOT EXISTS kinds (id TEXT PRIMARY KEY, spec TEXT)");
            Run("CREATE TABLE IF NOT EXISTS objects (id TEXT PRIMARY KEY, kind TEXT, json TEXT)");
            Run("CREATE INDEX IF NOT EXISTS objects_kind ON objects (kind)");
            Run("CREATE TABLE IF NOT EXISTS meta (k TEXT PRIMARY KEY, v INTEGER)");
            Run("CREATE TABLE IF NOT EXISTS permissions (kind TEXT, caller TEXT, ops TEXT, PRIMARY KEY (kind, caller))");
            // db8's RevTimestamp records: which revision the database was at, at each purge.
            Run("CREATE TABLE IF NOT EXISTS revtimes (rev INTEGER PRIMARY KEY, ts INTEGER)");
            foreach (var r in Rows("SELECT spec FROM kinds"))
            {
                try { var k = ParseKind(Jx.ParseObj((string)r[0])); AddKind(k); }
                catch (Exception e) { LunaLog.Write("[db8] " + service + ": stored kind unreadable: " + e.Message); }
            }
            rev = Meta("rev"); idCounter = Meta("ids");
            worker = Task.Factory.StartNew(Loop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        private int dbThread = -1;

        private void Loop()
        {
            dbThread = Environment.CurrentManagedThreadId;
            foreach (var a in queue.GetConsumingEnumerable())
            {
                try { a(); }
                catch (Exception e) { LunaLog.Write("[db8] " + service + ": " + e); }
            }
        }

        /// On db8's thread, in order.
        private void Work(Action f) { try { queue.Add(f); } catch (InvalidOperationException) { } }

        public void Register(Bus bus)
        {
            this.bus = bus;
            foreach (var m in new[] { "putKind", "delKind", "put", "get", "merge", "del", "find", "search", "watch", "reserveIds", "batch", "putPermissions", "purge", "purgeStatus" })
            {
                var method = m;
                bus.Register(service, method, c => Work(() => Handle(method, c)));
            }
            bus.Register(service, "internal/scheduledPurge", ScheduledPurge);
        }

        /// Registers a package's configuration/db files, as webOS's configurator did at install.
        public void Configure(List<JObj> kindFiles, List<JArr> permissionFiles)
        {
            Work(() =>
            {
                foreach (var k in kindFiles)
                {
                    try { PutKind(Configurator, k); }
                    catch (Exception e) { LunaLog.Write("[db8] " + service + ": kind " + k.Str("id") + ": " + e.Message); }
                }
                foreach (var p in permissionFiles)
                {
                    try { PutPermissions(Configurator, new JObj().Put("permissions", p)); }
                    catch (Exception e) { LunaLog.Write("[db8] " + service + ": permissions: " + e.Message); }
                }
            });
        }

        /// `f` once every operation already asked of this database is done.
        public void After(Action f) { Work(f); }

        /// Removes every object of `kind` outright (no tombstones), firing watches: for a
        /// mirrored kind whose records are to be made again from its source.
        public void PurgeKind(string kind)
        {
            Work(() =>
            {
                try
                {
                    var all = Objects(kind).Values.ToList();
                    Transaction(() => { foreach (var o in all) DelOne(o, true); });
                    LunaLog.Write("[db8] " + service + ": " + kind + " purged (" + all.Count + " objects)");
                }
                catch (Exception e) { LunaLog.Write("[db8] " + service + ": purge " + kind + ": " + e.Message); }
            });
        }

        /// An account's copy of something kept elsewhere (Db8.kt's syncAccount): `records`, each
        /// with its remoteId, become `kind`'s objects for the account. A new one is created, a
        /// changed one has the given properties merged in, and one no longer given is deleted
        /// (a tombstone, which the contacts linker needs to let a person go). One transaction;
        /// `done(added, changed, removed)` on db8's thread.
        public void SyncAccount(string kind, string accountId, List<JObj> records, Action<int, int, int> done = null)
        {
            Work(() =>
            {
                try
                {
                    var have = new Dictionary<string, JObj>();
                    foreach (var o in Objects(kind).Values)
                        if (!o.Bool("_del") && o.Str("accountId") == accountId) have[o.Str("remoteId")] = o;
                    var wanted = new HashSet<string>(records.Select(r => r.Str("remoteId")));
                    var gone = have.Where(kv => !wanted.Contains(kv.Key)).Select(kv => kv.Value).ToList();
                    int added = 0, updated = 0;
                    Transaction(() =>
                    {
                        foreach (var old in gone) DelOne(old, false);
                        foreach (var r in records)
                        {
                            r.Put("accountId", accountId);
                            JObj old;
                            if (!have.TryGetValue(r.Str("remoteId"), out old))
                            {
                                var o = r.Copy().Put("_kind", kind).Put("_rev", NextRev());
                                if (o.Str("_id").Length == 0) o.Put("_id", NewId());
                                // An _id this account gave before, on an object deleted since: the new one takes its place.
                                var dead = ById(o.Str("_id"));
                                Write(dead, o);
                                added++;
                            }
                            else if (!r.Keys.All(k => old.Has(k) && Identical(old[k], r[k])))
                            {
                                var o = old.Copy();
                                foreach (var k in r.Keys) o.Put(k, Jx.Clone(r[k]));
                                o.Put("_rev", NextRev());
                                Write(old, o);
                                updated++;
                            }
                        }
                    });
                    if (done != null) done(added, updated, gone.Count);
                }
                catch (Exception e)
                {
                    LunaLog.Write("[db8] " + service + ": syncAccount " + kind + " for account " + accountId + " failed and was rolled back: " + e.Message);
                }
            });
        }

        // ---- mirrors (Db8.kt's mirror) ----

        /// A record's version as its source gives it (a file's size and time): when it differs
        /// from the last pass's, the file was rewritten and its record is replaced.
        public const string Stamp = "\u0000stamp";
        private const int MirrorMs = 2000, MirrorWatchMs = 5000;
        private static readonly HashSet<string> MirroredOps = new HashSet<string> { "find", "search", "watch", "get", "merge", "del" };
        private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();

        private sealed class Mirror
        {
            public Func<List<JObj>> Source;
            public HashSet<string> Initial;
            public Func<JObj, bool> Owns;
            public Action Added;
            public Func<JObj, string> Key;
            public bool Refreshing;
            public long Synced;
            public Dictionary<string, string> Stamps = new Dictionary<string, string>();
        }
        private readonly Dictionary<string, Mirror> mirrors = new Dictionary<string, Mirror>();
        // Where mirrors read their sources (a walk of the libraries, a picture's metadata): a
        // thread of their own, so db8 keeps answering from what is already indexed meanwhile.
        private readonly BlockingCollection<Action> mirrorQueue = new BlockingCollection<Action>();
        private Task mirrorWorker;
        private Timer mirrorTicker;

        /// A kind whose truth is elsewhere (the files of the phone's libraries, for the media and
        /// file indexes): db8 registers it, and when something queries it, brings its records
        /// into line with `source`, at most every two seconds; while a watch is open on it, every
        /// five. Each record is matched to its source by `path` (or `key`): a new one is created,
        /// a changed one takes the source's properties (keeping anything an app added), one whose
        /// source has gone is purged, each firing watches as any write does. `initial` names
        /// properties the source sets only on a new record, which others then own; `owns` picks
        /// the records the source speaks for; `added` runs after a pass that made new records.
        public void MirrorKind(JObj spec, JArr permissions, Func<List<JObj>> source, IEnumerable<string> initial = null,
            Func<JObj, bool> owns = null, Action added = null, Func<JObj, string> key = null)
        {
            Work(() =>
            {
                try
                {
                    PutKind(Configurator, spec);
                    PutPermissions(Configurator, new JObj().Put("permissions", permissions));
                }
                catch (Exception e) { LunaLog.Write("[db8] " + service + ": mirror " + spec.Str("id") + ": " + e.Message); return; }
                if (mirrorWorker == null)
                {
                    mirrorWorker = Task.Factory.StartNew(() =>
                    {
                        foreach (var a in mirrorQueue.GetConsumingEnumerable())
                        {
                            try { a(); } catch (Exception e) { LunaLog.Write("[db8] " + service + " mirrors: " + e); }
                        }
                    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                }
                var m = new Mirror
                {
                    Source = source,
                    Initial = new HashSet<string>(initial ?? Enumerable.Empty<string>()),
                    Owns = owns ?? (o => true),
                    Added = added,
                    Key = key ?? (o => o.Str("path"))
                };
                mirrors[spec.Str("id")] = m;
                // A first pass at once, as filenotifyd indexed at boot.
                RefreshMirror(spec.Str("id"), m);
                if (mirrorTicker == null)
                    mirrorTicker = new Timer(_ => Work(() =>
                    {
                        foreach (var kv in mirrors.ToList()) if (watches.Any(w => w.Kinds.Contains(kv.Key))) RefreshMirror(kv.Key, kv.Value);
                    }), null, MirrorWatchMs, MirrorWatchMs);
            });
        }

        /// A pass of `kind`'s mirror unless one is running or the last ended under two seconds
        /// ago; on db8's thread. The source is read on the mirror thread, the difference written here.
        private void RefreshMirror(string kind, Mirror m)
        {
            if (m.Refreshing || (m.Synced != 0 && Clock.ElapsedMilliseconds - m.Synced < MirrorMs)) return;
            m.Refreshing = true;
            try
            {
                mirrorQueue.Add(() =>
                {
                    List<JObj> files;
                    try { files = m.Source(); }
                    catch (Exception e) { LunaLog.Write("[db8] " + service + ": mirror " + kind + ": " + e.Message); files = null; }
                    Work(() =>
                    {
                        m.Refreshing = false;
                        m.Synced = Clock.ElapsedMilliseconds;
                        if (files != null) ApplyMirror(kind, m, files);
                    });
                });
            }
            catch (InvalidOperationException) { m.Refreshing = false; }
        }

        /// A pass's result: `kind`'s records brought into line with what its source read. On db8's thread.
        private void ApplyMirror(string kind, Mirror m, List<JObj> files)
        {
            var byPath = new Dictionary<string, JObj>();
            var order = new List<string>();
            var stamps = new Dictionary<string, string>();
            foreach (var f in files)
            {
                var k = m.Key(f);
                if (!byPath.ContainsKey(k)) order.Add(k);
                byPath[k] = f;
                var st = f[Stamp] as string;
                if (st != null) { stamps[k] = st; f.Remove(Stamp); }
            }
            var current = Objects(kind).Values.Where(o => !o.Bool("_del") && m.Owns(o)).ToList();
            var rewritten = new List<JObj>();
            bool any = false;
            Transaction(() =>
            {
                foreach (var o in current)
                {
                    var path = m.Key(o);
                    JObj f;
                    if (!byPath.TryGetValue(path, out f)) { DelOne(o, true); continue; }
                    byPath.Remove(path);
                    string was, now;
                    if (m.Stamps.TryGetValue(path, out was) && stamps.TryGetValue(path, out now) && was != now) { DelOne(o, true); rewritten.Add(f); continue; }
                    foreach (var k in m.Initial) f.Remove(k);
                    if (f.Keys.Any(k => !Same(o[k], f[k]))) MergeOne(o, f);
                }
                foreach (var f in order.Where(byPath.ContainsKey).Select(k => byPath[k]).Concat(rewritten))
                {
                    var o = f.Copy().Put("_kind", kind).Put("_id", NewId()).Put("_rev", NextRev());
                    Write(null, o);
                    any = true;
                }
            });
            m.Stamps = stamps;
            if (any && m.Added != null) m.Added();
        }

        /// Equal as a source says it: the _ids db8 gave objects inside a record aren't the source's.
        private static bool Same(object a, object b)
        {
            var ao = a as JObj; var bo = b as JObj;
            if (ao != null && bo != null)
            {
                var ka = ao.Keys.Where(k => k != "_id").ToList();
                var kb = new HashSet<string>(bo.Keys.Where(k => k != "_id"));
                return ka.Count == kb.Count && ka.All(k => kb.Contains(k) && Same(ao[k], bo[k]));
            }
            var aa = a as JArr; var ba = b as JArr;
            if (aa != null && ba != null)
            {
                if (aa.Count != ba.Count) return false;
                for (int i = 0; i < aa.Count; i++) if (!Same(aa[i], ba[i])) return false;
                return true;
            }
            if (Jx.IsNumber(a) && Jx.IsNumber(b)) return Jx.Num(a) == Jx.Num(b);
            if ((a == null || a is JNull) && (b == null || b is JNull)) return true;
            return Equals(a, b);
        }

        /// For a mirror's source, on the mirror thread: the mirrored `kind` brought into line now,
        /// and its records' ids by path (an image's albumId is its folder's album, which so
        /// exists before its first image is written).
        public Dictionary<string, string> MirrorIds(string kind)
        {
            Mirror m = null;
            List<JObj> files = null;
            OnDbThread(() => { mirrors.TryGetValue(kind, out m); return 0; });
            if (m != null) { try { files = m.Source(); } catch (Exception) { files = null; } }
            return OnDbThread(() =>
            {
                if (m != null && files != null) { m.Synced = Clock.ElapsedMilliseconds; ApplyMirror(kind, m, files); }
                var ids = new Dictionary<string, string>();
                foreach (var o in Objects(kind).Values) if (!o.Bool("_del")) ids[o.Str("path")] = o.Str("_id");
                return ids;
            });
        }

        /// `f` on db8's thread, waited for.
        private T OnDbThread<T>(Func<T> f)
        {
            if (Environment.CurrentManagedThreadId == dbThread) return f();
            var done = new ManualResetEventSlim(false);
            T result = default(T);
            Exception err = null;
            Work(() => { try { result = f(); } catch (Exception e) { err = e; } finally { done.Set(); } });
            done.Wait();
            if (err != null) throw err;
            return result;
        }

        /// What an operation could read or change of a mirrored kind: the one it names and every
        /// kind that extends it. A call by _id names no kind and reads what is there.
        private void SyncMirrorsFor(string method, Bus.Call call)
        {
            if (mirrors.Count == 0 || !MirroredOps.Contains(method)) return;
            var q = Jx.ObjOrEmpty(call.Raw).Obj("query");
            var from = q == null ? null : q.Str("from");
            if (string.IsNullOrEmpty(from)) return;
            List<string> fam;
            try { fam = Family(from); } catch (Exception) { return; }
            foreach (var k in fam) { Mirror m; if (mirrors.TryGetValue(k, out m)) RefreshMirror(k, m); }
        }

        /// What the file picker reads (RegisterPickerRelay).
        private static readonly HashSet<string> PickerKinds = new HashSet<string> { "com.palm.media.image.album:1", "com.palm.media.image.file:1",
            "com.palm.media.video.file:1", "com.palm.media.types:1", "com.palm.media.audio.file:1", "com.palm.media.misc.file:1" };

        /// webOS's file picker read the media index as the system UI (com.palm.systemui, a consumer
        /// of every media kind). Lunacy's picker page runs as the app that opened it, which may not,
        /// so it reads through palm://org.webosarchive.lunacy/media/find and media/search: the same
        /// queries, watches included, answered as the system UI, for the media kinds only (Db8.kt).
        public void RegisterPickerRelay(Bus bus)
        {
            foreach (var m in new[] { "find", "search" })
            {
                var method = m;
                bus.Register("org.webosarchive.lunacy", "media/" + method, c =>
                {
                    var q = Jx.ObjOrEmpty(c.Raw).Obj("query");
                    var from = q == null ? "" : q.Str("from");
                    if (!PickerKinds.Contains(from)) { c.Reply(Error(-3963, "db: permission denied")); return; }
                    Work(() => Handle(method, c, "com.palm.systemui"));
                });
            }
        }

        private void Handle(string method, Bus.Call call, string caller = null)
        {
            try { SyncMirrorsFor(method, call); } catch (Exception) { }
            string reply;
            try
            {
                reply = Exec(method, caller ?? call.AppId, Jx.ObjOrEmpty(call.Params.Stringify()), call);
            }
            catch (DbError e) { reply = Error(e.Code, e.Message); }
            catch (Exception e)
            {
                LunaLog.Write("[db8] " + service + "/" + method + " failed: " + e);
                reply = Error(-1, "db: internal error: " + e.Message);
            }
            call.Reply(reply);
            // A watch that fires at once is over, like one that fires later (Changed).
            if (method == "watch" && reply == Fired) call.Cancel();
        }

        /// One operation. call is null inside a batch (no watches there).
        private string Exec(string method, string caller, JObj p, Bus.Call call)
        {
            switch (method)
            {
                case "putKind": return PutKind(caller, p);
                case "delKind": return DelKind(caller, p);
                case "put": return Put(caller, p);
                case "get": return Get(caller, p);
                case "merge": return Merge(caller, p);
                case "del": return Del(caller, p);
                case "find": return Find(caller, p, call, false);
                case "search": return Find(caller, p, call, true);
                case "watch": return WatchOp(caller, p, call);
                case "reserveIds": return ReserveIds(caller, p);
                case "batch": return Batch(caller, p);
                case "putPermissions": return PutPermissions(caller, p);
                case "purge":
                    {
                        if (!Admins.Contains(caller)) throw new DbError(-3963, "db: permission denied");
                        var w = p["window"];
                        if (w != null && !(w is long)) throw new DbError(22, "invalid parameters: caller='" + caller + "' error='invalid type for property 'window''");
                        return new JObj().Put("returnValue", true).Put("count", PurgeDeleted(w is long ? (int)(long)w : PurgeWindowDays)).ToString();
                    }
                case "purgeStatus": return new JObj().Put("returnValue", true).Put("rev", LastPurgedRev()).ToString();
                default: return Error(-1, "Unknown method \"" + method + "\" for category \"/\"");
            }
        }

        // ---- kinds ----

        private string PutKind(string caller, JObj p)
        {
            var id = Typed<string>(caller, p, "id");
            var owner = p.Has("owner") ? p.Str("owner") : caller;
            Kind old;
            kinds.TryGetValue(id, out old);
            if (caller != Configurator && (!Related(caller, owner) || (old != null && !Related(caller, old.Owner)))) throw new DbError(-3963, "db: permission denied");
            var k = ParseKind(p);
            Run("INSERT OR REPLACE INTO kinds (id, spec) VALUES (?, ?)", id, p.ToString());
            AddKind(k);
            return Ok();
        }

        private void AddKind(Kind k)
        {
            if (!kinds.ContainsKey(k.Id)) kindOrder.Add(k.Id);
            kinds[k.Id] = k;
        }

        private string DelKind(string caller, JObj p)
        {
            var id = Typed<string>(caller, p, "id");
            var k = KindOf(id);
            if (!Related(caller, k.Owner)) throw new DbError(-3963, "db: permission denied");
            Run("DELETE FROM objects WHERE kind = ?", id);
            Run("DELETE FROM kinds WHERE id = ?", id);
            kinds.Remove(id); kindOrder.Remove(id); cache.Remove(id);
            return Ok();
        }

        private static Kind ParseKind(JObj p)
        {
            // Every kind has db8's own _id index (MojDbKind::IdIndexJson), which includes deleted
            // objects: Email's facade finds an email by "_id" = its id, with a watch.
            var indexes = new List<Index>
            {
                new Index { Name = "_id", Props = new List<IndexProp> { new IndexProp { Name = "_del" }, new IndexProp { Name = "_id" } } }
            };
            var a = p.Arr("indexes");
            if (a != null)
            {
                foreach (var e in a)
                {
                    // A trailing comma (Calendar's event kind ends its list with one) is dropped
                    // by the parser, as webOS's took the list.
                    var ix = e as JObj;
                    if (ix == null) continue;
                    var props = new List<IndexProp>();
                    var pa = ix.Arr("props");
                    var first = pa != null && pa.Count > 0 && pa[0] is JObj ? ((JObj)pa[0]).Str("name") : null;
                    // An index that includes deleted objects leads with _del, as db8's did, unless
                    // it names _del first itself (the message kinds' "deleted": _del, _rev).
                    if (ix.Bool("incDel") && first != "_del") props.Add(new IndexProp { Name = "_del" });
                    if (pa != null)
                    {
                        foreach (var pe in pa)
                        {
                            var po = pe as JObj;
                            if (po == null) continue;
                            List<string> include = null;
                            if (po.Str("type") == "multi" && po.Arr("include") != null)
                                include = po.Arr("include").OfType<JObj>().Select(x => x.Str("name")).Where(x => x.Length > 0).ToList();
                            props.Add(new IndexProp
                            {
                                Name = po.Str("name"),
                                Collate = po.Str("collate").Length > 0 ? po.Str("collate") : null,
                                Default = po["default"],
                                Include = include
                            });
                        }
                    }
                    indexes.Add(new Index { Name = ix.Str("name"), Props = props });
                }
            }
            var ext = new List<string>();
            var ea = p.Arr("extends");
            if (ea != null) foreach (var e in ea) if (e is string) ext.Add((string)e);
            var revSets = new List<RevSet>();
            var ra = p.Arr("revSets");
            if (ra != null)
            {
                foreach (var e in ra.OfType<JObj>())
                {
                    var props = e.Arr("props") == null ? new List<string>() : e.Arr("props").OfType<JObj>().Select(x => x.Str("name")).ToList();
                    revSets.Add(new RevSet { Name = e.Str("name"), Props = props });
                }
            }
            return new Kind { Id = p.Str("id"), Owner = p.Str("owner"), Extends = ext, Indexes = indexes, RevSets = revSets };
        }

        private Kind KindOf(string id)
        {
            Kind k;
            if (id != null && kinds.TryGetValue(id, out k)) return k;
            throw new DbError(-3970, "kind not registered: '" + id + "'");
        }

        /// The kind and every kind that extends it, directly or not.
        private List<string> Family(string id)
        {
            var out_ = new List<string> { id };
            var set = new HashSet<string> { id };
            bool grew = true;
            while (grew)
            {
                grew = false;
                foreach (var kid in kindOrder)
                {
                    var k = kinds[kid];
                    if (!set.Contains(k.Id) && k.Extends.Any(set.Contains)) { set.Add(k.Id); out_.Add(k.Id); grew = true; }
                }
            }
            return out_;
        }

        /// Who may use a kind: its owner, callers it has granted (putPermissions), and callers in
        /// the same package by name (an app and its service, com.foo.app and com.foo.app.service).
        private static bool Related(string caller, string owner)
        {
            return caller == owner || caller.StartsWith(owner + ".", StringComparison.Ordinal) || owner.StartsWith(caller + ".", StringComparison.Ordinal);
        }

        private bool Allowed(string caller, Kind k, string op)
        {
            if (Related(caller, k.Owner)) return true;
            return Permission(caller, k, op, new HashSet<string>()) == "allow";
        }

        /// db8's permission engine (MojDbPermissionEngine::check, MojDbKind::objectPermission):
        /// the caller's own entry for the kind, else the longest wildcard entry that matches it,
        /// gives the operation's value; a kind with no value for it takes its first super kind's.
        private string Permission(string caller, Kind k, string op, HashSet<string> seen)
        {
            if (!seen.Add(k.Id)) return null;
            string exact = null, wild = null;
            int wildLength = -1;
            foreach (var r in Rows("SELECT caller, ops FROM permissions WHERE kind = ?", k.Id))
            {
                var who = (string)r[0];
                var ops = r[1] as string ?? "{}";
                if (who == caller) exact = ops;
                else if (who.Length > wildLength && who.EndsWith("*", StringComparison.Ordinal) && caller.StartsWith(who.Substring(0, who.Length - 1), StringComparison.Ordinal))
                {
                    wild = ops; wildLength = who.Length;
                }
            }
            var text = exact ?? wild;
            string value = null;
            if (text != null) { var v = Jx.ObjOrEmpty(text).Str(op); if (v.Length > 0) value = v; }
            if (value != null) return value;
            Kind super_;
            return k.Extends.Count > 0 && kinds.TryGetValue(k.Extends[0], out super_) ? Permission(caller, super_, op, seen) : null;
        }

        private void CheckAccess(string caller, string kindId, string op = "read")
        {
            if (!Allowed(caller, KindOf(kindId), op)) throw new DbError(-3963, "db: permission denied");
        }

        private string PutPermissions(string caller, JObj p)
        {
            var perms = Typed<JArr>(caller, p, "permissions");
            foreach (var e in perms)
            {
                var pm = e as JObj;
                if (pm == null) continue;
                var k = KindOf(pm.Str("object"));
                if (caller != Configurator && !Related(caller, k.Owner)) throw new DbError(-3963, "db: permission denied");
                Run("INSERT OR REPLACE INTO permissions (kind, caller, ops) VALUES (?, ?, ?)", k.Id, pm.Str("caller"), (pm.Obj("operations") ?? new JObj()).ToString());
            }
            return Ok();
        }

        // ---- objects ----

        private Dictionary<string, JObj> Objects(string kind)
        {
            Dictionary<string, JObj> m;
            if (cache.TryGetValue(kind, out m)) return m;
            m = new Dictionary<string, JObj>();
            foreach (var r in Rows("SELECT id, json FROM objects WHERE kind = ? ORDER BY id", kind))
            {
                try { m[(string)r[0]] = Jx.ParseObj((string)r[1]); }
                catch (FormatException e) { LunaLog.Write("[db8] " + service + ": object " + r[0] + " unreadable: " + e.Message); }
            }
            cache[kind] = m;
            return m;
        }

        /// The object's kind is looked up first, so only that kind's objects are loaded.
        private JObj ById(string id)
        {
            foreach (var m in cache.Values) { JObj o; if (m.TryGetValue(id, out o)) return o; }
            var rows = Rows("SELECT kind FROM objects WHERE id = ?", id);
            if (rows.Count == 0) return null;
            var kind = (string)rows[0][0];
            if (!kinds.ContainsKey(kind)) return null;
            JObj found;
            return Objects(kind).TryGetValue(id, out found) ? found : null;
        }

        /// Stores an object as db8 writes one: arrays' objects given their ids, the kind's revision
        /// sets moved on, the row written, and watchers told.
        private void Write(JObj old, JObj o)
        {
            AssignIds(o);
            ApplyRevSets(old, o);
            Store(o);
            Changed(old, o);
        }

        private static JObj IdRev(JObj o) { return new JObj().Put("id", o.Str("_id")).Put("rev", o.Long("_rev")); }

        /// db8's revision sets (MojDbRevisionSet), for the kind and every kind it extends.
        private void ApplyRevSets(JObj old, JObj o)
        {
            var seen = new HashSet<string>();
            var todo = new Queue<string>();
            todo.Enqueue(o.Str("_kind"));
            while (todo.Count > 0)
            {
                Kind k;
                if (!kinds.TryGetValue(todo.Dequeue(), out k) || !seen.Add(k.Id)) continue;
                foreach (var e in k.Extends) todo.Enqueue(e);
                foreach (var rs in k.RevSets)
                {
                    if (old == null || rs.Props.Any(p => ValueText(old, p) != ValueText(o, p))) o.Put(rs.Name, o.Long("_rev"));
                }
            }
        }

        /// db8 (MojDb::assignIds) gives every object inside an array that has no _id one of its
        /// own, the next number of the revision counter in hex.
        private void AssignIds(JObj o)
        {
            foreach (var k in o.Keys.ToList())
            {
                var v = o[k];
                var a = v as JArr;
                if (a != null)
                {
                    foreach (var e in a.OfType<JObj>())
                    {
                        if (!e.Has("_id")) e.Put("_id", NextRev().ToString("x"));
                        AssignIds(e);
                    }
                }
                else if (v is JObj) AssignIds((JObj)v);
            }
        }

        private string ValueText(JObj o, string path) { var v = Value(o, path); return v == null ? null : Jx.Stringify(v); }

        private void Store(JObj o)
        {
            var id = o.Str("_id"); var kind = o.Str("_kind");
            Run("INSERT OR REPLACE INTO objects (id, kind, json) VALUES (?, ?, ?)", id, kind, o.ToString());
            Objects(kind)[id] = o;
        }

        /// The object's row gone, and its in-memory copy: a purge (db8's hard delete).
        private void Erase(JObj o)
        {
            Run("DELETE FROM objects WHERE id = ?", o.Str("_id"));
            Objects(o.Str("_kind")).Remove(o.Str("_id"));
        }

        private string Put(string caller, JObj p)
        {
            var objs = ObjectsParam(caller, p);
            var prepared = new List<KeyValuePair<JObj, JObj>>();
            foreach (JObj given in objs)
            {
                var o = given.Copy();
                var kindId = o.Str("_kind");
                if (kindId.Length == 0) throw new DbError(-3969, "db: kind not specified");
                var old = o.Str("_id").Length > 0 ? ById(o.Str("_id")) : null;
                CheckAccess(caller, kindId, old != null ? "update" : "create");
                if (old != null)
                {
                    if (!o.Has("_rev")) throw new DbError(-3960, "db: _rev must be specified for existing objects");
                    CheckRev(old, o.Long("_rev"));
                }
                prepared.Add(new KeyValuePair<JObj, JObj>(old, o));
            }
            var results = new JArr();
            Transaction(() =>
            {
                foreach (var pr in prepared)
                {
                    var old = pr.Key; var o = pr.Value;
                    if (!o.Has("_id")) o.Put("_id", NewId());
                    // The same object put again is no update (MojDb::putObj): it keeps its revision.
                    if (old != null && Identical(o, old)) { results.Add(IdRev(old)); continue; }
                    o.Put("_rev", NextRev());
                    if (old != null && old.Str("_kind") != o.Str("_kind")) Erase(old);
                    Write(old, o);
                    results.Add(IdRev(o));
                }
            });
            return new JObj().Put("returnValue", true).Put("results", results).ToString();
        }

        private static void CheckRev(JObj old, long given)
        {
            var expected = old.Long("_rev");
            if (given != expected) throw new DbError(-3961, "db: revision mismatch - expected " + expected + ", got " + given);
        }

        private string Get(string caller, JObj p)
        {
            var ids = Typed<JArr>(caller, p, "ids");
            var results = new JArr();
            foreach (var id in ids)
            {
                var o = id is string ? ById((string)id) : null;
                if (o == null) continue;
                CheckAccess(caller, o.Str("_kind"));
                results.Add(o);
            }
            return new JObj().Put("returnValue", true).Put("results", results).ToString();
        }

        private string Merge(string caller, JObj p)
        {
            if (p.Has("query"))
            {
                var props = Typed<JObj>(caller, p, "props");
                var q = QueryParam(caller, p);
                CheckAccess(caller, q.Str("from"), "update");
                var matches = RunQuery(caller, q, false, true).Key;
                Transaction(() => { foreach (var o in matches) MergeOne(o, props); });
                return new JObj().Put("returnValue", true).Put("count", matches.Count).ToString();
            }
            var objs = ObjectsParam(caller, p);
            var results = new JArr();
            Transaction(() =>
            {
                foreach (JObj patch in objs)
                {
                    var old = patch.Str("_id").Length > 0 ? ById(patch.Str("_id")) : null;
                    // A new id, or none, with a kind is created, as a put would (MojDb::putImpl
                    // with the merge flag; measured on the reference TouchPad).
                    if (old == null)
                    {
                        var kindId = patch.Str("_kind");
                        if (kindId.Length == 0) throw new DbError(-3950, "db: object not found");
                        CheckAccess(caller, kindId, "create");
                        var o = patch.Copy().Put("_rev", NextRev());
                        if (!o.Has("_id")) o.Put("_id", NewId());
                        Write(null, o);
                        results.Add(IdRev(o));
                        continue;
                    }
                    CheckAccess(caller, old.Str("_kind"), "update");
                    if (patch.Has("_rev")) CheckRev(old, patch.Long("_rev"));
                    results.Add(IdRev(MergeOne(old, patch)));
                }
            });
            return new JObj().Put("returnValue", true).Put("results", results).ToString();
        }

        /// Merges props into an object: objects merge recursively, anything else replaces. A merge
        /// that changes nothing leaves the object as it was, its revision too, and wakes no watch.
        private JObj MergeOne(JObj old, JObj props)
        {
            var o = old.Copy();
            DeepMerge(o, props);
            o.Put("_id", old.Str("_id")).Put("_kind", old.Str("_kind")).Put("_rev", old["_rev"]);
            if (Identical(o, old)) return old;
            o.Put("_rev", NextRev());
            Write(old, o);
            return o;
        }

        private static void DeepMerge(JObj into, JObj from)
        {
            foreach (var kv in from)
            {
                if (kv.Key == "_id" || kv.Key == "_rev" || kv.Key == "_kind") continue;
                var cur = into[kv.Key] as JObj;
                var v = kv.Value as JObj;
                if (v != null && cur != null) DeepMerge(cur, v);
                else into.Put(kv.Key, Jx.Clone(kv.Value));
            }
        }

        private string Del(string caller, JObj p)
        {
            var purge = p.Bool("purge");
            if (p.Has("query"))
            {
                var q = QueryParam(caller, p);
                CheckAccess(caller, q.Str("from"), "delete");
                var matches = RunQuery(caller, q, false, true).Key;
                Transaction(() => { foreach (var o in matches) DelOne(o, purge); });
                return new JObj().Put("returnValue", true).Put("count", matches.Count).ToString();
            }
            var ids = Typed<JArr>(caller, p, "ids");
            var results = new JArr();
            Transaction(() =>
            {
                foreach (var id in ids)
                {
                    var old = id is string ? ById((string)id) : null;
                    if (old == null) continue;
                    CheckAccess(caller, old.Str("_kind"), "delete");
                    var o = DelOne(old, purge);
                    var r = new JObj().Put("id", old.Str("_id"));
                    if (o != null) r.Put("rev", o.Long("_rev"));
                    results.Add(r);
                }
            });
            return new JObj().Put("returnValue", true).Put("results", results).ToString();
        }

        /// A soft delete marks _del and takes a revision; purge removes the object.
        private JObj DelOne(JObj old, bool hard)
        {
            if (hard) { Erase(old); Changed(old, null); return null; }
            var o = old.Copy().Put("_del", true).Put("_rev", NextRev());
            Write(old, o);
            return o;
        }

        // ---- queries ----

        private string Find(string caller, JObj p, Bus.Call call, bool search)
        {
            var q = QueryParam(caller, p);
            if (!search && q.Has("filter")) throw new DbError(-3978, "db: filter not allowed in find");
            var r = RunQuery(caller, q, search, true);
            var all = r.Key;
            int limit = Math.Max(1, Math.Min(MaxLimit, q.Has("limit") ? q.Int("limit", MaxLimit) : MaxLimit));
            int offset = q.Str("page").Length > 0 ? PageOffset(q.Str("page")) : 0;
            var page = all.Skip(offset).Take(limit).ToList();
            var select = q.Arr("select") == null ? null : q.Arr("select").OfType<string>().ToList();
            var results = new JArr();
            foreach (var o in page) results.Add(select == null ? o : Project(o, select));
            var out_ = new JObj().Put("returnValue", true).Put("results", results);
            if (page.Count == limit) out_.Put("next", PageToken(offset + limit));
            if (search || p.Bool("count")) out_.Put("count", all.Count);
            if (call != null && p.Bool("watch")) AddWatch(call, r.Value, Clauses(q.Arr("where")));
            return out_.ToString();
        }

        /// A query that has results already fires at once, as db8's did; one that hasn't answers
        /// {"returnValue":true} and fires when an object it would return changes.
        private string WatchOp(string caller, JObj p, Bus.Call call)
        {
            var q = QueryParam(caller, p);
            var r = RunQuery(caller, q, false, true);
            if (r.Key.Count > 0) return Fired;
            if (call != null) AddWatch(call, r.Value, Clauses(q.Arr("where")));
            return Ok();
        }

        /// The query's matching objects, sorted, and the kinds it covers.
        private KeyValuePair<List<JObj>, HashSet<string>> RunQuery(string caller, JObj q, bool search, bool validate)
        {
            var from = q.Str("from");
            if (from.Length == 0) throw new DbError(22, "invalid parameters: caller='" + caller + "' error='required property not found - 'from' for property 'query''");
            var k = KindOf(from);
            CheckAccess(caller, from);
            var where = Clauses(q.Arr("where"));
            var filter = Clauses(q.Arr("filter"));
            var orderBy = q.Str("orderBy").Length > 0 ? q.Str("orderBy") : null;
            // A query that names _del (an index that includes deleted objects) gets them as it asks.
            bool incDel = q.Bool("incDel") || where.Concat(filter).Any(c => c.Prop == "_del");
            if (validate)
            {
                if (q.Bool("incDel") && orderBy != null) throw new DbError(-3978, "db: query order not compatible with where clause");
                // search sorts what it finds itself (MojDbSearchCursor), so only find's order has
                // to be the index's.
                if (!IndexServes(k, where, search ? null : orderBy)) throw new DbError(-3965, "db: no index for query");
            }
            var family = Family(from);
            string collate = null;
            if (orderBy != null)
            {
                var ip = k.Indexes.SelectMany(ix => ix.Props).FirstOrDefault(x => x.Name == orderBy);
                if (ip != null) collate = ip.Collate;
            }
            // A find ordered by a property is read from that property's index, which holds only the
            // objects that have it (or whose index gives it a default; an explicit null counts).
            bool indexedOnly = orderBy != null && orderBy != "_id" && !search;
            var list = new List<JObj>();
            foreach (var kid in family)
            {
                foreach (var o in Objects(kid).Values)
                {
                    if (!incDel && o.Bool("_del")) continue;
                    if (!where.All(c => Matches(o, c)) || !filter.All(c => Matches(o, c))) continue;
                    if (indexedOnly && IndexedValue(o, orderBy) == null && !StoredNull(o, orderBy)) continue;
                    list.Add(o);
                }
            }
            var ordered = list.Select(o => new { o, key = orderBy == null ? null : IndexedValue(o, orderBy), id = o.Str("_id") }).ToList();
            ordered.Sort((a, b) =>
            {
                int c = orderBy == null ? 0 : Compare(a.key, b.key, collate);
                return c != 0 ? c : string.CompareOrdinal(a.id, b.id);
            });
            var sorted = ordered.Select(x => x.o).ToList();
            if (q.Bool("desc")) sorted.Reverse();
            return new KeyValuePair<List<JObj>, HashSet<string>>(sorted, new HashSet<string>(family));
        }

        /// Whether an index can serve the query, as db8 requires: the "=" props first (in any
        /// order), then at most one other prop, and orderBy on that prop, or on the next one.
        private static bool IndexServes(Kind k, List<Clause> where, string orderBy)
        {
            if (where.Count == 0 && (orderBy == null || orderBy == "_id")) return true;
            var eq = new HashSet<string>(where.Where(c => c.Op == "=").Select(c => c.Prop));
            var range = new HashSet<string>(where.Where(c => c.Op != "=").Select(c => c.Prop));
            if (range.Count > 1) return false;
            foreach (var ix in k.Indexes)
            {
                var props = ix.Props.Select(x => x.Name).ToList();
                // An index leading with _del serves a query that doesn't name it, as _del = false.
                if (props.Count > 0 && props[0] == "_del" && !eq.Contains("_del") && !range.Contains("_del")) props.RemoveAt(0);
                if (props.Count < eq.Count || !new HashSet<string>(props.Take(eq.Count)).SetEquals(eq)) continue;
                var next = props.Count > eq.Count ? props[eq.Count] : null;
                if (range.Count > 0) { if (next == range.First() && (orderBy == null || orderBy == next)) return true; }
                else if (orderBy != null) { if (orderBy == next || eq.Contains(orderBy)) return true; }
                else return true;
            }
            return false;
        }

        private static List<Clause> Clauses(JArr a)
        {
            var out_ = new List<Clause>();
            if (a == null) return out_;
            foreach (var e in a.OfType<JObj>())
                out_.Add(new Clause { Prop = e.Str("prop"), Op = e.Has("op") ? e.Str("op") : "=", Value = e["val"] });
            return out_;
        }

        private static object Value(JObj o, string path)
        {
            // Every object is deleted or not: db8 indexed a live one as _del false.
            if (path == "_del") return o.Bool("_del");
            return Walk(o, path.Split('.'), 0);
        }

        /// A dotted path's value. Through an array of objects it is every element's value, as db8
        /// indexed it (a prop under an array has one index entry per element).
        private static object Walk(object cur, string[] parts, int i)
        {
            if (cur == null || cur is JNull) return null;
            if (i == parts.Length) return cur;
            var o = cur as JObj;
            if (o != null) return Walk(o[parts[i]], parts, i + 1);
            var a = cur as JArr;
            if (a != null)
            {
                var out_ = new JArr();
                foreach (var e in a)
                {
                    var v = Walk(e, parts, i);
                    if (v == null) continue;
                    var va = v as JArr;
                    if (va != null) out_.AddRange(va); else out_.Add(v);
                }
                return out_.Count == 0 ? null : out_;
            }
            return null;
        }

        /// A prop as db8's indexes held it: missing, it is the index's "default" for it.
        private object IndexedValue(JObj o, string prop)
        {
            return Value(o, prop) ?? Multi(o, prop) ?? DefaultOf(o.Str("_kind"), prop, new HashSet<string>());
        }

        /// A "multi" prop (MojDbMultiExtractor): the words of the props it includes.
        private object Multi(JObj o, string prop)
        {
            var include = IncludeOf(o.Str("_kind"), prop, new HashSet<string>());
            if (include == null) return null;
            var out_ = new List<string>();
            foreach (var name in include)
            {
                var v = Value(o, name);
                if (v is string) out_.Add((string)v);
                else if (v is JArr) out_.AddRange(((JArr)v).OfType<string>());
            }
            return out_.Count == 0 ? null : string.Join(" ", out_);
        }

        private List<string> IncludeOf(string kindId, string prop, HashSet<string> seen)
        {
            Kind k;
            if (!kinds.TryGetValue(kindId, out k) || !seen.Add(kindId)) return null;
            foreach (var ix in k.Indexes) foreach (var p in ix.Props) if (p.Name == prop && p.Include != null) return p.Include;
            foreach (var e in k.Extends) { var r = IncludeOf(e, prop, seen); if (r != null) return r; }
            return null;
        }

        private object DefaultOf(string kindId, string prop, HashSet<string> seen)
        {
            Kind k;
            if (!kinds.TryGetValue(kindId, out k) || !seen.Add(kindId)) return null;
            foreach (var ix in k.Indexes) foreach (var p in ix.Props) if (p.Name == prop && p.Default != null && !(p.Default is JNull)) return p.Default;
            foreach (var e in k.Extends) { var r = DefaultOf(e, prop, seen); if (r != null) return r; }
            return null;
        }

        /// A clause matches when any of the prop's values matches any given value.
        private bool Matches(JObj o, Clause c)
        {
            var v = IndexedValue(o, c.Prop);
            IEnumerable<object> have = v is JArr ? (IEnumerable<object>)(JArr)v : new[] { v };
            IEnumerable<object> want = c.Op == "=" && c.Value is JArr ? (IEnumerable<object>)(JArr)c.Value : new[] { c.Value };
            var wantList = want.ToList();
            return have.Any(h => wantList.Any(w => Test(h, c.Op, w)));
        }

        private static bool Test(object h, string op, object w)
        {
            switch (op)
            {
                case "=": return SameType(h, w) && Compare(h, w, null) == 0;
                case "!=": return !(SameType(h, w) && Compare(h, w, null) == 0);
                case "<": return SameType(h, w) && Compare(h, w, null) < 0;
                case "<=": return SameType(h, w) && Compare(h, w, null) <= 0;
                case ">": return SameType(h, w) && Compare(h, w, null) > 0;
                case ">=": return SameType(h, w) && Compare(h, w, null) >= 0;
                case "%": return h is string && w is string && ((string)h).StartsWith((string)w, StringComparison.Ordinal);
                case "?":
                    if (!(h is string) || !(w is string)) return false;
                    var hw = Words((string)h);
                    return Words((string)w).All(ww => hw.Any(x => x.StartsWith(ww, StringComparison.Ordinal)));
                default: throw new DbError(-3978, "db: invalid operator '" + op + "'");
            }
        }

        private static readonly Regex NonWord = new Regex(@"[^\p{L}\p{N}]+");
        private static string[] Words(string s) { return NonWord.Split(s.ToLowerInvariant()).Where(x => x.Length > 0).ToArray(); }

        private static int Rank(object v)
        {
            if (v == null || v is JNull) return 0;
            if (v is bool) return 1;
            if (Jx.IsNumber(v)) return 2;
            if (v is string) return 3;
            if (v is JArr) return 4;
            return 5;
        }
        private static bool SameType(object a, object b) { return Rank(a) == Rank(b); }

        /// db8's order: null, booleans, numbers, strings (by the index's collation), then the rest.
        private static int Compare(object a, object b, string collate)
        {
            int ra = Rank(a), rb = Rank(b);
            if (ra != rb) return ra.CompareTo(rb);
            if (a is bool) return ((bool)a).CompareTo((bool)b);
            if (Jx.IsNumber(a)) return Jx.Num(a).CompareTo(Jx.Num(b));
            if (a is string)
            {
                var opts = Collation(collate);
                return opts == null ? string.CompareOrdinal((string)a, (string)b)
                    : CultureInfo.CurrentCulture.CompareInfo.Compare((string)a, (string)b, opts.Value);
            }
            return 0;
        }

        private static CompareOptions? Collation(string collate)
        {
            switch (collate)
            {
                case null: case "": case "default": case "identical": return null;
                case "primary": return CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreKanaType | CompareOptions.IgnoreWidth;
                case "secondary": return CompareOptions.IgnoreCase | CompareOptions.IgnoreKanaType | CompareOptions.IgnoreWidth;
                default: return CompareOptions.None;
            }
        }

        private static JObj Project(JObj o, List<string> select)
        {
            var out_ = new JObj();
            // A property stored as null is selected as null, as db8 returned it.
            foreach (var s in select)
            {
                var v = Value(o, s);
                if (v == null && StoredNull(o, s)) v = JNull.Value;
                if (v == null) continue;
                var parts = s.Split('.');
                var cur = out_;
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    var next = cur.Obj(parts[i]);
                    if (next == null) { next = new JObj(); cur.Put(parts[i], next); }
                    cur = next;
                }
                cur.Put(parts[parts.Length - 1], v);
            }
            return out_;
        }

        /// Whether a dotted path (through objects) holds an explicit null.
        private static bool StoredNull(JObj o, string path)
        {
            var parts = path.Split('.');
            var cur = o;
            for (int i = 0; i < parts.Length - 1; i++) { cur = cur.Obj(parts[i]); if (cur == null) return false; }
            return cur.IsNull(parts[parts.Length - 1]);
        }

        private static string PageToken(int offset)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes("lunacy:" + offset)).Replace('+', '-').Replace('/', '_');
        }

        private static int PageOffset(string token)
        {
            try
            {
                var t = token.Replace('-', '+').Replace('_', '/');
                while (t.Length % 4 != 0) t += "=";
                var s = Encoding.UTF8.GetString(Convert.FromBase64String(t));
                int n;
                if (s.StartsWith("lunacy:", StringComparison.Ordinal) && int.TryParse(s.Substring(7), out n)) return n;
            }
            catch (FormatException) { }
            throw new DbError(-3978, "db: invalid page");
        }

        // ---- watches ----

        private void AddWatch(Bus.Call call, HashSet<string> kinds_, List<Clause> where)
        {
            var w = new Watch { Call = call, Kinds = kinds_, Where = where };
            watches.Add(w);
            call.OnCancel(() => Work(() => watches.Remove(w)));
        }

        /// One-shot, as on webOS: a change to anything the query could return fires the watch.
        private void Changed(JObj old, JObj @new)
        {
            if (watches.Count == 0) return;
            var fired = watches.Where(w => new[] { old, @new }.Any(o => o != null && w.Kinds.Contains(o.Str("_kind")) && w.Where.All(c => Matches(o, c)))).ToList();
            foreach (var w in fired)
            {
                watches.Remove(w);
                w.Call.Reply(Fired);
                w.Call.Cancel();
            }
        }

        // ---- purge ----

        /// Deleted objects are kept, whole, until they have been deleted longer than the purge
        /// window (MojDb::purge, purgeImpl; Docs/db8.md, "Deleted objects and the purge").
        private int PurgeDeleted(int windowDays)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            int count = 0;
            Transaction(() =>
            {
                Run("INSERT INTO revtimes (rev, ts) VALUES (?, ?)", NextRev(), now);
                var cutRows = Rows("SELECT rev, ts FROM revtimes WHERE ts <= ? ORDER BY ts DESC LIMIT 1", now - windowDays * 86400000L);
                if (cutRows.Count == 0) return;
                long cutRev = (long)cutRows[0][0], cutTs = (long)cutRows[0][1];
                var gone = new List<JObj>();
                foreach (var r in Rows("SELECT json FROM objects WHERE json LIKE '%\"_del\":true%'"))
                {
                    JObj o;
                    try { o = Jx.ParseObj((string)r[0]); } catch (FormatException) { continue; }
                    if (o.Bool("_del") && o.Long("_rev") <= cutRev) gone.Add(o);
                }
                foreach (var o in gone) Erase(o);
                count = gone.Count;
                if (count > 0) SetMeta(LastPurged, cutRev + 1);
                Run("DELETE FROM revtimes WHERE ts <= ?", cutTs);
            });
            if (count > 0) LunaLog.Write("[db8] " + service + ": purged " + count + " deleted objects");
            return count;
        }

        /// -1 until a purge has removed something, as on the reference TouchPad.
        private long LastPurgedRev() { return Meta(LastPurged) - 1; }

        /// palm://com.palm.db/internal/scheduledPurge, the daily "mojodbpurge" activity's
        /// callback: answer at once, adopt the activity, purge, and complete it with restart.
        private void ScheduledPurge(Bus.Call call)
        {
            var p = Jx.ObjOrEmpty(call.Params.Stringify());
            var act = p.Obj("$activity");
            var activityId = act == null ? null : act["activityId"];
            if (activityId == null) { call.Reply(Error(22, "invalid parameters: caller='" + call.AppId + "' error='required property not found - '$activity''")); return; }
            call.Reply(Ok());
            var ids = new JObj().Put("activityId", activityId).Put("activityName", "mojodb scheduled purge");
            bool done = false;
            Bus.Call adopt = null;
            adopt = bus.DispatchPrivate(service, "palm://com.palm.activitymanager/adopt", ids.Copy().Put("wait", true).Put("subscribe", true).ToString(), reply =>
            {
                var r = Jx.ObjOrEmpty(reply);
                if (done || !(r.Bool("adopted") || r.Str("event") == "orphan")) return;
                done = true;
                Work(() =>
                {
                    try { PurgeDeleted(PurgeWindowDays); } catch (Exception e) { LunaLog.Write("[db8] " + service + ": purge failed: " + e.Message); }
                    bus.DispatchPrivate(service, "palm://com.palm.activitymanager/complete", ids.Copy().Put("restart", true).ToString(), _ =>
                    {
                        if (adopt != null) adopt.Cancel();
                    });
                });
            });
        }

        // ---- batch ----

        private string Batch(string caller, JObj p)
        {
            var ops = Typed<JArr>(caller, p, "operations");
            var responses = new JArr();
            foreach (var e in ops)
            {
                var op = e as JObj ?? new JObj();
                string r;
                try { r = Exec(op.Str("method"), caller, op.Obj("params") ?? new JObj(), null); }
                catch (DbError ex) { r = Error(ex.Code, ex.Message); }
                responses.Add(Jx.ParseObj(r));
            }
            return new JObj().Put("returnValue", true).Put("responses", responses).ToString();
        }

        // ---- ids, revisions, helpers ----

        /// Whether two values are the same as db8 compares objects (MojObject's ==).
        private static bool Identical(object a, object b)
        {
            var oa = a as JObj; var ob = b as JObj;
            if (oa != null && ob != null) return oa.Count == ob.Count && oa.All(kv => ob.Has(kv.Key) && Identical(kv.Value, ob[kv.Key]));
            var aa = a as JArr; var ab = b as JArr;
            if (aa != null && ab != null) return aa.Count == ab.Count && aa.Select((x, i) => Identical(x, ab[i])).All(x => x);
            if (Jx.IsNumber(a) && Jx.IsNumber(b))
            {
                bool ai = a is long, bi = b is long;
                return ai && bi ? (long)a == (long)b : !ai && !bi && Jx.Num(a) == Jx.Num(b);
            }
            if (a is JNull || b is JNull) return a is JNull && b is JNull;
            return Equals(a, b);
        }

        /// "++" and 14 characters in an order-preserving base-64 alphabet: time, then a counter.
        private string NewId()
        {
            idCounter = Math.Max(idCounter + 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() << 20);
            SetMeta("ids", idCounter);
            ulong n = (ulong)idCounter;
            var sb = new char[14];
            for (int i = 13; i >= 0; i--) { sb[i] = IdChars[(int)(n & 63)]; n >>= 6; }
            return "++" + new string(sb);
        }

        private long NextRev() { rev += 1; SetMeta("rev", rev); return rev; }

        private long Meta(string k)
        {
            var r = Rows("SELECT v FROM meta WHERE k = ?", k);
            return r.Count == 0 || !(r[0][0] is long) ? 0 : (long)r[0][0];
        }

        private void SetMeta(string k, long v) { Run("INSERT OR REPLACE INTO meta (k, v) VALUES (?, ?)", k, v); }

        private int txDepth;
        private void Transaction(Action f)
        {
            if (txDepth > 0) { f(); return; }
            txDepth++;
            Run("BEGIN");
            bool ok = false;
            try { f(); ok = true; }
            finally
            {
                txDepth--;
                Run(ok ? "COMMIT" : "ROLLBACK");
                // The cache took each write as it went: what the rollback undid is read again.
                if (!ok) cache.Clear();
            }
        }

        /// As the TouchPad answers (measured): 0 is no ids, a negative count is out of range,
        /// and more than 1000 is refused.
        private string ReserveIds(string caller, JObj p)
        {
            var v = Required(caller, p, "count");
            if (!Jx.IsNumber(v)) throw new DbError(22, "invalid parameters: caller='" + caller + "' error='invalid type for property 'count''");
            long count = (long)Jx.Num(v);
            if (count < 0) throw new DbError(22, "invalid parameters: caller='" + caller + "' error='value out of range for property 'count''");
            if (count > MaxReserve) throw new DbError(-3967, "cannot reserve more than " + MaxReserve + " ids");
            var ids = new JArr();
            for (long i = 0; i < count; i++) ids.Add(NewId());
            return new JObj().Put("returnValue", true).Put("ids", ids).ToString();
        }

        private static object Required(string caller, JObj p, string name)
        {
            var v = p[name];
            if (v == null) throw new DbError(22, "invalid parameters: caller='" + caller + "' error='required property not found - '" + name + "''");
            return v;
        }

        private static DbError InvalidType(string caller, string name)
        {
            return new DbError(22, "invalid parameters: caller='" + caller + "' error='invalid type for property '" + name + "''");
        }

        private static T Typed<T>(string caller, JObj p, string name) where T : class
        {
            var v = Required(caller, p, name) as T;
            if (v == null) throw InvalidType(caller, name);
            return v;
        }

        /// An array of objects: `put {"objects":[5]}` is the same "invalid type" on the device.
        private static JArr ObjectsParam(string caller, JObj p)
        {
            var a = Typed<JArr>(caller, p, "objects");
            if (a.Any(x => !(x is JObj))) throw InvalidType(caller, "objects");
            return a;
        }

        /// A query. One that isn't an object is reported by its missing `from`, and a `from`
        /// that isn't a string as the wrong type.
        private static JObj QueryParam(string caller, JObj p)
        {
            var q = Required(caller, p, "query") as JObj;
            if (q == null) throw new DbError(22, "invalid parameters: caller='" + caller + "' error='required property not found - 'from' for property 'query''");
            if (q.Has("from") && !(q["from"] is string))
                throw new DbError(22, "invalid parameters: caller='" + caller + "' error='invalid type for property 'from' for property 'query''");
            return q;
        }

        private static string Ok() { return "{\"returnValue\":true}"; }
        private static string Error(int code, string text)
        {
            return new JObj().Put("errorCode", code).Put("errorText", text).Put("returnValue", false).ToString();
        }

        // ---- SQLite ----

        private void Run(string sql, params object[] args)
        {
            IntPtr st;
            int rc = Sqlite.Prepare(db, sql, out st);
            if (rc != Sqlite.OK) throw new InvalidOperationException("db8 sql: " + Sqlite.ErrMsg(db) + " in " + sql);
            try
            {
                Bind(st, args);
                rc = Sqlite.sqlite3_step(st);
                if (rc != Sqlite.DONE && rc != Sqlite.ROW) throw new InvalidOperationException("db8 sql: " + Sqlite.ErrMsg(db) + " in " + sql);
            }
            finally { Sqlite.sqlite3_finalize(st); }
        }

        private List<object[]> Rows(string sql, params object[] args)
        {
            var out_ = new List<object[]>();
            IntPtr st;
            if (Sqlite.Prepare(db, sql, out st) != Sqlite.OK) throw new InvalidOperationException("db8 sql: " + Sqlite.ErrMsg(db) + " in " + sql);
            try
            {
                Bind(st, args);
                int n = Sqlite.sqlite3_column_count(st);
                while (Sqlite.sqlite3_step(st) == Sqlite.ROW)
                {
                    var row = new object[n];
                    for (int i = 0; i < n; i++)
                    {
                        switch (Sqlite.sqlite3_column_type(st, i))
                        {
                            case Sqlite.INTEGER: row[i] = Sqlite.sqlite3_column_int64(st, i); break;
                            case Sqlite.FLOAT: row[i] = Sqlite.sqlite3_column_double(st, i); break;
                            case Sqlite.NULL: row[i] = null; break;
                            default: row[i] = Sqlite.ColumnText(st, i); break;
                        }
                    }
                    out_.Add(row);
                }
            }
            finally { Sqlite.sqlite3_finalize(st); }
            return out_;
        }

        private static void Bind(IntPtr st, object[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (a == null) Sqlite.sqlite3_bind_null(st, i + 1);
                else if (a is long || a is int) Sqlite.sqlite3_bind_int64(st, i + 1, Convert.ToInt64(a));
                else if (a is double) Sqlite.sqlite3_bind_double(st, i + 1, (double)a);
                else Sqlite.BindText(st, i + 1, a.ToString());
            }
        }

        private const int MaxReserve = 1000;
        private const string LastPurged = "lastPurgedRev";
        /// The reference TouchPad's purgeWindow (/etc/palm/mojodb.conf).
        private const int PurgeWindowDays = 7;
        /// db8's admin role (/etc/palm/mojodb.conf), the only callers allowed purge.
        private static readonly HashSet<string> Admins = new HashSet<string> { "com.palm.configurator", "com.palm.service.backup", "com.palm.odd.service",
            "com.palm.service.migrationscript", "com.palm.spacecadet", "com.palm.backup.privileged", "com.palm.app.backup.service" };
        public const int MaxLimit = 500;
        /// The caller name for package configuration, which may register any kind.
        public const string Configurator = "com.palm.configurator";
        private const string IdChars = "+0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ_abcdefghijklmnopqrstuvwxyz";
        private static readonly string Fired = "{\"returnValue\":true,\"fired\":true}";
    }
}
