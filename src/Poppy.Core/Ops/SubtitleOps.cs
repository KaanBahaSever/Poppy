using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Poppy.Core.Engines;

namespace Poppy.Core.Ops;

public sealed record Cue(TimeSpan Start, TimeSpan End, string Text);

/// <summary>SRT / VTT / ASS subtitles. SRT and VTT are handled here; ASS goes through FFmpeg.</summary>
public static partial class SubtitleOps
{
    [GeneratedRegex(@"(?<s>\d{1,2}:\d{2}:\d{2}[,.]\d{1,3}|\d{1,2}:\d{2}[,.]\d{1,3})\s*-->\s*(?<e>\d{1,2}:\d{2}:\d{2}[,.]\d{1,3}|\d{1,2}:\d{2}[,.]\d{1,3})")]
    private static partial Regex TimeLine();

    [GeneratedRegex(@"^(Dialogue:\s*[^,]*,)(\d+:\d{2}:\d{2}\.\d{2}),(\d+:\d{2}:\d{2}\.\d{2})", RegexOptions.Multiline)]
    private static partial Regex AssDialogue();

    [GeneratedRegex(@"<[^>]+>|\{[^}]*\}")]
    private static partial Regex Tags();

    public static string? Unavailable(string src, string target) =>
        (src is "ass" or "ssa" || target == "ass") && Engines.Engines.Ffmpeg == null ? L.T("need.ffmpeg") : null;

    public static async Task<string> ConvertAsync(string input, string target, CancellationToken ct)
    {
        var src = FileKinds.Ext(input);
        var output = OutputPaths.For(input, target);
        if (src is "ass" or "ssa" || target == "ass")
        {
            if (target == "txt")
            {
                // ASS -> SRT with FFmpeg, then strip to text.
                using var tmp = new TempDir();
                var srt = tmp.File("s.srt");
                await Ffmpeg(input, srt, ct);
                await File.WriteAllTextAsync(output, ToText(ParseSrtVtt(await File.ReadAllTextAsync(srt, ct))), new UTF8Encoding(false), ct);
                return output;
            }
            await Ffmpeg(input, output, ct);
            return output;
        }
        var cues = ParseSrtVtt(await File.ReadAllTextAsync(input, ct));
        var text = target switch
        {
            "srt" => ToSrt(cues),
            "vtt" => ToVtt(cues),
            "txt" => ToText(cues),
            _ => throw new OpException("Unsupported subtitle format: " + target),
        };
        await File.WriteAllTextAsync(output, text, new UTF8Encoding(false), ct);
        return output;
    }

    static async Task Ffmpeg(string input, string output, CancellationToken ct)
    {
        var ffmpeg = Engines.Engines.Ffmpeg ?? throw new OpException(L.T("need.ffmpeg"));
        var r = await ProcessRunner.RunAsync(ffmpeg, ["-y", "-hide_banner", "-i", input, output], null, ct, TimeSpan.FromMinutes(2));
        if (!r.Ok) throw new OpException(r.Tail());
    }

    /// <summary>Moves every cue by "ms" milliseconds (negative = earlier).</summary>
    public static async Task<string> ShiftAsync(string input, OpOptions o, CancellationToken ct)
    {
        var offset = TimeSpan.FromMilliseconds(o.GetDouble("ms"));
        var ext = FileKinds.Ext(input);
        var output = OutputPaths.For(input, ext, "shifted");
        var text = await File.ReadAllTextAsync(input, ct);
        string result;
        if (ext is "ass" or "ssa")
        {
            result = AssDialogue().Replace(text, m =>
                m.Groups[1].Value + FormatAss(Clamp(ParseAss(m.Groups[2].Value) + offset)) + "," + FormatAss(Clamp(ParseAss(m.Groups[3].Value) + offset)));
        }
        else
        {
            var cues = ParseSrtVtt(text).Select(c => c with { Start = Clamp(c.Start + offset), End = Clamp(c.End + offset) }).ToList();
            result = ext == "vtt" ? ToVtt(cues) : ToSrt(cues);
        }
        await File.WriteAllTextAsync(output, result, new UTF8Encoding(false), ct);
        return output;
    }

    static TimeSpan Clamp(TimeSpan t) => t < TimeSpan.Zero ? TimeSpan.Zero : t;

    public static List<Cue> ParseSrtVtt(string text)
    {
        var cues = new List<Cue>();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var m = TimeLine().Match(lines[i]);
            if (!m.Success) continue;
            var body = new List<string>();
            int j = i + 1;
            for (; j < lines.Length && lines[j].Trim().Length > 0; j++) body.Add(lines[j]);
            cues.Add(new Cue(ParseTime(m.Groups["s"].Value), ParseTime(m.Groups["e"].Value), string.Join("\n", body)));
            i = j;
        }
        return cues;
    }

    static TimeSpan ParseTime(string s)
    {
        s = s.Replace(',', '.');
        var parts = s.Split(':');
        double h = 0, m, sec;
        if (parts.Length == 3) { h = double.Parse(parts[0], CultureInfo.InvariantCulture); m = double.Parse(parts[1], CultureInfo.InvariantCulture); sec = double.Parse(parts[2], CultureInfo.InvariantCulture); }
        else { m = double.Parse(parts[0], CultureInfo.InvariantCulture); sec = double.Parse(parts[1], CultureInfo.InvariantCulture); }
        return TimeSpan.FromSeconds(h * 3600 + m * 60 + sec);
    }

    static TimeSpan ParseAss(string s)
    {
        var p = s.Split(':');
        return TimeSpan.FromSeconds(int.Parse(p[0]) * 3600 + int.Parse(p[1]) * 60 + double.Parse(p[2], CultureInfo.InvariantCulture));
    }

    static string FormatAss(TimeSpan t) => $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds / 10:00}";

    static string Stamp(TimeSpan t, char sep) => $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}{sep}{t.Milliseconds:000}";

    public static string ToSrt(IEnumerable<Cue> cues)
    {
        var sb = new StringBuilder();
        int n = 1;
        foreach (var c in cues)
            sb.Append(n++).Append("\r\n").Append(Stamp(c.Start, ',')).Append(" --> ").Append(Stamp(c.End, ',')).Append("\r\n").Append(c.Text.Replace("\n", "\r\n")).Append("\r\n\r\n");
        return sb.ToString();
    }

    public static string ToVtt(IEnumerable<Cue> cues)
    {
        var sb = new StringBuilder("WEBVTT\n\n");
        foreach (var c in cues)
            sb.Append(Stamp(c.Start, '.')).Append(" --> ").Append(Stamp(c.End, '.')).Append('\n').Append(c.Text).Append("\n\n");
        return sb.ToString();
    }

    public static string ToText(IEnumerable<Cue> cues) =>
        string.Join("\r\n", cues.Select(c => Tags().Replace(c.Text, "").Replace("\n", " ").Trim()).Where(t => t.Length > 0)) + "\r\n";
}
