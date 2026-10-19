using Silo.Contracts.Providers.Data;
using Silo.DbModel.Meta;

namespace Silo.Providers.Definitions;

/// <summary>The words the lists, dialogs and Explorer use for enums and syntaxes, in one place so they say the same thing everywhere.</summary>
public static class DefinitionText
{
    /// <summary>What an enum type is, in a few words.</summary>
    public static string Describe(EnumType type) => type switch
    {
        EnumType.Integral => "Integral (whole numbers, 0 and up)",
        EnumType.Signed   => "Signed (whole numbers, negative too)",
        EnumType.Float    => "Float (decimal numbers)",
        EnumType.String   => "String (text)",
        EnumType.Bool     => "Bool (true and false)",
        EnumType.Byte     => "Byte (0 to 255)",
        EnumType.Guid     => "Guid (unique identifiers)",
        _                 => type.ToString()
    };

    /// <summary>The flags of an enum as a short phrase, empty when there are none.</summary>
    public static string Describe(EnumFlags flags)
    {
        var parts = new List<string>();
        if ((flags & EnumFlags.Bitmask)         != 0) parts.Add("bitmask");
        if ((flags & EnumFlags.KnownValuesOnly) != 0) parts.Add("known values only");
        if ((flags & EnumFlags.DontUseName)     != 0) parts.Add("shows numbers");
        return string.Join(", ", parts);
    }

    /// <summary>What a syntax is built on: <c>Parser: Timestamp</c>, or <c>Pattern</c>.</summary>
    public static string Source(SyntaxRecord syntax)
    {
        if (syntax.Kind == SyntaxKind.Pattern) return "Pattern";
        string? key = syntax.Parser;
        return "Parser: " + (SyntaxParsers.Find(key)?.Title ?? key ?? "(none)");
    }

    /// <summary>The syntax's name, with its alias after it when it has one: <c>JObject (jobj)</c>.</summary>
    public static string NameWithAlias(SyntaxRecord syntax) => string.IsNullOrEmpty(syntax.Alias) ? syntax.Name : $"{syntax.Name} ({syntax.Alias})";

    /// <summary>The label and value of an enum value for the Explorer: <c>Open = 0</c>.</summary>
    public static string Label(EnumValueRecord value) => string.IsNullOrEmpty(value.Value) ? value.Name : $"{value.Name} = {value.Value}";

    /// <summary>A string that changes whenever anything about the enum (or its values) does, for the Explorer to know a node needs refreshing.</summary>
    public static string Stamp(EnumRecord e) =>
        string.Join("|", e.Name, e.Type, (int)e.Flags, e.Description, string.Join(";", e.Values.Select(v => $"{v.Id}:{v.Name}={v.Value}/{v.Description}")));

    /// <summary>A string that changes whenever anything about the syntax does.</summary>
    public static string Stamp(SyntaxRecord s) => string.Join("|", s.Name, s.Alias, s.Description, s.Kind, s.Parser, s.Pattern, s.Example, (int)s.Flags);
}
