using System.Globalization;
using System.Text;

namespace Silo.Unicode;

/// <summary>A way of writing text with other characters: <c>hello</c> as <c>🅗🅔🅛🅛🅞</c>.</summary>
/// <param name="Id">A short stable name ("negative-circled").</param>
/// <param name="Name">What it is called to a person.</param>
/// <param name="Group">The heading it is listed under.</param>
/// <param name="Convert">Writes a text in the style. Characters the style has nothing for stay as they are.</param>
public sealed record TextStyle(string Id, string Name, string Group, Func<string, string> Convert)
{
    /// <summary>The text written in this style.</summary>
    public string Apply(string text) => Convert(text);
}

/// <summary>
///     The converters of the Unicode Explorer's Generate tab. Most are table driven: the Unicode blocks of styled letters (Mathematical
///     Alphanumeric Symbols, Enclosed Alphanumerics, Halfwidth and Fullwidth Forms...) put the alphabet in order, with a few letters
///     that were there before and sit elsewhere (the "holes" of the script, fraktur and double-struck alphabets).
/// </summary>
public static class TextStyles
{
    /// <summary>All the styles, grouped in the order they are shown.</summary>
    public static IReadOnlyList<TextStyle> All => Styles.Value;

    // built on first use, after the tables below are filled in (static fields are set in the order they are written)
    private static readonly Lazy<IReadOnlyList<TextStyle>> Styles = new(Build);

    /// <summary>The style with this id.</summary>
    public static TextStyle? Find(string id) => All.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The text in the style with this id.</summary>
    /// <exception cref="ArgumentException">There is no such style.</exception>
    public static string Apply(string id, string text) => (Find(id) ?? throw new ArgumentException($"There is no text style called '{id}'.", nameof(id))).Apply(text);

    // ---- building ------------------------------------------------------------------------------------------------

    private static IReadOnlyList<TextStyle> Build()
    {
        var list = new List<TextStyle>();

        void Add(string id, string name, string group, Func<string, string> convert) => list.Add(new TextStyle(id, name, group, convert));

        // Mathematical Alphanumeric Symbols
        const string math = "Letters and numbers";
        Add("bold",             "Bold",                 math, Alphabet(0x1D400, 0x1D41A, 0x1D7CE));
        Add("italic",           "Italic",               math, Alphabet(0x1D434, 0x1D44E, null, null, new() { ['h'] = 0x210E }));
        Add("bold-italic",      "Bold italic",          math, Alphabet(0x1D468, 0x1D482, null));
        Add("script",           "Script",               math, Alphabet(0x1D49C, 0x1D4B6, null,
                                                                       new() { ['B'] = 0x212C, ['E'] = 0x2130, ['F'] = 0x2131, ['H'] = 0x210B, ['I'] = 0x2110, ['L'] = 0x2112, ['M'] = 0x2133, ['R'] = 0x211B },
                                                                       new() { ['e'] = 0x212F, ['g'] = 0x210A, ['o'] = 0x2134 }));
        Add("bold-script",      "Bold script",          math, Alphabet(0x1D4D0, 0x1D4EA, null));
        Add("fraktur",          "Fraktur",              math, Alphabet(0x1D504, 0x1D51E, null, new() { ['C'] = 0x212D, ['H'] = 0x210C, ['I'] = 0x2111, ['R'] = 0x211C, ['Z'] = 0x2128 }));
        Add("bold-fraktur",     "Bold fraktur",         math, Alphabet(0x1D56C, 0x1D586, null));
        Add("double-struck",    "Double-struck",        math, Alphabet(0x1D538, 0x1D552, 0x1D7D8,
                                                                       new() { ['C'] = 0x2102, ['H'] = 0x210D, ['N'] = 0x2115, ['P'] = 0x2119, ['Q'] = 0x211A, ['R'] = 0x211D, ['Z'] = 0x2124 }));
        Add("sans",             "Sans-serif",           math, Alphabet(0x1D5A0, 0x1D5BA, 0x1D7E2));
        Add("sans-bold",        "Sans-serif bold",      math, Alphabet(0x1D5D4, 0x1D5EE, 0x1D7EC));
        Add("sans-italic",      "Sans-serif italic",    math, Alphabet(0x1D608, 0x1D622, null));
        Add("sans-bold-italic", "Sans-serif bold italic", math, Alphabet(0x1D63C, 0x1D656, null));
        Add("monospace",        "Monospace",            math, Alphabet(0x1D670, 0x1D68A, 0x1D7F6));

        // Enclosed
        const string enclosed = "Enclosed";
        Add("circled",          "Circled",              enclosed, Enclosed(0x24B6, 0x24D0, i => i == 0 ? 0x24EA : 0x2460 + i - 1));
        Add("negative-circled", "Negative circled",     enclosed, Enclosed(0x1F150, 0x1F150, i => i == 0 ? 0x24FF : 0x2776 + i - 1));
        Add("squared",          "Squared",              enclosed, Enclosed(0x1F130, 0x1F130, null));
        Add("negative-squared", "Negative squared",     enclosed, Enclosed(0x1F170, 0x1F170, null));
        Add("parenthesized",    "Parenthesized",        enclosed, Enclosed(0x1F110, 0x249C, i => i == 0 ? -1 : 0x2474 + i - 1));
        Add("regional",         "Regional indicators",  enclosed, Enclosed(0x1F1E6, 0x1F1E6, null));

        // width and case
        const string form = "Width and case";
        Add("fullwidth",        "Fullwidth",            form, Fullwidth);
        Add("upper",            "UPPER CASE",           form, t => t.ToUpperInvariant());
        Add("lower",            "lower case",           form, t => t.ToLowerInvariant());
        Add("small-caps",       "Small caps",           form, Table(SmallCaps));

        // small forms
        const string small = "Small and turned";
        Add("superscript",      "Superscript",          small, Table(Superscripts));
        Add("subscript",        "Subscript",            small, Table(Subscripts));
        Add("upside-down",      "Upside down",          small, t => Reverse(Table(UpsideDown)(t)));
        Add("reversed",         "Reversed",             small, Reverse);

        // marks put after each character
        const string marks = "Lines through and under";
        Add("strikethrough",    "Strikethrough",        marks, Combine(0x0336));
        Add("underline",        "Underline",            marks, Combine(0x0332));
        Add("double-underline", "Double underline",     marks, Combine(0x0333));
        Add("overline",         "Overline",             marks, Combine(0x0305));
        Add("slashed",          "Slashed",              marks, Combine(0x0338));

        // the way back
        Add("plain",            "Plain (compatibility normalization)", "Undo", t => t.Normalize(NormalizationForm.FormKC));

        return list;
    }

    // ---- the tables ----------------------------------------------------------------------------------------------

    /// <summary>The A-Z, a-z (and 0-9) of a styled alphabet, with the letters that live elsewhere (the holes) taken from the lists.</summary>
    private static Func<string, string> Alphabet(int upper, int lower, int? digit, Dictionary<char, int>? upperHoles = null, Dictionary<char, int>? lowerHoles = null)
    {
        return text => Map(text, cp =>
                                 {
                                     if (cp is >= 'A' and <= 'Z') return upperHoles is not null && upperHoles.TryGetValue((char)cp, out int u) ? u : upper + (cp - 'A');
                                     if (cp is >= 'a' and <= 'z') return lowerHoles is not null && lowerHoles.TryGetValue((char)cp, out int l) ? l : lower + (cp - 'a');
                                     if (cp is >= '0' and <= '9' && digit is { } d) return d + (cp - '0');
                                     return -1;
                                 });
    }

    /// <summary>Letters of an enclosed alphabet: one set for both cases when the set has only capitals; digits from a function (0..9, -1 for none).</summary>
    private static Func<string, string> Enclosed(int upper, int lower, Func<int, int>? digits)
    {
        return text => Map(text, cp =>
                                 {
                                     if (cp is >= 'A' and <= 'Z') return upper + (cp - 'A');
                                     if (cp is >= 'a' and <= 'z') return lower + (cp - 'a');
                                     if (cp is >= '0' and <= '9' && digits is not null) return digits(cp - '0');
                                     return -1;
                                 });
    }

    private static string Fullwidth(string text)
    {
        return Map(text, cp => cp == ' ' ? 0x3000 : cp is >= 0x21 and <= 0x7E ? cp + 0xFEE0 : -1);
    }

    private static readonly int[] SmallCaps =
    [
        0x1D00, 0x0299, 0x1D04, 0x1D05, 0x1D07, 0xA730, 0x0262, 0x029C, 0x026A, 0x1D0A, 0x1D0B, 0x029F, 0x1D0D,
        0x0274, 0x1D0F, 0x1D18, 0x01EB, 0x0280, 0xA731, 0x1D1B, 0x1D1C, 0x1D20, 0x1D21, 'x', 0x028F, 0x1D22
    ];

    private static readonly Dictionary<int, int> Superscripts = Pairs(
        "abcdefghijklmnoprstuvwxyz", "ᵃᵇᶜᵈᵉᶠᵍʰⁱʲᵏˡᵐⁿᵒᵖʳˢᵗᵘᵛʷˣʸᶻ",
        "ABDEGHIJKLMNOPRTUVW", "ᴬᴮᴰᴱᴳᴴᴵᴶᴷᴸᴹᴺᴼᴾᴿᵀᵁⱽᵂ",
        "0123456789+-=()", "⁰¹²³⁴⁵⁶⁷⁸⁹⁺⁻⁼⁽⁾");

    private static readonly Dictionary<int, int> Subscripts = Pairs(
        "aehijklmnoprstuvx", "ₐₑₕᵢⱼₖₗₘₙₒₚᵣₛₜᵤᵥₓ",
        "0123456789+-=()", "₀₁₂₃₄₅₆₇₈₉₊₋₌₍₎");

    private static readonly Dictionary<int, int> UpsideDown = Pairs(
        "abcdefghijklmnopqrstuvwxyz", "ɐqɔpǝɟƃɥᴉɾʞןɯuodbɹsʇnʌʍxʎz",
        "ABCDEFGJKLMPQRTUVWY", "∀ᗺƆᗡƎℲ⅁ſ⋊˥WԀΌᴚ⊥∩ΛM⅄",
        "0123456789", "0ƖᄅƐㄣϛ9ㄥ86",
        ".,'?!&_", "˙'‘¿¡⅋‾");

    private static Func<string, string> Table(int[] smallCaps)
    {
        return text => Map(text, cp => cp is >= 'a' and <= 'z' ? smallCaps[cp - 'a'] : cp is >= 'A' and <= 'Z' ? smallCaps[cp - 'A'] : -1);
    }

    private static Func<string, string> Table(Dictionary<int, int> table)
    {
        return text => Map(text, cp => table.TryGetValue(cp, out int to) ? to : -1);
    }

    /// <summary>Pairs of strings (what, with what) as one table; the strings of a pair have the same number of characters.</summary>
    private static Dictionary<int, int> Pairs(params string[] pairs)
    {
        var table = new Dictionary<int, int>();
        for (int p = 0; p + 1 < pairs.Length; p += 2)
        {
            var from = ToCodePoints(pairs[p]);
            var to   = ToCodePoints(pairs[p + 1]);
            if (from.Length != to.Length) throw new InvalidOperationException($"The text style table {p / 2} has {from.Length} characters and {to.Length} to map them to.");
            for (int i = 0; i < from.Length; i++) table[from[i]] = to[i];
        }

        return table;
    }

    private static int[] ToCodePoints(string text)
    {
        var list = new List<int>();
        foreach (var rune in text.EnumerateRunes()) list.Add(rune.Value);
        return list.ToArray();
    }

    private static Func<string, string> Combine(int mark)
    {
        return text =>
               {
                   var sb = new StringBuilder();
                   foreach (var rune in text.EnumerateRunes())
                   {
                       sb.Append(rune.ToString());
                       if (!Rune.IsWhiteSpace(rune) && !Rune.IsControl(rune)) sb.Append(char.ConvertFromUtf32(mark));
                   }

                   return sb.ToString();
               };
    }

    /// <summary>The text turned around by whole characters as a person sees them (a letter with its accents stays together).</summary>
    private static string Reverse(string text)
    {
        var parts = new List<string>();
        var e     = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext()) parts.Add((string)e.Current);
        parts.Reverse();
        return string.Concat(parts);
    }

    /// <summary>Maps every code point of the text: a result of -1 leaves it as it is, any other is the code point it becomes.</summary>
    private static string Map(string text, Func<int, int> map)
    {
        var sb = new StringBuilder(text.Length * 2);
        foreach (var rune in text.EnumerateRunes())
        {
            int to = map(rune.Value);
            sb.Append(to >= 0 && to <= CodePoint.MaxValue && !(to is >= 0xD800 and <= 0xDFFF) ? char.ConvertFromUtf32(to) : rune.ToString());
        }

        return sb.ToString();
    }
}
