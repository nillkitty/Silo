using Silo.DbModel.Meta;

namespace Silo.Providers.Data;

/// <summary>A syntax defined in a Silo.</summary>
/// <param name="Id">The row's id; 0 for a syntax not saved yet.</param>
/// <param name="Name">Unique name; this is what <c>syntax:Name</c> refers to.</param>
/// <param name="Alias">An optional second name (<c>jobj</c>).</param>
/// <param name="Description">What the syntax is for.</param>
/// <param name="Kind">Whether a built-in parser or a pattern decides what is valid.</param>
/// <param name="Parser">The built-in parser's key (<see cref="SyntaxKind.Parser" />).</param>
/// <param name="Pattern">The regular expression (<see cref="SyntaxKind.Pattern" />).</param>
/// <param name="Example">A sample of a valid value.</param>
/// <param name="Flags">Flags, such as <see cref="SyntaxFlags.Seeded" />.</param>
public sealed record SyntaxRecord(
    int         Id,
    string      Name,
    string?     Alias,
    string?     Description,
    SyntaxKind  Kind,
    string?     Parser,
    string?     Pattern,
    string?     Example,
    SyntaxFlags Flags = SyntaxFlags.None)
{
    /// <summary>Whether the syntax came with Silo.</summary>
    public bool IsSeeded => (Flags & SyntaxFlags.Seeded) != 0;

    /// <summary>A new syntax backed by a built-in parser.</summary>
    public static SyntaxRecord ForParser(string  name,           string      parser, string? alias = null, string? description = null,
                                         string? example = null, SyntaxFlags flags = SyntaxFlags.None)
    {
        return new SyntaxRecord(0, name, alias, description, SyntaxKind.Parser, parser, null, example, flags);
    }

    /// <summary>A new syntax backed by a regular expression.</summary>
    public static SyntaxRecord ForPattern(string  name,           string      pattern, string? alias = null, string? description = null,
                                          string? example = null, SyntaxFlags flags = SyntaxFlags.None)
    {
        return new SyntaxRecord(0, name, alias, description, SyntaxKind.Pattern, null, pattern, example, flags);
    }
}

/// <summary>
///     The syntaxes defined in a Silo (the <c>MvSyntax</c> table). A new Silo starts with the predefined ones (Timestamp,
///     Guid, JObject ... see <c>SyntaxSeeds</c>); they are ordinary rows that can be changed or deleted, and
///     <see cref="RestoreDefaults" /> brings back any that were deleted. Saving checks the definition
///     (<c>SyntaxRules</c>) and throws <see cref="DefinitionException" /> when it is not acceptable. Every successful
///     change is a real change to the Silo: it raises <see cref="IDatabaseChanges.Changed" />.
/// </summary>
public interface ISyntaxStore
{
    /// <summary>Every syntax, ordered by name.</summary>
    IReadOnlyList<SyntaxRecord> GetSyntaxes();

    /// <summary>The syntax with that id, or null.</summary>
    SyntaxRecord? GetSyntax(int id);

    /// <summary>The syntax with that name or alias (any case), or null.</summary>
    SyntaxRecord? FindSyntax(string nameOrAlias);

    /// <summary>Adds the syntax (<see cref="SyntaxRecord.Id" /> 0) or replaces the one it names. Returns what was stored.</summary>
    SyntaxRecord SaveSyntax(SyntaxRecord syntax);

    /// <summary>Deletes the syntax. Returns whether there was one.</summary>
    bool DeleteSyntax(int id);

    /// <summary>Adds back every predefined syntax whose name (or alias) is not taken. Returns how many were added.</summary>
    int RestoreDefaults();
}