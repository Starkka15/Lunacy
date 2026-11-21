using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.UI.StartScreen;

namespace LunaKit
{
    /// The apps the user has hidden from the launcher with Software Manager (HiddenApps.kt). A
    /// hidden app is still installed and still runs: another app can open it, and the bus lists
    /// it as before. Only the places the user starts apps from leave it out: the launcher's pages
    /// and the dock (and Just Type, once the shell has one). webOS had no such setting; this is
    /// Lunacy's, kept in the app's settings beside the launcher's.
    internal static class HiddenApps
    {
        private const string Key = "hiddenApps";
        private static HashSet<string> ids;

        public static event Action Changed;

        public static HashSet<string> All()
        {
            if (ids == null)
            {
                var set = new HashSet<string>();
                try
                {
                    var s = ApplicationData.Current.LocalSettings.Values[Key] as string;
                    if (!string.IsNullOrEmpty(s)) foreach (var id in s.Split('\n')) if (id.Length > 0) set.Add(id);
                }
                catch (Exception) { }
                ids = set;
            }
            return new HashSet<string>(ids);
        }

        public static bool Contains(string id) { return All().Contains(id); }

        public static void Set(string id, bool hidden)
        {
            var next = All();
            if (hidden ? !next.Add(id) : !next.Remove(id)) return;
            ids = next;
            try { ApplicationData.Current.LocalSettings.Values[Key] = string.Join("\n", next.OrderBy(x => x, StringComparer.Ordinal)); }
            catch (Exception e) { LunaLog.Write("[launcher] can't save hidden apps: " + e.Message); }
            var h = Changed;
            if (h != null) h();
        }
    }

    /// A webOS app on Windows' Start screen (Android's HomeScreenPins.kt): a secondary tile whose
    /// arguments name the app, so tapping it opens Lunacy straight into that app. Windows asks the
    /// user itself; only a refusal comes back as an error.
    internal static class StartTiles
    {
        public const string Prefix = "lunacy-app:";

        /// The app a tile's launch arguments name, or null.
        public static string AppOf(string arguments)
        {
            return arguments != null && arguments.StartsWith(Prefix, StringComparison.Ordinal) ? arguments.Substring(Prefix.Length) : null;
        }

        private static string TileId(string appId)
        {
            // A tile id is at most 64 letters, digits, '.' and '_'.
            var s = new string(appId.Select(ch => char.IsLetterOrDigit(ch) || ch == '.' ? ch : '_').ToArray());
            return s.Length > 64 ? s.Substring(0, 64) : s;
        }

        public static async Task<string> Pin(AppInfo app)
        {
            if (SecondaryTile.Exists(TileId(app.Id))) return null;
            Uri logo;
            try { logo = await TileLogo(app); }
            catch (Exception e) { return "Couldn't make " + app.Title + "'s tile: " + e.Message; }
            var tile = new SecondaryTile(TileId(app.Id), app.Title, Prefix + app.Id, logo, TileSize.Square150x150);
            tile.VisualElements.ShowNameOnSquare150x150Logo = true;
            bool ok;
            try { ok = await tile.RequestCreateAsync(); }
            catch (Exception e) { return "Windows wouldn't pin " + app.Title + ": " + e.Message; }
            return ok ? null : app.Title + " wasn't pinned";
        }

        /// The app's icon, copied where a tile can read it (ms-appdata:///local/).
        private static async Task<Uri> TileLogo(AppInfo app)
        {
            var src = System.IO.Path.Combine(app.Dir, (app.Icon ?? "icon.png").Replace('/', '\\'));
            var name = TileId(app.Id) + System.IO.Path.GetExtension(src);
            var folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync("tiles", CreationCollisionOption.OpenIfExists);
            var file = await folder.CreateFileAsync(name, CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteBytesAsync(file, System.IO.File.ReadAllBytes(src));
            return new Uri("ms-appdata:///local/tiles/" + name);
        }
    }

    /// Whether Windows' Start background follows webOS's wallpaper (Android's HostWallpaper.kt,
    /// whose home and lock screens follow it). Screen & Lock's switch; off unless turned on.
    internal static class HostWallpaper
    {
        private const string Key = "hostWallpaper";

        public static bool On
        {
            get { try { return ApplicationData.Current.LocalSettings.Values[Key] as bool? == true; } catch (Exception) { return false; } }
            set { try { ApplicationData.Current.LocalSettings.Values[Key] = value; } catch (Exception) { } }
        }

        /// Sets Windows' background to webOS's wallpaper now: null, or why it couldn't.
        public static async Task<string> Apply(WebosFs fs, string webosFile)
        {
            if (!Windows.System.UserProfile.UserProfilePersonalizationSettings.IsSupported()) return "Windows doesn't let apps set its background here";
            if (string.IsNullOrEmpty(webosFile)) return "webOS has no wallpaper set";
            byte[] data;
            try { data = fs.Read(webosFile); }
            catch (Exception e) { return "Couldn't read the wallpaper: " + e.Message; }
            // Windows takes a file in the app's own local folder only.
            var file = await ApplicationData.Current.LocalFolder.CreateFileAsync("host-wallpaper" + System.IO.Path.GetExtension(webosFile),
                CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteBytesAsync(file, data);
            bool ok = await Windows.System.UserProfile.UserProfilePersonalizationSettings.Current.TrySetWallpaperImageAsync(file);
            return ok ? null : "Windows wouldn't set its background";
        }
    }

    /// files/list for Lunacy's file picker (LunacyService.kt's listFiles): a folder of the webOS
    /// tree, grouped by the folder each file sits in, the way webOS's picker showed albums. The
    /// libraries mapped into /media/internal are listed beside Lunacy's own folders by WebosFs.
    internal static class FileListing
    {
        public static string List(WebosFs fs, JObj p)
        {
            var path = WebosFs.Normal(p.Has("path") && p.Str("path").Length > 0 ? p.Str("path") : "/media/internal");
            var typesArr = p.Arr("types");
            List<string> types = null;
            if (typesArr != null) types = typesArr.Select(t => (t as string ?? "").ToLowerInvariant()).ToList();
            if (types != null && types.Contains("ringtone")) return Ringtones(fs);
            if (!fs.IsDir(path)) return Bus.Error("No such folder: " + path);
            var subfolders = Names(fs, path).Where(n => !n.StartsWith(".") && fs.IsDir(path + "/" + n))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Select(n => path.TrimEnd('/') + "/" + n);
            var albums = new JArr();
            foreach (var dir in new[] { path }.Concat(subfolders))
            {
                var files = new JArr();
                foreach (var n in Names(fs, dir).Where(x => !x.StartsWith(".")).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    var full = dir.TrimEnd('/') + "/" + n;
                    JObj st;
                    try { st = fs.Stat(full); } catch (Exception) { continue; }
                    if (st.Bool("dir")) continue;
                    var type = TypeOf(n);
                    if (types != null && !types.Contains(type)) continue;
                    files.Add(new JObj().Put("name", n).Put("fullPath", full).Put("attachmentType", type).Put("size", st["size"]));
                }
                if (files.Count > 0)
                    albums.Add(new JObj().Put("name", dir == path ? RootName(path) : dir.Substring(dir.LastIndexOf('/') + 1))
                        .Put("path", dir).Put("files", files));
            }
            return new JObj().Put("returnValue", true).Put("path", path).Put("albums", albums).ToString();
        }

        /// webOS's "ringtone" type: the sounds in /media/internal/ringtones, each with its title,
        /// as Palm's ringtone picker listed them from the media index.
        private static string Ringtones(WebosFs fs)
        {
            const string dir = "/media/internal/ringtones";
            var files = new JArr();
            foreach (var n in Names(fs, dir).Where(x => !x.StartsWith(".") && TypeOf(x) == "audio").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var full = dir + "/" + n;
                long size = 0;
                try { size = Convert.ToInt64(fs.Stat(full)["size"]); } catch (Exception) { }
                var title = System.IO.Path.GetFileNameWithoutExtension(n);
                files.Add(new JObj().Put("name", n).Put("title", title).Put("artist", "").Put("searchKey", title.ToLowerInvariant())
                    .Put("fullPath", full).Put("attachmentType", "audio").Put("size", size));
            }
            var albums = new JArr();
            if (files.Count > 0) albums.Add(new JObj().Put("name", "Ringtones").Put("path", dir).Put("files", files));
            return new JObj().Put("returnValue", true).Put("path", dir).Put("albums", albums).ToString();
        }

        private static List<string> Names(WebosFs fs, string dir)
        {
            try { return fs.List(dir); } catch (Exception) { return new List<string>(); }
        }

        private static string RootName(string path)
        {
            var t = path.TrimEnd('/');
            var n = t.Substring(t.LastIndexOf('/') + 1);
            return n.Length == 0 ? "Files" : n;
        }

        /// webOS's attachment types, by extension: what a FilePicker's fileType filters on.
        public static string TypeOf(string name)
        {
            var ext = System.IO.Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
            switch (ext)
            {
                case "jpg": case "jpeg": case "png": case "gif": case "bmp": case "webp": return "image";
                case "mp3": case "m4a": case "aac": case "wav": case "ogg": case "oga": case "flac": case "wma": return "audio";
                case "mp4": case "m4v": case "mov": case "avi": case "mkv": case "webm": case "3gp": case "wmv": return "video";
                default: return FileIndex.IsDocument(name) ? "document" : "other";
            }
        }
    }
}
