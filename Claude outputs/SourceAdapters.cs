using System.Collections;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using Newtonsoft.Json.Linq;
using Silo.Providers.Data.Source;
using Telefrag.DI;

namespace Silo.Providers.Diagramming;

/// <summary>
///     Turns resolved objects into <see cref="ITreeSource" />s and <see cref="IRowSource" />s: the object itself when it
///     implements one, else the registered <see cref="IDiagramSourceAdapter" />s by priority (the built-in
///     <see cref="ConnectorModelSourceAdapter" /> last).
/// </summary>
public static class DiagramSources
{
    private static readonly ILogger Log = Serilog.Log.ForContext(typeof(DiagramSources));

    /// <summary>The object as a tree, or null when nothing knows how to show it as one.</summary>
    public static ITreeSource? AsTree(object? value)
    {
        return value switch
        {
            null           => null,
            ITreeSource ts => ts,
            _              => Ask(a => a.AsTree(value))
        };
    }

    /// <summary>The object as rows, or null when nothing knows how to show it as rows.</summary>
    public static IRowSource? AsRows(object? value)
    {
        return value switch
        {
            null          => null,
            IRowSource rs => rs,
            _             => Ask(a => a.AsRows(value))
        };
    }

    private static T? Ask<T>(Func<IDiagramSourceAdapter, T?> ask) where T : class
    {
        List<IDiagramSourceAdapter> adapters;
        try
        {
            adapters = App.ResolveAll<IDiagramSourceAdapter>();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not resolve the diagram source adapters; using the built-in one");
            adapters = [];
        }

        if (!adapters.OfType<ConnectorModelSourceAdapter>().Any()) adapters.Add(ConnectorModelSourceAdapter.Instance);

        foreach (var adapter in adapters.OrderByDescending(a => a.Priority))
            try
            {
                if (ask(adapter) is { } found) return found;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Diagram source adapter {adapter} failed", adapter.GetType().Name);
            }

        return null;
    }
}

/// <summary>
///     The built-in heuristics for objects that are not Silo-aware: directories, JSON (and documents that read as JSON:
///     <see cref="IStructuredDataSource" />), <see cref="IListDataSource" />, <see cref="ITabularDataSource" />,
///     <see cref="IFlatDataProvider" />, <see cref="IHierarchicalDataProvider" /> and, last, any enumerable.
/// </summary>
[Singleton(typeof(IDiagramSourceAdapter))]
public sealed class ConnectorModelSourceAdapter : IDiagramSourceAdapter
{
    internal static readonly ConnectorModelSourceAdapter Instance = new();

    public int Priority => int.MinValue;

    // ---- trees -------------------------------------------------------------------------------

    public ITreeSource? AsTree(object value)
    {
        var children = ChildrenOf(value);
        return children is null ? null : new DelegateTreeSource(children);
    }

    /// <summary>A reader of the object's children as tree items, or null when it has none we know of.</summary>
    private static Func<CancellationToken, ValueTask<IReadOnlyList<ITreeItem>>>? ChildrenOf(object value)
    {
        switch (value)
        {
            case ITreeSource ts:
                return ts.GetRootsAsync;

            case ITreeItem item:
                return item.GetChildrenAsync;

            case DirectoryInfo dir:
                return _ => new ValueTask<IReadOnlyList<ITreeItem>>(
                           dir.EnumerateFileSystemInfos()
                              .OrderBy(f => f is FileInfo)
                              .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                              .Select(f => Item(f.Name, f.Name, f, f is DirectoryInfo ? null : false))
                              .ToList());

            case IStructuredDataSource structured:
                return structured.GetData() is { } data ? ChildrenOf(data) : null;

            case JObject obj:
                return _ => Ready(obj.Properties().Select(p => Item(p.Name, JsonText(p.Name, p.Value), p.Value, p.Value is JContainer { HasValues: true })));

            case JArray array:
                return _ => Ready(array.Select((v, i) => Item(i.ToString(CultureInfo.InvariantCulture), JsonText(null, v), v, v is JContainer { HasValues: true })));

            case IListDataSource list:
                return _ => Ready(ListItems(list.Items, ""));

            case IListItem listItem:
                return _ => Ready(ListItems(listItem.Children, ""));

            case ITabularDataSource table:
                return _ => Ready(RowsOf(table).Select(r => Item(r.Key, r.Text, r, false)));

            case IHierarchicalDataProvider tree:
                return _ => Ready(tree.GetChildren().Select(c => Item(c.ProviderName, c.DisplayName ?? c.ProviderName, c, null))
                                      .Concat(tree.GetDataSources().Select(s => Item(s.CanonicalName, s.CanonicalName, s, null))));

            case IFlatDataProvider flat:
                return _ => Ready(flat.GetDataSources().Select(s => Item(s.CanonicalName, s.CanonicalName, s, null)));

            case string:
            case JValue:
                return null;

            case IDictionary dictionary:
                return _ => Ready(dictionary.Cast<DictionaryEntry>()
                                            .Select(e => Item(Convert.ToString(e.Key, CultureInfo.InvariantCulture) ?? "", ObjectPath.Display(e.Value), e.Value, null)));

            case IEnumerable sequence:
                return _ => Ready(sequence.Cast<object?>().Select((v, i) => Item(i.ToString(CultureInfo.InvariantCulture), ObjectPath.Display(v), v, null)));
        }

        return null;
    }

    private static IEnumerable<ITreeItem> ListItems(IReadOnlyList<IListItem> items, string prefix)
    {
        return items.Select((li, i) => Item(prefix + i.ToString(CultureInfo.InvariantCulture), li.Text, li, li.Children.Count > 0));
    }

    private static string JsonText(string? name, JToken value)
    {
        string shown = value switch
        {
            JValue v  => Convert.ToString(v.Value, CultureInfo.InvariantCulture) ?? "null",
            JArray a  => $"[{a.Count}]",
            JObject o => o.Count == 0 ? "{}" : "",
            _         => value.ToString()
        };

        if (name is null) return shown.Length > 0 ? shown : "{…}";
        return shown.Length > 0 ? $"{name}: {shown}" : name;
    }

    private static ITreeItem Item(string key, string text, object? value, bool? hasChildren)
    {
        return new DelegateTreeItem(key, text, value, hasChildren);
    }

    private static ValueTask<IReadOnlyList<ITreeItem>> Ready(IEnumerable<ITreeItem> items)
    {
        return new ValueTask<IReadOnlyList<ITreeItem>>(items.ToList());
    }

    /// <summary>A tree source over a reader of its top items.</summary>
    private sealed class DelegateTreeSource(Func<CancellationToken, ValueTask<IReadOnlyList<ITreeItem>>> roots) : ITreeSource
    {
        public ValueTask<IReadOnlyList<ITreeItem>> GetRootsAsync(CancellationToken cancel)
        {
            return roots(cancel);
        }
    }

    /// <summary>A tree item whose children are whatever the heuristics make of its value.</summary>
    private sealed class DelegateTreeItem(string key, string text, object? value, bool? hasChildren) : ITreeItem
    {
        public string  Key   { get; } = key;
        public string  Text  { get; } = text;
        public object? Value { get; } = value;

        public bool? HasChildren { get; } = hasChildren;

        public ValueTask<IReadOnlyList<ITreeItem>> GetChildrenAsync(CancellationToken cancel)
        {
            if (HasChildren == false || Value is null || ChildrenOf(Value) is not { } read)
                return new ValueTask<IReadOnlyList<ITreeItem>>(Array.Empty<ITreeItem>());

            return read(cancel);
        }

        public override string ToString()
        {
            return Text;
        }
    }

    // ---- rows --------------------------------------------------------------------------------

    public IRowSource? AsRows(object value)
    {
        switch (value)
        {
            case ITabularDataSource table:
                return new ListRowSource(table.ColumnNames, () => RowsOf(table));

            case IListDataSource list:
                return new ListRowSource(["Text"], () => list.Items.Select((li, i) => (IRowItem)new MapRow(i.ToString(CultureInfo.InvariantCulture), li.Text, li,
                                                                                                         new Dictionary<string, object?> { ["Text"] = li.Text })));

            case IStructuredDataSource structured:
                return structured.GetData() is { } data ? AsRows(data) : null;

            case JArray array:
                return JsonRows(array);

            case JObject obj:
                return new ListRowSource(["Name", "Value"],
                                         () => obj.Properties().Select(p => (IRowItem)new MapRow(p.Name, JsonText(p.Name, p.Value), p.Value,
                                                                                                 new Dictionary<string, object?> { ["Name"] = p.Name, ["Value"] = Scalar(p.Value) })));

            case DirectoryInfo dir:
                return new ListRowSource(["Name", "Kind", "Size", "Modified"],
                                         () => dir.EnumerateFileSystemInfos()
                                                  .OrderBy(f => f is FileInfo)
                                                  .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                                                  .Select(f => (IRowItem)new MapRow(f.Name, f.Name, f, new Dictionary<string, object?>
                                                  {
                                                      ["Name"]     = f.Name,
                                                      ["Kind"]     = f is DirectoryInfo ? "Folder" : f.Extension.TrimStart('.').ToUpperInvariant(),
                                                      ["Size"]     = f is FileInfo fi ? fi.Length : null,
                                                      ["Modified"] = f.LastWriteTime
                                                  })));

            case IFlatDataProvider flat:
            {
                var sources = flat.GetDataSources().ToList();
                var first   = (IDataSource?)sources.OfType<ITabularDataSource>().FirstOrDefault() ?? sources.OfType<IListDataSource>().FirstOrDefault();
                return first is null ? null : AsRows(first);
            }

            case string:
            case JValue:
                return null;

            case IDictionary dictionary:
                return new ListRowSource(["Name", "Value"],
                                         () => dictionary.Cast<DictionaryEntry>()
                                                         .Select(e =>
                                                                 {
                                                                     string k = Convert.ToString(e.Key, CultureInfo.InvariantCulture) ?? "";
                                                                     return (IRowItem)new MapRow(k, $"{k}: {ObjectPath.Display(e.Value)}", e.Value,
                                                                                                 new Dictionary<string, object?> { ["Name"] = k, ["Value"] = e.Value });
                                                                 }));

            case IEnumerable sequence:
                return ObjectRows(sequence);
        }

        return null;
    }

    private static IEnumerable<IRowItem> RowsOf(ITabularDataSource table)
    {
        var columns = table.ColumnNames;
        return table.GetRows().Select((cells, i) =>
                                      {
                                          var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                                          for (int c = 0; c < columns.Count && c < cells.Count; c++) map[columns[c]] = cells[c];
                                          return (IRowItem)new MapRow(i.ToString(CultureInfo.InvariantCulture), string.Join(" | ", cells), null, map);
                                      });
    }

    private static IRowSource JsonRows(JArray array)
    {
        bool objects = array.Count > 0 && array.All(t => t is JObject);
        if (!objects)
            return new ListRowSource(["Value"], () => array.Select((t, i) => (IRowItem)new MapRow(i.ToString(CultureInfo.InvariantCulture), JsonText(null, t), t,
                                                                                                new Dictionary<string, object?> { ["Value"] = Scalar(t) })));

        // the columns: every property name of the first rows, in the order first seen
        var columns = new List<string>();
        foreach (var o in array.Take(50).OfType<JObject>())
        foreach (var p in o.Properties())
            if (!columns.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
                columns.Add(p.Name);

        return new ListRowSource(columns,
                                 () => array.OfType<JObject>().Select((o, i) =>
                                                                      {
                                                                          var map = o.Properties().ToDictionary(p => p.Name, p => Scalar(p.Value), StringComparer.OrdinalIgnoreCase);
                                                                          string key = (o["id"] ?? o["Id"] ?? o["key"] ?? o["name"])?.ToString() ?? i.ToString(CultureInfo.InvariantCulture);
                                                                          string text = columns.Count > 0 && map.TryGetValue(columns[0], out var first) ? ObjectPath.Display(first) : key;
                                                                          return (IRowItem)new MapRow(key, text, o, map);
                                                                      }));
    }

    private static IRowSource ObjectRows(IEnumerable sequence)
    {
        var items       = sequence.Cast<object?>().ToList();
        var elementType = items.FirstOrDefault(i => i is not null)?.GetType();

        if (elementType is null || IsScalar(elementType))
            return new ListRowSource(["Value"], () => items.Select((v, i) => (IRowItem)new MapRow(i.ToString(CultureInfo.InvariantCulture), ObjectPath.Display(v), v,
                                                                                                 new Dictionary<string, object?> { ["Value"] = v })));

        var properties = elementType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                    .Where(p => p.GetIndexParameters().Length == 0 && IsScalar(Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType))
                                    .ToList();

        return new ListRowSource(properties.Select(p => p.Name).ToList(),
                                 () => items.Select((v, i) =>
                                                    {
                                                        var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                                                        if (v is not null)
                                                            foreach (var p in properties)
                                                                try
                                                                {
                                                                    map[p.Name] = p.DeclaringType!.IsInstanceOfType(v) ? p.GetValue(v) : null;
                                                                }
                                                                catch (Exception)
                                                                {
                                                                    map[p.Name] = null;
                                                                }

                                                        return (IRowItem)new MapRow(i.ToString(CultureInfo.InvariantCulture), ObjectPath.Display(v), v, map);
                                                    }));
    }

    private static bool IsScalar(Type t)
    {
        return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t == typeof(DateTime) || t == typeof(DateTimeOffset) ||
               t == typeof(TimeSpan) || t == typeof(Guid) || t == typeof(Uri);
    }

    private static object? Scalar(JToken t)
    {
        return t is JValue v ? v.Value : JsonText(null, t);
    }

    /// <summary>Rows read from a factory every time (so a refresh sees what is there then).</summary>
    private sealed class ListRowSource(IReadOnlyList<string> columns, Func<IEnumerable<IRowItem>> rows) : IRowSource
    {
        public IReadOnlyList<string> Columns { get; } = columns;

        public ValueTask<RowPage> GetRowsAsync(int limit, CancellationToken cancel)
        {
            var all = rows().Take(limit + 1).ToList();
            if (all.Count <= limit) return new ValueTask<RowPage>(new RowPage(all, all.Count));

            // more than asked for: count the rest only when that is cheap enough to be worth it
            int? total = null;
            try
            {
                total = rows().Take(100_000).Count();
            }
            catch (Exception)
            {
                // leave the total unknown
            }

            return new ValueTask<RowPage>(new RowPage(all.Take(limit).ToList(), total));
        }
    }

    /// <summary>A row over a column map.</summary>
    private sealed class MapRow(string key, string text, object? value, IReadOnlyDictionary<string, object?> cells) : IRowItem
    {
        public string  Key   { get; } = key;
        public string  Text  { get; } = text;
        public object? Value { get; } = value;

        public bool TryGetCell(string column, out object? cell)
        {
            if (cells.TryGetValue(column, out cell)) return true;
            foreach (var (k, v) in cells)
                if (string.Equals(k, column, StringComparison.OrdinalIgnoreCase))
                {
                    cell = v;
                    return true;
                }

            return false;
        }

        public override string ToString()
        {
            return Text;
        }
    }
}
