using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace Silo.Labs.Parser;

/// <summary>A value a <c>named</c> node labelled.</summary>
public sealed record NamedValue(string Name, object? Value)
{
    public override string ToString() => $"{Name}={SpecValues.Format(Value)}";
}

/// <summary>
///     What the values a parse produces are made of and how they are changed: flattened, converted (<c>convert</c> nodes, a rule's declared return
///     type), tested (<c>where</c> nodes) and shown. A value is a string (what a token matched), an array (a sequence or a repetition), a
///     <see cref="NamedValue" />, or what a conversion made of one of those.
/// </summary>
public static class SpecValues
{
    /// <summary>The leaves of a value: strings and converted things, the nesting taken out.</summary>
    public static IEnumerable<object?> Flatten(object? value)
    {
        switch (value)
        {
            case null:
                yield break;
            case NamedValue named:
                foreach (var leaf in Flatten(named.Value)) yield return leaf;
                yield break;
            case string:
            case byte[]:
                yield return value;
                yield break;
            case System.Collections.IEnumerable items:
                foreach (var item in items)
                foreach (var leaf in Flatten(item))
                    yield return leaf;
                yield break;
            default:
                yield return value;
                yield break;
        }
    }

    /// <summary>The text of a value: every string in it, joined.</summary>
    public static string Text(object? value)
    {
        var sb = new StringBuilder();
        foreach (var leaf in Flatten(value)) sb.Append(leaf is byte[] bytes ? System.Convert.ToHexString(bytes) : System.Convert.ToString(leaf, CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    /// <summary>The value changed as the conversion named <paramref name="to" /> says (see <see cref="SpecCatalog.Conversions" />).</summary>
    /// <exception cref="FormatException">The text is not a value of that kind.</exception>
    public static object? Convert(string to, object? value)
    {
        string text = Text(value);
        switch (to.Trim().ToLowerInvariant())
        {
            case "none":
                return null;
            case "text":
            case "string":
                return text;
            case "int":
                return int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
            case "long":
                return long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
            case "decimal":
                return decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
            case "bool":
                return bool.Parse(text);
            case "guid":
                return Guid.Parse(text);
            case "hex-int":
                return int.Parse(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            case "byte-hex":
                return byte.Parse(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            case "bytes-hex":
                return System.Convert.FromHexString(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text);
            default:
                throw new FormatException($"There is no conversion called '{to}'.");
        }
    }

    /// <summary>
    ///     Shapes a rule's value into the CLR type it declares ("byte[6]", "int", "string"...). Null or empty <paramref name="type" /> leaves the
    ///     value as it is.
    /// </summary>
    /// <exception cref="FormatException">The value cannot be that type.</exception>
    public static object? Coerce(string? type, object? value)
    {
        if (string.IsNullOrWhiteSpace(type)) return value;

        type = type.Trim();
        var array = Regex.Match(type, @"^(?<t>[\w.]+)\[(?<n>\d*)\]$");
        if (array.Success)
        {
            string element = array.Groups["t"].Value.ToLowerInvariant();
            int?   count   = array.Groups["n"].Value.Length > 0 ? int.Parse(array.Groups["n"].Value) : null;

            if (element is "byte" or "system.byte")
            {
                var bytes = new List<byte>();
                foreach (var leaf in Flatten(value))
                    switch (leaf)
                    {
                        case byte b:
                            bytes.Add(b);
                            break;
                        case byte[] more:
                            bytes.AddRange(more);
                            break;
                        case string s when s.Length > 0:
                            bytes.AddRange(System.Convert.FromHexString(s.Length % 2 == 1 ? "0" + s : s));
                            break;
                        case IConvertible c:
                            bytes.Add(c.ToByte(CultureInfo.InvariantCulture));
                            break;
                        default:
                            throw new FormatException($"'{leaf}' is not a byte.");
                    }

                if (count is { } n && bytes.Count != n) throw new FormatException($"{type} needs {n} byte(s), the text gave {bytes.Count}.");
                return bytes.ToArray();
            }

            var items = Flatten(value).ToArray();
            if (count is { } want && items.Length != want) throw new FormatException($"{type} needs {want} item(s), the text gave {items.Length}.");
            return items;
        }

        // already the thing the rule says it makes (a convert node did it): leave it alone
        var only = Flatten(value).Take(2).ToArray();
        if (only.Length == 1 && only[0] is { } one && Is(type, one)) return one;

        return type.ToLowerInvariant() switch
               {
                   "string" or "system.string" => Text(value),
                   "int" or "int32" or "system.int32" => Convert("int", value),
                   "long" or "int64" or "system.int64" => Convert("long", value),
                   "decimal" or "system.decimal" => Convert("decimal", value),
                   "bool" or "boolean" or "system.boolean" => Convert("bool", value),
                   "guid" or "system.guid" => Convert("guid", value),
                   "byte" or "system.byte" => Convert("byte-hex", value),
                   "datetime" or "system.datetime" => DateTime.Parse(Text(value), CultureInfo.InvariantCulture),
                   "object" or "system.object" => value,
                   _ => throw new FormatException($"Unknown return type '{type}'.")
               };
    }

    private static bool Is(string type, object value)
    {
        return type.ToLowerInvariant() switch
               {
                   "string" or "system.string"                 => value is string,
                   "int" or "int32" or "system.int32"          => value is int,
                   "long" or "int64" or "system.int64"         => value is long,
                   "decimal" or "system.decimal"               => value is decimal,
                   "bool" or "boolean" or "system.boolean"     => value is bool,
                   "guid" or "system.guid"                     => value is Guid,
                   "byte" or "system.byte"                     => value is byte,
                   "datetime" or "system.datetime"             => value is DateTime,
                   _                                           => false
               };
    }

    /// <summary>A test a <c>where</c> node applies to the text of its child ("len:2-4", "range:0..255", "regex:^[a-z]+$"); null when the text is not one.</summary>
    public static Func<string, bool>? Predicate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        int colon = text.IndexOf(':');
        if (colon < 0) return null;

        string kind = text[..colon].Trim().ToLowerInvariant();
        string arg  = text[(colon + 1)..].Trim();

        switch (kind)
        {
            case "len":
            {
                var parts = arg.Split('-', 2);
                int min   = int.TryParse(parts[0], out var a) ? a : 0;
                int? max  = parts.Length > 1 && int.TryParse(parts[1], out var b) ? b : null;
                return s => s.Length >= min && (max is null || s.Length <= max);
            }
            case "range":
            {
                var parts = arg.Split("..", 2, StringSplitOptions.TrimEntries);
                if (parts.Length != 2 || !long.TryParse(parts[0], out var lo) || !long.TryParse(parts[1], out var hi)) return null;
                return s => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= lo && n <= hi;
            }
            case "regex":
            {
                try
                {
                    var regex = new Regex(arg, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                    return s => regex.IsMatch(s);
                }
                catch (ArgumentException)
                {
                    return null;
                }
            }
            default:
                return null;
        }
    }

    /// <summary>A value as one line of text for the terminal and the lists.</summary>
    public static string Format(object? value)
    {
        switch (value)
        {
            case null:
                return "(nothing)";
            case string s:
                return "\"" + s + "\"";
            case byte[] bytes:
                return "byte[" + bytes.Length + "] { " + string.Join(" ", bytes.Select(b => b.ToString("X2"))) + " }";
            case NamedValue named:
                return named.ToString();
            case System.Collections.IEnumerable items:
                return "[" + string.Join(", ", items.Cast<object?>().Select(Format)) + "]";
            case IFormattable f:
                return f.ToString(null, CultureInfo.InvariantCulture);
            default:
                return value.ToString() ?? string.Empty;
        }
    }

    /// <summary>
    ///     A value as one of the formats a spec says it converts to: <c>hex</c>, <c>hex-colon</c>, <c>hex-dash</c>, <c>hex-dotted4</c> (groups of
    ///     four digits, as Cisco writes a MAC address), <c>base64</c>, <c>json</c>, <c>text</c>.
    /// </summary>
    /// <exception cref="FormatException">There is no such format.</exception>
    public static string ConvertTo(string format, object? value)
    {
        byte[] Bytes() => value as byte[] ?? Coerce("byte[]", value) as byte[] ?? [];

        switch (format.Trim().ToLowerInvariant())
        {
            case "text":
                return Text(value);
            case "hex":
                return System.Convert.ToHexString(Bytes()).ToLowerInvariant();
            case "hex-colon":
                return string.Join(":", Bytes().Select(b => b.ToString("x2")));
            case "hex-dash":
                return string.Join("-", Bytes().Select(b => b.ToString("x2")));
            case "hex-dotted4":
            {
                string hex = System.Convert.ToHexString(Bytes()).ToLowerInvariant();
                return string.Join(".", Enumerable.Range(0, (hex.Length + 3) / 4).Select(i => hex.Substring(i * 4, Math.Min(4, hex.Length - i * 4))));
            }
            case "base64":
                return System.Convert.ToBase64String(Bytes());
            case "json":
                return JsonConvert.SerializeObject(value is byte[] b ? b : Plain(value));
            default:
                throw new FormatException($"There is no format called '{format}'. Formats: hex, hex-colon, hex-dash, hex-dotted4, base64, json, text.");
        }
    }

    /// <summary>The value with named things replaced by their parts, for JSON.</summary>
    private static object? Plain(object? value)
    {
        return value switch
               {
                   NamedValue n => new Dictionary<string, object?> { [n.Name] = Plain(n.Value) },
                   string or byte[] => value,
                   System.Collections.IEnumerable items => items.Cast<object?>().Select(Plain).ToList(),
                   _ => value
               };
    }
}
