using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Poppy.Core.Engines;
using Poppy.Core.Ops;

namespace Poppy.Core.Tests;

public class PdfSplitMergeTests
{
    [Fact]
    public void SplitPlans()
    {
        Assert.Equal([[1], [2], [3]], PdfOps.SplitPlan("each", 0, "", 3));
        Assert.Equal([[1, 2], [3, 4], [5]], PdfOps.SplitPlan("every", 2, "", 5));
        Assert.Equal([[1, 2, 3], [4, 5], [6, 7]], PdfOps.SplitPlan("ranges", 0, "1-3, 4-5, 6-", 7));
        Assert.Equal([[2]], PdfOps.SplitPlan("ranges", 0, "2, 40-50", 7)); // out-of-range parts are dropped
        Assert.Equal([[1], [2]], PdfOps.SplitPlan("every", 0, "", 2));     // n < 1 means 1
    }

    [Fact]
    public async Task SplitEveryTwoPages()
    {
        var input = Samples.Copy("sample.pdf", out _);
        var r = await Runner.RunAsync("pdf.split", new OpContext { Inputs = [input], Options = new OpOptions().Set("mode", "every").Set("n", 2) });
        var files = Directory.GetFiles(r.Outputs[0], "*.pdf").Order(NaturalComparer.Instance).ToList();
        Assert.Equal(["sample-1-2.pdf", "sample-3.pdf"], files.Select(Path.GetFileName));
        Assert.Equal([2, 1], files.Select(Check.PdfPages));
    }

    [Fact]
    public async Task SplitIntoRanges()
    {
        var input = Samples.Copy("sample.pdf", out _);
        var r = await Runner.RunAsync("pdf.split", new OpContext { Inputs = [input], Options = new OpOptions().Set("mode", "ranges").Set("ranges", "1, 2-3") });
        var files = Directory.GetFiles(r.Outputs[0], "*.pdf");
        Assert.Equal(2, files.Length);
        Assert.Equal(3, files.Sum(Check.PdfPages));
    }

    [Fact]
    public async Task MergeUsesNaturalNameOrder()
    {
        Samples.EnsureFonts();
        var dir = Path.Combine(Path.GetTempPath(), "PoppyTests", "runs", Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        string Make(string name, bool landscape)
        {
            using var doc = new PdfDocument();
            var page = doc.AddPage();
            page.Orientation = landscape ? PdfSharp.PageOrientation.Landscape : PdfSharp.PageOrientation.Portrait;
            var path = Path.Combine(dir, name);
            doc.Save(path);
            return path;
        }
        var p10 = Make("part10.pdf", landscape: true);
        var p2 = Make("part2.pdf", landscape: false);
        var r = await Runner.RunAsync("pdf.merge", new OpContext { Inputs = [p10, p2] }); // dropped in the "wrong" order
        Assert.EndsWith("part2-merged.pdf", r.Outputs[0]);
        using var merged = PdfReader.Open(r.Outputs[0], PdfDocumentOpenMode.Import);
        Assert.True(merged.Pages[0].Width.Point < merged.Pages[0].Height.Point, "part2 (portrait) should come first");
        Assert.True(merged.Pages[1].Width.Point > merged.Pages[1].Height.Point);
    }
}

public class EngineInstallTests
{
    static T WithDisabled<T>(Func<string, bool> disabled, Func<T> body)
    {
        var old = Engines.Engines.Disabled;
        Engines.Engines.Disabled = disabled;
        try { return body(); }
        finally { Engines.Engines.Disabled = old; }
    }

    [Fact]
    public void EveryNeedMapsToAnInstallableEngine()
    {
        Assert.Equal(EngineInstaller.All.Count, EngineInstaller.All.Select(e => e.Id).Distinct().Count());
        var nothing = new Func<string, bool>(_ => true); // pretend no engine at all is installed
        var needs = WithDisabled(nothing, () =>
            new[] { "a.mp4", "a.mp3", "a.srt", "a.docx", "a.md", "a.epub", "a.pptx", "a.xlsx", "a.png", "a.zip" }
                .SelectMany(f => Enum.GetValues<RingMode>().SelectMany(m => Catalog.Items(m, [f])))
                .Where(i => !i.Enabled && i.Need != null).Select(i => i.Need!).Distinct().ToList());
        Assert.NotEmpty(needs);
        foreach (var n in needs) Assert.NotNull(EngineInstaller.Get(n));
    }

    [Fact]
    public void MissingEngineIsTheRightOne()
    {
        WithDisabled(n => n is "ffmpeg", () =>
        {
            Assert.Equal("ffmpeg", Converter.MissingEngine("a.mp4", "mp3"));
            var trim = Catalog.Items(RingMode.Tools, ["a.mp4"]).Single(i => i.Id == "video.trim");
            Assert.False(trim.Enabled);
            Assert.Equal("ffmpeg", trim.Need);
            Assert.Null(Converter.MissingEngine("a.png", "webp")); // images never need FFmpeg
            return 0;
        });
        WithDisabled(n => n is "pandoc" or "libreoffice" or "word" or "excel" or "powerpoint" or "edge", () =>
        {
            Assert.Equal("pandoc", Converter.MissingEngine("notes.md", "docx"));
            Assert.Equal("libreoffice", Converter.MissingEngine("deck.pptx", "pdf"));
            Assert.Equal("libreoffice", Converter.MissingEngine("a.xls", "pdf"));
            Assert.Null(Converter.MissingEngine("a.csv", "xlsx")); // built in, nothing to install
            return 0;
        });
        Assert.Null(Converter.MissingEngine("a.zip", "tar"));
    }

    [Fact]
    public void WingetCommandNeverUpgradesAndIsSilent()
    {
        var args = EngineInstaller.WingetArgs("7zip.7zip");
        Assert.Equal("install", args[0]);
        Assert.Contains("--no-upgrade", args);
        Assert.Contains("--silent", args);
        Assert.Contains("--exact", args);
        Assert.Contains("--disable-interactivity", args);
        Assert.DoesNotContain("upgrade", args);
    }

    /// <summary>Every winget id Poppy offers exists (the Ghostscript id once did not). Uses the network.</summary>
    [Fact]
    public async Task WingetIdsExist()
    {
        var winget = EngineInstaller.Winget;
        if (winget == null || Environment.GetEnvironmentVariable("CI") != null) return; // runners have no winget sources
        foreach (var e in EngineInstaller.All.Where(e => e.WingetId != null))
        {
            var r = await ProcessRunner.RunAsync(winget,
                ["show", "--id", e.WingetId!, "--exact", "--source", "winget", "--accept-source-agreements", "--disable-interactivity"],
                null, default, TimeSpan.FromMinutes(2));
            Assert.True(r.Ok && r.StdOut.Contains("Found", StringComparison.OrdinalIgnoreCase), $"winget has no package {e.WingetId}: {r.StdOut}");
        }
    }

    [Fact]
    public async Task InstallingSomethingAlreadyThereDoesNothing()
    {
        // Pandoc is used by the samples, so it is installed wherever this suite runs fully.
        var pandoc = EngineInstaller.Get("pandoc")!;
        if (!pandoc.IsInstalled()) return;
        var r = await EngineInstaller.InstallAsync(pandoc, null, default);
        Assert.True(r.Ok);
    }

    [Fact]
    public void RefreshFindsEnginesAgain()
    {
        var before = Engines.Engines.Pandoc;
        Engines.Engines.Refresh();
        Assert.Equal(before, Engines.Engines.Pandoc);
    }
}
