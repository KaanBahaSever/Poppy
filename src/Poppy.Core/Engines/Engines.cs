using Microsoft.Win32;

namespace Poppy.Core.Engines;

/// <summary>
/// Finds the external programs Poppy drives. Bundled copies in the app's
/// "engines" folder win, then PATH, then the usual install locations.
/// </summary>
public static class Engines
{
    // Where winget puts the command-line shims of portable packages (FFmpeg, ...).
    const string WingetLinks = @"%LocalAppData%\Microsoft\WinGet\Links";

    static Lazy<string?> ffmpeg = null!, ffprobe = null!, pandoc = null!, soffice = null!, sevenZip = null!, ghostscript = null!, edge = null!;
    static Lazy<bool> word = null!, powerPoint = null!, excel = null!;

    static Engines() => Init();

    static void Init()
    {
        ffmpeg = new(() => Find("ffmpeg.exe", @"engines\ffmpeg", WingetLinks, @"C:\ffmpeg\bin", @"%ProgramFiles%\ffmpeg\bin"));
        ffprobe = new(() => Find("ffprobe.exe", @"engines\ffmpeg", WingetLinks, @"C:\ffmpeg\bin", @"%ProgramFiles%\ffmpeg\bin"));
        pandoc = new(() => Find("pandoc.exe", @"engines\pandoc", @"%LocalAppData%\Pandoc", @"%ProgramFiles%\Pandoc", WingetLinks));
        soffice = new(() => Find("soffice.exe", @"%ProgramFiles%\LibreOffice\program", @"%ProgramFiles(x86)%\LibreOffice\program"));
        sevenZip = new(() => Find("7z.exe", @"engines\7zip", @"%ProgramFiles%\7-Zip", @"%ProgramFiles(x86)%\7-Zip", WingetLinks));
        ghostscript = new(FindGhostscript);
        edge = new(() => Find("msedge.exe", @"%ProgramFiles(x86)%\Microsoft\Edge\Application", @"%ProgramFiles%\Microsoft\Edge\Application"));
        word = new(() => HasProgId("Word.Application"));
        powerPoint = new(() => HasProgId("PowerPoint.Application"));
        excel = new(() => HasProgId("Excel.Application"));
    }

    /// <summary>Looks for engines again (after Poppy installed one), without restarting.</summary>
    public static void Refresh()
    {
        Init();
        lock (EncodersLock) encoders = null;
        Ops.WicImaging.Reset();
        Ops.PdfOps.ResetOcr();
    }

    /// <summary>Overrides for tests and for the "disable engine" switches.</summary>
    public static Func<string, bool>? Disabled { get; set; }

    public static string? Ffmpeg => Pick("ffmpeg", ffmpeg);
    public static string? Ffprobe => Pick("ffmpeg", ffprobe);
    public static string? Pandoc => Pick("pandoc", pandoc);
    public static string? LibreOffice => Pick("libreoffice", soffice);
    public static string? SevenZip => Pick("7zip", sevenZip);
    public static string? Ghostscript => Pick("ghostscript", ghostscript);
    public static string? Edge => Pick("edge", edge);
    public static bool Word => Disabled?.Invoke("word") != true && word.Value;
    public static bool PowerPoint => Disabled?.Invoke("powerpoint") != true && powerPoint.Value;
    public static bool Excel => Disabled?.Invoke("excel") != true && excel.Value;

    public static bool HasFfmpeg => Ffmpeg != null && Ffprobe != null;

    static string? Pick(string name, Lazy<string?> v) => Disabled?.Invoke(name) == true ? null : v.Value;

    static string? Find(string exe, params string[] dirs)
    {
        var baseDir = AppContext.BaseDirectory;
        foreach (var d in dirs)
        {
            var dir = Environment.ExpandEnvironmentVariables(d);
            if (!Path.IsPathRooted(dir)) dir = Path.Combine(baseDir, dir);
            var p = Path.Combine(dir, exe);
            if (File.Exists(p)) return p;
        }
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var d in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(d.Trim('"'), exe);
                if (File.Exists(p)) return p;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    static string? FindGhostscript()
    {
        var onPath = Find("gswin64c.exe");
        if (onPath != null) return onPath;
        var root = Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\gs");
        if (!Directory.Exists(root)) return null;
        return Directory.EnumerateFiles(root, "gswin64c.exe", SearchOption.AllDirectories)
            .OrderDescending().FirstOrDefault();
    }

    static bool HasProgId(string progId)
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(progId + @"\CLSID");
            return key?.GetValue(null) is string;
        }
        catch { return false; }
    }

    static HashSet<string>? encoders;
    static readonly object EncodersLock = new();

    /// <summary>True when the FFmpeg build has the named encoder (libx264, h264_mf, ...).</summary>
    public static bool HasEncoder(string name)
    {
        lock (EncodersLock) return LoadEncoders().Contains(name);
    }

    static HashSet<string> LoadEncoders()
    {
        if (encoders == null)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Ffmpeg != null)
            {
                var r = ProcessRunner.Run(Ffmpeg, ["-hide_banner", "-encoders"], TimeSpan.FromSeconds(20));
                foreach (var line in r.StdOut.Split('\n'))
                {
                    var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && parts[0].Length == 6) set.Add(parts[1]);
                }
            }
            encoders = set;
        }
        return encoders;
    }

    /// <summary>First encoder from the list that this FFmpeg build has.</summary>
    public static string PickEncoder(params string[] names) =>
        names.FirstOrDefault(HasEncoder) ?? names[^1];
}
