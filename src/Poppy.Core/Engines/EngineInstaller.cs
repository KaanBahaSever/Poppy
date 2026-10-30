using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Poppy.Core.Engines;

/// <summary>An optional program Poppy can install for the user.</summary>
/// <param name="WingetId">winget package (verified with <c>winget show</c>), or null.</param>
/// <param name="StoreId">Microsoft Store product, or null.</param>
/// <param name="Page">Download page when it cannot be installed automatically.</param>
public sealed record EngineInfo(string Id, string Name, string PurposeKey, string Size, string? WingetId, string? StoreId, string Page, Func<bool> IsInstalled)
{
    public string Purpose => L.T(PurposeKey);
    public bool CanAutoInstall => WingetId != null || Id is "ocr" or "ghostscript";
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
        new("ghostscript", "Ghostscript", "eng.gs", "≈ 65 MB", null, null, "https://ghostscript.com/releases/gsdnld.html", () => Engines.Ghostscript != null),
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
            if (e.Id == "ghostscript") return await InstallGhostscript(e, status, ct);
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

    // ---------- Ghostscript (not on winget): the signed installer from Artifex's own GitHub ----------

    public const string GhostscriptRepo = "ArtifexSoftware/ghostpdl-downloads";

    static readonly HttpClient Http = CreateHttp();

    static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Poppy");
        return h;
    }

    /// <summary>Name and URL of the newest 64-bit Windows installer Artifex has published.</summary>
    public static async Task<(string Name, string Url)?> LatestGhostscriptAsync(CancellationToken ct)
    {
        var json = await Http.GetStringAsync($"https://api.github.com/repos/{GhostscriptRepo}/releases/latest", ct);
        using var doc = JsonDocument.Parse(json);
        foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            if (Regex.IsMatch(name, @"^gs\d+w64\.exe$"))
                return (name, a.GetProperty("browser_download_url").GetString()!);
        }
        return null;
    }

    static async Task<InstallResult> InstallGhostscript(EngineInfo e, Action<string>? status, CancellationToken ct)
    {
        status?.Invoke(L.T("inst.prepare"));
        (string Name, string Url)? asset;
        try { asset = await LatestGhostscriptAsync(ct); }
        catch (HttpRequestException) { asset = null; }
        if (asset == null)
        {
            OpenPage(e.Page);
            return new(false, L.T("inst.page"));
        }

        var dir = Path.Combine(Path.GetTempPath(), "Poppy", "installers");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, asset.Value.Name);
        try
        {
            using (var response = await Http.GetAsync(asset.Value.Url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? 0;
                await using var src = await response.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(file);
                var buffer = new byte[81920];
                long done = 0;
                int read, lastPct = -1;
                while ((read = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    var pct = total > 0 ? (int)(done * 100 / total) : 0;
                    if (pct / 5 != lastPct / 5) { lastPct = pct; status?.Invoke(L.F("inst.downloadpct", pct)); }
                }
            }

            // Never run something that Artifex did not sign.
            if (!Authenticode.IsSignedBy(file, "Artifex")) return new(false, L.T("inst.badsig"));

            status?.Invoke(L.T("inst.admin"));
            try
            {
                using var p = Process.Start(new ProcessStartInfo(file, "/S") { UseShellExecute = true, Verb = "runas" });
                if (p != null) await p.WaitForExitAsync(ct);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return new(false, L.T("inst.declined"));
            }
        }
        catch (HttpRequestException ex)
        {
            return new(false, L.F("inst.failed", e.Name) + " · " + ex.Message);
        }
        finally
        {
            try { File.Delete(file); } catch { }
        }

        Engines.Refresh();
        return e.IsInstalled() ? new(true, L.F("inst.done", e.Name)) : new(false, L.F("inst.failed", e.Name));
    }

    static void OpenPage(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }
}
