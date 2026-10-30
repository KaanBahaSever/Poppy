namespace Poppy.Core;

public enum FileCategory
{
    Image,
    Video,
    Audio,
    Pdf,
    Document,
    Slides,
    Spreadsheet,
    Ebook,
    Subtitle,
    Archive,
    Folder,
    Other,
}

public static class FileKinds
{
    static readonly Dictionary<string, FileCategory> Map = new(StringComparer.OrdinalIgnoreCase);

    static FileKinds()
    {
        Add(FileCategory.Image, "png", "jpg", "jpeg", "jfif", "webp", "avif", "heic", "heif", "tif", "tiff", "bmp", "gif", "ico", "svg", "tga", "psd", "jxl", "dng", "cr2", "nef", "arw");
        Add(FileCategory.Video, "mp4", "m4v", "mkv", "webm", "mov", "avi", "wmv", "flv", "mpg", "mpeg", "ts", "mts", "3gp", "ogv");
        Add(FileCategory.Audio, "mp3", "wav", "flac", "m4a", "aac", "ogg", "oga", "opus", "wma", "aiff", "aif", "amr");
        Add(FileCategory.Pdf, "pdf");
        Add(FileCategory.Document, "docx", "doc", "odt", "rtf", "txt", "md", "markdown", "html", "htm");
        Add(FileCategory.Slides, "pptx", "ppt", "odp");
        Add(FileCategory.Spreadsheet, "xlsx", "xls", "ods", "csv", "tsv");
        Add(FileCategory.Ebook, "epub");
        Add(FileCategory.Subtitle, "srt", "vtt", "ass", "ssa");
        Add(FileCategory.Archive, "zip", "7z", "rar", "tar", "gz", "tgz", "bz2", "xz");
    }

    static void Add(FileCategory c, params string[] exts)
    {
        foreach (var e in exts) Map[e] = c;
    }

    /// <summary>Lower-case extension without the dot ("photo.JPG" -> "jpg"). ".tar.gz" -> "tar.gz".</summary>
    public static string Ext(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        if (name.EndsWith(".tar.gz")) return "tar.gz";
        var e = Path.GetExtension(name);
        return e.Length > 1 ? e[1..] : "";
    }

    public static FileCategory Detect(string path)
    {
        if (Directory.Exists(path)) return FileCategory.Folder;
        var ext = Ext(path);
        if (ext == "tar.gz") return FileCategory.Archive;
        return Map.TryGetValue(ext, out var c) ? c : FileCategory.Other;
    }

    /// <summary>The single category shared by every path, or null for a mixed selection.</summary>
    public static FileCategory? Common(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return null;
        var first = Detect(paths[0]);
        for (int i = 1; i < paths.Count; i++)
            if (Detect(paths[i]) != first) return null;
        return first;
    }

    /// <summary>Extensions that mean the same format, so "JPG" is not offered for a .jpeg.</summary>
    public static bool SameFormat(string ext, string target)
    {
        static string Norm(string e) => e switch
        {
            "jpeg" or "jfif" => "jpg",
            "tif" => "tiff",
            "heif" => "heic",
            "htm" => "html",
            "markdown" => "md",
            "aif" => "aiff",
            "oga" => "ogg",
            "m4v" => "mp4",
            "tgz" => "tar.gz",
            "ssa" => "ass",
            _ => e,
        };
        return Norm(ext) == Norm(target);
    }
}
