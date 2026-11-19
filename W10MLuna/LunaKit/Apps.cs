using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Windows.Data.Json;

namespace LunaKit
{
    /// One installed webOS app, from its appinfo.json (AndroidLuna's AppRegistry.kt).
    internal sealed class AppInfo
    {
        public string Id, Title, Main, Icon, Version, Vendor, Dir, Keywords;
        public bool Visible, NoWindow, Removable;
        public int UiRevision;
        public JsonObject Raw;

        /// What the shell and applicationManager/listApps hand out: appinfo.json's own fields,
        /// as webOS listed them, with the icon and the main page as paths on the app's origin.
        public JsonObject Listed()
        {
            var o = JsonObject.Parse(Raw.Stringify());
            o["id"] = JsonValue.CreateStringValue(Id);
            o["title"] = JsonValue.CreateStringValue(Title);
            o["icon"] = JsonValue.CreateStringValue(Url(Icon));
            o["main"] = JsonValue.CreateStringValue(Url(Main));
            o["visible"] = JsonValue.CreateBooleanValue(Visible);
            o["removable"] = JsonValue.CreateBooleanValue(Removable);
            o["appmenu"] = JsonValue.CreateStringValue(Title);
            o["launchPointId"] = JsonValue.CreateStringValue(Id + "_default");
            return o;
        }

        public string Url(string relative) { return AppServer.PagePath(Id, relative ?? ""); }
    }

    /// The apps the shell knows: those that ship with it (the package's webos\apps, which can't
    /// be removed, as webOS's ROM apps couldn't) and those installed since (LocalState\apps,
    /// AndroidLuna's files/apps). An installed app with a bundled app's id wins, as an update
    /// in /media/cryptofs did over the ROM's copy.
    internal sealed class AppRegistry
    {
        private readonly string bundled, installed;
        private Dictionary<string, AppInfo> byId = new Dictionary<string, AppInfo>(StringComparer.Ordinal);
        private readonly object gate = new object();

        public AppRegistry(string bundled, string installed)
        {
            this.bundled = bundled;
            this.installed = installed;
        }

        public string InstalledDir { get { return installed; } }

        public void Scan()
        {
            var found = new Dictionary<string, AppInfo>(StringComparer.Ordinal);
            foreach (var a in Read(bundled, false)) found[a.Id] = a;
            foreach (var a in Read(installed, true)) found[a.Id] = a;
            lock (gate) byId = found;
            LunaLog.Write("[LunaKit] apps: " + found.Count + " (" + string.Join(", ", found.Keys.OrderBy(k => k)) + ")");
        }

        public AppInfo Get(string id)
        {
            if (id == null) return null;
            AppInfo a;
            lock (gate) return byId.TryGetValue(id, out a) ? a : null;
        }

        public List<AppInfo> All()
        {
            lock (gate) return byId.Values.ToList();
        }

        /// The list the shell's launcher draws from.
        public string Json()
        {
            var arr = new JsonArray();
            foreach (var a in All().OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase)) arr.Add(a.Listed());
            return arr.Stringify();
        }

        private static IEnumerable<AppInfo> Read(string root, bool removable)
        {
            if (root == null || !Directory.Exists(root)) yield break;
            foreach (var dir in Directory.GetDirectories(root))
            {
                var f = Path.Combine(dir, "appinfo.json");
                if (!File.Exists(f)) continue;
                AppInfo a = null;
                try
                {
                    var o = J.ParseObject(File.ReadAllText(f).TrimStart('﻿'));
                    a = new AppInfo
                    {
                        Id = o.GetString("id", Path.GetFileName(dir)),
                        Title = o.GetString("title", Path.GetFileName(dir)),
                        Main = o.GetString("main", "index.html"),
                        Icon = o.GetString("icon", "icon.png"),
                        Version = o.GetString("version", "1.0.0"),
                        Vendor = o.GetString("vendor", ""),
                        Keywords = o.ContainsKey("keywords") && o["keywords"].ValueType == JsonValueType.Array ? o["keywords"].Stringify() : "[]",
                        Visible = !o.ContainsKey("visible") || o.GetBool("visible"),
                        NoWindow = o.GetBool("noWindow"),
                        UiRevision = o.GetInt("uiRevision", 1),
                        Removable = removable,
                        Dir = Path.GetFullPath(dir),
                        Raw = o,
                    };
                }
                catch (Exception e) { LunaLog.Write("[LunaKit] " + f + " unreadable: " + e.Message); }
                if (a != null) yield return a;
            }
        }
    }
}
