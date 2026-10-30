using System.IO.Compression;
using ClosedXML.Excel;
using ImageMagick;
using ImageMagick.Drawing;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using Poppy.Core.Engines;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Poppy.Core.Tests;

/// <summary>
/// Builds one small sample of every supported input type, once per test run, using the same
/// engines the app ships with (FFmpeg, Pandoc, Magick, PDFsharp).
/// </summary>
public static class Samples
{
    public static readonly string Dir = Path.Combine(Path.GetTempPath(), "PoppyTests", "samples");
    static readonly object Gate = new();
    static bool built;

    public static string Get(string name)
    {
        Build();
        var p = Path.Combine(Dir, name);
        if (!File.Exists(p) && !Directory.Exists(p)) throw new FileNotFoundException("Sample not built", p);
        return p;
    }

    /// <summary>A private copy so every test writes its outputs into its own folder.</summary>
    public static string Copy(string name, out string folder)
    {
        var src = Get(name);
        folder = Path.Combine(Path.GetTempPath(), "PoppyTests", "runs", Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(folder);
        var dst = Path.Combine(folder, name);
        if (Directory.Exists(src)) CopyDir(src, dst);
        else File.Copy(src, dst);
        return dst;
    }

    static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(src)) CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    static void Build()
    {
        lock (Gate)
        {
            if (built) return;
            if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
            Directory.CreateDirectory(Dir);
            Images();
            Media();
            Pdf();
            Documents();
            Sheets();
            Subtitles();
            Archives();
            built = true;
        }
    }

    static string P(string name) => Path.Combine(Dir, name);

    static void Images()
    {
        using (var img = new MagickImage("gradient:#ff7a45-#7c3aed", 640, 400))
        {
            img.Alpha(AlphaAction.Set);
            new Drawables().FillColor(MagickColors.White).Circle(320, 200, 320, 300).Draw(img);
            // Transparent corner so alpha handling is exercised.
            new Drawables().FillColor(MagickColors.Transparent).Rectangle(0, 0, 120, 80).Draw(img);
            img.Write(P("sample.png"));
        }
        using (var img = new MagickImage(P("sample.png")))
        {
            img.Alpha(AlphaAction.Remove);
            img.Quality = 92;
            // An EXIF orientation tag so metadata stripping has something to strip.
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Make, "PoppyCam");
            img.SetProfile(exif);
            img.Write(P("sample.jpg"));
            img.Write(P("sample.webp"));
            img.Write(P("sample.bmp"));
            img.Write(P("sample.tiff"));
        }
        using (var frames = new MagickImageCollection())
        {
            foreach (var color in new[] { "#ff7a45", "#22c55e", "#3b82f6" })
            {
                var f = new MagickImage(new MagickColor(color), 120, 120) { AnimationDelay = 20 };
                frames.Add(f);
            }
            frames.Write(P("animated.gif"));
        }
        File.WriteAllText(P("sample.svg"), """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="200" viewBox="0 0 200 200">
              <circle cx="100" cy="100" r="80" fill="#ff6a3d"/><circle cx="100" cy="100" r="30" fill="#3b1f0e"/>
            </svg>
            """);
        using (var img = new MagickImage(P("sample.jpg")))
        {
            // A real HEVC-coded HEIC, like an iPhone photo, made with the Windows encoder.
            if (Ops.WicImaging.CanEncodeHeic)
                File.WriteAllBytes(P("sample.heic"), Ops.WicImaging.EncodeHeic(img.ToByteArray(MagickFormat.Png), 0.8).GetAwaiter().GetResult());
            if (Ops.ImageOps.CanWrite("avif")) img.Write(P("sample.avif"), MagickFormat.Avif);
        }
    }

    static void Ffmpeg(params string[] args)
    {
        var r = ProcessRunner.Run(Engines.Engines.Ffmpeg!, ["-y", "-hide_banner", "-loglevel", "error", .. args], TimeSpan.FromMinutes(2));
        if (!r.Ok) throw new Exception("ffmpeg sample failed: " + r.StdErr);
    }

    static void Media()
    {
        if (!Engines.Engines.HasFfmpeg) return;
        Ffmpeg("-f", "lavfi", "-i", "testsrc2=size=320x240:rate=25:duration=3", "-f", "lavfi", "-i", "sine=frequency=440:duration=3:sample_rate=44100",
            "-ac", "2", "-c:v", Engines.Engines.PickEncoder("libx264", "libopenh264", "h264_mf", "mpeg4"), "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", "-metadata", "title=Poppy sample", P("sample.mp4"));
        Ffmpeg("-i", P("sample.mp4"), "-c:v", "libvpx-vp9", "-b:v", "300k", "-deadline", "realtime", "-cpu-used", "8", "-c:a", "libopus", P("sample.webm"));
        Ffmpeg("-i", P("sample.mp4"), "-c", "copy", P("sample.mkv"));
        Ffmpeg("-i", P("sample.mp4"), "-c:v", "mpeg4", "-c:a", "libmp3lame", P("sample.avi"));
        Ffmpeg("-f", "lavfi", "-i", "sine=frequency=330:duration=4:sample_rate=44100", "-af", "pan=stereo|c0=c0|c1=c0", P("sample.wav"));
        Ffmpeg("-i", P("sample.wav"), "-c:a", "libmp3lame", "-q:a", "4", "-metadata", "artist=Poppy", P("sample.mp3"));
        Ffmpeg("-i", P("sample.wav"), "-c:a", "flac", P("sample.flac"));
        Ffmpeg("-i", P("sample.wav"), "-c:a", "aac", P("sample.m4a"));
        Ffmpeg("-i", P("sample.wav"), "-c:a", "libvorbis", P("sample.ogg"));
    }

    public static void EnsureFonts()
    {
        if (GlobalFontSettings.FontResolver == null) GlobalFontSettings.UseWindowsFontsUnderWindows = true;
    }

    static void Pdf()
    {
        EnsureFonts();
        using var doc = new PdfDocument();
        doc.Info.Title = "Secret title";
        doc.Info.Author = "Poppy Tester";
        var font = new XFont("Arial", 20);
        // A big noisy photo so compression has real work to do.
        var photo = P("photo-for-pdf.jpg");
        using (var img = new MagickImage("plasma:fractal", 2400, 1600)) { img.Quality = 95; img.Write(photo); }
        for (int i = 1; i <= 3; i++)
        {
            var page = doc.AddPage();
            using var g = XGraphics.FromPdfPage(page);
            g.DrawString($"Poppy test page {i}", font, XBrushes.Black, new XPoint(60, 80));
            if (i == 1)
            {
                using var x = XImage.FromFile(photo);
                g.DrawImage(x, 60, 120, 480, 320);
            }
        }
        doc.Save(P("sample.pdf"));
        using var two = new PdfDocument();
        var p2 = two.AddPage();
        using (var g = XGraphics.FromPdfPage(p2)) g.DrawString("Second document", font, XBrushes.Black, new XPoint(60, 80));
        two.Save(P("second.pdf"));
    }

    const string Markdown = """
        # Poppy Sample

        Hello **world**. Türkçe karakterler: çğıöşü ÇĞİÖŞÜ.

        - one
        - two

        | a | b |
        |---|---|
        | 1 | 2 |
        """;

    static void Documents()
    {
        File.WriteAllText(P("sample.md"), Markdown);
        File.WriteAllText(P("sample.txt"), "Poppy plain text.\r\nSecond line çğış.\r\n");
        File.WriteAllText(P("sample.html"), "<!doctype html><html><head><meta charset=\"utf-8\"><title>Sample</title></head><body><h1>Poppy Sample</h1><p>Hello <b>world</b> çğış.</p></body></html>");
        if (Engines.Engines.Pandoc == null) return;
        void Pandoc(string to, string output, string input = "sample.md")
        {
            var r = ProcessRunner.Run(Engines.Engines.Pandoc!, ["-s", "--metadata", "title=Poppy Sample", "-o", P(output), "-t", to, P(input)], TimeSpan.FromMinutes(1));
            if (!r.Ok) throw new Exception("pandoc sample failed: " + r.StdErr);
        }
        Pandoc("docx", "sample.docx");
        Pandoc("odt", "sample.odt");
        Pandoc("rtf", "sample.rtf");
        Pandoc("epub", "sample.epub");
        File.WriteAllText(P("slides.md"), "# Slide one\n\nHello\n\n# Slide two\n\n- a\n- b\n");
        Pandoc("pptx", "sample.pptx", "slides.md");
    }

    static void Sheets()
    {
        File.WriteAllText(P("sample.csv"), "name;city;score\r\nAyşe;\"İstanbul; Kadıköy\";90\r\nJohn \"JJ\";London;85.5\r\n");
        File.WriteAllText(P("sample.tsv"), "name\tscore\r\nA\t1\r\nB\t2\r\n");
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Data");
        ws.Cell(1, 1).Value = "product"; ws.Cell(1, 2).Value = "price";
        ws.Cell(2, 1).Value = "Poppy Pro"; ws.Cell(2, 2).Value = 9.99;
        ws.Cell(3, 1).Value = "Çiçek"; ws.Cell(3, 2).Value = 3;
        wb.SaveAs(P("sample.xlsx"));
    }

    public const string Srt = "1\r\n00:00:01,000 --> 00:00:02,500\r\nHello <i>there</i>\r\n\r\n2\r\n00:00:03,000 --> 00:00:04,000\r\nSecond line\r\nTwo rows\r\n\r\n";

    static void Subtitles()
    {
        File.WriteAllText(P("sample.srt"), Srt);
        File.WriteAllText(P("sample.vtt"), "WEBVTT\n\n00:01.000 --> 00:02.500\nHello there\n\n00:00:03.000 --> 00:00:04.000\nSecond line\n\n");
        if (Engines.Engines.Ffmpeg != null) Ffmpeg("-i", P("sample.srt"), P("sample.ass"));
    }

    static void Archives()
    {
        var folder = P("folder");
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        File.WriteAllText(Path.Combine(folder, "a.txt"), "A");
        File.WriteAllText(Path.Combine(folder, "sub", "b.txt"), "B");
        ZipFile.CreateFromDirectory(folder, P("sample.zip"), CompressionLevel.Optimal, includeBaseDirectory: true);
        using (var fs = File.Create(P("sample.tar.gz")))
        using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
            System.Formats.Tar.TarFile.CreateFromDirectory(folder, gz, includeBaseDirectory: true);
        File.WriteAllText(P("notes.xyz"), "unknown type");
    }
}
