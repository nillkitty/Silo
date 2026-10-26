using System.Linq;
using System.Threading;
using Markdig;
using Silo.Drawio.Live;
using Silo.Providers.Data;
using Silo.Providers.Format;

namespace Silo.Providers.Diagramming;

/// <summary>
///     Resolves a bound diagram node's URI to the object it binds to. In order: a live value scheme registered with
///     <see cref="DiagramData" /> (<c>silo:</c>, <c>bind:</c>, <c>push:</c>, ...), the registered <see cref="IUriResolver" />s,
///     then the built-in handling of <c>file:</c> (a folder; JSON and XML as structured data; Markdown tables and lists; CSV
///     and TSV as rows; any other file as itself). Then <c>silo-path</c> is followed into the result.
/// </summary>
public static class BoundData
{
    private static readonly Serilog.ILogger Log = Serilog.Log.ForContext(typeof(BoundData));

    /// <summary>Resolves <paramref name="uri" /> (off the UI thread) and follows <paramref name="path" /> into it.</summary>
    /// <exception cref="InvalidOperationException">Nothing resolves the URI, or the path is not there.</exception>
    public static async Task<object> ResolveAsync(Uri uri, string? path, CancellationToken cancel)
    {
        object? value = await Task.Run(() => Resolve(uri), cancel).ConfigureAwait(false);
        if (value is null) throw new InvalidOperationException($"Nothing in Silo can open '{uri.OriginalString}'.");

        if (!string.IsNullOrWhiteSpace(path))
        {
            string p = path;
            value = await Task.Run(() => ObjectPath.TryGet(value, p, out object? found) ? found : null, cancel).ConfigureAwait(false);
            if (value is null) throw new InvalidOperationException($"'{path}' was not found in '{uri.OriginalString}'.");
        }

        return value;
    }

    private static object? Resolve(Uri uri)
    {
        if (uri.IsAbsoluteUri && DiagramData.IsRegistered(uri.Scheme) &&
            DiagramData.TryResolve(uri.Scheme + ":" + uri.OriginalString[(uri.Scheme.Length + 1)..], out object? live))
            return live;

        foreach (var resolver in Resolvers())
            foreach (var context in (UriContext[])[UriContext.Any, UriContext.Private])
                try
                {
                    if (resolver.Resolve(uri, context) is { } found) return found;
                }
                catch (NotImplementedException)
                {
                    break; // a resolver that is not written yet
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "{resolver} failed to resolve {uri}", resolver.GetType().Name, uri);
                    break;
                }

        return uri.IsAbsoluteUri && uri.IsFile ? ResolveFile(uri) : null;
    }

    private static List<IUriResolver> Resolvers()
    {
        try
        {
            return App.ResolveAll<IUriResolver>();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not resolve the URI resolvers");
            return [];
        }
    }

    private static object? ResolveFile(Uri uri)
    {
        string path = uri.LocalPath;
        if (Directory.Exists(path)) return new DirectoryInfo(path);
        if (!File.Exists(path)) throw new FileNotFoundException($"'{path}' does not exist.", path);

        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            // structured documents are read once here (their GetData reads the file on every call) and bound as JSON
            case ".json":
                return new Format.JsonDocument(Formats.All.OfType<JsonFormat>().FirstOrDefault() ?? new JsonFormat(), uri).GetData();

            case ".xml":
                return new Format.XmlDocument(Formats.All.OfType<XmlFormat>().FirstOrDefault() ?? new XmlFormat(), uri).GetData();

            case ".md":
            case ".markdown":
                var pipeline = new MarkdownPipelineBuilder().UsePipeTables().Build();
                return new MarkdownDataProvider(Markdown.Parse(File.ReadAllText(path), pipeline), uri);

            case ".csv":
                return DelimitedRows.Read(path, ',');

            case ".tsv":
            case ".tab":
                return DelimitedRows.Read(path, '\t');

            default:
                return new FileInfo(path);
        }
    }
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
