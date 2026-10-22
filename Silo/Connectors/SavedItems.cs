using Serilog;
using Silo.Connectors.Virtual;
using Silo.Design;
using Silo.Providers;
using Silo.Providers.Data;
using Silo.Providers.Data.Shape;
using Silo.Unicode;
using Silo.ViewModel;

namespace Silo.Connectors;

/// <summary>
///     One thing the Silo keeps in its <c>Stored</c> table (a constant, a shortcut key, a saved character or string...),
///     with
///     what the Explorer needs to file it: the type it was written from, if that type is loaded, and the
///     <see cref="DataShape" /> of that type. It is also the <see cref="SiloNode.DataContext" /> of its node, so it
///     describes itself for the Properties pane.
/// </summary>
public sealed class SavedItem : INodeDetails, IRealmed
{
    /// <summary>The longest stretch of a value the Properties pane shows.</summary>
    private const int PreviewLength = 400;

    /// <summary>The type the value was written from; null when it is not loaded (the stored name is only information).</summary>
    public Type? ClrType { get; }

    public StoredRecord Record { get; }

    public DataShape Shape { get; }

    /// <summary>The type's readable name: what the type folder is called.</summary>
    public string TypeLabel { get; }

    public Uri? Icon => new(Shape == DataShape.Scalar ? "Record" : "Data1", UriKind.Relative);

    public string Name => Record.Key;

    public string TypeName => TypeLabel;

    public Uri Uri { get; }

    /// <summary>Only the permanent tier is listed, which is kept in the Silo's own (global) database.</summary>
    public Realm Realm => Realm.Global;

    public SavedItem(StoredRecord record, Type? clrType, Uri uri)
    {
        Record    = record;
        ClrType   = clrType;
        Shape     = SavedItems.ShapeOf(clrType);
        Uri       = uri;
        TypeLabel = clrType is null ? SavedItems.UnresolvedLabel(record.TypeName) : SavedItems.FriendlyName(clrType);
    }

    public IEnumerable<(string Name, string Value)> GetDetails()
    {
        yield return ("Key", Record.Key);
        yield return ("Type", ClrType?.FullName ?? Record.TypeName);
        yield return ("Shape", Shape.ToString());
        yield return ("Format", Record.Format);
        yield return ("Kept in", Realms.Describe(Realm));
        yield return ("Scope", Record.Scope == StoredScope.Personal ? "Yours" : "The Silo's");
        yield return ("Created", Record.Created.ToLocalTime().ToString("g"));
        yield return ("Modified", Record.Modified.ToLocalTime().ToString("g"));

        string data = Record.Data ?? string.Empty;
        yield return ("Value", data.Length <= PreviewLength ? data : data[..PreviewLength] + $"... ({data.Length} characters)");
    }
}

/// <summary>
///     Reads what the Silo has stored, and sorts it for the Explorer: by <see cref="DataShape" />, then type.
/// </summary>
public static class SavedItems
{
    /// <summary>Keys the app keeps for its own use (window and tool state): shown only to a developer.</summary>
    private static readonly string[] InternalPrefixes = ["unicodeexplorer:"];

    /// <summary>The shapes shown under Saved Objects, in the order they are listed.</summary>
    public static readonly DataShape[] ObjectShapes =
    [
        DataShape.Linear, DataShape.Columnar, DataShape.Tabular, DataShape.Multitabular,
        DataShape.Object, DataShape.Container, DataShape.Dynamic, DataShape.Unknown
    ];

    /// <summary>
    ///     Everything stored in the Silo's own (permanent) tier, yours and the Silo's. A <see cref="StoredTier.Session" />
    ///     item is only remembered "where I was" and is not listed.
    /// </summary>
    public static IReadOnlyList<SavedItem> Load(IStoredStore? store, bool showInternal)
    {
        if (store is null) return [];

        var items = new List<SavedItem>();
        foreach (var scope in new[] { StoredScope.Personal, StoredScope.Shared })
        {
            IReadOnlyList<StoredRecord> records;
            try
            {
                records = store.ListStored(null, scope);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Listing the {Scope} stored items failed", scope);
                continue;
            }

            foreach (var record in records)
            {
                if (!showInternal && InternalPrefixes.Any(p => record.Key.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var type = ResolveType(record.TypeName);
                var uri  = (UriConstants.LocalSiloUri ?? new Uri("silo:///active/")).Append("saved", scope.ToString(), record.Key);
                items.Add(new SavedItem(record, type, uri));
            }
        }

        return items;
    }

    /// <summary>
    ///     The type a stored name stands for, only if it is already loaded: a stored name never makes the program load
    ///     an assembly or create a type. Null when not found.
    /// </summary>
    public static Type? ResolveType(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName)) return null;

        try
        {
            return Type.GetType(typeName,
                                name => AppDomain.CurrentDomain.GetAssemblies()
                                                 .FirstOrDefault(a => string.Equals(a.GetName().Name, name.Name,
                                                                                    StringComparison.OrdinalIgnoreCase)),
                                (assembly, name, ignoreCase) => (assembly ?? typeof(object).Assembly).GetType(name, false, ignoreCase),
                                false);
        }
        catch (Exception)
        {
            // a malformed name, a type that will not load: not found
            return null;
        }
    }

    /// <summary>
    ///     How the Explorer classifies a stored type: what <see cref="RuntimeType" /> says, except that a Unicode
    ///     <see cref="CodePoint" /> (a character) and a run of them (a string) are single values to the person, so they are
    ///     scalars. Unknown when the type is not loaded.
    /// </summary>
    public static DataShape ShapeOf(Type? type)
    {
        if (type is null) return DataShape.Unknown;

        if (type == typeof(CodePoint) || type == typeof(CodePoint[]) || type == typeof(string))
            return DataShape.Scalar;

        try
        {
            return new RuntimeType(type).Shape;
        }
        catch (Exception)
        {
            return DataShape.Unknown;
        }
    }

    /// <summary><c>List&lt;String&gt;</c> rather than <c>List`1</c>.</summary>
    public static string FriendlyName(Type type)
    {
        if (type.IsArray) return FriendlyName(type.GetElementType()!) + "[]";
        if (!type.IsGenericType) return type.Name;

        string name         = type.Name;
        int    tick         = name.IndexOf('`');
        if (tick >= 0) name = name[..tick];
        return name + "<" + string.Join(", ", type.GetGenericArguments().Select(FriendlyName)) + ">";
    }

    /// <summary>The name for a stored type that is not loaded: its simple name, marked as not found.</summary>
    public static string UnresolvedLabel(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName)) return "(unknown type)";

        // the part before the first comma that is not inside [ ]
        int depth = 0, end = typeName.Length;
        for (int i = 0; i < typeName.Length; i++)
        {
            char c = typeName[i];
            if (c == '[')
            {
                depth++;
            }
            else if (c == ']')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                end = i;
                break;
            }
        }

        string full = typeName[..end].Trim();
        int    dot  = full.LastIndexOf('.');
        return (dot >= 0 ? full[(dot + 1)..] : full) + " (not loaded)";
    }
}