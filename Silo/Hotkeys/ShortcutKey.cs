using System.Globalization;
using Newtonsoft.Json;

namespace Silo.Hotkeys;

/// <summary>The modifier keys of a shortcut. The values are the ones Windows uses for hot keys (<c>MOD_ALT</c>, <c>MOD_CONTROL</c>, <c>MOD_SHIFT</c>, <c>MOD_WIN</c>).</summary>
[Flags]
public enum ShortcutModifiers
{
    /// <summary>No modifier.</summary>
    None = 0,

    /// <summary>The Alt key.</summary>
    Alt = 1,

    /// <summary>The Ctrl key.</summary>
    Control = 2,

    /// <summary>The Shift key.</summary>
    Shift = 4,

    /// <summary>The Windows key.</summary>
    Windows = 8
}

/// <summary>A key with its modifiers: <c>Ctrl+Win+J</c>. Written to JSON as that text.</summary>
/// <param name="Modifiers">The modifier keys that are held.</param>
/// <param name="VirtualKey">The Windows virtual-key code of the key (<c>0x4A</c> for J); 0 for none.</param>
[JsonConverter(typeof(ShortcutKeyJsonConverter))]
public readonly record struct ShortcutKey(ShortcutModifiers Modifiers, int VirtualKey)
{
    // key name -> virtual key; the first name listed for a code is the one shown
    private static readonly (string Name, int Vk)[] Names = BuildNames();

    private static readonly Dictionary<string, int> ByName = BuildByName();

    private static readonly Dictionary<int, string> ByVk = Names.GroupBy(n => n.Vk).ToDictionary(g => g.Key, g => g.First().Name);

    /// <summary>No shortcut.</summary>
    public static ShortcutKey None => default;

    /// <summary>Whether a key is set.</summary>
    public bool IsSet => VirtualKey != 0;

    /// <summary>The key's own name, without modifiers: <c>J</c>, <c>F5</c>, <c>PageUp</c>; empty when none is set.</summary>
    public string KeyName => ByVk.TryGetValue(VirtualKey, out string? n) ? n : VirtualKey == 0 ? string.Empty : "VK" + VirtualKey.ToString("X2", CultureInfo.InvariantCulture);

    /// <summary>The shortcut as text, modifiers first: <c>Ctrl+Alt+Shift+Win+J</c>; empty when none is set.</summary>
    public override string ToString()
    {
        if (!IsSet) return string.Empty;

        var parts = new List<string>(5);
        if (Modifiers.HasFlag(ShortcutModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ShortcutModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ShortcutModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ShortcutModifiers.Windows)) parts.Add("Win");
        parts.Add(KeyName);
        return string.Join('+', parts);
    }

    /// <summary>The shortcut for a key written as a name (<c>J</c>, <c>F5</c>, <c>PageDown</c>, <c>;</c>); false for a name that is none.</summary>
    public static bool TryFromKeyName(string name, ShortcutModifiers modifiers, out ShortcutKey key)
    {
        key = default;
        if (!ByName.TryGetValue(name.Trim(), out int vk)) return false;
        key = new ShortcutKey(modifiers, vk);
        return true;
    }

    /// <summary>Reads <c>Ctrl+Win+J</c> (modifier and key names in any order and case; <c>Control</c>, <c>Windows</c>, <c>Cmd</c> also work; <c>Ctrl++</c> is Ctrl and the plus key).</summary>
    public static bool TryParse(string? text, out ShortcutKey key)
    {
        key = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string t = text.Trim();

        // "Ctrl++" and "+" end in the plus key itself; a lone trailing separator ("Ctrl+") names no key
        bool plusKey = false;
        if (t.EndsWith('+'))
        {
            string rest = t[..^1];
            if (rest.Length > 0 && !rest.EndsWith('+')) return false;
            plusKey = true;
            t       = rest.Length > 0 ? rest[..^1] : string.Empty;
        }

        var  modifiers = ShortcutModifiers.None;
        int? vk        = null;
        foreach (string raw in t.Length == 0 ? [] : t.Split('+'))
        {
            string part = raw.Trim();
            if (part.Length == 0) return false;

            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control" or "ctl":
                    modifiers |= ShortcutModifiers.Control;
                    break;
                case "alt" or "option" or "opt":
                    modifiers |= ShortcutModifiers.Alt;
                    break;
                case "shift":
                    modifiers |= ShortcutModifiers.Shift;
                    break;
                case "win" or "windows" or "meta" or "super" or "cmd" or "command":
                    modifiers |= ShortcutModifiers.Windows;
                    break;
                default:
                    if (vk is not null || !ByName.TryGetValue(part, out int code)) return false;
                    vk = code;
                    break;
            }
        }

        if (plusKey)
        {
            if (vk is not null) return false;
            vk = ByName["Plus"];
        }

        if (vk is null) return false;
        key = new ShortcutKey(modifiers, vk.Value);
        return true;
    }

    /// <summary>
    ///     Whether the shortcut can be a global one (a hot key that works in every application) without taking over normal typing: it needs
    ///     Ctrl, Alt or the Windows key held with it, unless it is one of the keys F13 to F24 that keyboards rarely have.
    /// </summary>
    /// <param name="problem">When false, what to change.</param>
    public bool IsValidGlobal(out string? problem)
    {
        problem = null;
        if (!IsSet)
        {
            problem = "Choose a key.";
            return false;
        }

        bool strong = (Modifiers & (ShortcutModifiers.Control | ShortcutModifiers.Alt | ShortcutModifiers.Windows)) != 0;
        if (strong || VirtualKey is >= 0x7C and <= 0x87) return true;

        problem = Modifiers == ShortcutModifiers.None
                      ? "A global shortcut needs Ctrl, Alt or the Windows key with it (otherwise it would take over that key everywhere)."
                      : "Shift alone is not enough for a global shortcut: add Ctrl, Alt or the Windows key.";
        return false;
    }

    private static (string, int)[] BuildNames()
    {
        var list = new List<(string, int)>();
        for (char c = 'A'; c <= 'Z'; c++) list.Add((c.ToString(), c));
        for (char c = '0'; c <= '9'; c++) list.Add((c.ToString(), c));
        for (int i = 1; i <= 24; i++) list.Add(("F" + i.ToString(CultureInfo.InvariantCulture), 0x6F + i));
        for (int i = 0; i <= 9; i++) list.Add(("NumPad" + i.ToString(CultureInfo.InvariantCulture), 0x60 + i));

        list.AddRange(
        [
            ("Space", 0x20), ("Enter", 0x0D), ("Tab", 0x09), ("Escape", 0x1B), ("Backspace", 0x08), ("Insert", 0x2D), ("Delete", 0x2E),
            ("Home", 0x24), ("End", 0x23), ("PageUp", 0x21), ("PageDown", 0x22), ("Left", 0x25), ("Up", 0x26), ("Right", 0x27), ("Down", 0x28),
            ("PrintScreen", 0x2C), ("Pause", 0x13), ("ScrollLock", 0x91), ("NumLock", 0x90), ("CapsLock", 0x14),
            ("Multiply", 0x6A), ("Add", 0x6B), ("Subtract", 0x6D), ("Decimal", 0x6E), ("Divide", 0x6F),
            (";", 0xBA), ("Plus", 0xBB), (",", 0xBC), ("-", 0xBD), (".", 0xBE), ("/", 0xBF), ("`", 0xC0), ("[", 0xDB), ("\\", 0xDC), ("]", 0xDD), ("'", 0xDE)
        ]);
        return list.ToArray();
    }

    private static Dictionary<string, int> BuildByName()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, vk) in Names) map[name] = vk;

        // other ways people write them
        foreach (var (alias, vk) in new (string, int)[]
                                    {
                                        ("Esc", 0x1B), ("Return", 0x0D), ("Del", 0x2E), ("Ins", 0x2D), ("PgUp", 0x21), ("PgDn", 0x22), ("Prior", 0x21), ("Next", 0x22),
                                        ("Back", 0x08), ("PrtSc", 0x2C), ("Snapshot", 0x2C), ("Equals", 0xBB), ("=", 0xBB), ("Minus", 0xBD), ("Comma", 0xBC),
                                        ("Period", 0xBE), ("Dot", 0xBE), ("Slash", 0xBF), ("Semicolon", 0xBA), ("Backtick", 0xC0), ("Grave", 0xC0), ("Tilde", 0xC0),
                                        ("Quote", 0xDE), ("Backslash", 0xDC), ("OpenBracket", 0xDB), ("CloseBracket", 0xDD), ("Spacebar", 0x20),
                                        ("Num0", 0x60), ("Num1", 0x61), ("Num2", 0x62), ("Num3", 0x63), ("Num4", 0x64), ("Num5", 0x65), ("Num6", 0x66), ("Num7", 0x67), ("Num8", 0x68), ("Num9", 0x69)
                                    })
            map.TryAdd(alias, vk);

        return map;
    }
}

/// <summary>Writes a <see cref="ShortcutKey" /> as its text (<c>"Ctrl+Win+J"</c>) and reads that back.</summary>
public sealed class ShortcutKeyJsonConverter : JsonConverter<ShortcutKey>
{
    /// <inheritdoc />
    public override void WriteJson(JsonWriter writer, ShortcutKey value, JsonSerializer serializer) => writer.WriteValue(value.ToString());

    /// <inheritdoc />
    public override ShortcutKey ReadJson(JsonReader reader, Type objectType, ShortcutKey existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null) return ShortcutKey.None;

        string? text = reader.Value as string;
        if (string.IsNullOrEmpty(text)) return ShortcutKey.None;
        if (ShortcutKey.TryParse(text, out var key)) return key;
        throw new JsonSerializationException($"\"{text}\" is not a shortcut key (write it like Ctrl+Win+J).");
    }
}
