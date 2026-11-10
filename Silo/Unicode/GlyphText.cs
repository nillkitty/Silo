using System.Text;

namespace Silo.Unicode;

/// <summary>
///     Text made of glyphs for the clipboard. An emoji can be drawn as a coloured picture or as a plain monochrome glyph; what follows it decides
///     (U+FE0F asks for the picture, U+FE0E for the text form), and with neither it is up to the program that shows it. <see cref="AsText" />
///     adds U+FE0E so that the glyph pastes as text.
/// </summary>
public static class GlyphText
{
    /// <summary>VARIATION SELECTOR-15: the character before it is drawn as text.</summary>
    public const int TextSelector = 0xFE0E;

    /// <summary>VARIATION SELECTOR-16: the character before it is drawn as an emoji picture.</summary>
    public const int EmojiSelector = 0xFE0F;

    private const int ZeroWidthJoiner = 0x200D;

    /// <summary>
    ///     Whether a character is one that U+FE0E can follow: it has the Emoji property and is not plain ASCII (the digits, # and * are emoji only as
    ///     the start of a keycap), not a skin tone modifier (it is part of another character's look), and not a regional indicator (a flag is two of
    ///     them, and a selector between them would split it).
    /// </summary>
    public static bool CanForceText(UnicodeCharacter info)
    {
        int cp = info.CodePoint.Value;
        return info.IsEmoji && cp > 0x7F && !info.IsEmojiModifier && cp is not (>= 0x1F1E6 and <= 0x1F1FF);
    }

    /// <summary>
    ///     The text with U+FE0E after every emoji that can take it, so that each pastes as a text glyph. An emoji that already has U+FE0F has it
    ///     changed to U+FE0E; one that has U+FE0E is left; one joined to the next with a zero width joiner is left (a selector would break the
    ///     sequence). Text that has no emoji comes back as it was.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="info">Tells what a code point is (the Unicode database's <c>Get</c>); null leaves the text alone.</param>
    public static string AsText(string text, Func<int, UnicodeCharacter>? info)
    {
        if (info is null || text.Length == 0) return text;

        var sb      = new StringBuilder(text.Length + 4);
        var runes   = text.EnumerateRunes().ToList();
        bool changed = false;

        for (int i = 0; i < runes.Count; i++)
        {
            int cp = runes[i].Value;
            // the picture selector of a character that is not joined to the next becomes the text selector
            if (cp == EmojiSelector && i > 0 && CanForceText(info(runes[i - 1].Value)) && !(i + 1 < runes.Count && runes[i + 1].Value == ZeroWidthJoiner))
            {
                sb.Append(char.ConvertFromUtf32(TextSelector));
                changed = true;
                continue;
            }

            sb.Append(runes[i].ToString());

            int next = i + 1 < runes.Count ? runes[i + 1].Value : -1;
            // a selector or a joiner already follows it, or a skin tone does (the tone has to touch its base)
            if (next is TextSelector or EmojiSelector or ZeroWidthJoiner or (>= 0x1F3FB and <= 0x1F3FF)) continue;

            // the last part of a joined sequence: the sequence is one picture, not a glyph to turn into text
            if (i > 0 && runes[i - 1].Value == ZeroWidthJoiner) continue;

            if (CanForceText(info(cp)))
            {
                sb.Append(char.ConvertFromUtf32(TextSelector));
                changed = true;
            }
        }

        return changed ? sb.ToString() : text;
    }
}
