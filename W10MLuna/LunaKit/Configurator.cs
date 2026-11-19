using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LunaKit
{
    /// What webOS's configurator did for the system and installed packages (AndroidLuna's
    /// Configurator.kt): every app and service folder's configuration/ files. The files in
    /// db/kinds (putKind parameters) and db/permissions (putPermissions arrays) go to db8, and
    /// tempdb/ likewise to tempdb. Runs at startup and after each install; registering again
    /// replaces. Bundled apps count too: Palm's Clock ships its alarm and preference kinds that way.
    ///
    /// Not here yet: activities/ (waits for the activity manager) and filecache types (the file
    /// cache is one of the TouchPad's native services, for the PDK runtime).
    internal sealed class Configurator
    {
        private readonly AppRegistry apps;
        private readonly Db8 db, tempdb;
        private readonly string rom;
        private readonly Bus bus;

        /// `rom`: the webOS root's read-only part (webos\rootfs), with /etc/palm and /usr/palm/services.
        public Configurator(AppRegistry apps, Db8 db, Db8 tempdb, string rom, Bus bus)
        {
            this.apps = apps; this.db = db; this.tempdb = tempdb; this.rom = rom; this.bus = bus;
        }

        /// Each phase in the order webOS's configurator kept.
        public void Run()
        {
            SystemKinds();
            AppKinds();
            SystemOwnedKinds();
            CarrierDefaults();
            ServiceKinds();
        }

        /// The system's own kinds first, as the device's configurator read /etc/palm. Its
        /// permissions wait for db_kinds (SystemOwnedKinds), since one may name a kind from there.
        private void SystemKinds()
        {
            ConfigureFiles(Path.Combine(rom, "etc", "palm", "db"), db, "/etc/palm/db", false);
            ConfigureFiles(Path.Combine(rom, "etc", "palm", "tempdb"), tempdb, "/etc/palm/tempdb", true);
        }

        private void AppKinds()
        {
            foreach (var a in apps.All())
            {
                Configure(Path.Combine(a.Dir, "configuration", "db"), db, a.Id);
                Configure(Path.Combine(a.Dir, "configuration", "tempdb"), tempdb, a.Id);
            }
        }

        /// /etc/palm/db_kinds, the kinds the native services own, after the apps (they extend
        /// kinds an app owns); then /etc/palm/db's permissions.
        private void SystemOwnedKinds()
        {
            var kinds = ParseFiles(Path.Combine(rom, "etc", "palm", "db_kinds"), "/etc/palm/db_kinds", (t, o) => Jx.ParseObj(t));
            var permissions = ParseFiles(Path.Combine(rom, "etc", "palm", "db", "permissions"), "/etc/palm/db", (t, o) => (JArr)Jx.Parse(t));
            if (kinds.Count > 0 || permissions.Count > 0) db.Configure(kinds, permissions);
        }

        /// Services' kinds: the ROM's /usr/palm/services, and installed packages' services.
        private void ServiceKinds()
        {
            var roots = new[] { Path.Combine(rom, "usr", "palm", "services"), Path.Combine(apps.InstalledDir, "..", "services") };
            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;
                foreach (var dir in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
                {
                    var cfg = Path.Combine(dir, "configuration");
                    if (!Directory.Exists(cfg)) continue;
                    var name = Path.GetFileName(dir);
                    ConfigureFiles(Path.Combine(cfg, "db"), db, name, true);
                    ConfigureFiles(Path.Combine(cfg, "tempdb"), tempdb, name, true);
                }
            }
        }

        /// Email's carrier defaults, as webOS's customization service wrote them: Lunacy's
        /// signature (codepoet's exception to rule 0, Docs/luna-deltas.md).
        private void CarrierDefaults()
        {
            if (apps.Get(Email) == null) return;
            var sig = new JObj().Put("_id", "lunacy-email-carrier-defaults").Put("_kind", Email + ".carrier_defaults:1")
                .Put("defaultSignature", "-- Sent from Lunacy");
            // After the kinds above are in: db8 works in order.
            db.After(() => bus.Dispatch(Customization, "palm://com.palm.db/merge", new JObj().Put("objects", new JArr().Put(sig)).ToString(), reply =>
            {
                if (!Jx.ObjOrEmpty(reply).Bool("returnValue")) LunaLog.Write("[configurator] carrier defaults: " + reply);
            }));
        }

        private void Configure(string dir, Db8 target, string owner)
        {
            var kinds = Read(Path.Combine(dir, "kinds"), owner, t => Jx.ParseObj(t));
            var permissions = Read(Path.Combine(dir, "permissions"), owner, t => (JArr)Jx.Parse(t));
            if (kinds.Count > 0 || permissions.Count > 0) target.Configure(kinds, permissions);
        }

        private static List<T> Read<T>(string dir, string owner, Func<string, T> parse)
        {
            var out_ = new List<T>();
            if (!Directory.Exists(dir)) return out_;
            foreach (var f in Directory.GetFiles(dir).OrderBy(x => x, StringComparer.Ordinal))
            {
                try { out_.Add(parse(File.ReadAllText(f))); }
                catch (Exception e) { LunaLog.Write("[configurator] " + owner + "/" + Path.GetFileName(f) + " unreadable: " + e.Message); }
            }
            return out_;
        }

        private void ConfigureFiles(string dir, Db8 target, string pkg, bool withPermissions)
        {
            var kinds = ParseFiles(Path.Combine(dir, "kinds"), pkg, (t, owner) =>
            {
                var k = Jx.ParseObj(t);
                if (owner != null && !k.Has("owner")) k.Put("owner", owner);
                return k;
            });
            var permissions = withPermissions ? ParseFiles(Path.Combine(dir, "permissions"), pkg, (t, o) => (JArr)Jx.Parse(t)) : new List<JArr>();
            if (kinds.Count > 0 || permissions.Count > 0) target.Configure(kinds, permissions);
        }

        /// The files in dir, and in its folders one level down. /etc/palm/db/kinds keeps a
        /// service's kinds in a folder named for it, and those kinds name no owner: the folder is.
        private static List<T> ParseFiles<T>(string dir, string pkg, Func<string, string, T> parse)
        {
            var out_ = new List<T>();
            if (!Directory.Exists(dir)) return out_;
            var files = Directory.GetFiles(dir).OrderBy(x => x, StringComparer.Ordinal).Select(f => new KeyValuePair<string, string>(f, null))
                .Concat(Directory.GetDirectories(dir).OrderBy(x => x, StringComparer.Ordinal).SelectMany(d =>
                    Directory.GetFiles(d).OrderBy(x => x, StringComparer.Ordinal).Select(f => new KeyValuePair<string, string>(f, Path.GetFileName(d)))));
            foreach (var f in files)
            {
                try { out_.Add(parse(File.ReadAllText(f.Key), f.Value)); }
                catch (Exception e) { LunaLog.Write("[configurator] " + pkg + "/" + Path.GetFileName(f.Key) + " unreadable: " + e.Message); }
            }
            return out_;
        }

        private const string Customization = "com.palm.service.customization";
        private const string Email = "com.palm.app.email";
    }
}
