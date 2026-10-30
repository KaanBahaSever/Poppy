using System.Diagnostics;
using System.Globalization;

namespace Poppy.Core.Engines;

/// <summary>An optional program Poppy can install for the user.</summary>
/// <param name="WingetId">winget package (verified with <c>winget show</c>), or null.</param>
/// <param name="StoreId">Microsoft Store product, or null.</param>
/// <param name="Page">Download page when it cannot be installed automatically.</param>
public sealed record EngineInfo(string Id, string Name, string PurposeKey, string Size, string? WingetId, string? StoreId, string Page, Func<bool> IsInstalled)
{
    public string Purpose => L.T(PurposeKey);
    public bool CanAutoInstall => WingetId != null || Id == "ocr";
}

public sealed record InstallResult(bool Ok, string Message);

/// <summary>
/// Installs engines so people never have to open a terminal: winget for regular programs, the
/// Store for codec extensions, an elevated Windows capability for OCR languages.
/// </summary>
public static class EngineInstaller
{
    public static readonly IReadOnlyList<EngineInfo> All =
    [
        new("ffmpeg", "FFmpeg", "eng.ffmpeg", "≈ 90 MB", "Gyan.FFmpeg.Essentials", null, "https://ffmpeg.org/download.html", () => Engines.HasFfmpeg),
        new("libreoffice", "LibreOffice", "eng.libre", "≈ 350 MB", "TheDocumentFoundation.LibreOffice", null, "https://www.libreoffice.org/download/", () => Engines.LibreOffice != null),
        new("pandoc", "Pandoc", "eng.pandoc", "≈ 40 MB", "JohnMacFarlane.Pandoc", null, "https://pandoc.org/installing.html", () => Engines.Pandoc != null),
        new("7zip", "7-Zip", "eng.7zip", "≈ 2 MB", "7zip.7zip", null, "https://www.7-zip.org/", () => Engines.SevenZip != null),
        new("heic", "HEVC Video Extensions", "eng.hevc", "≈ 2 MB", null, "9NMZLZ57R3T7", "ms-windows-store://pdp/?ProductId=9NMZLZ57R3T7", () => Ops.WicImaging.CanEncodeHeic),
        new("ocr", "Windows OCR", "eng.ocr", "≈ 20 MB", null, null, "ms-settings:regionlanguage", () => Ops.PdfOps.OcrAvailable),
        new("ghostscript", "Ghostscript", "eng.gs", "≈ 60 MB", null, null, "https://ghostscript.com/releases/gsdnld.html", () => Engines.Ghostscript != null),
    ];

    public static EngineInfo? Get(string? id) => All.FirstOrDefault(e => e.Id == id);

    static readonly SemaphoreSlim OneAtATime = new(1, 1);

    public static string? Winget
    {
        get
        {
            var alias = Environment.ExpandEnvironmentVariables(@"%LocalAppData%\Microsoft\WindowsApps\winget.exe");
            if (File.Exists(alias)) return alias;
            foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var p = Path.Combine(d.Trim('"'), "winget.exe");
                    if (File.Exists(p)) return p;
                }
                catch (ArgumentException) { }
            }
            return null;
        }
    }

    /// <summary>The winget command line Poppy runs. Never upgrades something already installed.</summary>
    public static IReadOnlyList<string> WingetArgs(string id) =>
        ["install", "--id", id, "--exact", "--source", "winget", "--silent", "--no-upgrade",
         "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"];

    /// <summary>Installs the engine; <paramref name="status"/> gets short progress messages.</summary>
    public static async Task<InstallResult> InstallAsync(EngineInfo e, Action<string>? status, CancellationToken ct)
    {
        await OneAtATime.WaitAsync(ct);
        try
        {
            if (e.IsInstalled()) return new(true, L.F("inst.done", e.Name));
            if (e.WingetId != null) return await ViaWinget(e, status, ct);
            if (e.Id == "ocr") return await InstallOcr(e, status);
            OpenPage(e.Page);
            return new(false, e.StoreId != null ? L.T("inst.store") : L.T("inst.page"));
        }
        finally
        {
            OneAtATime.Release();
        }
    }

    static async Task<InstallResult> ViaWinget(EngineInfo e, Action<string>? status, CancellationToken ct)
    {
        var winget = Winget;
        if (winget == null)
        {
            OpenPage(e.Page);
            return new(false, L.T("inst.nowinget"));
        }
        status?.Invoke(L.T("inst.prepare"));
        var r = await ProcessRunner.RunAsync(winget, WingetArgs(e.WingetId!), line =>
        {
            if (line.Contains("Downloading", StringComparison.OrdinalIgnoreCase)) status?.Invoke(L.T("inst.download"));
            else if (line.Contains("Starting package install", StringComparison.OrdinalIgnoreCase)
                  || line.Contains("Successfully verified", StringComparison.OrdinalIgnoreCase)) status?.Invoke(L.T("inst.setup"));
        }, ct, TimeSpan.FromMinutes(20));
        // Trust what is on disk, not the exit code (winget has many "fine" codes).
        Engines.Refresh();
        if (e.IsInstalled()) return new(true, L.F("inst.done", e.Name));
        var tail = r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? r.Tail(1);
        return new(false, L.F("inst.failed", e.Name) + (tail.Length > 0 ? " · " + tail : ""));
    }

    /// <summary>OCR languages are a Windows capability; adding one asks for administrator permission once.</summary>
    static async Task<InstallResult> InstallOcr(EngineInfo e, Action<string>? status)
    {
        var lang = CultureInfo.CurrentUICulture.Name;
        if (string.IsNullOrEmpty(lang)) lang = "en-US";
        status?.Invoke(L.T("inst.admin"));
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = $"-NoProfile -NonInteractive -Command \"Get-WindowsCapability -Online -Name 'Language.OCR~~~{lang}~*' | Add-WindowsCapability -Online\"",
        };
        try
        {
            using var p = Process.Start(psi);
            if (p != null) await p.WaitForExitAsync();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new(false, L.T("inst.declined")); // the user said no to the UAC prompt
        }
        Engines.Refresh();
        if (e.IsInstalled()) return new(true, L.F("inst.done", e.Name));
        OpenPage(e.Page);
        return new(false, L.F("inst.failed", e.Name));
    }

    static void OpenPage(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }
}
