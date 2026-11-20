using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;

namespace LunaKit
{
    /// webOS's media index for photos and videos (LunaCore's MediaIndex.kt): the
    /// `com.palm.media.image.file:1`, `com.palm.media.video.file:1` and
    /// `com.palm.media.image.album:1` records that Photos & Videos, the photos service, the video
    /// player and the system UI read, answered from the files themselves (codepoet's design,
    /// luna-deltas A16). db8 brings the kinds into line with the folders whenever they are read
    /// (Db8.MirrorKind); the photos service still makes the thumbnails, as on a device. The
    /// records are filenotifyd's as measured on the reference TouchPad; MediaIndex.kt describes
    /// each property.
    ///
    /// W10M's camera roll is Camera Roll, /media/internal/camera here (UserFiles): a picture
    /// directly in it is in Palm's "Photo roll", as Android's DCIM/Camera is.
    internal sealed class MediaIndex
    {
        public const string Types = "com.palm.media.types:1";
        public const string Album = "com.palm.media.image.album:1";
        public const string Image = "com.palm.media.image.file:1";
        public const string VideoKind = "com.palm.media.video.file:1";
        public const string AudioKind = "com.palm.media.audio.file:1";

        private const string Owner = "com.palm.filenotifyd.js";
        /// filenotifyd's bus name, which called the photos service.
        private const string Indexer = "com.palm.filenotifyd";
        private const string PhotosService = "com.palm.service.photos";
        private const string CameraRoll = "camera://";
        private const string CameraDir = "/media/internal/camera/";
        private const string MediaSync = "mediasync://";
        private const string AllImages = "[all]";
        private static readonly string[] AllOperations = { "create", "read", "update", "delete" };
        private static readonly HashSet<string> Images = new HashSet<string> { "jpg", "jpeg", "png", "bmp" };
        private static readonly HashSet<string> Videos = new HashSet<string> { "mp4", "m4v", "mov", "3gp", "3g2", "wmv", "asf", "avi" };
        private static readonly HashSet<string> AudioFiles = new HashSet<string> { "mp3", "m4a", "m4r", "aac", "wav", "ogg", "oga", "amr", "wma", "flac" };
        private const string UnknownArtist = "Unknown Artist", UnknownAlbum = "Unknown Album", UnknownGenre = "Unknown Genre";

        private static readonly Dictionary<string, string[]> Consumers = new Dictionary<string, string[]>
        {
            { Types, new[] { "com.palm.app.photos", "com.palm.service.photos", "com.palm.mojo-systemui", "com.palm.systemui" } },
            { Album, new[] { "com.palm.app.photos", "com.palm.service.photos", "com.palm.app.camera", "com.palm.service.mediacache", "com.palm.systemui", "com.palm.mojo-systemui" } },
            { Image, new[] { "com.palm.app.photos", "com.palm.app.camera", "com.palm.systemui", "com.palm.mojo-systemui", "com.palm.service.mediacache", "com.palm.service.photos" } },
            { AudioKind, new[] { "com.palm.app.musicplayer", "com.palm.service.music", "com.palm.launcher", "com.palm.service.mediacache", "com.palm.systemui", "com.palm.mojo-systemui" } },
            { VideoKind, new[] { "com.palm.app.camera", "com.palm.app.videoplayer", "com.palm.service.mediacache", "com.palm.systemui", "com.palm.mojo-systemui" } },
        };

        /// Palm's photos library's predefined albums (album.js), in English: name and priority.
        private static readonly Dictionary<string, KeyValuePair<string, int>> Predefined = new Dictionary<string, KeyValuePair<string, int>>
        {
            { CameraRoll, P("Photo roll", 200) }, { AllImages, P("All Images", 250) },
            { "/media/internal", P("Miscellaneous", 300) }, { MediaSync, P("Media Sync", 400) },
            { "/media/internal/downloads", P("Downloads", 900) }, { "/media/internal/messaging", P("Messaging", 900) },
            { "/media/internal/screencaptures", P("Screen captures", 900) }, { "/media/internal/wallpapers", P("Wallpapers", 900) },
        };
        private static KeyValuePair<string, int> P(string n, int p) { return new KeyValuePair<string, int>(n, p); }

        private readonly UserFiles user;

        /// A file's record as last read, kept while the file is unchanged: EXIF and movie tags are slow.
        private sealed class Seen { public long Modified, Size; public JObj Record; }
        private readonly Dictionary<string, Seen> seen = new Dictionary<string, Seen>();
        private readonly Dictionary<string, Seen> seenAudio = new Dictionary<string, Seen>();
        private readonly object scanLock = new object();
        private long scanned;
        private List<JObj> media = new List<JObj>();
        /// When a pseudo-album with no folder was first seen: its modifiedTime.
        private readonly Dictionary<string, long> firstSeen = new Dictionary<string, long>();
        private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();

        public MediaIndex(UserFiles user) { this.user = user; }

        public void Attach(Db8 db, Bus bus)
        {
            db.Configure(new List<JObj> { TypesKind() }, new List<JArr> { Permissions(Types) });
            db.MirrorKind(AlbumKind(), Permissions(Album), Albums, new[] { "showAlbum" }, Local);
            Action added = () => CacheThumbnails(bus);
            db.MirrorKind(ImageKind(), Permissions(Image), () => Files(db, "image"), new[] { "appCacheComplete" }, Local, added);
            db.MirrorKind(VideoKindSpec(), Permissions(VideoKind), () => Files(db, "video"), new[] { "appCacheComplete", "playbackPosition" }, Local, added);
            db.MirrorKind(AudioKindSpec(), Permissions(AudioKind), Ringtones, new[] { "bookmark", "serviced", "hasResizedThumbnails" });
        }

        /// palm://com.palm.filenotifyd/state: whether the indexer is busy and /media/internal is
        /// mounted (Video Player's "indexing" spinner). The index is brought up to date as it is
        /// read, and /media/internal is always there, so the answer is the TouchPad's at rest and
        /// a subscription is never told a change. Measured on the reference TouchPad: without a
        /// subscription {"returnValue":false,"errorText":"mustSubscribe"}; private bus only.
        public void RegisterState(Bus bus)
        {
            bus.RegisterPrivate(Indexer, "state", true, c =>
            {
                // "watch" alone isn't enough: the TouchPad wanted "subscribe" itself.
                if (!c.Params.GetBool("subscribe")) { c.Reply(new JObj().Put("returnValue", false).Put("errorText", "mustSubscribe").ToString()); return; }
                c.Reply(new JObj().Put("returnValue", true).Put("state", "idle").Put("mounted", new JObj().Put("/media/internal", true)).ToString());
            });
        }

        /// The ringtones, as filenotifyd's audio module filed them (MediaIndex.kt's ringtones).
        private List<JObj> Ringtones()
        {
            var dir = Path.Combine(user.Own, "ringtones");
            if (!Directory.Exists(dir)) return new List<JObj>();
            var files = Directory.EnumerateFiles(dir).Select(p => new FileInfo(p))
                .Where(f => !f.Name.StartsWith(".", StringComparison.Ordinal) && AudioFiles.Contains(Ext(f.Name)))
                .OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
            var paths = new HashSet<string>(files.Select(f => "/media/internal/ringtones/" + f.Name));
            lock (seenAudio) foreach (var k in seenAudio.Keys.Where(k => !paths.Contains(k)).ToList()) seenAudio.Remove(k);
            var out_ = new List<JObj>();
            foreach (var fi in files)
            {
                var p = "/media/internal/ringtones/" + fi.Name;
                var mf = new MediaFile { Path = p, Name = fi.Name, Local = fi.FullName, Size = fi.Length,
                    Modified = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds() };
                Seen old;
                lock (seenAudio) seenAudio.TryGetValue(p, out old);
                if (old != null && old.Modified == mf.Modified && old.Size == mf.Size) { out_.Add(old.Record.Copy()); continue; }
                var r = AudioRecord(mf).Put(Db8.Stamp, mf.Modified + ":" + mf.Size);
                lock (seenAudio) seenAudio[p] = new Seen { Modified = mf.Modified, Size = mf.Size, Record = r.Copy() };
                out_.Add(r);
            }
            return out_;
        }

        /// A sound file's record as filenotifyd's audio module made it (MediaIndex.kt's audioRecord).
        public static JObj AudioRecord(MediaFile f)
        {
            Func<string, string> tag = v => v == null || v.Trim().Length == 0 ? null : v.Trim();
            var tags = MediaFiles.ReadAudio(f);
            string title = tag(tags == null ? null : tags.Title), artist = tag(tags == null ? null : tags.Artist), album = tag(tags == null ? null : tags.Album);
            string albumArtist = tag(tags == null ? null : tags.AlbumArtist), genre = tag(tags == null ? null : tags.Genre);
            Func<string, int, int> num = (s, fallback) => { int n; return s != null && int.TryParse(s.Split('/')[0], out n) ? n : fallback; };
            int track = num(tag(tags == null ? null : tags.Track), 0), disc = num(tag(tags == null ? null : tags.Disc), 1);
            var dot = f.Name.LastIndexOf('.');
            var name = dot > 0 ? f.Name.Substring(0, dot) : f.Name;
            var t = title ?? name;
            var alb = album ?? UnknownAlbum;
            var albArtist = albumArtist ?? UnknownArtist;
            var art = artist ?? albumArtist ?? UnknownArtist;
            // AudioObject's defaults for a file without tags; refreshSortKeys's, tab-joined, once any is set.
            bool tagged = artist != null || album != null || albumArtist != null || track != 0 || disc != 1;
            var trackAndDisc = (disc * 100000 + track).ToString();
            var sortKey = !tagged
                ? new JObj().Put("trackAndDisc", "100000").Put("albumDiscAndTrack", UnknownAlbum + "100000")
                    .Put("albumArtistDiscAndTrack", UnknownAlbum + UnknownArtist + "100000").Put("artistAlbumDiscAndTrack", UnknownArtist + UnknownAlbum + "100000")
                : new JObj().Put("trackAndDisc", trackAndDisc).Put("albumDiscAndTrack", alb + "\t\t" + trackAndDisc)
                    .Put("albumArtistDiscAndTrack", alb + "\t\t" + art + "\t\t" + trackAndDisc).Put("artistAlbumDiscAndTrack", art + "\t\t" + alb + "\t\t" + trackAndDisc);
            return new JObj().Put("path", f.Path).Put("title", t).Put("artist", art).Put("album", alb)
                .Put("albumArtist", albArtist).Put("genre", genre ?? UnknownGenre)
                .Put("disc", new JObj().Put("position", (long)disc).Put("total", 1L))
                .Put("track", new JObj().Put("position", (long)track).Put("total", 0L))
                .Put("searchKey", art + "\t\t" + alb + "\t\t" + t).Put("sortKey", sortKey)
                .Put("isRingtone", true).Put("duration", 0L).Put("bookmark", 0L).Put("createdTime", 0L)
                .Put("modifiedTime", f.Modified / 1000).Put("size", f.Size)
                .Put("thumbnails", new JArr()).Put("serviced", false).Put("hasResizedThumbnails", false);
        }

        /// New pictures are indexed: the photos service makes their thumbnails, as filenotifyd had
        /// it do after every pass ("calling into photos service to take over app caching").
        private static void CacheThumbnails(Bus bus)
        {
            Task.Run(() =>
            {
                if (bus.Has(PhotosService)) bus.DispatchPrivate(Indexer, "palm://" + PhotosService + "/cacheThumbnails", "{}", r => { });
            });
        }

        private static bool Local(JObj o) { return o.Str("type") == "local"; }

        /// The images or videos, each with its album's id. On db8's mirror thread.
        private List<JObj> Files(Db8 db, string type)
        {
            var albums = db.MirrorIds(Album);
            return Scan().Where(r => r.Str("mediaType") == type).Select(r =>
            {
                var o = r.Copy();
                string id;
                if (albums.TryGetValue(r.Str("albumPath"), out id)) o.Put("albumId", id);
                return o;
            }).ToList();
        }

        /// One album per folder that holds images or videos.
        private List<JObj> Albums()
        {
            return Scan().GroupBy(r => r.Str("albumPath")).Select(g =>
            {
                var path = g.Key;
                var name = AlbumName(path);
                var items = g.ToList();
                var images = items.Where(i => i.Str("mediaType") == "image").ToList();
                var thumbs = new JArr(images.OrderByDescending(i => Jx.Num(i["createdTime"])).Take(3)
                    .Select(i => (object)((JObj)i.Arr("thumbnails")[0]).Copy()));
                return new JObj().Put("name", name.Key).Put("sortKey", name.Value + "_" + name.Key).Put("searchKey", name.Key)
                    .Put("path", path).Put("type", "local").Put("showAlbum", true)
                    .Put("total", new JObj().Put("images", (long)images.Count).Put("videos", (long)(items.Count - images.Count)))
                    .Put("thumbnails", thumbs).Put("modifiedTime", AlbumTime(path));
            }).ToList();
        }

        /// Every image and video under /media/internal, as records without their albumId; read at most every second.
        private List<JObj> Scan()
        {
            lock (scanLock)
            {
                long now = Clock.ElapsedMilliseconds;
                if (scanned != 0 && now - scanned < 1000) return media.Select(m => m.Copy()).ToList();
                var paths = new HashSet<string>();
                var out_ = MediaFiles.Walk(user, "media index", f =>
                {
                    var ext = Ext(f.Name);
                    if (!Images.Contains(ext) && !Videos.Contains(ext)) return null;
                    paths.Add(f.Path);
                    Seen old;
                    // A file rewritten in place (the photo editor's Replace Original) is a new
                    // record, as filenotifyd filed it: the stamp tells db8.
                    if (seen.TryGetValue(f.Path, out old) && old.Modified == f.Modified && old.Size == f.Size) return old.Record == null ? null : old.Record.Copy();
                    var r = Images.Contains(ext) ? ImageRecord(f) : VideoRecord(f);
                    if (r != null) r.Put(Db8.Stamp, f.Modified + ":" + f.Size);
                    seen[f.Path] = new Seen { Modified = f.Modified, Size = f.Size, Record = r == null ? null : r.Copy() };
                    return r;
                });
                foreach (var k in seen.Keys.Where(k => !paths.Contains(k)).ToList()) seen.Remove(k);
                media = out_.Select(m => m.Copy()).ToList();
                scanned = now;
                return out_;
            }
        }

        private JObj ImageRecord(MediaFile f)
        {
            double created = 0;
            long offset = 0, length = 0;
            var exif = Ext(f.Name) == "jpg" || Ext(f.Name) == "jpeg" ? MediaFiles.ReadExif(f) : null;
            if (exif != null)
            {
                if (exif.DateTime != null)
                {
                    var a = exif.DateTime.Replace(" ", ":").Split(':').Select(x => { int n; return int.TryParse(x.Trim(), out n) ? (int?)n : null; }).ToList();
                    if (a.Count >= 6 && a.Take(6).All(n => n != null))
                    {
                        try
                        {
                            // new Date(y, m - 1, d, h, min, s): local time, the month at least January,
                            // the rest rolling over as Java's lenient calendar does.
                            var dt = new DateTime(a[0].Value, 1, 1, 0, 0, 0, DateTimeKind.Local).AddMonths(Math.Max(a[1].Value - 1, 0))
                                .AddDays(a[2].Value - 1).AddHours(a[3].Value).AddMinutes(a[4].Value).AddSeconds(a[5].Value);
                            created = new DateTimeOffset(dt).ToUnixTimeMilliseconds() / 1000.0;
                        }
                        catch (Exception) { created = 0; }
                    }
                    int s;
                    var sub = exif.SubSec == null ? "" : exif.SubSec.Trim();
                    if (sub.Length > 0 && int.TryParse(sub, out s)) created += s / Math.Pow(10, s.ToString().Length);
                }
                offset = exif.ThumbnailOffset; length = exif.ThumbnailLength;
            }
            return new JObj().Put("path", f.Path).Put("type", "local").Put("albumPath", AlbumPath(f.Path))
                .Put("createdTime", created)
                .Put("thumbnails", new JArr().Put(new JObj().Put("type", "embedded")
                    .Put("data", new JObj().Put("offset", offset).Put("length", length).Put("path", f.Path))))
                .Put("mediaType", "image").Put("appCacheComplete", "unattempted");
        }

        private JObj VideoRecord(MediaFile f)
        {
            var v = MediaFiles.ReadVideo(f);
            if (v == null) return null;
            var duration = v.DurationMs / 1000.0;
            var dot = f.Name.LastIndexOf('.');
            var bare = dot > 0 ? f.Name.Substring(0, dot) : f.Name;
            var title = string.IsNullOrEmpty(v.Title) ? (bare.Length == 0 ? f.Name : bare) : v.Title;
            var album = AlbumPath(f.Path);
            return new JObj().Put("path", f.Path).Put("type", "local").Put("albumPath", album)
                .Put("capturedOnDevice", f.Path.StartsWith("/media/internal/DCIM/", StringComparison.Ordinal) || album == CameraRoll)
                .Put("createdTime", v.Date)
                .Put("modifiedTime", f.Modified / 1000).Put("size", f.Size)
                .Put("title", title).Put("searchKey", title).Put("description", "")
                .Put("thumbnails", new JArr()).Put("duration", Math.Round(duration * 100) / 100.0)
                .Put("playbackPosition", 0L).Put("mediaType", "video").Put("appCacheComplete", "unattempted");
        }

        /// Palm's Album.getPathFromImage: the camera's roll, the media sync roll, or the folder.
        private static string AlbumPath(string path)
        {
            var n = path.ToLowerInvariant();
            if (n.StartsWith(CameraDir, StringComparison.Ordinal) && n.IndexOf('/', CameraDir.Length) == -1) return CameraRoll;
            if (n.StartsWith("/media/internal/dcim/", StringComparison.Ordinal) && n.Length >= 28)
            {
                var roll = n.Substring(21, 7);
                int num;
                if (int.TryParse(roll.Substring(0, 3), out num) && num >= 100 && num <= 999 && roll.Substring(3) == "palm") return CameraRoll;
            }
            if (n.StartsWith("/media/internal/photos/full resolution/", StringComparison.Ordinal)) return MediaSync;
            if (n.LastIndexOf('/') == 0) return AllImages;
            return path.Substring(0, path.LastIndexOf('/'));
        }

        /// Album.parse's name and priority: Palm's predefined albums, or the folder's name at 900.
        private static KeyValuePair<string, int> AlbumName(string path)
        {
            KeyValuePair<string, int> p;
            return Predefined.TryGetValue(path.ToLowerInvariant(), out p) ? p : P(path.Substring(path.LastIndexOf('/') + 1), 900);
        }

        /// RebuildAlbums' modifiedTime: the folder's mtime, its UTC fields read as local time.
        private long AlbumTime(string path)
        {
            long mtime = 0;
            if (path != MediaSync && path != AllImages)
            {
                var webos = path == CameraRoll ? "/media/internal/camera" : path;
                try
                {
                    StorageFolder lib; string rest;
                    if (user.InLibrary(webos, out lib, out rest))
                    {
                        var item = UserFiles.Item(lib, rest);
                        if (item != null) mtime = item.GetBasicPropertiesAsync().AsTask().GetAwaiter().GetResult().DateModified.ToUnixTimeMilliseconds();
                    }
                    else
                    {
                        var local = Path.Combine(user.Own, webos.Substring(Math.Min(webos.Length, UserFiles.Root.Length)).TrimStart('/').Replace('/', '\\'));
                        if (Directory.Exists(local)) mtime = new DateTimeOffset(Directory.GetLastWriteTimeUtc(local)).ToUnixTimeMilliseconds();
                    }
                }
                catch (Exception) { }
            }
            // No folder: filenotifyd took the time it looked; keep the first, so the album stays still.
            if (mtime == 0)
                lock (firstSeen) { if (!firstSeen.TryGetValue(path, out mtime)) firstSeen[path] = mtime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }
            var offset = (long)TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.FromUnixTimeMilliseconds(mtime)).TotalMilliseconds;
            return (mtime - offset) / 1000;
        }

        private static string Ext(string name)
        {
            var dot = name.LastIndexOf('.');
            return dot < 0 ? "" : name.Substring(dot + 1).ToLowerInvariant();
        }

        private static JObj Spec(string id, object[] indexes, bool extends)
        {
            return KindSpec.Kind(id, Owner, KindSpec.Indexes(indexes), extends ? Types : null);
        }

        private static string[] L(params string[] names) { return names; }
        private static JObj I(string name, params JObj[] props) { return KindSpec.Index(name, props); }
        private static JObj Pr(string name, string collate = null, string tokenize = null) { return KindSpec.Prop(name, collate, tokenize); }

        // filenotifyd's registrations (filenotifyd_module_mediaParent.js, _image.js, _video.js).
        private static JObj TypesKind()
        {
            return Spec(Types, new object[] {
                L("path"), L("albumPath"), L("albumId"), L("appCacheComplete", "albumId"),
                L("appCacheComplete", "type", "albumId"), L("mediaType"), L("appCacheComplete", "albumId", "createdTime"),
                I("albumCountChangedWatch", Pr("appCacheComplete"), Pr("albumId"), Pr("_rev")).Put("incDel", true),
                L("albumPath", "createdTime"), L("_del") }, false);
        }

        private static JObj AlbumKind()
        {
            return Spec(Album, new object[] {
                L("path"), L("type"), L("type", "modifiedTime"), L("accountId"), L("modifiedTime"),
                L("toBeDeleted"), L("accountId", "modifiedTime"), L("name"), L("name", "type"), L("showAlbum"),
                L("showAlbum", "modifiedTime"), L("showAlbum", "type", "modifiedTime"), L("showAlbum", "accountId", "modifiedTime"),
                I("sortKeyIndex", Pr("sortKey", "primary")), I("searchKeyIndex", Pr("searchKey", "primary", "default")),
                L("_rev") }, false);
        }

        private static JObj ImageKind()
        {
            return Spec(Image, new object[] {
                L("path"), L("albumId"), L("type"), L("albumId", "createdTime"), L("appCacheComplete", "albumId"),
                L("albumPath", "createdTime"), L("mediaType"), L("_rev") }, true);
        }

        private static JObj VideoKindSpec()
        {
            return Spec(VideoKind, new object[] {
                L("path"), L("albumPath"), L("mediaType"), L("albumPath", "createdTime"), L("albumId"),
                L("capturedOnDevice", "albumId"), L("appCacheComplete", "albumId"),
                I("loadedVidsIndex", Pr("capturedOnDevice"), Pr("searchKey", "primary", "default")),
                I("customCameraIndex", Pr("capturedOnDevice"), Pr("albumId"), Pr("createdTime", "primary", "default")),
                I("capturedVidsIndex", Pr("capturedOnDevice"), Pr("createdTime"), Pr("searchKey", "primary", "default")),
                I("sideloadedListIndex", Pr("capturedOnDevice"), Pr("title", "primary")),
                L("capturedOnDevice", "modifiedTime"), L("capturedOnDevice", "createdTime"), L("_rev") }, true);
        }

        // filenotifyd_module_audio.js's registration of com.palm.media.audio.file.
        private static JObj AudioKindSpec()
        {
            return Spec(AudioKind, new object[] {
                L("path"), L("hasResizedThumbnails", "_del"),
                I("searchAllSongs", Pr("isRingtone"), Pr("searchKey", "primary", "default")),
                I("songsByAlbumWithGenre", Pr("isRingtone"), Pr("genre"), Pr("album", "primary")),
                I("songsByArtistWithGenre", Pr("isRingtone"), Pr("genre"), Pr("artist", "primary")),
                I("songsByGenre", Pr("isRingtone"), Pr("genre"), Pr("title", "primary")),
                I("songsByArtist", Pr("isRingtone"), Pr("artist"), Pr("sortKey.trackAndDisc"), Pr("title", "primary")),
                I("songsByArtistAlbums", Pr("isRingtone"), Pr("artist"), Pr("sortKey.albumDiscAndTrack"), Pr("title", "primary")),
                I("AllSongsByArtistAlbumTrackAndDisc", Pr("isRingtone"), Pr("sortKey.artistAlbumDiscAndTrack", "primary")),
                I("songsByAlbum", Pr("isRingtone"), Pr("album"), Pr("sortKey.trackAndDisc"), Pr("title", "primary")),
                I("songsByAlbumArist", Pr("isRingtone"), Pr("album"), Pr("albumArtist"), Pr("sortKey.trackAndDisc"), Pr("title", "primary")),
                I("songsByArtistAlbum", Pr("isRingtone"), Pr("artist"), Pr("album"), Pr("sortKey.trackAndDisc"), Pr("title", "primary")),
                L("isRingtone", "album", "albumArtist"),
                I("songsByTitle", Pr("isRingtone"), Pr("title", "primary")),
                I("AllSongsByArtist", Pr("isRingtone"), Pr("artist", "primary")),
                L("isRingtone", "title", "artist"),
                I("AllSongsByAlbum", Pr("isRingtone"), Pr("album", "primary")),
                I("AllSongsByAlbumTrackAndDisc", Pr("isRingtone"), Pr("sortKey.albumDiscAndTrack", "primary")),
                I("serviced", Pr("serviced")), I("hasResizedThumbnails", Pr("hasResizedThumbnails")),
                L("_rev") }, false);
        }

        private static JArr Permissions(string kind) { return KindSpec.Permissions(kind, Consumers[kind], AllOperations); }
    }

    /// webOS's file index for documents (LunaCore's FileIndex.kt): the
    /// `com.palm.media.misc.file:1` records Quick Office, Adobe Reader and the system UI list
    /// files from, answered from the files themselves as filenotifyd's "unknown" module wrote
    /// them: name and searchKey (the file name without its extension), path, extension, size,
    /// modifiedTime in seconds.
    internal sealed class FileIndex
    {
        public const string Kind = "com.palm.media.misc.file:1";
        private const string Owner = "com.palm.filenotifyd";
        private static readonly string[] Consumers = { "com.palm.systemui", "com.quickoffice.webos", "com.quickoffice.ar", "com.palm.mojo-systemui" };
        private static readonly HashSet<string> Media = new HashSet<string> {
            "mp3", "m4a", "m4b", "aac", "wav", "ogg", "oga", "flac", "amr", "qcp", "wma", "mid", "midi",
            "jpg", "jpeg", "png", "gif", "bmp", "webp", "heic",
            "mp4", "m4v", "mov", "3gp", "3g2", "avi", "mkv", "webm", "wmv" };

        private readonly UserFiles user;
        public FileIndex(UserFiles user) { this.user = user; }

        /// Audio, images and video had kinds of their own; everything else is a document.
        public static bool IsDocument(string name)
        {
            var dot = name.LastIndexOf('.');
            return !Media.Contains(dot < 0 ? "" : name.Substring(dot + 1).ToLowerInvariant());
        }

        public void Attach(Db8 db)
        {
            var spec = KindSpec.Kind(Kind, Owner, new JArr {
                KindSpec.Index("path", KindSpec.Prop("path")),
                KindSpec.Index("rev", KindSpec.Prop("_rev")),
                KindSpec.Index("revPath", KindSpec.Prop("_rev"), KindSpec.Prop("path")),
                KindSpec.Index("searchKeyIndex", KindSpec.Prop("searchKey", "primary", "default")),
                KindSpec.Index("searchKeyIndexOnExtension", KindSpec.Prop("extension"), KindSpec.Prop("searchKey", "primary", "default")),
                KindSpec.Index("listIndexOnExtension", KindSpec.Prop("extension"), KindSpec.Prop("name", "primary")),
                KindSpec.Index("listIndex", KindSpec.Prop("name", "primary")),
            });
            db.MirrorKind(spec, KindSpec.Permissions(Kind, Consumers, "read", "update"),
                () => MediaFiles.Walk(user, "document index", f => IsDocument(f.Name) ? Record(f) : null));
        }

        private static JObj Record(MediaFile f)
        {
            var name = NameOf(f.Path);
            return new JObj().Put("path", f.Path).Put("name", name).Put("searchKey", name)
                .Put("extension", ExtensionOf(f.Path)).Put("size", f.Size).Put("modifiedTime", f.Modified / 1000);
        }

        /// filenotifyd's parseNameFromPath: the last segment, up to its last dot.
        private static string NameOf(string path)
        {
            int slash = path.LastIndexOf('/');
            int start = slash != -1 && slash + 1 < path.Length ? slash + 1 : 0;
            int dot = path.LastIndexOf('.');
            int end = dot > slash ? dot : path.Length;
            return path.Substring(start, end - start);
        }

        /// filenotifyd's parseExtensionFromPath: after the last dot, if it is in the last segment.
        private static string ExtensionOf(string path)
        {
            int slash = path.LastIndexOf('/'), dot = path.LastIndexOf('.');
            return slash >= dot || dot == -1 ? "" : path.Substring(dot + 1);
        }
    }

    /// Photos & Videos' screennails at the screen's size (AndroidLuna's PhotoCache.kt;
    /// LunaRuntimes/photos-service 0001): the photos service fits each screennail into a box the
    /// screen's longer side square (LUNACY_SCREEN_PX, which the service page's node.js gives from
    /// the screen). Screennails made at another size are cleared once: their files deleted and
    /// `appScreenNail` nulled, so Photos asks the service for each again as it shows it.
    internal static class PhotoCache
    {
        private const string Side = "screenNailSide";

        public static void Refresh(Bus bus, string ownMediaInternal, int screenPx)
        {
            var settings = ApplicationData.Current.LocalSettings.Values;
            object was;
            if (screenPx <= 0 || (settings.TryGetValue(Side, out was) && was is int && (int)was == screenPx)) return;
            int gone = 0;
            try
            {
                var generated = Path.Combine(ownMediaInternal, ".photosApp", "Generated");
                if (Directory.Exists(generated))
                    foreach (var f in Directory.EnumerateFiles(generated, "appScreenNail-*")) { try { File.Delete(f); gone++; } catch (Exception) { } }
            }
            catch (Exception) { }
            var p = new JObj().Put("query", new JObj().Put("from", MediaIndex.Image))
                .Put("props", new JObj().Put("appScreenNail", JNull.Value));
            // As the photos service, whose property it is.
            bus.DispatchPrivate("com.palm.service.photos", "palm://com.palm.db/merge", p.ToString(), r =>
            {
                var o = Jx.ObjOrEmpty(r);
                if (o.Bool("returnValue"))
                {
                    settings[Side] = screenPx;
                    LunaLog.Write("[photos] screennails cleared for a " + screenPx + "-px screen (" + gone + " files)");
                }
                else LunaLog.Write("[photos] couldn't clear screennails: " + o.Str("errorText"));
            });
        }
    }
}
