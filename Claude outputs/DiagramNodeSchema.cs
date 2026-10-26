using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Silo.Connectors;

namespace Silo.Providers.Diagramming;

/// <summary>
///     The cell attributes data-bound nodes use, and reading a node's settings (<see cref="DynamicNode" /> subclasses) from
///     a cell's attributes by their <see cref="NodeAttributeAttribute" /> names.
/// </summary>
public static class DiagramNodeSchema
{
    /// <summary>On the node's own cell: its kind (<c>status</c>, <c>table</c>, <c>list</c>, <c>tree</c>).</summary>
    public const string KindAttribute = "silo-node";

    /// <summary>On every cell a node made (rows, tree items, connectors): the id of the node that owns it.</summary>
    public const string BoundOfAttribute = "silo-bound-of";

    /// <summary>On a cell a node made: the key of the item it shows (tree items: the path of keys from the node, '/'-separated).</summary>
    public const string KeyAttribute = "silo-key";

    /// <summary>On the template a node copies for each item (a table row, a list line, a tree item): <c>1</c>.</summary>
    public const string PrototypeAttribute = "silo-prototype";

    /// <summary>On a table's header row: <c>1</c> (its cells are named after the columns when columns are automatic).</summary>
    public const string HeaderAttribute = "silo-header";

    /// <summary>On a table header or prototype cell: the column it shows.</summary>
    public const string ColumnAttribute = "silo-column";

    /// <summary>On the "… N more" cell a node adds when it left items out, and on a lazy tree item's placeholder child.</summary>
    public const string MoreAttribute = "silo-more";

    /// <summary>On the node's own cell: why it could not read its source last time (removed once it can).</summary>
    public const string ErrorAttribute = "silo-error";

    /// <summary>On the node's own cell: when it last read its source (ISO 8601, UTC).</summary>
    public const string ReadAtAttribute = "silo-read-at";

    /// <summary>Attributes that belong to the node machinery and are never copied from a prototype to its copies.</summary>
    public static readonly IReadOnlySet<string> StructuralAttributes = new HashSet<string>(StringComparer.Ordinal)
    {
        KindAttribute, BoundOfAttribute, KeyAttribute, PrototypeAttribute, HeaderAttribute, MoreAttribute, ErrorAttribute,
        ReadAtAttribute, "label", "tooltip", "link", "placeholders", "id"
    };

    /// <summary>The settings properties of <paramref name="settingsType" /> with their attribute names.</summary>
    public static IEnumerable<(PropertyInfo Property, string Attribute)> Settings(Type settingsType)
    {
        return settingsType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                           .Select(p => (p, p.GetCustomAttribute<NodeAttributeAttribute>()?.Name))
                           .Where(x => x.Name is not null)
                           .Select(x => (x.p, x.Name!));
    }

    /// <summary>Reads a node's settings from its cell's attributes; missing or unreadable values keep their defaults.</summary>
    public static T Read<T>(IReadOnlyDictionary<string, string> attributes) where T : DynamicNode, new()
    {
        return (T)Read(typeof(T), attributes);
    }

    /// <summary>Reads a node's settings of type <paramref name="settingsType" /> from its cell's attributes.</summary>
    public static DynamicNode Read(Type settingsType, IReadOnlyDictionary<string, string> attributes)
    {
        var node = (DynamicNode)Activator.CreateInstance(settingsType)!;
        foreach (var (property, name) in Settings(settingsType))
        {
            if (!attributes.TryGetValue(name, out string? text) || string.IsNullOrWhiteSpace(text) || !property.CanWrite) continue;
            if (TryConvert(text.Trim(), property.PropertyType, out object? value)) property.SetValue(node, value);
        }

        return node;
    }

    /// <summary>The attributes a new node of this kind starts with: its kind and every setting with a default value.</summary>
    public static Dictionary<string, string> Defaults(Type settingsType, string kind)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal) { [KindAttribute] = kind };
        var node       = (DynamicNode)Activator.CreateInstance(settingsType)!;
        foreach (var (property, name) in Settings(settingsType))
            attributes[name] = property.GetValue(node) switch
            {
                null           => "",
                bool b         => b ? "1" : "0",
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                var v          => v.ToString() ?? ""
            };

        return attributes;
    }

    private static bool TryConvert(string text, Type type, out object? value)
    {
        value = null;
        type  = Nullable.GetUnderlyingType(type) ?? type;

        try
        {
            if (type == typeof(Uri))
            {
                value = UriRules.Parse(text);
                return value is not null;
            }

            if (type == typeof(bool))
            {
                value = text is "1" or "true" or "True" or "yes" or "on";
                return true;
            }

            if (type.IsEnum)
            {
                bool ok = Enum.TryParse(type, text, true, out object? e);
                value = e;
                return ok;
            }

            if (type == typeof(string))
            {
                value = text;
                return true;
            }

            var converter = TypeDescriptor.GetConverter(type);
            value = converter.ConvertFromInvariantString(text);
            return value is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
