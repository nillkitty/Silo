using System.Text;
using System.Xml;
using System.Xml.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;

namespace Silo.Labs.Menus;

/// <summary>A menu as JSON, which is what the menu is made from (the XAML is made from it).</summary>
public static class MenuJson
{
    private static readonly JsonSerializerSettings Settings = new()
                                                              {
                                                                  Formatting        = Newtonsoft.Json.Formatting.Indented,
                                                                  NullValueHandling = NullValueHandling.Ignore,
                                                                  DefaultValueHandling = DefaultValueHandling.Ignore,
                                                                  ContractResolver  = new CamelCasePropertyNamesContractResolver(),
                                                                  Converters        = { new StringEnumConverter(new CamelCaseNamingStrategy()) },
                                                                  MissingMemberHandling = MissingMemberHandling.Ignore
                                                              };

    public static string ToJson(MenuDoc doc) => JsonConvert.SerializeObject(doc, Settings);

    /// <summary>Reads a menu from JSON. Fails, with what is wrong and where, for JSON that is not a menu.</summary>
    public static bool TryFromJson(string? text, out MenuDoc doc, out string? error)
    {
        doc   = new MenuDoc();
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "There is no JSON.";
            return false;
        }

        try
        {
            var read = JsonConvert.DeserializeObject<MenuDoc>(text, Settings);
            if (read is null)
            {
                error = "That is not a menu.";
                return false;
            }

            read.Items ??= [];
            Fix(read.Items);
            doc = read;
            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void Fix(List<MenuItemDef> items)
    {
        items.RemoveAll(i => i is null);
        foreach (var item in items)
        {
            item.Header   ??= string.Empty;
            item.Children ??= [];
            Fix(item.Children);
        }
    }
}

public enum MenuSeverity
{
    /// <summary>Something that will not work or will not be accepted.</summary>
    Error,

    /// <summary>Something that works but is probably not what was meant.</summary>
    Warning
}

/// <summary>Something wrong with an entry (or with the menu, when <see cref="Path" /> is empty).</summary>
public sealed record MenuProblem(int[] Path, MenuSeverity Severity, string Text)
{
    public string Where => Path.Length == 0 ? "menu" : string.Join(" > ", Path.Select(p => (p + 1).ToString()));
}

/// <summary>Looks at a menu for what is wrong with it.</summary>
public static class MenuCheck
{
    public const int MaxDepth = 4;

    public static IReadOnlyList<MenuProblem> Validate(MenuDoc doc)
    {
        var problems = new List<MenuProblem>();
        if (doc.Items.Count == 0) problems.Add(new MenuProblem([], MenuSeverity.Warning, "The menu has no entries."));

        var ids      = new Dictionary<string, int[]>(StringComparer.Ordinal);
        var gestures = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);

        foreach (var (path, item, depth) in doc.Walk())
        {
            if (item.Separator)
            {
                var list = ListOf(doc, path);
                int at   = path[^1];
                if (path.Length == 1 && doc.Kind == MenuKind.Bar) problems.Add(new MenuProblem(path, MenuSeverity.Warning, "A line in the bar itself shows as a stray mark: lines belong inside menus."));
                else if (at == 0) problems.Add(new MenuProblem(path, MenuSeverity.Warning, "A menu should not start with a line."));
                else if (at == list.Count - 1) problems.Add(new MenuProblem(path, MenuSeverity.Warning, "A menu should not end with a line."));
                else if (list[at - 1].Separator) problems.Add(new MenuProblem(path, MenuSeverity.Warning, "Two lines in a row: one is enough."));
                continue;
            }

            if (string.IsNullOrWhiteSpace(item.Header)) problems.Add(new MenuProblem(path, MenuSeverity.Error, "The entry has no text."));
            if (depth >= MaxDepth) problems.Add(new MenuProblem(path, MenuSeverity.Warning, $"Menus inside menus {MaxDepth + 1} deep are hard to use."));

            if (!MenuGesture.TryNormalize(item.Gesture, out var normal, out var why))
                problems.Add(new MenuProblem(path, MenuSeverity.Error, "The shortcut: " + why));
            else if (normal.Length > 0)
            {
                if (item.IsSubmenu) problems.Add(new MenuProblem(path, MenuSeverity.Warning, "A submenu is never run, so its shortcut does nothing."));
                if (gestures.TryGetValue(normal, out var other)) problems.Add(new MenuProblem(path, MenuSeverity.Warning, $"{normal} is also the shortcut of entry {new MenuProblem(other, MenuSeverity.Warning, "").Where}."));
                else gestures[normal] = path;
            }

            if (!string.IsNullOrWhiteSpace(item.Id))
            {
                if (ids.TryGetValue(item.Id, out var other)) problems.Add(new MenuProblem(path, MenuSeverity.Error, $"The id {item.Id} is also the id of entry {new MenuProblem(other, MenuSeverity.Warning, "").Where}."));
                else ids[item.Id] = path;
            }
            else if (!item.IsSubmenu)
                problems.Add(new MenuProblem(path, MenuSeverity.Warning, "The entry has no id, so there is nothing to say which command it runs."));

            if (item.Checked && !item.Checkable) problems.Add(new MenuProblem(path, MenuSeverity.Warning, "The check mark is on but the entry is not checkable, so it is never shown."));
            if (item.Default && item.Destructive) problems.Add(new MenuProblem(path, MenuSeverity.Warning, "An entry that is both the default (bold) and destructive (red) is easy to run by mistake."));

            // two entries of one menu with the same access key: the key only reaches the first
            if (item.AccessKey is { } key)
            {
                var siblings = ListOf(doc, path);
                int first    = siblings.FindIndex(s => !s.Separator && s.AccessKey == key);
                if (first != path[^1]) problems.Add(new MenuProblem(path, MenuSeverity.Warning, $"The key {key} also chooses entry {first + 1} of this menu, which is first in line for it."));
            }
        }

        return problems;
    }

    private static List<MenuItemDef> ListOf(MenuDoc doc, int[] path)
    {
        var list = doc.Items;
        for (int level = 0; level < path.Length - 1; level++) list = list[path[level]].Children;
        return list;
    }
}

/// <summary>A menu as XAML: one entry of a resource dictionary, ready to put in a file of resources.</summary>
public static class MenuXaml
{
    public static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    public static readonly XNamespace X            = "http://schemas.microsoft.com/winfx/2006/xaml";
    public static readonly XNamespace Silo         = "clr-namespace:Silo";

    /// <summary>What the menu's resource is called: its name made into something a resource key can be (letters, digits, dots and underscores).</summary>
    public static string KeyOf(MenuDoc doc)
    {
        var key = new string(doc.Name.Trim().Select(c => char.IsLetterOrDigit(c) || c is '.' or '_' ? c : '_').ToArray());
        if (key.Length == 0) key = doc.Kind == MenuKind.Bar ? "MainMenu" : "ContextMenu";
        return char.IsDigit(key[0]) ? "_" + key : key;
    }

    public static string Generate(MenuDoc doc, bool wrap = true)
    {
        var root = new XElement(doc.Kind == MenuKind.Bar ? Presentation + "Menu" : Presentation + "ContextMenu", new XAttribute(X + "Key", KeyOf(doc)));
        foreach (var item in doc.Items) root.Add(Element(item));

        XElement top = root;
        if (wrap) top = new XElement(Presentation + "ResourceDictionary", root);

        // the prefixes are declared on the outermost element, and the silo one only when an icon uses it
        top.Add(new XAttribute(XNamespace.Xmlns + "x", X.NamespaceName));
        if (Uses(doc)) top.Add(new XAttribute(XNamespace.Xmlns + "silo", Silo.NamespaceName));

        // the default namespace is the presentation one, and the prefixes are only declared where they are used
        var settings = new XmlWriterSettings { Indent = true, IndentChars = "    ", OmitXmlDeclaration = true, NewLineChars = "\r\n", NewLineHandling = NewLineHandling.Replace, Encoding = new UTF8Encoding(false) };
        var sb       = new StringBuilder();
        using (var writer = XmlWriter.Create(sb, settings)) top.WriteTo(writer);
        return sb.ToString();
    }

    private static bool Uses(MenuDoc doc) => doc.Walk().Any(w => !w.Item.Separator && !string.IsNullOrWhiteSpace(w.Item.IconKey));

    private static XElement Element(MenuItemDef item)
    {
        if (item.Separator) return new XElement(Presentation + "Separator");

        var e = new XElement(Presentation + "MenuItem", new XAttribute("Header", item.Header));
        if (!string.IsNullOrWhiteSpace(item.Id)) e.Add(new XAttribute("Tag", item.Id));
        if (MenuGesture.TryNormalize(item.Gesture, out var gesture, out _) && gesture.Length > 0) e.Add(new XAttribute("InputGestureText", gesture));
        if (item.Checkable)
        {
            e.Add(new XAttribute("IsCheckable", "True"));
            if (item.Checked) e.Add(new XAttribute("IsChecked", "True"));
        }

        if (!item.Enabled) e.Add(new XAttribute("IsEnabled", "False"));
        if (item.Default) e.Add(new XAttribute("FontWeight", "Bold"));
        if (item.Destructive) e.Add(new XAttribute("Foreground", "#E5484D"));
        if (!string.IsNullOrWhiteSpace(item.ToolTip)) e.Add(new XAttribute("ToolTip", item.ToolTip));
        if (!string.IsNullOrWhiteSpace(item.IconKey))
            e.Add(new XElement(Presentation + "MenuItem.Icon", new XElement(Silo + "AppIcon", new XAttribute("Icon", item.IconKey), new XAttribute("Size", "16"))));
        foreach (var child in item.Children) e.Add(Element(child));
        return e;
    }
}

/// <summary>Menus to start from, and a menu as an outline.</summary>
public static class MenuLibrary
{
    public static IReadOnlyList<(string Title, Func<MenuDoc> Make)> Templates { get; } =
    [
        ("File menu (bar)", FileMenu),
        ("Edit menu (bar)", EditMenu),
        ("Right-click menu of an item", NodeMenu)
    ];

    private static MenuItemDef Item(string header, string id, string? gesture = null, string? icon = null) => new() { Header = header, Id = id, Gesture = gesture, IconKey = icon };

    private static MenuDoc FileMenu() => new()
                                         {
                                             Name = "FileMenu", Kind = MenuKind.Bar, Description = "A File menu to start from.",
                                             Items =
                                             [
                                                 new MenuItemDef
                                                 {
                                                     Header = "_File", Children =
                                                     [
                                                         Item("_New", "file.new", "Ctrl+N", "TbNew"), Item("_Open...", "file.open", "Ctrl+O", "TbOpen"),
                                                         new MenuItemDef
                                                         {
                                                             Header = "Open _Recent", Children = [Item("_1 First.silo", "file.recent.1"), Item("_2 Second.silo", "file.recent.2")]
                                                         },
                                                         MenuItemDef.Line(), Item("_Save", "file.save", "Ctrl+S", "TbSave"), Item("Save _As...", "file.saveas", "Ctrl+Shift+S"), MenuItemDef.Line(),
                                                         Item("E_xit", "file.exit", "Alt+F4")
                                                     ]
                                                 }
                                             ]
                                         };

    private static MenuDoc EditMenu() => new()
                                         {
                                             Name = "EditMenu", Kind = MenuKind.Bar, Description = "An Edit menu to start from.",
                                             Items =
                                             [
                                                 new MenuItemDef
                                                 {
                                                     Header = "_Edit", Children =
                                                     [
                                                         Item("_Undo", "edit.undo", "Ctrl+Z"), Item("_Redo", "edit.redo", "Ctrl+Y"), MenuItemDef.Line(), Item("Cu_t", "edit.cut", "Ctrl+X"), Item("_Copy", "edit.copy", "Ctrl+C", "TbCopy"),
                                                         Item("_Paste", "edit.paste", "Ctrl+V", "TbPaste"), new MenuItemDef { Header = "_Delete", Id = "edit.delete", Gesture = "Delete", IconKey = "TbDelete", Destructive = true },
                                                         MenuItemDef.Line(), Item("Select _All", "edit.selectall", "Ctrl+A"), new MenuItemDef { Header = "_Word Wrap", Id = "edit.wrap", Checkable = true, Checked = true }
                                                     ]
                                                 }
                                             ]
                                         };

    private static MenuDoc NodeMenu() => new()
                                         {
                                             Name = "NodeMenu", Kind = MenuKind.Context, Description = "A right-click menu to start from.",
                                             Items =
                                             [
                                                 new MenuItemDef { Header = "_Open", Id = "node.open", Gesture = "Enter", IconKey = "TbOpen", Default = true }, Item("Open _With", "node.openwith"),
                                                 MenuItemDef.Line(), Item("_Rename", "node.rename", "F2"), Item("_Copy", "node.copy", "Ctrl+C", "TbCopy"), MenuItemDef.Line(),
                                                 new MenuItemDef { Header = "_Delete", Id = "node.delete", Gesture = "Delete", IconKey = "TbDelete", Destructive = true },
                                                 MenuItemDef.Line(), Item("_Properties", "node.properties", "Alt+Enter")
                                             ]
                                         };

    /// <summary>The menu as an indented outline, for reading in a terminal.</summary>
    public static string Outline(MenuDoc doc)
    {
        var sb = new StringBuilder();
        foreach (var (path, item, depth) in doc.Walk())
        {
            string at = string.Join('.', path.Select(p => p + 1));
            sb.Append(at.PadRight(8)).Append(new string(' ', depth * 2));
            if (item.Separator) { sb.AppendLine("--------"); continue; }

            sb.Append(item.Display);
            if (item.IsSubmenu) sb.Append("  >");
            if (!string.IsNullOrEmpty(item.Gesture)) sb.Append("  [").Append(item.Gesture).Append(']');
            if (!string.IsNullOrEmpty(item.Id)) sb.Append("  ").Append(item.Id);
            if (item.Checkable) sb.Append(item.Checked ? "  (checked)" : "  (checkable)");
            if (!item.Enabled) sb.Append("  (disabled)");
            if (item.Default) sb.Append("  (default)");
            if (item.Destructive) sb.Append("  (destructive)");
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>The entry at an address like "2.1" (counting from 1, one number for each level), or null.</summary>
    public static int[]? ParsePath(string text)
    {
        var parts = text.Trim().Split(new[] { '.', '/', '>' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return null;

        var path = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out int n) || n < 1) return null;

            path[i] = n - 1;
        }

        return path;
    }
}
