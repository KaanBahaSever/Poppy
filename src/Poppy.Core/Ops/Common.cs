using System.Globalization;

namespace Poppy.Core.Ops;

/// <summary>Answers collected from a dialog, keyed by name ("start", "quality", ...).</summary>
public sealed class OpOptions : Dictionary<string, string>
{
    public OpOptions() : base(StringComparer.OrdinalIgnoreCase) { }

    public string Get(string key, string fallback = "") => TryGetValue(key, out var v) ? v : fallback;

    public double GetDouble(string key, double fallback = 0) =>
        TryGetValue(key, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;

    public int GetInt(string key, int fallback = 0) => (int)Math.Round(GetDouble(key, fallback));

    public bool GetBool(string key) => TryGetValue(key, out var v) && (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));

    public OpOptions Set(string key, object value)
    {
        this[key] = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        return this;
    }
}

public sealed record OpResult(IReadOnlyList<string> Outputs, string? Message = null);

public sealed class OpException(string message) : Exception(message);

/// <summary>Everything an operation needs for one run over the dropped files.</summary>
public sealed class OpContext
{
    public required IReadOnlyList<string> Inputs { get; init; }
    public OpOptions Options { get; init; } = new();
    public IProgress<double>? Progress { get; init; }
    public CancellationToken Ct { get; init; }

    /// <summary>Overall progress for file <paramref name="index"/> being <paramref name="fraction"/> done.</summary>
    public void Report(int index, double fraction) =>
        Progress?.Report(Math.Clamp((index + Math.Clamp(fraction, 0, 1)) / Math.Max(1, Inputs.Count), 0, 1));
}

/// <summary>Picks where results go: next to the original unless the user chose a folder.</summary>
public static class OutputPaths
{
    /// <summary>Set from settings. Null means "same folder as the original".</summary>
    public static string? OverrideDir { get; set; }

    public static string DirFor(string input)
    {
        if (OverrideDir is { Length: > 0 } d && Directory.Exists(d)) return d;
        var full = Path.GetFullPath(input.TrimEnd('\\', '/'));
        return Path.GetDirectoryName(full) ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
    }

    public static string BaseName(string input)
    {
        var name = Path.GetFileName(input.TrimEnd('\\', '/'));
        if (Directory.Exists(input)) return name;
        if (name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)) return name[..^7];
        return Path.GetFileNameWithoutExtension(name);
    }

    /// <summary>"photo.png" -> "photo.jpg", or "photo-compressed.png" with a suffix. Never overwrites.</summary>
    public static string For(string input, string ext, string? suffix = null) =>
        Unique(DirFor(input), BaseName(input) + (suffix == null ? "" : "-" + suffix), "." + ext);

    /// <summary>A new folder for multi-file results ("deck-slides").</summary>
    public static string NewFolder(string input, string suffix)
    {
        var dir = Unique(DirFor(input), BaseName(input) + "-" + suffix, "");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string Unique(string dir, string name, string ext)
    {
        var path = Path.Combine(dir, name + ext);
        for (int i = 1; File.Exists(path) || Directory.Exists(path); i++)
            path = Path.Combine(dir, $"{name} ({i}){ext}");
        return path;
    }
}

/// <summary>Orders "photo2" before "photo10", the way File Explorer does.</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? a, string? b)
    {
        if (a == null || b == null) return string.Compare(a, b, StringComparison.Ordinal);
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                var na = a[si..i].TrimStart('0');
                var nb = b[sj..j].TrimStart('0');
                var c = na.Length != nb.Length ? na.Length.CompareTo(nb.Length) : string.CompareOrdinal(na, nb);
                if (c != 0) return c;
            }
            else
            {
                var c = string.Compare(a[i].ToString(), b[j].ToString(), StringComparison.CurrentCultureIgnoreCase);
                if (c != 0) return c;
                i++;
                j++;
            }
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }
}

/// <summary>A scratch folder that is deleted when disposed.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Poppy", Guid.NewGuid().ToString("N")[..12]);

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { }
    }
}

/// <summary>Runs code on a fresh STA thread (Office automation needs one).</summary>
public static class Sta
{
    public static Task<T> Run<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var t = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception e) { tcs.SetException(e); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        return tcs.Task;
    }
}
