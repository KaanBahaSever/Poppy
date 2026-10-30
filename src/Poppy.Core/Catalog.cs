using Poppy.Core.Engines;
using Poppy.Core.Ops;

namespace Poppy.Core;

public enum RingMode { Convert, Tools, Actions }

/// <summary>What the app has to ask the user before an item can run.</summary>
public enum InputKind
{
    None,
    Resize,
    VideoResize,
    Crop,
    Adjust,
    Annotate,
    Redact,
    Background,
    Trim,
    Speed,
    PageRange,
    SplitPdf,
    Password,
    Offset,
    Folder,
}

public sealed record RingItem(string Id, string Label, string Glyph = "", InputKind Input = InputKind.None)
{
    public bool Enabled { get; init; } = true;
    public string? Reason { get; init; }

    /// <summary>When disabled for want of an engine: the EngineInstaller id that would enable it.</summary>
    public string? Need { get; init; }

    public bool IsConvert => Id.StartsWith("convert:");
    public bool IsAction => Id.StartsWith("action.");
    public string? TargetExt => IsConvert ? Id["convert:".Length..] : null;
}

/// <summary>Decides which items the ring shows for the files being dragged.</summary>
public static class Catalog
{
    public const int MaxSegments = 8;

    public static IReadOnlyList<RingItem> Items(RingMode mode, IReadOnlyList<string> paths) => mode switch
    {
        RingMode.Convert => ConvertItems(paths),
        RingMode.Tools => ToolItems(paths),
        _ => ActionItems(),
    };

    // ---------- Convert ----------

    static readonly string[] ImageTargets = ["png", "jpg", "webp", "avif", "heic", "tiff", "bmp", "pdf", "gif", "ico"];
    static readonly string[] VideoTargets = ["mp4", "mkv", "webm", "mov", "avi", "gif", "mp3", "wav"];
    static readonly string[] AudioTargets = ["mp3", "wav", "flac", "m4a", "ogg", "opus", "aiff", "wma"];
    static readonly string[] PdfTargets = ["png", "jpg", "webp", "tiff", "txt"];
    static readonly string[] DocTargets = ["pdf", "docx", "odt", "rtf", "html", "md", "txt", "epub"];
    static readonly string[] SlideTargets = ["pdf", "png", "jpg", "pptx", "odp", "mp4"];
    static readonly string[] SheetTargets = ["xlsx", "csv", "pdf", "json", "html", "tsv", "ods", "xls"];
    static readonly string[] EbookTargets = ["pdf", "docx", "html", "md", "txt", "odt"];
    static readonly string[] SubtitleTargets = ["srt", "vtt", "ass", "txt"];
    static readonly string[] ArchiveTargets = ["zip", "7z", "tar", "tar.gz"];

    public static IReadOnlyList<string> TargetsFor(string path)
    {
        var cat = FileKinds.Detect(path);
        var ext = FileKinds.Ext(path);
        string[] all = cat switch
        {
            FileCategory.Image => ImageTargets,
            FileCategory.Video => VideoTargets,
            FileCategory.Audio => AudioTargets,
            FileCategory.Pdf => PdfTargets,
            FileCategory.Document => DocTargets,
            FileCategory.Slides => SlideTargets,
            FileCategory.Spreadsheet => SheetTargets,
            FileCategory.Ebook => EbookTargets,
            FileCategory.Subtitle => SubtitleTargets,
            FileCategory.Archive or FileCategory.Folder or FileCategory.Other => ArchiveTargets,
            _ => [],
        };
        return all.Where(t => !FileKinds.SameFormat(ext, t)).ToList();
    }

    static IReadOnlyList<RingItem> ConvertItems(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return [];
        IEnumerable<string> targets = TargetsFor(paths[0]);
        foreach (var p in paths.Skip(1))
        {
            var set = TargetsFor(p);
            targets = targets.Where(set.Contains);
        }
        return targets.Take(MaxSegments).Select(t =>
        {
            var blocked = paths.FirstOrDefault(p => Converter.Unavailable(p, t) != null);
            var reason = blocked == null ? null : Converter.Unavailable(blocked, t);
            return new RingItem("convert:" + t, Label(t))
            {
                Enabled = reason == null,
                Reason = reason,
                Need = blocked == null ? null : Converter.MissingEngine(blocked, t),
            };
        }).ToList();
    }

    static string Label(string ext) => ext switch
    {
        "tar.gz" => "TAR.GZ",
        _ => ext.ToUpperInvariant(),
    };

    // ---------- Tools ----------

    static IReadOnlyList<RingItem> ToolItems(IReadOnlyList<string> paths)
    {
        var cat = FileKinds.Common(paths);
        var single = paths.Count == 1;
        var items = cat switch
        {
            FileCategory.Image => new List<RingItem>
            {
                Tool("image.compress", "tool.compress", "\uE73F"),
                Tool("image.resize", "tool.resize", "\uE740", InputKind.Resize),
                Tool("image.crop", "tool.crop", "\uE7A8", InputKind.Crop, single),
                Tool("image.edit", "tool.edit", "\uE9E9", InputKind.Adjust, single),
                Tool("image.annotate", "tool.annotate", "\uE70F", InputKind.Annotate, single),
                Tool("image.redact", "tool.redact", "\uED1A", InputKind.Redact, single),
                Tool("image.background", "tool.background", "\uE790", InputKind.Background),
                Tool("image.metadata", "tool.metadata", "\uE8EC"),
            },
            FileCategory.Video => new List<RingItem>
            {
                Tool("video.compress", "tool.compress", "\uE73F"),
                Tool("video.trim", "tool.trim", "\uE8C6", InputKind.Trim, single),
                Tool("video.resize", "tool.resize", "\uE740", InputKind.VideoResize),
                Tool("video.rotate", "tool.rotate", "\uE7AD"),
                Tool("video.speed", "tool.speed", "\uEC4A", InputKind.Speed),
                Tool("video.mute", "tool.mute", "\uE74F"),
                Tool("video.frame", "tool.frame", "\uE722"),
                Tool("video.metadata", "tool.metadata", "\uE8EC"),
            },
            FileCategory.Audio => new List<RingItem>
            {
                Tool("audio.compress", "tool.compress", "\uE73F"),
                Tool("audio.trim", "tool.trim", "\uE8C6", InputKind.Trim, single),
                Tool("audio.normalize", "tool.normalize", "\uE767"),
                Tool("audio.fade", "tool.fade", "\uE706"),
                Tool("audio.speed", "tool.speed", "\uEC4A", InputKind.Speed),
                Tool("audio.mono", "tool.mono", "\uE7F6"),
                Tool("audio.metadata", "tool.metadata", "\uE8EC"),
            },
            FileCategory.Pdf => new List<RingItem>
            {
                Tool("pdf.compress", "tool.compress", "\uE73F"),
                Tool("pdf.merge", "tool.merge", "\uE710") with
                {
                    Enabled = paths.Count >= 2,
                    Reason = paths.Count >= 2 ? null : L.T("need.multiple"),
                },
                Tool("pdf.split", "tool.split", "\uE8A9", InputKind.SplitPdf, single),
                Tool("pdf.rotate", "tool.rotate", "\uE7AD"),
                Tool("pdf.extract", "tool.extract", "\uE8E5", InputKind.PageRange, single),
                Tool("pdf.protect", "tool.protect", "\uE72E", InputKind.Password),
                Tool("pdf.metadata", "tool.metadata", "\uE8EC"),
            },
            FileCategory.Subtitle => new List<RingItem>
            {
                Tool("subtitle.shift", "tool.shift", "\uE916", InputKind.Offset),
            },
            FileCategory.Archive => new List<RingItem>
            {
                Tool("archive.extract", "tool.unpack", "\uE8B7"),
            },
            _ => new List<RingItem>(),
        };

        if (cat is FileCategory.Video or FileCategory.Audio && !Engines.Engines.HasFfmpeg)
            items = items.Select(i => i with { Enabled = false, Reason = L.T("need.ffmpeg"), Need = "ffmpeg" }).ToList();
        if (cat == FileCategory.Archive && paths.Any(p => !ArchiveOps.CanRead(p)))
            items = items.Select(i => i with { Enabled = false, Reason = L.T("need.7zip") }).ToList();
        return items;
    }

    static RingItem Tool(string id, string labelKey, string glyph, InputKind input = InputKind.None, bool enabled = true) =>
        new(id, L.T(labelKey), glyph, input)
        {
            Enabled = enabled,
            Reason = enabled ? null : L.T("single"),
        };

    // ---------- Actions ----------

    static IReadOnlyList<RingItem> ActionItems() =>
    [
        new("action.share", L.T("action.share"), "\uE72D"),
        new("action.copy", L.T("action.copy"), "\uE8C8"),
        new("action.copypath", L.T("action.copypath"), "\uE71B"),
        new("action.move", L.T("action.move"), "\uE8DE", InputKind.Folder),
        new("action.zip", L.T("action.zip"), "\uE7B8"),
        new("action.terminal", L.T("action.terminal"), "\uE756"),
        new("action.shelf", L.T("action.shelf"), "\uE718"),
    ];
}
