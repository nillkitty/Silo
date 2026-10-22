using System.Text;
using System.Text.RegularExpressions;

namespace Silo.Labs.Regexes;

/// <summary>A regular expression being built: the pattern, how it is read, and the text it is tried on. This is what the Regex Lab studies and what is kept in the Silo.</summary>
public sealed class RegexDoc
{
    /// <summary>What it is called when it is kept (empty for one that is not).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The pattern, in .NET syntax.</summary>
    public string Pattern { get; set; } = string.Empty;

    /// <summary>The options that are on, as letters (see <see cref="RegexFlags" />): <c>i</c>, <c>m</c>, <c>s</c>, <c>n</c>, <c>x</c>, <c>r</c>, <c>e</c>, <c>c</c>, <c>b</c>.</summary>
    public string Flags { get; set; } = string.Empty;

    /// <summary>What it is for.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>The text it is tried on.</summary>
    public string TestText { get; set; } = string.Empty;

    /// <summary>What a match is replaced with (<c>$1</c>, <c>${name}</c>, <c>$&amp;</c>).</summary>
    public string Replacement { get; set; } = string.Empty;

    public RegexDoc Clone()
    {
        return new RegexDoc { Name = Name, Pattern = Pattern, Flags = Flags, Description = Description, TestText = TestText, Replacement = Replacement };
    }

    public override string ToString()
    {
        return RegexLiteral.Format(this);
    }
}

/// <summary>The options of a .NET regex and the letters they are written as (the letters JavaScript and Perl use, where those have the same option).</summary>
public static class RegexFlags
{
    public static readonly IReadOnlyList<(char Letter, string Name, RegexOptions Option, string Help)> All =
    [
        ('i', "IgnoreCase", RegexOptions.IgnoreCase, "Letters match in either case"),
        ('m', "Multiline", RegexOptions.Multiline, "^ and $ match at every line, not only the whole text"),
        ('s', "Singleline", RegexOptions.Singleline, ". matches a new line too"),
        ('n', "ExplicitCapture", RegexOptions.ExplicitCapture, "Only named groups capture"),
        ('x', "IgnorePatternWhitespace", RegexOptions.IgnorePatternWhitespace, "White space in the pattern is ignored, and # starts a comment"),
        ('r', "RightToLeft", RegexOptions.RightToLeft, "Search from the end of the text"),
        ('e', "ECMAScript", RegexOptions.ECMAScript, "JavaScript-like behaviour (only with i, m and c)"),
        ('c', "CultureInvariant", RegexOptions.CultureInvariant, "Ignore the language of the computer when comparing letters"),
        ('b', "NonBacktracking", RegexOptions.NonBacktracking, "Guaranteed linear time: no lookarounds, back-references or atomic groups")
    ];

    /// <summary>The options a set of letters stands for; unknown letters are skipped.</summary>
    public static RegexOptions Parse(string? flags)
    {
        var options = RegexOptions.None;
        foreach (char c in flags ?? string.Empty)
            foreach (var f in All)
                if (f.Letter == char.ToLowerInvariant(c))
                    options |= f.Option;

        return options;
    }

    /// <summary>The letters for a set of options, in the order of <see cref="All" />.</summary>
    public static string Format(RegexOptions options)
    {
        var text = new StringBuilder();
        foreach (var f in All)
            if ((options & f.Option) != 0)
                text.Append(f.Letter);

        return text.ToString();
    }

    /// <summary>Letters in the canonical order, each once, only the ones that are known.</summary>
    public static string Normalize(string? flags)
    {
        return Format(Parse(flags));
    }

    /// <summary>The first thing wrong with a set of options (the ones that cannot be used together), or null.</summary>
    public static string? Problem(string? flags)
    {
        var o = Parse(flags);
        if ((o & RegexOptions.ECMAScript) != 0 && (o & ~(RegexOptions.ECMAScript | RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)) != 0)
            return "ECMAScript can only be used with IgnoreCase, Multiline and CultureInvariant.";
        if ((o & RegexOptions.NonBacktracking) != 0 && (o & (RegexOptions.RightToLeft | RegexOptions.ECMAScript)) != 0)
            return "NonBacktracking cannot be used with RightToLeft or ECMAScript.";
        return null;
    }
}

/// <summary>The <c>/pattern/flags</c> way of writing a regex that most people know.</summary>
public static class RegexLiteral
{
    public static string Format(RegexDoc doc)
    {
        return "/" + doc.Pattern.Replace("/", "\\/") + "/" + RegexFlags.Normalize(doc.Flags);
    }

    /// <summary>Reads <c>/pattern/flags</c> (a slash inside the pattern is written <c>\/</c>). False when the text is not written that way.</summary>
    public static bool TryParse(string? text, out RegexDoc doc)
    {
        doc = new RegexDoc();
        if (string.IsNullOrEmpty(text) || text.Length < 2 || text[0] != '/') return false;

        var pattern = new StringBuilder();
        int i       = 1;
        for (; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < text.Length)
            {
                if (text[i + 1] == '/')
                {
                    pattern.Append('/');
                    i++;
                    continue;
                }

                pattern.Append(c).Append(text[i + 1]);
                i++;
                continue;
            }

            if (c == '/') break;
            pattern.Append(c);
        }

        if (i >= text.Length) return false;

        string flags = text[(i + 1)..];
        if (flags.Any(f => !RegexFlags.All.Any(a => a.Letter == f))) return false;

        doc = new RegexDoc { Pattern = pattern.ToString(), Flags = RegexFlags.Normalize(flags) };
        return true;
    }
}
