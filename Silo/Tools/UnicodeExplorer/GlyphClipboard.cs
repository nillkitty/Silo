using Silo.Storage;
using Silo.Unicode;

namespace Silo.Tools.UnicodeExplorer;

/// <summary>
///     What glyphs become on their way out of the explorer (the clipboard, a drag to another program, a click in a palette): by the Preferences,
///     emoji get U+FE0E after them (<see cref="GlyphText.AsText" />). The choice is read from the Silo's settings each time (it is small), so the
///     explorer and the palette windows, which can be open without it, agree.
/// </summary>
internal static class GlyphClipboard
{
    private const string SettingsKey = "unicodeexplorer:settings";

    /// <summary>Whether emoji are to be copied as text (on unless the Silo's settings say otherwise).</summary>
    public static bool EmojiAsText()
    {
        try
        {
            return Stored.At<ExplorerSettings>(SettingsKey).Get()?.CopyEmojiAsText ?? true;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>The glyphs as they should leave the explorer: emoji as text when the Preferences say so.</summary>
    public static string ForCopy(string glyphs)
    {
        if (!EmojiAsText()) return glyphs;

        try
        {
            var db = UnicodeDatabase.Default;
            return GlyphText.AsText(glyphs, cp => db.Get(cp));
        }
        catch (Exception)
        {
            return glyphs;
        }
    }
}
