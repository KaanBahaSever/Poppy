using Poppy.Core.Ops;

namespace Poppy.Core.Tests;

public class RingLayoutTests
{
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(5)] [InlineData(7)] [InlineData(8)]
    public void EverySegmentCentreHitsItsOwnSegment(int count)
    {
        var ring = new RingLayout(count);
        for (int i = 0; i < count; i++)
        {
            var (x, y) = ring.SegmentCenter(i);
            Assert.Equal(i, ring.HitTest(x, y));
        }
    }

    [Fact]
    public void FirstSegmentIsAtTwelveOClockAndOrderIsClockwise()
    {
        var ring = new RingLayout(4);
        Assert.Equal(0, ring.HitTest(0, -90));   // up
        Assert.Equal(1, ring.HitTest(90, 0));    // right
        Assert.Equal(2, ring.HitTest(0, 90));    // down
        Assert.Equal(3, ring.HitTest(-90, 0));   // left
    }

    [Fact]
    public void CentreAndOutside()
    {
        var ring = new RingLayout(6);
        Assert.Equal(RingLayout.Center, ring.HitTest(0, 0));
        Assert.Equal(RingLayout.Center, ring.HitTest(20, 20));
        Assert.Equal(RingLayout.Outside, ring.HitTest(400, 0));
        Assert.Equal(RingLayout.Outside, new RingLayout(0).HitTest(90, 0));
    }
}

public class DragGestureTests
{
    const int Shift = 0x10, Ctrl = 0x11, Alt = 0x12, A = 0x41;

    static DragGesture Make(out List<string> events)
    {
        var log = new List<string>();
        events = log;
        var s = new AppSettings();
        var g = new DragGesture { KeyToMode = s.ModeForKey };
        g.Show += (m, x, y) => log.Add($"show {m} {x},{y}");
        g.Switch += m => log.Add($"switch {m}");
        g.Released += () => log.Add("released");
        g.Cancelled += () => log.Add("cancelled");
        return g;
    }

    [Fact]
    public void KeyWithoutDragDoesNothing()
    {
        var g = Make(out var ev);
        Assert.False(g.KeyDown(Shift));
        g.MouseDown(100, 100);
        Assert.False(g.KeyDown(Ctrl)); // button down but not moved yet: a click, not a drag
        Assert.Empty(ev);
    }

    [Fact]
    public void TapDuringDragOpensRingAndSwallowsKey()
    {
        var g = Make(out var ev);
        g.MouseDown(100, 100);
        g.MouseMove(103, 100);
        g.MouseMove(130, 110);
        Assert.True(g.KeyDown(0xA0)); // left shift
        Assert.True(g.KeyUp(0xA0));
        Assert.Equal(["show Convert 130,110"], ev);
        Assert.Equal(RingMode.Convert, g.Open);
    }

    [Fact]
    public void OtherKeySwitchesModeAndReleaseCloses()
    {
        var g = Make(out var ev);
        g.MouseDown(0, 0);
        g.MouseMove(50, 0);
        g.KeyDown(Shift); g.KeyUp(Shift);
        g.KeyDown(Ctrl); g.KeyUp(Ctrl);
        g.KeyDown(Alt); g.KeyUp(Alt);
        g.KeyDown(Alt); g.KeyUp(Alt); // same mode again: no event
        g.MouseUp();
        Assert.Equal(["show Convert 50,0", "switch Tools", "switch Actions", "released"], ev);
        Assert.Null(g.Open);
    }

    [Fact]
    public void AutoRepeatDoesNotRetrigger()
    {
        var g = Make(out var ev);
        g.MouseDown(0, 0);
        g.MouseMove(0, 40);
        Assert.True(g.KeyDown(Shift));
        Assert.True(g.KeyDown(Shift));
        Assert.True(g.KeyDown(Shift));
        Assert.True(g.KeyUp(Shift));
        Assert.Single(ev);
    }

    [Fact]
    public void NonTriggerKeysPassThrough()
    {
        var g = Make(out var ev);
        g.MouseDown(0, 0);
        g.MouseMove(40, 40);
        Assert.False(g.KeyDown(A));
        Assert.False(g.KeyUp(A));
        Assert.Empty(ev);
    }

    [Fact]
    public void EscapeCancelsButIsNotSwallowed()
    {
        var g = Make(out var ev);
        g.MouseDown(0, 0);
        g.MouseMove(40, 40);
        g.KeyDown(Shift); g.KeyUp(Shift);
        Assert.False(g.KeyDown(DragGesture.VK_ESCAPE));
        g.KeyUp(DragGesture.VK_ESCAPE);
        g.MouseUp();
        Assert.Equal(["show Convert 40,40", "cancelled"], ev);
    }

    [Fact]
    public void KeysPassThroughWhenNotSureItIsAFileDrag()
    {
        var g = Make(out var ev);
        g.SwallowKeys = () => false;
        g.MouseDown(0, 0);
        g.MouseMove(40, 40);
        Assert.False(g.KeyDown(Ctrl));
        Assert.False(g.KeyUp(Ctrl));
        Assert.Equal(["show Tools 40,40"], ev);
    }

    [Fact]
    public void NotAFileDragIsIgnored()
    {
        var g = Make(out var ev);
        g.IsDragOfFiles = () => false;
        g.MouseDown(0, 0);
        g.MouseMove(40, 40);
        Assert.False(g.KeyDown(Shift));
        Assert.Empty(ev);
    }
}

public class CatalogTests
{
    static List<string> Labels(RingMode m, params string[] files) => Catalog.Items(m, files).Select(i => i.Label).ToList();

    [Fact]
    public void ConvertNeverOffersTheSameFormat()
    {
        Assert.DoesNotContain("PNG", Labels(RingMode.Convert, "a.png"));
        Assert.DoesNotContain("JPG", Labels(RingMode.Convert, "a.JPEG"));
        Assert.DoesNotContain("MP4", Labels(RingMode.Convert, "a.mp4"));
        Assert.Contains("MP3", Labels(RingMode.Convert, "a.mp4"));
    }

    [Fact]
    public void RingsHaveAtMostEightSegments()
    {
        foreach (var f in new[] { "a.png", "a.svg", "a.mp4", "a.wav", "a.pdf", "a.docx", "a.md", "a.pptx", "a.xlsx", "a.epub", "a.srt", "a.zip", "a.xyz" })
            foreach (var m in Enum.GetValues<RingMode>())
                Assert.InRange(Catalog.Items(m, [f]).Count, 0, Catalog.MaxSegments);
    }

    [Fact]
    public void MixedSelectionsOfferCommonTargetsOnly()
    {
        Assert.Equal(["WAV"], Labels(RingMode.Convert, "a.mp4", "b.mp3"));
        Assert.Empty(Labels(RingMode.Tools, "a.png", "b.mp4"));
        Assert.Equal(7, Labels(RingMode.Actions, "a.png", "b.mp4", "c\\").Count);
    }

    [Fact]
    public void EditorsNeedASingleFileAndMergeNeedsTwo()
    {
        var two = Catalog.Items(RingMode.Tools, ["a.png", "b.png"]);
        Assert.False(two.Single(i => i.Id == "image.crop").Enabled);
        Assert.True(two.Single(i => i.Id == "image.compress").Enabled);

        Assert.False(Catalog.Items(RingMode.Tools, ["a.pdf"]).Single(i => i.Id == "pdf.merge").Enabled);
        Assert.True(Catalog.Items(RingMode.Tools, ["a.pdf", "b.pdf"]).Single(i => i.Id == "pdf.merge").Enabled);
    }

    [Fact]
    public void EveryItemHasALabelAndEveryToolIdIsHandled()
    {
        foreach (var f in new[] { "a.png", "a.mp4", "a.wav", "a.pdf", "a.srt", "a.zip" })
            foreach (var item in Catalog.Items(RingMode.Tools, [f, "b." + FileKinds.Ext(f)]))
            {
                Assert.False(string.IsNullOrWhiteSpace(item.Label));
                Assert.False(L.Has(item.Label), "label is an untranslated key: " + item.Label);
                Assert.False(string.IsNullOrEmpty(item.Glyph));
            }
    }

    [Fact]
    public void TurkishLabels()
    {
        var old = L.Language;
        try
        {
            L.Language = "tr";
            Assert.Contains("Sıkıştır", Labels(RingMode.Tools, "a.png"));
            Assert.Contains("Yolu Kopyala", Labels(RingMode.Actions, "a.png"));
        }
        finally { L.Language = old; }
    }
}

public class SmallLogicTests
{
    [Theory]
    [InlineData("1-3,5", 10, new[] { 1, 2, 3, 5 })]
    [InlineData("8-", 10, new[] { 8, 9, 10 })]
    [InlineData("-2", 10, new[] { 1, 2 })]
    [InlineData("3-1", 10, new[] { 1, 2, 3 })]
    [InlineData("2, 2, 40", 5, new[] { 2 })]
    [InlineData("abc", 5, new int[0])]
    public void PageRanges(string spec, int count, int[] expected) =>
        Assert.Equal(expected, PdfOps.ParseRange(spec, count));

    [Fact]
    public void CsvWithSemicolonsQuotesAndNewlines()
    {
        var text = "a;b\r\n\"x;y\";\"he said \"\"hi\"\"\"\r\n\"multi\nline\";2";
        Assert.Equal(';', SheetOps.GuessDelimiter(text));
        var rows = SheetOps.ParseDelimited(text, ';');
        Assert.Equal(3, rows.Count);
        Assert.Equal("x;y", rows[1][0]);
        Assert.Equal("he said \"hi\"", rows[1][1]);
        Assert.Equal("multi\nline", rows[2][0]);
        Assert.Equal(rows, SheetOps.ParseDelimited(SheetOps.ToDelimited(rows, ','), ','));
    }

    [Fact]
    public void JsonKeepsNumbersAsNumbers()
    {
        var json = SheetOps.ToJson([["n", "s"], ["1.5", "x"], ["", "007"]]);
        Assert.Contains("\"n\": 1.5", json);
        Assert.Contains("\"n\": null", json);
    }

    [Fact]
    public void SubtitlesRoundTrip()
    {
        var cues = SubtitleOps.ParseSrtVtt(Samples.Srt);
        Assert.Equal(2, cues.Count);
        Assert.Equal("Second line\nTwo rows", cues[1].Text);
        Assert.Equal(cues, SubtitleOps.ParseSrtVtt(SubtitleOps.ToVtt(cues)));
        Assert.Equal("Hello there\r\nSecond line Two rows\r\n", SubtitleOps.ToText(cues));
    }

    [Fact]
    public void FileKindsAndExtensions()
    {
        Assert.Equal("tar.gz", FileKinds.Ext(@"C:\x\Backup.TAR.GZ"));
        Assert.Equal(FileCategory.Archive, FileKinds.Detect("a.tar.gz"));
        Assert.Equal(FileCategory.Image, FileKinds.Detect("a.HEIC"));
        Assert.Equal(FileCategory.Other, FileKinds.Detect("a.unknown"));
        Assert.Equal(FileCategory.Folder, FileKinds.Detect(Path.GetTempPath()));
        Assert.Null(FileKinds.Common(["a.png", "b.mp4"]));
        Assert.Equal(FileCategory.Video, FileKinds.Common(["a.mp4", "b.MKV"]));
    }

    [Fact]
    public void OutputPathsAreUniqueAndCanBeRedirected()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PoppyTests", "paths-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        var input = Path.Combine(dir, "a.png");
        File.WriteAllText(input, "");
        Assert.Equal(Path.Combine(dir, "a.jpg"), OutputPaths.For(input, "jpg"));
        File.WriteAllText(Path.Combine(dir, "a-small.png"), "");
        Assert.Equal(Path.Combine(dir, "a-small (1).png"), OutputPaths.For(input, "png", "small"));

        var other = Path.Combine(dir, "out");
        Directory.CreateDirectory(other);
        OutputPaths.OverrideDir = other;
        try { Assert.Equal(Path.Combine(other, "a.webp"), OutputPaths.For(input, "webp")); }
        finally { OutputPaths.OverrideDir = null; }
    }

    [Fact]
    public void SettingsRoundTrip()
    {
        var old = AppSettings.Folder;
        AppSettings.Folder = Path.Combine(Path.GetTempPath(), "PoppyTests", "settings-" + Guid.NewGuid().ToString("N")[..6]);
        try
        {
            Assert.Equal(AppSettings.VK_SHIFT, AppSettings.Load().ConvertKey);
            var s = new AppSettings { ConvertKey = AppSettings.VK_SPACE, Language = "tr", RevealWhenDone = true };
            s.Save();
            var back = AppSettings.Load();
            Assert.Equal(AppSettings.VK_SPACE, back.ConvertKey);
            Assert.Equal("tr", back.Language);
            Assert.True(back.RevealWhenDone);
            Assert.Equal(RingMode.Convert, back.ModeForKey(AppSettings.VK_SPACE));
            Assert.Null(back.ModeForKey(AppSettings.VK_SHIFT));
        }
        finally { AppSettings.Folder = old; }
    }
}
