using System.Formats.Tar;
using System.IO.Compression;
using Poppy.Core.Engines;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace Poppy.Core.Ops;

/// <summary>ZIP / TAR / TAR.GZ / 7Z creation and extraction of anything SharpCompress reads.</summary>
public static class ArchiveOps
{
    public static bool CanRead(string path) => FileKinds.Detect(path) == FileCategory.Archive;

    public static string? Unavailable(string target) =>
        target == "7z" && Engines.Engines.SevenZip == null ? L.T("need.7zip") : null;

    /// <summary>Extracts into a new folder named after the archive.</summary>
    public static Task<string> ExtractAsync(string input, CancellationToken ct) => Task.Run(() =>
    {
        var folder = OutputPaths.Unique(OutputPaths.DirFor(input), OutputPaths.BaseName(input), "");
        Directory.CreateDirectory(folder);
        try
        {
            ExtractTo(input, folder, ct);
        }
        catch
        {
            try { Directory.Delete(folder, true); } catch { }
            throw;
        }
        return folder;
    }, ct);

    static void ExtractTo(string input, string folder, CancellationToken ct)
    {
        var options = new ExtractionOptions { ExtractFullPath = true, Overwrite = true };
        var root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
        try
        {
            if (FileKinds.Ext(input) is "7z")
            {
                using var archive = ArchiveFactory.OpenArchive(input);
                foreach (var entry in archive.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    if (entry.IsDirectory || entry.Key == null) continue;
                    Guard(root, entry.Key);
                    entry.WriteToDirectory(folder, options);
                }
                return;
            }
            using var stream = File.OpenRead(input);
            using var reader = ReaderFactory.OpenReader(stream);
            while (reader.MoveToNextEntry())
            {
                ct.ThrowIfCancellationRequested();
                if (reader.Entry.IsDirectory) continue;
                var key = reader.Entry.Key;
                if (string.IsNullOrEmpty(key))
                {
                    // Plain .gz / .bz2 / .xz: one stream, named after the archive.
                    var single = Path.Combine(folder, OutputPaths.BaseName(input));
                    using var o = File.Create(single);
                    reader.WriteEntryTo(o);
                    continue;
                }
                Guard(root, key);
                reader.WriteEntryToDirectory(folder, options);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or InvalidFormatException or ArchiveException)
        {
            throw new OpException(e.Message);
        }
    }

    // Refuse "../../evil.exe" style entries (zip slip).
    static void Guard(string root, string key)
    {
        var full = Path.GetFullPath(Path.Combine(root, key));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new OpException("Unsafe path in archive: " + key);
    }

    /// <summary>
    /// Packs files and folders into one archive. Archives given as input are repacked
    /// (zip -> 7z etc.) one by one.
    /// </summary>
    public static async Task<IReadOnlyList<string>> PackAsync(IReadOnlyList<string> inputs, string target, Action<double>? progress, CancellationToken ct)
    {
        if (inputs.All(CanRead))
        {
            var outputs = new List<string>();
            for (int i = 0; i < inputs.Count; i++)
            {
                using var tmp = new TempDir();
                await Task.Run(() => ExtractTo(inputs[i], tmp.Path, ct), ct);
                var output = OutputPaths.For(inputs[i], target);
                await PackEntriesAsync(Directory.GetFileSystemEntries(tmp.Path), output, target, ct);
                outputs.Add(output);
                progress?.Invoke((i + 1.0) / inputs.Count);
            }
            return outputs;
        }
        var name = inputs.Count == 1 ? OutputPaths.BaseName(inputs[0]) : L.T("archive");
        var path = OutputPaths.Unique(OutputPaths.DirFor(inputs[0]), name, "." + target);
        await PackEntriesAsync(inputs, path, target, ct);
        return [path];
    }

    static async Task PackEntriesAsync(IReadOnlyList<string> inputs, string output, string target, CancellationToken ct)
    {
        try
        {
            switch (target)
            {
                case "zip":
                    await Task.Run(() => Zip(inputs, output, ct), ct);
                    break;
                case "tar":
                case "tar.gz":
                    await Task.Run(() => Tar(inputs, output, target == "tar.gz", ct), ct);
                    break;
                case "7z":
                    var exe = Engines.Engines.SevenZip ?? throw new OpException(L.T("need.7zip"));
                    var r = await ProcessRunner.RunAsync(exe, ["a", "-t7z", "-mx=7", "-y", output, .. inputs.Select(Path.GetFullPath)], null, ct);
                    if (!r.Ok) throw new OpException(r.Tail());
                    break;
                default:
                    throw new OpException("Unsupported archive: " + target);
            }
        }
        catch
        {
            if (File.Exists(output)) File.Delete(output);
            throw;
        }
    }

    static IEnumerable<(string Full, string Entry)> Walk(IReadOnlyList<string> inputs)
    {
        foreach (var input in inputs)
        {
            var full = Path.GetFullPath(input.TrimEnd('\\', '/'));
            if (Directory.Exists(full))
            {
                var parent = Path.GetDirectoryName(full)!;
                yield return (full, Path.GetRelativePath(parent, full).Replace('\\', '/') + "/");
                foreach (var e in Directory.EnumerateFileSystemEntries(full, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(parent, e).Replace('\\', '/');
                    yield return Directory.Exists(e) ? (e, rel + "/") : (e, rel);
                }
            }
            else if (File.Exists(full))
            {
                yield return (full, Path.GetFileName(full));
            }
        }
    }

    static void Zip(IReadOnlyList<string> inputs, string output, CancellationToken ct)
    {
        using var zip = ZipFile.Open(output, ZipArchiveMode.Create);
        foreach (var (full, entry) in Walk(inputs))
        {
            ct.ThrowIfCancellationRequested();
            if (entry.EndsWith('/')) zip.CreateEntry(entry);
            else zip.CreateEntryFromFile(full, entry, CompressionLevel.Optimal);
        }
    }

    static void Tar(IReadOnlyList<string> inputs, string output, bool gzip, CancellationToken ct)
    {
        using var file = File.Create(output);
        using Stream stream = gzip ? new GZipStream(file, CompressionLevel.Optimal) : file;
        using var tar = new TarWriter(stream, TarEntryFormat.Pax);
        foreach (var (full, entry) in Walk(inputs))
        {
            ct.ThrowIfCancellationRequested();
            tar.WriteEntry(full, entry.TrimEnd('/'));
        }
    }
}
