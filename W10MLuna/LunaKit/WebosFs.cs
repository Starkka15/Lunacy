using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace LunaKit
{
    /// The webOS root filesystem as JS services see it (AndroidLuna's WebosRoot, without links
    /// or a shell): absolute webOS paths, in layers. A read looks in the writable layer first
    /// (LocalState\webos: what services and packages wrote), then the bundled apps at their
    /// webOS path, then the ROM (webos\rootfs, read-only in the package). A write always goes to
    /// the writable layer, copying a ROM file up first when it is appended to. Deleting a file
    /// only the ROM has is refused, as a read-only filesystem refuses it.
    ///
    /// The operations answer Node's way, for node.js's fs: an error is {err: code, msg}, with
    /// code ENOENT, EEXIST, ENOTDIR, EISDIR, ENOTEMPTY or EROFS.
    internal sealed class WebosFs
    {
        private readonly string writable, rom;
        private readonly AppRegistry apps;
        /// The phone's libraries under /media/internal (UserFiles.cs); null until they are found.
        public UserFiles User;
        private const string AppsDir = "/media/cryptofs/apps/usr/palm/applications/";

        public WebosFs(string writable, string rom, AppRegistry apps)
        {
            this.writable = writable; this.rom = rom; this.apps = apps;
            Directory.CreateDirectory(writable);
            // The folders every TouchPad has, which services write into without making them.
            foreach (var d in new[] { "media/internal", "tmp", "var/tmp", "var/run", "var/log", "var/palm", "var/luna/preferences",
                "var/luna/data", "var/preferences", "var/file-cache", "home/root", "media/cryptofs/apps/usr/palm/services" })
                Directory.CreateDirectory(Path.Combine(writable, d.Replace('/', '\\')));
        }

        public sealed class FsError : Exception
        {
            public readonly string Code;
            public FsError(string code, string msg) : base(msg) { Code = code; }
        }

        /// A webOS path made absolute and normal: no ".", "..", or doubled slashes.
        public static string Normal(string p)
        {
            var parts = new List<string>();
            foreach (var s in (p ?? "").Replace('\\', '/').Split('/'))
            {
                if (s.Length == 0 || s == ".") continue;
                if (s == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
                parts.Add(s);
            }
            return "/" + string.Join("/", parts);
        }

        private string InWritable(string p) { return Path.Combine(writable, p.TrimStart('/').Replace('/', '\\')); }
        private string InRom(string p) { return Path.Combine(rom, p.TrimStart('/').Replace('/', '\\')); }

        /// A bundled or installed app's folder for a path under the applications folder.
        private string InApps(string p)
        {
            if (!p.StartsWith(AppsDir, StringComparison.Ordinal) && p + "/" != AppsDir) return null;
            var rest = p.Length > AppsDir.Length ? p.Substring(AppsDir.Length) : "";
            if (rest.Length == 0) return null;
            int slash = rest.IndexOf('/');
            var info = apps.Get(slash < 0 ? rest : rest.Substring(0, slash));
            if (info == null) return null;
            return slash < 0 ? info.Dir : Path.Combine(info.Dir, rest.Substring(slash + 1).Replace('/', '\\'));
        }

        /// Where a path's file or folder is for reading, or null.
        public string Find(string p)
        {
            p = Normal(p);
            var w = InWritable(p);
            if (File.Exists(w) || Directory.Exists(w)) return w;
            var a = InApps(p);
            if (a != null && (File.Exists(a) || Directory.Exists(a))) return a;
            var r = InRom(p);
            if (File.Exists(r) || Directory.Exists(r)) return r;
            return null;
        }

        private string Must(string p)
        {
            var f = Find(p);
            if (f == null) throw new FsError("ENOENT", "ENOENT: no such file or directory, '" + p + "'");
            return f;
        }

        private string ForWrite(string p)
        {
            p = Normal(p);
            var w = InWritable(p);
            var parent = Path.GetDirectoryName(w);
            if (!Directory.Exists(parent))
            {
                // A parent the ROM or an app has is made in the writable layer, as the file
                // would have gone beside it.
                var pp = Normal(p + "/..");
                if (Find(pp) == null) throw new FsError("ENOENT", "ENOENT: no such file or directory, '" + p + "'");
                Directory.CreateDirectory(parent);
            }
            return w;
        }

        // ---- operations ----

        /// A path into one of the phone's libraries: the library and the path inside it.
        private bool Lib(string p, out Windows.Storage.StorageFolder lib, out string rest)
        {
            lib = null; rest = null;
            return User != null && User.InLibrary(p, out lib, out rest);
        }

        private Windows.Storage.IStorageItem LibMust(Windows.Storage.StorageFolder lib, string rest, string p)
        {
            var item = UserFiles.Item(lib, rest);
            if (item == null) throw new FsError("ENOENT", "ENOENT: no such file or directory, '" + Normal(p) + "'");
            return item;
        }

        public JObj Stat(string p)
        {
            Windows.Storage.StorageFolder lib; string rest;
            if (Lib(p, out lib, out rest)) return UserFiles.Stat(LibMust(lib, rest, p));
            var f = Must(p);
            bool dir = Directory.Exists(f);
            var info = dir ? (FileSystemInfo)new DirectoryInfo(f) : new FileInfo(f);
            long mtime = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds();
            long ctime = new DateTimeOffset(info.CreationTimeUtc).ToUnixTimeMilliseconds();
            return new JObj().Put("size", dir ? 4096 : ((FileInfo)info).Length).Put("mtime", mtime).Put("ctime", ctime).Put("atime", mtime)
                .Put("mode", dir ? 0x41ED : 0x81A4).Put("dir", dir);
        }

        public bool Exists(string p)
        {
            Windows.Storage.StorageFolder lib; string rest;
            if (Lib(p, out lib, out rest)) return UserFiles.Item(lib, rest) != null;
            return Find(p) != null;
        }

        /// Whether a path is a folder, wherever it is.
        public bool IsDir(string p) { return Exists(p) && Stat(p).Bool("dir"); }

        public byte[] Read(string p)
        {
            Windows.Storage.StorageFolder lib; string rest;
            if (Lib(p, out lib, out rest))
            {
                var item = LibMust(lib, rest, p);
                var file = item as Windows.Storage.StorageFile;
                if (file == null) throw new FsError("EISDIR", "EISDIR: illegal operation on a directory, read");
                return UserFiles.Read(file);
            }
            var f = Must(p);
            if (Directory.Exists(f)) throw new FsError("EISDIR", "EISDIR: illegal operation on a directory, read");
            using (var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var b = new byte[s.Length];
                int n = 0;
                while (n < b.Length) { int r = s.Read(b, n, b.Length - n); if (r <= 0) break; n += r; }
                return b;
            }
        }

        public void Write(string p, byte[] data, bool append)
        {
            var np = Normal(p);
            Windows.Storage.StorageFolder lib; string rest;
            if (Lib(np, out lib, out rest)) { UserFiles.Write(lib, rest, np, data, append); return; }
            var w = ForWrite(np);
            if (Directory.Exists(w)) throw new FsError("EISDIR", "EISDIR: illegal operation on a directory, open '" + np + "'");
            if (append && !File.Exists(w))
            {
                var below = Find(np);
                if (below != null && File.Exists(below)) File.Copy(below, w);
            }
            using (var s = new FileStream(w, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                s.Write(data, 0, data.Length);
        }

        /// A file opened for writing from the start, for what arrives in pieces (a download).
        public Stream OpenWrite(string p)
        {
            var np = Normal(p);
            Windows.Storage.StorageFolder lib; string rest;
            if (Lib(np, out lib, out rest))
            {
                UserFiles.Write(lib, rest, np, new byte[0], false);
                var f = LibMust(lib, rest, np) as Windows.Storage.StorageFile;
                return f.OpenStreamForWriteAsync().ConfigureAwait(false).GetAwaiter().GetResult();
            }
            var w = ForWrite(np);
            if (Directory.Exists(w)) throw new FsError("EISDIR", "EISDIR: illegal operation on a directory, open '" + np + "'");
            return new FileStream(w, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        }

        public List<string> List(string p)
        {
            var np = Normal(p);
            Windows.Storage.StorageFolder lib; string rest;
            if (Lib(np, out lib, out rest))
            {
                var dir = LibMust(lib, rest, np) as Windows.Storage.StorageFolder;
                if (dir == null) throw new FsError("ENOTDIR", "ENOTDIR: not a directory, scandir '" + np + "'");
                return UserFiles.List(dir);
            }
            var names = new SortedSet<string>(StringComparer.Ordinal);
            bool any = false;
            // /media/internal lists the libraries mapped into it beside its own folders.
            if (np == UserFiles.Root && User != null) foreach (var m in User.Mapped) names.Add(m.Key);
            foreach (var dir in new[] { InWritable(np), InApps(np), InRom(np) })
            {
                if (dir == null) continue;
                if (File.Exists(dir) && !any) throw new FsError("ENOTDIR", "ENOTDIR: not a directory, scandir '" + np + "'");
                if (!Directory.Exists(dir)) continue;
                any = true;
                foreach (var e in Directory.EnumerateFileSystemEntries(dir)) names.Add(Path.GetFileName(e));
            }
            // The applications folder lists every app, bundled or installed.
            if (np + "/" == AppsDir) { any = true; foreach (var a in apps.All()) names.Add(a.Id); }
            if (!any) throw new FsError("ENOENT", "ENOENT: no such file or directory, scandir '" + np + "'");
            return names.ToList();
        }

        public void MakeDir(string p, bool recursive)
        {
            var np = Normal(p);
            Windows.Storage.StorageFolder lib; string rest;
            if (Lib(np, out lib, out rest))
            {
                if (rest.Length == 0) { if (recursive) return; throw new FsError("EEXIST", "EEXIST: file already exists, mkdir '" + np + "'"); }
                UserFiles.MakeDir(lib, rest, np, recursive);
                return;
            }
            if (Find(np) != null)
            {
                if (recursive) return;
                throw new FsError("EEXIST", "EEXIST: file already exists, mkdir '" + np + "'");
            }
            if (!recursive && Find(Normal(np + "/..")) == null) throw new FsError("ENOENT", "ENOENT: no such file or directory, mkdir '" + np + "'");
            Directory.CreateDirectory(InWritable(np));
        }

        public void Remove(string p, bool dir, bool recursive)
        {
            var np = Normal(p);
            Windows.Storage.StorageFolder lib; string rest;
            if (Lib(np, out lib, out rest))
            {
                // The library itself can't go, as /media/internal's own folders couldn't on a device.
                if (rest.Length == 0) throw new FsError("EBUSY", "EBUSY: resource busy or locked, '" + np + "'");
                var item = UserFiles.Item(lib, rest);
                if (item == null) { if (recursive) return; throw new FsError("ENOENT", "ENOENT: no such file or directory, '" + np + "'"); }
                UserFiles.Remove(item, np, dir, recursive);
                return;
            }
            var w = InWritable(np);
            bool here = File.Exists(w) || Directory.Exists(w);
            if (!here)
            {
                if (Find(np) == null) { if (recursive) return; throw new FsError("ENOENT", "ENOENT: no such file or directory, '" + np + "'"); }
                throw new FsError("EROFS", "EROFS: read-only file system, '" + np + "'");
            }
            if (Directory.Exists(w))
            {
                if (!dir) throw new FsError("EISDIR", "EISDIR: illegal operation on a directory, unlink '" + np + "'");
                if (!recursive && Directory.EnumerateFileSystemEntries(w).Any()) throw new FsError("ENOTEMPTY", "ENOTEMPTY: directory not empty, rmdir '" + np + "'");
                Directory.Delete(w, recursive);
            }
            else
            {
                if (dir && !recursive) throw new FsError("ENOTDIR", "ENOTDIR: not a directory, rmdir '" + np + "'");
                File.Delete(w);
            }
        }

        public void Rename(string from, string to)
        {
            var nf = Normal(from); var nt = Normal(to);
            Windows.Storage.StorageFolder libF, libT; string restF, restT;
            bool inF = Lib(nf, out libF, out restF), inT = Lib(nt, out libT, out restT);
            if (inF || inT)
            {
                if (inF && inT && libF == libT && restF.Length > 0 && restT.Length > 0)
                {
                    UserFiles.Rename(LibMust(libF, restF, nf), libT, restT, nt);
                    return;
                }
                // Between Lunacy's tree and a library, or two libraries: a copy, then the original goes.
                if (IsDir(nf)) throw new FsError("EXDEV", "EXDEV: cross-device link not permitted, rename '" + nf + "' -> '" + nt + "'");
                Write(nt, Read(nf), false);
                Remove(nf, false, false);
                return;
            }
            var src = InWritable(nf);
            if (!File.Exists(src) && !Directory.Exists(src))
            {
                if (Find(nf) == null) throw new FsError("ENOENT", "ENOENT: no such file or directory, rename '" + nf + "'");
                throw new FsError("EROFS", "EROFS: read-only file system, rename '" + nf + "'");
            }
            var dst = ForWrite(nt);
            if (File.Exists(src))
            {
                // rename over an existing file replaces it, as POSIX's does.
                if (File.Exists(dst)) File.Delete(dst);
                File.Move(src, dst);
            }
            else
            {
                if (Directory.Exists(dst)) { if (Directory.EnumerateFileSystemEntries(dst).Any()) throw new FsError("ENOTEMPTY", "ENOTEMPTY: directory not empty, rename '" + nt + "'"); Directory.Delete(dst); }
                Directory.Move(src, dst);
            }
        }

        public void Copy(string from, string to)
        {
            Write(to, Read(from), false);
        }

        /// Copies a file or folder (cp -r).
        public void CopyTree(string from, string to)
        {
            var nf = Normal(from);
            if (!Exists(nf)) throw new FsError("ENOENT", "ENOENT: no such file or directory, '" + nf + "'");
            if (!IsDir(nf)) { Copy(nf, to); return; }
            MakeDir(to, true);
            foreach (var n in List(nf)) CopyTree(nf + "/" + n, Normal(to) + "/" + n);
        }

        /// One operation from node.js's fs, as JSON in and out.
        public string Op(string op, string args)
        {
            try
            {
                var a = Jx.ObjOrEmpty(args);
                var p = a.Str("p");
                switch (op)
                {
                    case "stat": return Ok(Stat(p));
                    case "exists": return Ok(new JObj().Put("yes", Exists(p)));
                    case "read":
                        {
                            var b = Read(p);
                            return Ok(a.Str("enc") == "utf8" ? new JObj().Put("text", Utf8(b)) : new JObj().Put("b64", Convert.ToBase64String(b)));
                        }
                    case "write":
                        {
                            var data = a.Has("b64") ? Convert.FromBase64String(a.Str("b64")) : Encoding.UTF8.GetBytes(a.Str("text"));
                            Write(p, data, a.Bool("append"));
                            return Ok(new JObj());
                        }
                    case "list": return Ok(new JObj().Put("names", new JArr(List(p).Cast<object>())));
                    case "mkdir": MakeDir(p, a.Bool("recursive")); return Ok(new JObj());
                    case "unlink": Remove(p, false, false); return Ok(new JObj());
                    case "rmdir": Remove(p, true, a.Bool("recursive")); return Ok(new JObj());
                    case "rename": Rename(a.Str("from"), a.Str("to")); return Ok(new JObj());
                    case "copy": Copy(a.Str("from"), a.Str("to")); return Ok(new JObj());
                    case "copyTree": CopyTree(a.Str("from"), a.Str("to")); return Ok(new JObj());
                    default: return new JObj().Put("err", "ENOSYS").Put("msg", "ENOSYS: function not implemented, " + op).ToString();
                }
            }
            catch (FsError e) { return new JObj().Put("err", e.Code).Put("msg", e.Message).ToString(); }
            catch (UnauthorizedAccessException e) { return new JObj().Put("err", "EACCES").Put("msg", "EACCES: permission denied, " + e.Message).ToString(); }
            catch (IOException e) { return new JObj().Put("err", "EIO").Put("msg", "EIO: i/o error, " + e.Message).ToString(); }
        }

        private static string Ok(JObj o) { return o.Put("ok", true).ToString(); }

        private static string Utf8(byte[] b)
        {
            int start = b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF ? 3 : 0;
            return Encoding.UTF8.GetString(b, start, b.Length - start);
        }
    }
}
