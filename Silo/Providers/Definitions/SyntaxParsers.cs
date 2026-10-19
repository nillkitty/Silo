using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Silo.Providers.Definitions;

/// <summary>Reads a string into a value of one type. Returns false, with a message fit to show, when the text is not valid.</summary>
public delegate bool SyntaxParse<T>(string text, out T value, out string? error);

/// <summary>
///     One of the parsers built into Silo: it says whether a string is valid, turns it into an object (<see cref="ValueType" />) and turns such an
///     object back into the one canonical string stored for it. A defined syntax (<c>MvSyntax</c>) of kind Parser picks one by <see cref="Key" />.
/// </summary>
public sealed class SyntaxParser
{
    private readonly Func<string, (bool Ok, object? Value, string? Error)> _parse;
    private readonly Func<object, string>                                 _format;

    private SyntaxParser(string key, string title, string description, Type valueType, string example, Func<string, (bool, object?, string?)> parse, Func<object, string> format)
    {
        Key         = key;
        Title       = title;
        Description = description;
        ValueType   = valueType;
        Example     = example;
        _parse      = parse;
        _format     = format;
    }

    /// <summary>The key a syntax names it by (lower case, no spaces): <c>timestamp</c>, <c>guid</c> ...</summary>
    public string Key { get; }

    /// <summary>What it is called on screen.</summary>
    public string Title { get; }

    /// <summary>What it accepts, in a sentence.</summary>
    public string Description { get; }

    /// <summary>The type of object a valid string becomes.</summary>
    public Type ValueType { get; }

    /// <summary>A sample of a valid string.</summary>
    public string Example { get; }

    public static SyntaxParser Create<T>(string key, string title, string description, string example, SyntaxParse<T> parse, Func<T, string> format)
    {
        return new SyntaxParser(key, title, description, typeof(T), example,
                                text =>
                                {
                                    bool ok = parse(text, out T value, out string? error);
                                    return (ok, ok ? value : default, error);
                                },
                                value => value is T typed ? format(typed) : throw new ArgumentException($"A {typeof(T).Name} is expected, not a {value?.GetType().Name ?? "null"}.", nameof(value)));
    }

    /// <summary>Reads the text. A null text is never valid.</summary>
    public bool TryParse(string? text, out object? value, out string? error)
    {
        if (text is null)
        {
            value = null;
            error = "There is no value.";
            return false;
        }

        (bool ok, value, error) = _parse(text);
        return ok;
    }

    /// <summary>The canonical string for a value of <see cref="ValueType" />.</summary>
    public string Format(object value) => _format(value);
}

/// <summary>The parsers built into Silo, by key.</summary>
public static class SyntaxParsers
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    private static readonly string[] TimestampFormats = BuildTimestampFormats();

    private static readonly Regex IsoDuration = new(@"\A(-)?P(?:(\d+)W)?(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+(?:[.,]\d+)?)S)?)?\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, MatchTimeout);
    private static readonly Regex Ipv4        = new(@"\A\d{1,3}(\.\d{1,3}){3}\z", RegexOptions.CultureInvariant, MatchTimeout);
    private static readonly Regex OidShape    = new(@"\A[0-2](\.(0|[1-9]\d*))+\z", RegexOptions.CultureInvariant, MatchTimeout);
    private static readonly Regex SidShape    = new(@"\AS-1-\d+(-\d+){1,15}\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, MatchTimeout);

    /// <summary>Every parser, in the order they are offered.</summary>
    public static IReadOnlyList<SyntaxParser> All { get; } = Build();

    /// <summary>The parser with that key (any case), or null.</summary>
    public static SyntaxParser? Find(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        string k = key.Trim();
        return All.FirstOrDefault(p => string.Equals(p.Key, k, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<SyntaxParser> Build() =>
    [
        SyntaxParser.Create<DateTimeOffset>("timestamp", "Timestamp", "A date and time: ISO 8601 (2026-10-16T14:30:00Z, a date alone, or with a time zone offset) or RFC 1123. Without an offset the time is taken as UTC.",
                                            "2026-10-16T14:30:00Z", ParseTimestamp, FormatTimestamp),
        SyntaxParser.Create<TimeSpan>("duration", "Duration", "A length of time: ISO 8601 (PT1H30M, P1DT2H, P2W) or clock style (1:30:00, 1.02:03:04).",
                                      "PT1H30M", ParseDuration, v => v.ToString("c", CultureInfo.InvariantCulture)),
        SyntaxParser.Create<Guid>("guid", "Guid", "A globally unique identifier, with or without braces and dashes.",
                                  "3f2504e0-4f89-11d3-9a0c-0305e82c3301", ParseGuid, v => v.ToString("D", CultureInfo.InvariantCulture)),
        SyntaxParser.Create<Regex>("regex", "Regex", "A regular expression (.NET flavour).",
                                   @"^\d{3}-\d{4}$", ParseRegex, v => v.ToString()),
        SyntaxParser.Create<Wildcard>("wildcard", "Wildcard", "A pattern where * stands for any run of characters and ? for any one character.",
                                      "*.log", ParseWildcard, v => v.Pattern),
        SyntaxParser.Create<IPAddress>("ipaddress", "IPAddress", "An IPv4 address (four numbers 0 to 255) or an IPv6 address.",
                                       "192.168.1.10", ParseIPAddress, v => v.ToString()),
        SyntaxParser.Create<IPSubnet>("ipsubnet", "IPSubnet", "An address and a prefix length: 10.0.0.0/8 or fe80::/10.",
                                      "10.0.0.0/8", ParseIPSubnet, v => v.ToString()),
        SyntaxParser.Create<Version>("version", "Version", "Two to four numbers separated by dots: 1.2, 1.2.3, 1.2.3.4.",
                                     "1.2.3.4", ParseVersion, v => v.ToString()),
        SyntaxParser.Create<Oid>("oid", "Oid", "An object identifier in dotted decimal form: at least two numbers, the first 0, 1 or 2 (and the second 39 or less when the first is 0 or 1).",
                                 "1.3.6.1.4.1.311", ParseOid, v => v.Value ?? string.Empty),
        SyntaxParser.Create<SecurityIdentifier>("securityidentifier", "SecurityIdentifier", "A Windows security identifier written S-1-...",
                                                "S-1-5-32-544", ParseSid, v => v.Value),
        SyntaxParser.Create<JObject>("jobj", "JSON object", "A JSON object: {\"name\": \"value\"}.",
                                     "{\"name\":\"Silo\"}", (string t, out JObject v, out string? e) => ParseJson(t, JTokenType.Object, out v, out e), v => v.ToString(Formatting.None)),
        SyntaxParser.Create<JArray>("jarr", "JSON array", "A JSON array: [1, 2, 3].",
                                    "[1,2,3]", (string t, out JArray v, out string? e) => ParseJson(t, JTokenType.Array, out v, out e), v => v.ToString(Formatting.None)),
        SyntaxParser.Create<JToken>("jtok", "JSON value", "Any JSON value: an object, an array, a string, a number, true, false or null.",
                                    "{\"a\":[1,2]}", (string t, out JToken v, out string? e) => ParseJson(t, null, out v, out e), v => v.ToString(Formatting.None)),
        SyntaxParser.Create<byte[]>("base64", "Base64", "Bytes written in standard Base64 (A-Z a-z 0-9 + /, padded with =).",
                                    "SGVsbG8sIFNpbG8h", ParseBase64, Convert.ToBase64String),
        SyntaxParser.Create<string>("html", "Html", "Markup text. Any text is accepted; it is stored as written.",
                                    "<p>Hello, <b>Silo</b></p>", ParseAnyText, v => v),
        SyntaxParser.Create<string[][]>("csv", "Csv", "Comma separated values: rows on lines, fields in double quotes when they hold a comma, a quote or a line break.",
                                        "name,count\nsilo,3", (string t, out string[][] v, out string? e) => DelimitedText.TryParse(t, DelimitedText.Comma, out v, out e),
                                        v => DelimitedText.Format(v, DelimitedText.Comma)),
        SyntaxParser.Create<string[][]>("tsv", "Tsv", "Tab separated values: rows on lines, fields in double quotes when they hold a tab, a quote or a line break.",
                                        "name\tcount\nsilo\t3", (string t, out string[][] v, out string? e) => DelimitedText.TryParse(t, DelimitedText.Tab, out v, out e),
                                        v => DelimitedText.Format(v, DelimitedText.Tab)),
        SyntaxParser.Create<string[]>("cliargs", "CliArgs", "Command line arguments, split the way Windows does: spaces separate, double quotes group.",
                                      "--input \"my file.txt\" -v", (string t, out string[] v, out string? e) => CliText.TrySplit(t, out v, out e), CliText.Join),
        SyntaxParser.Create<CliCommand>("clicommand", "CliCommand", "A command and its arguments: the first word (or quoted group) is the program.",
                                        "git commit -m \"first commit\"", ParseCliCommand, v => v.ToString())
    ];

    // ---- timestamp -----------------------------------------------------------------------------------------------

    private static string[] BuildTimestampFormats()
    {
        var formats = new List<string> { "yyyy-MM-dd", "r" };
        foreach (string separator in new[] { "'T'", " " })
        foreach (string time in new[] { "HH:mm", "HH:mm:ss", "HH:mm:ss.FFFFFFF" })
        {
            formats.Add($"yyyy-MM-dd{separator}{time}");
            formats.Add($"yyyy-MM-dd{separator}{time}K");
        }

        return formats.ToArray();
    }

    private static bool ParseTimestamp(string text, out DateTimeOffset value, out string? error)
    {
        error = null;
        if (DateTimeOffset.TryParseExact(text.Trim(), TimestampFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out value)) return true;

        error = "Not a date and time. Use ISO 8601, for example 2026-10-16T14:30:00Z or 2026-10-16.";
        return false;
    }

    private static string FormatTimestamp(DateTimeOffset value)
    {
        return value.Offset == TimeSpan.Zero
                   ? value.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture).Replace(".Z", "Z")
                   : value.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture).Replace(".+", "+").Replace(".-", "-");
    }

    // ---- duration ------------------------------------------------------------------------------------------------

    private static bool ParseDuration(string text, out TimeSpan value, out string? error)
    {
        value = default;
        string t = text.Trim();
        error = "Not a duration. Use ISO 8601 (PT1H30M, P1DT2H) or clock style (1:30:00).";

        if (t.Length == 0) return false;

        if (t.StartsWith('P') || t.StartsWith("-P", StringComparison.OrdinalIgnoreCase) || t.StartsWith('p'))
        {
            var m = IsoDuration.Match(t);
            if (!m.Success || t.EndsWith('T') || t.EndsWith('P') || (m.Groups[2].Length + m.Groups[3].Length + m.Groups[4].Length + m.Groups[5].Length + m.Groups[6].Length) == 0)
            {
                error = "Not an ISO 8601 duration. Years and months have no fixed length, so use weeks, days, hours, minutes and seconds: P1DT2H30M.";
                return false;
            }

            try
            {
                decimal ticks = 0;
                if (m.Groups[2].Success) ticks += decimal.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 7 * TimeSpan.TicksPerDay;
                if (m.Groups[3].Success) ticks += decimal.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) * TimeSpan.TicksPerDay;
                if (m.Groups[4].Success) ticks += decimal.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) * TimeSpan.TicksPerHour;
                if (m.Groups[5].Success) ticks += decimal.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture) * TimeSpan.TicksPerMinute;
                if (m.Groups[6].Success) ticks += decimal.Parse(m.Groups[6].Value.Replace(',', '.'), CultureInfo.InvariantCulture) * TimeSpan.TicksPerSecond;
                if (m.Groups[1].Success) ticks = -ticks;
                value = new TimeSpan(decimal.ToInt64(decimal.Truncate(ticks)));
                error = null;
                return true;
            }
            catch (Exception ex) when (ex is OverflowException or ArgumentOutOfRangeException or FormatException)
            {
                error = "That duration is too long.";
                return false;
            }
        }

        if (t.Contains(':') && TimeSpan.TryParse(t, CultureInfo.InvariantCulture, out value))
        {
            error = null;
            return true;
        }

        return false;
    }

    // ---- guid, regex, wildcard ------------------------------------------------------------------------------------

    private static bool ParseGuid(string text, out Guid value, out string? error)
    {
        error = null;
        if (Guid.TryParse(text.Trim(), out value)) return true;

        error = "Not a guid. Use 32 hex digits in the form 3f2504e0-4f89-11d3-9a0c-0305e82c3301.";
        return false;
    }

    private static bool ParseRegex(string text, out Regex value, out string? error)
    {
        error = null;
        try
        {
            value = new Regex(text, RegexOptions.CultureInvariant, MatchTimeout);
            return true;
        }
        catch (ArgumentException ex)
        {
            value = null!;
            error = "Not a valid regular expression: " + ex.Message;
            return false;
        }
    }

    private static bool ParseWildcard(string text, out Wildcard value, out string? error)
    {
        error = null;
        if (text.Length > 0)
        {
            value = new Wildcard(text);
            return true;
        }

        value = null!;
        error = "A wildcard pattern cannot be empty.";
        return false;
    }

    // ---- addresses, versions, identifiers --------------------------------------------------------------------------

    private static bool ParseIPAddress(string text, out IPAddress value, out string? error)
    {
        string t = text.Trim();
        error = "Not an IP address. Use four numbers 0 to 255 (192.168.1.10) or an IPv6 address (fe80::1).";
        value = null!;

        if (t.Length == 0) return false;

        bool v4 = t.IndexOf(':') < 0;
        if (v4 && !Ipv4.IsMatch(t)) return false;
        if (t.Contains('[') || t.Contains(']') || t.Contains('/')) return false;

        if (!IPAddress.TryParse(t, out var parsed) || parsed.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)) return false;

        value = parsed;
        error = null;
        return true;
    }

    private static bool ParseIPSubnet(string text, out IPSubnet value, out string? error)
    {
        value = null!;
        string t = text.Trim();
        error = "Not a subnet. Write an address, a slash and a prefix length: 10.0.0.0/8 or fe80::/10.";

        int slash = t.IndexOf('/');
        if (slash <= 0 || slash == t.Length - 1) return false;

        if (!ParseIPAddress(t[..slash], out var address, out _) || t[..slash].Contains('%')) return false;
        if (!int.TryParse(t[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int prefix)) return false;

        int max = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        if (prefix > max)
        {
            error = $"The prefix length is {prefix}, but an address of this kind has {max} bits.";
            return false;
        }

        value = new IPSubnet(address, prefix);
        error = null;
        return true;
    }

    private static bool ParseVersion(string text, out Version value, out string? error)
    {
        error = null;
        if (Version.TryParse(text.Trim(), out var parsed)) // two to four numbers
        {
            value = parsed;
            return true;
        }

        value = null!;
        error = "Not a version. Use two to four numbers separated by dots: 1.2, 1.2.3, 1.2.3.4.";
        return false;
    }

    private static bool ParseOid(string text, out Oid value, out string? error)
    {
        value = null!;
        string t = text.Trim();
        error = "Not an object identifier. Use dotted decimal with at least two numbers, such as 1.3.6.1.4.1.311.";

        if (!OidShape.IsMatch(t)) return false;

        string[] arcs = t.Split('.');
        if (arcs[0] != "2" && (arcs[1].Length > 2 || int.Parse(arcs[1], CultureInfo.InvariantCulture) > 39))
        {
            error = "The second number of an identifier starting with 0 or 1 cannot be more than 39.";
            return false;
        }

        value = new Oid(t);
        error = null;
        return true;
    }

    private static bool ParseSid(string text, out SecurityIdentifier value, out string? error)
    {
        value = null!;
        string t = text.Trim();
        error = "Not a security identifier. Write it as S-1-5-32-544.";

        if (!SidShape.IsMatch(t)) return false;

        try
        {
            value = new SecurityIdentifier(t.ToUpperInvariant());
            error = null;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    // ---- json, base64, text ----------------------------------------------------------------------------------------

    private static bool ParseJson<T>(string text, JTokenType? required, out T value, out string? error) where T : JToken
    {
        value = null!;
        try
        {
            // System.Text.Json is the strict reader (no comments, no single quotes, no bare names); Newtonsoft then builds the tree
            using (System.Text.Json.JsonDocument.Parse(text)) { }

            var token = JToken.Parse(text);
            if (required is { } type && token.Type != type)
            {
                error = $"This is JSON, but a {token.Type.ToString().ToLowerInvariant()}; {(type == JTokenType.Object ? "an object" : "an array")} is needed.";
                return false;
            }

            value = (T)token;
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is JsonReaderException or System.Text.Json.JsonException)
        {
            error = "Not valid JSON: " + ex.Message;
            return false;
        }
    }

    private static bool ParseBase64(string text, out byte[] value, out string? error)
    {
        try
        {
            value = Convert.FromBase64String(text.Trim());
            error = null;
            return true;
        }
        catch (FormatException)
        {
            value = [];
            error = "Not Base64. Use A-Z, a-z, 0-9, + and /, padded with = to a multiple of four characters.";
            return false;
        }
    }

    private static bool ParseAnyText(string text, out string value, out string? error)
    {
        value = text;
        error = null;
        return true;
    }

    private static bool ParseCliCommand(string text, out CliCommand value, out string? error)
    {
        value = null!;
        if (!CliText.TrySplit(text, out string[] words, out error)) return false;

        if (words.Length == 0 || words[0].Length == 0)
        {
            error = "A command is needed: the program, then its arguments.";
            return false;
        }

        value = new CliCommand(words[0], words[1..]);
        return true;
    }
}
