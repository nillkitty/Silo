using System.Globalization;
using Silo.DbModel.Meta;
using Silo.Providers.Data;
using Silo.Providers.Definitions;

namespace Silo.Labs.Schemas;

public enum PropType
{
    Text,
    Lines,
    Bool,
    Choice,
    Flags,
    Number,

    /// <summary>The name of another construct in the draft (or in the Silo) of <see cref="SchemaProp.RefKind" />.</summary>
    Ref
}

/// <summary>One property a kind of construct has, which is what the property editor and the terminal are made from.</summary>
public sealed record SchemaProp(
    string                  Key,
    string                  Title,
    PropType                Type,
    string                  Hint     = "",
    IReadOnlyList<string>?  Choices  = null,
    ConstructKind?          RefKind  = null,
    string                  Fallback = "");

/// <summary>The properties of each kind, and the data types an attribute may have.</summary>
public static class SchemaProps
{
    /// <summary>The .NET types an attribute can be (the others are <c>enum:Name</c>).</summary>
    public static IReadOnlyList<string> ClrTypes { get; } =
        ["String", "Int32", "Int64", "Boolean", "Double", "Decimal", "DateTime", "DateTimeOffset", "TimeSpan", "Guid", "Uri", "Byte[]"];

    public static IReadOnlyList<string> AttributeFlags { get; } = Enum.GetNames<MvAttributeFlags>().Where(n => n != nameof(MvAttributeFlags.None)).ToList();

    public static IReadOnlyList<string> EnumFlagNames { get; } = Enum.GetNames<EnumFlags>().Where(n => n != nameof(EnumFlags.None)).ToList();

    private static readonly IReadOnlyList<SchemaProp> ClassProps =
    [
        new("displayName", "Display name", PropType.Text, "Human-readable, renamable; the name stays the key"),
        new("auxiliary", "Auxiliary", PropType.Bool, "Supplies extra attributes to other classes that include it"),
        new("description", "Description", PropType.Lines, "What the class is for (kept in the draft only: the Silo does not store it yet)")
    ];

    private static readonly IReadOnlyList<SchemaProp> AttributeProps =
    [
        new("displayName", "Display name", PropType.Text),
        new("type", "Type", PropType.Choice, ".NET type, or enum:Name", Fallback: "String"),
        new("syntax", "Syntax", PropType.Ref, "Rules a text value has to follow", RefKind: ConstructKind.Syntax),
        new("default", "Default value", PropType.Text),
        new("combinator", "Combinator", PropType.Text, "Type name that combines values of a multivalued attribute"),
        new("flags", "Flags", PropType.Flags, Choices: AttributeFlags),
        new("description", "Description", PropType.Lines, "Kept in the draft only")
    ];

    private static readonly IReadOnlyList<SchemaProp> EnumProps =
    [
        new("type", "Values are", PropType.Choice, Choices: Enum.GetNames<EnumType>(), Fallback: nameof(EnumType.Integral)),
        new("flags", "Flags", PropType.Flags, Choices: EnumFlagNames),
        new("description", "Description", PropType.Lines, "What the enum is for")
    ];

    private static readonly IReadOnlyList<SchemaProp> SyntaxProps =
    [
        new("kind", "Decided by", PropType.Choice, Choices: Enum.GetNames<SyntaxKind>(), Fallback: nameof(SyntaxKind.Pattern)),
        new("pattern", "Pattern", PropType.Text, "The regular expression (when decided by a pattern)"),
        new("parser", "Parser", PropType.Text, "The built-in parser's key (when decided by a parser)"),
        new("alias", "Alias", PropType.Text, "An optional second name"),
        new("example", "Example", PropType.Text, "A valid value; the check holds it against the syntax"),
        new("description", "Description", PropType.Lines)
    ];

    private static readonly IReadOnlyList<SchemaProp> RelationProps =
    [
        new("subject", "Subject", PropType.Ref, "The class that inherits or includes", RefKind: ConstructKind.Class),
        new("relation", "Relation", PropType.Choice, "Inheritance, or including an auxiliary class", Enum.GetNames<TypeRelation>(), Fallback: nameof(TypeRelation.Inheritance)),
        new("target", "Target", PropType.Ref, "The base class, or the auxiliary class", RefKind: ConstructKind.Class)
    ];

    private static readonly IReadOnlyList<SchemaProp> UnitProps =
    [
        new("type", "Type", PropType.Choice, Choices: Enum.GetNames<UnitType>(), Fallback: nameof(UnitType.Unit)),
        new("namePlural", "Plural", PropType.Text, "byte | bytes"),
        new("target", "Target", PropType.Ref, "What an alias, prefix, suffix or conversion refers to", RefKind: ConstructKind.Unit),
        new("relation", "Relation", PropType.Ref, "A chain of scales (instead of a target)", RefKind: ConstructKind.UnitRelation),
        new("formula", "Formula", PropType.Text, "For conversions: how the value is computed"),
        new("scale", "Scale", PropType.Number),
        new("reciprocal", "Reciprocal", PropType.Bool),
        new("formatter", "Formatter", PropType.Text, "How a value of the unit is written"),
        new("tags", "Tags", PropType.Text, "#si #bin ...")
    ];

    private static readonly IReadOnlyList<SchemaProp> UnitRelationProps =
    [
        new("unit", "Unit", PropType.Ref, RefKind: ConstructKind.Unit),
        new("next", "Next", PropType.Ref, "The next step in the chain", RefKind: ConstructKind.UnitRelation),
        new("scale", "Scale", PropType.Number),
        new("reciprocal", "Reciprocal", PropType.Bool)
    ];

    public static IReadOnlyList<SchemaProp> For(ConstructKind kind)
    {
        return kind switch
               {
                   ConstructKind.Class        => ClassProps,
                   ConstructKind.Attribute    => AttributeProps,
                   ConstructKind.Enum         => EnumProps,
                   ConstructKind.Syntax       => SyntaxProps,
                   ConstructKind.Relation     => RelationProps,
                   ConstructKind.Unit         => UnitProps,
                   ConstructKind.UnitRelation => UnitRelationProps,
                   _                          => []
               };
    }

    public static SchemaProp? Find(ConstructKind kind, string key)
    {
        return For(kind).FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Sets a property from text typed by the person, saying what is wrong with it when it cannot be set (a choice that is not
    ///     one of the choices, a number that is not a number). Returns null when it was set.
    /// </summary>
    public static string? TrySet(SchemaDraft draft, SchemaConstruct item, string key, string? text)
    {
        var prop = Find(item.Kind, key);
        if (prop is null) return $"A {item.Kind.Title().ToLowerInvariant()} has no property \"{key}\".";

        string v = (text ?? string.Empty).Trim();
        switch (prop.Type)
        {
            case PropType.Bool:
                if (v.Length > 0)
                    if (!EnumRules.TryNormalizeValue(EnumType.Bool, v, out string b, out _)) return $"\"{v}\" is not yes or no.";
                    else v = b;
                if (v == "false") v = string.Empty;
                break;
            case PropType.Number:
                if (v.Length > 0 && !(double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && double.IsFinite(d))) return $"\"{v}\" is not a number.";
                break;
            case PropType.Choice when prop.Choices is { Count: > 0 } && v.Length > 0:
                string? hit = prop.Choices.FirstOrDefault(c => string.Equals(c, v, StringComparison.OrdinalIgnoreCase));
                if (hit is null) return $"\"{v}\" is not one of: {string.Join(", ", prop.Choices)}.";
                v = hit;
                break;
            case PropType.Flags:
                var known = prop.Choices ?? [];
                var names = new List<string>();
                foreach (string part in v.Split(new[] { ',', ' ', '|', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    string? name = known.FirstOrDefault(c => string.Equals(c, part, StringComparison.OrdinalIgnoreCase));
                    if (name is null) return $"\"{part}\" is not a flag; the flags are {string.Join(", ", known)}.";
                    if (!names.Contains(name)) names.Add(name);
                }

                v = string.Join(", ", names);
                break;
            case PropType.Choice when item.Kind == ConstructKind.Attribute && key == "type" && v.Length > 0:
                break;
        }

        if (item.Kind == ConstructKind.Attribute && key == "type" && v.Length > 0)
        {
            if (v.StartsWith("enum:", StringComparison.OrdinalIgnoreCase)) v = "enum:" + v[5..].Trim();
            else if (SchemaTypes.TryNormalize(v, out string clr)) v = clr;
            else return $"\"{v}\" is not a type an attribute can have; use one of {string.Join(", ", ClrTypes)}, or enum:Name.";
        }

        item.Set(key, v);
        if (item.Kind == ConstructKind.Relation) draft.Relink();
        return null;
    }
}

/// <summary>What an attribute's type may be and how a value is read for it.</summary>
public static class SchemaTypes
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
                                                                 {
                                                                     ["string"]         = "String",
                                                                     ["text"]           = "String",
                                                                     ["int"]            = "Int32",
                                                                     ["int32"]          = "Int32",
                                                                     ["integer"]        = "Int32",
                                                                     ["long"]           = "Int64",
                                                                     ["int64"]          = "Int64",
                                                                     ["bool"]           = "Boolean",
                                                                     ["boolean"]        = "Boolean",
                                                                     ["double"]         = "Double",
                                                                     ["float"]          = "Double",
                                                                     ["decimal"]        = "Decimal",
                                                                     ["datetime"]       = "DateTime",
                                                                     ["date"]           = "DateTime",
                                                                     ["datetimeoffset"] = "DateTimeOffset",
                                                                     ["timespan"]       = "TimeSpan",
                                                                     ["guid"]           = "Guid",
                                                                     ["uri"]            = "Uri",
                                                                     ["url"]            = "Uri",
                                                                     ["byte[]"]         = "Byte[]",
                                                                     ["bytes"]          = "Byte[]",
                                                                     ["binary"]         = "Byte[]"
                                                                 };

    /// <summary>The canonical name (<c>int</c> gives <c>Int32</c>) of a .NET type an attribute can have.</summary>
    public static bool TryNormalize(string? text, out string canonical)
    {
        canonical = string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string t = text.Trim();
        if (t.StartsWith("System.", StringComparison.OrdinalIgnoreCase)) t = t[7..];
        return Aliases.TryGetValue(t, out canonical!);
    }

    /// <summary>The name of the enum an attribute's type refers to (<c>enum:Colour</c> gives <c>Colour</c>), or null.</summary>
    public static string? EnumOf(string? type)
    {
        return type is not null && type.StartsWith("enum:", StringComparison.OrdinalIgnoreCase) && type.Length > 5 ? type[5..].Trim() : null;
    }

    /// <summary>The .NET type a world's field of this attribute is given (an enum is its values' type).</summary>
    public static string ClrFor(string type, EnumType? enumType)
    {
        if (EnumOf(type) is null) return TryNormalize(type, out string c) ? c : "String";

        return enumType switch
               {
                   EnumType.Integral or EnumType.Signed or EnumType.Byte => "Int64",
                   EnumType.Float                                        => "Double",
                   EnumType.Bool                                         => "Boolean",
                   EnumType.Guid                                         => "Guid",
                   _                                                     => "String"
               };
    }

    /// <summary>Whether the text is a value of the .NET type. Enum types are checked by the caller.</summary>
    public static bool TryValue(string type, string text, out string? error)
    {
        error = null;
        var c = CultureInfo.InvariantCulture;
        switch (type)
        {
            case "String":
                return true;
            case "Int32":
                if (int.TryParse(text, NumberStyles.Integer, c, out _)) return true;
                error = "is not a whole number that fits in 32 bits";
                return false;
            case "Int64":
                if (long.TryParse(text, NumberStyles.Integer, c, out _)) return true;
                error = "is not a whole number";
                return false;
            case "Boolean":
                if (EnumRules.TryNormalizeValue(EnumType.Bool, text, out _, out _)) return true;
                error = "is not true or false";
                return false;
            case "Double":
                if (double.TryParse(text, NumberStyles.Float, c, out double d) && double.IsFinite(d)) return true;
                error = "is not a number";
                return false;
            case "Decimal":
                if (decimal.TryParse(text, NumberStyles.Float, c, out _)) return true;
                error = "is not a number";
                return false;
            case "DateTime":
                if (DateTime.TryParse(text, c, DateTimeStyles.RoundtripKind, out _)) return true;
                error = "is not a date and time (try 2026-10-05T12:00:00Z)";
                return false;
            case "DateTimeOffset":
                if (DateTimeOffset.TryParse(text, c, DateTimeStyles.RoundtripKind, out _)) return true;
                error = "is not a date and time with an offset (try 2026-10-05T12:00:00+00:00)";
                return false;
            case "TimeSpan":
                if (TimeSpan.TryParse(text, c, out _)) return true;
                error = "is not a time span (try 01:30:00)";
                return false;
            case "Guid":
                if (Guid.TryParse(text, out _)) return true;
                error = "is not a GUID";
                return false;
            case "Uri":
                if (Uri.TryCreate(text, UriKind.Absolute, out _)) return true;
                error = "is not an absolute URI";
                return false;
            case "Byte[]":
                try
                {
                    Convert.FromBase64String(text);
                    return true;
                }
                catch (FormatException)
                {
                    error = "is not base64";
                    return false;
                }
            default:
                error = "has a type that is not known";
                return false;
        }
    }
}
