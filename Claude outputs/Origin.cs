namespace Silo.Contracts;

/// <summary>
///     Where, in a source text file, something was read from: a 1-based line/column pair (the
///     same convention <see cref="Newtonsoft.Json.IJsonLineInfo" /> and
///     <see cref="System.Xml.IXmlLineInfo" /> already use), plus an optional 0-based absolute
///     character offset from the start of the file when the underlying parser tracks one.
/// </summary>
/// <remarks>
///     A single abstraction shared across every text-based <see cref="IDocumentProvider{TContent}" />
///     (JSON via <c>JsonFormat</c>/<c>OriginExtensions.GetOrigin</c>, XML via
///     <c>XmlFormat</c>/<c>OriginExtensions.GetOrigin</c>, YAML natively via
///     <c>YamlDocNode.Start</c>/<c>YamlDocNode.End</c>), so callers can ask "where did this come
///     from in the source file" the same way regardless of which format produced it.
/// </remarks>
/// <param name="Line">The 1-based line number, or <c>0</c> if unknown (see <see cref="IsKnown" />).</param>
/// <param name="Column">The 1-based column number, or <c>0</c> if unknown (see <see cref="IsKnown" />).</param>
/// <param name="CharacterOffset">
///     The 0-based absolute offset, in characters, from the start of the file, or <c>null</c> if
///     the underlying parser doesn't track one.
/// </param>
public readonly record struct Origin(int Line, int Column, long? CharacterOffset = null)
{
    /// <summary>
    ///     The sentinel value meaning "no position information is available" -- e.g. because the
    ///     underlying library didn't track it for this particular node, or the value wasn't
    ///     parsed from a file at all. Distinct from a real <c>Line</c>/<c>Column</c>, since both
    ///     of those are always &gt;= 1 for an actual position (see <see cref="IsKnown" />).
    /// </summary>
    public static readonly Origin Unknown = new(0, 0, null);

    /// <summary>
    ///     True if this <see cref="Origin" /> carries a real line/column position, i.e. is not
    ///     <see cref="Unknown" />.
    /// </summary>
    public bool IsKnown => Line > 0 && Column > 0;

    public override string ToString() =>
        IsKnown
            ? CharacterOffset is { } offset ? $"{Line}:{Column} (char {offset})" : $"{Line}:{Column}"
            : "(unknown position)";
}
