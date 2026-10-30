using ImageMagick;
using Poppy.Core.Engines;

namespace Poppy.Core.Ops;

/// <summary>Image conversion and photo tools, built on Magick.NET.</summary>
public static class ImageOps
{
    static readonly HashSet<string> Animated = ["gif", "webp"];

    public static MagickFormat FormatFor(string ext) => ext switch
    {
        "png" => MagickFormat.Png,
        "jpg" or "jpeg" or "jfif" => MagickFormat.Jpeg,
        "webp" => MagickFormat.WebP,
        "avif" => MagickFormat.Avif,
        "heic" or "heif" => MagickFormat.Heic,
        "tiff" or "tif" => MagickFormat.Tiff,
        "bmp" => MagickFormat.Bmp,
        "gif" => MagickFormat.Gif,
        "ico" => MagickFormat.Icon,
        "pdf" => MagickFormat.Pdf,
        _ => throw new OpException("Unsupported image format: " + ext),
    };

    /// <summary>True when Poppy can produce the format (Magick.NET, or Windows' codec for HEIC).</summary>
    public static bool CanWrite(string ext) => MagickCanWrite(ext) || ext is "heic" or "heif" && WicImaging.CanEncodeHeic;

    static bool MagickCanWrite(string ext)
    {
        try
        {
            var f = FormatFor(ext);
            return MagickNET.SupportedFormats.Any(i => i.Format == f && i.SupportsWriting);
        }
        catch (OpException) { return false; }
    }

    static MagickReadSettings ReadSettings(string path) =>
        FileKinds.Ext(path) == "svg" ? new MagickReadSettings { Density = new Density(192), BackgroundColor = MagickColors.Transparent } : new MagickReadSettings();

    static MagickImage Load(string path)
    {
        try
        {
            var img = new MagickImage(path, ReadSettings(path));
            img.AutoOrient();
            return img;
        }
        catch (MagickException) when (FileKinds.Ext(path) is "heic" or "heif" or "avif" or "jxl")
        {
            // Some HEIC photos (HEVC) need the decoder Windows ships; FFmpeg is the last resort.
            try
            {
                return new MagickImage(WicImaging.DecodeToPng(path).GetAwaiter().GetResult());
            }
            catch (Exception) when (Engines.Engines.Ffmpeg != null)
            {
                return new MagickImage(FfmpegDecode(path));
            }
        }
    }

    static byte[] FfmpegDecode(string path)
    {
        using var tmp = new TempDir();
        var png = tmp.File("decoded.png");
        var r = ProcessRunner.Run(Engines.Engines.Ffmpeg!, ["-y", "-hide_banner", "-i", path, "-frames:v", "1", png], TimeSpan.FromMinutes(1));
        if (!r.Ok || !File.Exists(png)) throw new OpException("Cannot read this image. " + r.Tail(2));
        return File.ReadAllBytes(png);
    }

    /// <summary>Writes the image in <paramref name="ext"/>; HEIC goes through the Windows encoder.</summary>
    static void Save(MagickImage img, string output, string ext)
    {
        if (ext is "heic" or "heif" && !MagickCanWrite(ext))
        {
            var quality = img.Quality is > 0 and <= 100 ? img.Quality / 100.0 : 0.8;
            File.WriteAllBytes(output, WicImaging.EncodeHeic(img.ToByteArray(MagickFormat.Png), quality).GetAwaiter().GetResult());
            return;
        }
        img.Write(output, FormatFor(ext));
    }

    /// <summary>
    /// The image as the user sees it (EXIF rotation applied) as PNG bytes, scaled to fit
    /// <paramref name="maxSide"/> (0 = full size), plus the full-size oriented dimensions.
    /// Editors use this so their coordinates match what the tools apply to.
    /// </summary>
    public static (byte[] Png, int Width, int Height) LoadOriented(string path, int maxSide)
    {
        using var img = Load(path);
        int w = (int)img.Width, h = (int)img.Height;
        if (maxSide > 0 && (img.Width > maxSide || img.Height > maxSide))
            img.Resize(new MagickGeometry((uint)maxSide, (uint)maxSide));
        return (img.ToByteArray(MagickFormat.Png), w, h);
    }

    /// <summary>Edit-dialog preview: applies the adjustments to already-decoded PNG bytes.</summary>
    public static byte[] PreviewAdjust(byte[] png, OpOptions o)
    {
        using var img = new MagickImage(png);
        ApplyAdjust(img, o);
        return img.ToByteArray(MagickFormat.Png);
    }

    // ---------- Convert ----------

    public static Task<string> ConvertAsync(string input, string target, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        var output = OutputPaths.For(input, target);
        if (!CanWrite(target)) return FfmpegImage(input, target, output, ct);

        var srcExt = FileKinds.Ext(input);
        if (Animated.Contains(srcExt) && Animated.Contains(target))
        {
            using var frames = new MagickImageCollection(input);
            if (frames.Count > 1)
            {
                frames.Coalesce();
                if (target == "gif") frames.Optimize();
                frames.Write(output, FormatFor(target));
                return output;
            }
        }

        using var img = Load(input);
        Prepare(img, target);
        Save(img, output, target);
        return output;
    }, ct);

    static void Prepare(MagickImage img, string target)
    {
        switch (target)
        {
            case "jpg" or "bmp":
                if (img.HasAlpha)
                {
                    img.BackgroundColor = MagickColors.White;
                    img.Alpha(AlphaAction.Remove);
                }
                if (target == "jpg") img.Quality = 90;
                break;
            case "webp":
                img.Quality = 85;
                break;
            case "avif" or "heic":
                img.Quality = 70;
                break;
            case "ico":
                if (img.Width > 256 || img.Height > 256) img.Resize(new MagickGeometry(256, 256));
                img.Settings.SetDefine(MagickFormat.Icon, "auto-resize", "256,128,64,48,32,16");
                break;
            case "tiff":
                img.Settings.Compression = CompressionMethod.LZW;
                break;
        }
    }

    /// <summary>Fallback encoder for formats Magick.NET cannot write in this build (AVIF/HEIC).</summary>
    static string FfmpegImage(string input, string target, string output, CancellationToken ct)
    {
        var ffmpeg = Engines.Engines.Ffmpeg ?? throw new OpException(L.T("need.ffmpeg"));
        using var tmp = new TempDir();
        var png = tmp.File("in.png");
        using (var img = Load(input)) img.Write(png, MagickFormat.Png);
        if (target != "avif") throw new OpException(L.T("need.hevc"));
        string[] codec = ["-c:v", Engines.Engines.PickEncoder("libaom-av1", "libsvtav1", "librav1e"), "-still-picture", "1", "-crf", "30", "-pix_fmt", "yuv420p"];
        var r = ProcessRunner.RunAsync(ffmpeg, ["-y", "-hide_banner", "-i", png, .. codec, "-frames:v", "1", output], null, ct).GetAwaiter().GetResult();
        if (!r.Ok) throw new OpException(r.Tail());
        return output;
    }

    // ---------- Tools ----------

    /// <summary>Smaller file in the same format. Returns null if the image cannot get smaller.</summary>
    public static Task<string?> CompressAsync(string input, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        var ext = FileKinds.Ext(input);
        // BMP has no compression; lossless PNG is the smaller equivalent.
        var outExt = ext switch { "bmp" => "png", "jpeg" or "jfif" => "jpg", "tif" => "tiff", "heif" => "heic", _ => ext };
        if (!CanWrite(outExt)) outExt = "jpg";
        var output = OutputPaths.For(input, outExt, "compressed");

        if (ext == "gif")
        {
            using var frames = new MagickImageCollection(input);
            frames.Coalesce();
            frames.Quantize(new QuantizeSettings { Colors = 128, DitherMethod = DitherMethod.FloydSteinberg });
            frames.Optimize();
            frames.OptimizeTransparency();
            frames.Write(output, MagickFormat.Gif);
        }
        else
        {
            using var img = Load(input);
            if (img.Width > 4096 || img.Height > 4096) img.Resize(new MagickGeometry(4096, 4096));
            switch (outExt)
            {
                case "jpg":
                    if (img.HasAlpha) { img.BackgroundColor = MagickColors.White; img.Alpha(AlphaAction.Remove); }
                    img.Quality = 72;
                    img.Settings.Interlace = Interlace.Plane;
                    img.Settings.SetDefine(MagickFormat.Jpeg, "sampling-factor", "4:2:0");
                    break;
                case "png":
                    // pngquant-style: 256-colour palette, which usually halves photos and screenshots.
                    img.Quantize(new QuantizeSettings { Colors = 256, DitherMethod = DitherMethod.FloydSteinberg });
                    img.Settings.SetDefine(MagickFormat.Png, "compression-level", "9");
                    break;
                case "webp":
                    img.Quality = 70;
                    img.Settings.SetDefine(MagickFormat.WebP, "method", "6");
                    break;
                case "avif" or "heic":
                    img.Quality = 45;
                    break;
                case "tiff":
                    img.Settings.Compression = CompressionMethod.Zip;
                    break;
            }
            img.Strip();
            Save(img, output, outExt);
        }

        if (ext == outExt && new FileInfo(output).Length >= new FileInfo(input).Length)
        {
            File.Delete(output);
            return null;
        }
        return output;
    }, ct);

    public static Task<string> StripMetadataAsync(string input, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        var ext = WritableExt(input);
        var output = OutputPaths.For(input, ext, "clean");
        using var img = Load(input); // AutoOrient bakes rotation in before the EXIF tag goes
        img.Strip();
        if (ext == "jpg") img.Quality = 95;
        Save(img, output, ext);
        return output;
    }, ct);

    /// <summary>mode "percent" with "value" 1-400, or mode "max" with "value" = longest side in px.</summary>
    public static Task<string> ResizeAsync(string input, OpOptions o, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        var ext = WritableExt(input);
        var output = OutputPaths.For(input, ext, "resized");
        using var img = Load(input);
        var value = Math.Max(1, o.GetDouble("value", 50));
        if (o.Get("mode", "percent") == "max")
        {
            var side = (uint)value;
            img.Resize(new MagickGeometry(side, side));
        }
        else
        {
            img.Resize(new Percentage(value));
        }
        if (ext == "jpg") img.Quality = 90;
        Save(img, output, ext);
        return output;
    }, ct);

    public static Task<string> CropAsync(string input, OpOptions o, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        var ext = WritableExt(input);
        var output = OutputPaths.For(input, ext, "cropped");
        using var img = Load(input);
        int x = Math.Clamp(o.GetInt("x"), 0, (int)img.Width - 1);
        int y = Math.Clamp(o.GetInt("y"), 0, (int)img.Height - 1);
        int w = Math.Clamp(o.GetInt("w", (int)img.Width), 1, (int)img.Width - x);
        int h = Math.Clamp(o.GetInt("h", (int)img.Height), 1, (int)img.Height - y);
        img.Crop(new MagickGeometry(x, y, (uint)w, (uint)h));
        img.ResetPage();
        if (ext == "jpg") img.Quality = 92;
        Save(img, output, ext);
        return output;
    }, ct);

    /// <summary>brightness/contrast/saturation in -100..100, rotate 0/90/180/270, flipH, flipV, grayscale.</summary>
    public static Task<string> AdjustAsync(string input, OpOptions o, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        var ext = WritableExt(input);
        var output = OutputPaths.For(input, ext, "edited");
        using var img = Load(input);
        ApplyAdjust(img, o);
        if (ext == "jpg") img.Quality = 92;
        Save(img, output, ext);
        return output;
    }, ct);

    /// <summary>Applies the Edit dialog's settings; the dialog also uses this for its preview.</summary>
    public static void ApplyAdjust(MagickImage img, OpOptions o)
    {
        var b = o.GetDouble("brightness");
        var c = o.GetDouble("contrast");
        if (b != 0 || c != 0) img.BrightnessContrast(new Percentage(b), new Percentage(c));
        var s = o.GetDouble("saturation");
        if (s != 0) img.Modulate(new Percentage(100), new Percentage(100 + s), new Percentage(100));
        if (o.GetBool("grayscale")) img.Grayscale();
        var rot = ((o.GetInt("rotate") % 360) + 360) % 360;
        if (rot != 0) img.Rotate(rot);
        if (o.GetBool("flipH")) img.Flop();
        if (o.GetBool("flipV")) img.Flip();
    }

    /// <summary>Fills transparency with "color" (#rrggbb) and adds "padding" percent of margin.</summary>
    public static Task<string> BackgroundAsync(string input, OpOptions o, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        var ext = WritableExt(input);
        var output = OutputPaths.For(input, ext, "bg");
        using var img = Load(input);
        var color = new MagickColor(o.Get("color", "#FFFFFF"));
        var pad = Math.Clamp(o.GetDouble("padding"), 0, 100);
        if (pad > 0)
        {
            var extra = (uint)Math.Round(Math.Max(img.Width, img.Height) * pad / 100);
            img.BackgroundColor = MagickColors.Transparent;
            img.Extent(img.Width + 2 * extra, img.Height + 2 * extra, Gravity.Center);
        }
        img.BackgroundColor = color;
        img.Alpha(AlphaAction.Remove);
        img.Alpha(AlphaAction.Off);
        if (ext == "jpg") img.Quality = 92;
        Save(img, output, ext);
        return output;
    }, ct);

    /// <summary>
    /// Saves a PNG rendered by the app's editor (annotate/redact) in the original's format.
    /// Redacted pixels must not survive, so metadata (and its embedded thumbnail) is stripped.
    /// </summary>
    public static string SaveEdited(string original, string renderedPng, string suffix)
    {
        var ext = WritableExt(original);
        var output = OutputPaths.For(original, ext, suffix);
        using var img = new MagickImage(renderedPng);
        img.Strip();
        if (ext is "jpg" or "bmp" && img.HasAlpha)
        {
            img.BackgroundColor = MagickColors.White;
            img.Alpha(AlphaAction.Remove);
        }
        if (ext == "jpg") img.Quality = 92;
        Save(img, output, ext);
        return output;
    }

    /// <summary>The original's format if Magick can write it, else PNG (e.g. for SVG or RAW input).</summary>
    public static string WritableExt(string input)
    {
        var ext = FileKinds.Ext(input) switch { "jpeg" or "jfif" => "jpg", "tif" => "tiff", "heif" => "heic", var e => e };
        if (ext == "heic" && !CanWrite("heic")) return "jpg";
        return CanWrite(ext) && ext != "pdf" ? ext : "png";
    }
}
