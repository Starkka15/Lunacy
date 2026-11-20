using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Search;

namespace LunaKit
{
    /// One file under /media/internal as the media walk found it: in Lunacy's own tree (`Local`)
    /// or in one of the phone's libraries (`Storage`).
    internal sealed class MediaFile
    {
        public string Path, Name, Local;
        public StorageFile Storage;
        public long Size, Modified;   // bytes; ms since 1970

        private static T Wait<T>(Windows.Foundation.IAsyncOperation<T> op) { return op.AsTask().GetAwaiter().GetResult(); }

        /// The file as a StorageFile, for Windows' own media properties.
        public StorageFile File()
        {
            return Storage ?? Wait(StorageFile.GetFileFromPathAsync(Local));
        }

        /// Up to `max` bytes from the start of the file.
        public byte[] Head(int max)
        {
            using (var s = Storage != null ? Storage.OpenStreamForReadAsync().GetAwaiter().GetResult() : System.IO.File.OpenRead(Local))
            {
                var buf = new byte[max];
                int n = 0, r;
                while (n < max && (r = s.Read(buf, n, max - n)) > 0) n += r;
                if (n < max) Array.Resize(ref buf, n);
                return buf;
            }
        }
    }

    /// The walk of user storage (LunaCore's UserFiles.walkMedia) and what a file says about itself
    /// (LunaCore's MediaTags, which AndroidLuna answers with ExifInterface and
    /// MediaMetadataRetriever): here a JPEG's EXIF is read from the file itself, and a movie's or a
    /// song's tags from Windows' property system.
    internal static class MediaFiles
    {
        private const int MaxDepth = 12, MaxFiles = 5000;
        private static readonly Dictionary<string, string> lastSummary = new Dictionary<string, string>();

        private static T Wait<T>(Windows.Foundation.IAsyncOperation<T> op) { return op.AsTask().GetAwaiter().GetResult(); }

        /// Every file in /media/internal as `record` makes it, the way filenotifyd walked user
        /// storage: Lunacy's own tree first, then each of the phone's mapped libraries; names in
        /// order, hidden files and folders left out, a library inside another (Camera Roll and
        /// webOS Wallpapers in Pictures) walked once under its own name, each path once,
        /// twelve deep and 5000 records at most. `record` gives null for a file it doesn't index.
        public static List<JObj> Walk(UserFiles user, string label, Func<MediaFile, JObj> record)
        {
            var out_ = new List<JObj>();
            var paths = new HashSet<string>(StringComparer.Ordinal);
            var mapped = user.Mapped.ToList();
            var roots = mapped.Select(m => m.Value.Path.TrimEnd('\\')).Where(p => p.Length > 0).ToList();
            Func<string, bool> nested = p => roots.Any(r => !string.Equals(r, p, StringComparison.OrdinalIgnoreCase)
                && p.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase)) && roots.Any(r => string.Equals(r, p, StringComparison.OrdinalIgnoreCase));

            Action<MediaFile> add = f =>
            {
                if (paths.Contains(f.Path)) return;
                JObj r;
                try { r = record(f); } catch (Exception e) { LunaLog.Write("[media] " + f.Path + ": " + e.Message); r = null; }
                if (r == null) return;
                paths.Add(f.Path);
                out_.Add(r);
            };

            Action<string, string, int> walkLocal = null;
            walkLocal = (dir, path, depth) =>
            {
                if (depth > MaxDepth || out_.Count >= MaxFiles) return;
                string[] entries;
                try { entries = Directory.EnumerateFileSystemEntries(dir).ToArray(); } catch (Exception) { return; }
                foreach (var e in entries.OrderBy(x => System.IO.Path.GetFileName(x), StringComparer.Ordinal))
                {
                    var name = System.IO.Path.GetFileName(e);
                    if (name.StartsWith(".", StringComparison.Ordinal)) continue;
                    var p = path + "/" + name;
                    if (Directory.Exists(e)) { walkLocal(e, p, depth + 1); continue; }
                    var fi = new FileInfo(e);
                    add(new MediaFile { Path = p, Name = name, Local = e, Size = fi.Length,
                        Modified = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds() });
                    if (out_.Count >= MaxFiles) return;
                }
            };

            Action<StorageFolder, string, int> walkLibrary = null;
            walkLibrary = (dir, path, depth) =>
            {
                if (depth > MaxDepth || out_.Count >= MaxFiles) return;
                IReadOnlyList<StorageFile> files;
                IReadOnlyList<StorageFolder> folders;
                try
                {
                    // Size and time fetched with the listing: one query, not a call per file.
                    var opts = new QueryOptions(CommonFileQuery.DefaultQuery, null) { FolderDepth = FolderDepth.Shallow, IndexerOption = IndexerOption.DoNotUseIndexer };
                    opts.SetPropertyPrefetch(PropertyPrefetchOptions.BasicProperties, new string[0]);
                    files = Wait(dir.CreateFileQueryWithOptions(opts).GetFilesAsync());
                    folders = Wait(dir.GetFoldersAsync());
                }
                catch (Exception) { return; }
                var items = files.Select(f => (IStorageItem)f).Concat(folders).OrderBy(i => i.Name, StringComparer.Ordinal);
                foreach (var it in items)
                {
                    if (it.Name.StartsWith(".", StringComparison.Ordinal)) continue;
                    var p = path + "/" + it.Name;
                    var folder = it as StorageFolder;
                    if (folder != null) { if (!nested(folder.Path.TrimEnd('\\'))) walkLibrary(folder, p, depth + 1); continue; }
                    var file = (StorageFile)it;
                    long size = 0, mtime = file.DateCreated.ToUnixTimeMilliseconds();
                    try { var bp = Wait(file.GetBasicPropertiesAsync()); size = (long)bp.Size; mtime = bp.DateModified.ToUnixTimeMilliseconds(); } catch (Exception) { }
                    add(new MediaFile { Path = p, Name = it.Name, Storage = file, Size = size, Modified = mtime });
                    if (out_.Count >= MaxFiles) return;
                }
            };

            walkLocal(user.Own, UserFiles.Root, 0);
            // Per library, for the log: which could be read and what was found there.
            var counts = new List<string>();
            foreach (var m in mapped)
            {
                int before = out_.Count;
                walkLibrary(m.Value, UserFiles.Root + "/" + m.Key, 0);
                counts.Add(m.Key + " " + (out_.Count - before));
            }
            var summary = out_.Count + " files; " + string.Join(", ", counts) + (out_.Count >= MaxFiles ? "; stopped at " + MaxFiles : "");
            bool changed;
            lock (lastSummary)
            {
                string was;
                changed = !lastSummary.TryGetValue(label, out was) || was != summary;
                lastSummary[label] = summary;
            }
            // Once, and again when it changes: a watched kind is walked every few seconds.
            if (changed) LunaLog.Write("[media] " + label + ": " + summary);
            return out_;
        }

        // ---- a picture's EXIF ----

        internal sealed class Exif
        {
            public string DateTime, SubSec;
            public long ThumbnailOffset, ThumbnailLength;
        }

        /// A JPEG's EXIF: DateTime (or DateTimeOriginal), SubSecTime (or SubSecTimeOriginal), and
        /// where its embedded thumbnail lies in the file. Null for a file without EXIF.
        public static Exif ReadExif(MediaFile f)
        {
            byte[] d;
            try { d = f.Head(256 * 1024); } catch (Exception) { return null; }
            if (d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8) return null;
            int i = 2;
            while (i + 4 <= d.Length)
            {
                if (d[i] != 0xFF) return null;
                int marker = d[i + 1];
                if (marker == 0xD8 || (marker >= 0xD0 && marker <= 0xD7) || marker == 0x01) { i += 2; continue; }
                if (marker == 0xDA || marker == 0xD9) return null;
                int len = (d[i + 2] << 8) | d[i + 3];
                if (marker == 0xE1 && i + 10 <= d.Length && Encoding.ASCII.GetString(d, i + 4, 4) == "Exif" && d[i + 8] == 0 && d[i + 9] == 0)
                    return Tiff(d, i + 10, Math.Min(d.Length, i + 2 + len));
                i += 2 + len;
            }
            return null;
        }

        private static Exif Tiff(byte[] d, int t, int end)
        {
            if (t + 8 > end) return null;
            bool le = d[t] == 'I' && d[t + 1] == 'I';
            if (!le && !(d[t] == 'M' && d[t + 1] == 'M')) return null;
            Func<int, int> u16 = o => o + 2 > end ? 0 : le ? d[o] | (d[o + 1] << 8) : (d[o] << 8) | d[o + 1];
            Func<int, long> u32 = o => o + 4 > end ? 0 : le ? (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24))
                : (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);
            var tags = new Dictionary<int, object>[3];
            Func<long, int, long> ifd = null;
            ifd = (off, which) =>
            {
                var map = tags[which] = new Dictionary<int, object>();
                int o = t + (int)off;
                if (off <= 0 || o + 2 > end) return 0;
                int n = u16(o);
                for (int k = 0; k < n; k++)
                {
                    int e = o + 2 + k * 12;
                    if (e + 12 > end) break;
                    int tag = u16(e), type = u16(e + 2);
                    long count = u32(e + 4);
                    if (type == 2)
                    {
                        int at = count <= 4 ? e + 8 : t + (int)u32(e + 8);
                        if (at < 0 || at + count > end) continue;
                        map[tag] = Encoding.ASCII.GetString(d, at, (int)count).TrimEnd('\0');
                    }
                    else if (type == 3) map[tag] = (long)u16(e + 8);
                    else if (type == 4) map[tag] = u32(e + 8);
                }
                return u32(o + 2 + n * 12);
            };
            long next = ifd(u32(t + 4), 0);
            object exifPtr;
            if (tags[0].TryGetValue(0x8769, out exifPtr) && exifPtr is long) ifd((long)exifPtr, 1); else tags[1] = new Dictionary<int, object>();
            if (next > 0) ifd(next, 2); else tags[2] = new Dictionary<int, object>();
            Func<int, int, string> str = (w, tag) => { object v; return tags[w].TryGetValue(tag, out v) ? v as string : null; };
            var x = new Exif
            {
                DateTime = str(0, 0x0132) ?? str(1, 0x9003),
                SubSec = str(1, 0x9290) ?? str(1, 0x9291)
            };
            object toff, tlen;
            if (tags[2].TryGetValue(0x0201, out toff) && tags[2].TryGetValue(0x0202, out tlen) && toff is long && tlen is long && (long)tlen > 0)
            {
                x.ThumbnailOffset = t + (long)toff;
                x.ThumbnailLength = (long)tlen;
            }
            return x;
        }

        // ---- a movie's and a song's tags, from Windows' property system ----

        internal sealed class Video
        {
            public long DurationMs;
            public string Title;
            public long Date;   // seconds; 0 without one
        }

        /// A movie with a picture: its length, title and creation time; null for one without a
        /// video track or one Windows can't read.
        public static Video ReadVideo(MediaFile f)
        {
            try
            {
                var file = f.File();
                var v = Wait(file.Properties.GetVideoPropertiesAsync());
                if (v.Width == 0 && v.Height == 0) return null;
                long date = 0;
                try
                {
                    var props = Wait(file.Properties.RetrievePropertiesAsync(new[] { "System.Media.DateEncoded" }));
                    object o;
                    // An MP4 with no creation time has 1904 (QuickTime's epoch) or 1970.
                    if (props.TryGetValue("System.Media.DateEncoded", out o) && o is DateTimeOffset) date = Math.Max(0, ((DateTimeOffset)o).ToUnixTimeSeconds());
                }
                catch (Exception) { }
                return new Video { DurationMs = (long)v.Duration.TotalMilliseconds, Title = v.Title, Date = date };
            }
            catch (Exception) { return null; }
        }

        internal sealed class Audio
        {
            public string Title, Artist, Album, AlbumArtist, Genre, Track, Disc;
        }

        public static Audio ReadAudio(MediaFile f)
        {
            try
            {
                var file = f.File();
                var m = Wait(file.Properties.GetMusicPropertiesAsync());
                string disc = null;
                try
                {
                    var props = Wait(file.Properties.RetrievePropertiesAsync(new[] { "System.Music.DiscNumber" }));
                    object o;
                    if (props.TryGetValue("System.Music.DiscNumber", out o) && o != null) disc = Convert.ToString(o);
                }
                catch (Exception) { }
                return new Audio
                {
                    Title = m.Title, Artist = m.Artist, Album = m.Album, AlbumArtist = m.AlbumArtist,
                    Genre = m.Genre.FirstOrDefault(), Track = m.TrackNumber == 0 ? null : m.TrackNumber.ToString(), Disc = disc
                };
            }
            catch (Exception) { return null; }
        }
    }

    /// The db8 registrations Lunacy makes for kinds it answers itself (LunaCore's KindSpec.kt),
    /// built the way their services' own putKind and putPermissions calls wrote them.
    internal static class KindSpec
    {
        /// An index property: its name, and the collation and tokenizing a search index gives it.
        public static JObj Prop(string name, string collate = null, string tokenize = null)
        {
            var o = new JObj().Put("name", name);
            if (collate != null) o.Put("collate", collate);
            if (tokenize != null) o.Put("tokenize", tokenize);
            return o;
        }

        public static JObj Index(string name, params JObj[] props) { return new JObj().Put("name", name).Put("props", new JArr(props)); }

        /// An index on plain properties in order, named as filenotifyd named such indexes: joined with "_".
        public static JObj Plain(params string[] names) { return Index(string.Join("_", names), names.Select(n => Prop(n)).ToArray()); }

        /// A kind's indexes from a compact table: each a ready index, or plain property names.
        public static JArr Indexes(params object[] table)
        {
            return new JArr(table.Select(i => i is JObj ? i : Plain((string[])i)));
        }

        public static JObj Kind(string id, string owner, JArr indexes, string extends = null)
        {
            var o = new JObj().Put("id", id).Put("owner", owner).Put("indexes", indexes);
            if (extends != null) o.Put("extends", new JArr().Put(extends));
            return o;
        }

        /// One db.kind permission per caller, each allowing `operations`.
        public static JArr Permissions(string kind, IEnumerable<string> callers, params string[] operations)
        {
            return new JArr(callers.Select(app =>
            {
                var ops = new JObj();
                foreach (var op in operations) ops.Put(op, "allow");
                return (object)new JObj().Put("type", "db.kind").Put("object", kind).Put("caller", app).Put("operations", ops);
            }));
        }
    }
}
