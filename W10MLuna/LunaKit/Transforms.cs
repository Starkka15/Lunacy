using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace LunaKit
{
    // Lunacy's global serve-time transforms (AndroidLuna's AppServer.kt: HtmlTransforms,
    // JsTransforms, CssTransforms; iOSLuna's Transforms.swift), ported as they are. Each is one
    // mechanical rule applied to every file of its type; none names an app. The reasons are
    // recorded in the Kotlin originals and Lunacy's Docs/fix-log.md; a change to a rule belongs
    // there first.

    internal static class HtmlTransforms
    {
        private static readonly HashSet<string> Void = new HashSet<string> {
            "area", "base", "basefont", "bgsound", "br", "col", "command", "embed", "frame", "hr",
            "img", "input", "isindex", "keygen", "link", "meta", "param", "source", "track", "wbr" };
        private static readonly HashSet<string> RawText = new HashSet<string> { "script", "style", "textarea", "title" };

        private static bool IsWord(char c) { return (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '-' || c > 127; }
        private static bool IsSpace(char c) { return c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f'; }

        /// `<script src="x.js" />` closes the element, as webOS's pre-HTML5 tokenizer did.
        public static string SelfClosingTags(string s)
        {
            if (!s.Contains("/>")) return s;
            var o = new StringBuilder(s.Length + 64);
            int i = 0;
            while (true)
            {
                int lt = s.IndexOf('<', i);
                if (lt < 0) { o.Append(s, i, s.Length - i); break; }
                o.Append(s, i, lt - i);
                if (string.CompareOrdinal(s, lt, "<!--", 0, 4) == 0)
                {
                    int end = s.IndexOf("-->", lt + 4, System.StringComparison.Ordinal);
                    int stop = end < 0 ? s.Length : end + 3;
                    o.Append(s, lt, stop - lt); i = stop; continue;
                }
                int j = lt + 1;
                while (j < s.Length && IsWord(s[j])) j++;
                var name = s.Substring(lt + 1, j - lt - 1).ToLowerInvariant();
                if (name.Length == 0) { o.Append('<'); i = lt + 1; continue; }
                int k = j;
                char quote = '\0';
                while (k < s.Length)
                {
                    char c = s[k];
                    if (quote != '\0') { if (c == quote) quote = '\0'; }
                    else if (c == '"' || c == '\'') quote = c;
                    else if (c == '>') break;
                    k++;
                }
                if (k >= s.Length) { o.Append(s, lt, s.Length - lt); i = s.Length; continue; }
                int slash = k - 1;
                while (slash > j && IsSpace(s[slash])) slash--;
                bool closes = slash >= j && s[slash] == '/';
                if (closes && !Void.Contains(name)) { o.Append(s, lt, slash - lt).Append("></").Append(name).Append('>'); }
                else o.Append(s, lt, k - lt + 1);
                i = k + 1;
                if (RawText.Contains(name) && !closes)
                {
                    int stop = FindClose(s, name, i);
                    o.Append(s, i, stop - i); i = stop;
                }
            }
            return o.ToString();
        }

        /// `</name` followed by a non-word character, ignoring case; the end of the text if none.
        private static int FindClose(string s, string name, int from)
        {
            var needle = "</" + name;
            int i = from;
            while (true)
            {
                int at = s.IndexOf(needle, i, System.StringComparison.OrdinalIgnoreCase);
                if (at < 0) return s.Length;
                int after = at + needle.Length;
                if (after == s.Length || !IsWord(s[after])) return at;
                i = at + 1;
            }
        }
    }

    internal static class JsTransforms
    {
        private static readonly Regex Directive = new Regex(
            "((?:^|[{};>])(?:\\s|/\\*[\\s\\S]*?\\*/|//[^\\n]*\\n)*)([\"'])use strict\\2");

        /// `"use strict"` does nothing, as on the device (WebKit 534.6 predates strict mode).
        public static string SloppyMode(string js)
        {
            if (!js.Contains("use strict")) return js;
            return Directive.Replace(js, "$1$2not strict$2");
        }
    }

    internal static class CssTransforms
    {
        private static readonly Regex Rule = new Regex("\\{[^{}]*\\}");
        private static readonly Regex BorderImage = new Regex("-webkit-border-image\\s*:(?!\\s*none)", RegexOptions.IgnoreCase);
        private static readonly Regex MouseTarget = new Regex("-webkit-palm-mouse-target\\s*:\\s*ignore", RegexOptions.IgnoreCase);
        private static readonly Regex SelectorRule = new Regex("([^{}]+)\\{([^{}]*)\\}");

        public static string Apply(string css) { return MouseTargetIgnore(BorderImageNeedsStyle(css)); }

        /// webOS's `-webkit-palm-mouse-target: ignore` as `pointer-events: none` on the element alone.
        public static string MouseTargetIgnore(string css)
        {
            return SelectorRule.Replace(css, m =>
            {
                var body = m.Groups[2].Value;
                if (!MouseTarget.IsMatch(body)) return m.Value;
                var children = string.Join(",", m.Groups[1].Value.Split(',').Select(x => x.Trim() + " > *"));
                var trimmed = body.TrimEnd();
                if (trimmed.EndsWith(";")) trimmed = trimmed.Substring(0, trimmed.Length - 1);
                return m.Groups[1].Value + "{" + trimmed + ";pointer-events:none}" + children + "{pointer-events:auto}";
            });
        }

        /// 2011 WebKit drew -webkit-border-image whatever the border-style.
        public static string BorderImageNeedsStyle(string css)
        {
            return Rule.Replace(css, m =>
                BorderImage.IsMatch(m.Value) ? m.Value.Substring(0, m.Value.Length - 1) + ";border-style:solid;border-color:transparent}" : m.Value);
        }
    }
}
