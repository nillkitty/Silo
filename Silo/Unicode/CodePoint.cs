using System.Globalization;
using System.Text;
using Newtonsoft.Json;

namespace Silo.Unicode;

/// <summary>
///     One Unicode code point, <c>U+0000</c> to <c>U+10FFFF</c>. Unlike <see cref="Rune" /> it also stands for a lone surrogate, so any
///     value a person can type or paste into the Unicode Explorer has a <see cref="CodePoint" />. Written to JSON as <c>"U+1F7E2"</c>.
/// </summary>
[JsonConverter(typeof(CodePointJsonConverter))]
public readonly record struct CodePoint : IComparable<CodePoint>
{
    /// <summary>The largest code point, <c>U+10FFFF</c>.</summary>
    public const int MaxValue = 0x10FFFF;

    /// <summary>A code point with the given numeric value.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or above <see cref="MaxValue" />.</exception>
    public CodePoint(int value)
    {
        if ((uint)value > MaxValue) throw new ArgumentOutOfRangeException(nameof(value), value, "A code point is between 0 and 0x10FFFF.");
        Value = value;
    }

    /// <summary>The number, 0 to 0x10FFFF.</summary>
    public int Value { get; }

    /// <summary>The usual way to write it: <c>U+1F7E2</c> (at least four hex digits).</summary>
    public string Hex => "U+" + Value.ToString("X4", CultureInfo.InvariantCulture);

    /// <summary>False for the surrogate code points (<c>U+D800</c> to <c>U+DFFF</c>), which are no characters on their own.</summary>
    public bool IsScalar => Value is < 0xD800 or > 0xDFFF;

    /// <summary>The text of the code point: one or two UTF-16 units (a lone surrogate is a single unit).</summary>
    public string Text => IsScalar ? char.ConvertFromUtf32(Value) : ((char)Value).ToString();

    /// <summary>The code point as a <see cref="Rune" />; false for a surrogate.</summary>
    public bool TryGetRune(out Rune rune) => Rune.TryCreate(Value, out rune);

    /// <summary>The text of the code point (the character itself, not its number).</summary>
    public override string ToString() => Text;

    /// <inheritdoc />
    public int CompareTo(CodePoint other) => Value.CompareTo(other.Value);

    /// <summary>Whether this code point is before <paramref name="b" />.</summary>
    public static bool operator <(CodePoint a, CodePoint b) => a.Value < b.Value;

    /// <summary>Whether this code point is after <paramref name="b" />.</summary>
    public static bool operator >(CodePoint a, CodePoint b) => a.Value > b.Value;

    /// <summary>Whether this code point is not after <paramref name="b" />.</summary>
    public static bool operator <=(CodePoint a, CodePoint b) => a.Value <= b.Value;

    /// <summary>Whether this code point is not before <paramref name="b" />.</summary>
    public static bool operator >=(CodePoint a, CodePoint b) => a.Value >= b.Value;

    /// <summary>The code point of a single UTF-16 unit (a surrogate half stands for itself).</summary>
    public static CodePoint FromChar(char c) => new(c);

    /// <summary>The code points of <paramref name="text" />: a surrogate pair is one, a lone surrogate is itself.</summary>
    public static IEnumerable<CodePoint> Enumerate(string? text)
    {
        if (string.IsNullOrEmpty(text)) yield break;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                yield return new CodePoint(char.ConvertToUtf32(c, text[i + 1]));
                i++;
            }
            else
            {
                yield return new CodePoint(c);
            }
        }
    }

    /// <summary>The text the code points make when written one after the other.</summary>
    public static string ToText(IEnumerable<CodePoint>? codePoints)
    {
        if (codePoints is null) return string.Empty;

        var sb = new StringBuilder();
        foreach (var cp in codePoints) sb.Append(cp.Text);
        return sb.ToString();
    }

    /// <summary>
    ///     Reads one code point from how people write it: <c>U+1F7E2</c>, <c>0x1F7E2</c>, <c>\u{1F7E2}</c>, <c>🟢</c>,
    ///     <c>\U0001F7E2</c>, <c>&amp;#128994;</c>, <c>&amp;#x1F7E2;</c>, the character itself, or just hex digits (<c>1F7E2</c>).
    /// </summary>
    /// <remarks>A single character that is also a hex digit (<c>A</c>) is the character; two or more hex digits are a number (<c>41</c> is <c>A</c>).</remarks>
    public static bool TryParse(string? text, out CodePoint codePoint)
    {
        codePoint = default;
        if (string.IsNullOrEmpty(text)) return false;

        string t = text.Trim();
        if (t.Length == 0) return false;

        if (TryParseExplicit(t, out codePoint)) return true;

        var cps = Enumerate(t).ToList();
        if (cps.Count == 1)
        {
            codePoint = cps[0];
            return true;
        }

        if (t.Length is >= 2 and <= 6 && t.All(Uri.IsHexDigit) && int.TryParse(t, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int v) && v <= MaxValue)
        {
            codePoint = new CodePoint(v);
            return true;
        }

        return false;
    }

    /// <summary>Like <see cref="TryParse" />, but only for the written forms (<c>U+...</c>, <c>0x...</c>, escapes, entities), never a bare character or bare hex.</summary>
    public static bool TryParseExplicit(string? text, out CodePoint codePoint)
    {
        codePoint = default;
        if (string.IsNullOrEmpty(text)) return false;
        string t = text.Trim();

        // U+1F7E2 / u+1f7e2 / 0x1F7E2
        if (t.Length > 2 && (t.StartsWith("U+", StringComparison.OrdinalIgnoreCase) || t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)))
            return TryHex(t[2..], out codePoint);

        // &#128994; / &#x1F7E2;
        if (t.Length > 3 && t[0] == '&' && t[1] == '#' && t[^1] == ';')
        {
            string body = t[2..^1];
            if (body.Length > 1 && (body[0] == 'x' || body[0] == 'X')) return TryHex(body[1..], out codePoint);
            return int.TryParse(body, NumberStyles.None, CultureInfo.InvariantCulture, out int d) && d <= MaxValue && Make(d, out codePoint);
        }

        // \u{1F7E2} / \U0001F7E2 / 🟢 / ὾
        if (t.Length > 2 && t[0] == '\\' && (t[1] == 'u' || t[1] == 'U'))
        {
            string body = t[2..];
            if (body.Length > 2 && body[0] == '{' && body[^1] == '}') return TryHex(body[1..^1], out codePoint);

            int split = body.IndexOf("\\u", StringComparison.OrdinalIgnoreCase);
            if (split == 4 && TryHex(body[..4], out var hi) && TryHex(body[6..], out var lo) && char.IsHighSurrogate((char)hi.Value) && char.IsLowSurrogate((char)lo.Value))
            {
                codePoint = new CodePoint(char.ConvertToUtf32((char)hi.Value, (char)lo.Value));
                return true;
            }

            if (split < 0 && body.Length is 4 or 8) return TryHex(body, out codePoint);
        }

        return false;
    }

    /// <summary>Reads a list: every token explicit (<c>U+1F7E2 U+1F7E1</c>, separated by spaces, commas or semicolons), or else the characters of the text.</summary>
    public static IReadOnlyList<CodePoint> ParseList(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var tokens = text.Split(new[] { ' ', '\t', '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        var result = new List<CodePoint>(tokens.Length);
        foreach (string token in tokens)
        {
            if (!TryParseExplicit(token, out var cp)) return Enumerate(text.Trim()).ToList();
            result.Add(cp);
        }

        return result;
    }

    private static bool TryHex(string digits, out CodePoint cp)
    {
        cp = default;
        if (digits.Length is 0 or > 8) return false;
        return int.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int v) && Make(v, out cp);
    }

    private static bool Make(int value, out CodePoint cp)
    {
        cp = default;
        if ((uint)value > MaxValue) return false;
        cp = new CodePoint(value);
        return true;
    }
}

/// <summary>Writes a <see cref="CodePoint" /> as <c>"U+1F7E2"</c> and reads that (or a plain number) back.</summary>
public sealed class CodePointJsonConverter : JsonConverter<CodePoint>
{
    /// <inheritdoc />
    public override void WriteJson(JsonWriter writer, CodePoint value, JsonSerializer serializer) => writer.WriteValue(value.Hex);

    /// <inheritdoc />
    public override CodePoint ReadJson(JsonReader reader, Type objectType, CodePoint existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        switch (reader.TokenType)
        {
            case JsonToken.Integer when Convert.ToInt64(reader.Value, CultureInfo.InvariantCulture) is >= 0 and <= CodePoint.MaxValue and var n:
                return new CodePoint((int)n);
            case JsonToken.String when CodePoint.TryParse((string?)reader.Value, out var cp):
                return cp;
            default:
                throw new JsonSerializationException($"\"{reader.Value}\" is not a code point (write it like U+1F7E2).");
        }
    }
}
