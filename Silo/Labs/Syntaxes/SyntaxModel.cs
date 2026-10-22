using Silo.Providers.Data;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Silo.DbModel.Meta;
using Silo.Providers.Definitions;

namespace Silo.Labs.Syntaxes;

/// <summary>A syntax being edited: what a <see cref="SyntaxRecord" /> holds, but changeable.</summary>
public sealed class SyntaxDraft
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Alias { get; set; }

    public string? Description { get; set; }

    public SyntaxKind Kind { get; set; } = SyntaxKind.Pattern;

    /// <summary>The key of the built-in parser (<see cref="SyntaxParsers" />), for the Parser kind.</summary>
    public string? Parser { get; set; }

    /// <summary>The regular expression the whole text has to match, for the Pattern kind.</summary>
    public string? Pattern { get; set; }

    public string? Example { get; set; }

    public SyntaxFlags Flags { get; set; }

    public static SyntaxDraft From(SyntaxRecord record)
    {
        return new SyntaxDraft
               {
                   Id = record.Id, Name = record.Name, Alias = record.Alias, Description = record.Description, Kind = record.Kind, Parser = record.Parser,
                   Pattern = record.Pattern, Example = record.Example, Flags = record.Flags
               };
    }

    public SyntaxRecord ToRecord()
    {
        return new SyntaxRecord(Id, Name ?? string.Empty, Alias, Description, Kind, Parser, Pattern, Example, Flags);
    }

    public SyntaxDraft Clone()
    {
        return (SyntaxDraft)MemberwiseClone();
    }
}

/// <summary>The strings a syntax is tried on, one to a line in each list, kept with the syntax.</summary>
public sealed class SyntaxBook
{
    /// <summary>Strings that have to be valid.</summary>
    public string Valid { get; set; } = string.Empty;

    /// <summary>Strings that must not be valid.</summary>
    public string Invalid { get; set; } = string.Empty;

    /// <summary>Strings to look at, with no opinion about them.</summary>
    public string Try { get; set; } = string.Empty;

    /// <summary>Whether <c>\n</c>, <c>\t</c> and <c>\\</c> in a line stand for a new line, a tab and a backslash (so a sample can have several lines).</summary>
    public bool Escapes { get; set; } = true;

    public bool IsEmpty => Valid.Length == 0 && Invalid.Length == 0 && Try.Length == 0;
}

public enum TrialExpect
{
    Nothing,
    Valid,
    Invalid
}

/// <summary>What one string came to under a syntax.</summary>
/// <param name="Text">The string, as it was tried (escapes already read).</param>
/// <param name="Value">What it became, as text.</param>
/// <param name="TypeName">The type of what it became.</param>
/// <param name="Canonical">How the syntax writes the value back.</param>
/// <param name="RoundTrips">Whether the canonical text is valid and becomes the same value.</param>
public sealed record TrialResult(string Text, TrialExpect Expect, bool IsValid, string? Error, string? TypeName, string? Value, string? Canonical, bool RoundTrips)
{
    /// <summary>Whether the string came out the way it was expected to.</summary>
    public bool Passed => Expect switch
                          {
                              TrialExpect.Valid   => IsValid,
                              TrialExpect.Invalid => !IsValid,
                              _                   => true
                          };
}

/// <summary>What can be made of a draft: the syntax, or everything that is wrong with it.</summary>
public sealed record SyntaxBuild(StoredSyntax? Syntax, IReadOnlyList<string> Problems)
{
    public bool IsBuilt => Syntax is not null;
}

/// <summary>Builds the syntax of a draft and tries strings on it, with the Silo's own code (<see cref="StoredSyntax" />, <see cref="SyntaxRules" />).</summary>
public static class SyntaxTrial
{
    /// <summary>
    ///     Builds the syntax. <paramref name="others" /> are the syntaxes already in the Silo (the one with the draft's own id is not counted), so
    ///     a name that is taken is reported with the other problems.
    /// </summary>
    public static SyntaxBuild Build(SyntaxDraft draft, IEnumerable<SyntaxRecord>? others = null)
    {
        var record = SyntaxRules.Normalize(draft.ToRecord());
        var rest   = (others ?? []).Where(o => draft.Id == 0 || o.Id != draft.Id).ToList();
        var problems = new List<string>();

        if (!StoredSyntax.TryCreate(record, out var syntax, out string? error))
        {
            problems.Add(error ?? "The syntax cannot be built.");
            // name problems are still worth showing
            problems.AddRange(SyntaxRules.Validate(record, rest).Where(p => p != error && !p.StartsWith("The example is not valid", StringComparison.Ordinal) && p != "The syntax cannot be built."));
            return new SyntaxBuild(null, problems.Distinct().ToList());
        }

        problems.AddRange(SyntaxRules.Validate(record, rest));
        return new SyntaxBuild(syntax, problems);
    }

    public static IReadOnlyList<string> Lines(string? text, bool escapes)
    {
        if (string.IsNullOrEmpty(text)) return [];

        return text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).Select(l => escapes ? Unescape(l) : l).ToList();
    }

    /// <summary>Reads <c>\n</c>, <c>\r</c>, <c>\t</c> and <c>\\</c>; any other backslash stays as it is.</summary>
    public static string Unescape(string text)
    {
        if (!text.Contains('\\')) return text;

        var result = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                switch (text[i + 1])
                {
                    case 'n':
                        result.Append('\n');
                        i++;
                        continue;
                    case 'r':
                        result.Append('\r');
                        i++;
                        continue;
                    case 't':
                        result.Append('\t');
                        i++;
                        continue;
                    case '\\':
                        result.Append('\\');
                        i++;
                        continue;
                }
            }

            result.Append(text[i]);
        }

        return result.ToString();
    }

    /// <summary>The opposite of <see cref="Unescape" />: new lines and tabs written so a result fits on one line.</summary>
    public static string Escape(string text)
    {
        return text.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
    }

    public static TrialResult Run(StoredSyntax syntax, string text, TrialExpect expect = TrialExpect.Nothing)
    {
        if (!syntax.TryParse(text, out object? value, out string? error)) return new TrialResult(text, expect, false, error ?? "Not valid.", null, null, null, false);

        string shown = Describe(value);
        string? canonical = null;
        bool roundTrips = false;
        try
        {
            canonical = syntax.GetString(value!);
            roundTrips = syntax.TryParse(canonical, out object? again, out _) && Describe(again) == shown;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidCastException)
        {
            canonical = null;
        }

        return new TrialResult(text, expect, true, null, TypeNameOf(value), shown, canonical, roundTrips);
    }

    public static IReadOnlyList<TrialResult> RunAll(StoredSyntax syntax, SyntaxBook book)
    {
        var results = new List<TrialResult>();
        foreach (string s in Lines(book.Valid, book.Escapes)) results.Add(Run(syntax, s, TrialExpect.Valid));
        foreach (string s in Lines(book.Invalid, book.Escapes)) results.Add(Run(syntax, s, TrialExpect.Invalid));
        foreach (string s in Lines(book.Try, book.Escapes)) results.Add(Run(syntax, s));
        return results;
    }

    public static string TypeNameOf(object? value)
    {
        return value switch
               {
                   null            => "null",
                   byte[]          => "byte[]",
                   string[][]      => "string[][]",
                   string[]        => "string[]",
                   _               => value.GetType().Name
               };
    }

    /// <summary>A value as one line of text, to see what a syntax made of the string.</summary>
    public static string Describe(object? value)
    {
        switch (value)
        {
            case null:
                return "(null)";
            case string s:
                return s;
            case byte[] bytes:
                return $"{bytes.Length} byte(s): {Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 24)))}{(bytes.Length > 24 ? "…" : string.Empty)}";
            case JToken token:
                return token.ToString(Newtonsoft.Json.Formatting.None);
            case string[][] rows:
                return $"{rows.Length} row(s): " + string.Join(" ; ", rows.Take(5).Select(r => "[" + string.Join(", ", r) + "]")) + (rows.Length > 5 ? " …" : string.Empty);
            case string[] words:
                return string.Join(" | ", words.Select(w => "\"" + w + "\""));
            case DateTimeOffset when_:
                return when_.ToString("O", CultureInfo.InvariantCulture);
            case TimeSpan span:
                return span.ToString("c", CultureInfo.InvariantCulture);
            case Regex regex:
                return regex.ToString();
            case IPAddress ip:
                return ip.ToString();
            default:
                return value.ToString() ?? value.GetType().Name;
        }
    }

    /// <summary>The syntax as a JSON document (what the Silo keeps, written out).</summary>
    public static string ToJson(SyntaxDraft draft)
    {
        return Newtonsoft.Json.JsonConvert.SerializeObject(new
                                                           {
                                                               draft.Name, draft.Alias, draft.Description, Kind = draft.Kind.ToString(), draft.Parser, draft.Pattern, draft.Example
                                                           }, Newtonsoft.Json.Formatting.Indented);
    }
}
