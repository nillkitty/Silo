using System.Globalization;
using Silo.Parsing;
using Byte = Silo.Parsing.Byte;

namespace Silo.DataDefs;

/// <summary>
///     Matches, locates and deserializes data with data definitions (<see cref="Parsing.DataDefinition" /> and its parts).
/// </summary>
/// <remarks>
///     A definition's children match in sequence. A part with an <see cref="Parsing.Arity" /> repeats greedily, giving
///     back
///     repetitions when what follows fails (each repetition keeps its first way of matching). <see cref="Parsing.Any" />
///     tries its
///     children in order. A scope that is bounded (an element of a <see cref="Parsing.Split" /> or
///     <see cref="Parsing.Lines" />, the body of a
///     <see cref="Parsing.Span" /> or <see cref="Parsing.Block" />, or the whole data for
///     <see cref="IsMatch(Parsing.IDefPart, byte[])" />) must be
///     consumed entirely by what is matched in it.
/// </remarks>
public static class DefinitionMatcher
{
    /// <summary>
    ///     Whether the definition matches all of <paramref name="data" />.
    /// </summary>
    public static bool IsMatch(this IDefPart definition, byte[] data)
    {
        return definition.Match(data) is not null;
    }

    /// <summary>
    ///     Whether the definition matches all of <paramref name="text" />.
    /// </summary>
    public static bool IsMatch(this IDefPart definition, string text)
    {
        return definition.Match(text) is not null;
    }

    /// <summary>
    ///     Matches the definition against all of <paramref name="data" />; null if it does not match.
    /// </summary>
    public static DefinitionMatch? Match(this IDefPart definition, byte[] data)
    {
        return new Runner().Run(definition, Scope.Of(data, 0, data.Length, false), 0, true);
    }

    /// <summary>
    ///     Matches the definition against all of <paramref name="text" /> (already decoded); null if it does not match.
    /// </summary>
    public static DefinitionMatch? Match(this IDefPart definition, string text)
    {
        return new Runner().Run(definition, Scope.Of(text, 0, text.Length, false), 0, true);
    }

    /// <summary>
    ///     Matches the definition at <paramref name="offset" />, taking as much of the data as it will; null if it does not
    ///     match there.
    /// </summary>
    public static DefinitionMatch? MatchAt(this IDefPart definition, byte[] data, int offset)
    {
        return new Runner().Run(definition, Scope.Of(data, offset, data.Length, false), 0, false);
    }

    /// <summary>
    ///     Matches the definition against the rest of <paramref name="stream" /> (which is left where it was).
    /// </summary>
    public static DefinitionMatch? Match(this IDefPart definition, Stream stream)
    {
        return definition.Match(ReadRest(stream));
    }

    /// <summary>
    ///     Deserializes all of <paramref name="data" /> into a capture tree.
    /// </summary>
    /// <exception cref="FormatException">The data does not match the definition.</exception>
    public static CaptureNode Deserialize(this IDefPart definition, byte[] data)
    {
        return definition.Match(data)?.Root ?? throw new FormatException($"The data does not match {Describe(definition)}.");
    }

    /// <summary>
    ///     Deserializes all of <paramref name="text" /> into a capture tree.
    /// </summary>
    /// <exception cref="FormatException">The text does not match the definition.</exception>
    public static CaptureNode Deserialize(this IDefPart definition, string text)
    {
        return definition.Match(text)?.Root ?? throw new FormatException($"The text does not match {Describe(definition)}.");
    }

    /// <summary>
    ///     Deserializes the rest of <paramref name="stream" /> (which is left where it was) into a capture tree.
    /// </summary>
    /// <exception cref="FormatException">The data does not match the definition.</exception>
    public static CaptureNode Deserialize(this IDefPart definition, Stream stream)
    {
        return definition.Deserialize(ReadRest(stream));
    }

    /// <summary>
    ///     Whether all of <paramref name="data" /> matches: true if it does, null if it could be the start of a match (the
    ///     data ran out first), false otherwise.
    /// </summary>
    public static bool? Probe(this IDefPart definition, byte[] data)
    {
        var runner = new Runner();
        if (runner.Run(definition, Scope.Of(data, 0, data.Length, true), 0, true) is not null) return true;
        return runner.NeedMore ? null : false;
    }

    /// <summary>
    ///     <see cref="Probe(IDefPart, byte[])" /> for the rest of <paramref name="stream" /> (which is left where it was).
    /// </summary>
    public static bool? Probe(this IDefPart definition, Stream stream)
    {
        return definition.Probe(ReadRest(stream));
    }

    /// <summary>
    ///     Finds occurrences of the definition in <paramref name="data" />, scanning from <paramref name="start" />. Each
    ///     match takes as much data as it will; the scan resumes after it unless <paramref name="overlapping" />.
    ///     Zero-length matches are not reported.
    /// </summary>
    public static IEnumerable<DefinitionMatch> Locate(this IDefPart definition, byte[] data, int start = 0, bool overlapping = false)
    {
        var literal = LeadingLiteral(definition);
        int number  = 0;
        for (int pos = start; pos < data.Length;)
        {
            if (literal is { Length: > 0 })
            {
                int hit = data.AsSpan(pos).IndexOf(literal);
                if (hit < 0) yield break;
                pos += hit;
            }

            var m = new Runner().Run(definition, Scope.Of(data, pos, data.Length, false), number, false);
            if (m is { Length: > 0 })
            {
                number++;
                yield return m;
                pos += overlapping ? 1 : m.Length;
            }
            else
            {
                pos++;
            }
        }
    }

    /// <summary>
    ///     <see cref="Locate(IDefPart, byte[], int, bool)" /> over the rest of <paramref name="stream" />; offsets are
    ///     relative
    ///     to the stream's position.
    /// </summary>
    public static IEnumerable<DefinitionMatch> Locate(this IDefPart definition, Stream stream, bool overlapping = false)
    {
        return definition.Locate(ReadRest(stream), 0, overlapping);
    }

    private static string Describe(IDefPart definition)
    {
        return definition switch
               {
                   DataDefinition { Name       : { } n } => $"'{n}'",
                   DataDefinition { ContentType: { } c } => c,
                   _                                     => definition.GetType().Name
               };
    }

    private static byte[] ReadRest(Stream stream)
    {
        long pos = stream.CanSeek ? stream.Position : 0;
        try
        {
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }
        finally
        {
            if (stream.CanSeek) stream.Position = pos;
        }
    }

    /// <summary>
    ///     The bytes every match must start with, when the definition opens with a required literal; used to skip ahead.
    /// </summary>
    private static byte[]? LeadingLiteral(IDefPart part)
    {
        for (int guard = 0; guard < 64; guard++)
            switch (part)
            {
                case Bytes { Value.Length: > 0 } b: return b.Value;
                case Byte b:                        return [b.Value];
                case DataDefinition or Capture or Block:
                    var d = (DefinitionBase<IDefPart>)part;
                    if (d.Items.Count == 0 || part is IHasArity { Arity: Arity.ZeroOrOne or Arity.ZeroOrMore }) return null;
                    var first = d.Items[0];
                    if (first is IHasArity { Arity: Arity.ZeroOrOne or Arity.ZeroOrMore }) return null;
                    part = first;
                    break;
                default: return null;
            }

        return null;
    }
}

/// <summary>
///     Helpers for the text written in definition attributes.
/// </summary>
public static class DefText
{
    /// <summary>
    ///     Replaces <c>\\</c> escapes: <c>\\ \" \' \t \r \n \0 \xHH \uHHHH</c>. Other backslashes are kept as they are, so a
    ///     lone <c>\</c> is a backslash.
    /// </summary>
    public static string Unescape(string s)
    {
        if (!s.Contains('\\')) return s;

        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c != '\\' || i + 1 >= s.Length)
            {
                sb.Append(c);
                continue;
            }

            char n = s[i + 1];
            switch (n)
            {
                case '\\' or '"' or '\'':
                    sb.Append(n);
                    i++;
                    break;
                case 't':
                    sb.Append('\t');
                    i++;
                    break;
                case 'r':
                    sb.Append('\r');
                    i++;
                    break;
                case 'n':
                    sb.Append('\n');
                    i++;
                    break;
                case '0':
                    sb.Append('\0');
                    i++;
                    break;
                case 'x' when TryHex(s, i + 2, 2, out int x):
                    sb.Append((char)x);
                    i += 3;
                    break;
                case 'u' when TryHex(s, i + 2, 4, out int u):
                    sb.Append((char)u);
                    i += 5;
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Parses hex digits, ignoring whitespace, commas, dashes and <c>0x</c> prefixes.
    /// </summary>
    /// <exception cref="FormatException">Not hex, or an odd number of digits.</exception>
    public static byte[] ParseHex(string s)
    {
        var digits = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '0' && i + 1 < s.Length && s[i + 1] is 'x' or 'X')
            {
                i++;
                continue;
            }

            if (char.IsWhiteSpace(c) || c is ',' or '-') continue;
            if (!char.IsAsciiHexDigit(c)) throw new FormatException($"'{c}' is not a hex digit in '{s}'.");
            digits.Append(c);
        }

        if (digits.Length % 2 != 0) throw new FormatException($"'{s}' has an odd number of hex digits.");
        return Convert.FromHexString(digits.ToString());
    }

    internal static bool TryHex(string s, int at, int count, out int value)
    {
        value = 0;
        return at + count <= s.Length &&
               int.TryParse(s.AsSpan(at, count), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
    }
}