namespace Silo.Parsing;

/// <summary>
///     In a data definition, matches the remainder of the current scope
///     until either the current scope fails, a parent scope terminates the scope
///     (delimiter or newline), or end of stream.
/// </summary>
public class Rest : IDefPart
{
}

/// <summary>
///     In a data definition, tries (and rules out) each child combinator and succeeds if any child combinator succeeds.
/// </summary>
public class Any : DefinitionBase<IDefPart>
{
}

/// <summary>
///     In a data definition, matches the single case when the cursor is at the end of stream
/// </summary>
public class End : IDefPart
{
}

/// <summary>
///     In a data definition, consumes (but throws away) the next n bytes or characters, then any values read from the
///     stream that belong to <see cref="Values" />
/// </summary>
public class Skip : IDefPart
{
    /// <summary>
    ///     A number of bytes to skip (binary data only)
    /// </summary>
    public int Bytes { get; set; }

    /// <summary>
    ///     A number of characters to skip (text)
    /// </summary>
    public int Chars { get; set; }

    /// <summary>
    ///     Values to skip while they keep appearing, e.g. whitespace; in binary data each char is compared as a byte value
    /// </summary>
    public char[] Values { get; set; } = [];
}

/// <summary>
///     In a data definition, matches a span of text defined by its attributes
/// </summary>
/// <remarks>
///     The span is read as: <see cref="Exactly" />, or else an optional <see cref="Start" />/<see cref="Starts" />, a body
///     (bounded by <see cref="While" />, <see cref="WhileNot" /> or <see cref="Until" /> when given), and an optional
///     <see cref="End" />/<see cref="Ends" />. Children, if any, must match the whole body. With an end marker and no body
///     attribute, the nearest end marker closes the span first.
/// </remarks>
public class Span : DefinitionBase<IDefPart>
{
    /// <summary>
    ///     If specified, requires the text to end with this substring
    /// </summary>
    public string? End { get; set; }

    /// <summary>
    ///     If specified, requires the text to end with this sequence of chars
    /// </summary>
    public char[] Ends { get; set; } = [];

    /// <summary>
    ///     If specified, requires the next text read to match this string
    /// </summary>
    public string? Exactly { get; set; }

    /// <summary>
    ///     If specified, requires the text to begin with this substring
    /// </summary>
    public string? Start { get; set; }

    /// <summary>
    ///     If specified, requires the text to begin with this sequence of chars
    /// </summary>
    public char[] Starts { get; set; } = [];

    /// <summary>
    ///     Matches text until a character is read which matches the string specified (char ranges, individual chars, or a
    ///     combination e.g.
    ///     <c>a-zA-Z0-9_+#</c>) and then succeeds;  if such a character is not encountered, the combinator does NOT succeed.
    /// </summary>
    public string? Until { get; set; }

    /// <summary>
    ///     Matches text while the characters match the string specified (char ranges, individual chars, or a combination e.g.
    ///     <c>a-zA-Z0-9_+#</c>)
    /// </summary>
    public string? While { get; set; }

    /// <summary>
    ///     Matches text while the characters read do not match the string specified (char ranges, individual chars, or a
    ///     combination e.g.
    ///     <c>a-zA-Z0-9_+#</c>)
    /// </summary>

    public string? WhileNot { get; set; }
}