using Newtonsoft.Json;

namespace Silo.Labs.Parser;

/// <summary>
///     One node of a spec's tree: what the Parser Lab's canvas shows and saves. A node is a <see cref="Kind" /> from the catalog
///     (<see cref="SpecCatalog" />) with the few settings that kind takes; the tree is built by dragging tokens, combinators and other rules
///     onto the canvas, and turned into a parser by <see cref="SpecCompiler" />.
/// </summary>
/// <remarks>
///     The kinds: leaves <c>token</c> (<see cref="Text" /> is a token id, or the name of a token of the spec's own), <c>literal</c> (<see cref="Text" />
///     is the string), <c>chars</c> (any of the characters in <see cref="Text" />, or none of them when <see cref="Negate" />), <c>unicode</c> (any
///     character of the Unicode classes named in <see cref="Text" />, or none of them when <see cref="Negate" />), <c>ref</c> (<see cref="Text" /> is a
///     rule's name, in the spec <see cref="Spec" /> when that is given) and <c>eof</c>; combinators <c>not</c> (matches, using up nothing, only where its
///     child does <i>not</i> match), <c>seq</c> (children in order), <c>choice</c> (the first child that matches), <c>repeat</c> (<see cref="Min" /> to
///     <see cref="Max" /> of its child), <c>try</c> (a failure there does not commit), <c>between</c> (open, body, close: the body is the result),
///     <c>delimited</c> (an item and a separator), <c>convert</c> (<see cref="Text" /> names a conversion of the child's value), <c>where</c> (the
///     child's text must satisfy the predicate in <see cref="Text" />) and <c>named</c> (labels the child's value with <see cref="Name" />).
/// </remarks>
public sealed class SpecNode
{
    public string Kind { get; set; } = string.Empty;

    /// <summary>A token id, literal string, character set, rule name, conversion or predicate, by <see cref="Kind" />.</summary>
    public string? Text { get; set; }

    /// <summary>For <c>repeat</c> and <c>delimited</c>: the fewest times.</summary>
    public int Min { get; set; }

    /// <summary>For <c>repeat</c>: the most times; null for as many as there are.</summary>
    public int? Max { get; set; }

    /// <summary>The node's value is left out of the result of the sequence it is in (a separator, white space).</summary>
    public bool Ignore { get; set; }

    /// <summary>For <c>literal</c>: capitals and small letters are the same.</summary>
    public bool IgnoreCase { get; set; }

    /// <summary>For <c>chars</c>: any character but those in <see cref="Text" />.</summary>
    public bool Negate { get; set; }

    /// <summary>For <c>named</c>: the label.</summary>
    public string? Name { get; set; }

    /// <summary>For <c>ref</c>: the imported spec the rule is in; null for a rule of this spec.</summary>
    public string? Spec { get; set; }

    public List<SpecNode> Children { get; set; } = [];

    // ---- builders (what the canvas and the tests use) --------------------------------------------

    public static SpecNode Token(string id) => new() { Kind = "token", Text = id };

    public static SpecNode Literal(string text, bool ignoreCase = false) => new() { Kind = "literal", Text = text, IgnoreCase = ignoreCase };

    public static SpecNode Chars(string set, bool negate = false) => new() { Kind = "chars", Text = set, Negate = negate };

    public static SpecNode Ref(string rule, string? spec = null) => new() { Kind = "ref", Text = rule, Spec = spec };

    /// <summary>Any character of the Unicode classes (<c>Lu</c>, <c>L,Nd</c>...), or of none of them when <paramref name="negate" />.</summary>
    public static SpecNode Unicode(string classes, bool negate = false) => new() { Kind = "unicode", Text = classes, Negate = negate };

    /// <summary>Matches where <paramref name="item" /> does not, using up nothing.</summary>
    public static SpecNode Not(SpecNode item) => new() { Kind = "not", Children = [item] };

    /// <summary>Exactly one of <paramref name="item" />.</summary>
    public static SpecNode One(SpecNode item) => Repeat(item, 1, 1);

    public static SpecNode Seq(params SpecNode[] items) => new() { Kind = "seq", Children = [.. items] };

    public static SpecNode Choice(params SpecNode[] items) => new() { Kind = "choice", Children = [.. items] };

    public static SpecNode Repeat(SpecNode item, int min, int? max) => new() { Kind = "repeat", Min = min, Max = max, Children = [item] };

    public static SpecNode Many(SpecNode item) => Repeat(item, 0, null);

    public static SpecNode AtLeastOnce(SpecNode item) => Repeat(item, 1, null);

    public static SpecNode Optional(SpecNode item) => Repeat(item, 0, 1);

    public static SpecNode Try(SpecNode item) => new() { Kind = "try", Children = [item] };

    public static SpecNode Convert(SpecNode item, string to) => new() { Kind = "convert", Text = to, Children = [item] };

    public SpecNode Dropped()
    {
        Ignore = true;
        return this;
    }

    /// <summary>The node in a few words (for messages, tooltips and the clipboard's text).</summary>
    public string Describe()
    {
        string Inner(int i) => i < Children.Count ? Children[i].Describe() : "(nothing)";
        switch (Kind)
        {
            case "token": return Text ?? "?";
            case "literal": return "\"" + Text + "\"";
            case "chars": return (Negate ? "[^" : "[") + Text + "]";
            case "unicode": return (Negate ? "not " : string.Empty) + "Unicode " + Text;
            case "ref": return string.IsNullOrEmpty(Spec) ? Text ?? "?" : Spec + "/" + Text;
            case "eof": return "end of input";
            case "not": return "not " + Inner(0);
            case "seq": return Children.Count == 0 ? "(empty)" : string.Join(" ", Children.Take(3).Select(c => c.Describe())) + (Children.Count > 3 ? " …" : string.Empty);
            case "choice": return string.Join(" | ", Children.Take(3).Select(c => c.Describe())) + (Children.Count > 3 ? " …" : string.Empty);
            case "repeat":
                return Inner(0) + (Min, Max) switch
                                  {
                                      (0, null)                   => "[0..*]",
                                      (1, null)                   => "[1..*]",
                                      (0, 1)                      => "[0..1]",
                                      ( { } a, { } b) when a == b => $"[{a}]",
                                      ( { } a, null)              => $"[{a}..*]",
                                      ( { } a, { } b)             => $"[{a}..{b}]"
                                  };
            case "convert": return Inner(0) + " ⇒ " + Text;
            case "named": return Name + "=" + Inner(0);
            default: return Kind + (Children.Count > 0 ? "(" + Inner(0) + ")" : string.Empty);
        }
    }

    /// <summary>A copy of the node and everything under it.</summary>
    public SpecNode Clone()
    {
        var copy = (SpecNode)MemberwiseClone();
        copy.Children = Children.Select(c => c.Clone()).ToList();
        return copy;
    }

    /// <summary>This node and all below it, depth first.</summary>
    public IEnumerable<SpecNode> SelfAndDescendants()
    {
        yield return this;
        foreach (var c in Children)
        foreach (var d in c.SelfAndDescendants())
            yield return d;
    }
}

/// <summary>Whether a rule is a syntax (built from tokens and other rules) or a token (a unit that is built from characters).</summary>
public enum RuleKind
{
    Syntax,
    Token
}

/// <summary>One named rule of a spec: <c>byte := hex-digit x 2</c>.</summary>
public sealed class RuleDef
{
    public string Name { get; set; } = string.Empty;

    public RuleKind Kind { get; set; }

    public string? Description { get; set; }

    /// <summary>What the rule is made of; null while it is still empty.</summary>
    public SpecNode? Body { get; set; }

    /// <summary>The CLR type the rule yields, as text ("byte[6]", "int"); null: whatever the body's value is.</summary>
    public string? Returns { get; set; }

    /// <summary>"Tree" (the nested values as they are) or "Linear" (flattened into one list).</summary>
    public string Shape { get; set; } = "Tree";
}

/// <summary>Whether a saved <see cref="Fragment" /> is a token (a reusable leaf, used by name) or a piece of syntax (copied in where it is dropped).</summary>
public enum FragmentKind
{
    /// <summary>A reusable token: characters or a combination of them, used by name (a <c>token</c> node) so that changing it changes every use.</summary>
    Token,

    /// <summary>A saved expression: dropped into a rule as a copy.</summary>
    Syntax
}

/// <summary>A named piece of a spec kept apart from the rules: what "Save Syntax" and "Save as Token" make.</summary>
public sealed class Fragment
{
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public FragmentKind Kind { get; set; }

    public SpecNode? Node { get; set; }

    public Fragment Clone() => new() { Name = Name, Description = Description, Kind = Kind, Node = Node?.Clone() };
}

/// <summary>One kind-of-token rule of the tokenizer mode: what is matched and the kind it makes (null kind: the text is skipped).</summary>
public sealed class TokenRule
{
    public SpecNode? Matcher { get; set; }

    public string? Kind { get; set; }
}

/// <summary>The tokenizer mode's spec: the kinds of token, and the rules that cut text into them, tried in order.</summary>
public sealed class TokenizerSpec
{
    /// <summary>
    ///     What the token kinds are called as a type ("CiscoCliToken"): the name of an enum of the Silo (<see cref="ExistingEnum" />), or of
    ///     the enum this spec defines on the spot out of <see cref="Kinds" />.
    /// </summary>
    public string TokenType { get; set; } = "Token";

    /// <summary>The names of the kinds: the values of the enum. Unique, without regard to case.</summary>
    public List<string> Kinds { get; set; } = [];

    /// <summary>True when <see cref="TokenType" /> is an enum that already exists in the Silo (its values are <see cref="Kinds" />); false when the spec defines it.</summary>
    public bool ExistingEnum { get; set; }

    /// <summary>For an enum defined here: add it to the Silo's Enums when the spec is kept.</summary>
    public bool IncludeInEnums { get; set; }

    public List<TokenRule> Rules { get; set; } = [];
}

/// <summary>Which part of the Lab a spec is being built for.</summary>
public enum SpecMode
{
    /// <summary>Rules over characters (a <c>TextParser</c> per rule).</summary>
    Parser,

    /// <summary>Rules that cut text into tokens (a <c>Tokenizer</c>).</summary>
    Tokenizer
}

/// <summary>
///     Everything saved of one data spec: its identity (name, OID, CLR type), what it converts to, which tokens it ignores and ends on, its rules,
///     and the tokenizer. Plain data: <see cref="SpecCompiler" /> makes the parser, and the Lab wraps it as an <c>IDataSpec</c>.
/// </summary>
public sealed class ParserSpecDoc
{
    /// <summary>The fully qualified name ("Net.Mac.Address").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The object identifier that tells this spec from any other in the metaverse and the universe; null for none.</summary>
    public string? Oid { get; set; }

    /// <summary>The CLR type the parser extracts ("System.Byte[]"); null for the nested values.</summary>
    public string? ClrType { get; set; }

    /// <summary>The types and formats a value of this spec converts to ("hex", "base64", "System.Guid").</summary>
    public List<string> ConvertsTo { get; set; } = [];

    /// <summary>Token ids skipped between the items of a sequence (white space, control characters).</summary>
    public List<string> Ignore { get; set; } = [];

    /// <summary>Token ids the text may end on, instead of ending (a white space or a new line ends a word).</summary>
    public List<string> ExitOn { get; set; } = [];

    public SpecMode Mode { get; set; }

    /// <summary>The rule being defined (what a sample is parsed with when no rule is named).</summary>
    public string? Root { get; set; }

    public List<RuleDef> Rules { get; set; } = [];

    public TokenizerSpec Tokenizer { get; set; } = new();

    /// <summary>The names of the specs whose rules this one uses (live: a <c>ref</c> node with a <see cref="SpecNode.Spec" />). Never a cycle (<see cref="SpecImports" />).</summary>
    public List<string> Imports { get; set; } = [];

    /// <summary>The tokens and pieces of syntax saved in this spec.</summary>
    public List<Fragment> Fragments { get; set; } = [];

    public Fragment? FindFragment(string? name, FragmentKind? kind = null)
    {
        return name is null ? null : Fragments.FirstOrDefault(f => (kind is null || f.Kind == kind) && string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public RuleDef? Find(string? name) => name is null ? null : Rules.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

    public string ToJson() => JsonConvert.SerializeObject(this, Formatting.None);

    public static ParserSpecDoc? FromJson(string json) => JsonConvert.DeserializeObject<ParserSpecDoc>(json);

    public ParserSpecDoc Clone() => FromJson(ToJson())!;
}

/// <summary>
///     A named collection of fixed strings to try the spec on. A sample only ever <i>matches</i> or <i>does not match</i>: what is expected of it
///     is not part of it (that is a <see cref="TestCase" />, which passes or fails).
/// </summary>
public sealed class SampleSet
{
    public string Name { get; set; } = string.Empty;

    /// <summary>The rule the samples are shown against (the Samples inspector's "Eval as"); null for the spec's root.</summary>
    public string? Rule { get; set; }

    public List<string> Items { get; set; } = [];
}

/// <summary>What a test expects of every sample it uses.</summary>
public enum Expectation
{
    /// <summary>The text is a whole match of the rule.</summary>
    Matches,

    /// <summary>The text is not a match.</summary>
    DoesNotMatch,

    /// <summary>The text is a match, and its value shown as text equals <see cref="TestCase.ExpectedValue" />.</summary>
    ValueEquals
}

/// <summary>
///     Samples and what to expect of them: turns "matches / does not match" into "pass / fail", the way the Test Explorer of Visual Studio does.
/// </summary>
public sealed class TestCase
{
    public string Name { get; set; } = string.Empty;

    /// <summary>The rule the samples are parsed with; null for the spec's root.</summary>
    public string? Rule { get; set; }

    /// <summary>The names of the sample sets it uses.</summary>
    public List<string> Samples { get; set; } = [];

    public Expectation Expect { get; set; } = Expectation.Matches;

    public string? ExpectedValue { get; set; }

    /// <summary>Strings of its own, besides the sets.</summary>
    public List<string> Extra { get; set; } = [];
}

/// <summary>Samples and tests of a spec: kept in the Silo beside it.</summary>
public sealed class SpecTestBook
{
    public List<SampleSet> Samples { get; set; } = [];

    public List<TestCase> Tests { get; set; } = [];

    public string ToJson() => JsonConvert.SerializeObject(this, Formatting.None);

    public static SpecTestBook FromJson(string json) => JsonConvert.DeserializeObject<SpecTestBook>(json) ?? new SpecTestBook();
}
