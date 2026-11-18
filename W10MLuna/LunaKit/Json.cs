using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Windows.Data.Json;

namespace LunaKit
{
    /// JSON with its members in the order given, as a device's were (iOSLuna's orderedJSON).
    /// Windows.Data.Json parses; this writes, so replies keep the reference device's order.
    internal static class J
    {
        public static string Str(string s)
        {
            if (s == null) return "null";
            var b = new StringBuilder(s.Length + 2);
            b.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': b.Append("\\\""); break;
                    case '\\': b.Append("\\\\"); break;
                    case '\n': b.Append("\\n"); break;
                    case '\r': b.Append("\\r"); break;
                    case '\t': b.Append("\\t"); break;
                    case '\b': b.Append("\\b"); break;
                    case '\f': b.Append("\\f"); break;
                    default:
                        // U+2028/2029 end a line inside a JS string literal: escape them, since
                        // replies are evaluated as script.
                        if (c < 0x20 || c == '\u2028' || c == '\u2029') b.Append("\\u").Append(((int)c).ToString("x4"));
                        else b.Append(c);
                        break;
                }
            }
            return b.Append('"').ToString();
        }

        public static string Value(object v)
        {
            if (v == null) return "null";
            if (v is bool) return (bool)v ? "true" : "false";
            if (v is string) return Str((string)v);
            if (v is int || v is long) return System.Convert.ToInt64(v).ToString(CultureInfo.InvariantCulture);
            if (v is double || v is float)
            {
                var d = System.Convert.ToDouble(v);
                if (double.IsNaN(d) || double.IsInfinity(d)) return "null";
                return d.ToString("R", CultureInfo.InvariantCulture);
            }
            if (v is Raw) return ((Raw)v).Text;
            if (v is IJsonValue) return ((IJsonValue)v).Stringify();
            if (v is IEnumerable<string>) return "[" + string.Join(",", ((IEnumerable<string>)v).Select(Str)) + "]";
            return Str(v.ToString());
        }

        public static string Obj(params KeyValuePair<string, object>[] pairs)
        {
            return "{" + string.Join(",", pairs.Select(p => Str(p.Key) + ":" + Value(p.Value))) + "}";
        }

        public static KeyValuePair<string, object> P(string k, object v) { return new KeyValuePair<string, object>(k, v); }

        /// Already JSON: written as it is.
        public sealed class Raw
        {
            public readonly string Text;
            public Raw(string text) { Text = text; }
        }

        public static JsonObject ParseObject(string s)
        {
            JsonObject o;
            return JsonObject.TryParse(string.IsNullOrEmpty(s) ? "{}" : s, out o) ? o : new JsonObject();
        }

        public static string GetString(this JsonObject o, string k, string fallback = null)
        {
            IJsonValue v;
            if (!o.TryGetValue(k, out v)) return fallback;
            return v.ValueType == JsonValueType.String ? v.GetString() : fallback;
        }

        public static bool GetBool(this JsonObject o, string k)
        {
            IJsonValue v;
            if (!o.TryGetValue(k, out v)) return false;
            if (v.ValueType == JsonValueType.Boolean) return v.GetBoolean();
            return v.ValueType == JsonValueType.String && v.GetString() == "true";
        }

        public static int GetInt(this JsonObject o, string k, int fallback = 0)
        {
            IJsonValue v;
            if (!o.TryGetValue(k, out v) || v.ValueType != JsonValueType.Number) return fallback;
            return (int)v.GetNumber();
        }
    }
}
