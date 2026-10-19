using System.Globalization;
using System.Text;

namespace Silo.Unicode;

/// <summary>A general category of the Unicode character database (<c>Lu</c>, <c>Nd</c>...): its two-letter code, name and one-letter group.</summary>
/// <param name="Code">Two letters, <c>Lu</c>.</param>
/// <param name="Name">The long name, <c>Uppercase Letter</c>.</param>
/// <param name="Major">The group's letter: <c>L</c>etter, <c>M</c>ark, <c>N</c>umber, <c>P</c>unctuation, <c>S</c>ymbol, <c>Z</c> separator, <c>C</c> other.</param>
public sealed record UnicodeCategoryInfo(string Code, string Name, string Major);

/// <summary>The names of the codes the character database uses (categories, bidirectional classes, widths, planes), and the rules that make some character names.</summary>
public static class UnicodeNames
{
    /// <summary>The thirty general categories, in the order the standard lists them.</summary>
    public static IReadOnlyList<UnicodeCategoryInfo> Categories { get; } =
    [
        new("Lu", "Uppercase Letter", "L"), new("Ll", "Lowercase Letter", "L"), new("Lt", "Titlecase Letter", "L"), new("Lm", "Modifier Letter", "L"),
        new("Lo", "Other Letter", "L"), new("Mn", "Nonspacing Mark", "M"), new("Mc", "Spacing Mark", "M"), new("Me", "Enclosing Mark", "M"),
        new("Nd", "Decimal Number", "N"), new("Nl", "Letter Number", "N"), new("No", "Other Number", "N"), new("Pc", "Connector Punctuation", "P"),
        new("Pd", "Dash Punctuation", "P"), new("Ps", "Open Punctuation", "P"), new("Pe", "Close Punctuation", "P"), new("Pi", "Initial Punctuation", "P"),
        new("Pf", "Final Punctuation", "P"), new("Po", "Other Punctuation", "P"), new("Sm", "Math Symbol", "S"), new("Sc", "Currency Symbol", "S"),
        new("Sk", "Modifier Symbol", "S"), new("So", "Other Symbol", "S"), new("Zs", "Space Separator", "Z"), new("Zl", "Line Separator", "Z"),
        new("Zp", "Paragraph Separator", "Z"), new("Cc", "Control", "C"), new("Cf", "Format", "C"), new("Cs", "Surrogate", "C"),
        new("Co", "Private Use", "C"), new("Cn", "Unassigned", "C")
    ];

    /// <summary>The groups of categories: the one letter and its name.</summary>
    public static IReadOnlyList<(string Code, string Name)> Majors { get; } =
    [
        ("L", "Letter"), ("M", "Mark"), ("N", "Number"), ("P", "Punctuation"), ("S", "Symbol"), ("Z", "Separator"), ("C", "Other")
    ];

    private static readonly Dictionary<string, string> BidiNames = new(StringComparer.Ordinal)
    {
        ["L"] = "Left-to-Right", ["R"] = "Right-to-Left", ["AL"] = "Right-to-Left Arabic", ["EN"] = "European Number", ["ES"] = "European Number Separator",
        ["ET"] = "European Number Terminator", ["AN"] = "Arabic Number", ["CS"] = "Common Number Separator", ["NSM"] = "Nonspacing Mark",
        ["BN"] = "Boundary Neutral", ["B"] = "Paragraph Separator", ["S"] = "Segment Separator", ["WS"] = "White Space", ["ON"] = "Other Neutrals",
        ["LRE"] = "Left-to-Right Embedding", ["LRO"] = "Left-to-Right Override", ["RLE"] = "Right-to-Left Embedding", ["RLO"] = "Right-to-Left Override",
        ["PDF"] = "Pop Directional Format", ["LRI"] = "Left-to-Right Isolate", ["RLI"] = "Right-to-Left Isolate", ["FSI"] = "First Strong Isolate",
        ["PDI"] = "Pop Directional Isolate"
    };

    private static readonly Dictionary<string, string> WidthNames = new(StringComparer.Ordinal)
    {
        ["F"] = "Fullwidth", ["H"] = "Halfwidth", ["W"] = "Wide", ["Na"] = "Narrow", ["A"] = "Ambiguous", ["N"] = "Neutral"
    };

    private static readonly Dictionary<string, UnicodeCategoryInfo> ByCode = Categories.ToDictionary(c => c.Code, StringComparer.Ordinal);

    /// <summary>The category with this two-letter code; null for an unknown code.</summary>
    public static UnicodeCategoryInfo? Category(string? code) => code is not null && ByCode.TryGetValue(code, out var c) ? c : null;

    /// <summary>The long name of a bidirectional class (<c>AL</c> is "Right-to-Left Arabic"); the code itself when it is not known.</summary>
    public static string BidiName(string code) => BidiNames.TryGetValue(code, out string? n) ? n : code;

    /// <summary>The long name of an East Asian width (<c>Na</c> is "Narrow"); the code itself when it is not known.</summary>
    public static string WidthName(string code) => WidthNames.TryGetValue(code, out string? n) ? n : code;

    /// <summary>The plane a code point is in, 0 to 16.</summary>
    public static int Plane(int codePoint) => codePoint >> 16;

    /// <summary>The name of a plane: "Basic Multilingual Plane" for 0.</summary>
    public static string PlaneName(int plane) => plane switch
    {
        0  => "Basic Multilingual Plane",
        1  => "Supplementary Multilingual Plane",
        2  => "Supplementary Ideographic Plane",
        3  => "Tertiary Ideographic Plane",
        14 => "Supplementary Special-purpose Plane",
        15 => "Supplementary Private Use Area-A",
        16 => "Supplementary Private Use Area-B",
        _  => "Unassigned plane"
    };

    private static readonly string[] HangulL =
        ["G", "GG", "N", "D", "DD", "R", "M", "B", "BB", "S", "SS", "", "J", "JJ", "C", "K", "T", "P", "H"];

    private static readonly string[] HangulV =
        ["A", "AE", "YA", "YAE", "EO", "E", "YEO", "YE", "O", "WA", "WAE", "OE", "YO", "U", "WEO", "WE", "WI", "YU", "EU", "YI", "I"];

    private static readonly string[] HangulT =
        ["", "G", "GG", "GS", "N", "NJ", "NH", "D", "L", "LG", "LM", "LB", "LS", "LT", "LP", "LH", "M", "B", "BS", "S", "SS", "NG", "J", "C", "K", "T", "P", "H"];

    /// <summary>The name of a precomposed Hangul syllable (<c>U+AC00..U+D7A3</c>), which the standard derives from its jamo: <c>HANGUL SYLLABLE GA</c>.</summary>
    public static string HangulSyllableName(int codePoint)
    {
        int s = codePoint - 0xAC00;
        if (s is < 0 or >= 11172) throw new ArgumentOutOfRangeException(nameof(codePoint), codePoint, "Not a Hangul syllable.");

        return "HANGUL SYLLABLE " + HangulL[s / 588] + HangulV[s % 588 / 28] + HangulT[s % 28];
    }

    /// <summary>The label the standard gives the code points that have no name: <c>&lt;private-use-E000&gt;</c>.</summary>
    public static string Label(string kind, int codePoint) => "<" + kind + "-" + codePoint.ToString("X4", CultureInfo.InvariantCulture) + ">";

    /// <summary>Lower-cases <paramref name="text" /> and turns everything that is not a letter or digit into one space, with a space at each end: the form the search compares.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return " ";

        var  sb      = new StringBuilder(text.Length + 2);
        bool pending = true;
        sb.Append(' ');
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
                pending = false;
            }
            else if (!pending)
            {
                sb.Append(' ');
                pending = true;
            }
        }

        if (!pending) sb.Append(' ');
        return sb.ToString();
    }
}
