using System.IO.Compression;
using ImageMagick;
using PdfSharp.Pdf.IO;
using Poppy.Core.Ops;

namespace Poppy.Core.Tests;

/// <summary>Every tool in the Tools ring, with the options its dialog would send.</summary>
public class ToolTests
{
    static async Task<string> Run(string id, string sample, OpOptions? o = null)
    {
        var input = Samples.Copy(sample, out _);
        var r = await Runner.RunAsync(id, new OpContext { Inputs = [input], Options = o ?? new OpOptions() });
        Assert.Single(r.Outputs);
        return r.Outputs[0];
    }

    static MagickImage Img(string path) => new(path);

    // ---------- Image ----------

    [Theory]
    [InlineData("sample.jpg")]
    [InlineData("sample.png")]
    [InlineData("sample.bmp")]
    [InlineData("sample.webp")]
    public async Task ImageCompressMakesSmallerFile(string sample)
    {
        var input = Samples.Get(sample);
        var r = await Runner.RunAsync("image.compress", new OpContext { Inputs = [Samples.Copy(sample, out _)] });
        if (r.Outputs.Count == 0)
        {
            Assert.Equal(L.T("already.small"), r.Message);
            return;
        }
        Assert.True(new FileInfo(r.Outputs[0]).Length < new FileInfo(input).Length);
        Assert.Contains("-compressed", r.Outputs[0]);
    }

    [Fact]
    public async Task CompressesManyImagesInOneDrop()
    {
        var a = Samples.Copy("sample.jpg", out var dir);
        var b = Path.Combine(dir, "two.png");
        var c = Path.Combine(dir, "three.bmp");
        File.Copy(Samples.Get("sample.png"), b);
        File.Copy(Samples.Get("sample.bmp"), c);
        var r = await Runner.RunAsync("image.compress", new OpContext { Inputs = [a, b, c] });
        // Each file is compressed on its own; one that cannot shrink is skipped, never broken.
        Assert.InRange(r.Outputs.Count, 2, 3);
        foreach (var o in r.Outputs)
        {
            Assert.Contains("-compressed", o);
            var original = new[] { a, b, c }.Single(x => Path.GetFileNameWithoutExtension(o).StartsWith(Path.GetFileNameWithoutExtension(x)));
            Assert.True(new FileInfo(o).Length < new FileInfo(original).Length, o);
        }
    }

    [Fact]
    public async Task ImageMetadataIsStripped()
    {
        using (var before = Img(Samples.Get("sample.jpg"))) Assert.NotNull(before.GetExifProfile());
        var output = await Run("image.metadata", "sample.jpg");
        using var after = Img(output);
        Assert.Null(after.GetExifProfile());
    }

    [Fact]
    public async Task ImageResizePercentAndMaxSide()
    {
        using (var half = Img(await Run("image.resize", "sample.png", new OpOptions().Set("mode", "percent").Set("value", 50))))
            Assert.Equal((320u, 200u), (half.Width, half.Height));
        using var max = Img(await Run("image.resize", "sample.png", new OpOptions().Set("mode", "max").Set("value", 100)));
        Assert.Equal((100u, 63u), (max.Width, max.Height));
    }

    [Fact]
    public async Task ImageCrop()
    {
        using var img = Img(await Run("image.crop", "sample.jpg", new OpOptions().Set("x", 10).Set("y", 20).Set("w", 100).Set("h", 50)));
        Assert.Equal((100u, 50u), (img.Width, img.Height));
    }

    [Fact]
    public async Task ImageCropClampsOutOfBounds()
    {
        using var img = Img(await Run("image.crop", "sample.jpg", new OpOptions().Set("x", 600).Set("y", 380).Set("w", 500).Set("h", 500)));
        Assert.Equal((40u, 20u), (img.Width, img.Height));
    }

    [Fact]
    public async Task ImageEditRotatesAndGrays()
    {
        using var img = Img(await Run("image.edit", "sample.jpg", new OpOptions().Set("rotate", 90).Set("grayscale", true).Set("brightness", 10)));
        Assert.Equal((400u, 640u), (img.Width, img.Height));
        var p = img.GetPixels().GetPixel(300, 200).ToColor()!;
        Assert.True(Math.Abs(p.R - p.G) < 3 && Math.Abs(p.G - p.B) < 3, "not grayscale");
    }

    [Fact]
    public async Task ImageBackgroundFillsTransparencyAndPads()
    {
        using var img = Img(await Run("image.background", "sample.png", new OpOptions().Set("color", "#00FF00").Set("padding", 10)));
        Assert.Equal((768u, 528u), (img.Width, img.Height));
        Assert.False(img.HasAlpha);
        var corner = img.GetPixels().GetPixel(2, 2).ToColor()!;
        Assert.Equal((0, 255, 0), (corner.R, corner.G, corner.B));
    }

    [Fact]
    public async Task AnnotatedImageKeepsOriginalFormat()
    {
        var input = Samples.Copy("sample.jpg", out var dir);
        var rendered = Path.Combine(dir, "rendered.png");
        using (var img = new MagickImage(MagickColors.Black, 640, 400)) img.Write(rendered);
        var r = await Runner.RunAsync("image.redact", new OpContext { Inputs = [input], Options = new OpOptions().Set("rendered", rendered) });
        Assert.EndsWith("sample-redacted.jpg", r.Outputs[0]);
        Check.Valid(r.Outputs[0], "jpg");
    }

    [Fact]
    public async Task HeicPhotosWork()
    {
        if (!WicImaging.CanEncodeHeic) return; // no HEVC codec on this machine: the ring shows HEIC as unavailable
        Assert.Null(Converter.Unavailable("a.png", "heic"));
        var heic = await Run("convert:heic", "sample.jpg");
        Check.Valid(heic, "heic");
        // ...and back: iPhone photo to JPG, plus a tool on a HEIC input keeps HEIC.
        var input = Samples.Copy("sample.jpg", out var dir);
        var copy = Path.Combine(dir, "phone.heic");
        File.Copy(heic, copy);
        var jpg = await Runner.RunAsync("convert:jpg", new OpContext { Inputs = [copy] });
        Check.Valid(jpg.Outputs[0], "jpg");
        var resized = await Runner.RunAsync("image.resize", new OpContext { Inputs = [copy], Options = new OpOptions().Set("mode", "percent").Set("value", 50) });
        Check.Valid(resized.Outputs[0], "heic");
    }

    // ---------- Video ----------

    [Fact]
    public async Task VideoTools()
    {
        if (!Engines.Engines.HasFfmpeg) return;

        var trimmed = await MediaOps.ProbeAsync(await Run("video.trim", "sample.mp4", new OpOptions().Set("start", 0.5).Set("end", 2)));
        Assert.InRange(trimmed.Duration, 1.3, 1.7);

        var muted = await MediaOps.ProbeAsync(await Run("video.mute", "sample.mp4"));
        Assert.True(muted.HasVideo);
        Assert.False(muted.HasAudio);

        var small = await MediaOps.ProbeAsync(await Run("video.resize", "sample.mp4", new OpOptions().Set("height", 120)));
        Assert.Equal((160, 120), (small.Width, small.Height));

        var rotated = await MediaOps.ProbeAsync(await Run("video.rotate", "sample.mp4"));
        Assert.Equal((240, 320), (rotated.Width, rotated.Height));

        Check.Valid(await Run("video.frame", "sample.mp4"), "png");

        var fast = await MediaOps.ProbeAsync(await Run("video.speed", "sample.mp4", new OpOptions().Set("factor", 2)));
        Assert.InRange(fast.Duration, 1.3, 1.7);
        Assert.True(fast.HasAudio);

        Check.Valid(await Run("video.compress", "sample.mp4"), "mp4");
        Check.Valid(await Run("video.compress", "sample.webm"), "webm");
        Check.Valid(await Run("video.metadata", "sample.mp4"), "mp4");
        Check.Valid(await Run("video.trim", "sample.avi", new OpOptions().Set("start", 1).Set("end", 2)), "avi");
    }

    [Fact]
    public async Task VideoMetadataRemovesTitle()
    {
        if (!Engines.Engines.HasFfmpeg) return;
        var output = await Run("video.metadata", "sample.mp4");
        var r = Engines.ProcessRunner.Run(Engines.Engines.Ffprobe!, ["-v", "error", "-show_entries", "format_tags=title", "-of", "default=nw=1", output], TimeSpan.FromSeconds(20));
        Assert.DoesNotContain("Poppy sample", r.StdOut);
    }

    // ---------- Audio ----------

    [Theory]
    [InlineData("sample.mp3")]
    [InlineData("sample.wav")]
    [InlineData("sample.flac")]
    [InlineData("sample.m4a")]
    [InlineData("sample.ogg")]
    public async Task AudioTools(string sample)
    {
        if (!Engines.Engines.HasFfmpeg) return;
        var compressed = await Run("audio.compress", sample);
        Assert.True((await MediaOps.ProbeAsync(compressed)).HasAudio);

        var trimmed = await MediaOps.ProbeAsync(await Run("audio.trim", sample, new OpOptions().Set("start", 1).Set("end", 2.5)));
        Assert.InRange(trimmed.Duration, 1.3, 1.7);

        foreach (var id in new[] { "audio.normalize", "audio.mono", "audio.fade", "audio.metadata" })
            Assert.True((await MediaOps.ProbeAsync(await Run(id, sample))).HasAudio, id);

        var slow = await MediaOps.ProbeAsync(await Run("audio.speed", sample, new OpOptions().Set("factor", 0.5)));
        Assert.InRange(slow.Duration, 7.5, 8.5);
    }

    [Fact]
    public async Task MonoHasOneChannel()
    {
        if (!Engines.Engines.HasFfmpeg) return;
        var output = await Run("audio.mono", "sample.wav");
        var r = Engines.ProcessRunner.Run(Engines.Engines.Ffprobe!, ["-v", "error", "-show_entries", "stream=channels", "-of", "csv=p=0", output], TimeSpan.FromSeconds(20));
        Assert.Equal("1", r.StdOut.Trim());
    }

    // ---------- PDF ----------

    [Fact]
    public async Task PdfMergeSplitRotateExtract()
    {
        var a = Samples.Copy("sample.pdf", out var dir);
        var b = Path.Combine(dir, "second.pdf");
        File.Copy(Samples.Get("second.pdf"), b);

        var merged = await Runner.RunAsync("pdf.merge", new OpContext { Inputs = [a, b] });
        Assert.Equal(4, Check.PdfPages(merged.Outputs[0]));

        var split = await Run("pdf.split", "sample.pdf");
        Assert.Equal(3, Directory.GetFiles(split, "*.pdf").Length);

        var rotated = await Run("pdf.rotate", "sample.pdf");
        using (var doc = PdfReader.Open(rotated, PdfDocumentOpenMode.Import))
            Assert.All(doc.Pages.Cast<PdfSharp.Pdf.PdfPage>(), p => Assert.Equal(90, p.Rotate));

        var extracted = await Run("pdf.extract", "sample.pdf", new OpOptions().Set("pages", "2-3"));
        Assert.Equal(2, Check.PdfPages(extracted));
    }

    [Fact]
    public async Task PdfToTextReadsThePages()
    {
        if (!PdfOps.OcrAvailable) return;
        var txt = await Run("convert:txt", "sample.pdf");
        var text = File.ReadAllText(txt);
        Assert.Contains("test page 1", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("test page 3", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PdfMergeNeedsTwoFiles()
    {
        await Assert.ThrowsAsync<OpException>(() => Runner.RunAsync("pdf.merge", new OpContext { Inputs = [Samples.Copy("sample.pdf", out _)] }));
    }

    [Fact]
    public async Task PdfProtectRequiresPassword()
    {
        var output = await Run("pdf.protect", "sample.pdf", new OpOptions().Set("password", "çiçek123"));
        Assert.ThrowsAny<Exception>(() => PdfReader.Open(output, PdfDocumentOpenMode.Import).Dispose());
        using var doc = PdfReader.Open(output, "çiçek123", PdfDocumentOpenMode.Import);
        Assert.Equal(3, doc.PageCount);
    }

    [Fact]
    public async Task PdfMetadataIsStripped()
    {
        var output = await Run("pdf.metadata", "sample.pdf");
        using var doc = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        Assert.True(string.IsNullOrEmpty(doc.Info.Title));
        Assert.True(string.IsNullOrEmpty(doc.Info.Author));
    }

    [Fact]
    public async Task PdfCompressShrinksPhotos()
    {
        var input = Samples.Get("sample.pdf");
        var output = await Run("pdf.compress", "sample.pdf");
        Assert.True(new FileInfo(output).Length < new FileInfo(input).Length * 0.8,
            $"{new FileInfo(output).Length} vs {new FileInfo(input).Length}");
        Assert.Equal(3, Check.PdfPages(output));
        // Still renders.
        var pages = await PdfOps.RenderPagesAsync(output, Path.GetDirectoryName(output)!, "check", "png", null, default, 0.5);
        Assert.Equal(3, pages.Count);
    }

    // ---------- Subtitles, archives, zip ----------

    [Fact]
    public async Task SubtitleShift()
    {
        var srt = await Run("subtitle.shift", "sample.srt", new OpOptions().Set("ms", 1500));
        var cues = SubtitleOps.ParseSrtVtt(File.ReadAllText(srt));
        Assert.Equal(TimeSpan.FromSeconds(2.5), cues[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(4), cues[0].End);

        var earlier = SubtitleOps.ParseSrtVtt(File.ReadAllText(await Run("subtitle.shift", "sample.vtt", new OpOptions().Set("ms", -2000))));
        Assert.Equal(TimeSpan.Zero, earlier[0].Start); // clamped, never negative

        if (Engines.Engines.Ffmpeg != null)
        {
            var ass = File.ReadAllText(await Run("subtitle.shift", "sample.ass", new OpOptions().Set("ms", 1000)));
            Assert.Contains("0:00:02.00,0:00:03.50", ass);
        }
    }

    [Theory]
    [InlineData("sample.zip")]
    [InlineData("sample.tar.gz")]
    public async Task ArchiveExtract(string sample)
    {
        var folder = await Run("archive.extract", sample);
        Assert.True(File.Exists(Path.Combine(folder, "folder", "sub", "b.txt")), "nested file missing in " + folder);
    }

    [Fact]
    public async Task ArchiveExtractRefusesZipSlip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PoppyTests", "runs", Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        var evil = Path.Combine(dir, "evil.zip");
        using (var zip = ZipFile.Open(evil, ZipArchiveMode.Create))
        using (var w = new StreamWriter(zip.CreateEntry("../../escaped.txt").Open())) w.Write("x");
        await Assert.ThrowsAnyAsync<Exception>(() => Runner.RunAsync("archive.extract", new OpContext { Inputs = [evil] }));
        Assert.False(File.Exists(Path.Combine(dir, "..", "..", "escaped.txt")));
    }

    [Fact]
    public async Task ZipActionPacksFilesAndFolders()
    {
        var folder = Samples.Copy("folder", out var dir);
        var file = Path.Combine(dir, "extra.txt");
        File.WriteAllText(file, "extra");
        var r = await Runner.RunAsync("action.zip", new OpContext { Inputs = [folder, file] });
        using var zip = ZipFile.OpenRead(r.Outputs[0]);
        var names = zip.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("folder/sub/b.txt", names);
        Assert.Contains("extra.txt", names);
    }
}
