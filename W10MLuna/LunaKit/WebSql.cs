using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Storage;

namespace LunaKit
{
    /// The store behind Lunacy's WebSQL polyfill (lunacy/websql.js), as AndroidLuna's WebSql.kt
    /// and iOSLuna's WebSql.swift keep it: one SQLite file per app and database name, the version
    /// in WebKit's own `__WebKitDatabaseInfo__` table, and each transaction a native one held
    /// open across the page's round trips. Statements run in order on a thread of their own,
    /// never the UI thread. EdgeHTML has no WebSQL at all, so every app's databases are here.
    internal sealed class WebSql
    {
        private const int UnknownErr = 0, DatabaseErr = 1, VersionErr = 2, QuotaErr = 4, SyntaxErr = 5, ConstraintErr = 6;
        private const string InfoTable = "__WebKitDatabaseInfo__";
        private const string VersionKey = "WebKitDatabaseVersionKey";
        private static readonly Regex LeadsWithRows = new Regex("^\\s*(WITH|PRAGMA|EXPLAIN|SELECT|VALUES)\\b", RegexOptions.IgnoreCase);
        private const string OkReply = "{\"ok\":true}";

        private readonly string dir;
        private readonly Action<int> ready;
        private readonly BlockingCollection<Action> queue = new BlockingCollection<Action>();
        private readonly Dictionary<string, IntPtr> open = new Dictionary<string, IntPtr>();
        private readonly Dictionary<string, bool> inTransaction = new Dictionary<string, bool>();  // name -> readOnly
        private readonly ConcurrentDictionary<int, string> results = new ConcurrentDictionary<int, string>();
        private int page;

        private sealed class Failure : Exception
        {
            public readonly int Code;
            public Failure(int code, string message) : base(message) { Code = code; }
        }

        public WebSql(string appId, Action<int> ready)
        {
            dir = Path.Combine(ApplicationData.Current.LocalFolder.Path, "websql", appId);
            Directory.CreateDirectory(dir);
            this.ready = ready;
            Task.Factory.StartNew(() => { foreach (var a in queue.GetConsumingEnumerable()) a(); }, TaskCreationOptions.LongRunning);
        }

        private T OnQueue<T>(Func<T> f)
        {
            var done = new TaskCompletionSource<T>();
            queue.Add(() => { try { done.SetResult(f()); } catch (Exception e) { done.SetException(e); } });
            return done.Task.GetAwaiter().GetResult();
        }

        public string Open(string name, string version, bool creationCallback)
        {
            return OnQueue(() =>
            {
                try { return OpenOn(name, version, creationCallback); }
                catch (Failure f) { return J.Obj(J.P("error", f.Message)); }
                catch (Exception) { return J.Obj(J.P("error", "unable to open database")); }
            });
        }

        private string OpenOn(string name, string version, bool creationCallback)
        {
            IntPtr db;
            if (!open.TryGetValue(name, out db)) { db = OpenFile(name); open[name] = db; }
            Exec(db, "CREATE TABLE IF NOT EXISTS " + InfoTable + " (key TEXT NOT NULL ON CONFLICT FAIL UNIQUE ON CONFLICT REPLACE, value TEXT NOT NULL ON CONFLICT FAIL)");
            var stored = ReadVersion(db);
            if (stored == null)
            {
                var v = creationCallback ? "" : version;
                WriteVersion(db, v);
                return J.Obj(J.P("version", v), J.P("created", true));
            }
            if (!string.IsNullOrEmpty(version) && version != stored) return J.Obj(J.P("mismatch", stored));
            return J.Obj(J.P("version", stored), J.P("created", false));
        }

        private IntPtr OpenFile(string name)
        {
            var safe = Convert.ToBase64String(Encoding.UTF8.GetBytes(name)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
            IntPtr db;
            if (Sqlite.Open(Path.Combine(dir, safe + ".db"), out db) != Sqlite.OK) throw new Failure(DatabaseErr, "unable to open database");
            return db;
        }

        public void Request(int id, string request)
        {
            queue.Add(() =>
            {
                int from = page;
                JsonObject j;
                var r = JsonObject.TryParse(request ?? "", out j) ? Handle(j) : Error(UnknownErr, "bad request");
                if (from != page) return;
                results[id] = r;
                ready(id);
            });
        }

        public string Result(int id)
        {
            string r;
            return results.TryRemove(id, out r) ? r : "{\"error\":{\"code\":0,\"message\":\"no result\"}}";
        }

        /// A new page: its predecessor's open transactions are rolled back.
        public void Reset()
        {
            queue.Add(() =>
            {
                page++;
                results.Clear();
                foreach (var name in inTransaction.Keys.ToList())
                {
                    IntPtr db;
                    if (open.TryGetValue(name, out db)) Sqlite.Exec(db, "ROLLBACK");
                }
                inTransaction.Clear();
            });
        }

        private string Handle(JsonObject r)
        {
            var name = r.GetString("db", "");
            IntPtr db;
            if (!open.TryGetValue(name, out db)) return Error(DatabaseErr, "unable to open database");
            switch (r.GetString("op"))
            {
                case "begin": return Begin(name, db, r.GetBool("readOnly"), r.GetString("oldVersion"));
                case "exec":
                    IJsonValue s;
                    var statements = r.TryGetValue("statements", out s) && s.ValueType == JsonValueType.Array ? s.GetArray() : new JsonArray();
                    return Execute(name, db, statements);
                case "commit": return Commit(name, db, r.GetString("newVersion"));
                case "rollback": return Rollback(name, db);
                default: return Error(UnknownErr, "unknown operation");
            }
        }

        private string Begin(string name, IntPtr db, bool readOnly, string oldVersion)
        {
            if (inTransaction.ContainsKey(name)) Rollback(name, db);
            try { Exec(db, "BEGIN"); }
            catch (Failure f) { return Error(DatabaseErr, "unable to begin transaction (" + f.Message + ")"); }
            inTransaction[name] = readOnly;
            if (oldVersion != null)
            {
                string current = null;
                try { current = ReadVersion(db); } catch (Failure) { }
                if (current != oldVersion)
                {
                    Rollback(name, db);
                    return Error(VersionErr, "current version of the database and `oldVersion` argument do not match");
                }
            }
            return OkReply;
        }

        private string Commit(string name, IntPtr db, string newVersion)
        {
            if (!inTransaction.ContainsKey(name)) return Error(DatabaseErr, "unable to commit transaction (none open)");
            try
            {
                if (newVersion != null) WriteVersion(db, newVersion);
                Exec(db, "COMMIT");
            }
            catch (Exception e)
            {
                inTransaction.Remove(name);
                Sqlite.Exec(db, "ROLLBACK");
                return Error(DatabaseErr, "unable to commit transaction (" + e.Message + ")");
            }
            inTransaction.Remove(name);
            return OkReply;
        }

        private string Rollback(string name, IntPtr db)
        {
            if (inTransaction.Remove(name)) Sqlite.Exec(db, "ROLLBACK");
            return OkReply;
        }

        private string Execute(string name, IntPtr db, JsonArray statements)
        {
            bool readOnly;
            if (!inTransaction.TryGetValue(name, out readOnly)) return Error(DatabaseErr, "no transaction is open");
            var results = new List<string>();
            foreach (var sv in statements)
            {
                if (sv.ValueType != JsonValueType.Object) continue;
                var s = sv.GetObject();
                IJsonValue a;
                var args = s.TryGetValue("args", out a) && a.ValueType == JsonValueType.Array ? a.GetArray() : new JsonArray();
                try { results.Add(Statement(db, s.GetString("sql", ""), args, readOnly)); }
                catch (Failure f) { results.Add(Error(f.Code, f.Message)); break; }
                catch (Exception e) { results.Add(Error(UnknownErr, e.Message)); break; }
            }
            return "{\"results\":[" + string.Join(",", results) + "]}";
        }

        private string Statement(IntPtr db, string sql, JsonArray args, bool readOnly)
        {
            bool query = LeadsWithRows.IsMatch(sql);
            if (readOnly && !query) throw new Failure(SyntaxErr, "not authorized");
            int expected = ParameterCount(sql);
            if (expected >= 0 && expected != args.Count)
                throw new Failure(SyntaxErr, "number of '?'s in statement string does not match argument count");
            IntPtr st;
            if (Sqlite.Prepare(db, sql, out st) != Sqlite.OK || st == IntPtr.Zero) throw new Failure(SyntaxErr, Sqlite.ErrMsg(db));
            try
            {
                for (int i = 0; i < args.Count; i++)
                {
                    var v = args[i];
                    int n = i + 1;
                    switch (v.ValueType)
                    {
                        case JsonValueType.Null: Sqlite.sqlite3_bind_null(st, n); break;
                        case JsonValueType.Boolean: Sqlite.sqlite3_bind_int64(st, n, v.GetBoolean() ? 1 : 0); break;
                        case JsonValueType.Number:
                            var d = v.GetNumber();
                            if (d != Math.Round(d) || Math.Abs(d) >= 9.2e18) Sqlite.sqlite3_bind_double(st, n, d);
                            else Sqlite.sqlite3_bind_int64(st, n, (long)d);
                            break;
                        case JsonValueType.String: Sqlite.BindText(st, n, v.GetString()); break;
                        default: Sqlite.BindText(st, n, v.Stringify()); break;
                    }
                }
                int cols = Sqlite.sqlite3_column_count(st);
                var columns = new List<string>();
                for (int c = 0; c < cols; c++) columns.Add(Sqlite.ColumnName(st, c));
                var rows = new List<string>();
                while (true)
                {
                    int rc = Sqlite.sqlite3_step(st);
                    if (rc == Sqlite.DONE) break;
                    if (rc != Sqlite.ROW)
                    {
                        int code = (rc & 0xff) == Sqlite.CONSTRAINT ? ConstraintErr : (rc & 0xff) == Sqlite.FULL ? QuotaErr : DatabaseErr;
                        throw new Failure(code, Sqlite.ErrMsg(db));
                    }
                    var row = new List<string>();
                    for (int c = 0; c < cols; c++)
                    {
                        switch (Sqlite.sqlite3_column_type(st, c))
                        {
                            case Sqlite.NULL: row.Add("null"); break;
                            case Sqlite.INTEGER: row.Add(Sqlite.sqlite3_column_int64(st, c).ToString(CultureInfo.InvariantCulture)); break;
                            case Sqlite.FLOAT: row.Add(J.Value(Sqlite.sqlite3_column_double(st, c))); break;
                            case Sqlite.BLOB:
                                // As WebKit gave a blob: its bytes as Latin-1 characters.
                                var bytes = Sqlite.ColumnBlob(st, c);
                                row.Add(J.Str(new string(bytes.Select(x => (char)x).ToArray())));
                                break;
                            default: row.Add(J.Str(Sqlite.ColumnText(st, c))); break;
                        }
                    }
                    rows.Add("[" + string.Join(",", row) + "]");
                }
                int changed = query ? 0 : Sqlite.sqlite3_changes(db);
                var insertId = !query && changed > 0 ? Sqlite.sqlite3_last_insert_rowid(db).ToString(CultureInfo.InvariantCulture) : "null";
                return "{\"columns\":" + J.Value(columns) + ",\"rows\":[" + string.Join(",", rows) + "],\"rowsAffected\":" + changed + ",\"insertId\":" + insertId + "}";
            }
            finally { Sqlite.sqlite3_finalize(st); }
        }

        private static void Exec(IntPtr db, string sql)
        {
            if (Sqlite.Exec(db, sql) != Sqlite.OK) throw new Failure(DatabaseErr, Sqlite.ErrMsg(db));
        }

        private static string ReadVersion(IntPtr db)
        {
            IntPtr st;
            if (Sqlite.Prepare(db, "SELECT value FROM " + InfoTable + " WHERE key = ?", out st) != Sqlite.OK) throw new Failure(DatabaseErr, Sqlite.ErrMsg(db));
            try
            {
                Sqlite.BindText(st, 1, VersionKey);
                return Sqlite.sqlite3_step(st) == Sqlite.ROW ? Sqlite.ColumnText(st, 0) : null;
            }
            finally { Sqlite.sqlite3_finalize(st); }
        }

        private static void WriteVersion(IntPtr db, string version)
        {
            IntPtr st;
            if (Sqlite.Prepare(db, "INSERT INTO " + InfoTable + " (key, value) VALUES (?, ?)", out st) != Sqlite.OK) throw new Failure(DatabaseErr, Sqlite.ErrMsg(db));
            try
            {
                Sqlite.BindText(st, 1, VersionKey);
                Sqlite.BindText(st, 2, version);
                if (Sqlite.sqlite3_step(st) != Sqlite.DONE) throw new Failure(DatabaseErr, Sqlite.ErrMsg(db));
            }
            finally { Sqlite.sqlite3_finalize(st); }
        }

        private static string Error(int code, string message) { return "{\"error\":" + J.Obj(J.P("code", code), J.P("message", message)) + "}"; }

        /// The number of `?` parameters, or -1 when the statement names or numbers them.
        internal static int ParameterCount(string s)
        {
            int n = 0, i = 0;
            while (i < s.Length)
            {
                char ch = s[i];
                if (ch == '\'' || ch == '"' || ch == '`')
                {
                    i++;
                    while (i < s.Length) { if (s[i] == ch) { if (i + 1 < s.Length && s[i + 1] == ch) i++; else break; } i++; }
                }
                else if (ch == '[') { while (i < s.Length && s[i] != ']') i++; }
                else if (ch == '-' && i + 1 < s.Length && s[i + 1] == '-') { while (i < s.Length && s[i] != '\n') i++; }
                else if (ch == '/' && i + 1 < s.Length && s[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < s.Length && !(s[i] == '*' && s[i + 1] == '/')) i++;
                    i++;
                }
                else if (ch == '?')
                {
                    if (i + 1 < s.Length && char.IsDigit(s[i + 1])) return -1;
                    n++;
                }
                else if ((ch == ':' || ch == '@' || ch == '$') && i + 1 < s.Length && (char.IsLetter(s[i + 1]) || s[i + 1] == '_')) return -1;
                i++;
            }
            return n;
        }
    }
}
