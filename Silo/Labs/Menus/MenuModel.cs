using System.Text;
using Newtonsoft.Json;

namespace Silo.Labs.Menus;

/// <summary>What the menu is for: a menu bar (across the top of a window) or a context menu (opened with the right button).</summary>
public enum MenuKind
{
    Bar,
    Context
}

/// <summary>
///     One entry of a menu: a command, a separator or a submenu. The fields are those of the Silo's own <c>ProvidedCommand</c> (id, header, icon, tooltip, shortcut, check mark, default
///     and destructive), so what is made here says what a command in the Explorer's menus does.
/// </summary>
public sealed class MenuItemDef
{
    /// <summary>The text, in which an underscore marks the key that chooses the entry once the menu is open ("_Open" is chosen with O).</summary>
    public string Header { get; set; } = string.Empty;

    /// <summary>A line between groups of entries. Nothing else of the entry matters.</summary>
    public bool Separator { get; set; }

    /// <summary>What the command is called in the program ("file.open"); what the Tag says in the XAML.</summary>
    public string? Id { get; set; }

    /// <summary>The shortcut, as text ("Ctrl+Shift+O", "F5"). It is shown at the right of the entry.</summary>
    public string? Gesture { get; set; }

    /// <summary>The key of one of the Silo's icons ("TbSave").</summary>
    public string? IconKey { get; set; }

    public string? ToolTip { get; set; }

    /// <summary>A switch: choosing the entry turns a check mark on or off.</summary>
    public bool Checkable { get; set; }

    /// <summary>Whether the check mark is on to begin with (only for a checkable entry).</summary>
    public bool Checked { get; set; }

    [System.ComponentModel.DefaultValue(true)]
    public bool Enabled { get; set; } = true;

    /// <summary>The command a double-click would run: shown in bold.</summary>
    public bool Default { get; set; }

    /// <summary>Deletes or discards something: shown in red.</summary>
    public bool Destructive { get; set; }

    /// <summary>What a submenu holds; an entry with some is a submenu and is not run itself.</summary>
    public List<MenuItemDef> Children { get; set; } = [];

    [JsonIgnore]
    public bool IsSubmenu => !Separator && Children.Count > 0;

    /// <summary>The key that chooses the entry when the menu is open (the one after the first single underscore), or null.</summary>
    [JsonIgnore]
    public char? AccessKey => AccessKeyOf(Header);

    /// <summary>The header as it is read, without the underscore that marks the key.</summary>
    [JsonIgnore]
    public string Display => Separator ? "────────" : Unmark(Header);

    // what is left at its usual value is left out of the JSON, to keep it short
    public bool ShouldSerializeHeader() => !Separator;

    public bool ShouldSerializeChildren() => Children.Count > 0;

    public MenuItemDef Clone()
    {
        var copy = (MenuItemDef)MemberwiseClone();
        copy.Children = Children.Select(c => c.Clone()).ToList();
        return copy;
    }

    public static char? AccessKeyOf(string? header)
    {
        if (string.IsNullOrEmpty(header)) return null;

        for (int i = 0; i < header.Length - 1; i++)
        {
            if (header[i] != '_') continue;
            if (header[i + 1] == '_') { i++; continue; }

            return char.ToUpperInvariant(header[i + 1]);
        }

        return null;
    }

    public static string Unmark(string header)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < header.Length; i++)
        {
            if (header[i] == '_' && i + 1 < header.Length)
            {
                if (header[i + 1] == '_') sb.Append('_');
                i++;
                if (header[i] != '_') sb.Append(header[i]);
                continue;
            }

            sb.Append(header[i]);
        }

        return sb.ToString();
    }

    /// <summary>A separator.</summary>
    public static MenuItemDef Line() => new() { Separator = true };
}

/// <summary>A menu: what it is called, what it is for, and its entries. This is what the Silo keeps, and (as JSON) what the menu is made from; the XAML is made from it.</summary>
public sealed class MenuDoc
{
    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public MenuKind Kind { get; set; } = MenuKind.Context;

    public List<MenuItemDef> Items { get; set; } = [];

    public MenuDoc Clone()
    {
        var copy = (MenuDoc)MemberwiseClone();
        copy.Items = Items.Select(i => i.Clone()).ToList();
        return copy;
    }

    /// <summary>Every entry in order, with where it is (the position at each level, from 0) and how deep.</summary>
    public IEnumerable<(int[] Path, MenuItemDef Item, int Depth)> Walk()
    {
        return WalkList(Items, []);
    }

    private static IEnumerable<(int[] Path, MenuItemDef Item, int Depth)> WalkList(List<MenuItemDef> items, int[] parent)
    {
        for (int i = 0; i < items.Count; i++)
        {
            int[] path = [.. parent, i];
            yield return (path, items[i], parent.Length);
            foreach (var inner in WalkList(items[i].Children, path)) yield return inner;
        }
    }

    [JsonIgnore]
    public int Count => Walk().Count();
}

/// <summary>Changing a menu's tree: each returns the position that the entry that was worked on has afterwards (null when there is none).</summary>
public static class MenuEdit
{
    public static bool TryGet(MenuDoc doc, int[] path, out MenuItemDef item, out List<MenuItemDef> list, out int index)
    {
        item  = null!;
        list  = doc.Items;
        index = -1;
        if (path.Length == 0) return false;

        for (int level = 0; level < path.Length; level++)
        {
            if (path[level] < 0 || path[level] >= list.Count) return false;

            index = path[level];
            item  = list[index];
            if (level < path.Length - 1) list = item.Children;
        }

        return true;
    }

    public static MenuItemDef? Find(MenuDoc doc, int[]? path) => path is not null && TryGet(doc, path, out var item, out _, out _) ? item : null;

    /// <summary>Puts the entry after the one at <paramref name="after" /> (at the end of the menu when null), or as the last entry of the submenu when <paramref name="into" />.</summary>
    public static int[] Add(MenuDoc doc, MenuItemDef item, int[]? after = null, bool into = false)
    {
        if (after is not null && TryGet(doc, after, out var target, out var list, out int index))
        {
            if (into && !target.Separator)
            {
                target.Children.Add(item);
                return [.. after, target.Children.Count - 1];
            }

            list.Insert(index + 1, item);
            return [.. after[..^1], index + 1];
        }

        doc.Items.Add(item);
        return [doc.Items.Count - 1];
    }

    public static int[]? Remove(MenuDoc doc, int[] path)
    {
        if (!TryGet(doc, path, out _, out var list, out int index)) return null;

        list.RemoveAt(index);
        if (list.Count == 0) return path.Length > 1 ? path[..^1] : null;

        return [.. path[..^1], Math.Min(index, list.Count - 1)];
    }

    /// <summary>Moves the entry up (-1) or down (1) among those of its menu.</summary>
    public static int[]? Move(MenuDoc doc, int[] path, int delta)
    {
        if (!TryGet(doc, path, out var item, out var list, out int index)) return null;

        int to = Math.Clamp(index + delta, 0, list.Count - 1);
        if (to == index) return path;

        list.RemoveAt(index);
        list.Insert(to, item);
        return [.. path[..^1], to];
    }

    /// <summary>Makes the entry the last of the submenu of the entry above it.</summary>
    public static int[]? Indent(MenuDoc doc, int[] path)
    {
        if (!TryGet(doc, path, out var item, out var list, out int index) || index == 0) return null;

        var above = list[index - 1];
        if (above.Separator) return null;

        list.RemoveAt(index);
        above.Children.Add(item);
        return [.. path[..^1], index - 1, above.Children.Count - 1];
    }

    /// <summary>Takes the entry out of its submenu and puts it after the submenu.</summary>
    public static int[]? Outdent(MenuDoc doc, int[] path)
    {
        if (path.Length < 2 || !TryGet(doc, path, out var item, out var list, out int index)) return null;

        int[] parentPath = path[..^1];
        TryGet(doc, parentPath, out _, out var parentList, out int parentIndex);
        list.RemoveAt(index);
        parentList.Insert(parentIndex + 1, item);
        return [.. parentPath[..^1], parentIndex + 1];
    }

    public static int[]? Duplicate(MenuDoc doc, int[] path)
    {
        if (!TryGet(doc, path, out var item, out var list, out int index)) return null;

        var copy = item.Clone();
        if (!string.IsNullOrEmpty(copy.Id)) copy.Id += ".copy";
        list.Insert(index + 1, copy);
        return [.. path[..^1], index + 1];
    }
}

/// <summary>Shortcuts as the menu shows them: the modifiers (Ctrl, Alt, Shift, Win) and a key, written the way WPF's KeyGesture reads them.</summary>
public static class MenuGesture
{
    private static readonly string[] Named =
    [
        "Enter", "Escape", "Space", "Tab", "Back", "Delete", "Insert", "Home", "End", "PageUp", "PageDown", "Up", "Down", "Left", "Right", "Add", "Subtract", "Multiply", "Divide", "OemPlus", "OemMinus",
        "OemComma", "OemPeriod", "OemQuestion", "OemTilde", "OemOpenBrackets", "OemCloseBrackets", "OemPipe", "OemSemicolon", "OemQuotes", "PrintScreen", "Pause", "Apps", "BrowserBack", "BrowserForward"
    ];

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
                                                               {
                                                                   ["control"] = "Ctrl", ["windows"] = "Win", ["esc"] = "Escape", ["return"] = "Enter", ["backspace"] = "Back", ["del"] = "Delete",
                                                                   ["ins"] = "Insert", ["pgup"] = "PageUp", ["pgdn"] = "PageDown", ["pagedn"] = "PageDown", ["+"] = "OemPlus", ["plus"] = "OemPlus", ["-"] = "OemMinus",
                                                                   ["minus"] = "OemMinus", [","] = "OemComma", ["."] = "OemPeriod", ["/"] = "OemQuestion", ["`"] = "OemTilde", ["["] = "OemOpenBrackets",
                                                                   ["]"] = "OemCloseBrackets", ["\\"] = "OemPipe", [";"] = "OemSemicolon", ["'"] = "OemQuotes"
                                                               };

    /// <summary>
    ///     Reads a shortcut and writes it in the one way ("ctrl+shift+o" becomes "Ctrl+Shift+O"). Returns false, with what is wrong, for a key that is not known, a key typed twice, or a letter
    ///     or digit with no Ctrl, Alt or Win to go with it (a window cannot have a shortcut that is just a letter).
    /// </summary>
    public static bool TryNormalize(string? text, out string normalized, out string? problem)
    {
        normalized = string.Empty;
        problem    = null;
        if (string.IsNullOrWhiteSpace(text)) return true;

        // "Ctrl++" is Ctrl and the plus key
        var parts = new List<string>();
        var raw   = text.Trim();
        if (raw == "+") parts.Add("+");
        else if (raw.EndsWith("++", StringComparison.Ordinal))
        {
            if (raw.Length > 2) parts.AddRange(raw[..^2].Split('+').Select(p => p.Trim()));
            parts.Add("+");
        }
        else
            parts.AddRange(raw.Split('+').Select(p => p.Trim()));

        bool ctrl = false, alt = false, shift = false, win = false;
        string? key = null;
        foreach (var part in parts)
        {
            if (part.Length == 0) { problem = "There is an empty part between two plus signs."; return false; }

            string name = Aliases.TryGetValue(part, out var alias) ? alias : part;
            switch (name.ToLowerInvariant())
            {
                case "ctrl": ctrl = true; continue;
                case "alt": alt = true; continue;
                case "shift": shift = true; continue;
                case "win": win = true; continue;
            }

            if (key is not null) { problem = $"There are two keys, {key} and {name}; a shortcut has one key and any of Ctrl, Alt, Shift and Win."; return false; }

            if (name.Length == 1 && char.IsAsciiLetter(name[0])) key = name.ToUpperInvariant();
            else if (name.Length == 1 && char.IsAsciiDigit(name[0])) key = name;
            else if (name.Length is 2 or 3 && (name[0] is 'F' or 'f') && int.TryParse(name[1..], out int f) && f is >= 1 and <= 24) key = "F" + f;
            else if (Named.FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } known) key = known;
            else { problem = $"\"{part}\" is not a key a shortcut can use."; return false; }
        }

        if (key is null) { problem = "A shortcut needs a key as well as Ctrl, Alt, Shift or Win."; return false; }

        bool plain = key.Length == 1 && (char.IsAsciiLetterOrDigit(key[0]));
        if (plain && !ctrl && !alt && !win) { problem = $"{key} alone cannot be a shortcut: a letter or digit needs Ctrl, Alt or Win with it."; return false; }

        normalized = string.Concat(ctrl ? "Ctrl+" : "", alt ? "Alt+" : "", shift ? "Shift+" : "", win ? "Win+" : "", key);
        return true;
    }
}
