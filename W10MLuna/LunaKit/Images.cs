using System;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace LunaKit
{
    /// Decoding, scaling and encoding pictures with Windows' imaging (WIC through
    /// Windows.Graphics.Imaging): what AndroidLuna's Platform.images does with BitmapFactory.
    /// Every method is off the UI thread's context (ConfigureAwait(false)), so a caller may wait.
    internal static class Images
    {
        public const int MaxSide = 4096;

        public sealed class Picture
        {
            public int Width, Height;
            public string Type;   // "jpeg", "png", "gif", "bmp"...
        }

        private static async Task<BitmapDecoder> Decoder(byte[] data)
        {
            var ms = new InMemoryRandomAccessStream();
            await ms.WriteAsync(data.AsBuffer()).AsTask().ConfigureAwait(false);
            ms.Seek(0);
            return await BitmapDecoder.CreateAsync(ms).AsTask().ConfigureAwait(false);
        }

        /// The picture's size and type, or null if Windows can't read it.
        public static async Task<Picture> Info(byte[] data)
        {
            try
            {
                var d = await Decoder(data).ConfigureAwait(false);
                var mime = d.DecoderInformation.MimeTypes.FirstOrDefault() ?? "";
                var type = mime.Contains("/") ? mime.Substring(mime.IndexOf('/') + 1).ToLowerInvariant() : "";
                if (type == "x-ms-bmp") type = "bmp";
                return new Picture { Width = (int)d.OrientedPixelWidth, Height = (int)d.OrientedPixelHeight, Type = type };
            }
            catch (Exception) { return null; }
        }

        /// The picture scaled to scaledW x scaledH (which may stretch it), then the window
        /// (left, top, outW, outH) of that cut out, encoded as JPEG (over black) or PNG.
        public static async Task<byte[]> Draw(byte[] data, int scaledW, int scaledH, int left, int top, int outW, int outH, bool jpeg)
        {
            var d = await Decoder(data).ConfigureAwait(false);
            scaledW = Math.Max(1, scaledW); scaledH = Math.Max(1, scaledH);
            outW = Math.Max(1, Math.Min(outW, scaledW)); outH = Math.Max(1, Math.Min(outH, scaledH));
            left = Math.Max(0, Math.Min(left, scaledW - outW)); top = Math.Max(0, Math.Min(top, scaledH - outH));
            var t = new BitmapTransform
            {
                ScaledWidth = (uint)scaledW, ScaledHeight = (uint)scaledH,
                InterpolationMode = BitmapInterpolationMode.Fant,
                Bounds = new BitmapBounds { X = (uint)left, Y = (uint)top, Width = (uint)outW, Height = (uint)outH }
            };
            var px = await d.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, t,
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb).AsTask().ConfigureAwait(false);
            return await Encode(px.DetachPixelData(), outW, outH, jpeg).ConfigureAwait(false);
        }

        /// BGRA, premultiplied, encoded. A JPEG drops the alpha: premultiplied colours are the
        /// picture over black, as webOS's image service drew a transparent one.
        public static async Task<byte[]> Encode(byte[] bgra, int w, int h, bool jpeg)
        {
            var outStream = new InMemoryRandomAccessStream();
            var e = await BitmapEncoder.CreateAsync(jpeg ? BitmapEncoder.JpegEncoderId : BitmapEncoder.PngEncoderId, outStream).AsTask().ConfigureAwait(false);
            e.SetPixelData(BitmapPixelFormat.Bgra8, jpeg ? BitmapAlphaMode.Ignore : BitmapAlphaMode.Premultiplied, (uint)w, (uint)h, 96, 96, bgra);
            await e.FlushAsync().AsTask().ConfigureAwait(false);
            var bytes = new byte[outStream.Size];
            outStream.Seek(0);
            await outStream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None).AsTask().ConfigureAwait(false);
            return bytes;
        }

        /// Fitted inside w x h, keeping its shape (grown too when `grow`).
        public static int[] Fit(int sw, int sh, int w, int h, bool grow)
        {
            double s = Math.Min((double)w / sw, (double)h / sh);
            if (!grow) s = Math.Min(s, 1);
            return new[] { Math.Max(1, (int)Math.Round(sw * s)), Math.Max(1, (int)Math.Round(sh * s)) };
        }

        /// Exactly w x h, filled: scaled to cover, the middle kept.
        public static async Task<byte[]> Fill(byte[] data, Picture p, int w, int h, bool jpeg)
        {
            double s = Math.Max((double)w / p.Width, (double)h / p.Height);
            int sw = Math.Max(w, (int)Math.Ceiling(p.Width * s)), sh = Math.Max(h, (int)Math.Ceiling(p.Height * s));
            return await Draw(data, sw, sh, (sw - w) / 2, (sh - h) / 2, w, h, jpeg).ConfigureAwait(false);
        }

        /// A JPEG copy whose short side is about `size`: AppServer's ?__lunacy_thumb.
        public static async Task<byte[]> Thumbnail(byte[] data, int size)
        {
            var p = await Info(data).ConfigureAwait(false);
            if (p == null) return null;
            int shortSide = Math.Min(p.Width, p.Height);
            double s = Math.Min(1.0, (double)size / shortSide);
            int w = Math.Max(1, (int)Math.Round(p.Width * s)), h = Math.Max(1, (int)Math.Round(p.Height * s));
            return await Draw(data, w, h, 0, 0, w, h, true).ConfigureAwait(false);
        }

        /// A square JPEG of the middle of the picture, `size` on a side: a wallpaper's thumbnail.
        public static async Task<byte[]> CoverThumbnail(byte[] data, int size)
        {
            var p = await Info(data).ConfigureAwait(false);
            if (p == null) return null;
            return await Fill(data, p, size, size, true).ConfigureAwait(false);
        }

        public static T Wait<T>(Task<T> t) { return t.GetAwaiter().GetResult(); }
    }

    /// webOS's thumbnailer (LunaCore's Extractfs.kt): /var/luna/data/extractfs/&lt;path&gt;:&lt;box&gt;, a
    /// FUSE filesystem on a device. The short form ends offset:length:w:h:x, the long one
    /// :3:...:format:offset:length:w:h:crop (format 1/5 JPEG, 2 PNG, 3 the sizes as JSON, 4
    /// the embedded bytes; crop 4 fills the box).
    internal static class Extractfs
    {
        private const int MaxEmbedded = 16 * 1024 * 1024;

        public sealed class Result { public byte[] Bytes; public string Mime; }

        public static async Task<Result> Read(string spec, Func<string, byte[]> open)
        {
            try
            {
                var parts = spec.Split(':');
                if (parts.Length >= 13 && parts[parts.Length - 12] == "" && parts[parts.Length - 11] == "3") return await Long(parts, open).ConfigureAwait(false);
                return await Short(parts, open).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                LunaLog.Write("[extractfs] " + spec + ": " + e.Message);
                return null;
            }
        }

        private static int[] Ints(string[] parts, int count)
        {
            var r = new int[count];
            for (int i = 0; i < count; i++) if (!int.TryParse(parts[parts.Length - count + i], out r[i])) return null;
            return r;
        }

        private static byte[] Source(string[] parts, int drop, int offset, int length, Func<string, byte[]> open)
        {
            var path = string.Join(":", parts.Take(parts.Length - drop));
            var all = open(path);
            if (all == null) return null;
            if (length <= 0) return all;
            if (offset < 0 || offset + length > all.Length) return null;
            var b = new byte[length];
            System.Buffer.BlockCopy(all, offset, b, 0, length);
            return b;
        }

        private static async Task<Result> Short(string[] parts, Func<string, byte[]> open)
        {
            if (parts.Length < 6) return null;
            var box = Ints(parts, 5);
            if (box == null) return null;
            int w = box[2], h = box[3];
            if (w < 1 || w > Images.MaxSide || h < 1 || h > Images.MaxSide) return null;
            if (box[0] < 0 || box[1] < 0 || box[1] > MaxEmbedded) return null;
            var data = Source(parts, 5, box[0], box[1], open);
            if (data == null) return null;
            var p = await Images.Info(data).ConfigureAwait(false);
            if (p == null) return null;
            var o = Images.Fit(p.Width, p.Height, w, h, false);
            return new Result { Bytes = await Images.Draw(data, o[0], o[1], 0, 0, o[0], o[1], false).ConfigureAwait(false), Mime = "image/png" };
        }

        private static async Task<Result> Long(string[] parts, Func<string, byte[]> open)
        {
            var n = Ints(parts, 11);
            if (n == null) return null;
            int format = n[5], offset = n[6], length = n[7], w = n[8], h = n[9], crop = n[10];
            if (w < 1 || w > Images.MaxSide || h < 1 || h > Images.MaxSide || length > MaxEmbedded) return null;
            var data = Source(parts, 12, offset, length, open);
            if (data == null) return null;
            if (format == 4) return new Result { Bytes = data, Mime = "application/octet-stream" };
            var p = await Images.Info(data).ConfigureAwait(false);
            if (p == null) return null;
            bool fill = crop == 4;
            var o = fill ? new[] { w, h } : Images.Fit(p.Width, p.Height, w, h, true);
            if (format == 3)
            {
                var json = "{\"original-width\":" + p.Width + ",\"original-height\":" + p.Height + ",\"output-width\":" + o[0] + ",\"output-height\":" + o[1] + "}";
                return new Result { Bytes = System.Text.Encoding.UTF8.GetBytes(json), Mime = "application/json" };
            }
            bool jpeg = format == 1 || format == 5;
            var bytes = fill ? await Images.Fill(data, p, w, h, jpeg).ConfigureAwait(false)
                : await Images.Draw(data, o[0], o[1], 0, 0, o[0], o[1], jpeg).ConfigureAwait(false);
            return new Result { Bytes = bytes, Mime = jpeg ? "image/jpeg" : "image/png" };
        }
    }

    /// palm://com.palm.image (AndroidLuna's ImageService.kt): convert (a copy in another format,
    /// or a scaled crop around a focus point), ezResize (stretched to a size) and imageInfo.
    /// Replies carry subscribed:false and an errorCode string, as the TouchPad's did. Paths are
    /// webOS paths, user storage included (WebosFs).
    internal sealed class ImageService
    {
        private const string Service = "com.palm.image";
        private readonly WebosFs fs;
        public ImageService(WebosFs fs) { this.fs = fs; }

        private sealed class ImageError : Exception { public ImageError(string m) : base(m) { } }

        public void Register(Bus bus)
        {
            foreach (var m in new[] { "convert", "ezResize", "imageInfo" })
            {
                var method = m;
                bus.Register(Service, method, c =>
                {
                    var p = Jx.ObjOrEmpty(c.Raw);
                    Task.Run(async () =>
                    {
                        JObj r;
                        try { r = method == "convert" ? await Convert(p) : method == "ezResize" ? await EzResize(p) : await ImageInfo(p); }
                        catch (ImageError e) { r = Fail(e.Message); }
                        catch (Exception e) { r = Fail(method + ": " + e.Message); }
                        c.Reply(r.ToString());
                    });
                });
            }
        }

        private static JObj Ok() { return new JObj().Put("subscribed", false).Put("returnValue", true); }
        private static JObj Fail(string text) { return new JObj().Put("subscribed", false).Put("returnValue", false).Put("errorCode", text); }

        private static string Need(JObj p, string key)
        {
            var v = p.Str(key);
            if (!p.Has(key) || v.Length == 0) throw new ImageError("'" + key + "' parameter missing");
            return v;
        }

        private byte[] Source(JObj p)
        {
            var path = Need(p, "src");
            if (path.StartsWith("file://", StringComparison.Ordinal)) path = path.Substring(7);
            try { if (!fs.IsDir(path)) return fs.Read(path); } catch (WebosFs.FsError) { }
            throw new ImageError("source file does not exist");
        }

        private void Save(byte[] bytes, string dest)
        {
            if (dest.StartsWith("file://", StringComparison.Ordinal)) dest = dest.Substring(7);
            try
            {
                var dir = WebosFs.Normal(dest + "/..");
                if (!fs.Exists(dir)) fs.MakeDir(dir, true);
                fs.Write(dest, bytes, false);
            }
            catch (WebosFs.FsError) { throw new ImageError("Unable to write destination file"); }
        }

        private static bool Jpeg(string type)
        {
            switch (type.ToLowerInvariant())
            {
                case "png": return false;
                case "jpg": case "jpeg": return true;
                default: throw new ImageError("Unsupported destination type: " + type);
            }
        }

        private static async Task<Images.Picture> Read(byte[] data)
        {
            var info = await Images.Info(data);
            if (info == null) throw new ImageError("Unable to read image");
            return info;
        }

        private async Task<JObj> Convert(JObj p)
        {
            var data = Source(p);
            var dest = Need(p, "dest");
            var type = Need(p, "destType");
            bool jpeg = Jpeg(type);
            var info = await Read(data);
            bool crop = new[] { "focusX", "focusY", "scale", "cropW", "cropH" }.Any(p.Has);
            double scale = 1;
            if (crop)
            {
                double focusX = p.Has("focusX") ? Jx.Num(p["focusX"]) : 0.5, focusY = p.Has("focusY") ? Jx.Num(p["focusY"]) : 0.5;
                if (focusX < 0 || focusX > 1) throw new ImageError("'focusX' parameter out of range (must be [0.0,1.0] )");
                if (focusY < 0 || focusY > 1) throw new ImageError("'focusY' parameter out of range (must be [0.0,1.0] )");
                scale = p.Has("scale") ? Jx.Num(p["scale"]) : 1.0;
                if (scale <= 0) throw new ImageError("'scale' parameter out of range ( must be > 0.0 )");
                int cropW = (int)(p.Has("cropW") ? Jx.Num(p["cropW"]) : 0), cropH = (int)(p.Has("cropH") ? Jx.Num(p["cropH"]) : 0);
                if (cropW <= 0 || cropH <= 0) throw new ImageError("'cropW' parameter out of range (must be > 0 )");
                // A scale of 1 is a straight copy, as the TouchPad's service gave.
                if (scale != 1.0)
                {
                    double sw = info.Width * scale, sh = info.Height * scale;
                    int outW = Math.Max(1, (int)Math.Round(Math.Min(cropW, sw))), outH = Math.Max(1, (int)Math.Round(Math.Min(cropH, sh)));
                    double left = Math.Max(0, Math.Min(focusX * sw - outW / 2.0, Math.Max(0, sw - outW)));
                    double top = Math.Max(0, Math.Min(focusY * sh - outH / 2.0, Math.Max(0, sh - outH)));
                    Save(await Images.Draw(data, (int)Math.Round(sw), (int)Math.Round(sh), (int)Math.Round(left), (int)Math.Round(top), outW, outH, jpeg), dest);
                    return Ok();
                }
            }
            Save(await Images.Draw(data, info.Width, info.Height, 0, 0, info.Width, info.Height, jpeg), dest);
            return Ok();
        }

        private async Task<JObj> EzResize(JObj p)
        {
            var data = Source(p);
            var dest = Need(p, "dest");
            var type = Need(p, "destType");
            if (!p.Has("destSizeW")) throw new ImageError("'destSizeW' missing");
            if (!p.Has("destSizeH")) throw new ImageError("'destSizeH' missing");
            int w = p.Int("destSizeW"), h = p.Int("destSizeH");
            if (w <= 0 || h <= 0 || w > Images.MaxSide * 2 || h > Images.MaxSide * 2) throw new ImageError("ezResize: unable to allocate memory for QImage");
            bool jpeg = Jpeg(type);
            await Read(data);
            Save(await Images.Draw(data, w, h, 0, 0, w, h, jpeg), dest);
            return Ok();
        }

        private async Task<JObj> ImageInfo(JObj p)
        {
            var info = await Read(Source(p));
            return Ok().Put("width", info.Width).Put("height", info.Height).Put("bpp", 8).Put("type", info.Type);
        }
    }
}
