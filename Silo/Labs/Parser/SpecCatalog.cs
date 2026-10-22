namespace Silo.Labs.Parser;

/// <summary>An atomic building block of a spec (a character class, end of input...): what the Tokens inspector lists.</summary>
/// <param name="Id">The id a <c>token</c> node refers to ("alpha-upper").</param>
/// <param name="Title">What it is called to a person.</param>
/// <param name="Description">What it matches.</param>
/// <param name="IconKey">Key of its icon (Resources/NavIcons.xaml); null for none.</param>
/// <param name="Test">Whether one character is this token; null for a token that is not a single character (end of input, accept).</param>
/// <param name="CanIgnore">Whether it makes sense as something to skip or to end on (a white space does, a letter hardly).</param>
public sealed record TokenInfo(string Id, string Title, string Description, string? IconKey, Func<char, bool>? Test, bool CanIgnore = true);

/// <summary>A combinator the Combinators inspector lists: how the nodes of a spec are put together.</summary>
/// <param name="Id">The id it is dragged as.</param>
/// <param name="Group">The heading it is listed under.</param>
/// <param name="Title">What it is called (the Superpower name where there is one).</param>
/// <param name="Description">What it does.</param>
/// <param name="Make">Wraps (or, with no node, starts) a tree: what dropping it on a node does.</param>
public sealed record CombinatorInfo(string Id, string Group, string Title, string Description, string? IconKey, Func<SpecNode?, SpecNode> Make);

/// <summary>The tokens and combinators a spec is built from. Where the Superpower library has the thing, the title is its name.</summary>
public static class SpecCatalog
{
    /// <summary>The token ids that match one character.</summary>
    public static IReadOnlyList<TokenInfo> Tokens { get; } =
    [
        new("alpha-upper", "alpha-upper", "A capital letter", "Data1", char.IsUpper, false),
        new("alpha-lower", "alpha-lower", "A small letter", "Data1", char.IsLower, false),
        new("alpha", "alpha", "A letter", "Data1", char.IsLetter, false),
        new("decimal-digit", "decimal-digit", "0-9 (or any decimal digit)", "Data1", char.IsDigit, false),
        new("hex-digit", "hex-digit", "0-9, a-f, A-F", "Data1", Uri.IsHexDigit, false),
        new("alnum", "alnum", "A letter or a digit", "Data1", char.IsLetterOrDigit, false),
        new("word-char", "word-char", "A letter, a digit or _", "Data1", c => char.IsLetterOrDigit(c) || c == '_', false),
        new("printable-char", "printable-char", "Any character that is not a control character", "Data1", c => !char.IsControl(c), false),
        new("punctuation", "punctuation", "A punctuation mark", "Data1", char.IsPunctuation, false),
        new("whitespace", "whitespace", "A space, tab or line break", "Data1", char.IsWhiteSpace),
        new("control-char", "control-char", "A control character (below space, and DEL)", "Data1", char.IsControl),
        new("newline", "newline", "A carriage return or line feed", "Data1", c => c is '\r' or '\n'),
        new("any-char", "any-char", "Any one character", "Data1", _ => true, false),
        new("EOF", "EOF", "The end of the text (matches nothing, only where there is nothing left)", "Data1", null),
        new("accept", "accept", "Always matches, using up nothing", "Data1", null, false)
    ];

    /// <summary>The token with this id, any case.</summary>
    public static TokenInfo? Token(string? id) => id is null ? null : Tokens.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The ids of the tokens that make sense to ignore or end on.</summary>
    public static IReadOnlyList<string> IgnorableTokenIds { get; } = Tokens.Where(t => t.CanIgnore && t.Test is not null).Select(t => t.Id).ToList();

    private static SpecNode Wrap(SpecNode? node, Func<SpecNode, SpecNode> wrap) => wrap(node ?? new SpecNode { Kind = "seq" });

    /// <summary>The combinators.</summary>
    public static IReadOnlyList<CombinatorInfo> Combinators { get; } =
    [
        new("many", "Quantifiers", "Many", "Zero or more of it", null, n => Wrap(n, SpecNode.Many)),
        new("once", "Quantifiers", "AtLeastOnce", "One or more of it", null, n => Wrap(n, SpecNode.AtLeastOnce)),
        new("repeat", "Quantifiers", "Repeat", "Exactly some number of it (2 to start with; change it on the node)", null, n => SpecNode.Repeat(n ?? new SpecNode { Kind = "seq" }, 2, 2)),
        new("optional", "Quantifiers", "Optional", "Zero or one of it", null, n => Wrap(n, SpecNode.Optional)),
        new("one", "Quantifiers", "[1]", "Exactly one of it ([1..1])", null, n => Wrap(n, SpecNode.One)),

        new("many-delimited", "Delimited", "ManyDelimitedBy", "Zero or more of the first, with the second between them",
            null, n => new SpecNode { Kind = "delimited", Min = 0, Children = [n ?? new SpecNode { Kind = "seq" }, new SpecNode { Kind = "chars", Text = "," }] }),
        new("once-delimited", "Delimited", "AtLeastOnceDelimitedBy", "One or more of the first, with the second between them",
            null, n => new SpecNode { Kind = "delimited", Min = 1, Children = [n ?? new SpecNode { Kind = "seq" }, new SpecNode { Kind = "chars", Text = "," }] }),

        new("between", "Structural", "Between", "Something between an opening and a closing (the value is what is between)",
            null, n => new SpecNode { Kind = "between", Children = [new SpecNode { Kind = "chars", Text = "(" }.Dropped(), n ?? new SpecNode { Kind = "seq" }, new SpecNode { Kind = "chars", Text = ")" }.Dropped()] }),
        new("ignore-then", "Structural", "IgnoreThen", "Match the first, leave it out of the value, then match the second",
            null, n => SpecNode.Seq((n ?? new SpecNode { Kind = "seq" }).Dropped())),
        new("ignore-many", "Structural", "IgnoreMany", "Zero or more of it, left out of the value", null, n => SpecNode.Many(n ?? new SpecNode { Kind = "seq" }).Dropped()),

        new("then", "Chaining", "Then", "One after the other (a sequence)", null, n => SpecNode.Seq(n ?? new SpecNode { Kind = "seq" })),
        new("or", "Chaining", "Or", "The first of these that matches (backtracking: each is tried from the start)", null, n => SpecNode.Choice(n ?? new SpecNode { Kind = "seq" })),
        new("apply", "Chaining", "Apply", "Turn the matched value into another (int, hex byte, text...)", null, n => SpecNode.Convert(n ?? new SpecNode { Kind = "seq" }, "text")),

        new("not", "Modifiers", "Not", "Matches only where it does not match, using up nothing (Not then any-char is \"any character but it\")", null, n => Wrap(n, SpecNode.Not)),
        new("try", "Modifiers", "Try", "Go back to the start when it fails part way", null, n => Wrap(n, SpecNode.Try)),
        new("where", "Modifiers", "Where", "It must also satisfy a test (len:2-4, range:0..255, regex:...)",
            null, n => new SpecNode { Kind = "where", Text = "len:1-", Children = [n ?? new SpecNode { Kind = "seq" }] }),

        new("named", "Metadata", "Named", "Gives the value a name", null, n => new SpecNode { Kind = "named", Name = "name", Children = [n ?? new SpecNode { Kind = "seq" }] })
    ];

    /// <summary>The combinator with this id.</summary>
    public static CombinatorInfo? Combinator(string? id) => id is null ? null : Combinators.Concat(TokenizerCombinators).FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The names of the conversions a <c>convert</c> node can do.</summary>
    public static IReadOnlyList<string> Conversions { get; } = ["text", "int", "decimal", "bool", "guid", "byte-hex", "bytes-hex", "hex-int", "none"];

    /// <summary>The output formats <c>ConvertTo</c> knows, for a spec to say it converts to.</summary>
    public static IReadOnlyList<string> Formats { get; } = ["text", "hex", "hex-colon", "hex-dash", "hex-dotted4", "base64", "json"];

    /// <summary>The names of the tokenizer mode's matchers (what its Combinators inspector lists).</summary>
    public static IReadOnlyList<CombinatorInfo> TokenizerCombinators { get; } =
    [
        new("many", "Quantifiers", "[0..*]", "Zero or more", null, n => Wrap(n, SpecNode.Many)),
        new("once", "Quantifiers", "[1..*]", "One or more", null, n => Wrap(n, SpecNode.AtLeastOnce)),
        new("optional", "Quantifiers", "[0..1]", "Zero or one", null, n => Wrap(n, SpecNode.Optional)),
        new("one", "Quantifiers", "[1]", "Exactly one ([1..1])", null, n => Wrap(n, SpecNode.One)),
        new("repeat", "Quantifiers", "[p..q]", "Between p and q", null, n => SpecNode.Repeat(n ?? new SpecNode { Kind = "seq" }, 2, 4)),
        new("not", "Matching", "Not", "Matches only where it does not match, using up nothing", null, n => Wrap(n, SpecNode.Not)),
        new("match", "Matching", "Match", "One character of a set or token", null, n => n ?? new SpecNode { Kind = "chars", Text = "." }),
        new("match-many", "Matching", "MatchMany", "A run of characters of a set or token", null, n => SpecNode.AtLeastOnce(n ?? new SpecNode { Kind = "chars", Text = "." }))
    ];
}
