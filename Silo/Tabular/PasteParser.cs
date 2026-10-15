using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Silo.Tabular;

/// <summary>What was on the clipboard, as text, per format (any may be null). Free of WPF so the parsing can be tested.</summary>
public sealed record ClipboardPayload(string? SiloTable, string? Html, string? Csv, string? Text);

/// <summary>A table found on the clipboard.</summary>
public sealed class ParsedPaste
{
    /// <summary>The clipboard format it was read from: <c>Silo.Table</c>, <c>HTML Format</c>, <c>UnicodeText</c> or <c>Csv</c>.</summary>
    public required string Format { get; init; }

    /// <summary>The text of that format exactly as it was on the clipboard (kept as provenance).</summary>
    public required string Raw { get; init; }

    /// <summary>The cells, every row the same length. For a <c>Silo.Table</c> paste these are the data rows.</summary>
    public required List<string[]> Rows { get; init; }

    /// <summary>Set when the paste is one of the Silo's own tables: it brings its column names, types and alignment along.</summary>
    public SheetData? Sheet { get; init; }

    /// <summary>The source marked its first row as a header (HTML <c>th</c> cells).</summary>
    public bool HeaderHint { get; init; }

    public int ColumnCount => Rows.Count == 0 ? 0 : Rows[0].Length;

    /// <summary>"12 rows x 4 columns, from an HTML table", for the paste dialog.</summary>
    public string Describe()
    {
        string source = Format switch
                        {
                            "Silo.Table"  => "a Silo table",
                            "HTML Format" => "an HTML table",
                            "Csv"         => "CSV text",
                            _             => "text"
                        };
        return $"{Rows.Count} row(s) x {ColumnCount} column(s), from {source}";
    }
}

/// <summary>
///     Turns what is on the clipboard into a table: the Silo's own table format first (it keeps types), then an HTML table
///     (Excel, Word, web pages), then tab- or comma-separated text. Also guesses whether the first row is a header and what
///     type each column is.
/// </summary>
public static class PasteParser
{
    public static ParsedPaste? Parse(ClipboardPayload payload)
    {
        if (!string.IsNullOrWhiteSpace(payload.SiloTable))
            try
            {
                var sheet = SheetData.FromJson(payload.SiloTable);
                if (sheet.Columns.Count > 0)
                    return new ParsedPaste
                           {
                               Format = "Silo.Table",
                               Raw    = payload.SiloTable,
                               Sheet  = sheet,
                               Rows   = sheet.Rows.Select(r => (string[])r.Clone()).ToList()
                           };
            }
            catch (JsonException)
            {
                // not ours after all: fall through to the other formats
            }

        if (!string.IsNullOrWhiteSpace(payload.Html))
        {
            var rows = ParseHtmlTable(payload.Html, out bool hasHeader);
            if (rows is { Count: > 0 })
                return new ParsedPaste { Format = "HTML Format", Raw = payload.Html, Rows = rows, HeaderHint = hasHeader };
        }

        string? text   = !string.IsNullOrEmpty(payload.Text) ? payload.Text : payload.Csv;
        var     format = !string.IsNullOrEmpty(payload.Text) ? "UnicodeText" : "Csv";
        if (string.IsNullOrWhiteSpace(text)) return null;

        var parsed = ParseText(text);
        return parsed.Count == 0 ? null : new ParsedPaste { Format = format, Raw = text, Rows = parsed };
    }

    // ---- text ----------------------------------------------------------------------------------

    /// <summary>Tab-separated (what Excel and the Silo copy), else comma / semicolon / bar separated when several lines agree, else one cell per line.</summary>
    public static List<string[]> ParseText(string text)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');

        if (text.Contains('\t')) return ParseDelimited(text, '\t');

        if (text.Contains('\n'))
            foreach (char delimiter in new[] { ',', ';', '|' })
            {
                // only when every non-blank line splits into the same number of cells (prose with a stray comma does not)
                var raw   = SplitDelimited(text, delimiter);
                var filled = raw.Where(r => !(r.Count == 1 && r[0].Length == 0)).ToList();
                if (filled.Count >= 2 && filled[0].Count >= 2 && filled.All(r => r.Count == filled[0].Count)) return Rectangular(raw);
            }

        // no separator: every line is a row of one cell (a single line is a single cell)
        var lines = text.Split('\n').ToList();
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines.Select(l => new[] { l }).ToList();
    }

    /// <summary>Splits delimited text with double-quote quoting (a quoted cell may hold the delimiter, line breaks and doubled quotes). Every row is padded to the widest.</summary>
    public static List<string[]> ParseDelimited(string text, char delimiter) => Rectangular(SplitDelimited(text, delimiter));

    private static List<List<string>> SplitDelimited(string text, char delimiter)
    {
        var rows       = new List<List<string>>();
        var row        = new List<string>();
        var cell       = new StringBuilder();
        bool quoted    = false;
        bool wasQuoted = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        cell.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    cell.Append(c);
                }
            }
            else if (c == '"' && cell.Length == 0 && !wasQuoted)
            {
                quoted    = true;
                wasQuoted = true;
            }
            else if (c == delimiter)
            {
                row.Add(cell.ToString());
                cell.Clear();
                wasQuoted = false;
            }
            else if (c == '\n')
            {
                row.Add(cell.ToString());
                cell.Clear();
                wasQuoted = false;
                rows.Add(row);
                row = [];
            }
            else
            {
                cell.Append(c);
            }
        }

        if (cell.Length > 0 || row.Count > 0 || wasQuoted)
        {
            row.Add(cell.ToString());
            rows.Add(row);
        }

        // a trailing blank line is not a row
        while (rows.Count > 0 && rows[^1].All(string.IsNullOrEmpty)) rows.RemoveAt(rows.Count - 1);

        return rows;
    }

    private static List<string[]> Rectangular(List<List<string>> rows)
    {
        int width = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
        return rows.Select(r =>
                           {
                               var arr = new string[width];
                               for (int i = 0; i < width; i++) arr[i] = i < r.Count ? r[i] : string.Empty;
                               return arr;
                           }).ToList();
    }

    // ---- HTML ----------------------------------------------------------------------------------

    private const RegexOptions Rx = RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>The cells of the first table in an HTML fragment (or a CF_HTML clipboard text), or null if it holds none.</summary>
    public static List<string[]>? ParseHtmlTable(string html, out bool firstRowIsHeader)
    {
        firstRowIsHeader = false;

        const string start = "<!--StartFragment-->";
        const string end   = "<!--EndFragment-->";
        int s = html.IndexOf(start, StringComparison.OrdinalIgnoreCase);
        int e = html.IndexOf(end, StringComparison.OrdinalIgnoreCase);
        string fragment = s >= 0 && e > s ? html.Substring(s + start.Length, e - (s + start.Length)) : html;

        var table = Regex.Match(fragment, @"<table\b.*?</table>", Rx);
        if (!table.Success) return null;

        var rows = new List<List<string>>();
        foreach (Match rowMatch in Regex.Matches(table.Value, @"<tr\b[^>]*>(.*?)</tr>", Rx))
        {
            var  cells = new List<string>();
            bool allTh = true;
            foreach (Match cellMatch in Regex.Matches(rowMatch.Groups[1].Value, @"<t([dh])\b([^>]*)>(.*?)</t\1>", Rx))
            {
                cells.Add(CellText(cellMatch.Groups[3].Value));
                allTh &= cellMatch.Groups[1].Value.Equals("h", StringComparison.OrdinalIgnoreCase);

                // a cell spanning columns leaves empty cells behind it
                var span = Regex.Match(cellMatch.Groups[2].Value, @"colspan\s*=\s*[""']?(\d+)", Rx);
                if (span.Success && int.TryParse(span.Groups[1].Value, out int n))
                    for (int k = 1; k < Math.Min(n, 200); k++)
                        cells.Add(string.Empty);
            }

            if (cells.Count == 0) continue;
            if (rows.Count == 0 && allTh) firstRowIsHeader = true;
            rows.Add(cells);
        }

        return rows.Count == 0 ? null : Rectangular(rows);
    }

    private static string CellText(string innerHtml)
    {
        string s = Regex.Replace(innerHtml, @"<br\s*/?>", "\u0001", Rx);
        s = Regex.Replace(s, @"<[^>]+>", string.Empty, Rx);
        s = WebUtility.HtmlDecode(s).Replace(' ', ' ');
        s = Regex.Replace(s, @"\s+", " ");
        return s.Replace("\u0001", "\n").Trim(' ', '\n');
    }

    // ---- guessing ------------------------------------------------------------------------------

    /// <summary>
    ///     True when the first row reads like column names: every cell filled, distinct, and not a number / date / yes-no, and
    ///     either some column below it holds numbers, dates or yes/no (so the first row differs in kind) or the cells are all short.
    ///     Only a guess -- the paste dialog lets the person decide.
    /// </summary>
    public static bool LooksLikeHeader(IReadOnlyList<string[]> rows)
    {
        if (rows.Count < 2) return false;

        var first = rows[0];
        if (first.Length == 0 || first.Any(string.IsNullOrWhiteSpace)) return false;
        if (first.Select(c => c.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != first.Length) return false;
        if (first.Any(c => IsNumber(c.Trim()) || IsDate(c.Trim()) || SheetExport.TryBool(c.Trim(), out _))) return false;

        for (int c = 0; c < first.Length; c++)
            if (InferType(rows.Skip(1).Select(r => r[c])) != CellType.Text)
                return true;

        return first.All(c => c.Length <= 40);
    }

    /// <summary>The type that fits every non-blank value: number, yes/no, date, else text.</summary>
    public static CellType InferType(IEnumerable<string> values)
    {
        var v = values.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
        if (v.Count == 0) return CellType.Text;
        if (v.All(IsNumber)) return CellType.Number;
        if (v.All(s => SheetExport.TryBool(s, out _))) return CellType.Boolean;
        if (v.All(IsDate)) return CellType.Date;
        return CellType.Text;
    }

    public static bool IsNumber(string s)
    {
        if (s.Length == 0 || s.Length > 30) return false;

        // an identifier that happens to be digits (a zip code, a long id) is text, not a number
        string digits = new(s.Where(char.IsDigit).ToArray());
        if (digits.Length > 15) return false;
        if (s.Length > 1 && s[0] == '0' && s[1] != '.' && s[1] != ',') return false;

        const NumberStyles styles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands;
        return decimal.TryParse(s, styles, CultureInfo.InvariantCulture, out _) || decimal.TryParse(s, styles, CultureInfo.CurrentCulture, out _);
    }

    public static bool IsDate(string s)
    {
        return s.Length >= 6 && s.Any(char.IsDigit) && s.Any(c => c is '-' or '/' or '.' or ':' or ' ') &&
               DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.None, out _);
    }

    // ---- building ------------------------------------------------------------------------------

    /// <summary>
    ///     The sheet a paste becomes as a new table. A Silo table comes back as it was; anything else gets one column per
    ///     pasted column (named from the first row when <paramref name="firstRowIsHeader" />, else A, B, C...), each typed from
    ///     what it holds, cells kept as pasted text.
    /// </summary>
    public static SheetData BuildSheet(ParsedPaste paste, bool firstRowIsHeader)
    {
        if (paste.Sheet is { } own) return own.Clone();

        var rows   = paste.Rows;
        int width  = paste.ColumnCount;
        var header = firstRowIsHeader && rows.Count > 0 ? rows[0] : null;
        var data   = rows.Skip(header is null ? 0 : 1).ToList();

        var sheet = new SheetData();
        for (int c = 0; c < width; c++)
        {
            string name = header is null || string.IsNullOrWhiteSpace(header[c]) ? SheetData.ColumnLetter(c) : header[c].Trim();
            sheet.Columns.Add(new SheetColumn { Name = name, Type = InferType(data.Select(r => r[c])) });
        }

        var unique = sheet.UniqueHeaders();
        for (int c = 0; c < width; c++) sheet.Columns[c].Name = unique[c];

        foreach (var row in data)
        {
            var cells = new string[width];
            for (int c = 0; c < width; c++)
            {
                string value = row[c].Trim();
                cells[c] = sheet.Columns[c].Type == CellType.Boolean && SheetExport.TryBool(value, out bool b) ? (b ? "true" : "false") : value;
            }

            sheet.Rows.Add(cells);
        }

        return sheet;
    }
}
