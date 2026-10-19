using System.Globalization;

namespace Silo.Unicode;

/// <summary>A block of the code space (<c>Arrows</c>, <c>U+2190..U+21FF</c>).</summary>
/// <param name="Start">The first code point.</param>
/// <param name="End">The last code point.</param>
/// <param name="Name">The block's name.</param>
public sealed record UnicodeBlock(int Start, int End, string Name)
{
    /// <summary>The number of code points the block spans.</summary>
    public int Count => End - Start + 1;

    /// <summary>Whether the code point is in the block.</summary>
    public bool Contains(int codePoint) => codePoint >= Start && codePoint <= End;

    /// <inheritdoc />
    public override string ToString() => $"{Name} (U+{Start:X4}..U+{End:X4})";
}

/// <summary>A script (<c>Latn</c>, "Latin").</summary>
/// <param name="Code">The four-letter ISO 15924 code.</param>
/// <param name="Name">The script's name.</param>
public sealed record UnicodeScript(string Code, string Name);

/// <summary>What the character database knows about one code point.</summary>
public sealed class UnicodeCharacter
{
    private readonly UnicodeDatabase.Record? _record;
    private readonly UnicodeDatabase         _db;

    internal UnicodeCharacter(UnicodeDatabase db, int codePoint, UnicodeDatabase.Record? record)
    {
        _db        = db;
        CodePoint  = new CodePoint(codePoint);
        _record    = record;
    }

    /// <summary>The code point.</summary>
    public CodePoint CodePoint { get; }

    /// <summary>The number.</summary>
    public int Value => CodePoint.Value;

    /// <summary>Whether the database has the code point at all (assigned, or a private-use, surrogate or noncharacter code point); false for an unassigned one.</summary>
    public bool IsKnown => _record is not null;

    /// <summary>Whether a character is assigned to the code point (private-use code points, surrogates, noncharacters and unassigned ones are not).</summary>
    public bool IsAssigned => _record is { } r && r.Category != "Cn" && r.Category != "Co" && r.Category != "Cs";

    /// <summary>Whether the code point is a noncharacter (<c>U+FDD0..U+FDEF</c>, <c>U+xxFFFE</c>, <c>U+xxFFFF</c>): reserved for internal use, never for text.</summary>
    public bool IsNoncharacter => Value is >= 0xFDD0 and <= 0xFDEF || (Value & 0xFFFE) == 0xFFFE;

    /// <summary>The official name (or, for a control, its standard name; for a code point without one, the standard's label such as <c>&lt;private-use-E000&gt;</c>).</summary>
    public string Name => _record is null ? (IsNoncharacter ? UnicodeNames.Label("noncharacter", Value) : "(unassigned)") : _record.NameOf(Value);

    /// <summary>Other names: abbreviations (<c>NBSP</c>), corrections, alternatives, and the HTML entity names.</summary>
    public IReadOnlyList<string> Aliases => _record?.Aliases ?? [];

    /// <summary>The HTML entity names without the <c>&amp;</c> and <c>;</c>: <c>rarr</c>, <c>RightArrow</c>.</summary>
    public IReadOnlyList<string> HtmlEntities => _record?.Entities ?? [];

    /// <summary>The two-letter general category: <c>Lu</c>, <c>Sm</c>, <c>Cn</c> for an unassigned code point.</summary>
    public string Category => _record?.Category ?? "Cn";

    /// <summary>The category's name: "Uppercase Letter".</summary>
    public string CategoryName => UnicodeNames.Category(Category)?.Name ?? Category;

    /// <summary>The group of the category: "Letter", "Symbol"...</summary>
    public string MajorCategoryName => UnicodeNames.Majors.FirstOrDefault(m => m.Code == Category[..1]).Name ?? Category[..1];

    /// <summary>The block the code point lies in; null when it is in none.</summary>
    public UnicodeBlock? Block => _db.BlockOf(Value);

    /// <summary>The script's four-letter code; <c>Zzzz</c> when it has none, <c>Zyyy</c> for characters used by several scripts, <c>Zinh</c> for those that take the script of their neighbour.</summary>
    public string ScriptCode => _record?.ScriptCode ?? "Zzzz";

    /// <summary>The name of the script: "Latin", "Common", "Inherited".</summary>
    public string Script => _db.ScriptName(ScriptCode);

    /// <summary>The Unicode version that assigned the code point (<c>6.0</c>); null for one that is not assigned.</summary>
    public string? Age => _record is { Age.Length: > 0 } r ? r.Age : null;

    /// <summary>The plane number, 0 to 16.</summary>
    public int Plane => UnicodeNames.Plane(Value);

    /// <summary>The plane's name.</summary>
    public string PlaneName => UnicodeNames.PlaneName(Plane);

    /// <summary>The canonical combining class: 0 for most characters; a mark's position relative to its base otherwise.</summary>
    public int CombiningClass => _record?.Ccc ?? 0;

    /// <summary>The bidirectional class code (<c>L</c>, <c>R</c>, <c>AL</c>, <c>EN</c>...).</summary>
    public string BidiClass => _record?.Bidi ?? "L";

    /// <summary>The bidirectional class's name.</summary>
    public string BidiClassName => UnicodeNames.BidiName(BidiClass);

    /// <summary>The East Asian width code (<c>W</c>, <c>Na</c>...).</summary>
    public string EastAsianWidth => _record?.Eaw ?? "N";

    /// <summary>The East Asian width's name.</summary>
    public string EastAsianWidthName => UnicodeNames.WidthName(EastAsianWidth);

    /// <summary>Whether the glyph is mirrored in right-to-left text (parentheses, arrows).</summary>
    public bool IsMirrored => _record?.Flags.HasFlag(UnicodeDatabase.UnicodeFlags.Mirrored) ?? false;

    /// <summary>Whether the code point has the Emoji property.</summary>
    public bool IsEmoji => _record?.Flags.HasFlag(UnicodeDatabase.UnicodeFlags.Emoji) ?? false;

    /// <summary>Whether it shows as a coloured picture by default (Emoji_Presentation).</summary>
    public bool IsEmojiPresentation => _record?.Flags.HasFlag(UnicodeDatabase.UnicodeFlags.EmojiPresentation) ?? false;

    /// <summary>Whether it is a skin-tone modifier.</summary>
    public bool IsEmojiModifier => _record?.Flags.HasFlag(UnicodeDatabase.UnicodeFlags.EmojiModifier) ?? false;

    /// <summary>Whether it is an extended pictographic (what emoji sequences are built from).</summary>
    public bool IsExtendedPictographic => _record?.Flags.HasFlag(UnicodeDatabase.UnicodeFlags.Pictographic) ?? false;

    /// <summary>The emoji group ("smileys-emotion", "symbols"...); empty for a code point that is not a listed emoji.</summary>
    public string EmojiGroup => _record?.EmojiGroup ?? string.Empty;

    /// <summary>The numeric value (<c>7</c>, <c>1/2</c>) of a digit, fraction or numeral; null for other characters.</summary>
    public string? NumericValue => _record is { Numeric.Length: > 0 } r ? r.Numeric : null;

    /// <summary>The decomposition as the database writes it: <c>0041 030A</c> or <c>&lt;compat&gt; 0020 0308</c>; empty when the character does not decompose.</summary>
    public string Decomposition => _record?.Decomposition ?? string.Empty;

    /// <summary>The code points the character decomposes to (without the compatibility tag).</summary>
    public IReadOnlyList<CodePoint> DecompositionCodePoints
    {
        get
        {
            var list = new List<CodePoint>();
            foreach (string part in Decomposition.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (!part.StartsWith('<') && int.TryParse(part, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int v) && v <= CodePoint.MaxValue)
                    list.Add(new CodePoint(v));
            return list;
        }
    }

    /// <summary>The kind of the decomposition: "canonical", the compatibility tag (<c>compat</c>, <c>font</c>, <c>super</c>...), or empty.</summary>
    public string DecompositionKind
    {
        get
        {
            string d = Decomposition;
            if (d.Length == 0) return string.Empty;
            return d[0] == '<' ? d[1..d.IndexOf('>')] : "canonical";
        }
    }

    /// <summary>
    ///     The search words: the explicit tags (CLDR tags and shortcodes for emoji) and the words of the name and aliases, lower-case, without duplicates.
    /// </summary>
    public IReadOnlyList<string> Keywords
    {
        get
        {
            var words = new List<string>();
            if (_record is not null) words.AddRange(_record.Keywords);
            foreach (string w in UnicodeNames.Normalize(Name).Split(' ', StringSplitOptions.RemoveEmptyEntries)) words.Add(w);
            foreach (string a in Aliases)
            foreach (string w in UnicodeNames.Normalize(a).Split(' ', StringSplitOptions.RemoveEmptyEntries))
                words.Add(w);
            return words.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    /// <summary>The character itself as text (a lone surrogate comes out as that single unit).</summary>
    public string Text => CodePoint.Text;

    /// <summary>
    ///     Something safe to draw for the code point: the character, or a stand-in for the ones that draw nothing (a control picture for a control,
    ///     an open box for a space, a dotted circle in front of a combining mark, an empty square for the unassigned and invisible).
    /// </summary>
    public string Glyph
    {
        get
        {
            int v = Value;
            string cat = Category;
            if (v < 0x20) return ((char)(0x2400 + v)).ToString();
            if (v == 0x7F) return "␡";
            if (cat == "Zs" || cat == "Zl" || cat == "Zp") return "␣";
            if (cat is "Cc" or "Cs" or "Co" or "Cn" or "Cf") return "□";
            if (cat is "Mn" or "Me" or "Mc") return "◌" + Text;
            return Text;
        }
    }

    /// <inheritdoc />
    public override string ToString() => $"{CodePoint.Hex} {Name}";
}
