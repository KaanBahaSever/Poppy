using System.Net;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;

namespace Poppy.Core.Ops;

/// <summary>Spreadsheet conversions that need no Office: XLSX/CSV/TSV in, XLSX/CSV/TSV/JSON/HTML out.</summary>
public static class SheetOps
{
    static readonly HashSet<string> In = ["xlsx", "csv", "tsv"];
    static readonly HashSet<string> Out = ["xlsx", "csv", "tsv", "json", "html"];

    public static bool CanConvert(string src, string target) => In.Contains(src) && Out.Contains(target);

    public static Task<string> ConvertAsync(string input, string output, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        var rows = Read(input);
        switch (FileKinds.Ext(output))
        {
            case "xlsx": WriteXlsx(rows, output, OutputPaths.BaseName(input)); break;
            case "csv": File.WriteAllText(output, ToDelimited(rows, ','), new UTF8Encoding(true)); break;
            case "tsv": File.WriteAllText(output, ToDelimited(rows, '\t'), new UTF8Encoding(false)); break;
            case "json": File.WriteAllText(output, ToJson(rows)); break;
            case "html": File.WriteAllText(output, ToHtml(rows, OutputPaths.BaseName(input))); break;
        }
        return output;
    }, ct);

    public static List<List<string>> Read(string path)
    {
        switch (FileKinds.Ext(path))
        {
            case "xlsx":
                using (var wb = new XLWorkbook(path))
                {
                    var ws = wb.Worksheets.First();
                    var used = ws.RangeUsed();
                    if (used == null) return [];
                    var rows = new List<List<string>>();
                    foreach (var row in used.Rows())
                        rows.Add(row.Cells().Select(c => c.GetFormattedString()).ToList());
                    return rows;
                }
            case "tsv":
                return ParseDelimited(File.ReadAllText(path), '\t');
            default:
                var text = File.ReadAllText(path);
                return ParseDelimited(text, GuessDelimiter(text));
        }
    }

    /// <summary>Comma, semicolon (Excel in many locales, Turkish included) or tab.</summary>
    public static char GuessDelimiter(string text)
    {
        var firstLine = text.Split('\n', 2)[0];
        var counts = new[] { ',', ';', '\t' }.Select(c => (c, n: firstLine.Count(ch => ch == c)));
        var best = counts.OrderByDescending(x => x.n).First();
        return best.n > 0 ? best.c : ',';
    }

    /// <summary>RFC 4180: quoted fields, doubled quotes, newlines inside quotes.</summary>
    public static List<List<string>> ParseDelimited(string text, char sep)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else quoted = false;
                }
                else field.Append(c);
            }
            else if (c == '"' && field.Length == 0) quoted = true;
            else if (c == sep) { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString());
                field.Clear();
                rows.Add(row);
                row = [];
            }
            else if (c != '\uFEFF' || i > 0) field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        return rows;
    }

    public static string ToDelimited(List<List<string>> rows, char sep)
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            sb.Append(string.Join(sep, row.Select(v =>
                v.IndexOfAny([sep, '"', '\n', '\r']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v)));
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>An array of objects keyed by the header row; numbers stay numbers.</summary>
    public static string ToJson(List<List<string>> rows)
    {
        if (rows.Count == 0) return "[]";
        var header = rows[0].Select((h, i) => string.IsNullOrWhiteSpace(h) ? "column" + (i + 1) : h).ToList();
        var list = new List<Dictionary<string, object?>>();
        foreach (var row in rows.Skip(1))
        {
            var o = new Dictionary<string, object?>();
            for (int i = 0; i < header.Count; i++)
            {
                var v = i < row.Count ? row[i] : "";
                o[header[i]] = double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) && v.Trim() == v && v.Length > 0
                    ? d : v.Length == 0 ? null : v;
            }
            list.Add(o);
        }
        return JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    static string ToHtml(List<List<string>> rows, string title)
    {
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>").Append(WebUtility.HtmlEncode(title)).Append("</title>");
        sb.Append("<style>body{font-family:Segoe UI,Arial,sans-serif;margin:2em}table{border-collapse:collapse}td,th{border:1px solid #ccc;padding:.35em .7em;text-align:left}th{background:#f3f3f3}</style></head><body><table>");
        for (int r = 0; r < rows.Count; r++)
        {
            var tag = r == 0 ? "th" : "td";
            sb.Append("<tr>");
            foreach (var v in rows[r]) sb.Append('<').Append(tag).Append('>').Append(WebUtility.HtmlEncode(v)).Append("</").Append(tag).Append('>');
            sb.Append("</tr>");
        }
        sb.Append("</table></body></html>");
        return sb.ToString();
    }

    static void WriteXlsx(List<List<string>> rows, string output, string sheetName)
    {
        using var wb = new XLWorkbook();
        var name = new string(sheetName.Where(c => !"[]:*?/\\".Contains(c)).ToArray());
        var ws = wb.Worksheets.Add(name.Length == 0 ? "Sheet1" : name[..Math.Min(31, name.Length)]);
        for (int r = 0; r < rows.Count; r++)
            for (int c = 0; c < rows[r].Count; c++)
            {
                var v = rows[r][c];
                var cell = ws.Cell(r + 1, c + 1);
                if (double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) && v.Trim() == v && v.Length > 0 && !(v.Length > 1 && v[0] == '0' && v[1] != '.'))
                    cell.Value = d;
                else
                    cell.Value = v;
            }
        if (rows.Count > 0)
        {
            ws.Row(1).Style.Font.Bold = true;
            ws.Columns().AdjustToContents(1, Math.Min(rows.Count, 200));
        }
        wb.SaveAs(output);
    }
}
