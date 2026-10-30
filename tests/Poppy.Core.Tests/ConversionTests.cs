using Poppy.Core.Ops;
using Xunit.Abstractions;

namespace Poppy.Core.Tests;

/// <summary>Every target the Convert ring offers, for one sample of every input type.</summary>
public class ConversionTests(ITestOutputHelper log)
{
    static readonly string[] SampleNames =
    [
        "sample.png", "sample.jpg", "sample.webp", "sample.bmp", "sample.tiff", "animated.gif", "sample.svg", "sample.heic", "sample.avif",
        "sample.mp4", "sample.webm", "sample.mkv", "sample.avi",
        "sample.wav", "sample.mp3", "sample.flac", "sample.m4a", "sample.ogg",
        "sample.pdf",
        "sample.docx", "sample.odt", "sample.rtf", "sample.md", "sample.txt", "sample.html",
        "sample.pptx",
        "sample.xlsx", "sample.csv", "sample.tsv",
        "sample.epub",
        "sample.srt", "sample.vtt", "sample.ass",
        "sample.zip", "sample.tar.gz", "folder", "notes.xyz",
    ];

    public static IEnumerable<object[]> Pairs() =>
        SampleNames.SelectMany(s => Catalog.TargetsFor(s == "folder" ? "folder\\" : s).Select(t => new object[] { s, t }));

    [Theory]
    [MemberData(nameof(Pairs))]
    public async Task Converts(string sample, string target)
    {
        if (!File.Exists(Path.Combine(Samples.Dir, sample)) && !Directory.Exists(Path.Combine(Samples.Dir, sample)))
        {
            try { Samples.Get(sample); }
            catch (FileNotFoundException)
            {
                log.WriteLine($"SKIP {sample}: this machine cannot build the sample");
                return;
            }
        }
        var input = Samples.Copy(sample, out _);
        var why = Converter.Unavailable(input, target);
        if (why != null)
        {
            log.WriteLine($"SKIP {sample} -> {target}: {why}");
            return;
        }

        var ctx = new OpContext { Inputs = [input] };
        var result = await Runner.RunAsync("convert:" + target, ctx);
        Assert.Single(result.Outputs);
        Check.Valid(result.Outputs[0], target);
        log.WriteLine($"OK {sample} -> {Path.GetFileName(result.Outputs[0])}");
    }

    [Fact]
    public async Task ConvertsSeveralFilesAtOnce()
    {
        var a = Samples.Copy("sample.png", out var dir);
        var b = Path.Combine(dir, "other.jpg");
        File.Copy(Samples.Get("sample.jpg"), b);
        var result = await Runner.RunAsync("convert:webp", new OpContext { Inputs = [a, b] });
        Assert.Equal(2, result.Outputs.Count);
        foreach (var o in result.Outputs) Check.Valid(o, "webp");
    }

    [Fact]
    public async Task SeveralImagesBecomeOnePdfInNameOrder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PoppyTests", "runs", Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        // Landscape, portrait, landscape, in a drop order that is not the name order.
        void Make(string name, uint w, uint h, ImageMagick.MagickFormat f)
        {
            using var img = new ImageMagick.MagickImage(ImageMagick.MagickColors.Coral, w, h);
            img.Write(Path.Combine(dir, name), f);
        }
        Make("photo10.webp", 400, 200, ImageMagick.MagickFormat.WebP);
        Make("photo1.jpg", 400, 200, ImageMagick.MagickFormat.Jpeg);
        Make("photo2.png", 200, 400, ImageMagick.MagickFormat.Png);
        string[] dropped = ["photo10.webp", "photo1.jpg", "photo2.png"];

        var result = await Runner.RunAsync("convert:pdf", new OpContext { Inputs = dropped.Select(n => Path.Combine(dir, n)).ToList() });

        var pdf = Assert.Single(result.Outputs);
        Assert.Equal(Path.Combine(dir, "photo1-merged.pdf"), pdf);
        using var doc = PdfSharp.Pdf.IO.PdfReader.Open(pdf, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        Assert.Equal(3, doc.PageCount);
        var landscape = doc.Pages.Cast<PdfSharp.Pdf.PdfPage>().Select(p => p.Width.Point > p.Height.Point).ToArray();
        Assert.Equal([true, false, true], landscape); // photo1, photo2, photo10
    }

    [Fact]
    public async Task OneImageStillBecomesItsOwnPdf()
    {
        var input = Samples.Copy("sample.png", out var dir);
        var result = await Runner.RunAsync("convert:pdf", new OpContext { Inputs = [input] });
        Assert.Equal(Path.Combine(dir, "sample.pdf"), Assert.Single(result.Outputs));
    }

    [Fact]
    public void NaturalOrder()
    {
        string[] names = ["img10.jpg", "img2.jpg", "IMG1.jpg", "img02b.jpg", "a.jpg"];
        Assert.Equal(["a.jpg", "IMG1.jpg", "img2.jpg", "img02b.jpg", "img10.jpg"], names.Order(NaturalComparer.Instance));
    }

    [Fact]
    public async Task NeverOverwritesExistingFiles()
    {
        var input = Samples.Copy("sample.png", out var dir);
        var existing = Path.Combine(dir, "sample.jpg");
        File.WriteAllText(existing, "keep me");
        var result = await Runner.RunAsync("convert:jpg", new OpContext { Inputs = [input] });
        Assert.Equal("keep me", File.ReadAllText(existing));
        Assert.Equal(Path.Combine(dir, "sample (1).jpg"), result.Outputs[0]);
    }

    [Fact]
    public async Task AnimatedGifStaysAnimatedAsWebp()
    {
        var input = Samples.Copy("animated.gif", out _);
        var result = await Runner.RunAsync("convert:webp", new OpContext { Inputs = [input] });
        using var frames = new ImageMagick.MagickImageCollection(result.Outputs[0]);
        Assert.Equal(3, frames.Count);
    }

    [Fact]
    public async Task ReportsProgress()
    {
        if (!Engines.Engines.HasFfmpeg) return;
        var input = Samples.Copy("sample.mp4", out _);
        var seen = new List<double>();
        var progress = new SyncProgress(seen.Add);
        await Runner.RunAsync("convert:webm", new OpContext { Inputs = [input], Progress = progress });
        Assert.NotEmpty(seen);
        Assert.Equal(1, seen[^1], 3);
        Assert.All(seen, v => Assert.InRange(v, 0, 1));
    }

    [Fact]
    public async Task CancelStopsFfmpeg()
    {
        if (!Engines.Engines.HasFfmpeg) return;
        var input = Samples.Copy("sample.mp4", out var dir);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Runner.RunAsync("convert:webm", new OpContext { Inputs = [input], Ct = cts.Token }));
    }
}

/// <summary>IProgress that reports on the calling thread (Progress&lt;T&gt; posts asynchronously).</summary>
public sealed class SyncProgress(Action<double> report) : IProgress<double>
{
    public void Report(double value)
    {
        lock (this) report(value);
    }
}
