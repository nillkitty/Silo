using System.Linq;
using System.Threading;
using Silo.Providers.Diagramming;
using Silo.Providers.Reflection;
using Telefrag.DI;

namespace Silo.Providers.Format;

/// <summary>
///     The base of the delimited-text formats (CSV, TSV): the first line names the columns, each line after it is a row.
///     A bound node gets the file as <see cref="DelimitedRows" />.
/// </summary>
public abstract class DelimitedFormat : IBindableFormat
{
    /// <summary>The character between the fields of a line.</summary>
    public abstract char Delimiter { get; }

    public abstract string DisplayName   { get; }
    public abstract string FileExtension { get; }
    public abstract string ContentType   { get; }

    /// <summary>Reads the whole file as rows.</summary>
    public DelimitedRows Read(string path)
    {
        return DelimitedRows.Read(path, Delimiter);
    }

    /// <inheritdoc />
    public object? Bind(Uri uri)
    {
        return Read(uri.LocalPath);
    }
}

/// <summary>Comma-separated values.</summary>
[Singleton(typeof(IFileFormat))]
public sealed class CsvFormat : DelimitedFormat
{
    public override char   Delimiter     => ',';
    public override string DisplayName   => "CSV File";
    public override string FileExtension => "csv";
    public override string ContentType   => "text/csv";
}

/// <summary>Tab-separated values.</summary>
[Singleton(typeof(IFileFormat))]
public sealed class TsvFormat : DelimitedFormat
{
    public override char   Delimiter     => '\t';
    public override string DisplayName   => "TSV File";
    public override string FileExtension => "tsv,tab";
    public override string ContentType   => "text/tab-separated-values";
}

/// <summary>A CSV / TSV file as rows (the first line names the columns), read for a Table or List node.</summary>
public sealed class DelimitedRows : IRowSource
{
    private readonly List<string[]> _rows;

    private readonly string _name;

    private DelimitedRows(string name, IReadOnlyList<string> columns, List<string[]> rows)
    {
        _name   = name;
        Columns = columns;
        _rows   = rows;
    }

    /// <summary>The file's name (what <c>{.}</c> shows for the node).</summary>
    public override string ToString()
    {
        return _name;
    }

    public IReadOnlyList<string> Columns { get; }

    public ValueTask<RowPage> GetRowsAsync(int limit, CancellationToken cancel)
    {
        var rows = _rows.Take(limit).Select((cells, i) => (IRowItem)new Row(i.ToString(System.Globalization.CultureInfo.InvariantCulture), Columns, cells)).ToList();
        return new ValueTask<RowPage>(new RowPage(rows, _rows.Count));
    }

    /// <summary>Reads the whole file (quoted fields with doubled quotes and embedded delimiters/newlines are understood).</summary>
    public static DelimitedRows Read(string path, char delimiter)
    {
        var records = Parse(File.ReadAllText(path), delimiter).ToList();
        string name = Path.GetFileName(path);
        if (records.Count == 0) return new DelimitedRows(name, [], []);

        return new DelimitedRows(name, records[0], records.Skip(1).ToList());
    }

    private static IEnumerable<string[]> Parse(string text, char delimiter)
    {
        var  fields = new List<string>();
        var  field  = new StringBuilder();
        bool quoted = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"' && field.Length == 0)
            {
                quoted = true;
            }
            else if (c == delimiter)
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                fields.Add(field.ToString());
                field.Clear();
                if (fields.Count > 1 || fields[0].Length > 0) yield return fields.ToArray();
                fields.Clear();
            }
            else
            {
                field.Append(c);
            }
        }

        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            yield return fields.ToArray();
        }
    }

    private sealed class Row(string key, IReadOnlyList<string> columns, string[] cells) : IRowItem
    {
        public string  Key   { get; } = key;
        public string  Text  { get; } = string.Join(" | ", cells);
        public object? Value => null;

        public bool TryGetCell(string column, out object? value)
        {
            for (int c = 0; c < columns.Count; c++)
                if (string.Equals(columns[c], column, StringComparison.OrdinalIgnoreCase))
                {
                    value = c < cells.Length ? cells[c] : "";
                    return true;
                }

            value = null;
            return false;
        }
    }
}
