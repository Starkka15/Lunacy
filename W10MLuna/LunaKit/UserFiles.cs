using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.AccessCache;
using Windows.Storage.FileProperties;

namespace LunaKit
{
    /// /media/internal, webOS's user storage (LunaCore's UserFiles.kt, AndroidFolders.kt).
    ///
    /// Lunacy keeps its own copy of the tree (JS services write there, the shipped wallpapers
    /// land there), but the files a person has are in the phone's own libraries, so the folders
    /// a webOS device had are mapped onto them:
    ///
    ///   photos -> Pictures       music -> Music          video -> Videos
    ///   camera -> Camera Roll    documents -> Documents   downloads -> Downloads\Lunacy
    ///   wallpapers -> Pictures\webOS Wallpapers
    ///
    /// As on Android, a first segment that Lunacy's own tree already has is Lunacy's; any other
    /// mapped name resolves into the library. ringtones stays in Lunacy's own tree: Windows 10
    /// Mobile gives an app no way into the phone's ringtones folder.
    ///
    /// A UWP app on 15063 can't open library files with System.IO (that came with 1803's
    /// broadFileSystemAccess), so every operation here is the StorageFolder API, waited on.
    /// The waits are on the caller's thread; WinRT's storage operations complete on the thread
    /// pool, so a wait on the UI thread blocks it for the operation and no longer.
    internal sealed class UserFiles
    {
        public const string Root = "/media/internal";
        private readonly Dictionary<string, StorageFolder> mapped = new Dictionary<string, StorageFolder>(StringComparer.Ordinal);
        private readonly string own;
        /// Lunacy's own /media/internal.
        public string Own { get { return own; } }

        /// `own`: Lunacy's own /media/internal (LocalState\webos\media\internal).
        public UserFiles(string own) { this.own = own; }

        /// The mapped folders this phone actually has, by webOS name.
        public IEnumerable<KeyValuePair<string, StorageFolder>> Mapped { get { lock (mapped) return mapped.ToList(); } }

        /// Finds the libraries. Once, at start, off the UI thread.
        public async Task Open()
        {
            await Add("photos", () => Task.FromResult(KnownFolders.PicturesLibrary));
            await Add("music", () => Task.FromResult(KnownFolders.MusicLibrary));
            await Add("video", () => Task.FromResult(KnownFolders.VideosLibrary));
            await Add("camera", () => Task.FromResult(KnownFolders.CameraRoll));
            await Add("documents", () => Task.FromResult(KnownFolders.DocumentsLibrary));
            await Add("wallpapers", async () => await KnownFolders.PicturesLibrary.CreateFolderAsync("webOS Wallpapers", CreationCollisionOption.OpenIfExists));
            await Add("downloads", Downloads);
            // An empty folder of a mapped name left in Lunacy's own tree would hide the library
            // (Luna DE's Main.kt does the same).
            foreach (var name in Mapped.Select(m => m.Key))
            {
                var d = Path.Combine(own, name);
                try { if (Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch (Exception) { }
            }
            LunaLog.Write("[userfiles] /media/internal: " + string.Join(", ", Mapped.Select(m => m.Key + " = " + m.Value.Path)));
        }

        private async Task Add(string name, Func<Task<StorageFolder>> get)
        {
            try
            {
                var f = await get();
                if (f != null) lock (mapped) mapped[name] = f;
            }
            catch (Exception e) { LunaLog.Write("[userfiles] no " + name + " folder: " + e.Message); }
        }

        /// Windows lets an app make one folder of its own in Downloads, once; it is kept by token.
        private static async Task<StorageFolder> Downloads()
        {
            const string Token = "downloads";
            if (StorageApplicationPermissions.FutureAccessList.ContainsItem(Token))
            {
                try { return await StorageApplicationPermissions.FutureAccessList.GetFolderAsync(Token); }
                catch (Exception) { StorageApplicationPermissions.FutureAccessList.Remove(Token); }
            }
            var f = await Windows.Storage.DownloadsFolder.CreateFolderAsync("Lunacy", CreationCollisionOption.GenerateUniqueName);
            StorageApplicationPermissions.FutureAccessList.AddOrReplace(Token, f);
            return f;
        }

        // ---- resolving ----

        /// A path under /media/internal that names a mapped folder Lunacy's own tree hasn't got:
        /// the library and the path inside it ("" for the folder itself). False for anything else.
        public bool InLibrary(string webosPath, out StorageFolder library, out string rest)
        {
            library = null; rest = null;
            var p = WebosFs.Normal(webosPath);
            if (!p.StartsWith(Root + "/", StringComparison.Ordinal)) return false;
            var rel = p.Substring(Root.Length + 1);
            int slash = rel.IndexOf('/');
            var first = slash < 0 ? rel : rel.Substring(0, slash);
            if (Directory.Exists(Path.Combine(own, first)) || File.Exists(Path.Combine(own, first))) return false;
            lock (mapped) if (!mapped.TryGetValue(first, out library)) return false;
            rest = slash < 0 ? "" : rel.Substring(slash + 1);
            return true;
        }

        /// The webOS path of a library file, or null: the most specific mapped folder, so a
        /// wallpaper is /media/internal/wallpapers/x, not photos/webOS Wallpapers/x.
        public string WebosPath(string windowsPath)
        {
            foreach (var m in Mapped.OrderByDescending(x => x.Value.Path.Length))
            {
                var root = m.Value.Path.TrimEnd('\\');
                if (root.Length == 0) continue;
                if (string.Equals(windowsPath, root, StringComparison.OrdinalIgnoreCase)) return Root + "/" + m.Key;
                if (windowsPath.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
                    return Root + "/" + m.Key + "/" + windowsPath.Substring(root.Length + 1).Replace('\\', '/');
            }
            return null;
        }

        private static T Wait<T>(Windows.Foundation.IAsyncOperation<T> op) { return op.AsTask().GetAwaiter().GetResult(); }
        private static void Wait(Windows.Foundation.IAsyncAction op) { op.AsTask().GetAwaiter().GetResult(); }

        /// The item at `rest` in a library, or null. Each segment is looked up by name.
        public static IStorageItem Item(StorageFolder library, string rest)
        {
            IStorageItem cur = library;
            foreach (var seg in (rest ?? "").Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var dir = cur as StorageFolder;
                if (dir == null) return null;
                cur = Wait(dir.TryGetItemAsync(seg));
                if (cur == null) return null;
            }
            return cur;
        }

        private static StorageFolder Parent(StorageFolder library, string rest, out string name)
        {
            var parts = (rest ?? "").Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            name = parts.Length == 0 ? "" : parts[parts.Length - 1];
            return Item(library, string.Join("/", parts.Take(Math.Max(0, parts.Length - 1)))) as StorageFolder;
        }

        // ---- operations, for WebosFs ----

        public static JObj Stat(IStorageItem item)
        {
            bool dir = item is StorageFolder;
            long size = 0, mtime;
            BasicProperties props = null;
            try { props = Wait(item.GetBasicPropertiesAsync()); } catch (Exception) { }
            if (!dir && props != null) size = (long)props.Size;
            mtime = (props != null ? props.DateModified : item.DateCreated).ToUnixTimeMilliseconds();
            long ctime = item.DateCreated.ToUnixTimeMilliseconds();
            return new JObj().Put("size", dir ? 4096 : size).Put("mtime", mtime).Put("ctime", ctime).Put("atime", mtime)
                .Put("mode", dir ? 0x41ED : 0x81A4).Put("dir", dir);
        }

        public static byte[] Read(StorageFile f)
        {
            var buf = Wait(FileIO.ReadBufferAsync(f));
            return buf.ToArray();
        }

        public static void Write(StorageFolder library, string rest, string webosPath, byte[] data, bool append)
        {
            string name;
            var parent = Parent(library, rest, out name);
            if (parent == null || name.Length == 0) throw new WebosFs.FsError("ENOENT", "ENOENT: no such file or directory, '" + webosPath + "'");
            var existing = Wait(parent.TryGetItemAsync(name));
            if (existing is StorageFolder) throw new WebosFs.FsError("EISDIR", "EISDIR: illegal operation on a directory, open '" + webosPath + "'");
            var f = existing as StorageFile ?? Wait(parent.CreateFileAsync(name, CreationCollisionOption.OpenIfExists));
            if (append && existing != null)
            {
                var old = Read(f);
                var all = new byte[old.Length + data.Length];
                Buffer.BlockCopy(old, 0, all, 0, old.Length);
                Buffer.BlockCopy(data, 0, all, old.Length, data.Length);
                data = all;
            }
            Wait(FileIO.WriteBytesAsync(f, data));
        }

        public static List<string> List(StorageFolder dir)
        {
            return Wait(dir.GetItemsAsync()).Select(i => i.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        }

        public static void MakeDir(StorageFolder library, string rest, string webosPath, bool recursive)
        {
            IStorageItem cur = library;
            var parts = rest.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                var dir = cur as StorageFolder;
                if (dir == null) throw new WebosFs.FsError("ENOTDIR", "ENOTDIR: not a directory, mkdir '" + webosPath + "'");
                var next = Wait(dir.TryGetItemAsync(parts[i]));
                bool last = i == parts.Length - 1;
                if (next == null)
                {
                    if (!last && !recursive) throw new WebosFs.FsError("ENOENT", "ENOENT: no such file or directory, mkdir '" + webosPath + "'");
                    next = Wait(dir.CreateFolderAsync(parts[i], CreationCollisionOption.OpenIfExists));
                }
                else if (last && !recursive) throw new WebosFs.FsError("EEXIST", "EEXIST: file already exists, mkdir '" + webosPath + "'");
                cur = next;
            }
        }

        public static void Remove(IStorageItem item, string webosPath, bool dir, bool recursive)
        {
            var folder = item as StorageFolder;
            if (folder != null)
            {
                if (!dir) throw new WebosFs.FsError("EISDIR", "EISDIR: illegal operation on a directory, unlink '" + webosPath + "'");
                if (!recursive && Wait(folder.GetItemsAsync(0, 1)).Count > 0) throw new WebosFs.FsError("ENOTEMPTY", "ENOTEMPTY: directory not empty, rmdir '" + webosPath + "'");
            }
            else if (dir && !recursive) throw new WebosFs.FsError("ENOTDIR", "ENOTDIR: not a directory, rmdir '" + webosPath + "'");
            Wait(item.DeleteAsync(StorageDeleteOption.PermanentDelete));
        }

        /// A rename within one library: the same folder is a rename, another is a move.
        public static void Rename(IStorageItem item, StorageFolder library, string toRest, string webosTo)
        {
            string name;
            var parent = Parent(library, toRest, out name);
            if (parent == null || name.Length == 0) throw new WebosFs.FsError("ENOENT", "ENOENT: no such file or directory, rename '" + webosTo + "'");
            var file = item as StorageFile;
            if (file == null)
            {
                // Windows has no folder move; a folder is renamed in place only.
                var cur = Path.GetDirectoryName(item.Path);
                if (!string.Equals(cur, parent.Path, StringComparison.OrdinalIgnoreCase))
                    throw new WebosFs.FsError("EXDEV", "EXDEV: cross-device link not permitted, rename '" + webosTo + "'");
                Wait(item.RenameAsync(name, NameCollisionOption.FailIfExists));
                return;
            }
            if (string.Equals(Path.GetDirectoryName(file.Path), parent.Path, StringComparison.OrdinalIgnoreCase))
                Wait(file.RenameAsync(name, NameCollisionOption.ReplaceExisting));
            else
                Wait(file.MoveAsync(parent, name, NameCollisionOption.ReplaceExisting));
        }

        /// Writes a file into a library from outside it (a download landing, a copy from Lunacy's tree).
        public static StorageFile CreateFile(StorageFolder library, string rest, bool replace)
        {
            string name;
            var parent = Parent(library, rest, out name);
            if (parent == null || name.Length == 0) return null;
            return Wait(parent.CreateFileAsync(name, replace ? CreationCollisionOption.ReplaceExisting : CreationCollisionOption.OpenIfExists));
        }
    }
}
