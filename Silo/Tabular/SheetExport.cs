using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Silo.Tabular;

/// <summary>The output formats a table can be written as.</summary>
public enum SheetFormat
{
    Markdown,
    Html,
    Tsv,
    Csv,
    Json,
    AlignedText
}

/// <summary>
///     Writes a <see cref="SheetData" /> as a document: markdown (GitHub pipe table), HTML, TSV (what Slack and
///     spreadsheets take on paste), CSV, JSON, and monospaced aligned text. Rich text cells carry their formatting into
///     markdown and HTML and are flattened to plain text everywhere else.
/// </summary>
public static class SheetExport
{
    public static readonly IReadOnlyList<(SheetFormat Format, string Name, string Extension)> Formats =
    [
        (SheetFormat.Markdown, "Markdown", ".md"),
        (SheetFormat.Html, "HTML", ".html"),
        (SheetFormat.Tsv, "TSV (Slack / spreadsheet)", ".tsv"),
        (SheetFormat.Csv, "CSV", ".csv"),
        (SheetFormat.Json, "JSON", ".json"),
        (SheetFormat.AlignedText, "Aligned text", ".txt")
    ];

    public static string Write(SheetData sheet, SheetFormat format)
    {
        return format switch
               {
                   SheetFormat.Markdown    => ToMarkdown(sheet),
                   SheetFormat.Html        => ToHtml(sheet),
                   SheetFormat.Tsv         => ToTsv(sheet),
                   SheetFormat.Csv         => ToCsv(sheet),
                   SheetFormat.Json        => ToJson(sheet),
                   SheetFormat.AlignedText => ToAlignedText(sheet),
                   _                       => throw new ArgumentOutOfRangeException(nameof(format))
               };
    }

    // ---- markdown ---------------------------------------------------------------------------------

    /// <summary>
    ///     A GitHub-flavoured pipe table, padded so the raw text lines up. Rich text cells are already markdown and go
    ///     in as written; text cells are escaped so they show literally. Line breaks become &lt;br&gt;, pipes are escaped.
    /// </summary>
    public static string ToMarkdown(SheetData sheet, bool pad = true)
    {
        if (sheet.Columns.Count == 0) return string.Empty;
        int cols = sheet.Columns.Count;

        var head = sheet.Columns.Select(c => MarkdownCell(c.Name, CellType.Text)).ToArray();
        var body = sheet.Rows.Select(r => Enumerable.Range(0, cols).Select(i => MarkdownCell(Cell(r, i), sheet.Columns[i].Type)).ToArray()).ToList();

        var width = new int[cols];
        for (int c = 0; c < cols; c++)
        {
            int w = Math.Max(3, head[c].Length);
            foreach (var r in body) w = Math.Max(w, r[c].Length);
            width[c] = pad ? w : 3;
        }

        var sb = new StringBuilder();
        AppendMdRow(sb, head, width, sheet, pad);
        sb.Append('|');
        for (int c = 0; c < cols; c++)
        {
            int w = width[c];
            sb.Append(' ');
            sb.Append(sheet.Columns[c].EffectiveAlign switch
                      {
                          CellAlign.Right  => new string('-', w - 1) + ":",
                          CellAlign.Center => ":" + new string('-', Math.Max(1, w - 2)) + ":",
                          _                => new string('-', w)
                      });
            sb.Append(" |");
        }

        sb.Append('\n');
        foreach (var r in body) AppendMdRow(sb, r, width, sheet, pad);
        return sb.ToString();
    }

    private static void AppendMdRow(StringBuilder sb, string[] cells, int[] width, SheetData sheet, bool pad)
    {
        sb.Append('|');
        for (int c = 0; c < cells.Length; c++)
        {
            sb.Append(' ');
            string t = cells[c];
            if (!pad) { sb.Append(t).Append(" |"); continue; }

            switch (sheet.Columns[c].EffectiveAlign)
            {
                case CellAlign.Right:
                    sb.Append(t.PadLeft(width[c]));
                    break;
                case CellAlign.Center:
                    int total = width[c] - t.Length, left = total / 2;
                    sb.Append(new string(' ', left)).Append(t).Append(new string(' ', total - left));
                    break;
                default:
                    sb.Append(t.PadRight(width[c]));
                    break;
            }

            sb.Append(" |");
        }

        sb.Append('\n');
    }

    private static string MarkdownCell(string? text, CellType type)
    {
        string t = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        t = type == CellType.RichText ? t.Replace("|", "\\|") : InlineMarkup.EscapeMarkdown(t);
        return t.Replace("\n", "<br>").Trim();
    }

    // ---- html -------------------------------------------------------------------------------------

    /// <summary>
    ///     An HTML table. <paramref name="styled" /> adds inline borders and padding so it still looks like a table when
    ///     pasted into a mail or a document that has no stylesheet of its own.
    /// </summary>
    public static string ToHtml(SheetData sheet, bool styled = false)
    {
        if (sheet.Columns.Count == 0) return string.Empty;

        string table = styled ? " style=\"border-collapse:collapse\"" : string.Empty;
        string cell   = styled ? "border:1px solid #bbb;padding:4px 8px;" : string.Empty;
        string headBg = styled ? "background:#f0f0f0;" : string.Empty;

        string Style(SheetColumn c, string extra = "")
        {
            string align = c.EffectiveAlign == CellAlign.Left ? string.Empty : $"text-align:{c.EffectiveAlign.ToString().ToLowerInvariant()};";
            string all   = cell + extra + align;
            return all.Length == 0 ? string.Empty : $" style=\"{all}\"";
        }

        var sb = new StringBuilder();
        sb.Append("<table").Append(table).Append(">\n<thead>\n<tr>");
        foreach (var c in sheet.Columns) sb.Append("<th").Append(Style(c, headBg)).Append('>').Append(InlineMarkup.ToHtml(c.Name, false)).Append("</th>");
        sb.Append("</tr>\n</thead>\n<tbody>\n");
        foreach (var r in sheet.Rows)
        {
            sb.Append("<tr>");
            for (int i = 0; i < sheet.Columns.Count; i++)
                sb.Append("<td").Append(Style(sheet.Columns[i])).Append('>')
                  .Append(InlineMarkup.ToHtml(Cell(r, i), sheet.Columns[i].Type == CellType.RichText))
                  .Append("</td>");
            sb.Append("</tr>\n");
        }

        sb.Append("</tbody>\n</table>\n");
        return sb.ToString();
    }

    /// <summary>A complete, standalone .html page holding the (styled) table.</summary>
    public static string ToHtmlDocument(SheetData sheet, string title)
    {
        return "<!doctype html>\n<html>\n<head>\n<meta charset=\"utf-8\">\n<title>" + WebUtility.HtmlEncode(title)
               + "</title>\n</head>\n<body style=\"font-family:Segoe UI,Arial,sans-serif\">\n" + ToHtml(sheet, true) + "</body>\n</html>\n";
    }

    // ---- tsv / csv --------------------------------------------------------------------------------

    /// <summary>
    ///     Tab-separated, header row first. A cell holding a tab, line break or quote is quoted (quotes doubled), which
    ///     is what Excel, Google Sheets and Slack's table paste all read back as one cell.
    /// </summary>
    public static string ToTsv(SheetData sheet) => Delimited(sheet, '\t', "\n");

    /// <summary>Comma-separated per RFC 4180 (CRLF line ends).</summary>
    public static string ToCsv(SheetData sheet) => Delimited(sheet, ',', "\r\n");

    private static string Delimited(SheetData sheet, char sep, string eol)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(sep, sheet.Columns.Select(c => Quote(c.Name, sep)))).Append(eol);
        foreach (var r in sheet.Rows)
            sb.Append(string.Join(sep, Enumerable.Range(0, sheet.Columns.Count).Select(i => Quote(Plain(sheet, r, i), sep)))).Append(eol);
        return sb.ToString();
    }

    private static string Quote(string s, char sep)
    {
        return s.IndexOfAny([sep, '"', '\n', '\r']) < 0 ? s : "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    // ---- json -------------------------------------------------------------------------------------

    /// <summary>An array of objects keyed by header. Numbers and booleans are written as such when they parse; rich text keeps its markdown.</summary>
    public static string ToJson(SheetData sheet)
    {
        var headers = sheet.UniqueHeaders();
        using var ms = new System.IO.MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartArray();
            foreach (var r in sheet.Rows)
            {
                w.WriteStartObject();
                for (int i = 0; i < headers.Count; i++)
                {
                    string v = Cell(r, i);
                    w.WritePropertyName(headers[i]);
                    switch (sheet.Columns[i].Type)
                    {
                        case CellType.Number when decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var d):
                            w.WriteNumberValue(d);
                            break;
                        case CellType.Boolean when TryBool(v, out bool b):
                            w.WriteBooleanValue(b);
                            break;
                        default:
                            w.WriteStringValue(v);
                            break;
                    }
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }

    public static bool TryBool(string? s, out bool value)
    {
        switch ((s ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "true" or "yes" or "y" or "1" or "x" or "on" or "✓":
                value = true;
                return true;
            case "false" or "no" or "n" or "0" or "off" or "":
                value = false;
                return true;
            default:
                value = false;
                return false;
        }
    }

    // ---- aligned text -----------------------------------------------------------------------------

    /// <summary>
    ///     Monospaced columns separated by two spaces under a dashed header rule; <paramref name="fenced" /> wraps it in
    ///     a code fence so Slack, chat and markdown keep the alignment.
    /// </summary>
    public static string ToAlignedText(SheetData sheet, bool fenced = false)
    {
        int cols = sheet.Columns.Count;
        if (cols == 0) return string.Empty;

        var head = sheet.Columns.Select(c => Flat(c.Name)).ToArray();
        var body = sheet.Rows.Select(r => Enumerable.Range(0, cols).Select(i => Flat(Plain(sheet, r, i))).ToArray()).ToList();
        var w    = Enumerable.Range(0, cols).Select(c => Math.Max(head[c].Length, body.Count == 0 ? 0 : body.Max(r => r[c].Length))).ToArray();

        string Line(string[] cells) =>
            string.Join("  ", Enumerable.Range(0, cols).Select(c => sheet.Columns[c].EffectiveAlign == CellAlign.Right ? cells[c].PadLeft(w[c]) : cells[c].PadRight(w[c]))).TrimEnd();

        var sb = new StringBuilder();
        if (fenced) sb.Append("```\n");
        sb.Append(Line(head)).Append('\n');
        sb.Append(string.Join("  ", w.Select(n => new string('-', Math.Max(1, n))))).Append('\n');
        foreach (var r in body) sb.Append(Line(r)).Append('\n');
        if (fenced) sb.Append("```\n");
        return sb.ToString();
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private static string Cell(string[] row, int i) => i < row.Length ? row[i] ?? string.Empty : string.Empty;

    /// <summary>The cell as plain text (rich text flattened).</summary>
    private static string Plain(SheetData sheet, string[] row, int i)
    {
        string v = Cell(row, i);
        return sheet.Columns[i].Type == CellType.RichText ? InlineMarkup.ToPlain(v) : v;
    }

    private static string Flat(string s) => s.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');
}
