namespace Silo;

[Flags]
public enum NewLine
{
    None = 0,
    Lf   = 1,
    Cr   = 2,
    CrLf = 3,
    Lax  = 4
}

/// <summary>
///     Splits the rest of the current scope into elements. The children form a sequence over the elements: each element is
///     matched whole by one child, and an element child without an <see cref="Arity" /> repeats any number of times.
/// </summary>
/// <remarks>
///     <see cref="Delimiter" />, <see cref="Qualifier" /> and <see cref="Escape" /> accept <c>\</c> escapes, so
///     <c>Qualifier='\"'</c> is a double quote. A qualified element is given to its children unquoted and unescaped.
/// </remarks>
public class Split : DefinitionBase<IDefPart>
{
    /// <summary>
    ///     Whether to suppress zero-length elements
    /// </summary>
    public bool Consolidate { get; set; }

    /// <summary>
    ///     The string upon which to split the input text; when unset, runs of whitespace split it
    /// </summary>
    public string? Delimiter { get; set; }

    /// <summary>
    ///     If specified, indicates a character used prior to the <see cref="Delimiter" /> to escape it.
    ///     The Escape character twice is used to include the Escape character.
    ///     <para />
    ///     Example 1:  Delimiter=[<c>,</c>], Qualifier=[<c><![CDATA["]]></c>], Escape=[<c><![CDATA[\]]></c>] --
    ///     <![CDATA["Qualified with \"quotes\" and a domain\\backslash in it."]]>
    ///     <para />
    ///     Example 2:  Delimiter=[<c>,</c>], Qualifier=[<c><![CDATA["]]></c>], Escape=[<c><![CDATA["]]></c>] --
    ///     <![CDATA["Qualified with ""quotes"" and a stray 5"" quote right there."]]>
    /// </summary>
    public string? Escape { get; set; }

    /// <summary>
    ///     If specified, limits the number of output tokens; the last token holds the rest of the scope
    /// </summary>
    public int? MaxTokens { get; set; }

    /// <summary>
    ///     If specified, and exists at the start (and end) of a string, Delimiters in that span are ignored.
    /// </summary>
    public string? Qualifier { get; set; }
}

/// <summary>
///     Splits the rest of the current scope into lines. The children form a sequence over the lines, as for
///     <see cref="Split" />. A newline ending the last line does not make an extra empty line.
/// </summary>
public class Lines : DefinitionBase<IDefPart>
{
    /// <summary>
    ///     Whether to suppress zero-length elements
    /// </summary>
    public bool Consolidate { get; set; }

    /// <summary>
    ///     The Newline matching to use: <see cref="NewLine.Lf" />, <see cref="NewLine.Cr" /> or <see cref="NewLine.CrLf" />
    ///     alone accept only that terminator; <see cref="NewLine.Lax" /> (or <see cref="NewLine.None" />) accepts any of them.
    /// </summary>
    public NewLine Newline { get; set; }
}

/// <summary>
///     Captures what its children match (the rest of the scope when it has none) as a <see cref="CaptureNode" />. Captures
///     made inside it become the node's children.
/// </summary>
public class Capture : DefinitionBase<IDefPart>, IHasArity
{
    /// <inheritdoc />
    public Arity Arity { get; set; }

    /// <summary>
    ///     The capture's name. <c>{n}</c> is replaced with the current element number of the n-th enclosing
    ///     <see cref="Split" /> or <see cref="Lines" /> (0 = innermost; the outermost is the match number); see
    ///     <see cref="InstanceNum" />. <c>{{</c> and <c>}}</c> are literal braces. When unset, no node is made.
    /// </summary>
    public string? To { get; set; }
}