namespace Silo.DbModel.Meta;

/// <summary>How a defined <see cref="MvSyntax" /> decides what is valid and what a value turns into.</summary>
public enum SyntaxKind
{
    /// <summary>
    ///     Backed by one of the parsers built into Silo (<see cref="MvSyntax.Parser" /> is its key: <c>timestamp</c>,
    ///     <c>guid</c>, <c>ipsubnet</c> ...). Values become real objects (a <c>DateTimeOffset</c>, a <c>Guid</c> ...).
    /// </summary>
    Parser = 0,

    /// <summary>Backed by a regular expression (<see cref="MvSyntax.Pattern" />) the whole string has to match. Values stay strings.</summary>
    Pattern = 1
}

/// <summary>Flags kept on a defined syntax.</summary>
[Flags]
public enum SyntaxFlags
{
    None = 0,

    /// <summary>The syntax came with Silo (it was seeded into the Silo when it was created). It can still be edited or deleted.</summary>
    Seeded = 1
}

/// <summary>
///     A syntax defined in a Silo: rules for validating a string and turning it into a usable object (see
///     <c>ISyntax</c>), under a name of the Silo's own. Reached as the type <c>syntax:Name</c> through the
///     <c>SyntaxTypeProvider</c>; <see cref="MvAttribute.Syntax" /> refers to one by <see cref="Id" />.
/// </summary>
public class MvSyntax : Item
{
    /// <summary>Primary key</summary>
    public int Id { get; set; }

    /// <summary>Unique name (letters, digits, underscore, dot and dash; not starting with a digit). Compared without regard to case.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>An optional second name that resolves to the same syntax (<c>jobj</c> for JObject). Unique among all names and aliases.</summary>
    public string? Alias { get; set; }

    /// <summary>What the syntax is for, in a sentence.</summary>
    public string? Description { get; set; }

    /// <summary>Whether the syntax is built from one of Silo's parsers or from a pattern.</summary>
    public SyntaxKind Kind { get; set; }

    /// <summary>The key of the built-in parser, when <see cref="Kind" /> is <see cref="SyntaxKind.Parser" />.</summary>
    public string? Parser { get; set; }

    /// <summary>The regular expression the whole string must match, when <see cref="Kind" /> is <see cref="SyntaxKind.Pattern" />.</summary>
    public string? Pattern { get; set; }

    /// <summary>A sample of a valid value. When given it must be valid under the syntax.</summary>
    public string? Example { get; set; }

    /// <summary>Flags</summary>
    public SyntaxFlags Flags { get; set; }
}
