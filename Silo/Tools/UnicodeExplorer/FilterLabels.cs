using Silo.Unicode;

namespace Silo.Tools.UnicodeExplorer;

/// <summary>The words the explorer uses for the filter choices, in the menus and in the line under the search box.</summary>
internal static class FilterLabels
{
    private static readonly Dictionary<string, string> MajorNames = new()
                                                                    {
                                                                        ["L"] = "Letters", ["M"] = "Marks", ["N"] = "Numbers", ["P"] = "Punctuation",
                                                                        ["S"] = "Symbols", ["Z"] = "Separators", ["C"] = "Other (controls, format, private use…)"
                                                                    };

    /// <summary>The plural name of a one-letter group (<c>Letters</c>).</summary>
    public static string Major(string code) => MajorNames.TryGetValue(code, out string? name) ? name : code;

    /// <summary>A category or a group of them, as a person reads it: <c>Letters</c>, <c>Lu – Uppercase Letter</c>.</summary>
    public static string Category(string code)
    {
        return code.Length == 1 ? Major(code) : $"{code} – {UnicodeNames.Category(code)?.Name ?? code}";
    }

    public static string Script(UnicodeDatabase? db, string code) => db?.ScriptName(code) is { Length: > 0 } name ? name : code;

    public static string Kind(GlyphKind kind)
    {
        return kind switch
               {
                   GlyphKind.Everything    => "Everything",
                   GlyphKind.AssignedOnly  => "Assigned only",
                   GlyphKind.AvailableOnly => "Available only",
                   GlyphKind.PrintableOnly => "Printable only",
                   GlyphKind.EmojiOnly     => "Emoji only",
                   GlyphKind.NonEmojiOnly  => "Non-emoji only",
                   _                       => "Joinable only"
               };
    }

    public static string KindMenu(GlyphKind kind)
    {
        return kind switch
               {
                   GlyphKind.Everything    => "Everything",
                   GlyphKind.AssignedOnly  => "Assigned Only",
                   GlyphKind.AvailableOnly => "Available Only",
                   GlyphKind.PrintableOnly => "Printable Only",
                   GlyphKind.EmojiOnly     => "Emoji Only",
                   GlyphKind.NonEmojiOnly  => "Non-Emoji Only",
                   _                       => "Joinable Only"
               };
    }

    public static string KindTip(GlyphKind kind)
    {
        return kind switch
               {
                   GlyphKind.Everything    => "Every character, drawable or not. With a block chosen, its unassigned code points too.",
                   GlyphKind.AssignedOnly  => "Every assigned character, whether or not a font on this computer can draw it.",
                   GlyphKind.AvailableOnly => "Assigned characters a font on this computer can draw (controls, spaces and format characters stay: they are meant to be invisible).",
                   GlyphKind.PrintableOnly => "Available characters that show something: no controls, spaces or format characters.",
                   GlyphKind.EmojiOnly     => "Available characters that are emoji.",
                   GlyphKind.NonEmojiOnly  => "Available characters that are not emoji.",
                   _                       => "The zero width joiner and non-joiner and the pictographs a joiner can join into sequences (family, profession, couple emoji)."
               };
    }

    public static string Op(VersionOp op) => op.ToString();

    public static string Version(VersionFilter v)
    {
        string verb = v.Mode == VersionMode.Exclude ? "Not added " : "Added ";
        return verb + v.Op.ToString().ToLowerInvariant() + " " + v.Version;
    }

    public static string Color(int filter)
    {
        return filter switch
               {
                   -1 => "marked (any color)",
                   -2 => "not marked",
                   _  => CharacterColors.At(filter)?.Name ?? "?"
               };
    }
}
