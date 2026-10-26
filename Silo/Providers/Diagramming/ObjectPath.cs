using System.Collections;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using Silo.Drawio.Live;
using Silo.Providers.Data.Source;

namespace Silo.Providers.Diagramming;

/// <summary>
///     Reads values out of bound objects by path, for scoped binding tokens and <c>silo-path</c>. A path is dotted
///     (<c>Owner.Name</c>) with optional indexers (<c>Items[0]</c>, <c>Tags["x"]</c>); on JSON data it is a JSON path. Each
///     step understands JSON, dictionaries, rows (<see cref="IRowItem" /> columns), tree items, data providers (data
///     sources by name), lists (by index) and, last, public properties (case-insensitive).
/// </summary>
public static class ObjectPath
{
    /// <summary>Follows <paramref name="path" /> from <paramref name="root" />; an empty path is the root itself.</summary>
    public static bool TryGet(object? root, string? path, out object? value)
    {
        value = root;
        if (string.IsNullOrWhiteSpace(path) || path.Trim() == ".") return true;
        if (root is null) return false;
        if (root is IStructuredDataSource structured) root = structured.GetData();
        if (root is null) return false;

        path = path.Trim().TrimStart('.');

        // a row's own column names come first (also over JSON rows, whose names may differ in case)
        if (root is IRowItem row && row.TryGetCell(path, out value)) return true;

        // JSON data: a JSON path does it all (and is what people write for it)
        if (Unwrap(root) is JToken json)
        {
            try
            {
                value = json.SelectToken(path.StartsWith('$') ? path : "$." + path, false);
                return value is not null;
            }
            catch (Exception)
            {
                // not a valid JSON path: fall through to the step-by-step walk
            }
        }

        object? current = root;
        foreach (string step in Steps(path))
        {
            if (!Step(current, step, out current)) return false;
        }

        value = current;
        return true;
    }

    /// <summary>The text an object shows as when it is put into a label as a whole (<c>{.}</c>, <c>{item}</c>).</summary>
    public static string Display(object? value)
    {
        return value switch
        {
            null              => "",
            string s          => s,
            ITreeItem t       => t.Text,
            IRowItem r        => r.Text,
            JValue v          => Convert.ToString(v.Value, CultureInfo.InvariantCulture) ?? "",
            JObject o         => NameOf(o) ?? "{…}",
            JArray a          => $"[{a.Count}]",
            JToken j          => j.ToString(),
            FileSystemInfo fs => fs.Name.Length > 0 ? fs.Name : fs.FullName,
            IDataSource ds    => ds.CanonicalName,
            Silo.Contracts.ConnectorServices.IDisplayName d when d.DisplayName is { } n => n,
            IFormattable f    => f.ToString(null, CultureInfo.InvariantCulture),
            _                 => value.ToString() ?? ""
        };
    }

    /// <summary>What a JSON object is called, by the usual naming properties (<c>name</c>, <c>title</c>, <c>id</c>, <c>key</c>).</summary>
    private static string? NameOf(JObject o)
    {
        foreach (string name in (string[])["name", "title", "displayName", "id", "key"])
            if (o.GetValue(name, StringComparison.OrdinalIgnoreCase) is JValue { Value: { } v })
                return Convert.ToString(v, CultureInfo.InvariantCulture);
        return null;
    }

    /// <summary>A plain value for formatting: JSON scalars become their CLR value, so <c>|fmt:</c> and <c>|range:</c> work on them.</summary>
    public static object? Simplify(object? value)
    {
        return value switch
        {
            JValue v => v.Value,
            _        => value
        };
    }

    private static object? Unwrap(object? value)
    {
        return value switch
        {
            ITreeItem { Value: { } v } => v,
            IRowItem { Value: { } v }  => v,
            _                          => value
        };
    }

    /// <summary>Splits <c>a.b[0]["c d"]</c> into <c>a</c>, <c>b</c>, <c>[0]</c>, <c>["c d"]</c>.</summary>
    private static IEnumerable<string> Steps(string path)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < path.Length; i++)
        {
            char c = path[i];
            if (c == '.')
            {
                if (sb.Length > 0) yield return sb.ToString();
                sb.Clear();
            }
            else if (c == '[')
            {
                if (sb.Length > 0) yield return sb.ToString();
                sb.Clear();
                int end = path.IndexOf(']', i);
                if (end < 0) end = path.Length - 1;
                yield return path[i..(end + 1)];
                i = end;
            }
            else
            {
                sb.Append(c);
            }
        }

        if (sb.Length > 0) yield return sb.ToString();
    }

    private static bool Step(object? from, string step, out object? value)
    {
        value = null;
        if (from is null) return false;

        bool    indexed = step.StartsWith('[');
        string  name    = indexed ? step.Trim('[', ']').Trim().Trim('"', '\'') : step;
        int?    index   = indexed && int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : null;

        switch (from)
        {
            case IRowItem row:
                if (row.TryGetCell(name, out value)) return true;
                if (row.Value is { } rv) return Step(rv, step, out value);
                break;

            case ITreeItem item:
                if (item.Value is { } iv && Step(iv, step, out value)) return true;
                break;

            case JToken json:
                value = index is { } ji && json is JArray ja ? ji < ja.Count ? ja[ji] : null : json[name];
                return value is not null;

            case IDictionary dictionary:
                if (dictionary.Contains(name))
                {
                    value = dictionary[name];
                    return true;
                }

                foreach (DictionaryEntry e in dictionary)
                    if (string.Equals(Convert.ToString(e.Key, CultureInfo.InvariantCulture), name, StringComparison.OrdinalIgnoreCase))
                    {
                        value = e.Value;
                        return true;
                    }

                return false;

            case IFlatDataProvider flat when !indexed:
                value = flat.GetDataSources().FirstOrDefault(s => string.Equals(s.CanonicalName, name, StringComparison.OrdinalIgnoreCase));
                if (value is not null) return true;
                break;

            case IHierarchicalDataProvider tree when !indexed:
                value = tree.GetDataSources().FirstOrDefault(s => string.Equals(s.CanonicalName, name, StringComparison.OrdinalIgnoreCase)) ??
                        (object?)tree.GetChildren().FirstOrDefault(c => string.Equals(c.ProviderName, name, StringComparison.OrdinalIgnoreCase));
                if (value is not null) return true;
                break;

            case DirectoryInfo dir when !indexed:
                var child = dir.EnumerateFileSystemInfos(name).FirstOrDefault();
                if (child is not null)
                {
                    value = child;
                    return true;
                }

                break;
        }

        if (index is { } i)
        {
            if (from is IList list) return (value = i >= 0 && i < list.Count ? list[i] : null) is not null;
            if (from is IEnumerable seq and not string) return (value = seq.Cast<object?>().Skip(i).FirstOrDefault()) is not null;
            return false;
        }

        var property = from.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (property is null || property.GetIndexParameters().Length > 0) return false;

        try
        {
            value = property.GetValue(from);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>
///     What a data-bound cell's scoped tokens read: the object in scope (the node's object, or a child's item) and the
///     owning node's object.
/// </summary>
public sealed class BindingScope(object? current, object? node) : IBindingScope
{
    /// <summary>The object <c>{.Path}</c>, <c>{item:Path}</c> and <c>{row:Column}</c> read.</summary>
    public object? Current { get; } = current;

    /// <summary>The object <c>{node:Path}</c> reads.</summary>
    public object? Node { get; } = node;

    public bool TryGet(string prefix, string path, out object? value)
    {
        object? root = prefix == "node" ? Node : Current;
        if (!ObjectPath.TryGet(root, path, out value)) return false;

        // a whole object goes into the label as its display text; scalars stay typed for the formatters
        value = string.IsNullOrWhiteSpace(path) || value is ITreeItem or IRowItem or FileSystemInfo or JContainer
                    ? ObjectPath.Display(value)
                    : ObjectPath.Simplify(value);
        return true;
    }
}
