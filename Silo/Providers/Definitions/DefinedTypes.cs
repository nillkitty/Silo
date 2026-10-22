using Silo.Providers.Data;
using Silo.Providers.Data.Shape;

namespace Silo.Providers.Definitions;

/// <summary>
///     An enum defined in a Silo, as a type: <c>enum:Name</c>. Its values are matched with the labels it defines (see
///     <see cref="EnumRules" />).
/// </summary>
public sealed record DefinedEnum(EnumRecord Definition) : IType
{
    public const string TypePrefix = "enum";
    public       Uri    Namespace => NamespaceUri;

    public static Uri      NamespaceUri { get; } = new("ns://enums", UriKind.Absolute);
    public        Uri      Uri          => new($"ns://enums/{Uri.EscapeDataString(Name)}", UriKind.Absolute);
    public        DataKind Kind         => DataKind.Nominal;

    public string    Name   => Definition.Name;
    public string    Prefix => TypePrefix;
    public DataShape Shape  => DataShape.Scalar;

    /// <summary>The label of the known value equal to <paramref name="value" />, or null when there is none.</summary>
    public string? LabelFor(string? value)
    {
        return EnumRules.LabelFor(Definition, value);
    }

    /// <summary>The value (canonical text) of the known value with that label, or null when there is none.</summary>
    public string? ValueFor(string? label)
    {
        return EnumRules.ValueFor(Definition, label);
    }
}

/// <summary>
///     A syntax defined in a Silo, as a type: <c>syntax:Name</c> (or by its alias). It is
///     <see cref="DataKind.Syntax" />: a string with rules, and <see cref="Syntax" /> turns it into an object.
/// </summary>
public sealed record DefinedSyntax(SyntaxRecord Definition, StoredSyntax Syntax) : IType
{
    public const string TypePrefix = "syntax";
    public       Uri    Namespace => NamespaceUri;

    public static Uri      NamespaceUri { get; } = new("ns://syntaxes", UriKind.Absolute);
    public        Uri      Uri          => new($"ns://syntaxes/{Uri.EscapeDataString(Name)}", UriKind.Absolute);
    public        DataKind Kind         => DataKind.Syntax;

    public string    Name   => Definition.Name;
    public string    Prefix => TypePrefix;
    public DataShape Shape  => DataShape.Scalar;
}

/// <summary>
///     What the enum and syntax type providers do, apart from where the Silo's store comes from (so it can be tested
///     with a fake store).
/// </summary>
public static class DefinedTypes
{
    /// <summary>
    ///     The <c>enum:Name</c> type for the lookup, or null when the lookup is for another prefix, there is no store, or
    ///     no such enum.
    /// </summary>
    public static IType? LookupEnum(IEnumStore? store, TypeLookup lookup)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        if (store is null || !string.Equals(lookup.Prefix, DefinedEnum.TypePrefix, StringComparison.OrdinalIgnoreCase)) return null;

        string name = DefinitionNames.LastSegment(lookup.Name);
        if (name.Length == 0) return null;

        return store.FindEnum(name) is { } found ? new DefinedEnum(found) : null;
    }

    /// <summary>
    ///     The <c>syntax:Name</c> type for the lookup (by name or alias), or null when the lookup is for another prefix,
    ///     there is no store, no such syntax, or the syntax cannot be built.
    /// </summary>
    public static IType? LookupSyntax(ISyntaxStore? store, TypeLookup lookup)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        if (store is null || !string.Equals(lookup.Prefix, DefinedSyntax.TypePrefix, StringComparison.OrdinalIgnoreCase)) return null;

        string name = DefinitionNames.LastSegment(lookup.Name);
        if (name.Length == 0) return null;

        if (store.FindSyntax(name) is not { } found) return null;
        return StoredSyntax.TryCreate(found, out var syntax, out _) ? new DefinedSyntax(found, syntax!) : null;
    }
}