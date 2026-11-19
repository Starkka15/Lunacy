using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace LunaKit
{
    /// A JSON tree in managed memory, for db8 and the services that hold many objects:
    /// Windows.Data.Json keeps every number as a double and crosses into WinRT on each read,
    /// where db8 filters thousands of cached objects at a time. Values are JObj, JArr, string,
    /// bool, long, double, or JNull.Value (a stored null, which is not a missing key). Integers
    /// stay integers, as org.json's do in AndroidLuna, so ids and revisions round-trip exactly.
    internal sealed class JNull
    {
        public static readonly JNull Value = new JNull();
        private JNull() { }
        public override string ToString() { return "null"; }
    }

    /// An object that keeps its keys in the order they were added, as org.json and JSON.stringify do.
    internal sealed class JObj : IEnumerable<KeyValuePair<string, object>>
    {
        private readonly Dictionary<string, object> map = new Dictionary<string, object>();
        private readonly List<string> order = new List<string>();

        public int Count { get { return map.Count; } }
        public IEnumerable<string> Keys { get { return order; } }
        public bool Has(string k) { return map.ContainsKey(k); }

        /// The value, or null when the key is missing (a stored null is JNull.Value).
        public object this[string k]
        {
            get { object v; return map.TryGetValue(k, out v) ? v : null; }
            set { Put(k, value); }
        }

        public JObj Put(string k, object v)
        {
            if (v == null) v = JNull.Value;
            if (v is int) v = (long)(int)v;
            if (!map.ContainsKey(k)) order.Add(k);
            map[k] = v;
            return this;
        }

        public bool Remove(string k)
        {
            if (!map.Remove(k)) return false;
            order.Remove(k);
            return true;
        }

        public bool IsNull(string k) { object v; return map.TryGetValue(k, out v) && v is JNull; }
        public string Str(string k, string fallback = "") { var v = this[k] as string; return v ?? fallback; }
        public bool Bool(string k) { var v = this[k]; return v is bool && (bool)v; }
        public long Long(string k, long fallback = 0)
        {
            var v = this[k];
            if (v is long) return (long)v;
            if (v is double) return (long)(double)v;
            if (v is string) { long l; if (long.TryParse((string)v, NumberStyles.Integer, CultureInfo.InvariantCulture, out l)) return l; }
            return fallback;
        }
        public int Int(string k, int fallback = 0) { return Has(k) ? (int)Long(k, fallback) : fallback; }
        public JObj Obj(string k) { return this[k] as JObj; }
        public JArr Arr(string k) { return this[k] as JArr; }

        public JObj Copy() { return (JObj)Jx.Clone(this); }

        public IEnumerator<KeyValuePair<string, object>> GetEnumerator()
        {
            foreach (var k in order) yield return new KeyValuePair<string, object>(k, map[k]);
        }
        IEnumerator IEnumerable.GetEnumerator() { return GetEnumerator(); }
        public override string ToString() { return Jx.Stringify(this); }
    }

    internal sealed class JArr : List<object>
    {
        public JArr() { }
        public JArr(IEnumerable<object> items) : base(items) { }
        public JArr Put(object v) { Add(v == null ? JNull.Value : v is int ? (long)(int)v : v); return this; }
        public override string ToString() { return Jx.Stringify(this); }
    }

    internal static class Jx
    {
        public static object Parse(string s)
        {
            var p = new Parser(s);
            p.Space();
            var v = p.Value();
            p.Space();
            if (p.I < s.Length) throw new FormatException("JSON: unexpected '" + s[p.I] + "' at " + p.I);
            return v;
        }

        public static JObj ParseObj(string s)
        {
            var v = Parse(s) as JObj;
            if (v == null) throw new FormatException("JSON: not an object");
            return v;
        }

        /// An object from a string that may be empty or broken: an empty object then.
        public static JObj ObjOrEmpty(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return new JObj();
            try { return Parse(s) as JObj ?? new JObj(); } catch (FormatException) { return new JObj(); }
        }

        public static object Clone(object v)
        {
            var o = v as JObj;
            if (o != null) { var c = new JObj(); foreach (var kv in o) c.Put(kv.Key, Clone(kv.Value)); return c; }
            var a = v as JArr;
            if (a != null) { var c = new JArr(); foreach (var x in a) c.Add(Clone(x)); return c; }
            return v;
        }

        public static bool IsNumber(object v) { return v is long || v is double || v is int; }
        public static double Num(object v) { return v is long ? (long)v : v is int ? (int)v : v is double ? (double)v : 0; }

        public static string Stringify(object v)
        {
            var sb = new StringBuilder();
            Write(sb, v);
            return sb.ToString();
        }

        public static void Write(StringBuilder sb, object v)
        {
            if (v == null || v is JNull) { sb.Append("null"); return; }
            var s = v as string;
            if (s != null) { Quote(sb, s); return; }
            if (v is bool) { sb.Append((bool)v ? "true" : "false"); return; }
            if (v is long || v is int) { sb.Append(Convert.ToInt64(v).ToString(CultureInfo.InvariantCulture)); return; }
            if (v is double)
            {
                var d = (double)v;
                if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append("null"); return; }
                if (d == Math.Floor(d) && Math.Abs(d) < 1e15) { sb.Append(((long)d).ToString(CultureInfo.InvariantCulture)); return; }
                sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            var o = v as JObj;
            if (o != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (var kv in o)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Quote(sb, kv.Key);
                    sb.Append(':');
                    Write(sb, kv.Value);
                }
                sb.Append('}');
                return;
            }
            var a = v as IList;
            if (a != null)
            {
                sb.Append('[');
                for (int i = 0; i < a.Count; i++) { if (i > 0) sb.Append(','); Write(sb, a[i]); }
                sb.Append(']');
                return;
            }
            var raw = v as J.Raw;
            if (raw != null) { sb.Append(raw.Text); return; }
            Quote(sb, v.ToString());
        }

        public static void Quote(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case (char)0x2028: sb.Append("\\u2028"); break;
                    case (char)0x2029: sb.Append("\\u2029"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        private sealed class Parser
        {
            private readonly string s;
            public int I;
            public Parser(string s) { this.s = s ?? ""; }

            public void Space() { while (I < s.Length && (s[I] == ' ' || s[I] == '\t' || s[I] == '\n' || s[I] == '\r')) I++; }

            private FormatException Bad(string what) { return new FormatException("JSON: " + what + " at " + I); }

            public object Value()
            {
                if (I >= s.Length) throw Bad("unexpected end");
                char c = s[I];
                if (c == '{') return Object();
                if (c == '[') return Array();
                if (c == '"') return String();
                if (c == 't' && Word("true")) return true;
                if (c == 'f' && Word("false")) return false;
                if (c == 'n' && Word("null")) return JNull.Value;
                if (c == '-' || (c >= '0' && c <= '9')) return Number();
                throw Bad("unexpected '" + c + "'");
            }

            private bool Word(string w)
            {
                if (string.CompareOrdinal(s, I, w, 0, w.Length) != 0) return false;
                I += w.Length;
                return true;
            }

            // A comma before a closing brace or bracket is taken as if it weren't there, as
            // webOS's json-c read Palm's own files (Configurator.kt's jsonC).
            private JObj Object()
            {
                var o = new JObj();
                I++;
                Space();
                if (I < s.Length && s[I] == '}') { I++; return o; }
                while (true)
                {
                    Space();
                    if (I < s.Length && s[I] == '}') { I++; return o; }
                    if (I >= s.Length || s[I] != '"') throw Bad("expected a key");
                    var k = String();
                    Space();
                    if (I >= s.Length || s[I] != ':') throw Bad("expected ':'");
                    I++;
                    Space();
                    o.Put(k, Value());
                    Space();
                    if (I < s.Length && s[I] == ',') { I++; continue; }
                    if (I < s.Length && s[I] == '}') { I++; return o; }
                    throw Bad("expected ',' or '}'");
                }
            }

            private JArr Array()
            {
                var a = new JArr();
                I++;
                Space();
                if (I < s.Length && s[I] == ']') { I++; return a; }
                while (true)
                {
                    Space();
                    if (I < s.Length && s[I] == ']') { I++; return a; }
                    a.Add(Value());
                    Space();
                    if (I < s.Length && s[I] == ',') { I++; continue; }
                    if (I < s.Length && s[I] == ']') { I++; return a; }
                    throw Bad("expected ',' or ']'");
                }
            }

            private string String()
            {
                I++;
                StringBuilder sb = null;
                int start = I;
                while (I < s.Length)
                {
                    char c = s[I];
                    if (c == '"')
                    {
                        string r = sb == null ? s.Substring(start, I - start) : sb.Append(s, start, I - start).ToString();
                        I++;
                        return r;
                    }
                    if (c == '\\')
                    {
                        if (sb == null) sb = new StringBuilder();
                        sb.Append(s, start, I - start);
                        I++;
                        if (I >= s.Length) break;
                        char e = s[I++];
                        switch (e)
                        {
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case 'b': sb.Append('\b'); break;
                            case 'f': sb.Append('\f'); break;
                            case 'u':
                                if (I + 4 > s.Length) throw Bad("short \\u escape");
                                sb.Append((char)int.Parse(s.Substring(I, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                                I += 4;
                                break;
                            default: sb.Append(e); break;
                        }
                        start = I;
                        continue;
                    }
                    I++;
                }
                throw Bad("unterminated string");
            }

            private object Number()
            {
                int start = I;
                if (s[I] == '-') I++;
                bool real = false;
                while (I < s.Length)
                {
                    char c = s[I];
                    if (c >= '0' && c <= '9') { I++; continue; }
                    if (c == '.' || c == 'e' || c == 'E' || c == '+' || (c == '-' && I > start)) { real = true; I++; continue; }
                    break;
                }
                var t = s.Substring(start, I - start);
                long l;
                if (!real && long.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out l)) return l;
                double d;
                if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
                throw Bad("bad number '" + t + "'");
            }
        }
    }
}
