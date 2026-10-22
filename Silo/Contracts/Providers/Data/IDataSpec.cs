using System.IO;

namespace Silo.Providers.Data;

/// <summary>How one <see cref="IDataSpec.Parse" /> went.</summary>
/// <param name="Success">Whether the text (up to where the specification lets it end) is a match.</param>
/// <param name="Value">
///     What was extracted: the specification's <see cref="IDataSpec.ClrType" /> when it has one, else
///     nested lists and strings.
/// </param>
/// <param name="Consumed">How many characters were used.</param>
/// <param name="ErrorPosition">Where it went wrong (the furthest place any attempt reached); -1 on success.</param>
/// <param name="Error">What was expected there; null on success.</param>
public sealed record DataParseResult(bool Success, object? Value, int Consumed, int ErrorPosition, string? Error)
{
    /// <summary>A refusal.</summary>
    public static DataParseResult Failure(int position, string error)
    {
        return new DataParseResult(false, null, 0, position, error);
    }
}

/// <summary>
///     Defines a data specification: a named, identified format of data that can be recognised in a stream or text, parsed
///     into a value, and
///     converted to other forms. The Parser Lab builds them; anything else may implement it.
/// </summary>
/// <remarks>
///     Everything but <see cref="Name" /> and <see cref="MatchesData" /> has a default, so a specification that only
///     recognises data stays a
///     two-member implementation. No parsing library's types appear here: how <see cref="Parse" /> works is the
///     implementation's business.
/// </remarks>
public interface IDataSpec
{
    /// <summary>The fully qualified name ("Net.Mac.Address").</summary>
    string Name { get; }

    /// <summary>
    ///     The CLR type <see cref="Parse" /> extracts (<c>byte[]</c> for a MAC address); null when it extracts nested
    ///     lists and strings.
    /// </summary>
    Type? ClrType => null;

    /// <summary>
    ///     The types and formats a value of this specification converts to (<c>hex</c>, <c>base64</c>, <c>System.Guid</c>
    ///     ).
    /// </summary>
    IReadOnlyList<string> ConvertsTo => [];

    /// <summary>What the specification is for, in a sentence.</summary>
    string? Description => null;

    /// <summary>
    ///     The kinds of character that end the text the parser reads instead of being an error (a white space ends a
    ///     word), as token ids.
    /// </summary>
    IReadOnlyList<string> ExitTokens => [];

    /// <summary>
    ///     The kinds of character the parser skips between the items it reads (white space, control characters), as token
    ///     ids.
    /// </summary>
    IReadOnlyList<string> IgnoredTokens => [];

    /// <summary>The object identifier that tells this specification from any other; null for none.</summary>
    string? Oid => null;

    /// <summary>
    ///     Gets whether the data specification matches the Stream,
    ///     at the current stream position.
    /// </summary>
    /// <returns>
    ///     Returns true if successful,
    ///     false if no match, and
    ///     null if indeterminate (e.g. not enough data)
    /// </returns>
    bool? MatchesData(Stream stream);

    /// <summary>Reads <paramref name="text" /> as this specification (all of it, or up to a token the specification ends on).</summary>
    DataParseResult Parse(string text)
    {
        return DataParseResult.Failure(0, "This specification cannot read text.");
    }

    /// <summary>A value this specification parsed, as <paramref name="target" /> (one of <see cref="ConvertsTo" />).</summary>
    /// <exception cref="NotSupportedException">The specification does not convert to that.</exception>
    object? ConvertTo(object? value, string target)
    {
        throw new NotSupportedException($"{Name} does not convert to {target}.");
    }
}

public interface IFormatProvider
{
}