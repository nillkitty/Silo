using System.Globalization;
using System.Text;

namespace Silo.Unicode;

/// <summary>One way of writing a code point: how it is encoded, escaped or typed.</summary>
/// <param name="Group">The kind of entry the Explorer lists it under: "Numbers", "Unicode encodings", "Windows", "Web", "Source code", "Case and normalization".</param>
/// <param name="Name">What it is: "UTF-8 (hex bytes)".</param>
/// <param name="Value">The text itself, ready to copy.</param>
/// <param name="Note">A hint on how to use it; empty when none is needed.</param>
public sealed record EncodingEntry(string Group, string Name, string Value, string Note = "");

/// <summary>The Windows Alt codes of a character: the number to type on the numeric keypad while holding Alt.</summary>
public static class AltCodes
{
    // the characters of the original IBM PC character set at 1..31 and 127, which Alt+1..31 types as pictures
    private const string OemLow = "☺☻♥♦♣♠•◘○◙♂♀♪♫☼►◄↕‼¶§▬↨↑↓→←∟↔▲▼";

    // code page 437 at 0x80..0xFF
    private const string Cp437High =
        "ÇüéâäàåçêëèïîìÄÅÉæÆôöòûùÿÖÜ¢£¥₧ƒ" +
        "áíóúñÑªº¿⌐¬½¼¡«»░▒▓│┤╡╢╖╕╣║╗╝╜╛┐" +
        "└┴┬├─┼╞╟╚╔╩╦╠═╬╧╨╤╥╙╘╒╓╫╪┘┌█▄▌▐▀" +
        "αßΓπΣσµτΦΘΩδ∞φε∩≡±≥≤⌠⌡÷≈°∙·√ⁿ²■ ";

    // Windows-1252 at 0x80..0x9F (0 where the code page has no character)
    private const string Cp1252Mid =
        "€\u0000‚ƒ„…†‡ˆ‰Š‹Œ\u0000Ž\u0000\u0000‘’“”•–—˜™š›œ\u0000žŸ";

    /// <summary>
    ///     The number to type after Alt (with no leading zero) for the character: the code in the PC's OEM code page (437 on a US system), 1 to 255;
    ///     null when that code page has no such character.
    /// </summary>
    /// <remarks>The OEM code page of the system can be a different one (850, 852...), which types other characters above 127 for the same numbers.</remarks>
    public static int? Oem(int codePoint)
    {
        if (codePoint is >= 0x20 and <= 0x7E) return codePoint;
        if (codePoint == 0x2302) return 127;

        if (codePoint > 0xFFFF) return null;

        int low = OemLow.IndexOf((char)codePoint);
        if (low >= 0) return low + 1;

        int high = Cp437High.IndexOf((char)codePoint);
        return high >= 0 ? 0x80 + high : null;
    }

    /// <summary>The number to type after Alt with a leading zero (<c>Alt+0233</c>) for the character: its byte in the ANSI code page (Windows-1252); null when it has none.</summary>
    public static int? Ansi(int codePoint)
    {
        if (codePoint is >= 0x20 and <= 0x7E or >= 0xA0 and <= 0xFF) return codePoint;
        if (codePoint > 0xFFFF) return null;

        int mid = Cp1252Mid.IndexOf((char)codePoint);
        return mid >= 0 && codePoint != 0 ? 0x80 + mid : null;
    }

    /// <summary>The character that Alt+<paramref name="number" /> types: the PC character set's picture for 1..31, else OEM 437 for 128..255; null when it types nothing.</summary>
    public static string? FromOem(int number)
    {
        if (number is >= 1 and <= 31) return OemLow[number - 1].ToString();
        if (number is >= 32 and <= 126) return ((char)number).ToString();
        if (number == 127) return "⌂";
        if (number is >= 128 and <= 255) return Cp437High[number - 128].ToString();
        return null;
    }

    /// <summary>The character that Alt+0<paramref name="number" /> types (Windows-1252); null when it types nothing.</summary>
    public static string? FromAnsi(int number)
    {
        if (number is >= 32 and <= 126 or >= 160 and <= 255) return ((char)number).ToString();
        if (number is >= 128 and <= 159 && Cp1252Mid[number - 128] != '\0') return Cp1252Mid[number - 128].ToString();
        return null;
    }
}

/// <summary>Every way the Unicode Explorer shows a code point written down: numbers, encodings, escapes for markup and programming languages, Alt codes, case and normalization.</summary>
public static class UnicodeEncodings
{
    /// <summary>The group names, in the order the Explorer shows them.</summary>
    public static IReadOnlyList<string> Groups { get; } = ["Numbers", "Unicode encodings", "Windows", "Web", "Source code", "Case and normalization"];

    /// <summary>All the entries for the code point; <paramref name="info" /> (when given) adds what only the database knows, such as HTML entity names and decompositions.</summary>
    public static IReadOnlyList<EncodingEntry> Describe(CodePoint cp, UnicodeCharacter? info = null)
    {
        var list = new List<EncodingEntry>();
        int v    = cp.Value;
        bool scalar = cp.IsScalar;

        // ---- numbers
        list.Add(new("Numbers", "Code point", cp.Hex));
        list.Add(new("Numbers", "Decimal", v.ToString(CultureInfo.InvariantCulture)));
        list.Add(new("Numbers", "Hexadecimal", "0x" + v.ToString("X", CultureInfo.InvariantCulture)));
        list.Add(new("Numbers", "Octal", "0" + Convert.ToString(v, 8)));
        list.Add(new("Numbers", "Binary", Binary(v)));

        // ---- unicode encodings
        string utf16 = cp.Text;
        if (scalar)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(utf16);
            list.Add(new("Unicode encodings", "UTF-8 (hex bytes)", Hex(utf8)));
            list.Add(new("Unicode encodings", "UTF-8 (decimal bytes)", string.Join(' ', utf8.Select(b => b.ToString(CultureInfo.InvariantCulture)))));
        }

        list.Add(new("Unicode encodings", "UTF-16 (code units)", string.Join(' ', utf16.Select(c => ((int)c).ToString("X4", CultureInfo.InvariantCulture)))));
        list.Add(new("Unicode encodings", "UTF-16LE (hex bytes)", Hex(Encoding.Unicode.GetBytes(utf16))));
        list.Add(new("Unicode encodings", "UTF-16BE (hex bytes)", Hex(Encoding.BigEndianUnicode.GetBytes(utf16))));
        if (scalar)
        {
            list.Add(new("Unicode encodings", "UTF-32 (code unit)", v.ToString("X8", CultureInfo.InvariantCulture)));
            list.Add(new("Unicode encodings", "UTF-32LE (hex bytes)", Hex(Encoding.UTF32.GetBytes(utf16))));
        }
        else
        {
            list.Add(new("Unicode encodings", "Not a scalar value", "no UTF-8 or UTF-32 form", "A surrogate code point is half of a UTF-16 pair and cannot stand alone in UTF-8."));
        }

        // ---- windows
        if (AltCodes.Oem(v) is { } oem)
            list.Add(new("Windows", "Alt code (OEM)", "Alt+" + oem.ToString(CultureInfo.InvariantCulture), "Hold Alt and type the number on the numeric keypad. Characters above 127 are those of the OEM code page (437 on a US system)."));
        if (AltCodes.Ansi(v) is { } ansi)
            list.Add(new("Windows", "Alt code (ANSI)", "Alt+0" + ansi.ToString("D3", CultureInfo.InvariantCulture), "Hold Alt and type the number, with its leading 0, on the numeric keypad (Windows-1252)."));
        if (scalar)
        {
            list.Add(new("Windows", "Alt code (Unicode, hex)", "Alt+(+)" + v.ToString("X", CultureInfo.InvariantCulture),
                         "Hold Alt, press + on the keypad, then type the hex digits. Needs the EnableHexNumpad setting (a string value \"1\" in HKCU\\Control Panel\\Input Method)."));
            list.Add(new("Windows", "Word / WordPad (Alt+X)", v.ToString("X", CultureInfo.InvariantCulture) + "  Alt+X", "Type the hex digits after the cursor, then press Alt+X."));
            list.Add(new("Windows", "GTK / Linux input", "Ctrl+Shift+U " + v.ToString("x", CultureInfo.InvariantCulture) + " Enter"));
        }

        // ---- web
        if (scalar)
        {
            list.Add(new("Web", "HTML / XML (decimal)", "&#" + v.ToString(CultureInfo.InvariantCulture) + ";"));
            list.Add(new("Web", "HTML / XML (hex)", "&#x" + v.ToString("X", CultureInfo.InvariantCulture) + ";"));
        }

        if (info is not null)
            foreach (string entity in info.HtmlEntities)
                list.Add(new("Web", "HTML entity", "&" + entity + ";"));

        if (scalar)
        {
            list.Add(new("Web", "CSS escape", "\\" + v.ToString("X", CultureInfo.InvariantCulture), "Follow it with a space when the next character is a hex digit."));
            list.Add(new("Web", "URL (percent-encoded UTF-8)", string.Concat(Encoding.UTF8.GetBytes(utf16).Select(b => "%" + b.ToString("X2", CultureInfo.InvariantCulture)))));
        }

        // ---- source code
        string units = string.Concat(utf16.Select(c => "\\u" + ((int)c).ToString("X4", CultureInfo.InvariantCulture)));
        list.Add(new("Source code", "C# / Java / JavaScript / JSON", scalar ? units : units.ToLowerInvariant(), "UTF-16 code units: a pair for a character above U+FFFF."));
        if (scalar)
        {
            list.Add(new("Source code", "C# / C / C++ / Python / Go (32-bit)", "\\U" + v.ToString("X8", CultureInfo.InvariantCulture)));
            list.Add(new("Source code", "JavaScript (ES6) / Rust / Swift / PowerShell 7", "\\u{" + v.ToString("X", CultureInfo.InvariantCulture) + "}"));
            list.Add(new("Source code", "C / C++ (UTF-8 bytes)", string.Concat(Encoding.UTF8.GetBytes(utf16).Select(b => "\\x" + b.ToString("X2", CultureInfo.InvariantCulture)))));
            list.Add(new("Source code", "Perl / PCRE / Ruby (regex)", "\\x{" + v.ToString("X", CultureInfo.InvariantCulture) + "}"));
            list.Add(new("Source code", "PowerShell", "[char]::ConvertFromUtf32(0x" + v.ToString("X", CultureInfo.InvariantCulture) + ")"));
            list.Add(new("Source code", "PostgreSQL", "U&'\\+" + v.ToString("X6", CultureInfo.InvariantCulture) + "'"));
            if (info is { IsAssigned: true }) list.Add(new("Source code", "Python (by name)", "\\N{" + info.Name + "}"));
        }

        // ---- case and normalization
        if (scalar && cp.TryGetRune(out var rune))
        {
            AddCase(list, "Uppercase", Rune.ToUpperInvariant(rune), rune);
            AddCase(list, "Lowercase", Rune.ToLowerInvariant(rune), rune);
        }

        if (scalar)
        {
            foreach (var (name, form) in new[]
                                         {
                                             ("NFC (composed)", NormalizationForm.FormC), ("NFD (decomposed)", NormalizationForm.FormD),
                                             ("NFKC (compatibility composed)", NormalizationForm.FormKC), ("NFKD (compatibility decomposed)", NormalizationForm.FormKD)
                                         })
            {
                string? normalized = TryNormalize(utf16, form);
                if (normalized is not null && normalized != utf16)
                    list.Add(new("Case and normalization", name, string.Join(' ', CodePoint.Enumerate(normalized).Select(c => c.Hex)),
                                 normalized.Length == 0 ? string.Empty : "= " + normalized));
            }
        }

        if (info is { Decomposition.Length: > 0 })
            list.Add(new("Case and normalization", "Decomposition (" + info.DecompositionKind + ")", string.Join(' ', info.DecompositionCodePoints.Select(c => c.Hex))));

        return list;
    }

    /// <summary>The entries of one group.</summary>
    public static IEnumerable<EncodingEntry> InGroup(IEnumerable<EncodingEntry> entries, string group) => entries.Where(e => e.Group == group);

    private static void AddCase(List<EncodingEntry> list, string name, Rune mapped, Rune original)
    {
        if (mapped != original)
            list.Add(new("Case and normalization", name, "U+" + mapped.Value.ToString("X4", CultureInfo.InvariantCulture), "= " + mapped));
    }

    private static string? TryNormalize(string text, NormalizationForm form)
    {
        try
        {
            return text.Normalize(form);
        }
        catch (Exception ex) when (ex is ArgumentException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    private static string Hex(byte[] bytes) => string.Join(' ', bytes.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));

    private static string Binary(int value)
    {
        string bits = Convert.ToString(value, 2);
        int    pad  = (4 - bits.Length % 4) % 4;
        bits = new string('0', pad) + bits;

        var sb = new StringBuilder();
        for (int i = 0; i < bits.Length; i += 4)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(bits, i, 4);
        }

        return sb.ToString();
    }
}
