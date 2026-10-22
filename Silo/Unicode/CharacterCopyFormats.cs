using System.Globalization;
using System.Net;

namespace Silo.Unicode;

/// <summary>How a mark color is shown on a character.</summary>
public enum ColorMode
{
    /// <summary>The character itself is drawn in the color.</summary>
    Glyph,

    /// <summary>The color fills the area behind the character.</summary>
    Background,

    /// <summary>A bar of the color along the bottom of the character's cell.</summary>
    Accent
}

/// <summary>
///     A character written the way source code and markup need it (the Developer menu's "Copy as"): escapes for C-like languages, entities, PowerShell and
///     Visual Basic, and HTML and XAML that carry the character's mark color.
/// </summary>
public static class CharacterCopyFormats
{
    private static string Hex(int value, int digits = 1) => value.ToString("X" + digits, CultureInfo.InvariantCulture);

    /// <summary>
    ///     A C, C# or C++ escape: <c>'ဗ'</c> for a character in the Basic Multilingual Plane (a char literal), <c>"\U0001F7E2"</c> above it (a character
    ///     of its own cannot fit a char literal, so it is a string of one).
    /// </summary>
    public static string CEscape(CodePoint cp)
    {
        return cp.Value <= 0xFFFF ? "'\\u" + Hex(cp.Value, 4) + "'" : "\"\\U" + Hex(cp.Value, 8) + "\"";
    }

    /// <summary>An XML and HTML character reference in hex: <c>&amp;#x1F7E2;</c>.</summary>
    public static string Entity(CodePoint cp) => "&#x" + Hex(cp.Value) + ";";

    /// <summary>PowerShell: <c>[char]0x1017</c> for a code unit, <c>[char]::ConvertFromUtf32(0x1F7E2)</c> for a character above U+FFFF.</summary>
    public static string PowerShell(CodePoint cp)
    {
        return cp.Value <= 0xFFFF ? "[char]0x" + Hex(cp.Value, 4) : "[char]::ConvertFromUtf32(0x" + Hex(cp.Value) + ")";
    }

    /// <summary>Visual Basic: <c>ChrW(&amp;H1017)</c> for a code unit, <c>Char.ConvertFromUtf32(&amp;H1F7E2)</c> above U+FFFF.</summary>
    public static string VisualBasic(CodePoint cp)
    {
        return cp.Value <= 0xFFFF ? "ChrW(&H" + Hex(cp.Value, 4) + ")" : "Char.ConvertFromUtf32(&H" + Hex(cp.Value) + ")";
    }

    /// <summary>
    ///     The Alt code that types the character in Windows: <c>Alt+0233</c> (ANSI) or <c>Alt+130</c> (OEM) when it has one, else the hexadecimal
    ///     form <c>Alt+(+)1F7E2</c> (which needs the EnableHexNumpad setting).
    /// </summary>
    public static string AltCode(CodePoint cp)
    {
        if (AltCodes.Ansi(cp.Value) is { } ansi) return "Alt+0" + ansi.ToString("D3", CultureInfo.InvariantCulture);
        if (AltCodes.Oem(cp.Value) is { } oem) return "Alt+" + oem.ToString(CultureInfo.InvariantCulture);
        return "Alt+(+)" + Hex(cp.Value);
    }

    /// <summary>
    ///     The character as HTML, showing its mark color: in <c>color:</c> (<see cref="ColorMode.Glyph" />), <c>background-color:</c>
    ///     (<see cref="ColorMode.Background" />) or an underline (<see cref="ColorMode.Accent" />). Without a color it is just the escaped character.
    /// </summary>
    /// <param name="cp">The character.</param>
    /// <param name="hexColor">The color as <c>#RRGGBB</c>; null for none.</param>
    /// <param name="mode">How the color is shown.</param>
    public static string ColoredHtml(CodePoint cp, string? hexColor, ColorMode mode)
    {
        string text = WebUtility.HtmlEncode(cp.Text);
        if (string.IsNullOrEmpty(hexColor)) return text;

        string style = mode switch
                       {
                           ColorMode.Background => $"background-color:{hexColor};",
                           ColorMode.Accent     => $"border-bottom:3px solid {hexColor};",
                           _                    => $"color:{hexColor};"
                       };
        return $"<span style=\"{style}\">{text}</span>";
    }

    /// <summary>
    ///     The character as XAML showing its mark color: a <c>TextBlock</c> with a <c>Foreground</c>, or inside a <c>Border</c> with a <c>Background</c> or a
    ///     bottom <c>BorderBrush</c>. Without a color it is a plain <c>TextBlock</c>.
    /// </summary>
    public static string ColoredXaml(CodePoint cp, string? hexColor, ColorMode mode)
    {
        string text = Entity(cp);
        if (string.IsNullOrEmpty(hexColor)) return $"<TextBlock Text=\"{text}\" />";

        return mode switch
               {
                   ColorMode.Background => $"<Border Background=\"{hexColor}\"><TextBlock Text=\"{text}\" /></Border>",
                   ColorMode.Accent     => $"<Border BorderBrush=\"{hexColor}\" BorderThickness=\"0,0,0,3\"><TextBlock Text=\"{text}\" /></Border>",
                   _                    => $"<TextBlock Text=\"{text}\" Foreground=\"{hexColor}\" />"
               };
    }
}
