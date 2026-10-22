using System.Text.RegularExpressions;
using System.Text;

namespace Silo.Labs.Regexes;

/// <summary>A well-known pattern to start from.</summary>
public sealed record LibraryEntry(string Name, string Pattern, string Flags, string Description, string Sample);

/// <summary>The patterns the Lab comes with, and the cheat sheet of the syntax it lists.</summary>
public static class RegexLibrary
{
    public static readonly IReadOnlyList<LibraryEntry> Entries =
    [
        new("Integer", @"[+-]?\d+", "", "A whole number with an optional sign", "7, -42, +100, 3.14"),
        new("Decimal number", @"[+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?", "", "A number with a fraction or an exponent", "1.5 -0.25 .5 6e23 42"),
        new("Hex number", @"\b(?:0[xX])?[0-9a-fA-F]+\b", "", "Hexadecimal digits, with or without 0x", "0xFF 1a2B dead"),
        new("Email address", @"[\w.+-]+@[\w-]+(?:\.[\w-]+)+", "", "A simple e-mail address (not every address the standards allow)", "write to ann@example.com or bob.smith+x@mail.example.org"),
        new("URL (http/https)", @"https?://[^\s/$.?#].[^\s]*", "i", "A web address", "see https://example.com/a?b=1#c and http://localhost:8080"),
        new("IPv4 address", @"\b(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)\b", "", "Four numbers from 0 to 255 with dots", "10.0.0.1 and 192.168.1.255, not 300.1.1.1"),
        new("MAC address", @"\b[0-9A-Fa-f]{2}(?:[:-][0-9A-Fa-f]{2}){5}\b|\b[0-9A-Fa-f]{4}\.[0-9A-Fa-f]{4}\.[0-9A-Fa-f]{4}\b", "", "A hardware address in colon, dash or dotted form", "00:1A:2B:3C:4D:5E 00-1a-2b-3c-4d-5e 001a.2b3c.4d5e"),
        new("GUID", @"\b[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}\b", "", "A globally unique identifier", "id 123e4567-e89b-12d3-a456-426614174000 end"),
        new("ISO date", @"\b(?<year>\d{4})-(?<month>0[1-9]|1[0-2])-(?<day>0[1-9]|[12]\d|3[01])\b", "", "A date as 2026-10-20, with the parts named", "2026-10-20 and 1999-12-31 but not 2026-13-01"),
        new("Time (24 hour)", @"\b(?:[01]\d|2[0-3]):[0-5]\d(?::[0-5]\d)?\b", "", "Hours, minutes and optional seconds", "at 09:30 or 23:59:59, not 24:00"),
        new("Hex color", @"#(?:[0-9a-fA-F]{3}){1,2}\b", "", "A color as #RGB or #RRGGBB", "#fff #1A2B3C #12"),
        new("Semantic version", @"\b(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)(?:-(?<pre>[0-9A-Za-z.-]+))?(?:\+(?<build>[0-9A-Za-z.-]+))?\b", "", "A version like 1.2.3-beta.1+build5", "1.0.0 2.3.4-rc.1 1.2.3+meta"),
        new("Quoted string", "\"(?:[^\"\\\\]|\\\\.)*\"", "", "Text in double quotes, with backslash escapes", "say \"hello \\\"there\\\"\" and \"bye\""),
        new("Word", @"\b\w+\b", "", "Every word", "the quick brown fox"),
        new("Repeated word", @"\b(\w+)\s+\1\b", "i", "A word written twice in a row", "this is is a test of of the the system"),
        new("Leading / trailing white space", @"^\s+|\s+$", "m", "White space at the start or the end of a line", "  padded  \nclean\n\ttabbed"),
        new("HTML tag", @"</?(?<tag>[A-Za-z][\w-]*)(?<attrs>(?:\s+[\w:-]+(?:\s*=\s*(?:""[^""]*""|'[^']*'|[^\s>]+))?)*)\s*/?>", "", "An opening, closing or self-closing tag with its attributes", "<p class=\"a\">text</p><br/>"),
        new("Windows path", @"\b[A-Za-z]:\\(?:[^\\/:*?""<>|\r\n]+\\)*[^\\/:*?""<>|\r\n]*", "", "A drive path", @"open C:\Users\nill\file.txt now"),
        new("US phone number", @"\(?\b\d{3}\)?[-. ]?\d{3}[-. ]\d{4}\b", "", "A ten-digit phone number", "call (555) 123-4567 or 555.123.4567"),
        new("Key = value", @"^\s*(?<key>[\w.-]+)\s*=\s*(?<value>.*?)\s*$", "m", "Lines of a settings file", "name = Silo\nversion=1.2\n  path = C:\\x  ")
    ];

    public sealed record ReferenceItem(string Group, string Token, string Meaning);

    public static readonly IReadOnlyList<ReferenceItem> Reference =
    [
        new("Characters", ".", "Any character except a new line"),
        new("Characters", @"\d  \D", "A digit; anything but a digit"),
        new("Characters", @"\w  \W", "A word character (letter, digit, _); anything else"),
        new("Characters", @"\s  \S", "White space; anything but white space"),
        new("Characters", @"\n  \r  \t", "New line, carriage return, tab"),
        new("Characters", @"\uXXXX  \xXX", "A character by its code"),
        new("Characters", @"\p{L}  \p{Lu}  \p{IsGreek}", "A Unicode category or block"),
        new("Classes", "[abc]", "One of a, b or c"),
        new("Classes", "[^abc]", "Anything but a, b or c"),
        new("Classes", "[a-z]", "A character from a to z"),
        new("Classes", "[a-z-[aeiou]]", "a to z except vowels (subtraction)"),
        new("Anchors", "^  $", "Start and end of the text (of a line with the m flag)"),
        new("Anchors", @"\A  \z  \Z", "The very start; the very end; the end or before the last new line"),
        new("Anchors", @"\b  \B", "At a word boundary; not at one"),
        new("Anchors", @"\G", "Where the last match ended"),
        new("Quantifiers", "*  +  ?", "Zero or more; one or more; zero or one"),
        new("Quantifiers", "{n}  {n,}  {n,m}", "Exactly n; n or more; n to m"),
        new("Quantifiers", "*?  +?  ??  {n,m}?", "The same, as few as possible (lazy)"),
        new("Groups", "(abc)", "A capturing group"),
        new("Groups", "(?<name>abc)", "A named capturing group"),
        new("Groups", "(?:abc)", "A group that does not capture"),
        new("Groups", @"\1  \k<name>", "What an earlier group matched (back-reference)"),
        new("Groups", "(?>abc)", "An atomic group: no backtracking into it"),
        new("Groups", "a|b", "Either a or b"),
        new("Lookaround", "(?=abc)  (?!abc)", "Followed by; not followed by"),
        new("Lookaround", "(?<=abc)  (?<!abc)", "Preceded by; not preceded by"),
        new("Options", "(?i)  (?-i)", "Turn ignore-case on / off from here"),
        new("Options", "(?i:abc)", "Options for one group only"),
        new("Replacement", "$1  ${name}", "What a group matched"),
        new("Replacement", "$&  $`  $'  $$", "The whole match; the text before; the text after; a dollar sign")
    ];

    public static LibraryEntry? Find(string name)
    {
        return Entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public static RegexDoc ToDoc(LibraryEntry entry)
    {
        return new RegexDoc { Name = entry.Name, Pattern = entry.Pattern, Flags = entry.Flags, Description = entry.Description, TestText = entry.Sample };
    }
}

/// <summary>The regex as code in other languages, to paste where it is used.</summary>
public static class RegexCode
{
    public static readonly IReadOnlyList<string> Languages = ["C#", "C# (generated)", "JavaScript", "Python", "PowerShell", "Java", "Literal"];

    public static string Generate(string language, RegexDoc doc)
    {
        return language switch
               {
                   "C#"             => CSharp(doc),
                   "C# (generated)" => CSharpGenerated(doc),
                   "JavaScript"     => JavaScript(doc),
                   "Python"         => Python(doc),
                   "PowerShell"     => PowerShell(doc),
                   "Java"           => Java(doc),
                   _                => RegexLiteral.Format(doc)
               };
    }

    /// <summary>A C# verbatim string for the text: quotes are doubled.</summary>
    public static string Verbatim(string text)
    {
        return "@\"" + text.Replace("\"", "\"\"") + "\"";
    }

    private static string OptionsText(RegexDoc doc)
    {
        var parts = RegexFlags.All.Where(f => RegexFlags.Parse(doc.Flags).HasFlag(f.Option) && f.Option != RegexOptions.None).Select(f => "RegexOptions." + f.Name).ToList();
        return parts.Count == 0 ? string.Empty : string.Join(" | ", parts);
    }

    private static string CSharp(RegexDoc doc)
    {
        string options = OptionsText(doc);
        string pattern = Verbatim(doc.Pattern);
        return "using System.Text.RegularExpressions;\r\n\r\n" +
               $"var regex = new Regex({pattern}{(options.Length > 0 ? ", " + options : string.Empty)});\r\n" +
               "foreach (Match m in regex.Matches(text))\r\n    Console.WriteLine(m.Value);";
    }

    private static string CSharpGenerated(RegexDoc doc)
    {
        string options = OptionsText(doc);
        return "using System.Text.RegularExpressions;\r\n\r\n" +
               $"[GeneratedRegex({Verbatim(doc.Pattern)}{(options.Length > 0 ? ", " + options : string.Empty)})]\r\n" +
               "private static partial Regex MyRegex();";
    }

    private static string JavaScript(RegexDoc doc)
    {
        string flags = new([.. doc.Flags.Where(c => "ims".Contains(c))]);
        return $"const re = /{doc.Pattern.Replace("/", "\\/")}/{flags}g;\r\nconst matches = [...text.matchAll(re)];";
    }

    private static string Python(RegexDoc doc)
    {
        var flags = new List<string>();
        if (doc.Flags.Contains('i')) flags.Add("re.IGNORECASE");
        if (doc.Flags.Contains('m')) flags.Add("re.MULTILINE");
        if (doc.Flags.Contains('s')) flags.Add("re.DOTALL");
        if (doc.Flags.Contains('x')) flags.Add("re.VERBOSE");
        string pattern = "r\"\"\"" + doc.Pattern + "\"\"\"";
        return $"import re\r\n\r\nrx = re.compile({pattern}{(flags.Count > 0 ? ", " + string.Join(" | ", flags) : string.Empty)})\r\nmatches = rx.findall(text)";
    }

    private static string PowerShell(RegexDoc doc)
    {
        string options = OptionsText(doc).Replace("RegexOptions.", "").Replace(" | ", ", ");
        string pattern = "'" + doc.Pattern.Replace("'", "''") + "'";
        return options.Length == 0
                   ? $"[regex]::Matches($text, {pattern})"
                   : $"[regex]::Matches($text, {pattern}, '{options}')";
    }

    private static string Java(RegexDoc doc)
    {
        var flags = new List<string>();
        if (doc.Flags.Contains('i')) flags.Add("Pattern.CASE_INSENSITIVE");
        if (doc.Flags.Contains('m')) flags.Add("Pattern.MULTILINE");
        if (doc.Flags.Contains('s')) flags.Add("Pattern.DOTALL");
        if (doc.Flags.Contains('x')) flags.Add("Pattern.COMMENTS");
        string pattern = "\"" + doc.Pattern.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        return $"Pattern p = Pattern.compile({pattern}{(flags.Count > 0 ? ", " + string.Join(" | ", flags) : string.Empty)});\r\nMatcher m = p.matcher(text);\r\nwhile (m.find()) System.out.println(m.group());";
    }
}
