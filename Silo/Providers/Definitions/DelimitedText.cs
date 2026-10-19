namespace Silo.Providers.Definitions;

/// <summary>
///     Reads and writes delimited text (CSV with commas, TSV with tabs) the way spreadsheets do: fields containing the delimiter, a quote or a
///     line break are wrapped in double quotes and a quote inside is written twice; rows end at a line break (CRLF, LF or CR).
/// </summary>
public static class DelimitedText
{
    public const char Comma = ',';
    public const char Tab   = '\t';

    /// <summary>Reads the text into rows of fields. A final line break does not add an empty row; text with no characters has no rows. Returns false, with the problem, when a quote is not closed or something follows a closing quote.</summary>
    public static bool TryParse(string? text, char delimiter, out string[][] rows, out string? error)
    {
        rows  = [];
        error = null;
        if (string.IsNullOrEmpty(text)) return true;

        var result = new List<string[]>();
        var row    = new List<string>();
        var field  = new StringBuilder();
        bool inQuotes = false, afterQuote = false, quoted = false;
        int line = 1;

        void EndField()
        {
            row.Add(field.ToString());
            field.Clear();
            quoted     = false;
            afterQuote = false;
        }

        void EndRow()
        {
            EndField();
            result.Add(row.ToArray());
            row.Clear();
            line++;
        }

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes   = false;
                        afterQuote = true;
                    }
                }
                else
                {
                    if (c == '\n') line++;
                    field.Append(c);
                }

                continue;
            }

            if (c == delimiter)
            {
                EndField();
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                EndRow();
            }
            else if (afterQuote)
            {
                error = $"Line {line}: something follows a closing quote.";
                return false;
            }
            else if (c == '"')
            {
                if (field.Length > 0 || quoted)
                {
                    error = $"Line {line}: a quote in the middle of a field (write it twice, inside a quoted field).";
                    return false;
                }

                inQuotes = true;
                quoted   = true;
            }
            else
            {
                field.Append(c);
            }
        }

        if (inQuotes)
        {
            error = $"Line {line}: a quote is never closed.";
            return false;
        }

        if (field.Length > 0 || row.Count > 0 || quoted) EndRow();

        rows = result.ToArray();
        return true;
    }

    /// <summary>Writes the rows: fields quoted only where needed, rows separated by CRLF, no line break after the last.</summary>
    public static string Format(IEnumerable<IReadOnlyList<string>> rows, char delimiter)
    {
        var sb    = new StringBuilder();
        bool firstRow = true;
        foreach (var row in rows)
        {
            if (!firstRow) sb.Append("\r\n");
            firstRow = false;

            for (int i = 0; i < row.Count; i++)
            {
                if (i > 0) sb.Append(delimiter);
                AppendField(sb, row[i] ?? string.Empty, delimiter);
            }
        }

        return sb.ToString();
    }

    private static void AppendField(StringBuilder sb, string field, char delimiter)
    {
        if (field.IndexOfAny([delimiter, '"', '\r', '\n']) < 0)
        {
            sb.Append(field);
            return;
        }

        sb.Append('"').Append(field.Replace("\"", "\"\"")).Append('"');
    }
}
